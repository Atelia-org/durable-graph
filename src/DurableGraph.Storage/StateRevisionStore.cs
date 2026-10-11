using Atelia.Binary;
using Atelia.FrameStore;
using Atelia.Rbf;
using BackingFrameStore = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Storage;

/// <summary>
/// Appends and reads State Revision Frames through a borrowed FrameStore.
/// </summary>
/// <remarks>
/// The caller owns the lifetime and publication authority of the supplied FrameStore.
/// This type appends candidate data and never chooses or publishes a head.
/// Existing complete frames must remain unchanged while this facade is open. Serialize
/// operations and do not overlap them with an externally held backing writer lease.
/// Dispose this facade before closing its borrowed backing store. Recovery performed by
/// the backing owner precedes this facade. Additional rescue requires
/// closing all old Stores and caches, then opening fresh resources.
/// </remarks>
public sealed class StateRevisionStore : IDisposable {
    /// <summary>Frame role for a Revision payload, with or without caller-owned TailMeta.</summary>
    public const uint FrameTag = StateRevisionWireFormat.RbfTag;
    /// <summary>Default estimated resident read-cache budget per Store: 8 MiB.</summary>
    public const long DefaultReadCacheBudgetBytes = 8 * 1024 * 1024;
    private readonly BackingFrameStore _frames;
    private StateRevisionReadCache? _readCache;
    private bool _disposed;
    private long _revisionHits, _revisionMisses, _revisionDecodes;
    private long _mapHits, _mapMisses, _mapMaterializations;
    private long _objectChainReads, _objectChainEntries, _mapReplayFrames;

    /// <summary>Creates a facade over an existing append-only FrameStore.</summary>
    /// <param name="frames">Borrowed storage, which must outlive this facade.</param>
    /// <param name="readCacheBudgetBytes">
    /// Estimated resident budget shared by decoded frames and complete head maps.
    /// Zero disables residency; negative budgets are rejected. This is not a process heap or peak allocation limit.
    /// </param>
    public StateRevisionStore(BackingFrameStore frames, long readCacheBudgetBytes = DefaultReadCacheBudgetBytes) {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentOutOfRangeException.ThrowIfNegative(readCacheBudgetBytes);
        frames.EnsureUsable();
        _frames = frames;
        if (readCacheBudgetBytes != 0) { _readCache = new(readCacheBudgetBytes); }
    }

    internal StateRevisionReadCacheStatistics ReadCacheStatistics => new(
        _revisionHits, _revisionMisses, _revisionDecodes, _mapHits, _mapMisses, _mapMaterializations,
        _readCache?.Evictions ?? 0, _readCache?.AdmissionBypasses ?? 0,
        _readCache?.ResidentChargeBytes ?? 0, _readCache?.PeakResidentChargeBytes ?? 0,
        _readCache?.EntryCount ?? 0);

    internal StateRevisionTraversalStatistics TraversalStatistics => new(
        _objectChainReads, _objectChainEntries, _mapReplayFrames);

    /// <summary>Appends candidate Revision data and optional caller-owned TailMeta; does not publish or confirm it.</summary>
    public FrameAddress Append(StateRevision revision, ReadOnlySpan<byte> tailMeta = default) {
        EnsureUsable();
        ArgumentNullException.ThrowIfNull(revision);
        long payloadBytes = StateRevisionWireWriter.Measure(revision);
        if (tailMeta.Length > RbfFile.MaxTailMetaLength ||
            payloadBytes + tailMeta.Length > RbfFile.MaxPayloadAndMetaLength) {
            throw new InvalidDataException("The State Revision exceeds the backing store's frame capacity.");
        }
        ValidateCompletedReferences(revision);

        using FrameBuilder builder = _frames.BeginAppend();
        StateRevisionWireWriter.Write(builder.PayloadAndMeta, revision);
        BareValueWriter writer = new(builder.PayloadAndMeta);
        writer.WriteRawBytes(tailMeta);
        // Admission reads wire: unencoded inputs must not bypass actual byte accounting.
        return builder.EndAppend(FrameTag, tailMeta.Length).Unwrap();
    }

