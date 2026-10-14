using System.Buffers.Binary;
using Atelia.Data;

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
            using GraphRecordStore writer = new(frames) { DirectoryReadAmplificationThreshold = 100 };
            StoredGraphRevision first = writer.Append(StoredGraphKind.State, null, null,
                new(null, new(1), Enumerable.Range(1, 64).Select(id => B((uint)id, [(byte)id]))));
            StoredGraphRevision second = writer.Append(StoredGraphKind.State, first.Address, null,
                new(first.Address, new(1), [D(1, first.ObjectHeads[1], [65, 66])]));
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
        using GraphRecordStore reader = new(opened);
        Assert.Equal(expectedH, reader.ReadDirectoryPayloadBytes(directory));
        GraphPhysicalReadStatistics costOnly = reader.ReadCacheStatistics;
        Assert.Equal(0, costOnly.DirectoryMaterializations);
        Assert.Equal(expectedH, reader.ReadDirectoryPayloadBytes(directory));
        Assert.Equal(0, reader.ReadCacheStatistics.DirectoryMaterializations);
        Assert.Equal(0, reader.ReadCacheStatistics.DirectoryPayloadHits); // H-only misses do not admit an incomplete map.
        Assert.True(reader.ReadCacheStatistics.DirectoryReplayFrames > costOnly.DirectoryReplayFrames);
        StoredGraphRevision firstRead = reader.ReadGraph(graph);
        IReadOnlyDictionary<uint, FrameAddress> heads = reader.ReadDirectory(directory);
        ObjectVersionBatch ownedBatch = reader.ReadBatch(batch);
        ObjectHeadDirectoryVersion version = reader.ReadDirectoryVersion(directory);
        ObjectVersionChain chain = reader.ReadObjectChain(graph, 1);
        Assert.Equal(2, chain.Entries.Count);
        Assert.Equal(ObjectVersionPayloadSize.GetBasePayloadBytes(1) +
            ObjectVersionPayloadSize.GetDeltaPayloadBytes(2, chain.Entries[0].ContainingBatch), chain.ReconstructionPayloadBytes);
        Assert.Equal(expectedH, reader.ReadDirectoryPayloadBytes(directory));
        GraphPhysicalReadStatistics before = reader.ReadCacheStatistics;
        Assert.Equal(firstRead.ObjectHeads.OrderBy(row => row.Key), reader.ReadGraph(graph).ObjectHeads.OrderBy(row => row.Key));
        Assert.Same(heads, reader.ReadDirectory(directory));
        Assert.Same(ownedBatch, reader.ReadBatch(batch));
        Assert.Same(version, reader.ReadDirectoryVersion(directory));
        Assert.Equal(chain.ReconstructionPayloadBytes, reader.ReadObjectChain(graph, 1).ReconstructionPayloadBytes);
        Assert.Equal(expectedH, reader.ReadDirectoryPayloadBytes(directory));
        GraphPhysicalReadStatistics after = reader.ReadCacheStatistics;
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
        using (GraphRecordStore probe = new(frames)) {
            directoryEntry = GraphPhysicalReadCache.EntryOverheadBytes + GraphPhysicalReadCache.GetDirectoryVersionCharge(probe.ReadDirectoryVersion(directoryAddress)) +
                GraphPhysicalReadCache.GetDirectoryCharge(1);
            batchEntry = GraphPhysicalReadCache.EntryOverheadBytes + GraphPhysicalReadCache.GetBatchCharge(probe.ReadBatch(batchAddress));
            graphEntry = GraphPhysicalReadCache.EntryOverheadBytes + GraphPhysicalReadCache.GetGraphCharge();
        }
        Assert.True(graphEntry <= batchEntry);
        long budget = directoryEntry + batchEntry;
        using GraphRecordStore reader = new(frames, readCacheBudgetBytes: budget);
        ObjectHeadDirectoryVersion version = reader.ReadDirectoryVersion(directoryAddress);
        IReadOnlyDictionary<uint, FrameAddress> heads = reader.ReadDirectory(directoryAddress);
        ObjectVersionBatch batch = reader.ReadBatch(batchAddress);
        Assert.Equal(budget, reader.ReadCacheStatistics.ResidentChargeBytes);
        Assert.Same(heads, reader.ReadDirectory(directoryAddress)); // Touching the map promotes its frame component too.
        reader.ReadMetadata(graphAddress);
        Assert.Equal(1, reader.ReadCacheStatistics.Evictions);
        Assert.Same(version, reader.ReadDirectoryVersion(directoryAddress));
        Assert.NotSame(batch, reader.ReadBatch(batchAddress));
        ObjectVersionBatch reloaded = reader.ReadBatch(batchAddress);
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
    public void Rotated_batches_with_equal_tickets_keep_full_address_cache_keys_and_reject_wrong_length() {
        string path = NewPath();
        FrameAddress first, second;
        byte[] firstBody = Enumerable.Repeat((byte)10, 256).ToArray();
        byte[] secondBody = Enumerable.Repeat((byte)20, 256).ToArray();
        using (Frames frames = Frames.Create(path, 128)) {
            first = RawBatch(frames, null, [B(1, firstBody)]);
            second = RawBatch(frames, null, [B(1, secondBody)]);
            frames.ConfirmDurable();
        }
        Span<byte> firstBytes = stackalloc byte[FrameAddress.EncodedSize];
        Span<byte> secondBytes = stackalloc byte[FrameAddress.EncodedSize];
        Assert.True(first.TryWrite(firstBytes));
        Assert.True(second.TryWrite(secondBytes));
        Assert.NotEqual(BinaryPrimitives.ReadUInt32LittleEndian(firstBytes), BinaryPrimitives.ReadUInt32LittleEndian(secondBytes));
        Assert.Equal(firstBytes[4..].ToArray(), secondBytes[4..].ToArray());
        using Frames reopened = Frames.OpenReadOnly(path);
        using GraphRecordStore reader = new(reopened);
        ObjectVersionBatch firstOwned = reader.ReadBatch(first), secondOwned = reader.ReadBatch(second);
        Assert.Equal(firstBody, Assert.Single(firstOwned.Records).Body.ToArray());
        Assert.Equal(secondBody, Assert.Single(secondOwned.Records).Body.ToArray());
        Assert.NotSame(firstOwned, secondOwned);
        Assert.Same(firstOwned, reader.ReadBatch(first));
        Assert.Same(secondOwned, reader.ReadBatch(second));
        GraphPhysicalReadStatistics before = reader.ReadCacheStatistics;
        FrameAddress wrongLength = DifferentTicket(first);
        for (int attempt = 1; attempt <= 2; attempt++) {
            Assert.Throws<InvalidOperationException>(() => reader.ReadBatch(wrongLength));
            GraphPhysicalReadStatistics rejected = reader.ReadCacheStatistics;
            Assert.Equal(before.BatchMisses + attempt, rejected.BatchMisses);
            Assert.Equal(before.BatchDecodes, rejected.BatchDecodes);
            Assert.Equal(before.EntryCount, rejected.EntryCount);
            Assert.Equal(before.ResidentChargeBytes, rejected.ResidentChargeBytes);
        }
        Assert.Same(firstOwned, reader.ReadBatch(first));
        Assert.Same(secondOwned, reader.ReadBatch(second));
        Assert.Equal(0, reader.AppendedFrames);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Combined_directory_components_over_budget_preserve_the_first_component_and_other_hot_entries(bool mapFirst) {
        using Frames frames = Frames.Create(NewPath());
        ObjectVersionRecord[] records = Enumerable.Range(1, 64).Select(id => B((uint)id, [10])).ToArray();
        FrameAddress firstBatch = RawBatch(frames, null, records);
        FrameAddress secondBatch = RawBatch(frames, null, records);
        var original = new SortedDictionary<uint, FrameAddress>(records.ToDictionary(row => row.ObjectId, _ => firstBatch));
        var changed = new SortedDictionary<uint, FrameAddress>(records.ToDictionary(row => row.ObjectId, _ => secondBatch));
        FrameAddress baseDirectory = RawDirectory(frames, null, ObjectVersionKind.Base, null, original);
        FrameAddress hotGraph = RawGraph(frames, null, baseDirectory, null);
        FrameAddress directory = mapFirst
            ? frames.Append(GraphFrameCodec.DirectoryTag, GraphFrameCodec.EncodeDirectory(hotGraph, ObjectVersionKind.Delta,
                baseDirectory, ObjectHeadDirectoryCodec.PrepareDelta(original, changed).Body)).Unwrap()
            : baseDirectory;
        long versionCharge;
        using (GraphRecordStore probe = new(frames)) { versionCharge = GraphPhysicalReadCache.GetDirectoryVersionCharge(probe.ReadDirectoryVersion(directory)); }
        long frameEntry = GraphPhysicalReadCache.EntryOverheadBytes + versionCharge;
        long mapEntry = GraphPhysicalReadCache.EntryOverheadBytes + GraphPhysicalReadCache.GetDirectoryCharge(64);
        long hotEntry = GraphPhysicalReadCache.EntryOverheadBytes + GraphPhysicalReadCache.GetGraphCharge();
        long budget = Math.Max(frameEntry, mapEntry) + hotEntry;
        Assert.True(frameEntry + GraphPhysicalReadCache.GetDirectoryCharge(64) > budget);
        using GraphRecordStore reader = new(frames, readCacheBudgetBytes: budget);
        if (mapFirst) {
            // Real ancestor reads evict the root frame; successful materialization
            // must look its address up again and admit the complete root map.
            IReadOnlyDictionary<uint, FrameAddress> heads = reader.ReadDirectory(directory);
            Assert.Equal(changed.OrderBy(static pair => pair.Key), heads.OrderBy(static pair => pair.Key));
            Assert.True(reader.ReadCacheStatistics.Evictions > 0);
            reader.ReadMetadata(hotGraph);
            GraphPhysicalReadStatistics before = reader.ReadCacheStatistics;
            ObjectHeadDirectoryVersion returned = reader.ReadDirectoryVersion(directory);
            Assert.Equal(ObjectVersionKind.Delta, returned.Kind);
            Assert.Equal(before.AdmissionBypasses + 1, reader.ReadCacheStatistics.AdmissionBypasses);
            Assert.Equal(before.Evictions, reader.ReadCacheStatistics.Evictions);
            Assert.Equal(before.ResidentChargeBytes, reader.ReadCacheStatistics.ResidentChargeBytes);
            Assert.Same(heads, reader.ReadDirectory(directory));
            reader.ReadMetadata(hotGraph);
            Assert.Equal(before.GraphDecodes, reader.ReadCacheStatistics.GraphDecodes);
        } else {
            ObjectHeadDirectoryVersion version = reader.ReadDirectoryVersion(directory);
            reader.ReadMetadata(hotGraph);
            GraphPhysicalReadStatistics before = reader.ReadCacheStatistics;
            IReadOnlyDictionary<uint, FrameAddress> heads = reader.ReadDirectory(directory);
            Assert.Equal(original.OrderBy(static pair => pair.Key), heads.OrderBy(static pair => pair.Key));
            Assert.Equal(before.AdmissionBypasses + 1, reader.ReadCacheStatistics.AdmissionBypasses);
            Assert.Same(version, reader.ReadDirectoryVersion(directory));
            Assert.NotSame(heads, reader.ReadDirectory(directory));
            Assert.Equal(before.Evictions, reader.ReadCacheStatistics.Evictions);
            Assert.Equal(before.ResidentChargeBytes, reader.ReadCacheStatistics.ResidentChargeBytes);
            reader.ReadMetadata(hotGraph);
            Assert.Equal(before.GraphDecodes, reader.ReadCacheStatistics.GraphDecodes);
        }
        Assert.Equal(2, reader.ReadCacheStatistics.EntryCount);
        Assert.InRange(reader.ReadCacheStatistics.PeakResidentChargeBytes, 1, budget);
        Assert.Equal(0, reader.AppendedFrames);
    }

    [Fact]
    public void ZeroBudgetRepeatsProductionAndNegativeBudgetsRejectBeforeBackingUse() {
        using Frames frames = Frames.Create(NewPath());
        int ownerChecks = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphRecordStore(frames, () => ownerChecks++, readCacheBudgetBytes: -1));
        Assert.Equal(0, ownerChecks);
        string repositoryPath = NewPath();
        Assert.Throws<ArgumentOutOfRangeException>(() => Repository.CreateStorageNew(repositoryPath, new(), readCacheBudgetBytes: -1, schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared));
        Assert.False(Directory.Exists(repositoryPath));
        FrameAddress batch = RawBatch(frames, null, [B(1, [10])]);
        FrameAddress directory = RawDirectory(frames, null, ObjectVersionKind.Base, null, Map((1u, batch)));
        using GraphRecordStore reader = new(frames, readCacheBudgetBytes: 0);
        Assert.NotSame(reader.ReadBatch(batch), reader.ReadBatch(batch));
        Assert.NotSame(reader.ReadDirectory(directory), reader.ReadDirectory(directory));
        GraphPhysicalReadStatistics stats = reader.ReadCacheStatistics;
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
    [InlineData("graph", false)]
    [InlineData("batch", false)]
    [InlineData("directory", false)]
    [InlineData("graph", true)]
    [InlineData("batch", true)]
    [InlineData("directory", true)]
    public void FreshMissingAndCrcDamagedFrameReadsRetryWithoutAdmissionAndKeepHealthyEntries(string role, bool corruptPayload) {
        string path = NewPath();
        FrameAddress rejected, healthyBatch;
        using (Frames frames = Frames.Create(path)) {
            using GraphRecordStore writer = new(frames);
            StoredGraphRevision first = writer.Append(StoredGraphKind.State, null, null, new(null, new(1), [B(1, [10])]));
            rejected = role switch {
                "graph" => first.Address,
                "batch" => first.OwnedBatch!.Value,
                "directory" => first.DirectoryRoot,
                _ => throw new InvalidOperationException(role),
            };
            // A later independent complete graph keeps normal EOF admission healthy.
            StoredGraphRevision second = writer.Append(StoredGraphKind.State, null, null, new(null, new(1), [B(1, [42])]));
            healthyBatch = second.OwnedBatch!.Value;
            frames.ConfirmDurable();
        }
        string file = Assert.Single(Directory.GetFiles(path, "*.rbf", SearchOption.AllDirectories));
        byte[] bytes = File.ReadAllBytes(file);
        if (corruptPayload) {
            Span<byte> encoded = stackalloc byte[FrameAddress.EncodedSize];
            Assert.True(rejected.TryWrite(encoded));
            long offset = SizedPtr.FromPacked(BinaryPrimitives.ReadUInt64LittleEndian(encoded[sizeof(uint)..])).Offset;
            // Hostile offline fixture only: all writers, readers and caches have closed.
            // The first payload byte follows HeadLen; CRC and framing remain untouched.
            bytes[checked((int)offset + sizeof(uint))] ^= 1;
            File.WriteAllBytes(file, bytes);
        } else { rejected = TestAddress(uint.MaxValue); } // Canonical numeric address, genuinely absent file.
        using (Frames reopened = Frames.OpenReadOnly(path)) {
            using GraphRecordStore fresh = new(reopened);
            Action readRejected = role switch {
                "graph" => () => fresh.ReadMetadata(rejected),
                "batch" => () => fresh.ReadBatch(rejected),
                "directory" => () => fresh.ReadDirectoryVersion(rejected),
                _ => throw new InvalidOperationException(role),
            };
            for (int attempt = 1; attempt <= 2; attempt++) {
                AssertRejected();
                GraphPhysicalReadStatistics failed = fresh.ReadCacheStatistics;
                Assert.Equal(attempt, RoleMisses(failed, role));
                Assert.Equal(0, failed.GraphHits + failed.BatchHits + failed.DirectoryVersionHits);
                Assert.Equal(0, failed.GraphDecodes + failed.BatchDecodes + failed.DirectoryVersionDecodes);
                Assert.Equal(0, failed.DirectoryMaterializations);
                Assert.Equal(0, failed.EntryCount);
                Assert.Equal(0, failed.ResidentChargeBytes);
                Assert.Equal(0, failed.PeakResidentChargeBytes);
                Assert.Equal(0, failed.AdmissionBypasses); // Neither positive nor negative entry was offered.
            }
            ObjectVersionBatch healthy = fresh.ReadBatch(healthyBatch);
            Assert.Equal(new byte[] { 42 }, Assert.Single(healthy.Records).Body.ToArray());
            Assert.Same(healthy, fresh.ReadBatch(healthyBatch));
            GraphPhysicalReadStatistics positive = fresh.ReadCacheStatistics;
            Assert.Equal(1, positive.BatchDecodes);
            Assert.Equal(1, positive.BatchHits);
            Assert.Equal(1, positive.EntryCount);
            Assert.True(positive.ResidentChargeBytes > 0);
            AssertRejected();
            GraphPhysicalReadStatistics retried = fresh.ReadCacheStatistics;
            Assert.Equal(RoleMisses(positive, role) + 1, RoleMisses(retried, role));
            AssertPhysicalProductionEqual(positive, retried);
            Assert.Equal(positive.EntryCount, retried.EntryCount);
            Assert.Equal(positive.ResidentChargeBytes, retried.ResidentChargeBytes);
            Assert.Equal(positive.PeakResidentChargeBytes, retried.PeakResidentChargeBytes);
            Assert.Equal(positive.Evictions, retried.Evictions);
            Assert.Same(healthy, fresh.ReadBatch(healthyBatch));
            Assert.Equal(0, fresh.AppendedFrames);

            void AssertRejected() {
                if (corruptPayload) {
                    InvalidOperationException error = Assert.Throws<InvalidOperationException>(readRejected);
                    Assert.Contains("CRC", error.Message, StringComparison.OrdinalIgnoreCase);
                } else { Assert.Throws<FileNotFoundException>(readRejected); }
            }
        }
        Assert.Equal(bytes, File.ReadAllBytes(file)); // No repair or write was performed during any read.
    }

    [Theory]
    [InlineData("owned")]
    [InlineData("creator")]
    public void WarmValidDirectoryCannotQualifyAnInvalidGraphRelationship(string error) {
        using Frames frames = Frames.Create(NewPath());
        using GraphRecordStore reader = new(frames);
        StoredGraphRevision first = reader.Append(StoredGraphKind.State, null, null, new(null, new(1), [B(1, [10])]));
        StoredGraphRevision second = reader.Append(StoredGraphKind.State, first.Address, null, new(first.Address, new(1), [B(1, [20])]));
        FrameAddress directory, owned, parent;
        if (error == "owned") {
            directory = first.DirectoryRoot;
            owned = RawBatch(frames, first.Address, [B(1, [30])]);
            parent = first.Address;
        } else {
            directory = RawDirectory(frames, first.Address, ObjectVersionKind.Base, null, second.ObjectHeads);
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
        GraphPhysicalReadStatistics before = reader.ReadCacheStatistics;
        Assert.Throws<InvalidDataException>(() => reader.ReadGraph(bad));
        Assert.Throws<InvalidDataException>(() => reader.ReadGraph(bad));
        AssertPhysicalProductionEqual(before, reader.ReadCacheStatistics);
        Assert.True(reader.ReadCacheStatistics.GraphHits > before.GraphHits);
        Assert.True(reader.ReadCacheStatistics.ResidentChargeBytes > 0); // Ordinary semantic rejection retains correct raw/map values.
        if (error == "creator") {
            FrameAddress positive = RawGraph(frames, first.Address, directory, owned);
            Assert.Equal(second.ObjectHeads[1], reader.ReadGraph(positive).ObjectHeads[1]);
        } else { Assert.Equal(first.ObjectHeads[1], reader.ReadGraph(first.Address).ObjectHeads[1]); }
    }

    [Fact]
    public void WarmBadPriorAndFailedDirectoryReplayStayRejectedWhileNewBaseCutsBadContent() {
        using Frames frames = Frames.Create(NewPath());
        using GraphRecordStore reader = new(frames);
        StoredGraphRevision first = reader.Append(StoredGraphKind.State, null, null, new(null, new(1), [B(1, [10])]));
        StoredGraphRevision second = reader.Append(StoredGraphKind.State, first.Address, null, new(first.Address, new(1), [D(1, first.ObjectHeads[1], [20])]));
        FrameAddress wrongBatch = RawBatch(frames, second.Address, [D(1, first.ObjectHeads[1], [30])]);
        FrameAddress wrongDirectory = RawDirectory(frames, second.Address, ObjectVersionKind.Base, null, Map((1u, wrongBatch)));
        FrameAddress retained = RawGraph(frames, second.Address, wrongDirectory, null);
        Assert.Equal(wrongBatch, reader.ReadGraph(retained).ObjectHeads[1]); // This external-head declaration is intentionally shallow.
        reader.ReadDirectory(second.DirectoryRoot);
        reader.ReadBatch(wrongBatch);
        GraphPhysicalReadStatistics before = reader.ReadCacheStatistics;
        Assert.Throws<InvalidDataException>(() => reader.ReadObjectChain(retained, 1));
        Assert.Throws<InvalidDataException>(() => reader.ReadObjectChain(retained, 1));
        AssertPhysicalProductionEqual(before, reader.ReadCacheStatistics);
        StoredGraphRevision reset = reader.Append(StoredGraphKind.State, retained, null, new(retained, new(1), [B(1, [])]));
        ObjectVersionChain resetChain = reader.ReadObjectChain(reset.Address, 1);
        Assert.Single(resetChain.Entries);
        Assert.Equal(reset.OwnedBatch, resetChain.Head);
        Assert.Equal(ObjectVersionPayloadSize.GetBasePayloadBytes(0), resetChain.ReconstructionPayloadBytes);

        FrameAddress malformed = frames.Append(GraphFrameCodec.DirectoryTag,
            GraphFrameCodec.EncodeDirectory(second.Address, ObjectVersionKind.Delta, second.DirectoryRoot, [])).Unwrap();
        reader.ReadDirectoryVersion(malformed); // Valid owned frame envelope; invalid Dictionary Delta body.
        Assert.Throws<EndOfStreamException>(() => reader.ReadDirectory(malformed));
        GraphPhysicalReadStatistics failed = reader.ReadCacheStatistics;
        Assert.Throws<EndOfStreamException>(() => reader.ReadDirectory(malformed));
        Assert.Equal(failed.DirectoryHits, reader.ReadCacheStatistics.DirectoryHits);
        Assert.Equal(failed.DirectoryMaterializations, reader.ReadCacheStatistics.DirectoryMaterializations);
        Assert.Equal(failed.DirectoryVersionDecodes, reader.ReadCacheStatistics.DirectoryVersionDecodes);
        Assert.True(reader.ReadCacheStatistics.DirectoryReplayFrames > failed.DirectoryReplayFrames);
        Assert.True(reader.ReadCacheStatistics.DirectoryVersionHits > failed.DirectoryVersionHits);
        Assert.Equal(second.ObjectHeads[1], reader.ReadDirectory(second.DirectoryRoot)[1]);
    }

    [Fact]
    public void OwnedBuffersAndMapsCannotBePollutedAndEqualAddressesRemainLocalToEachStore() {
        using Frames firstFrames = Frames.Create(NewPath());
        using Frames secondFrames = Frames.Create(NewPath());
        FrameAddress firstAddress = RawBatch(firstFrames, null, [B(1, [10])]);
        FrameAddress secondAddress = RawBatch(secondFrames, null, [B(1, [20])]);
        Assert.Equal(firstAddress, secondAddress);
        using GraphRecordStore first = new(firstFrames), second = new(secondFrames);
        ObjectVersionBatch firstBatch = first.ReadBatch(firstAddress), secondBatch = second.ReadBatch(secondAddress);
        Assert.NotSame(firstBatch, secondBatch);
        Assert.Equal(new byte[] { 10 }, firstBatch.Records[0].Body.ToArray());
        Assert.Equal(new byte[] { 20 }, secondBatch.Records[0].Body.ToArray());
        byte[] batchCopy = firstBatch.Records[0].Body.ToArray();
        batchCopy[0] = 99;
        Assert.Equal(new byte[] { 10 }, first.ReadBatch(firstAddress).Records[0].Body.ToArray());
        Assert.False(firstBatch.Records is ObjectVersionRecord[]);
        Assert.False(firstBatch.Records is IList<ObjectVersionRecord>);
        byte[] input = [1, 2, 3];
        ObjectHeadDirectoryVersion standalone = new(null, ObjectVersionKind.Base, null, input, checked((int)ObjectVersionPayloadSize.GetBasePayloadBytes(input.Length)));
        input[0] = 99;
        Assert.Equal(new byte[] { 1, 2, 3 }, standalone.Body.ToArray()); // Constructor must own the original mutable input.
        FrameAddress directory = RawDirectory(firstFrames, null, ObjectVersionKind.Base, null, Map((1u, firstAddress)));
        ObjectHeadDirectoryVersion version = first.ReadDirectoryVersion(directory);
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
        using RepositoryResources resources = RepositoryResources.CreateStorageNew(NewPath());
        using RepositoryStorageFacade facade = new(resources);
        using (PendingRepositorySave pending = facade.Prepare("main", StoredGraphKind.State,
            new(null, new(1), [B(1, [10])]), new(null, new(1), [B(1, [0])]))) { facade.Commit(pending); }
        GraphRecordStore retained = facade.Store;
        FrameAddress address = facade.GetHead("main")!.Value;
        StoredGraphRevision graph = retained.ReadGraph(address);
        ObjectVersionBatch batch = retained.ReadBatch(graph.OwnedBatch!.Value);
        ObjectHeadDirectoryVersion directory = retained.ReadDirectoryVersion(graph.DirectoryRoot);
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
        Assert.Equal(graph.ObjectHeads[1], heads[1]);
        Assert.Equal(new byte[] { 10 }, batch.Records[0].Body.ToArray());
        Assert.NotEmpty(directory.Body.ToArray());
        if (stop == "StoreDispose") {
            using FrameRead liveBacking = resources.Data.ReadFrame(graph.OwnedBatch.Value).Unwrap();
            Assert.Equal(GraphFrameCodec.BatchTag, liveBacking.Tag); // Disposing a borrowed Store does not close its backing owner.
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
        frames.Append(GraphFrameCodec.BatchTag, GraphFrameCodec.EncodeBatch(source, null, records)).Unwrap();
    private static FrameAddress RawDirectory(Frames frames, FrameAddress? source, ObjectVersionKind kind, FrameAddress? prior,
        IReadOnlyDictionary<uint, FrameAddress> heads) => frames.Append(GraphFrameCodec.DirectoryTag,
        GraphFrameCodec.EncodeDirectory(source, kind, prior, ObjectHeadDirectoryCodec.PrepareBase(heads).Body)).Unwrap();
    private static FrameAddress RawGraph(Frames frames, FrameAddress? parent, FrameAddress directory, FrameAddress? batch) =>
        frames.Append(GraphFrameCodec.GraphTag, GraphFrameCodec.EncodeGraph(StoredGraphKind.State, parent, new(1), directory, null, batch)).Unwrap();
    private static void AssertPhysicalProductionEqual(GraphPhysicalReadStatistics expected, GraphPhysicalReadStatistics actual) {
        Assert.Equal(expected.GraphDecodes, actual.GraphDecodes);
        Assert.Equal(expected.BatchDecodes, actual.BatchDecodes);
        Assert.Equal(expected.DirectoryVersionDecodes, actual.DirectoryVersionDecodes);
        Assert.Equal(expected.DirectoryMaterializations, actual.DirectoryMaterializations);
        Assert.Equal(expected.DirectoryReplayFrames, actual.DirectoryReplayFrames);
    }
    private static long RoleMisses(GraphPhysicalReadStatistics statistics, string role) => role switch {
        "graph" => statistics.GraphMisses,
        "batch" => statistics.BatchMisses,
        "directory" => statistics.DirectoryVersionMisses,
        _ => throw new InvalidOperationException(role),
    };
    public void Dispose() {
        foreach (string path in _paths) { SharedReadModel.DeleteFixture(path, Prefix); }
    }
}
