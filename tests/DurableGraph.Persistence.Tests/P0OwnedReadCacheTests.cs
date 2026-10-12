using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Frames = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

// Physical owned-value witnesses. These counters are wire decodes and directory
// replay, not typed binding/Normalize counts, disk reads, elapsed time or total heap.
public sealed class P0OwnedReadCacheTests : IDisposable {
    private const string Prefix = "dg-p0-owned-cache-";
    private readonly List<string> _paths = [];

    [Fact]
    public void WarmRolesReuseOwnedFramesAndCompleteDirectoriesWithActualPayloadH() {
        string path = NewPath();
        FrameAddress graph, directory, batch;
        long expectedH;
        using (Frames frames = Frames.Create(path)) {
            using P0GraphStore writer = new(frames) { DirectoryReadAmplificationThreshold = 100 };
            P0GraphSnapshot first = writer.Append(P0GraphKind.State, null, null,
                new(null, new(1), Enumerable.Range(1, 64).Select(id => B((uint)id, [(byte)id]))));
            P0GraphSnapshot second = writer.Append(P0GraphKind.State, first.Address, null,
                new(first.Address, new(1), [D(1, first.Objects[1], [65, 66])]));
            Assert.Equal(ObjectVersionKind.Delta, writer.ReadDirectoryVersion(second.DirectoryRoot).Kind);
            graph = second.Address;
            directory = second.DirectoryRoot;
            batch = second.OwnedBatch!.Value;
            expectedH = writer.ReadDirectoryVersion(first.DirectoryRoot).PayloadBytes +
                writer.ReadDirectoryVersion(directory).PayloadBytes;
            // Append's final actual ReadGraph is allowed to populate the physical cache.
            Assert.True(writer.ReadCacheStatistics.BatchDecodes > 0);
            Assert.NotNull(writer.ReadBatch(batch).Records[0].EncodedPayloadBytes);
            frames.ConfirmDurable();
        }
        using Frames opened = Frames.OpenReadOnly(path);
        using P0GraphStore reader = new(opened);
        Assert.Equal(expectedH, reader.ReadDirectoryPayloadBytes(directory));
        P0GraphReadCacheStatistics costOnly = reader.ReadCacheStatistics;
        Assert.Equal(0, costOnly.DirectoryMaterializations);
        Assert.Equal(expectedH, reader.ReadDirectoryPayloadBytes(directory));
        Assert.Equal(0, reader.ReadCacheStatistics.DirectoryMaterializations);
        Assert.Equal(0, reader.ReadCacheStatistics.DirectoryPayloadHits); // H-only misses do not admit an incomplete map.
        Assert.True(reader.ReadCacheStatistics.DirectoryReplayFrames > costOnly.DirectoryReplayFrames);
        P0GraphSnapshot firstRead = reader.ReadGraph(graph);
        IReadOnlyDictionary<uint, FrameAddress> heads = reader.ReadDirectory(directory);
        P0Batch ownedBatch = reader.ReadBatch(batch);
        P0DirectoryVersion version = reader.ReadDirectoryVersion(directory);
        P0ObjectChain chain = reader.ReadObjectChain(graph, 1);
        Assert.Equal(2, chain.Entries.Count);
        Assert.Equal(ObjectVersionPayloadSize.GetBasePayloadBytes(1) +
            ObjectVersionPayloadSize.GetDeltaPayloadBytes(2, chain.Entries[0].ContainingBatch), chain.ReconstructionPayloadBytes);
        Assert.Equal(expectedH, reader.ReadDirectoryPayloadBytes(directory));
        P0GraphReadCacheStatistics before = reader.ReadCacheStatistics;
        Assert.Equal(firstRead.Objects.OrderBy(row => row.Key), reader.ReadGraph(graph).Objects.OrderBy(row => row.Key));
        Assert.Same(heads, reader.ReadDirectory(directory));
        Assert.Same(ownedBatch, reader.ReadBatch(batch));
        Assert.Same(version, reader.ReadDirectoryVersion(directory));
        Assert.Equal(chain.ReconstructionPayloadBytes, reader.ReadObjectChain(graph, 1).ReconstructionPayloadBytes);
        Assert.Equal(expectedH, reader.ReadDirectoryPayloadBytes(directory));
        P0GraphReadCacheStatistics after = reader.ReadCacheStatistics;
        AssertPhysicalProductionEqual(before, after);
        Assert.True(after.GraphHits > before.GraphHits);
        Assert.True(after.BatchHits > before.BatchHits);
        Assert.True(after.DirectoryVersionHits > before.DirectoryVersionHits);
        Assert.True(after.DirectoryHits > before.DirectoryHits);
        Assert.True(after.DirectoryPayloadHits > before.DirectoryPayloadHits);
    }

