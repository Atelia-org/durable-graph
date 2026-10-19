using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using DataStore = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Persistence;

// A borrowed facade with owned physical reads. Reading a graph validates its selected physical
// relations; typed body/view/reference qualification remains with each producer.
internal sealed class GraphRecordStore : IDisposable {
    internal const long DefaultReadCacheBudgetBytes = 8 * 1024 * 1024;
    private readonly DataStore _frames;
    private readonly Action? _ensureOwner;
    private GraphPhysicalReadCache? _readCache;
    private long _graphHits, _graphMisses, _graphDecodes;
    private long _batchHits, _batchMisses, _batchDecodes;
    private long _directoryVersionHits, _directoryVersionMisses, _directoryVersionDecodes;
    private long _directoryHits, _directoryMisses, _directoryMaterializations, _directoryReplayFrames, _directoryPayloadHits;
    private bool _disposed;
    private bool _appending;
    private int _directoryThreshold = 5;

    internal GraphRecordStore(DataStore frames, Action? ensureOwner = null,
        long readCacheBudgetBytes = DefaultReadCacheBudgetBytes) {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentOutOfRangeException.ThrowIfNegative(readCacheBudgetBytes);
        _frames = frames;
        _ensureOwner = ensureOwner;
        EnsureUsable();
        if (readCacheBudgetBytes != 0) { _readCache = new(readCacheBudgetBytes); }
    }

    internal GraphPhysicalReadStatistics ReadCacheStatistics => new(
        _graphHits, _graphMisses, _graphDecodes, _batchHits, _batchMisses, _batchDecodes,
        _directoryVersionHits, _directoryVersionMisses, _directoryVersionDecodes,
        _directoryHits, _directoryMisses, _directoryMaterializations, _directoryReplayFrames, _directoryPayloadHits,
        _readCache?.Evictions ?? 0, _readCache?.AdmissionBypasses ?? 0,
        _readCache?.ResidentChargeBytes ?? 0, _readCache?.PeakResidentChargeBytes ?? 0, _readCache?.EntryCount ?? 0);

    internal long AppendedFrames { get; private set; }
    // Completed business qualifications, including warm physical-cache hits; not frame decodes.
    internal long BusinessGraphQualificationCount { get; private set; }
    internal int DirectoryReadAmplificationThreshold {
        get => _directoryThreshold;
        set {
            if (value < 1) { throw new ArgumentOutOfRangeException(nameof(value)); }
            _directoryThreshold = value;
        }
    }

    internal StoredGraphRevision AppendBusiness(StoredGraphKind kind, FrameAddress? expectedStateSource,
        FrameAddress? schemaRoot, PreparedGraphWrite write) {
        RequireBusinessKind(kind);
        ValidateBusinessSource(expectedStateSource);
        return AppendCore(kind, previousMeta: null, expectedStateSource, schemaRoot, write);
    }

    internal StoredGraphRevision AppendMeta(FrameAddress? previousMeta, PreparedGraphWrite write) {
        ValidateMetaSource(previousMeta);
        return AppendCore(StoredGraphKind.Meta, previousMeta, previousMeta, schemaRoot: null, write);
    }

