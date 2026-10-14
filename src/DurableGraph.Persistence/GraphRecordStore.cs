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
    internal int DirectoryReadAmplificationThreshold {
        get => _directoryThreshold;
        set {
            if (value < 1) { throw new ArgumentOutOfRangeException(nameof(value)); }
            _directoryThreshold = value;
        }
    }

    internal StoredGraphRevision Append(StoredGraphKind kind, FrameAddress? parent,
        FrameAddress? schemaRoot, PreparedGraphWrite write) {
        EnsureUsable();
        if (_appending) { throw new InvalidOperationException("P0 graph writes cannot be reentered."); }
        ArgumentNullException.ThrowIfNull(write);
        _appending = true;
        try {
            FrameAddress? source = ResolveContentSource(kind, parent);
            if (write.SourceGraph != source) { throw new InvalidDataException("Prepared source is not the exact content Parent."); }
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
                GraphFrameCodec.EncodeGraph(kind, parent, write.RootId, directory, schemaRoot, batch));
            return ReadGraph(graph);
        } finally { _appending = false; }
    }

    internal StoredGraphRevision ReadGraph(FrameAddress address) {
        EnsureUsable();
        GraphEnvelope metadata = ReadMetadata(address);
        FrameAddress? source = ValidateMetadataRelations(address, metadata,
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
        return new(address, metadata, source, heads, this);
    }

    // Qualify a selected Event's source links without replaying its directory,
    // validating live membership, or invoking object body readers.
    internal void ValidateMetadataRelations(FrameAddress address) {
        EnsureUsable();
        _ = ValidateMetadataRelations(address, ReadMetadata(address), out _, out _);
    }

    private FrameAddress? ValidateMetadataRelations(FrameAddress address, GraphEnvelope metadata,
        out FrameAddress? sourceDirectory, out ObjectVersionBatch? ownedBatch) {
        FrameAddress? source = ResolveContentSource(metadata.Kind, metadata.Parent, address);
        RequireSchemaRoot(metadata.Kind, metadata.SchemaRoot);
        sourceDirectory = source is { } actualSource ? ReadMetadata(actualSource).DirectoryRoot : null;
        ObjectHeadDirectoryVersion directoryVersion = ReadDirectoryVersion(metadata.DirectoryRoot);
        if (sourceDirectory == metadata.DirectoryRoot) {
            if (metadata.OwnedBatch is not null) { throw new InvalidDataException("A reused directory cannot own a new batch."); }
        } else {
            if (directoryVersion.SourceGraph != source) { throw new InvalidDataException("Directory creator does not match the graph's content Parent."); }
            if (directoryVersion.Kind == ObjectVersionKind.Delta &&
                (sourceDirectory is null || directoryVersion.Prior != sourceDirectory)) {
                throw new InvalidDataException("Directory Delta must extend the exact content Parent directory.");
            }
            if (source is null && directoryVersion.Kind != ObjectVersionKind.Base) {
                throw new InvalidDataException("An initial directory must be Base.");
            }
        }
        ownedBatch = metadata.OwnedBatch is { } owned ? ReadBatch(owned) : null;
        if (ownedBatch is { } batch) {
            if (batch.SourceGraph != source) { throw new InvalidDataException("Owned batch does not use the graph's exact content Parent."); }
            if (batch.SchemaRoot != metadata.SchemaRoot) { throw new InvalidDataException("Owned Base Schema root differs from the selected graph view."); }
        }
        return source;
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

    internal ObjectVersionChain ReadObjectChain(FrameAddress graphAddress, uint objectId) {
        if (objectId == 0) { throw new ArgumentOutOfRangeException(nameof(objectId)); }
        StoredGraphRevision graph = ReadGraph(graphAddress);
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

    internal FrameAddress? ResolveContentSource(StoredGraphKind kind, FrameAddress? parent, FrameAddress? current = null) {
        EnsureUsable();
        if (kind == StoredGraphKind.Meta) {
            if (parent is { } previous) {
                if (current == previous || ReadMetadata(previous).Kind != StoredGraphKind.Meta) {
                    throw new InvalidDataException("Meta Parent must be an earlier meta graph.");
                }
            }
            return parent;
        }
        if (kind is not StoredGraphKind.State and not StoredGraphKind.Event) { throw new InvalidDataException("Unknown graph kind."); }
        var visited = new HashSet<FrameAddress>();
        if (current is { } self) { visited.Add(self); }
        while (parent is { } address) {
            if (!visited.Add(address)) { throw new InvalidDataException("Business Parent chain contains a cycle."); }
            GraphEnvelope graph = ReadMetadata(address);
            if (graph.Kind == StoredGraphKind.Meta) { throw new InvalidDataException("Meta is not a business ancestor."); }
            if (graph.Kind == StoredGraphKind.State) { return address; }
            parent = graph.Parent;
        }
        return null;
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

    private void EnsureUsable() {
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
