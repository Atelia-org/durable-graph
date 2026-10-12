using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using DataStore = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Persistence.MetaBootstrapP0;

// A borrowed, uncached P0 facade. Reading a graph validates its selected physical
// relations; typed body/view/reference qualification remains with each producer.
internal sealed class P0GraphStore : IDisposable {
    private readonly DataStore _frames;
    private readonly Action? _ensureOwner;
    private bool _disposed;
    private bool _appending;
    private int _directoryThreshold = 5;

    internal P0GraphStore(DataStore frames, Action? ensureOwner = null) {
        ArgumentNullException.ThrowIfNull(frames);
        _frames = frames;
        _ensureOwner = ensureOwner;
        EnsureUsable();
    }

    internal long AppendedFrames { get; private set; }
    internal int DirectoryReadAmplificationThreshold {
        get => _directoryThreshold;
        set {
            if (value < 1) { throw new ArgumentOutOfRangeException(nameof(value)); }
            _directoryThreshold = value;
        }
    }

    internal P0GraphSnapshot Append(P0GraphKind kind, FrameAddress? parent,
        FrameAddress? schemaRoot, P0PreparedGraphWrite write) {
        EnsureUsable();
        if (_appending) { throw new InvalidOperationException("P0 graph writes cannot be reentered."); }
        ArgumentNullException.ThrowIfNull(write);
        _appending = true;
        try {
            FrameAddress? source = ResolveContentSource(kind, parent);
            if (write.SourceGraph != source) { throw new InvalidDataException("Prepared source is not the exact content Parent."); }
            RequireSchemaRoot(kind, schemaRoot);
            P0GraphMetadata? sourceMetadata = source is { } address ? ReadMetadata(address) : null;
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
                batch = AppendFrame(P0GraphWire.BatchTag, P0GraphWire.EncodeBatch(source, schemaRoot, write.Records));
                foreach (ObjectVersionRecord record in write.Records) {
                    finalHeads[record.ObjectId] = batch.Value;
                    writtenHeads.Add(record.ObjectId, batch.Value);
                }
            }

            FrameAddress directory;
            if (sourceMetadata is { } baseline) {
                var delta = P0DirectoryCodec.PrepareDelta(sourceHeads, write.RemovedIds, writtenHeads);
                if (!delta.HasChanges) {
                    if (batch is not null) { throw new InvalidDataException("A new batch must change the selected directory."); }
                    directory = baseline.DirectoryRoot;
                } else {
                    var baseBody = P0DirectoryCodec.PrepareBase(finalHeads);
                    long b = ObjectVersionPayloadSize.GetBasePayloadBytes(baseBody.Body.Length);
                    long d = ObjectVersionPayloadSize.GetDeltaPayloadBytes(delta.Body.Length, baseline.DirectoryRoot);
                    long h = ReadDirectoryPayloadBytes(baseline.DirectoryRoot);
                    bool useBase = b <= d || (Int128)h + d > (Int128)_directoryThreshold * b;
                    directory = AppendFrame(P0GraphWire.DirectoryTag, useBase
                        ? P0GraphWire.EncodeDirectory(source, ObjectVersionKind.Base, null, baseBody.Body)
                        : P0GraphWire.EncodeDirectory(source, ObjectVersionKind.Delta, baseline.DirectoryRoot, delta.Body));
                }
            } else {
                var baseBody = P0DirectoryCodec.PrepareBase(finalHeads);
                directory = AppendFrame(P0GraphWire.DirectoryTag,
                    P0GraphWire.EncodeDirectory(null, ObjectVersionKind.Base, null, baseBody.Body));
            }
            FrameAddress graph = AppendFrame(P0GraphWire.GraphTag,
                P0GraphWire.EncodeGraph(kind, parent, write.RootId, directory, schemaRoot, batch));
            return ReadGraph(graph);
        } finally { _appending = false; }
    }

    internal P0GraphSnapshot ReadGraph(FrameAddress address) {
        EnsureUsable();
        P0GraphMetadata metadata = ReadMetadata(address);
        FrameAddress? source = ResolveContentSource(metadata.Kind, metadata.Parent, address);
        RequireSchemaRoot(metadata.Kind, metadata.SchemaRoot);
        FrameAddress? sourceDirectory = source is { } actualSource ? ReadMetadata(actualSource).DirectoryRoot : null;
        P0DirectoryVersion directoryVersion = ReadDirectoryVersion(metadata.DirectoryRoot);
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
        IReadOnlyDictionary<uint, FrameAddress> heads = ReadDirectory(metadata.DirectoryRoot);
        if (!heads.ContainsKey(metadata.RootId.Value)) { throw new InvalidDataException("RootId is not live."); }
        if (metadata.OwnedBatch is { } owned) {
            P0Batch batch = ReadBatch(owned);
            if (batch.SourceGraph != source) { throw new InvalidDataException("Owned batch does not use the graph's exact content Parent."); }
            if (batch.SchemaRoot != metadata.SchemaRoot) { throw new InvalidDataException("Owned Base Schema root differs from the selected graph view."); }
            IReadOnlyDictionary<uint, FrameAddress>? sourceHeads = null;
            foreach (ObjectVersionRecord record in batch.Records) {
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
        return new(address, metadata, source, heads);
    }

    internal P0GraphMetadata ReadMetadata(FrameAddress address) {
        EnsureUsable();
        return P0GraphWire.DecodeGraph(ReadPayload(address, P0GraphWire.GraphTag));
    }

    internal P0Batch ReadBatch(FrameAddress address) {
        EnsureUsable();
        return P0GraphWire.DecodeBatch(ReadPayload(address, P0GraphWire.BatchTag));
    }

    internal P0DirectoryVersion ReadDirectoryVersion(FrameAddress address) {
        EnsureUsable();
        return P0GraphWire.DecodeDirectory(ReadPayload(address, P0GraphWire.DirectoryTag));
    }

    internal IReadOnlyDictionary<uint, FrameAddress> ReadDirectory(FrameAddress address) {
        EnsureUsable();
        var chain = ReadDirectoryChain(address);
        IReadOnlyDictionary<uint, FrameAddress> heads = P0DirectoryCodec.ReadBase(chain[0].Body);
        for (int i = 1; i < chain.Count; i++) { heads = P0DirectoryCodec.ApplyDelta(heads, chain[i].Body); }
        return new FrozenSortedDictionary<uint, FrameAddress>(new SortedDictionary<uint, FrameAddress>(
            heads.ToDictionary(static pair => pair.Key, static pair => pair.Value)));
    }

    internal long ReadDirectoryPayloadBytes(FrameAddress address) =>
        ReadDirectoryChain(address).Aggregate(0L, static (bytes, version) => checked(bytes + version.PayloadBytes));

    internal P0ObjectChain ReadObjectChain(FrameAddress graphAddress, uint objectId) {
        if (objectId == 0) { throw new ArgumentOutOfRangeException(nameof(objectId)); }
        P0GraphSnapshot graph = ReadGraph(graphAddress);
        if (!graph.Objects.TryGetValue(objectId, out FrameAddress head)) { throw new InvalidDataException("ObjectId is not live in the graph."); }
        FrameAddress address = head;
        var visited = new HashSet<FrameAddress>();
        var entries = new List<P0ChainEntry>();
        while (true) {
            if (!visited.Add(address)) { throw new InvalidDataException("Object prior chain contains a cycle."); }
            P0Batch batch = ReadBatch(address);
            ObjectVersionRecord record = FindRecord(batch, objectId);
            entries.Add(new(record, address, batch.SourceGraph, batch.SchemaRoot,
                record.EncodedPayloadBytes ?? throw new InvalidDataException("Read records require actual payload accounting.")));
            if (record.Kind == ObjectVersionKind.Base) { break; }
            FrameAddress source = batch.SourceGraph ?? throw new InvalidDataException("Object Delta lacks a source graph.");
            P0GraphMetadata sourceGraph = ReadMetadata(source);
            RequireContentSourceRole(sourceGraph.Kind);
            IReadOnlyDictionary<uint, FrameAddress> sourceHeads = ReadDirectory(sourceGraph.DirectoryRoot);
            RequirePrior(sourceHeads, objectId, record.PriorAddress);
            address = record.PriorAddress!.Value;
        }
        entries.Reverse();
        return new(objectId, head, entries);
    }

    internal FrameAddress? ResolveContentSource(P0GraphKind kind, FrameAddress? parent, FrameAddress? current = null) {
        EnsureUsable();
        if (kind == P0GraphKind.Meta) {
            if (parent is { } previous) {
                if (current == previous || ReadMetadata(previous).Kind != P0GraphKind.Meta) {
                    throw new InvalidDataException("Meta Parent must be an earlier meta graph.");
                }
            }
            return parent;
        }
        if (kind is not P0GraphKind.State and not P0GraphKind.Event) { throw new InvalidDataException("Unknown graph kind."); }
        var visited = new HashSet<FrameAddress>();
        if (current is { } self) { visited.Add(self); }
        while (parent is { } address) {
            if (!visited.Add(address)) { throw new InvalidDataException("Business Parent chain contains a cycle."); }
            P0GraphMetadata graph = ReadMetadata(address);
            if (graph.Kind == P0GraphKind.Meta) { throw new InvalidDataException("Meta is not a business ancestor."); }
            if (graph.Kind == P0GraphKind.State) { return address; }
            parent = graph.Parent;
        }
        return null;
    }

    private IReadOnlyList<P0DirectoryVersion> ReadDirectoryChain(FrameAddress address) {
        var visited = new HashSet<FrameAddress>();
        var versions = new List<P0DirectoryVersion>();
        while (true) {
            if (!visited.Add(address)) { throw new InvalidDataException("Directory prior chain contains a cycle."); }
            P0DirectoryVersion version = ReadDirectoryVersion(address);
            versions.Add(version);
            // A new Base does not certify or read a discarded earlier content chain.
            if (version.Kind == ObjectVersionKind.Base) { break; }
            FrameAddress source = version.SourceGraph ?? throw new InvalidDataException("Directory Delta lacks source graph.");
            P0GraphMetadata graph = ReadMetadata(source);
            RequireContentSourceRole(graph.Kind);
            if (version.Prior != graph.DirectoryRoot) { throw new InvalidDataException("Directory prior differs from its source graph's directory."); }
            address = version.Prior!.Value;
        }
        versions.Reverse();
        return versions;
    }

    private void RequireSchemaRoot(P0GraphKind kind, FrameAddress? schemaRoot) {
        if (kind == P0GraphKind.Meta) {
            if (schemaRoot is not null) { throw new InvalidDataException("Meta has fixed interpretation without an external Schema root."); }
        } else if (schemaRoot is { } schema && ReadMetadata(schema).Kind != P0GraphKind.Meta) {
            throw new InvalidDataException("SchemaRoot must select a meta graph.");
        }
    }

    private static void RequireContentSourceRole(P0GraphKind kind) {
        if (kind is not P0GraphKind.State and not P0GraphKind.Meta) { throw new InvalidDataException("Content source must be State or meta."); }
    }

    private static void RequirePrior(IReadOnlyDictionary<uint, FrameAddress> heads, uint id, FrameAddress? prior) {
        if (prior is null || !heads.TryGetValue(id, out FrameAddress expected) || expected != prior.Value) {
            throw new InvalidDataException("Object Delta prior is not the head selected by its exact source graph.");
        }
    }

    private static ObjectVersionRecord FindRecord(P0Batch batch, uint id) {
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
        _frames.EnsureUsable();
        _ensureOwner?.Invoke();
    }

    public void Dispose() => _disposed = true;
}
