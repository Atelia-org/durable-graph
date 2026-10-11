using System.Collections;
using Atelia.Binary;
using Atelia.FrameStore;
using static Atelia.DurableGraph.Storage.Tests.StorageTestFrames;
using Frames = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Storage.Tests;

public sealed class ObjectVersionChainStoreTests : IDisposable {
    private readonly string _temporaryRoot = Path.GetFullPath(Path.GetTempPath());
    private readonly List<string> _storePaths = [];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Exact_chains_skip_unchanged_revisions_and_survive_reopen_with_owned_bytes(bool rollover) {
        string path = NewStorePath();
        long threshold = rollover ? RotationThreshold : 64 * 1024 * 1024;
        FrameAddress original;
        FrameAddress firstDelta;
        FrameAddress unchanged;
        FrameAddress secondDelta;
        ObjectVersionChain retained;
        using (Frames segments = Frames.Create(path, threshold)) {
            using StateRevisionStore store = new(segments);
            byte[] source = [10, 11];
            original = store.Append(StateRevision.CreateObjectHeadMapBase(null, [B(1, source), B(2, [20])], []));
            source.AsSpan().Fill(99);
            firstDelta = store.Append(StateRevision.CreateObjectHeadMapDelta(original, [D(1, original, [12])], []));
            unchanged = store.Append(StateRevision.CreateObjectHeadMapDelta(firstDelta, [], []));
            // ObjectHeadMap Base and ObjectVersion Delta are independent choices.
            secondDelta = store.Append(StateRevision.CreateObjectHeadMapBase(unchanged, [D(1, firstDelta, [13, 14])], [new(2, original)]));

            retained = Verify(store);
            segments.ConfirmDurable();
        }

        using (Frames reopened = Frames.OpenReadOnly(path)) {
            using StateRevisionStore coldStore = new(reopened);
            _ = Verify(coldStore);
        }

        Assert.Equal(new byte[] { 10, 11 }, retained.Records[0].Record.Body.ToArray());
        Assert.False(retained.Records is IList);
        Assert.False(retained.Records is ICollection);
        Assert.False(retained.Records is IList<ObjectVersionChainEntry>);
        byte[] externalCopy = retained.Records[1].Record.Body.ToArray();
        externalCopy[0] = 255;
        Assert.Equal(new byte[] { 12 }, retained.Records[1].Record.Body.ToArray());

        ObjectVersionChain Verify(StateRevisionStore store) {
            ObjectVersionChain chain = store.ReadObjectVersionChain(secondDelta, 1);
            Assert.Equal(1u, chain.ObjectId);
            Assert.Equal(secondDelta, chain.ObjectHeadAddress);
            Assert.Equal(new[] { original, firstDelta, secondDelta }, chain.Records.Select(x => x.ContainingRevisionAddress));
            Assert.Equal(new[] { ObjectVersionKind.Base, ObjectVersionKind.Delta, ObjectVersionKind.Delta }, chain.Records.Select(x => x.Record.Kind));
            Assert.Equal(new byte[] { 10, 11 }, chain.Records[0].Record.Body.ToArray());
            Assert.Equal(new byte[] { 12 }, chain.Records[1].Record.Body.ToArray());
            Assert.Equal(new byte[] { 13, 14 }, chain.Records[2].Record.Body.ToArray());
            AssertCosts(chain);
            Assert.Equal(original, Assert.Single(store.ReadObjectVersionChain(original, 1).Records).ContainingRevisionAddress);
            Assert.Equal(firstDelta, store.ReadObjectVersionChain(unchanged, 1).ObjectHeadAddress);
            Assert.Equal(new[] { original, firstDelta }, store.ReadObjectVersionChain(unchanged, 1).Records.Select(x => x.ContainingRevisionAddress));
            Assert.Equal(original, Assert.Single(store.ReadObjectVersionChain(secondDelta, 2).Records).ContainingRevisionAddress);
            Assert.Throws<InvalidDataException>(() => store.ReadObjectBaseBody(secondDelta, 1));
            Assert.Equal(new byte[] { 20 }, store.ReadObjectBaseBody(secondDelta, 2));
            return chain;
        }
    }

