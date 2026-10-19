using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence;

// Stored graph representations and the internal repository-save contract.
internal enum StoredGraphKind : byte {
    Event = 1,
    State = 2,
    Meta = 3,
}

internal sealed class PreparedGraphWrite {
    internal PreparedGraphWrite(FrameAddress? sourceGraph, ObjectId rootId,
        IEnumerable<ObjectVersionRecord> records, IEnumerable<uint>? removedIds = null) {
        if (sourceGraph is { } source) { FrameAddressValidator.ValidateRequired(source, nameof(sourceGraph)); }
        if (rootId.IsNull) { throw new ArgumentException("A graph requires a nonzero root.", nameof(rootId)); }
        ArgumentNullException.ThrowIfNull(records);
        ObjectVersionRecord[] rows = records.ToArray();
        if (rows.Any(static row => row is null)) { throw new ArgumentException("Null object records are invalid.", nameof(records)); }
        Array.Sort(rows, static (left, right) => left.ObjectId.CompareTo(right.ObjectId));
        for (int i = 1; i < rows.Length; i++) {
            if (rows[i - 1].ObjectId == rows[i].ObjectId) { throw new ArgumentException("Object IDs must be unique.", nameof(records)); }
        }
        uint[] removed = (removedIds ?? []).Order().ToArray();
        for (int i = 0; i < removed.Length; i++) {
            if (removed[i] == 0 || (i > 0 && removed[i - 1] == removed[i])) {
                throw new ArgumentException("Removed IDs must be nonzero and unique.", nameof(removedIds));
            }
        }
        if (rows.Any(row => Array.BinarySearch(removed, row.ObjectId) >= 0)) {
            throw new ArgumentException("Written and removed object IDs must be disjoint.", nameof(removedIds));
        }
        SourceGraph = sourceGraph;
        RootId = rootId;
        Records = new FrozenList<ObjectVersionRecord>(rows);
        RemovedIds = new FrozenList<uint>(removed);
    }

    internal FrameAddress? SourceGraph { get; }
    internal ObjectId RootId { get; }
    internal IReadOnlyList<ObjectVersionRecord> Records { get; }
    internal IReadOnlyList<uint> RemovedIds { get; }
}

internal readonly record struct GraphEnvelope(StoredGraphKind Kind, FrameAddress? PreviousMeta,
    ObjectId RootId, FrameAddress DirectoryRoot, FrameAddress? SchemaRoot, FrameAddress? OwnedBatch);

internal sealed class StoredGraphRevision {
    internal StoredGraphRevision(FrameAddress address, GraphEnvelope metadata, FrameAddress? sourceGraph,
        IReadOnlyDictionary<uint, FrameAddress> objects, GraphRecordStore? sourceOwner = null) {
        Address = address;
        Metadata = metadata;
        SourceGraph = sourceGraph;
        SourceOwner = sourceOwner;
        ObjectHeads = new FrozenSortedDictionary<uint, FrameAddress>(new SortedDictionary<uint, FrameAddress>(
            objects.ToDictionary(static pair => pair.Key, static pair => pair.Value)));
    }

    internal GraphEnvelope Metadata { get; }
    internal FrameAddress Address { get; }
    internal StoredGraphKind Kind => Metadata.Kind;
    internal FrameAddress? PreviousMeta => Metadata.PreviousMeta;
    internal ObjectId RootId => Metadata.RootId;
    internal FrameAddress DirectoryRoot => Metadata.DirectoryRoot;
    internal FrameAddress? SchemaRoot => Metadata.SchemaRoot;
    internal FrameAddress? OwnedBatch => Metadata.OwnedBatch;
    internal FrameAddress? SourceGraph { get; }
    internal GraphRecordStore? SourceOwner { get; }
    internal IReadOnlyDictionary<uint, FrameAddress> ObjectHeads { get; }
}

internal sealed record ObjectVersionBatch(FrameAddress? SourceGraph, FrameAddress? SchemaRoot,
    IReadOnlyList<ObjectVersionRecord> Records);

internal sealed class ObjectHeadDirectoryVersion {
    private readonly byte[] _body;
    internal ObjectHeadDirectoryVersion(FrameAddress? sourceGraph, ObjectVersionKind kind, FrameAddress? prior,
        ReadOnlySpan<byte> body, int payloadBytes) {
        SourceGraph = sourceGraph;
        Kind = kind;
        Prior = prior;
        _body = body.ToArray();
        PayloadBytes = payloadBytes;
    }
    internal FrameAddress? SourceGraph { get; }
    internal ObjectVersionKind Kind { get; }
    internal FrameAddress? Prior { get; }
    internal ReadOnlySpan<byte> Body => _body;
    internal int PayloadBytes { get; }
}

internal sealed record ObjectVersionChainEntry(ObjectVersionRecord Record, FrameAddress ContainingBatch,
    FrameAddress? SourceGraph, FrameAddress? SchemaRoot, int PayloadBytes);

internal sealed class ObjectVersionChain {
    internal ObjectVersionChain(uint objectId, FrameAddress head, IEnumerable<ObjectVersionChainEntry> entries) {
        ObjectId = objectId;
        Head = head;
        Entries = new FrozenList<ObjectVersionChainEntry>(entries);
        if (Entries.Count == 0 || Entries[0].Record.Kind != ObjectVersionKind.Base || Entries[^1].ContainingBatch != head) {
            throw new InvalidDataException("An object chain must start at Base and end at its selected head.");
        }
        long bytes = 0;
        for (int i = 0; i < Entries.Count; i++) {
            ObjectVersionChainEntry entry = Entries[i];
            if (entry.Record.ObjectId != objectId || entry.Record.Kind != (i == 0 ? ObjectVersionKind.Base : ObjectVersionKind.Delta) ||
                (i > 0 && entry.Record.PriorAddress != Entries[i - 1].ContainingBatch)) {
                throw new InvalidDataException("An object chain must follow exact priors for one ID.");
            }
            bytes = checked(bytes + entry.PayloadBytes);
        }
        ReconstructionPayloadBytes = bytes;
    }

    internal uint ObjectId { get; }
    internal FrameAddress Head { get; }
    internal IReadOnlyList<ObjectVersionChainEntry> Entries { get; }
    internal long ReconstructionPayloadBytes { get; }
}

internal sealed record RepositorySaveResult(StoredGraphRevision Business, StoredGraphRevision? Meta,
    FrameAddress? SharedSchemaRoot, Atelia.HeadStore.HeadEntryId CommitId, Atelia.HeadStore.HeadId BranchId);

internal enum RepositorySaveStage {
    BeforeAppend,
    AfterMetaAppend,
    AfterBusinessAppend,
    BeforePublication,
    AfterPublication,
    BeforeInstall,
}
