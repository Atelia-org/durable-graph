using System.Runtime.CompilerServices;
using Atelia.Data;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Xunit.Abstractions;
using Frames = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

// The live-heap witness must not include allocations from concurrent test collections.
[CollectionDefinition("BatchReadGrouping", DisableParallelization = true)]
public sealed class BatchReadGroupingCollection { }

[Collection("BatchReadGrouping")]
public sealed class BatchReadGroupingTests(ITestOutputHelper output) : IDisposable {
    private const string Prefix = "dg-batch-read-grouping-";
    private readonly List<string> _paths = [];

    [Theory]
    [InlineData(16, 0L)]
    [InlineData(32, 0L)]
    [InlineData(16, 1024L)]
    [InlineData(32, 1024L)]
    [InlineData(16, GraphRecordStore.DefaultReadCacheBudgetBytes)]
    [InlineData(32, GraphRecordStore.DefaultReadCacheBudgetBytes)]
    public void BaseBatchProductionDoesNotMultiplyWithItsLiveObjectCount(int count, long budget) {
        using Frames frames = Frames.Create(NewPath());
        FrameAddress batch = Batch(frames, null, Enumerable.Range(1, count).Select(id => B((uint)id, [10])).ToArray());
        FrameAddress graph = Graph(frames, null, Enumerable.Range(1, count).ToDictionary(id => (uint)id, _ => batch), batch);
        using GraphRecordStore reader = new(frames, readCacheBudgetBytes: budget);
        StoredGraphRevision snapshot = reader.ReadBusinessGraph(graph, null);
        Assert.Equal(1, reader.ReadCacheStatistics.BatchDecodes);
        Assert.Equal(count, reader.ReadObjectChains(snapshot).Count());
        long expected = budget == GraphRecordStore.DefaultReadCacheBudgetBytes ? 1 : 2;
        Assert.Equal(expected, reader.ReadCacheStatistics.BatchDecodes);
        output.WriteLine($"N={count}, budget={budget}, qualification=1, cumulative={reader.ReadCacheStatistics.BatchDecodes}");
        Assert.Equal(0, reader.AppendedFrames);
    }

    [Theory]
    [InlineData(16, 0L)]
    [InlineData(32, 0L)]
    [InlineData(16, 1024L)]
    [InlineData(32, 1024L)]
    public void SharedDeepFrontiersProduceEachBatchOncePerDepthAndReenumerateIndependently(int count, long budget) {
        using Frames frames = Frames.Create(NewPath());
        using GraphRecordStore writer = new(frames);
        StoredGraphRevision first = writer.AppendBusiness(StoredGraphKind.State, null, null,
            new(null, new(1), Enumerable.Range(1, count).Select(id => B((uint)id, [10]))));
        StoredGraphRevision second = writer.AppendBusiness(StoredGraphKind.State, first.Address, null,
            new(first.Address, new(1), Enumerable.Range(1, count).Select(id => D((uint)id, first.ObjectHeads[(uint)id], [20]))));
        StoredGraphRevision third = writer.AppendBusiness(StoredGraphKind.State, second.Address, null,
            new(second.Address, new(1), Enumerable.Range(1, count).Select(id => D((uint)id, second.ObjectHeads[(uint)id], [30]))));
        using GraphRecordStore reader = new(frames, readCacheBudgetBytes: budget);
        StoredGraphRevision snapshot = reader.ReadBusinessGraph(third.Address, second.Address);
        Assert.Equal(2, reader.ReadCacheStatistics.BatchDecodes); // Owned head plus grouped immediate prior.
        IEnumerable<ObjectVersionChain> enumerable = reader.ReadObjectChains(snapshot);
        for (int pass = 0; pass < 2; pass++) {
            long before = reader.ReadCacheStatistics.BatchDecodes;
            ObjectVersionChain[] chains = enumerable.ToArray();
            Assert.Equal(count, chains.Length);
            Assert.Equal(3, reader.ReadCacheStatistics.BatchDecodes - before);
            foreach (ObjectVersionChain chain in chains) {
                Assert.Equal(new[] { first.OwnedBatch!.Value, second.OwnedBatch!.Value, third.OwnedBatch!.Value },
                    chain.Entries.Select(entry => entry.ContainingBatch));
                AssertSameChain(writer.ReadObjectChain(third, chain.ObjectId), chain);
            }
        }
    }