    public StateRevision Read(FrameAddress address) {
        EnsureUsable();
        FrameAddressValidator.ValidateRequired(address, nameof(address));
        if (_readCache is { } cache && cache.TryGetRevision(address, out StateRevision cached)) {
            _revisionHits++;
            return cached;
        }
        _revisionMisses++;
        using FrameRead frame = _frames.ReadFrame(address).Unwrap();
        if (frame.IsTombstone) {
            throw new InvalidDataException($"State Revision Frame {address} is a tombstone.");
        }
        if (frame.Tag != FrameTag) {
            throw new InvalidDataException($"Frame has tag 0x{frame.Tag:X8}; expected State Revision tag 0x{FrameTag:X8}.");
        }
        StateRevision revision = StateRevisionWireReader.Read(frame.Payload);
        _revisionDecodes++;
        _readCache?.AdmitRevision(address, revision);
        return revision;
    }

    /// <summary>
    /// Reconstructs the object head for every ObjectId declared live by the
    /// ObjectHeadMap at the specified StateRevision address.
    /// </summary>
    /// <remarks>
    /// This does not identify or publish a publication head. Local ObjectIds map
    /// to the containing Revision Frame. External
    /// ObjectIds keep the absolute address recorded by the completing Base. The
    /// returned map is immutable and enumerates in ascending ObjectId order.
    /// These shallow declarations are not dereferenced or validated as
    /// ObjectVersion records by this operation.
    /// </remarks>
    public IReadOnlyDictionary<uint, FrameAddress> ReadLiveObjectHeadMap(
        FrameAddress revisionAddress) {
        EnsureUsable();
        FrameAddressValidator.ValidateRequired(revisionAddress, nameof(revisionAddress));
        if (_readCache is { } cache && cache.TryGetHeads(revisionAddress, out var cached)) {
            _mapHits++;
            return cached;
        }
        _mapMisses++;
        var heads = LiveObjectHeadMapMaterializer.Materialize(revisionAddress, ReadForMapReplay);
        _mapMaterializations++;
        _readCache?.AdmitHeads(revisionAddress, heads);
        return heads;
    }

    private StateRevision ReadForMapReplay(FrameAddress address) {
        _mapReplayFrames++;
        return Read(address);
    }

    /// <summary>
    /// Reads the complete Base body of an Object live in the specified StateRevision.
    /// The returned array is an independent copy owned by the caller.
    /// </summary>
    /// <remarks>
    /// Resolves membership from the exact Revision, then requires its declared
    /// containing Frame to hold a local Base record. An absent local record is
    /// invalid even if the containing Frame inherits that Object from its parent.
    /// This validates only the requested Object's locator and raw body; it does
    /// not validate types, other external heads, or graph references.
    /// </remarks>
    public byte[] ReadObjectBaseBody(FrameAddress revisionAddress, uint objectId) {
        EnsureUsable();
        FrameAddress head = ResolveObjectHead(revisionAddress, objectId);
        ObjectVersionRecord record = FindLocalRecord(Read(head), head, objectId);
        if (record.Kind != ObjectVersionKind.Base) {
            throw new InvalidDataException(
                $"ObjectId {objectId} at {head} has a Delta head, not a complete Base body.");
        }

        return record.Body.ToArray();
    }

    /// <summary>
    /// Reads one live Object's complete raw reconstruction chain, ordered from
    /// its most recent Base through its current Delta head.
    /// </summary>
    /// <remarks>
    /// Each Delta must name the object head selected by its exact Parent Revision.
    /// Records own their bytes and no pooled Frame lease escapes this operation.
    /// This does not interpret bodies or authenticate their Schema or producer.
    /// Complete maps retain their shallow external-head declaration contract.
    /// </remarks>
    public ObjectVersionChain ReadObjectVersionChain(
        FrameAddress revisionAddress,
        uint objectId) {
        EnsureUsable();
        ValidateObjectHeadArguments(revisionAddress, objectId);
        _objectChainReads++;
        FrameAddress head = ResolveObjectHead(revisionAddress, objectId);
        FrameAddress address = head;
        StateRevision revision = Read(address);
        ObjectVersionRecord record = FindLocalRecord(revision, address, objectId);
        List<ObjectVersionChainEntry> records = [];
        HashSet<FrameAddress> visited = [];
        while (true) {
            if (!visited.Add(address)) {
                throw new InvalidDataException("The ObjectVersion prior chain contains a cycle.");
            }
            records.Add(new ObjectVersionChainEntry(address, record));
            _objectChainEntries++;
            if (record.Kind == ObjectVersionKind.Base) {
                break;
            }

            FrameAddress parent = revision.ParentRevisionAddress
                ?? throw new InvalidDataException(
                    $"Delta ObjectId {objectId} at {address} has no Parent Revision.");
            FrameAddress prior = record.PriorAddress
                ?? throw new InvalidDataException(
                    $"Delta ObjectId {objectId} at {address} has no prior address.");
            RequireParentHead(ReadLiveObjectHeadMap(parent), objectId, prior);
            revision = Read(prior);
            record = FindLocalRecord(revision, prior, objectId);
            address = prior;
        }

        records.Reverse();
        return new ObjectVersionChain(objectId, head, records);
    }

