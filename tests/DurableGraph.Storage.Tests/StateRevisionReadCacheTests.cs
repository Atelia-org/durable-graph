using System.Buffers;
using Atelia.Data;
using Atelia.RbfSegmentStore;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace Atelia.DurableGraph.Storage.Tests;

public sealed class StateRevisionReadCacheTests : IDisposable {
    private readonly string _temporaryRoot = Path.GetFullPath(Path.GetTempPath());
    private readonly List<string> _paths = [];

    [Fact]
    public void Fitting_frame_and_map_are_reused_and_Append_does_not_seed_unmeasured_records() {
        using SegmentStore segments = SegmentStore.CreateNew(NewPath());
        using StateRevisionStore store = new(segments);
        StateRevision input = Base(7, [10, 20, 30]);
        FrameAddress address = store.AppendDurably(input);
        Assert.Null(input.LocalObjects[0].EncodedPayloadBytes);
        Assert.Equal(0, store.ReadCacheStatistics.EntryCount);

        StateRevision decoded = store.Read(address);
        Assert.NotSame(input, decoded);
        Assert.Equal(5, decoded.LocalObjects[0].EncodedPayloadBytes);
        Assert.Same(decoded, store.Read(address));
        IReadOnlyDictionary<uint, FrameAddress> heads = store.ReadLiveObjectHeadMap(address);
        Assert.Same(heads, store.ReadLiveObjectHeadMap(address));
        Assert.Equal(address, heads[7]);
        Assert.Equal(5L, store.ReadObjectVersionChain(address, 7).ReconstructionPayloadBytes);
        Assert.Equal(1L, store.ReadCacheStatistics.RevisionDecodes);
        Assert.Equal(1L, store.ReadCacheStatistics.MapMaterializations);
        Assert.Equal(1, store.ReadCacheStatistics.EntryCount);
        Assert.Equal(EntryCharge(decoded) + StateRevisionReadCache.GetMapCharge(1),
            store.ReadCacheStatistics.ResidentChargeBytes);
    }

    [Fact]
    public void Zero_budget_repeats_production_without_residency_and_negative_budget_is_rejected() {
        using SegmentStore segments = SegmentStore.CreateNew(NewPath());
        Assert.Throws<ArgumentOutOfRangeException>(() => new StateRevisionStore(segments, -1));
        using StateRevisionStore store = new(segments, 0);
        FrameAddress address = store.Append(Base(1, [1]));
        Assert.NotSame(store.Read(address), store.Read(address));
        Assert.NotSame(store.ReadLiveObjectHeadMap(address), store.ReadLiveObjectHeadMap(address));
        Assert.Equal(4L, store.ReadCacheStatistics.RevisionMisses);
        Assert.Equal(4L, store.ReadCacheStatistics.RevisionDecodes);
        Assert.Equal(2L, store.ReadCacheStatistics.MapMisses);
        Assert.Equal(2L, store.ReadCacheStatistics.MapMaterializations);
        Assert.Equal(0L, store.ReadCacheStatistics.RevisionHits);
        Assert.Equal(0L, store.ReadCacheStatistics.MapHits);
        Assert.Equal(0L, store.ReadCacheStatistics.ResidentChargeBytes);
        Assert.Equal(0, store.ReadCacheStatistics.EntryCount);
    }

    [Fact]
    public void Frame_hits_promote_the_whole_entry_before_LRU_eviction() {
        using SegmentStore segments = SegmentStore.CreateNew(NewPath());
        StateRevision revision = Base(1, [1]);
        long budget = 2 * EntryCharge(revision);
        using StateRevisionStore store = new(segments, budget);
        FrameAddress a = store.Append(revision);
        FrameAddress b = store.Append(revision);
        FrameAddress c = store.Append(revision);
        StateRevision retainedA = store.Read(a);
        StateRevision retainedB = store.Read(b);
        Assert.Same(retainedA, store.Read(a));
        _ = store.Read(c);
        Assert.Equal(1L, store.ReadCacheStatistics.Evictions);
        Assert.Same(retainedA, store.Read(a));
        Assert.NotSame(retainedB, store.Read(b));
        Assert.Equal(4L, store.ReadCacheStatistics.RevisionDecodes);
        Assert.Equal(2L, store.ReadCacheStatistics.Evictions);
        Assert.Equal(budget, store.ReadCacheStatistics.PeakResidentChargeBytes);
    }