    [Fact]
    public void DifferentDepthsMayRevisitAnAddressAndStillMatchIndependentChains() {
        using Frames frames = Frames.Create(NewPath());
        using GraphRecordStore writer = new(frames);
        StoredGraphRevision first = writer.AppendBusiness(StoredGraphKind.State, null, null,
            new(null, new(1), [B(1, [10]), B(2, [11]), B(3, [12])]));
        StoredGraphRevision second = writer.AppendBusiness(StoredGraphKind.State, first.Address, null,
            new(first.Address, new(1), [D(1, first.ObjectHeads[1], [20]), D(2, first.ObjectHeads[2], [21])]));
        StoredGraphRevision third = writer.AppendBusiness(StoredGraphKind.State, second.Address, null,
            new(second.Address, new(1), [D(1, second.ObjectHeads[1], [30])]));
        using GraphRecordStore reader = new(frames, readCacheBudgetBytes: 0);
        StoredGraphRevision snapshot = reader.ReadBusinessGraph(third.Address, second.Address);
        long before = reader.ReadCacheStatistics.BatchDecodes;
        ObjectVersionChain[] chains = reader.ReadObjectChains(snapshot).ToArray();
        Assert.Equal(6, reader.ReadCacheStatistics.BatchDecodes - before); // Frontiers contain 3, 2, then 1 distinct address.
        Assert.Equal(new uint[] { 1, 2, 3 }, chains.Select(chain => chain.ObjectId).Order());
        foreach (ObjectVersionChain chain in chains) { AssertSameChain(writer.ReadObjectChain(third, chain.ObjectId), chain); }
    }

    [Theory]
    [InlineData("StoreDispose")]
    [InlineData("OwnerFault")]
    [InlineData("OwnerDispose")]
    [InlineData("DataClose")]
    [InlineData("HeadsClose")]
    public void SuspendedFrontierRejectsStoppedResourcesBeforeDeliveringAnotherChain(string stop) {
        using RepositoryResources resources = RepositoryResources.CreateStorageNew(NewPath(), readCacheBudgetBytes: 0);
        GraphRecordStore reader = resources.GraphStore;
        StoredGraphRevision first = reader.AppendBusiness(StoredGraphKind.State, null, null,
            new(null, new(1), [B(1, [10]), B(2, [11])]));
        StoredGraphRevision second = reader.AppendBusiness(StoredGraphKind.State, first.Address, null,
            new(first.Address, new(1), [D(1, first.ObjectHeads[1], [20])]));
        using IEnumerator<ObjectVersionChain> iterator = reader.ReadObjectChains(second).GetEnumerator();
        Assert.True(iterator.MoveNext());
        ObjectVersionChain delivered = iterator.Current;
        Assert.Equal(2u, delivered.ObjectId); // ID 1 is pending at the next frontier.
        switch (stop) {
            case "StoreDispose": reader.Dispose(); break;
            case "OwnerFault": resources.MarkFaulted(); break;
            case "OwnerDispose": resources.Dispose(); break;
            case "DataClose": resources.Data.Dispose(); break;
            case "HeadsClose": resources.Heads.Dispose(); break;
        }
        Assert.ThrowsAny<InvalidOperationException>(() => iterator.MoveNext());
        Assert.Equal(new byte[] { 11 }, delivered.Entries[0].Record.Body.ToArray());
        Assert.Equal(0, reader.ReadCacheStatistics.EntryCount);
    }