    private StoredGraphRevision AppendCore(StoredGraphKind kind, FrameAddress? previousMeta,
        FrameAddress? source, FrameAddress? schemaRoot, PreparedGraphWrite write) {
        EnsureUsable();
        if (_appending) { throw new InvalidOperationException("P0 graph writes cannot be reentered."); }
        ArgumentNullException.ThrowIfNull(write);
        _appending = true;
        try {
            if (write.SourceGraph != source) { throw new InvalidDataException("Prepared source is not the exact content source."); }
            RequireSchemaRoot(kind, schemaRoot);
            GraphEnvelope? sourceMetadata = source is { } address ? ReadMetadata(address) : null;
            IReadOnlyDictionary<uint, FrameAddress> sourceHeads = sourceMetadata is { } metadata
                ? ReadDirectory(metadata.DirectoryRoot) : EmptyHeads();
            var finalHeads = new SortedDictionary<uint, FrameAddress>(sourceHeads.ToDictionary(static pair => pair.Key, static pair => pair.Value));
            foreach (uint id in write.RemovedIds) {
                if (!finalHeads.Remove(id)) { throw new InvalidDataException("Remove must identify an object in the exact source."); }
            }
            foreach (ObjectVersionRecord record in write.Records) {
                if (record.Kind == ObjectVersionKind.Delta) {
                    if (source is null) { throw new InvalidDataException("An initial object cannot be Delta."); }
                    RequirePrior(sourceHeads, record.ObjectId, record.PriorAddress);
                    _ = FindRecord(ReadBatch(record.PriorAddress!.Value), record.ObjectId);
                }
            }
            // Check membership before any output without inventing a future address.
            if (!finalHeads.ContainsKey(write.RootId.Value) && !write.Records.Any(row => row.ObjectId == write.RootId.Value)) {
                throw new InvalidDataException("RootId is absent from the final live set.");
            }

            FrameAddress? batch = null;
            var writtenHeads = new SortedDictionary<uint, FrameAddress>();
            if (write.Records.Count != 0) {
                batch = AppendFrame(GraphFrameCodec.BatchTag, GraphFrameCodec.EncodeBatch(source, schemaRoot, write.Records));
                foreach (ObjectVersionRecord record in write.Records) {
                    finalHeads[record.ObjectId] = batch.Value;
                    writtenHeads.Add(record.ObjectId, batch.Value);
                }
            }

            FrameAddress directory;
            if (sourceMetadata is { } baseline) {
                var delta = ObjectHeadDirectoryCodec.PrepareDelta(sourceHeads, write.RemovedIds, writtenHeads);
                if (!delta.HasChanges) {
                    if (batch is not null) { throw new InvalidDataException("A new batch must change the selected directory."); }
                    directory = baseline.DirectoryRoot;
                } else {
                    var baseBody = ObjectHeadDirectoryCodec.PrepareBase(finalHeads);
                    long b = ObjectVersionPayloadSize.GetBasePayloadBytes(baseBody.Body.Length);
                    long d = ObjectVersionPayloadSize.GetDeltaPayloadBytes(delta.Body.Length, baseline.DirectoryRoot);
                    long h = ReadDirectoryPayloadBytes(baseline.DirectoryRoot);
                    bool useBase = b <= d || (Int128)h + d > (Int128)_directoryThreshold * b;
                    directory = AppendFrame(GraphFrameCodec.DirectoryTag, useBase
                        ? GraphFrameCodec.EncodeDirectory(source, ObjectVersionKind.Base, null, baseBody.Body)
                        : GraphFrameCodec.EncodeDirectory(source, ObjectVersionKind.Delta, baseline.DirectoryRoot, delta.Body));
                }
            } else {
                var baseBody = ObjectHeadDirectoryCodec.PrepareBase(finalHeads);
                directory = AppendFrame(GraphFrameCodec.DirectoryTag,
                    GraphFrameCodec.EncodeDirectory(null, ObjectVersionKind.Base, null, baseBody.Body));
            }
            FrameAddress graph = AppendFrame(GraphFrameCodec.GraphTag,
                GraphFrameCodec.EncodeGraph(kind, previousMeta, write.RootId, directory, schemaRoot, batch));
            return kind == StoredGraphKind.Meta ? ReadMetaGraph(graph) : ReadBusinessGraph(graph, source);
        } finally { _appending = false; }
    }

    internal StoredGraphRevision ReadBusinessGraph(FrameAddress address, FrameAddress? expectedStateSource) {
        GraphEnvelope metadata = ReadMetadata(address);
        RequireBusinessKind(metadata.Kind);
        ValidateBusinessSource(expectedStateSource, address);
        return ReadGraphCore(address, metadata, expectedStateSource);
    }