    [Fact]
    public void Map_hits_promote_the_whole_entry_and_retained_values_survive_eviction() {
        using SegmentStore segments = SegmentStore.CreateNew(NewPath());
        StateRevision revision = Base(1, [9]);
        long budget = 2 * (EntryCharge(revision) + StateRevisionReadCache.GetMapCharge(1));
        using StateRevisionStore store = new(segments, budget);
        FrameAddress a = store.Append(revision);
        FrameAddress b = store.Append(revision);
        FrameAddress c = store.Append(revision);
        IReadOnlyDictionary<uint, FrameAddress> mapA = store.ReadLiveObjectHeadMap(a);
        IReadOnlyDictionary<uint, FrameAddress> mapB = store.ReadLiveObjectHeadMap(b);
        StateRevision rawA = store.Read(a);
        StateRevision rawB = store.Read(b);
        Assert.Same(mapA, store.ReadLiveObjectHeadMap(a));
        _ = store.ReadLiveObjectHeadMap(c);
        Assert.Same(rawA, store.Read(a));
        Assert.Equal(1L, store.ReadCacheStatistics.Evictions);
        Assert.Equal(b, mapB[1]);
        Assert.Equal(new byte[] { 9 }, rawB.LocalObjects[0].Body.ToArray());
        Assert.NotSame(rawB, store.Read(b));
        Assert.InRange(store.ReadCacheStatistics.PeakResidentChargeBytes, 1, budget);
    }

    [Fact]
    public void Oversized_raw_frame_bypasses_without_evicting_hot_entry_and_can_retain_only_its_map() {
        using SegmentStore segments = SegmentStore.CreateNew(NewPath());
        StateRevision small = Base(1, [1]);
        long mapEntryCharge = StateRevisionReadCache.EntryOverheadBytes + StateRevisionReadCache.GetMapCharge(1);
        long budget = EntryCharge(small) + mapEntryCharge;
        using StateRevisionStore store = new(segments, budget);
        FrameAddress hot = store.Append(small);
        FrameAddress huge = store.Append(Base(2, new byte[16 * 1024]));
        StateRevision retained = store.Read(hot);
        _ = store.Read(huge);
        Assert.Same(retained, store.Read(hot));
        Assert.Equal(0L, store.ReadCacheStatistics.Evictions);
        IReadOnlyDictionary<uint, FrameAddress> map = store.ReadLiveObjectHeadMap(huge);
        Assert.Same(map, store.ReadLiveObjectHeadMap(huge));
        Assert.Same(retained, store.Read(hot));
        Assert.Equal(2, store.ReadCacheStatistics.EntryCount);
        Assert.Equal(budget, store.ReadCacheStatistics.ResidentChargeBytes);
        Assert.Equal(2L, store.ReadCacheStatistics.AdmissionBypasses);
        _ = store.Read(huge);
        Assert.Same(map, store.ReadLiveObjectHeadMap(huge));
        Assert.Equal(0L, store.ReadCacheStatistics.Evictions);
    }