    [Fact]
    public void LaterDeltaInOneExternalBatchStillChecksItsOwnExactPrior() {
        using Frames frames = Frames.Create(NewPath());
        using GraphRecordStore writer = new(frames);
        StoredGraphRevision initial = writer.AppendBusiness(StoredGraphKind.State, null, null,
            new(null, new(1), [B(1, [10]), B(2, [11])]));
        FrameAddress batch = Batch(frames, initial.Address,
            [B(1, [20]), D(2, initial.DirectoryRoot, [21])]); // Source selects its batch, not this directory address.
        FrameAddress graph = Graph(frames, initial.Address,
            new Dictionary<uint, FrameAddress> { [1] = batch, [2] = batch }, owned: null);
        using GraphRecordStore reader = new(frames, readCacheBudgetBytes: 0);
        StoredGraphRevision snapshot = reader.ReadBusinessGraph(graph, initial.Address); // External declarations stay shallow.
        using IEnumerator<ObjectVersionChain> iterator = reader.ReadObjectChains(snapshot).GetEnumerator();
        Assert.True(iterator.MoveNext());
        ObjectVersionChain first = iterator.Current;
        Assert.Equal(1u, first.ObjectId);
        Assert.Equal(new byte[] { 20 }, Assert.Single(first.Entries).Record.Body.ToArray());
        ObjectVersionChain? second = null;
        InvalidDataException groupedError = Assert.Throws<InvalidDataException>(() => {
            if (iterator.MoveNext()) { second = iterator.Current; }
        });
        Assert.Null(second);
        Assert.Contains("prior", groupedError.Message, StringComparison.OrdinalIgnoreCase);
        InvalidDataException independentError = Assert.Throws<InvalidDataException>(() => reader.ReadObjectChain(snapshot, 2));
        Assert.Equal(independentError.Message, groupedError.Message);
        Assert.Equal(new byte[] { 20 }, first.Entries[0].Record.Body.ToArray());
        Assert.Equal(0, reader.AppendedFrames);
    }

