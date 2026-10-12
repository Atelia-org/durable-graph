using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.MetaBootstrapP0;

// Internal DB-087 witness. None of these types changes Repository's production format.
internal enum P0GraphKind : byte {
    Event = 1,
    State = 2,
    Meta = 3,
}

internal sealed class P0PreparedGraphWrite {
    internal P0PreparedGraphWrite(FrameAddress? sourceGraph, ObjectId rootId,
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

internal readonly record struct P0GraphMetadata(P0GraphKind Kind, FrameAddress? Parent,
    ObjectId RootId, FrameAddress DirectoryRoot, FrameAddress? SchemaRoot, FrameAddress? OwnedBatch);

internal sealed class P0GraphSnapshot {
    internal P0GraphSnapshot(FrameAddress address, P0GraphMetadata metadata, FrameAddress? sourceGraph,
        IReadOnlyDictionary<uint, FrameAddress> objects) {
        Address = address;
        Metadata = metadata;
        SourceGraph = sourceGraph;
        Objects = new FrozenSortedDictionary<uint, FrameAddress>(new SortedDictionary<uint, FrameAddress>(
            objects.ToDictionary(static pair => pair.Key, static pair => pair.Value)));
    }

    internal P0GraphMetadata Metadata { get; }
    internal FrameAddress Address { get; }
    internal P0GraphKind Kind => Metadata.Kind;
    internal FrameAddress? Parent => Metadata.Parent;
    internal ObjectId RootId => Metadata.RootId;
    internal FrameAddress DirectoryRoot => Metadata.DirectoryRoot;
    internal FrameAddress? SchemaRoot => Metadata.SchemaRoot;
    internal FrameAddress? OwnedBatch => Metadata.OwnedBatch;
    internal FrameAddress? SourceGraph { get; }
    internal IReadOnlyDictionary<uint, FrameAddress> Objects { get; }
    internal IReadOnlyDictionary<uint, FrameAddress> Heads => Objects;
}

internal sealed record P0Batch(FrameAddress? SourceGraph, FrameAddress? SchemaRoot,
    IReadOnlyList<ObjectVersionRecord> Records);

internal sealed record P0DirectoryVersion(FrameAddress? SourceGraph, ObjectVersionKind Kind,
    FrameAddress? Prior, byte[] Body, int PayloadBytes);

internal sealed record P0ChainEntry(ObjectVersionRecord Record, FrameAddress ContainingBatch,
    FrameAddress? SourceGraph, FrameAddress? SchemaRoot, int PayloadBytes);

internal sealed class P0ObjectChain {
    internal P0ObjectChain(uint objectId, FrameAddress head, IEnumerable<P0ChainEntry> entries) {
        ObjectId = objectId;
        Head = head;
        Entries = new FrozenList<P0ChainEntry>(entries);
        if (Entries.Count == 0 || Entries[0].Record.Kind != ObjectVersionKind.Base || Entries[^1].ContainingBatch != head) {
            throw new InvalidDataException("An object chain must start at Base and end at its selected head.");
        }
        long bytes = 0;
        for (int i = 0; i < Entries.Count; i++) {
            P0ChainEntry entry = Entries[i];
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
    internal IReadOnlyList<P0ChainEntry> Entries { get; }
    internal long ReconstructionPayloadBytes { get; }
}

internal sealed record P0SaveResult(P0GraphSnapshot Business, P0GraphSnapshot? Meta,
    FrameAddress? SharedSchemaRoot);

internal enum P0SaveCheckpoint {
    BeforeAppend,
    AfterMetaAppend,
    AfterBusinessAppend,
    BeforePublication,
    AfterPublication,
    BeforeInstall,
}