    [Fact]
    public void Combined_oversize_keeps_existing_frame_and_does_not_sweep_other_hot_entries() {
        using SegmentStore segments = SegmentStore.CreateNew(NewPath());
        StateRevision large = StateRevision.CreateObjectHeadMapBase(null,
            Enumerable.Range(1, 128).Select(id => ObjectVersionRecord.CreateBase((uint)id, [1])), []);
        StateRevision small = Base(200, [2]);
        long budget = EntryCharge(large) + EntryCharge(small);
        Assert.True(StateRevisionReadCache.GetMapCharge(128) > EntryCharge(small));
        Assert.True(StateRevisionReadCache.EntryOverheadBytes + StateRevisionReadCache.GetMapCharge(128) <= budget);
        using StateRevisionStore store = new(segments, budget);
        FrameAddress a = store.Append(large);
        FrameAddress b = store.Append(small);
        StateRevision rawA = store.Read(a);
        StateRevision rawB = store.Read(b);
        IReadOnlyDictionary<uint, FrameAddress> firstMap = store.ReadLiveObjectHeadMap(a);
        Assert.Equal(128, firstMap.Count);
        Assert.Same(rawA, store.Read(a));
        Assert.Same(rawB, store.Read(b));
        Assert.NotSame(firstMap, store.ReadLiveObjectHeadMap(a));
        Assert.Equal(2L, store.ReadCacheStatistics.AdmissionBypasses);
        Assert.Equal(0L, store.ReadCacheStatistics.Evictions);
        Assert.Equal(budget, store.ReadCacheStatistics.ResidentChargeBytes);
    }

    [Fact]
    public void Combined_oversize_keeps_existing_map_and_other_hot_entries_when_raw_arrives_later() {
        using SegmentStore segments = SegmentStore.CreateNew(NewPath());
        ObjectVersionRecord[] records = Enumerable.Range(1, 128)
            .Select(id => ObjectVersionRecord.CreateBase((uint)id, [1])).ToArray();
        StateRevision original = StateRevision.CreateObjectHeadMapBase(null, records, []);
        FrameAddress root;
        StateRevision rootRevision;
        using (StateRevisionStore writer = new(segments, 0)) {
            FrameAddress parent = writer.Append(original);
            rootRevision = StateRevision.CreateObjectHeadMapDelta(parent, records, []);
            root = writer.Append(rootRevision);
        }
        StateRevision small = Base(200, [2]);
        long budget = EntryCharge(rootRevision) + EntryCharge(small);
        Assert.True(StateRevisionReadCache.GetMapCharge(128) > EntryCharge(small));
        using StateRevisionStore store = new(segments, budget);
        // Walking to the Base evicts the root frame; admitting the root map then evicts the Base.
        IReadOnlyDictionary<uint, FrameAddress> map = store.ReadLiveObjectHeadMap(root);
        Assert.Equal(2L, store.ReadCacheStatistics.Evictions);
        FrameAddress hot = store.Append(small);
        StateRevision retained = store.Read(hot);
        Assert.Equal(2, store.ReadCacheStatistics.EntryCount);
        _ = store.Read(root);
        Assert.Same(map, store.ReadLiveObjectHeadMap(root));
        Assert.Same(retained, store.Read(hot));
        Assert.Equal(1L, store.ReadCacheStatistics.AdmissionBypasses);
        Assert.Equal(2L, store.ReadCacheStatistics.Evictions);
        Assert.InRange(store.ReadCacheStatistics.ResidentChargeBytes, 1, budget);
    }

    [Fact]
    public void Empty_values_have_positive_charge_and_tiny_budget_bypasses_every_admission() {
        using SegmentStore segments = SegmentStore.CreateNew(NewPath());
        StateRevision empty = StateRevision.CreateObjectHeadMapBase(null, [], []);
        Assert.True(StateRevisionReadCache.EntryOverheadBytes > 0);
        Assert.True(StateRevisionReadCache.GetRevisionCharge(empty) > 0);
        Assert.True(StateRevisionReadCache.GetMapCharge(0) > 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => StateRevisionReadCache.GetMapCharge(-1));
        Assert.True(StateRevisionReadCache.GetMapCharge(int.MaxValue) > int.MaxValue);
        using StateRevisionStore store = new(segments, 1);
        FrameAddress address = store.Append(empty);
        Assert.Empty(store.ReadLiveObjectHeadMap(address));
        Assert.Empty(store.ReadLiveObjectHeadMap(address));
        Assert.Equal(2L, store.ReadCacheStatistics.RevisionDecodes);
        Assert.Equal(2L, store.ReadCacheStatistics.MapMaterializations);
        Assert.Equal(4L, store.ReadCacheStatistics.AdmissionBypasses);
        Assert.Equal(0L, store.ReadCacheStatistics.ResidentChargeBytes);
        Assert.Equal(0L, store.ReadCacheStatistics.PeakResidentChargeBytes);
        Assert.Equal(0, store.ReadCacheStatistics.EntryCount);
    }

