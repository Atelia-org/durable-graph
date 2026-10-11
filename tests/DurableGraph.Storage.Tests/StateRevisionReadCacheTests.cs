using System.Buffers;
using System.Buffers.Binary;
using Atelia.Binary;
using Atelia.Data;
using Atelia.FrameStore;
using static Atelia.DurableGraph.Storage.Tests.StorageTestFrames;
using Frames = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Storage.Tests;

public sealed class StateRevisionReadCacheTests : IDisposable {
    private readonly string _temporaryRoot = Path.GetFullPath(Path.GetTempPath());
    private readonly List<string> _paths = [];

    [Fact]
    public void Fitting_frame_and_map_are_reused_and_Append_does_not_seed_unmeasured_records() {
        using Frames segments = Frames.Create(NewPath());
        using StateRevisionStore store = new(segments);
        StateRevision input = Base(7, [10, 20, 30]);
        FrameAddress address = store.Append(input);
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
        using Frames segments = Frames.Create(NewPath());
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
        using Frames segments = Frames.Create(NewPath());
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
        using Frames segments = Frames.Create(NewPath());
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
        using Frames segments = Frames.Create(NewPath());
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
        using Frames segments = Frames.Create(NewPath());
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
        using Frames segments = Frames.Create(NewPath());
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
        using Frames segments = Frames.Create(NewPath());
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
        using Frames segments = Frames.Create(NewPath());
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
    public void Independent_stores_with_the_same_address_never_share_cached_values() {
        using Frames firstSegments = Frames.Create(NewPath());
        using Frames secondSegments = Frames.Create(NewPath());
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
    [InlineData(false)]
    [InlineData(true)]
    public void Cached_raw_and_map_still_reject_wrong_prior_and_branch_while_new_Base_resets_content_chain(bool otherBranch) {
        using Frames segments = Frames.Create(NewPath());
        using StateRevisionStore store = new(segments);
        FrameAddress original = store.Append(Base(1, [1]));
        FrameAddress updated = store.Append(StateRevision.CreateObjectHeadMapDelta(original,
            [ObjectVersionRecord.CreateDelta(1, original, [2])], []));
        FrameAddress wrong = otherBranch ? store.Append(Base(1, [3])) : original;
        FrameAddress corrupt = RawAppend(segments, StateRevision.CreateObjectHeadMapDelta(updated,
            [ObjectVersionRecord.CreateDelta(1, wrong, [4])], []));
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
        using Frames segments = Frames.Create(NewPath());
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
        using Frames segments = Frames.Create(NewPath());
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
        Assert.Equal(new byte[] { 7 }, retained.LocalObjects[0].Body.ToArray());
        Assert.Equal(address, map[1]);
        using StateRevisionStore replacement = new(segments);
        Assert.Equal(new byte[] { 7 }, replacement.ReadObjectBaseBody(address, 1));
        _ = replacement.Append(input);
    }

    [Fact]
    public void Full_opaque_address_distinguishes_rotated_frames_and_wrong_length() {
        using Frames frames = Frames.Create(NewPath(), RotationThreshold);
        using StateRevisionStore store = new(frames);
        FrameAddress first = store.Append(Base(1, [10]));
        FrameAddress second = store.Append(Base(1, [20]));
        Assert.NotEqual(first, second);
        Assert.Equal(new byte[] { 10 }, store.Read(first).LocalObjects[0].Body.ToArray());
        Assert.Equal(new byte[] { 20 }, store.Read(second).LocalObjects[0].Body.ToArray());
        Assert.Throws<InvalidOperationException>(() => store.Read(WithWrongLength(first)));
        Assert.Equal(3L, store.ReadCacheStatistics.RevisionMisses);
        Assert.Equal(2L, store.ReadCacheStatistics.RevisionDecodes);
        Assert.Equal(2, store.ReadCacheStatistics.EntryCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_raw_decode_is_not_admitted_or_negative_cached(bool badRole) {
        using Frames frames = Frames.Create(NewPath());
        FrameAddress address = badRole
            ? frames.Append(0x01020304, Encode(Base(1, [1]))).Unwrap()
            : frames.Append(StateRevisionStore.FrameTag, [255]).Unwrap();
        using StateRevisionStore store = new(frames);
        Assert.Throws<InvalidDataException>(() => store.Read(address));
        Assert.Throws<InvalidDataException>(() => store.Read(address));
        Assert.Equal(2L, store.ReadCacheStatistics.RevisionMisses);
        Assert.Equal(0L, store.ReadCacheStatistics.RevisionDecodes);
        Assert.Equal(0, store.ReadCacheStatistics.EntryCount);
    }

    [Fact]
    public void Offline_corrupted_payload_fails_CRC_on_every_fresh_read_without_negative_cache() {
        string path = NewPath();
        FrameAddress address;
        using (Frames frames = Frames.Create(path)) {
            using StateRevisionStore store = new(frames);
            address = store.Append(Base(1, [10]));
            _ = store.Append(Base(2, [20])); // Healthy EOF keeps normal open independent of the earlier payload.
            frames.ConfirmDurable();
        }
        string file = Assert.Single(Directory.GetFiles(path, "*.rbf", SearchOption.AllDirectories));
        byte[] bytes = File.ReadAllBytes(file);
        byte[] encodedAddress = EncodeAddress(address);
        long offset = SizedPtr.FromPacked(BinaryPrimitives.ReadUInt64LittleEndian(encodedAddress.AsSpan(4))).Offset;
        // A hostile offline fixture changes payload ciphertext but preserves CRC and all framing.
        bytes[checked((int)offset + sizeof(uint))] ^= 1;
        File.WriteAllBytes(file, bytes);
        using Frames reopened = Frames.OpenReadOnly(path);
        using StateRevisionStore fresh = new(reopened);
        for (int i = 0; i < 2; i++) {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => fresh.Read(address));
            Assert.Contains("CRC", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(2L, fresh.ReadCacheStatistics.RevisionMisses);
        Assert.Equal(0L, fresh.ReadCacheStatistics.RevisionDecodes);
        Assert.Equal(0, fresh.ReadCacheStatistics.EntryCount);
    }

    [Fact]
    public void Failed_cyclic_map_retries_validation_but_keeps_successfully_decoded_raw_frame() {
        using Frames frames = Frames.Create(NewPath());
        int length = Encode(StateRevision.CreateObjectHeadMapDelta(Address(1, 32), [], [])).Length;
        FrameAddress address;
        using (var builder = frames.BeginAppend(length, 0, out address)) {
            BareValueWriter writer = new(builder.PayloadAndMeta);
            writer.WriteRawBytes(Encode(StateRevision.CreateObjectHeadMapDelta(address, [], [])));
            Assert.Equal(address, builder.EndAppend(StateRevisionStore.FrameTag).Unwrap());
        }
        using StateRevisionStore store = new(frames);
        Assert.Throws<InvalidDataException>(() => store.ReadLiveObjectHeadMap(address));
        Assert.Throws<InvalidDataException>(() => store.ReadLiveObjectHeadMap(address));
        Assert.Equal(1L, store.ReadCacheStatistics.RevisionDecodes);
        Assert.Equal(1L, store.ReadCacheStatistics.RevisionHits);
        Assert.Equal(2L, store.ReadCacheStatistics.MapMisses);
        Assert.Equal(0L, store.ReadCacheStatistics.MapMaterializations);
        Assert.Equal(1, store.ReadCacheStatistics.EntryCount);
    }

    [Fact]
    public void Warm_cached_reads_reject_a_closed_backing_owner() {
        using Frames frames = Frames.Create(NewPath());
        using StateRevisionStore store = new(frames);
        FrameAddress address = store.Append(Base(1, [9]));
        _ = store.Read(address);
        _ = store.ReadLiveObjectHeadMap(address);
        frames.Dispose();
        Assert.Throws<ObjectDisposedException>(() => store.Read(address));
        Assert.Throws<ObjectDisposedException>(() => store.ReadLiveObjectHeadMap(address));
        Assert.Throws<ObjectDisposedException>(() => store.ReadObjectBaseBody(address, 1));
        Assert.Throws<ObjectDisposedException>(() => store.ReadObjectVersionChain(address, 1));
        Assert.Throws<ObjectDisposedException>(() => store.Append(Base(1, [10])));
        Assert.Throws<ObjectDisposedException>(() => new StateRevisionStore(frames));
    }

    private static StateRevision Base(uint id, ReadOnlySpan<byte> body) =>
        StateRevision.CreateObjectHeadMapBase(null, [ObjectVersionRecord.CreateBase(id, body)], []);

    private static long EntryCharge(StateRevision revision) =>
        StateRevisionReadCache.EntryOverheadBytes + StateRevisionReadCache.GetRevisionCharge(revision);

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