    internal StoredGraphRevision ReadMetaGraph(FrameAddress address) {
        GraphEnvelope metadata = ReadMetadata(address);
        if (metadata.Kind != StoredGraphKind.Meta) { throw new InvalidDataException("A Meta read requires a Meta graph."); }
        ValidateMetaSource(metadata.PreviousMeta, address);
        return ReadGraphCore(address, metadata, metadata.PreviousMeta);
    }

    private StoredGraphRevision ReadGraphCore(FrameAddress address, GraphEnvelope metadata, FrameAddress? source) {
        ValidateMetadataRelations(metadata, source,
            out FrameAddress? sourceDirectory, out ObjectVersionBatch? batch);
        IReadOnlyDictionary<uint, FrameAddress> heads = ReadDirectory(metadata.DirectoryRoot);
        if (!heads.ContainsKey(metadata.RootId.Value)) { throw new InvalidDataException("RootId is not live."); }
        if (metadata.OwnedBatch is { } owned) {
            IReadOnlyDictionary<uint, FrameAddress>? sourceHeads = null;
            foreach (ObjectVersionRecord record in batch!.Records) {
                if (!heads.TryGetValue(record.ObjectId, out FrameAddress selected) || selected != owned) {
                    throw new InvalidDataException("Every local record must be live at the owned batch.");
                }
                if (record.Kind == ObjectVersionKind.Delta) {
                    if (sourceDirectory is null) { throw new InvalidDataException("An initial batch cannot contain Delta."); }
                    sourceHeads ??= ReadDirectory(sourceDirectory.Value);
                    RequirePrior(sourceHeads, record.ObjectId, record.PriorAddress);
                    _ = FindRecord(ReadBatch(record.PriorAddress!.Value), record.ObjectId);
                }
            }
        }
        // Shallow external-head declarations stay shallow. These checks do not
        // reconstruct old Delta chains or authenticate global entity history.
        foreach ((uint id, FrameAddress head) in heads) { _ = FindRecord(ReadBatch(head), id); }
        if (metadata.Kind is StoredGraphKind.State or StoredGraphKind.Event) { BusinessGraphQualificationCount++; }
        return new(address, metadata, source, heads, this);
    }

    // Saving and prepared-state reuse recheck the actual graph; a retained snapshot is
    // source material, not permission to bypass the current owner's validation.
    internal StoredGraphRevision Revalidate(StoredGraphRevision snapshot) {
        RequireSnapshotOwner(snapshot);
        StoredGraphRevision actual = snapshot.Kind == StoredGraphKind.Meta
            ? ReadMetaGraph(snapshot.Address) : ReadBusinessGraph(snapshot.Address, snapshot.SourceGraph);
        if (actual.Metadata != snapshot.Metadata || actual.SourceGraph != snapshot.SourceGraph ||
            !actual.ObjectHeads.SequenceEqual(snapshot.ObjectHeads)) {
            throw new InvalidDataException("A stored source snapshot must match its actual graph, source and membership.");
        }
        return actual;
    }