    [Fact]
    public void Materialization_readmits_root_by_address_after_ancestor_reads_evict_its_frame() {
        using SegmentStore segments = SegmentStore.CreateNew(NewPath());
        StateRevision baseRevision = Base(1, [1]);
        FrameAddress root;
        using (StateRevisionStore writer = new(segments, 0)) {
            root = writer.Append(baseRevision);
            for (int i = 0; i < 5; i++) {
                root = writer.Append(StateRevision.CreateObjectHeadMapDelta(root, [], []));
            }
        }
        long budget = EntryCharge(baseRevision);
        using StateRevisionStore store = new(segments, budget);
        IReadOnlyDictionary<uint, FrameAddress> map = store.ReadLiveObjectHeadMap(root);
        Assert.Single(map);
        Assert.Equal(6L, store.ReadCacheStatistics.RevisionDecodes);
        Assert.True(store.ReadCacheStatistics.Evictions >= 5);
        Assert.Same(map, store.ReadLiveObjectHeadMap(root));
        Assert.Equal(1L, store.ReadCacheStatistics.MapMaterializations);
        Assert.InRange(store.ReadCacheStatistics.PeakResidentChargeBytes, 1, budget);
    }

    [Fact]
    public void Full_address_key_distinguishes_file_offset_and_length() {
        RbfSegmentStoreOptions options = new() { SegmentSizeThresholdBytes = 8 };
        using SegmentStore segments = SegmentStore.CreateNew(NewPath(), options);
        using StateRevisionStore store = new(segments);
        FrameAddress first = store.Append(Base(1, [10]));
        FrameAddress second = store.Append(Base(1, [20]));
        Assert.NotEqual(first.FileNumber, second.FileNumber);
        Assert.Equal(first.FrameTicket, second.FrameTicket);
        Assert.Equal(new byte[] { 10 }, store.Read(first).LocalObjects[0].Body.ToArray());
        Assert.Equal(new byte[] { 20 }, store.Read(second).LocalObjects[0].Body.ToArray());
        FrameAddress wrongLength = new(first.FileNumber,
            SizedPtr.Create(first.FrameTicket.Offset, first.FrameTicket.Length + 4));
        Assert.Throws<InvalidOperationException>(() => store.Read(wrongLength));
        Assert.Equal(3L, store.ReadCacheStatistics.RevisionMisses);
        Assert.Equal(2L, store.ReadCacheStatistics.RevisionDecodes);
        Assert.Equal(2, store.ReadCacheStatistics.EntryCount);
        // Same-file offsets are exercised by every non-rollover eviction fixture.
    }

    [Fact]
    public void Independent_stores_with_the_same_address_never_share_cached_values() {
        using SegmentStore firstSegments = SegmentStore.CreateNew(NewPath());
        using SegmentStore secondSegments = SegmentStore.CreateNew(NewPath());
        using StateRevisionStore first = new(firstSegments);
        using StateRevisionStore second = new(secondSegments);
        FrameAddress firstAddress = first.Append(Base(1, [10]));
        FrameAddress secondAddress = second.Append(Base(1, [20]));
        Assert.Equal(firstAddress, secondAddress);
        Assert.Equal(new byte[] { 10 }, first.Read(firstAddress).LocalObjects[0].Body.ToArray());
        Assert.Equal(new byte[] { 20 }, second.Read(secondAddress).LocalObjects[0].Body.ToArray());
        Assert.Equal(1L, first.ReadCacheStatistics.RevisionDecodes);
        Assert.Equal(1L, second.ReadCacheStatistics.RevisionDecodes);
    }