    private void ValidateCompletedReferences(StateRevision revision) {
        // Addresses are opaque. Read the current owner's completed frame, rather than
        // asserting a numeric file/offset order or accepting an early Builder address.
        if (revision.ParentRevisionAddress is { } parentAddress) { _ = Read(parentAddress); }
        foreach (FrameAddress externalHead in revision.ExternalObjectHeads.Values) { _ = Read(externalHead); }

        IReadOnlyDictionary<uint, FrameAddress>? parentHeads = null;
        foreach (ObjectVersionRecord record in revision.LocalObjects) {
            if (record.Kind != ObjectVersionKind.Delta) {
                continue;
            }

            FrameAddress parent = revision.ParentRevisionAddress
                ?? throw new InvalidDataException(
                    $"Delta ObjectId {record.ObjectId} requires a Parent Revision.");
            FrameAddress prior = record.PriorAddress
                ?? throw new InvalidDataException(
                    $"Delta ObjectId {record.ObjectId} requires a prior address.");
            parentHeads ??= ReadLiveObjectHeadMap(parent);
            RequireParentHead(parentHeads, record.ObjectId, prior);
            _ = FindLocalRecord(Read(prior), prior, record.ObjectId);
        }
    }

    private FrameAddress ResolveObjectHead(FrameAddress revisionAddress, uint objectId) {
        ValidateObjectHeadArguments(revisionAddress, objectId);

        if (!ReadLiveObjectHeadMap(revisionAddress).TryGetValue(objectId, out FrameAddress head)) {
            throw new InvalidDataException(
                $"ObjectId {objectId} is not live in Revision {revisionAddress}.");
        }

        return head;
    }

    private static void ValidateObjectHeadArguments(FrameAddress revisionAddress, uint objectId) {
        FrameAddressValidator.ValidateRequired(revisionAddress, nameof(revisionAddress));
        if (objectId == 0) {
            throw new ArgumentOutOfRangeException(
                nameof(objectId), objectId, "ObjectId must be nonzero.");
        }
    }

    private static void RequireParentHead(
        IReadOnlyDictionary<uint, FrameAddress> parentHeads,
        uint objectId,
        FrameAddress prior) {
        if (!parentHeads.TryGetValue(objectId, out FrameAddress expected) || expected != prior) {
            throw new InvalidDataException(
                $"Delta ObjectId {objectId} prior {prior} is not the object head selected by its exact Parent Revision.");
        }
    }

    private static ObjectVersionRecord FindLocalRecord(
        StateRevision revision,
        FrameAddress address,
        uint objectId) {
        IReadOnlyList<ObjectVersionRecord> records = revision.LocalObjects;
        int low = 0;
        int high = records.Count - 1;
        while (low <= high) {
            int middle = low + ((high - low) >> 1);
            ObjectVersionRecord record = records[middle];
            if (record.ObjectId == objectId) {
                return record;
            }
            if (record.ObjectId < objectId) { low = middle + 1; }
            else { high = middle - 1; }
        }

        throw new InvalidDataException(
            $"Frame {address} has no local ObjectVersion record for ObjectId {objectId}.");
    }

    private void EnsureUsable() {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _frames.EnsureUsable();
    }

    /// <summary>Ends this facade and releases its entire cache; does not close borrowed storage.</summary>
    /// <remarks>Previously returned owned revisions and maps remain readable. Repeated disposal is harmless.</remarks>
    public void Dispose() {
        if (_disposed) { return; }
        _disposed = true;
        _readCache = null;
    }
}