    [Fact]
    public void Append_rejects_stale_other_branch_and_wrong_ticket_priors_before_writing() {
        using Frames segments = Frames.Create(NewStorePath());
        using StateRevisionStore store = new(segments);
        FrameAddress original = store.Append(StateRevision.CreateObjectHeadMapBase(null, [B(1, [1])], []));
        FrameAddress updated = store.Append(StateRevision.CreateObjectHeadMapDelta(original, [D(1, original, [2])], []));
        FrameAddress otherBranch = store.Append(StateRevision.CreateObjectHeadMapDelta(original, [B(1, [3])], []));
        FrameAddress wrongTicket = WithWrongLength(updated);
        foreach (FrameAddress prior in new[] { original, otherBranch, wrongTicket }) {
            AssertAppendRejectedWithoutWrite(segments, store, StateRevision.CreateObjectHeadMapDelta(updated, [D(1, prior, [4])], []));
        }
        FrameAddress valid = store.Append(StateRevision.CreateObjectHeadMapDelta(updated, [D(1, updated, [4])], []));
        Assert.Equal(3, store.ReadObjectVersionChain(valid, 1).Records.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_checks_edges_even_for_frames_that_bypass_Append(bool wrongBranch) {
        using Frames segments = Frames.Create(NewStorePath());
        using StateRevisionStore store = new(segments);
        FrameAddress original = store.Append(StateRevision.CreateObjectHeadMapBase(null, [B(1, [1])], []));
        FrameAddress updated = store.Append(StateRevision.CreateObjectHeadMapDelta(original, [D(1, original, [2])], []));
        FrameAddress otherBranch = store.Append(StateRevision.CreateObjectHeadMapDelta(original, [B(1, [3])], []));
        FrameAddress corrupt = RawAppend(segments, StateRevision.CreateObjectHeadMapDelta(updated, [D(1, wrongBranch ? otherBranch : original, [4])], []));
        Assert.Equal(corrupt, store.ReadLiveObjectHeadMap(corrupt)[1]);
        Assert.Equal(ObjectVersionKind.Delta, Assert.Single(store.Read(corrupt).LocalObjects).Kind);
        Assert.Throws<InvalidDataException>(() => store.ReadObjectVersionChain(corrupt, 1));
        // Append intentionally checks only the immediate edge, not the prior's ancestry.
        FrameAddress child = store.Append(StateRevision.CreateObjectHeadMapDelta(corrupt, [D(1, corrupt, [5])], []));
        Assert.Throws<InvalidDataException>(() => store.ReadObjectVersionChain(child, 1));
    }

    [Fact]
    public void Prior_must_have_a_local_record_and_cannot_fall_back_to_its_parent() {
        using Frames segments = Frames.Create(NewStorePath());
        using StateRevisionStore store = new(segments);
        FrameAddress original = store.Append(StateRevision.CreateObjectHeadMapBase(null, [B(1, [1])], []));
        FrameAddress unchanged = store.Append(StateRevision.CreateObjectHeadMapDelta(original, [], []));
        FrameAddress invalidMap = store.Append(StateRevision.CreateObjectHeadMapBase(unchanged, [], [new(1, unchanged)]));
        Assert.Equal(unchanged, store.ReadLiveObjectHeadMap(invalidMap)[1]);
        Assert.Throws<InvalidDataException>(() => store.ReadObjectVersionChain(invalidMap, 1));
        StateRevision delta = StateRevision.CreateObjectHeadMapDelta(invalidMap, [D(1, unchanged, [2])], []);
        AssertAppendRejectedWithoutWrite(segments, store, delta);
        FrameAddress corrupt = RawAppend(segments, delta);
        Assert.Throws<InvalidDataException>(() => store.ReadObjectVersionChain(corrupt, 1));
    }

    [Fact]
    public void Removed_id_requires_new_Base_and_later_Delta_cannot_rejoin_old_occupant() {
        using Frames segments = Frames.Create(NewStorePath());
        using StateRevisionStore store = new(segments);
        FrameAddress old = store.Append(StateRevision.CreateObjectHeadMapBase(null, [B(1, [1])], []));
        FrameAddress removed = store.Append(StateRevision.CreateObjectHeadMapDelta(old, [], [1]));
        StateRevision invalidInsert = StateRevision.CreateObjectHeadMapDelta(removed, [D(1, old, [2])], []);
        AssertAppendRejectedWithoutWrite(segments, store, invalidInsert);
        Assert.Throws<InvalidDataException>(() => store.ReadObjectVersionChain(RawAppend(segments, invalidInsert), 1));
        Assert.Throws<InvalidDataException>(() => store.ReadObjectVersionChain(removed, 1));
        FrameAddress fresh = store.Append(StateRevision.CreateObjectHeadMapDelta(removed, [B(1, [90])], []));
        AssertAppendRejectedWithoutWrite(segments, store, StateRevision.CreateObjectHeadMapDelta(fresh, [D(1, old, [91])], []));
        FrameAddress updated = store.Append(StateRevision.CreateObjectHeadMapDelta(fresh, [D(1, fresh, [91])], []));
        Assert.Equal(new[] { fresh, updated }, store.ReadObjectVersionChain(updated, 1).Records.Select(x => x.ContainingRevisionAddress));
        Assert.Equal(new byte[] { 1 }, Assert.Single(store.ReadObjectVersionChain(old, 1).Records).Record.Body.ToArray());
    }

    [Fact]
    public void Complete_map_can_reselect_old_head_as_a_shallow_declaration() {
        using Frames segments = Frames.Create(NewStorePath());
        using StateRevisionStore store = new(segments);
        FrameAddress old = store.Append(StateRevision.CreateObjectHeadMapBase(null, [B(1, [1])], []));
        FrameAddress removed = store.Append(StateRevision.CreateObjectHeadMapDelta(old, [], [1]));
        FrameAddress fresh = store.Append(StateRevision.CreateObjectHeadMapDelta(removed, [B(1, [90])], []));
        FrameAddress reselected = store.Append(StateRevision.CreateObjectHeadMapBase(fresh, [], [new(1, old)]));
        FrameAddress delta = store.Append(StateRevision.CreateObjectHeadMapDelta(reselected, [D(1, old, [2])], []));
        // Agreement with exact Parent is not global entity identity authentication.
        Assert.Equal(new[] { old, delta }, store.ReadObjectVersionChain(delta, 1).Records.Select(x => x.ContainingRevisionAddress));
        Assert.Equal(fresh, Assert.Single(store.ReadObjectVersionChain(fresh, 1).Records).ContainingRevisionAddress);
    }

    [Fact]
    public void New_Base_stops_content_validation_and_resets_H_even_if_older_Delta_edge_is_invalid() {
        using Frames segments = Frames.Create(NewStorePath());
        using StateRevisionStore store = new(segments);
        FrameAddress original = store.Append(StateRevision.CreateObjectHeadMapBase(null, [B(1, new byte[128])], []));
        FrameAddress validDelta = store.Append(StateRevision.CreateObjectHeadMapDelta(original, [D(1, original, [1])], []));
        FrameAddress corrupt = RawAppend(segments, StateRevision.CreateObjectHeadMapDelta(validDelta, [D(1, original, [2])], []));
        Assert.Throws<InvalidDataException>(() => store.ReadObjectVersionChain(corrupt, 1));
        FrameAddress reset = store.Append(StateRevision.CreateObjectHeadMapDelta(corrupt, [B(1, [])], []));
        FrameAddress latest = store.Append(StateRevision.CreateObjectHeadMapDelta(reset, [D(1, reset, [3])], []));
        ObjectVersionChain resetChain = store.ReadObjectVersionChain(reset, 1);
        Assert.Equal(reset, Assert.Single(resetChain.Records).ContainingRevisionAddress);
        Assert.Equal(2L, resetChain.ReconstructionPayloadBytes); // Base kind + zero body length.
        ObjectVersionChain chain = store.ReadObjectVersionChain(latest, 1);
        Assert.Equal(new[] { reset, latest }, chain.Records.Select(x => x.ContainingRevisionAddress));
        AssertCosts(chain);
        Assert.True(chain.ReconstructionPayloadBytes < store.ReadObjectVersionChain(validDelta, 1).ReconstructionPayloadBytes);
    }

    [Fact]
    public void H_uses_fixed_address_and_actual_body_length_after_rotation_and_reopen() {
        string path = NewStorePath();
        long threshold = RotationThreshold;
        FrameAddress original;
        FrameAddress firstDelta;
        FrameAddress latest;
        using (Frames segments = Frames.Create(path, threshold)) {
            using StateRevisionStore store = new(segments);
            original = store.Append(StateRevision.CreateObjectHeadMapBase(null, [B(128, new byte[127])], []));
            firstDelta = store.Append(StateRevision.CreateObjectHeadMapDelta(original, [D(128, original, new byte[128])], []));
            FrameAddress parent = firstDelta;
            for (int index = 0; index < 127; index++) {
                parent = store.Append(StateRevision.CreateObjectHeadMapDelta(parent, [], []));
            }
            latest = store.Append(StateRevision.CreateObjectHeadMapDelta(parent, [D(128, firstDelta, [])], []));
            segments.ConfirmDurable();

        }
        using Frames reopened = Frames.OpenReadOnly(path);
        using StateRevisionStore coldStore = new(reopened);
        ObjectVersionChain chain = coldStore.ReadObjectVersionChain(latest, 128);
        Assert.Equal(new[] { original, firstDelta, latest }, chain.Records.Select(x => x.ContainingRevisionAddress));
        Assert.Equal(129, chain.Records[0].ObjectVersionPayloadBytes); // kind + one-byte length + 127 bytes.
        Assert.Equal(1 + FrameAddress.EncodedSize + 2 + 128, chain.Records[1].ObjectVersionPayloadBytes);
        Assert.Equal(1 + FrameAddress.EncodedSize + 1, chain.Records[2].ObjectVersionPayloadBytes);
        AssertCosts(chain);
        Assert.Equal(chain.Records[0].ObjectVersionPayloadBytes + (long)chain.Records[1].ObjectVersionPayloadBytes,
            coldStore.ReadObjectVersionChain(firstDelta, 128).ReconstructionPayloadBytes);
    }

    [Fact]
    public void Chain_read_rejects_invalid_requests_and_distinguishes_empty_Base_from_missing_id() {
        using Frames segments = Frames.Create(NewStorePath());
        using StateRevisionStore store = new(segments);
        FrameAddress original = store.Append(StateRevision.CreateObjectHeadMapBase(null, [B(uint.MaxValue, [])], []));
        Assert.Empty(Assert.Single(store.ReadObjectVersionChain(original, uint.MaxValue).Records).Record.Body.ToArray());
        Assert.Throws<InvalidDataException>(() => store.ReadObjectVersionChain(original, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ReadObjectVersionChain(original, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ReadObjectVersionChain(default, 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Actual_two_frame_map_or_object_prior_cycle_fails_closed(bool objectPrior) {
        using Frames frames = Frames.Create(NewStorePath());
        // Only the hostile fixture uses early addresses. Both frames complete before DG reads them.
        FrameAddress witness = Address(1, 32);
        StateRevision template = objectPrior
            ? StateRevision.CreateObjectHeadMapBase(witness, [D(1, witness, [])], [])
            : StateRevision.CreateObjectHeadMapDelta(witness, [], []);
        int length = Encode(template).Length;
        using var firstBuilder = frames.BeginAppend(length, 0, out FrameAddress first);
        using var secondBuilder = frames.BeginAppend(length, 0, out FrameAddress second);
        StateRevision firstRevision = objectPrior
            ? StateRevision.CreateObjectHeadMapBase(second, [D(1, second, [])], [])
            : StateRevision.CreateObjectHeadMapDelta(second, [], []);
        StateRevision secondRevision = objectPrior
            ? StateRevision.CreateObjectHeadMapBase(first, [D(1, first, [])], [])
            : StateRevision.CreateObjectHeadMapDelta(first, [], []);
        BareValueWriter firstWriter = new(firstBuilder.PayloadAndMeta);
        firstWriter.WriteRawBytes(Encode(firstRevision));
        BareValueWriter secondWriter = new(secondBuilder.PayloadAndMeta);
        secondWriter.WriteRawBytes(Encode(secondRevision));
        Assert.Equal(first, firstBuilder.EndAppend(StateRevisionStore.FrameTag).Unwrap());
        Assert.Equal(second, secondBuilder.EndAppend(StateRevisionStore.FrameTag).Unwrap());
        using StateRevisionStore store = new(frames);
        InvalidDataException error = objectPrior
            ? Assert.Throws<InvalidDataException>(() => store.ReadObjectVersionChain(first, 1))
            : Assert.Throws<InvalidDataException>(() => store.ReadLiveObjectHeadMap(first));
        Assert.Contains("cycle", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertCosts(ObjectVersionChain chain) {
        long total = 0;
        foreach (ObjectVersionChainEntry entry in chain.Records) {
            // Independent wire construction: no production reader/writer or size estimator.
            List<byte> payload = [(byte)entry.Record.Kind];
            if (entry.Record.PriorAddress is { } prior) {
                payload.AddRange(EncodeAddress(prior));
            }
            payload.AddRange(EncodeVarint((ulong)entry.Record.Body.Length));
            payload.AddRange(entry.Record.Body.ToArray());
            Assert.Equal(payload.Count, entry.ObjectVersionPayloadBytes);
            total += payload.Count;
        }
        Assert.Equal(total, chain.ReconstructionPayloadBytes);
    }

    private static byte[] EncodeVarint(ulong value) {
        List<byte> bytes = [];
        while (value >= 128) {
            bytes.Add((byte)((value & 127) | 128));
            value >>= 7;
        }
        bytes.Add((byte)value);
        return bytes.ToArray();
    }

    private static void AssertAppendRejectedWithoutWrite(Frames frames, StateRevisionStore store, StateRevision revision) {
        long before = FrameCount(frames);
        Exception? error = Record.Exception(() => store.Append(revision));
        Assert.True(error is InvalidDataException or InvalidOperationException, $"Unexpected error: {error}");
        Assert.Equal(before, FrameCount(frames));
    }

    private static ObjectVersionRecord B(uint id, ReadOnlySpan<byte> body) => ObjectVersionRecord.CreateBase(id, body);
    private static ObjectVersionRecord D(uint id, FrameAddress prior, ReadOnlySpan<byte> body) => ObjectVersionRecord.CreateDelta(id, prior, body);
    private string NewStorePath() {
        string path = Path.GetFullPath(Path.Combine(_temporaryRoot, $"durable-graph-object-chain-{Guid.NewGuid():N}"));
        _storePaths.Add(path);
        return path;
    }

    public void Dispose() {
        foreach (string path in _storePaths) {
            string resolved = Path.GetFullPath(path);
            if (!string.Equals(Path.GetDirectoryName(resolved), Path.TrimEndingDirectorySeparator(_temporaryRoot),
                StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("durable-graph-object-chain-", StringComparison.Ordinal)) {
                throw new InvalidOperationException("Refusing to delete outside this fixture's temporary directory.");
            }
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
        }
    }
}