    private void RequireSnapshotOwner(StoredGraphRevision snapshot) {
        EnsureUsable();
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!ReferenceEquals(snapshot.SourceOwner, this)) {
            throw new InvalidDataException("A stored graph snapshot requires its issuing Store.");
        }
    }

    // Qualify a selected Event's source links without replaying its directory,
    // validating live membership, or invoking object body readers.
    internal void ValidateBusinessMetadata(FrameAddress address, FrameAddress? expectedStateSource) {
        GraphEnvelope metadata = ReadMetadata(address);
        RequireBusinessKind(metadata.Kind);
        ValidateBusinessSource(expectedStateSource, address);
        ValidateMetadataRelations(metadata, expectedStateSource, out _, out _);
    }

    private void ValidateMetadataRelations(GraphEnvelope metadata, FrameAddress? source,
        out FrameAddress? sourceDirectory, out ObjectVersionBatch? ownedBatch) {
        RequireSchemaRoot(metadata.Kind, metadata.SchemaRoot);
        sourceDirectory = source is { } actualSource ? ReadMetadata(actualSource).DirectoryRoot : null;
        ObjectHeadDirectoryVersion directoryVersion = ReadDirectoryVersion(metadata.DirectoryRoot);
        if (sourceDirectory == metadata.DirectoryRoot) {
            if (metadata.OwnedBatch is not null) { throw new InvalidDataException("A reused directory cannot own a new batch."); }
        } else {
            if (directoryVersion.SourceGraph != source) { throw new InvalidDataException("Directory creator does not match the graph's content source."); }
            if (directoryVersion.Kind == ObjectVersionKind.Delta &&
                (sourceDirectory is null || directoryVersion.Prior != sourceDirectory)) {
                throw new InvalidDataException("Directory Delta must extend the exact content source directory.");
            }
            if (source is null && directoryVersion.Kind != ObjectVersionKind.Base) {
                throw new InvalidDataException("An initial directory must be Base.");
            }
        }
        ownedBatch = metadata.OwnedBatch is { } owned ? ReadBatch(owned) : null;
        if (ownedBatch is { } batch) {
            if (batch.SourceGraph != source) { throw new InvalidDataException("Owned batch does not use the graph's exact content source."); }
            if (batch.SchemaRoot != metadata.SchemaRoot) { throw new InvalidDataException("Owned Base Schema root differs from the selected graph view."); }
        }
    }

    internal GraphEnvelope ReadMetadata(FrameAddress address) {
        EnsureUsable();
        if (_readCache is { } cache && cache.TryGetGraph(address, out var cached)) {
            _graphHits++;
            return cached;
        }
        _graphMisses++;
        GraphEnvelope graph = GraphFrameCodec.DecodeGraph(ReadPayload(address, GraphFrameCodec.GraphTag));
        _graphDecodes++;
        _readCache?.AdmitGraph(address, graph);
        return graph;
    }

    internal ObjectVersionBatch ReadBatch(FrameAddress address) {
        EnsureUsable();
        if (_readCache is { } cache && cache.TryGetBatch(address, out var cached)) {
            _batchHits++;
            return cached;
        }
        _batchMisses++;
        ObjectVersionBatch batch = GraphFrameCodec.DecodeBatch(ReadPayload(address, GraphFrameCodec.BatchTag));
        _batchDecodes++;
        _readCache?.AdmitBatch(address, batch);
        return batch;
    }

    internal ObjectHeadDirectoryVersion ReadDirectoryVersion(FrameAddress address) {
        EnsureUsable();
        if (_readCache is { } cache && cache.TryGetDirectoryVersion(address, out var cached)) {
            _directoryVersionHits++;
            return cached;
        }
        _directoryVersionMisses++;
        ObjectHeadDirectoryVersion directory = GraphFrameCodec.DecodeDirectory(ReadPayload(address, GraphFrameCodec.DirectoryTag));
        _directoryVersionDecodes++;
        _readCache?.AdmitDirectoryVersion(address, directory);
        return directory;
    }

    internal IReadOnlyDictionary<uint, FrameAddress> ReadDirectory(FrameAddress address) {
        EnsureUsable();
        if (_readCache is { } cache && cache.TryGetDirectory(address, out var cached)) {
            _directoryHits++;
            return cached.Heads;
        }
        _directoryMisses++;
        var chain = ReadDirectoryChain(address);
        IReadOnlyDictionary<uint, FrameAddress> heads = ObjectHeadDirectoryCodec.ReadBase(chain[0].Body);
        for (int i = 1; i < chain.Count; i++) { heads = ObjectHeadDirectoryCodec.ApplyDelta(heads, chain[i].Body); }
        var frozen = new FrozenSortedDictionary<uint, FrameAddress>(new SortedDictionary<uint, FrameAddress>(
            heads.ToDictionary(static pair => pair.Key, static pair => pair.Value)));
        long bytes = chain.Aggregate(0L, static (sum, version) => checked(sum + version.PayloadBytes));
        _directoryMaterializations++;
        _readCache?.AdmitDirectory(address, new(frozen, bytes));
        return frozen;
    }

    internal long ReadDirectoryPayloadBytes(FrameAddress address) {
        EnsureUsable();
        if (_readCache is { } cache && cache.TryGetDirectory(address, out var cached)) {
            _directoryPayloadHits++;
            return cached.PayloadBytes;
        }
        // A cost-only miss does not force a complete map materialization.
        return ReadDirectoryChain(address).Aggregate(0L, static (bytes, version) => checked(bytes + version.PayloadBytes));
    }

    internal ObjectVersionChain ReadObjectChain(StoredGraphRevision snapshot, uint objectId) {
        if (objectId == 0) { throw new ArgumentOutOfRangeException(nameof(objectId)); }
        RequireSnapshotOwner(snapshot);
        return ReadObjectChainCore(snapshot, objectId);
    }

    // Internal continuation of a concrete read, using its already-qualified snapshot.
    // Long-lived save baselines must instead pass through Revalidate.
    internal IEnumerable<ObjectVersionChain> ReadObjectChains(StoredGraphRevision snapshot) {
        RequireSnapshotOwner(snapshot);
        return EnumerateObjectChains(snapshot);
    }

    private IEnumerable<ObjectVersionChain> EnumerateObjectChains(StoredGraphRevision graph) {
        foreach (uint objectId in graph.ObjectHeads.Keys) {
            yield return ReadObjectChainCore(graph, objectId);
        }
    }

    private ObjectVersionChain ReadObjectChainCore(StoredGraphRevision graph, uint objectId) {
        EnsureUsable();
        if (!graph.ObjectHeads.TryGetValue(objectId, out FrameAddress head)) { throw new InvalidDataException("ObjectId is not live in the graph."); }
        FrameAddress address = head;
        var visited = new HashSet<FrameAddress>();
        var entries = new List<ObjectVersionChainEntry>();
        while (true) {
            if (!visited.Add(address)) { throw new InvalidDataException("Object prior chain contains a cycle."); }
            ObjectVersionBatch batch = ReadBatch(address);
            ObjectVersionRecord record = FindRecord(batch, objectId);
            entries.Add(new(record, address, batch.SourceGraph, batch.SchemaRoot,
                record.EncodedPayloadBytes ?? throw new InvalidDataException("Read records require actual payload accounting.")));
            if (record.Kind == ObjectVersionKind.Base) { break; }
            FrameAddress source = batch.SourceGraph ?? throw new InvalidDataException("Object Delta lacks a source graph.");
            GraphEnvelope sourceGraph = ReadMetadata(source);
            RequireContentSourceRole(sourceGraph.Kind);
            IReadOnlyDictionary<uint, FrameAddress> sourceHeads = ReadDirectory(sourceGraph.DirectoryRoot);
            RequirePrior(sourceHeads, objectId, record.PriorAddress);
            address = record.PriorAddress!.Value;
        }
        entries.Reverse();
        return new(objectId, head, entries);
    }

    private static void RequireBusinessKind(StoredGraphKind kind) {
        if (kind is not StoredGraphKind.State and not StoredGraphKind.Event) {
            throw new InvalidDataException("A business graph must be State or Event.");
        }
    }

    // Repository selects the nearest State through the exact Entry history. This
    // layer validates only its role and physical relations, without a second history.
    private void ValidateBusinessSource(FrameAddress? source, FrameAddress? current = null) {
        EnsureUsable();
        if (source is { } address && (current == address || ReadMetadata(address).Kind != StoredGraphKind.State)) {
            throw new InvalidDataException("A business source must be a distinct State graph.");
        }
    }

    private void ValidateMetaSource(FrameAddress? previousMeta, FrameAddress? current = null) {
        EnsureUsable();
        if (previousMeta is { } address && (current == address || ReadMetadata(address).Kind != StoredGraphKind.Meta)) {
            throw new InvalidDataException("PreviousMeta must select a distinct Meta graph.");
        }
    }

    private IReadOnlyList<ObjectHeadDirectoryVersion> ReadDirectoryChain(FrameAddress address) {
        var visited = new HashSet<FrameAddress>();
        var versions = new List<ObjectHeadDirectoryVersion>();
        while (true) {
            if (!visited.Add(address)) { throw new InvalidDataException("Directory prior chain contains a cycle."); }
            ObjectHeadDirectoryVersion version = ReadDirectoryVersion(address);
            _directoryReplayFrames++;
            versions.Add(version);
            // A new Base does not certify or read a discarded earlier content chain.
            if (version.Kind == ObjectVersionKind.Base) { break; }
            FrameAddress source = version.SourceGraph ?? throw new InvalidDataException("Directory Delta lacks source graph.");
            GraphEnvelope graph = ReadMetadata(source);
            RequireContentSourceRole(graph.Kind);
            if (version.Prior != graph.DirectoryRoot) { throw new InvalidDataException("Directory prior differs from its source graph's directory."); }
            address = version.Prior!.Value;
        }
        versions.Reverse();
        return versions;
    }

    private void RequireSchemaRoot(StoredGraphKind kind, FrameAddress? schemaRoot) {
        if (kind == StoredGraphKind.Meta) {
            if (schemaRoot is not null) { throw new InvalidDataException("Meta has fixed interpretation without an external Schema root."); }
        } else if (schemaRoot is { } schema && ReadMetadata(schema).Kind != StoredGraphKind.Meta) {
            throw new InvalidDataException("SchemaRoot must select a meta graph.");
        }
    }

    private static void RequireContentSourceRole(StoredGraphKind kind) {
        if (kind is not StoredGraphKind.State and not StoredGraphKind.Meta) { throw new InvalidDataException("Content source must be State or meta."); }
    }

    private static void RequirePrior(IReadOnlyDictionary<uint, FrameAddress> heads, uint id, FrameAddress? prior) {
        if (prior is null || !heads.TryGetValue(id, out FrameAddress expected) || expected != prior.Value) {
            throw new InvalidDataException("Object Delta prior is not the head selected by its exact source graph.");
        }
    }

    private static ObjectVersionRecord FindRecord(ObjectVersionBatch batch, uint id) {
        int low = 0, high = batch.Records.Count - 1;
        while (low <= high) {
            int middle = low + ((high - low) >> 1);
            ObjectVersionRecord record = batch.Records[middle];
            if (record.ObjectId == id) { return record; }
            if (record.ObjectId < id) { low = middle + 1; } else { high = middle - 1; }
        }
        throw new InvalidDataException("Head frame has no local record for the requested ObjectId.");
    }

    private byte[] ReadPayload(FrameAddress address, uint tag) {
        using FrameRead frame = _frames.ReadFrame(address).Unwrap();
        if (frame.IsTombstone || frame.Tag != tag || frame.TailMetaLength != 0) {
            throw new InvalidDataException("P0 address selects the wrong completed frame role.");
        }
        return frame.Payload.ToArray();
    }

    private FrameAddress AppendFrame(uint tag, byte[] payload) {
        FrameAddress address = _frames.Append(tag, payload).Unwrap();
        AppendedFrames++;
        return address;
    }

    private static IReadOnlyDictionary<uint, FrameAddress> EmptyHeads() =>
        new FrozenSortedDictionary<uint, FrameAddress>(new SortedDictionary<uint, FrameAddress>());

    internal void EnsureUsable() {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try {
            _frames.EnsureUsable();
            _ensureOwner?.Invoke();
        } catch {
            _readCache = null;
            throw;
        }
    }

    public void Dispose() {
        if (_disposed) { return; }
        _disposed = true;
        _readCache = null;
    }
}