    [Theory]
    [InlineData("tag")]
    [InlineData("tailMeta")]
    [InlineData("wire")]
    public void Failed_raw_decode_is_not_admitted_or_negative_cached(string failure) {
        using SegmentStore segments = SegmentStore.CreateNew(NewPath());
        FrameAddress address;
        using (RbfSegmentWriterLease writer = segments.OpenActiveWriter()) {
            ArrayBufferWriter<byte> payload = new();
            StateRevisionWireWriter.Write(payload, Base(1, [1]), new FileScope(writer.SegmentNumber));
            SizedPtr ticket = failure switch {
                "tag" => writer.File.Append(0x01020304, payload.WrittenSpan).Unwrap(),
                "tailMeta" => writer.File.Append(StateRevisionWireFormat.RbfTag, payload.WrittenSpan, tailMeta: [1]).Unwrap(),
                _ => writer.File.Append(StateRevisionWireFormat.RbfTag, [255]).Unwrap(),
            };
            address = new(writer.SegmentNumber, ticket);
        }
        using StateRevisionStore store = new(segments);
        Assert.Throws<InvalidDataException>(() => store.Read(address));
        Assert.Throws<InvalidDataException>(() => store.Read(address));
        Assert.Equal(2L, store.ReadCacheStatistics.RevisionMisses);
        Assert.Equal(0L, store.ReadCacheStatistics.RevisionDecodes);
        Assert.Equal(0, store.ReadCacheStatistics.EntryCount);
    }