    [Fact]
    public void QualificationChecksLaterMissingIdsWithinTheSameAddressGroup() {
        using Frames frames = Frames.Create(NewPath());
        FrameAddress batch = Batch(frames, null, [B(1, [10])]);
        FrameAddress valid = Graph(frames, null, new Dictionary<uint, FrameAddress> { [1] = batch }, owned: null);
        FrameAddress missing = Graph(frames, null,
            new Dictionary<uint, FrameAddress> { [1] = batch, [2] = batch }, owned: null);
        using GraphRecordStore reader = new(frames, readCacheBudgetBytes: 0);
        Assert.Single(reader.ReadBusinessGraph(valid, null).ObjectHeads);
        long before = reader.ReadCacheStatistics.BatchDecodes;
        StoredGraphRevision? delivered = null;
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => delivered = reader.ReadBusinessGraph(missing, null));
        Assert.Null(delivered);
        Assert.Contains("requested ObjectId", error.Message);
        Assert.Equal(1, reader.ReadCacheStatistics.BatchDecodes - before);
        Assert.Single(reader.ReadBusinessGraph(valid, null).ObjectHeads); // Ordinary bad content does not poison the Store.
    }

    [Fact]
    public void CompletedFrontiersDoNotAccumulateUnselectedLargePeerBodies() {
        const int peerBytes = 2 * 1024 * 1024;
        using Frames frames = Frames.Create(NewPath());
        (FrameAddress graph, FrameAddress? source, FrameAddress[] batches) = PeerHistory(frames, depth: 8, peerBytes);
        using GraphRecordStore reader = new(frames, readCacheBudgetBytes: 0);
        StoredGraphRevision snapshot = reader.ReadBusinessGraph(graph, source);
        WarmBacking(reader, batches);
        long before = GC.GetTotalMemory(forceFullCollection: true);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        using IEnumerator<ObjectVersionChain> iterator = reader.ReadObjectChains(snapshot).GetEnumerator();
        Assert.True(iterator.MoveNext());
        Assert.Equal(9, iterator.Current.Entries.Count);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        long retained = GC.GetTotalMemory(forceFullCollection: true) - before;
        output.WriteLine($"9 batches, peer={peerBytes}, allocated={allocated}, live heap increase at final Base={retained}");
        // Loose live-heap witness, not a total-heap/peak guarantee. A whole-operation
        // batch dictionary would retain all nine 2 MiB peers; only the current batch
        // plus selected tiny records should survive here. Backing buffers are prewarmed.
        Assert.True(retained < 4L * peerBytes, $"Unselected historical peers remain resident: {retained} bytes.");
        Assert.False(iterator.MoveNext());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WarmBacking(GraphRecordStore reader, IEnumerable<FrameAddress> batches) {
        foreach (FrameAddress address in batches) { _ = reader.ReadBatch(address); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (FrameAddress Graph, FrameAddress? Source, FrameAddress[] Batches) PeerHistory(Frames frames, int depth, int peerBytes) {
        List<FrameAddress> batches = [];
        FrameAddress? previous = null;
        FrameAddress? source = null;
        FrameAddress priorBatch = default;
        for (int step = 0; step <= depth; step++) {
            ObjectVersionRecord selected = step == 0 ? B(1, [10]) : D(1, priorBatch, [20]);
            FrameAddress batch = Batch(frames, previous, [selected, B((uint)step + 2, new byte[peerBytes])]);
            // External heads deliberately leave a large valid peer out of live membership.
            // No owned batch claims that the peer is selected by this graph.
            FrameAddress graph = Graph(frames, previous, new Dictionary<uint, FrameAddress> { [1] = batch }, null);
            source = previous;
            previous = graph;
            priorBatch = batch;
            batches.Add(batch);
        }
        return (previous!.Value, source, batches.ToArray());
    }

    private static void AssertSameChain(ObjectVersionChain expected, ObjectVersionChain actual) {
        Assert.Equal(expected.ObjectId, actual.ObjectId);
        Assert.Equal(expected.Head, actual.Head);
        Assert.Equal(expected.ReconstructionPayloadBytes, actual.ReconstructionPayloadBytes);
        Assert.Equal(expected.Entries.Count, actual.Entries.Count);
        for (int index = 0; index < expected.Entries.Count; index++) {
            ObjectVersionChainEntry left = expected.Entries[index], right = actual.Entries[index];
            Assert.Equal(left.ContainingBatch, right.ContainingBatch);
            Assert.Equal(left.SourceGraph, right.SourceGraph);
            Assert.Equal(left.SchemaRoot, right.SchemaRoot);
            Assert.Equal(left.PayloadBytes, right.PayloadBytes);
            Assert.Equal(left.Record.ObjectId, right.Record.ObjectId);
            Assert.Equal(left.Record.Kind, right.Record.Kind);
            Assert.Equal(left.Record.PriorAddress, right.Record.PriorAddress);
            Assert.Equal(left.Record.Body.ToArray(), right.Record.Body.ToArray());
        }
    }

    private string NewPath() {
        string path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
        _paths.Add(path);
        return path;
    }
    private static ObjectVersionRecord B(uint id, byte[] body) => ObjectVersionRecord.CreateBase(id, body);
    private static ObjectVersionRecord D(uint id, FrameAddress prior, byte[] body) => ObjectVersionRecord.CreateDelta(id, prior, body);
    private static FrameAddress Batch(Frames frames, FrameAddress? source, ObjectVersionRecord[] records) =>
        frames.Append(GraphFrameCodec.BatchTag, GraphFrameCodec.EncodeBatch(source, null, records)).Unwrap();
    private static FrameAddress Graph(Frames frames, FrameAddress? source, IReadOnlyDictionary<uint, FrameAddress> heads, FrameAddress? owned) {
        FrameAddress directory = frames.Append(GraphFrameCodec.DirectoryTag,
            GraphFrameCodec.EncodeDirectory(source, ObjectVersionKind.Base, null, ObjectHeadDirectoryCodec.PrepareBase(heads).Body)).Unwrap();
        return frames.Append(GraphFrameCodec.GraphTag,
            GraphFrameCodec.EncodeGraph(StoredGraphKind.State, null, new(1), directory, null, owned)).Unwrap();
    }
    public void Dispose() {
        foreach (string path in _paths) { SharedReadModel.DeleteFixture(path, Prefix); }
    }
}