    [Fact]
    public void FixedRolesAndDirectoryComponentsShareOneLruAndOversizeDoesNotSweepHotEntries() {
        using Frames frames = Frames.Create(NewPath());
        FrameAddress batchAddress = RawBatch(frames, null, [B(1, [10])]);
        FrameAddress directoryAddress = RawDirectory(frames, null, ObjectVersionKind.Base, null, Map((1u, batchAddress)));
        FrameAddress graphAddress = RawGraph(frames, null, directoryAddress, null);
        long directoryEntry, batchEntry, graphEntry;
        using (P0GraphStore probe = new(frames)) {
            directoryEntry = P0GraphReadCache.EntryOverheadBytes + P0GraphReadCache.GetDirectoryVersionCharge(probe.ReadDirectoryVersion(directoryAddress)) +
                P0GraphReadCache.GetDirectoryCharge(1);
            batchEntry = P0GraphReadCache.EntryOverheadBytes + P0GraphReadCache.GetBatchCharge(probe.ReadBatch(batchAddress));
            graphEntry = P0GraphReadCache.EntryOverheadBytes + P0GraphReadCache.GetGraphCharge();
        }
        Assert.True(graphEntry <= batchEntry);
        long budget = directoryEntry + batchEntry;
        using P0GraphStore reader = new(frames, readCacheBudgetBytes: budget);
        P0DirectoryVersion version = reader.ReadDirectoryVersion(directoryAddress);
        IReadOnlyDictionary<uint, FrameAddress> heads = reader.ReadDirectory(directoryAddress);
        P0Batch batch = reader.ReadBatch(batchAddress);
        Assert.Equal(budget, reader.ReadCacheStatistics.ResidentChargeBytes);
        Assert.Same(heads, reader.ReadDirectory(directoryAddress)); // Touching the map promotes its frame component too.
        reader.ReadMetadata(graphAddress);
        Assert.Equal(1, reader.ReadCacheStatistics.Evictions);
        Assert.Same(version, reader.ReadDirectoryVersion(directoryAddress));
        Assert.NotSame(batch, reader.ReadBatch(batchAddress));
        P0Batch reloaded = reader.ReadBatch(batchAddress);
        Assert.Same(heads, reader.ReadDirectory(directoryAddress));
        long evictions = reader.ReadCacheStatistics.Evictions, charge = reader.ReadCacheStatistics.ResidentChargeBytes;
        FrameAddress huge = RawBatch(frames, null, [B(2, new byte[32 * 1024])]);
        Assert.Equal(32 * 1024, reader.ReadBatch(huge).Records[0].Body.Length);
        Assert.True(reader.ReadCacheStatistics.AdmissionBypasses > 0);
        Assert.Equal(evictions, reader.ReadCacheStatistics.Evictions);
        Assert.Equal(charge, reader.ReadCacheStatistics.ResidentChargeBytes);
        Assert.Same(reloaded, reader.ReadBatch(batchAddress));
        Assert.Same(version, reader.ReadDirectoryVersion(directoryAddress));
        Assert.InRange(reader.ReadCacheStatistics.PeakResidentChargeBytes, 1, budget);
        // Cached role qualification precedes any attempt to reinterpret an address.
        Assert.Throws<InvalidDataException>(() => reader.ReadMetadata(batchAddress));
        Assert.Throws<InvalidDataException>(() => reader.ReadBatch(directoryAddress));
        Assert.Throws<InvalidDataException>(() => reader.ReadDirectoryVersion(batchAddress));
        Assert.Same(reloaded, reader.ReadBatch(batchAddress));
    }