    [Fact]
    public void Offline_corrupted_payload_fails_CRC_on_every_fresh_store_read_without_negative_cache() {
        string path = NewPath();
        RbfSegmentStoreOptions options = new() {
            NewStoreLayout = RbfSegmentStoreLayout.Flat,
        };
        FrameAddress address;
        using (SegmentStore segments = SegmentStore.CreateNew(path, options)) {
            using StateRevisionStore store = new(segments);
            address = store.AppendDurably(Base(1, [10]));
            // Open-time tail validation fully checks only the last frame; append a
            // successor so the damaged frame stays historical and per-read CRC
            // failures remain observable.
            _ = store.AppendDurably(Base(2, [20]));
        }
        string file = Assert.Single(Directory.GetFiles(path, "*.rbf", SearchOption.AllDirectories));
        byte[] bytes = File.ReadAllBytes(file);
        // RBF framing starts with a four-byte HeadLen. Change payload only, leaving CRC untouched.
        bytes[checked((int)address.FrameTicket.Offset + sizeof(uint))] ^= 1;
        File.WriteAllBytes(file, bytes);
        using SegmentStore reopened = SegmentStore.OpenExisting(path, options);
        using StateRevisionStore fresh = new(reopened);
        for (int i = 0; i < 2; i++) {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => fresh.Read(address));
            Assert.Contains("CRC", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(2L, fresh.ReadCacheStatistics.RevisionMisses);
        Assert.Equal(0L, fresh.ReadCacheStatistics.RevisionDecodes);
        Assert.Equal(0, fresh.ReadCacheStatistics.EntryCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_map_retries_validation_but_keeps_successfully_decoded_raw_frame(bool badExternal) {
        using SegmentStore segments = SegmentStore.CreateNew(NewPath());
        FrameAddress address;
        using (RbfSegmentWriterLease writer = segments.OpenActiveWriter()) {
            FrameAddress forward = new(writer.SegmentNumber, SizedPtr.Create(writer.File.TailOffset + 4, 4));
            StateRevision revision = badExternal
                ? StateRevision.CreateObjectHeadMapBase(null, [], [new(1, forward)])
                : StateRevision.CreateObjectHeadMapDelta(forward, [], []);
            address = RawAppend(writer, revision);
        }
        using StateRevisionStore store = new(segments);
        Assert.Throws<InvalidDataException>(() => store.ReadLiveObjectHeadMap(address));
        Assert.Throws<InvalidDataException>(() => store.ReadLiveObjectHeadMap(address));
        Assert.Equal(1L, store.ReadCacheStatistics.RevisionDecodes);
        Assert.Equal(1L, store.ReadCacheStatistics.RevisionHits);
        Assert.Equal(2L, store.ReadCacheStatistics.MapMisses);
        Assert.Equal(0L, store.ReadCacheStatistics.MapMaterializations);
        Assert.Equal(1, store.ReadCacheStatistics.EntryCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cached_raw_and_map_still_reject_wrong_prior_and_branch_while_new_Base_resets_content_chain(bool otherBranch) {
        using SegmentStore segments = SegmentStore.CreateNew(NewPath());
        using StateRevisionStore store = new(segments);
        FrameAddress original = store.Append(Base(1, [1]));
        FrameAddress updated = store.Append(StateRevision.CreateObjectHeadMapDelta(original,
            [ObjectVersionRecord.CreateDelta(1, original, [2])], []));
        FrameAddress wrong = otherBranch ? store.Append(Base(1, [3])) : original;
        FrameAddress corrupt;
        using (RbfSegmentWriterLease writer = segments.OpenActiveWriter()) {
            corrupt = RawAppend(writer, StateRevision.CreateObjectHeadMapDelta(updated,
                [ObjectVersionRecord.CreateDelta(1, wrong, [4])], []));
        }
        _ = store.Read(corrupt);
        _ = store.ReadLiveObjectHeadMap(corrupt);
        Assert.Throws<InvalidDataException>(() => store.ReadObjectVersionChain(corrupt, 1));
        long decodes = store.ReadCacheStatistics.RevisionDecodes;
        long maps = store.ReadCacheStatistics.MapMaterializations;
        Assert.Throws<InvalidDataException>(() => store.ReadObjectVersionChain(corrupt, 1));
        Assert.Equal(decodes, store.ReadCacheStatistics.RevisionDecodes);
        Assert.Equal(maps, store.ReadCacheStatistics.MapMaterializations);
        FrameAddress reset = store.Append(StateRevision.CreateObjectHeadMapDelta(corrupt,
            [ObjectVersionRecord.CreateBase(1, [])], []));
        ObjectVersionChain chain = store.ReadObjectVersionChain(reset, 1);
        Assert.Equal(reset, Assert.Single(chain.Records).ContainingRevisionAddress);
        Assert.Equal(2L, chain.ReconstructionPayloadBytes);
    }

    [Fact]
    public void Cached_shallow_map_cannot_turn_an_inherited_id_into_a_direct_local_record() {
        using SegmentStore segments = SegmentStore.CreateNew(NewPath());
        using StateRevisionStore store = new(segments);
        FrameAddress original = store.Append(Base(1, [1]));
        FrameAddress inherited = store.Append(StateRevision.CreateObjectHeadMapDelta(original, [], []));
        FrameAddress invalid = store.Append(StateRevision.CreateObjectHeadMapBase(inherited, [], [new(1, inherited)]));
        Assert.Equal(inherited, store.ReadLiveObjectHeadMap(invalid)[1]);
        _ = store.Read(inherited);
        for (int i = 0; i < 2; i++) {
            Assert.Throws<InvalidDataException>(() => store.ReadObjectBaseBody(invalid, 1));
            Assert.Throws<InvalidDataException>(() => store.ReadObjectVersionChain(invalid, 1));
            Assert.Throws<InvalidDataException>(() => store.Append(StateRevision.CreateObjectHeadMapDelta(invalid,
                [ObjectVersionRecord.CreateDelta(1, inherited, [2])], [])));
        }
        Assert.True(store.ReadCacheStatistics.MapHits > 0);
        Assert.True(store.ReadCacheStatistics.RevisionHits > 0);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(StateRevisionStore.DefaultReadCacheBudgetBytes)]
    public void Dispose_releases_residency_rejects_every_entry_point_and_leaves_borrowed_store_open(long budget) {
        using SegmentStore segments = SegmentStore.CreateNew(NewPath());
        using StateRevisionStore store = new(segments, budget);
        StateRevision input = Base(1, [7]);
        FrameAddress address = store.Append(input);
        StateRevision retained = store.Read(address);
        IReadOnlyDictionary<uint, FrameAddress> map = store.ReadLiveObjectHeadMap(address);
        store.Dispose();
        store.Dispose();
        Assert.Equal(0L, store.ReadCacheStatistics.ResidentChargeBytes);
        Assert.Equal(0, store.ReadCacheStatistics.EntryCount);
        Assert.Throws<ObjectDisposedException>(() => store.Read(address));
        Assert.Throws<ObjectDisposedException>(() => store.ReadLiveObjectHeadMap(address));
        Assert.Throws<ObjectDisposedException>(() => store.ReadObjectBaseBody(address, 1));
        Assert.Throws<ObjectDisposedException>(() => store.ReadObjectVersionChain(address, 1));
        Assert.Throws<ObjectDisposedException>(() => store.Append(input));
        Assert.Throws<ObjectDisposedException>(() => store.AppendDurably(input));
        Assert.Equal(new byte[] { 7 }, retained.LocalObjects[0].Body.ToArray());
        Assert.Equal(address, map[1]);
        using StateRevisionStore replacement = new(segments);
        Assert.Equal(new byte[] { 7 }, replacement.ReadObjectBaseBody(address, 1));
        _ = replacement.Append(input);
    }

    [Fact]
    public void Offline_rescue_discards_all_old_resources_before_reusing_address_in_a_fresh_store() {
        string path = NewPath();
        FrameAddress oldAddress;
        StateRevision retained;
        using (SegmentStore segments = SegmentStore.CreateNew(path)) {
            using StateRevisionStore store = new(segments);
            oldAddress = store.AppendDurably(Base(1, [10]));
            retained = store.Read(oldAddress);
            _ = store.ReadLiveObjectHeadMap(oldAddress);
        }
        // Explicit offline rescue: no StateRevisionStore or read cache exists here.
        RbfSegmentStoreOptions strict = new();
        using (SegmentStore rescue = SegmentStore.OpenExisting(path, strict)) {
            using RbfSegmentWriterLease writer = rescue.OpenActiveWriter();
            writer.File.Truncate(oldAddress.FrameTicket.Offset);
            writer.File.DurableFlush();
        }
        using SegmentStore reopened = SegmentStore.OpenExisting(path, strict);
        using StateRevisionStore fresh = new(reopened);
        FrameAddress replacementAddress = fresh.AppendDurably(Base(1, [20]));
        Assert.Equal(oldAddress, replacementAddress);
        Assert.Equal(new byte[] { 20 }, fresh.ReadObjectBaseBody(replacementAddress, 1));
        Assert.Equal(new byte[] { 10 }, retained.LocalObjects[0].Body.ToArray());
        Assert.Equal(1L, fresh.ReadCacheStatistics.RevisionDecodes);
    }

    private static StateRevision Base(uint id, ReadOnlySpan<byte> body) =>
        StateRevision.CreateObjectHeadMapBase(null, [ObjectVersionRecord.CreateBase(id, body)], []);

    private static long EntryCharge(StateRevision revision) =>
        StateRevisionReadCache.EntryOverheadBytes + StateRevisionReadCache.GetRevisionCharge(revision);

    private static FrameAddress RawAppend(RbfSegmentWriterLease writer, StateRevision revision) {
        ArrayBufferWriter<byte> payload = new();
        StateRevisionWireWriter.Write(payload, revision, new FileScope(writer.SegmentNumber));
        return new FrameAddress(writer.SegmentNumber,
            writer.File.Append(StateRevisionWireFormat.RbfTag, payload.WrittenSpan).Unwrap());
    }

    private string NewPath() {
        string path = Path.Combine(_temporaryRoot, $"durable-graph-read-cache-{Guid.NewGuid():N}");
        _paths.Add(path);
        return path;
    }

    public void Dispose() {
        foreach (string path in _paths) {
            string resolved = Path.GetFullPath(path);
            if (!string.Equals(Path.GetDirectoryName(resolved), Path.TrimEndingDirectorySeparator(_temporaryRoot),
                StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("durable-graph-read-cache-", StringComparison.Ordinal)) {
                throw new InvalidOperationException("Refusing to delete outside this fixture's temporary directory.");
            }
            if (Directory.Exists(resolved)) {
                Directory.Delete(resolved, recursive: true);
            }
        }
    }
}