    [Fact]
    public void ZeroBudgetRepeatsProductionAndNegativeBudgetsRejectBeforeBackingUse() {
        using Frames frames = Frames.Create(NewPath());
        int ownerChecks = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() => new P0GraphStore(frames, () => ownerChecks++, readCacheBudgetBytes: -1));
        Assert.Equal(0, ownerChecks);
        string repositoryPath = NewPath();
        Assert.Throws<ArgumentOutOfRangeException>(() => Repository.CreateMetaBootstrapNew(repositoryPath, new(), readCacheBudgetBytes: -1));
        Assert.False(Directory.Exists(repositoryPath));
        FrameAddress batch = RawBatch(frames, null, [B(1, [10])]);
        FrameAddress directory = RawDirectory(frames, null, ObjectVersionKind.Base, null, Map((1u, batch)));
        using P0GraphStore reader = new(frames, readCacheBudgetBytes: 0);
        Assert.NotSame(reader.ReadBatch(batch), reader.ReadBatch(batch));
        Assert.NotSame(reader.ReadDirectory(directory), reader.ReadDirectory(directory));
        P0GraphReadCacheStatistics stats = reader.ReadCacheStatistics;
        Assert.Equal(2, stats.BatchDecodes);
        Assert.Equal(2, stats.DirectoryVersionDecodes);
        Assert.Equal(2, stats.DirectoryMaterializations);
        Assert.Equal(2, stats.DirectoryReplayFrames);
        Assert.Equal(0, stats.BatchHits);
        Assert.Equal(0, stats.DirectoryHits);
        Assert.Equal(0, stats.EntryCount);
        Assert.Equal(0, stats.ResidentChargeBytes);
        Assert.Equal(0, stats.PeakResidentChargeBytes);
    }

    [Theory]
    [InlineData("owned")]
    [InlineData("creator")]
    public void WarmValidDirectoryCannotQualifyAnInvalidGraphRelationship(string error) {
        using Frames frames = Frames.Create(NewPath());
        using P0GraphStore reader = new(frames);
        P0GraphSnapshot first = reader.Append(P0GraphKind.State, null, null, new(null, new(1), [B(1, [10])]));
        P0GraphSnapshot second = reader.Append(P0GraphKind.State, first.Address, null, new(first.Address, new(1), [B(1, [20])]));
        FrameAddress directory, owned, parent;
        if (error == "owned") {
            directory = first.DirectoryRoot;
            owned = RawBatch(frames, first.Address, [B(1, [30])]);
            parent = first.Address;
        } else {
            directory = RawDirectory(frames, first.Address, ObjectVersionKind.Base, null, second.Objects);
            owned = second.OwnedBatch!.Value;
            parent = second.Address;
        }
        FrameAddress bad = RawGraph(frames, parent, directory, error == "owned" ? owned : null);
        reader.ReadGraph(first.Address);
        reader.ReadGraph(second.Address);
        reader.ReadBatch(owned);
        reader.ReadDirectoryVersion(directory);
        reader.ReadDirectory(directory);
        reader.ReadMetadata(bad);
        P0GraphReadCacheStatistics before = reader.ReadCacheStatistics;
        Assert.Throws<InvalidDataException>(() => reader.ReadGraph(bad));
        Assert.Throws<InvalidDataException>(() => reader.ReadGraph(bad));
        AssertPhysicalProductionEqual(before, reader.ReadCacheStatistics);
        Assert.True(reader.ReadCacheStatistics.GraphHits > before.GraphHits);
        Assert.True(reader.ReadCacheStatistics.ResidentChargeBytes > 0); // Ordinary semantic rejection retains correct raw/map values.
        if (error == "creator") {
            FrameAddress positive = RawGraph(frames, first.Address, directory, owned);
            Assert.Equal(second.Objects[1], reader.ReadGraph(positive).Objects[1]);
        } else { Assert.Equal(first.Objects[1], reader.ReadGraph(first.Address).Objects[1]); }
    }

    [Fact]
    public void WarmBadPriorAndFailedDirectoryReplayStayRejectedWhileNewBaseCutsBadContent() {
        using Frames frames = Frames.Create(NewPath());
        using P0GraphStore reader = new(frames);
        P0GraphSnapshot first = reader.Append(P0GraphKind.State, null, null, new(null, new(1), [B(1, [10])]));
        P0GraphSnapshot second = reader.Append(P0GraphKind.State, first.Address, null, new(first.Address, new(1), [D(1, first.Objects[1], [20])]));
        FrameAddress wrongBatch = RawBatch(frames, second.Address, [D(1, first.Objects[1], [30])]);
        FrameAddress wrongDirectory = RawDirectory(frames, second.Address, ObjectVersionKind.Base, null, Map((1u, wrongBatch)));
        FrameAddress retained = RawGraph(frames, second.Address, wrongDirectory, null);
        Assert.Equal(wrongBatch, reader.ReadGraph(retained).Objects[1]); // This external-head declaration is intentionally shallow.
        reader.ReadDirectory(second.DirectoryRoot);
        reader.ReadBatch(wrongBatch);
        P0GraphReadCacheStatistics before = reader.ReadCacheStatistics;
        Assert.Throws<InvalidDataException>(() => reader.ReadObjectChain(retained, 1));
        Assert.Throws<InvalidDataException>(() => reader.ReadObjectChain(retained, 1));
        AssertPhysicalProductionEqual(before, reader.ReadCacheStatistics);
        P0GraphSnapshot reset = reader.Append(P0GraphKind.State, retained, null, new(retained, new(1), [B(1, [])]));
        P0ObjectChain resetChain = reader.ReadObjectChain(reset.Address, 1);
        Assert.Single(resetChain.Entries);
        Assert.Equal(reset.OwnedBatch, resetChain.Head);
        Assert.Equal(ObjectVersionPayloadSize.GetBasePayloadBytes(0), resetChain.ReconstructionPayloadBytes);

        FrameAddress malformed = frames.Append(P0GraphWire.DirectoryTag,
            P0GraphWire.EncodeDirectory(second.Address, ObjectVersionKind.Delta, second.DirectoryRoot, [])).Unwrap();
        reader.ReadDirectoryVersion(malformed); // Valid owned frame envelope; invalid Dictionary Delta body.
        Assert.Throws<EndOfStreamException>(() => reader.ReadDirectory(malformed));
        P0GraphReadCacheStatistics failed = reader.ReadCacheStatistics;
        Assert.Throws<EndOfStreamException>(() => reader.ReadDirectory(malformed));
        Assert.Equal(failed.DirectoryHits, reader.ReadCacheStatistics.DirectoryHits);
        Assert.Equal(failed.DirectoryMaterializations, reader.ReadCacheStatistics.DirectoryMaterializations);
        Assert.Equal(failed.DirectoryVersionDecodes, reader.ReadCacheStatistics.DirectoryVersionDecodes);
        Assert.True(reader.ReadCacheStatistics.DirectoryReplayFrames > failed.DirectoryReplayFrames);
        Assert.True(reader.ReadCacheStatistics.DirectoryVersionHits > failed.DirectoryVersionHits);
        Assert.Equal(second.Objects[1], reader.ReadDirectory(second.DirectoryRoot)[1]);
    }

    [Fact]
    public void OwnedBuffersAndMapsCannotBePollutedAndEqualAddressesRemainLocalToEachStore() {
        using Frames firstFrames = Frames.Create(NewPath());
        using Frames secondFrames = Frames.Create(NewPath());
        FrameAddress firstAddress = RawBatch(firstFrames, null, [B(1, [10])]);
        FrameAddress secondAddress = RawBatch(secondFrames, null, [B(1, [20])]);
        Assert.Equal(firstAddress, secondAddress);
        using P0GraphStore first = new(firstFrames), second = new(secondFrames);
        P0Batch firstBatch = first.ReadBatch(firstAddress), secondBatch = second.ReadBatch(secondAddress);
        Assert.NotSame(firstBatch, secondBatch);
        Assert.Equal(new byte[] { 10 }, firstBatch.Records[0].Body.ToArray());
        Assert.Equal(new byte[] { 20 }, secondBatch.Records[0].Body.ToArray());
        byte[] batchCopy = firstBatch.Records[0].Body.ToArray();
        batchCopy[0] = 99;
        Assert.Equal(new byte[] { 10 }, first.ReadBatch(firstAddress).Records[0].Body.ToArray());
        Assert.False(firstBatch.Records is ObjectVersionRecord[]);
        Assert.False(firstBatch.Records is IList<ObjectVersionRecord>);
        byte[] input = [1, 2, 3];
        P0DirectoryVersion standalone = new(null, ObjectVersionKind.Base, null, input, checked((int)ObjectVersionPayloadSize.GetBasePayloadBytes(input.Length)));
        input[0] = 99;
        Assert.Equal(new byte[] { 1, 2, 3 }, standalone.Body.ToArray()); // Constructor must own the original mutable input.
        FrameAddress directory = RawDirectory(firstFrames, null, ObjectVersionKind.Base, null, Map((1u, firstAddress)));
        P0DirectoryVersion version = first.ReadDirectoryVersion(directory);
        byte[] original = version.Body.ToArray(), copy = version.Body.ToArray();
        copy[0] ^= 0xff;
        Assert.Equal(original, first.ReadDirectoryVersion(directory).Body.ToArray());
        IReadOnlyDictionary<uint, FrameAddress> heads = first.ReadDirectory(directory);
        Assert.False(heads is IDictionary<uint, FrameAddress>);
        KeyValuePair<uint, FrameAddress>[] mapCopy = heads.ToArray();
        mapCopy[0] = new(1, directory);
        Assert.Equal(firstAddress, first.ReadDirectory(directory)[1]);
        first.Dispose();
        Assert.Equal(original, version.Body.ToArray());
        Assert.Equal(firstAddress, heads[1]);
        Assert.Equal(new byte[] { 10 }, firstBatch.Records[0].Body.ToArray());
        Assert.Same(secondBatch, second.ReadBatch(secondAddress));
    }

    [Theory]
    [InlineData("StoreDispose")]
    [InlineData("OwnerFault")]
    [InlineData("OwnerDispose")]
    [InlineData("DataClose")]
    [InlineData("VersionsClose")]
    public void WarmBorrowedReadsRejectClosedOrFaultedResourcesAndReleaseResidency(string stop) {
        using GraphResources resources = GraphResources.CreateMetaBootstrapNew(NewPath());
        using P0Repository facade = new(resources);
        using (P0PendingSave pending = facade.Prepare("main", P0GraphKind.State,
            new(null, new(1), [B(1, [10])]), new(null, new(1), [B(1, [0])]))) { facade.Commit(pending); }
        P0GraphStore retained = facade.Store;
        FrameAddress address = facade.GetHead("main")!.Value;
        P0GraphSnapshot graph = retained.ReadGraph(address);
        P0Batch batch = retained.ReadBatch(graph.OwnedBatch!.Value);
        P0DirectoryVersion directory = retained.ReadDirectoryVersion(graph.DirectoryRoot);
        IReadOnlyDictionary<uint, FrameAddress> heads = retained.ReadDirectory(graph.DirectoryRoot);
        Assert.True(retained.ReadCacheStatistics.ResidentChargeBytes > 0);
        switch (stop) {
            case "StoreDispose": retained.Dispose(); break;
            case "OwnerFault": resources.MarkFaulted(); break;
            case "OwnerDispose": resources.Dispose(); break;
            case "DataClose": resources.Data.Dispose(); break;
            case "VersionsClose": resources.Versions.Dispose(); break;
            default: throw new InvalidOperationException(stop);
        }
        Assert.ThrowsAny<InvalidOperationException>(() => retained.ReadMetadata(address));
        Assert.ThrowsAny<InvalidOperationException>(() => retained.ReadBatch(graph.OwnedBatch.Value));
        Assert.ThrowsAny<InvalidOperationException>(() => retained.ReadDirectoryVersion(graph.DirectoryRoot));
        Assert.ThrowsAny<InvalidOperationException>(() => retained.ReadDirectory(graph.DirectoryRoot));
        Assert.ThrowsAny<InvalidOperationException>(() => retained.ReadDirectoryPayloadBytes(graph.DirectoryRoot));
        Assert.ThrowsAny<InvalidOperationException>(() => retained.ReadGraph(address));
        Assert.ThrowsAny<InvalidOperationException>(() => retained.ReadObjectChain(address, 1));
        Assert.Equal(0, retained.ReadCacheStatistics.ResidentChargeBytes);
        Assert.Equal(0, retained.ReadCacheStatistics.EntryCount);
        Assert.Equal(graph.Objects[1], heads[1]);
        Assert.Equal(new byte[] { 10 }, batch.Records[0].Body.ToArray());
        Assert.NotEmpty(directory.Body.ToArray());
        if (stop == "StoreDispose") {
            using FrameRead liveBacking = resources.Data.ReadFrame(graph.OwnedBatch.Value).Unwrap();
            Assert.Equal(P0GraphWire.BatchTag, liveBacking.Tag); // Disposing a borrowed Store does not close its backing owner.
        }
    }

    private string NewPath() {
        string path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
        _paths.Add(path);
        return path;
    }
    private static ObjectVersionRecord B(uint id, byte[] body) => ObjectVersionRecord.CreateBase(id, body);
    private static ObjectVersionRecord D(uint id, FrameAddress prior, byte[] body) => ObjectVersionRecord.CreateDelta(id, prior, body);
    private static SortedDictionary<uint, FrameAddress> Map(params (uint Id, FrameAddress Head)[] rows) => new(rows.ToDictionary(row => row.Id, row => row.Head));
    private static FrameAddress RawBatch(Frames frames, FrameAddress? source, ObjectVersionRecord[] records) =>
        frames.Append(P0GraphWire.BatchTag, P0GraphWire.EncodeBatch(source, null, records)).Unwrap();
    private static FrameAddress RawDirectory(Frames frames, FrameAddress? source, ObjectVersionKind kind, FrameAddress? prior,
        IReadOnlyDictionary<uint, FrameAddress> heads) => frames.Append(P0GraphWire.DirectoryTag,
        P0GraphWire.EncodeDirectory(source, kind, prior, P0DirectoryCodec.PrepareBase(heads).Body)).Unwrap();
    private static FrameAddress RawGraph(Frames frames, FrameAddress? parent, FrameAddress directory, FrameAddress? batch) =>
        frames.Append(P0GraphWire.GraphTag, P0GraphWire.EncodeGraph(P0GraphKind.State, parent, new(1), directory, null, batch)).Unwrap();
    private static void AssertPhysicalProductionEqual(P0GraphReadCacheStatistics expected, P0GraphReadCacheStatistics actual) {
        Assert.Equal(expected.GraphDecodes, actual.GraphDecodes);
        Assert.Equal(expected.BatchDecodes, actual.BatchDecodes);
        Assert.Equal(expected.DirectoryVersionDecodes, actual.DirectoryVersionDecodes);
        Assert.Equal(expected.DirectoryMaterializations, actual.DirectoryMaterializations);
        Assert.Equal(expected.DirectoryReplayFrames, actual.DirectoryReplayFrames);
    }
    public void Dispose() {
        foreach (string path in _paths) { SharedReadModel.DeleteFixture(path, Prefix); }
    }
}
