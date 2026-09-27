using System.Buffers;
using System.Collections;
using Atelia.Data;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.RbfSegmentStore;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed partial class ObjectRevisionPlannerTests : IDisposable {
    private readonly string _temporaryRoot = Path.GetFullPath(Path.GetTempPath());
    private readonly List<string> _paths = [];

    [Fact]
    public void Initial_rows_produce_owned_canonical_Base_and_can_be_appended_after_disposal_of_inputs() {
        using SegmentStore segments = NewStore();
        using StateRevisionStore store = new(segments);
        byte[] bytes = [10, 20];
        List<PreparedObject> rows = [PreparedObject.New(new ObjectId(2), new(bytes)), PreparedObject.New(new ObjectId(1), new([]))];
        PreparedObjectRevision result = Plan(store, null, rows);
        bytes.AsSpan().Fill(99);
        rows.Clear();
        Assert.Equal(ObjectHeadMapKind.Base, result.Revision.ObjectHeadMapKind);
        Assert.Null(result.Revision.ParentRevisionAddress);
        Assert.Equal(new uint[] { 1, 2 }, result.Revision.LocalObjectIds);
        Assert.Equal(new byte[] { 10, 20 }, result.Revision.LocalObjects[1].Body.ToArray());
        Assert.Equal(new long[] { 2, 4 }, result.Estimates.Select(x => x.BasePayloadBytes));
        Assert.All(result.Estimates, x => Assert.Equal(ObjectSaveChangeKind.Insert, x.ChangeKind));
        Assert.False(result.Estimates is ICollection);
        Assert.False(result.Estimates is IList<ObjectSaveEstimate>);
        Assert.False(result.RepresentationPlan.Writes is ICollection);
        FrameAddress address = store.Append(result.Revision);
        Assert.Equal(new byte[] { 10, 20 }, store.ReadObjectBaseBody(address, 2));
    }

    [Fact]
    public void Compared_classifies_by_HasChanges_even_when_unchanged_payload_is_nonempty() {
        using SegmentStore segments = NewStore();
        using StateRevisionStore store = new(segments);
        FrameAddress parent = store.Append(StateRevision.CreateObjectHeadMapBase(null, [B(1, new byte[100]), B(2, [7])], []));
        PreparedObjectRevision result = Plan(store, parent, [
            PreparedObject.Compared(new ObjectId(1), parent, Base(100), new(false, [0, 0])),
            PreparedObject.Unchanged(new ObjectId(2), parent, new([7])),
        ]);
        Assert.Empty(result.Revision.LocalObjects);
        Assert.Empty(result.Revision.RemovedObjectIds);
        Assert.All(result.Estimates, x => {
            Assert.Equal(ObjectSaveChangeKind.NoChange, x.ChangeKind);
            Assert.Null(x.DeltaPayloadBytesUpperBound);
            Assert.NotNull(x.ReconstructionPayloadBytes);
        });
        FrameAddress saved = store.Append(result.Revision);
        Assert.Equal(parent, store.ReadLiveObjectHeadMap(saved)[1]);
        Assert.Equal(parent, store.ReadLiveObjectHeadMap(saved)[2]);
    }

    [Fact]
    public void Mixed_rows_use_real_H_and_complete_membership_difference() {
        using SegmentStore segments = NewStore();
        using StateRevisionStore store = new(segments);
        FrameAddress first = store.Append(StateRevision.CreateObjectHeadMapBase(null, [B(1, new byte[100]), B(2, [2]), B(3, [3])], []));
        FrameAddress parent = store.Append(StateRevision.CreateObjectHeadMapDelta(first, [D(1, first, [4])], []));
        byte[] deltaBytes = [5];
        PreparedObjectRevision result = Plan(store, parent, [
            PreparedObject.Compared(new ObjectId(1), parent, Base(100), new(true, deltaBytes)),
            PreparedObject.Unchanged(new ObjectId(2), first, new([2])),
            PreparedObject.New(new ObjectId(4), new([4])),
        ]);
        deltaBytes[0] = 99;
        Assert.Equal(ObjectHeadMapKind.Delta, result.Revision.ObjectHeadMapKind);
        Assert.Equal(new uint[] { 1, 4 }, result.Revision.LocalObjectIds);
        Assert.Equal(new uint[] { 3 }, result.Revision.RemovedObjectIds);
        Assert.Equal(ObjectVersionKind.Delta, result.Revision.LocalObjects[0].Kind);
        Assert.Equal(parent, result.Revision.LocalObjects[0].PriorAddress);
        Assert.Equal(new byte[] { 5 }, result.Revision.LocalObjects[0].Body.ToArray());
        Assert.Equal(store.ReadObjectVersionChain(parent, 1).ReconstructionPayloadBytes, result.Estimates[0].ReconstructionPayloadBytes);
        FrameAddress saved = store.Append(result.Revision);
        Assert.Equal(new uint[] { 1, 2, 4 }, store.ReadLiveObjectHeadMap(saved).Keys.Order());
        Assert.Equal(first, store.ReadLiveObjectHeadMap(saved)[2]);
        Assert.Equal(3, store.ReadObjectVersionChain(saved, 1).Records.Count);
        Assert.Equal(new byte[] { 3 }, store.ReadObjectBaseBody(parent, 3));
    }

    [Fact]
    public void Empty_complete_set_removes_every_parent_object_and_initial_empty_is_valid() {
        using SegmentStore segments = NewStore();
        using StateRevisionStore store = new(segments);
        Assert.Empty(Plan(store, null, []).Revision.LocalObjects);
        FrameAddress parent = store.Append(StateRevision.CreateObjectHeadMapBase(null, [B(1, [1]), B(2, [2])], []));
        PreparedObjectRevision result = Plan(store, parent, []);
        Assert.Empty(result.Estimates);
        Assert.Empty(result.RepresentationPlan.Writes);
        Assert.Equal(ObjectHeadMapKind.Base, result.Revision.ObjectHeadMapKind);
        Assert.Empty(result.Revision.RemovedObjectIds);
        Assert.Equal(parent, result.Revision.ParentRevisionAddress);
        FrameAddress removed = store.Append(result.Revision);
        Assert.Empty(store.ReadLiveObjectHeadMap(removed));
        Assert.Equal(2, store.ReadLiveObjectHeadMap(parent).Count);
    }

    [Fact]
    public void Both_parameters_control_optional_Base_and_input_order_does_not_affect_results() {
        using SegmentStore segments = NewStore();
        using StateRevisionStore store = new(segments);
        FrameAddress first = store.Append(StateRevision.CreateObjectHeadMapBase(null, [B(1, new byte[20]), B(2, new byte[20])], []));
        FrameAddress parent = store.Append(StateRevision.CreateObjectHeadMapDelta(first, [D(1, first, new byte[30]), D(2, first, new byte[30])], []));
        PreparedObject[] rows = [PreparedObject.Unchanged(new ObjectId(1), parent, Base(20)), PreparedObject.Unchanged(new ObjectId(2), parent, Base(20))];
        Assert.Empty(Plan(store, parent, rows, 10, 100).Revision.LocalObjects);
        Assert.Equal(new uint[] { 1 }, Plan(store, parent, rows, 1, 1).Revision.LocalObjectIds);
        PreparedObjectRevision both = Plan(store, parent, rows, 1, 100);
        Assert.Equal(new uint[] { 1, 2 }, both.Revision.LocalObjectIds);
        PreparedObjectRevision reversed = Plan(store, parent, rows.Reverse(), 1, 100);
        Assert.Equal(both.Estimates, reversed.Estimates);
        Assert.Equal(both.RepresentationPlan.Writes, reversed.RepresentationPlan.Writes);
        FrameAddress saved = store.Append(both.Revision);
        Assert.Equal(22L, store.ReadObjectVersionChain(saved, 1).ReconstructionPayloadBytes);
    }

    [Fact]
    public void Removed_objects_do_not_inflate_optional_Base_budget() {
        using SegmentStore segments = NewStore();
        using StateRevisionStore store = new(segments);
        FrameAddress first = store.Append(StateRevision.CreateObjectHeadMapBase(null,
            [B(1, new byte[20]), B(2, new byte[20]), B(3, new byte[1000])], []));
        FrameAddress parent = store.Append(StateRevision.CreateObjectHeadMapDelta(first, [D(1, first, new byte[30]), D(2, first, new byte[30])], []));
        PreparedObjectRevision result = Plan(store, parent, [
            PreparedObject.Unchanged(new ObjectId(1), parent, Base(20)), PreparedObject.Unchanged(new ObjectId(2), parent, Base(20)),
        ], 1, 10);
        Assert.Equal(new uint[] { 1 }, result.Revision.LocalObjectIds);
        Assert.Equal(new uint[] { 3 }, result.Revision.RemovedObjectIds);
        Assert.Equal(44L, result.Estimates.Sum(x => x.BasePayloadBytes));
    }

    [Fact]
    public void Conservative_D_can_select_required_Base_when_actual_D_is_smaller() {
        using SegmentStore segments = NewStore();
        using StateRevisionStore store = new(segments);
        FrameAddress original = store.Append(StateRevision.CreateObjectHeadMapBase(null, [B(1, [1])], []));
        FrameAddress actualDelta = store.Append(StateRevision.CreateObjectHeadMapDelta(original, [D(1, original, [2])], []));
        int actual = store.ReadObjectVersionChain(actualDelta, 1).Records[1].ObjectVersionPayloadBytes;
        // With a one-byte body length, Base body = actual-1 gives B=actual+1.
        PreparedObjectRevision result = Plan(store, original,
            [PreparedObject.Compared(new ObjectId(1), original, Base(actual - 1), new(true, [2]))], int.MaxValue, 1);
        ObjectSaveEstimate estimate = Assert.Single(result.Estimates);
        Assert.True(actual < estimate.BasePayloadBytes);
        Assert.True(estimate.BasePayloadBytes <= estimate.DeltaPayloadBytesUpperBound);
        Assert.Equal(ObjectVersionKind.Base, Assert.Single(result.Revision.LocalObjects).Kind);
    }

    [Fact]
    public void Wrong_prior_branch_and_full_ticket_are_rejected_before_append_or_pending_rollover() {
        using SegmentStore segments = NewStore(rollover: true);
        using StateRevisionStore store = new(segments);
        FrameAddress first = store.Append(StateRevision.CreateObjectHeadMapBase(null, [B(1, [1])], []));
        FrameAddress parent = store.Append(StateRevision.CreateObjectHeadMapDelta(first, [B(1, [2])], []));
        FrameAddress other = store.Append(StateRevision.CreateObjectHeadMapDelta(first, [B(1, [3])], []));
        FrameAddress wrongTicket = new(parent.FileNumber, SizedPtr.Create(parent.FrameTicket.Offset, parent.FrameTicket.Length + 4));
        uint active = segments.ActiveSegmentNumber;
        foreach (FrameAddress wrong in new[] { first, other, wrongTicket }) {
            Assert.Throws<ArgumentException>(() => Plan(store, parent, [PreparedObject.BaseOnlyUpdate(new ObjectId(1), wrong, new([4]))]));
            Assert.Equal(active, segments.ActiveSegmentNumber);
        }
        PreparedObjectRevision good = Plan(store, parent, [PreparedObject.BaseOnlyUpdate(new ObjectId(1), parent, new([4]))]);
        Assert.Equal(active, segments.ActiveSegmentNumber);
        FrameAddress appended = store.Append(good.Revision);
        Assert.Equal(active + 1, appended.FileNumber);
        Assert.Equal(first.FrameTicket.Offset, appended.FrameTicket.Offset);
    }

    [Fact]
    public void Invalid_complete_rows_are_rejected_without_changing_the_file() {
        using SegmentStore segments = NewStore();
        using StateRevisionStore store = new(segments);
        FrameAddress parent = store.Append(StateRevision.CreateObjectHeadMapBase(null, [B(1, [1])], []));
        PreparedObject[][] invalid = [
            [PreparedObject.New(new ObjectId(1), new([1]))],
            [PreparedObject.Unchanged(new ObjectId(2), parent, new([2]))],
            [PreparedObject.New(new ObjectId(2), new([2])), PreparedObject.New(new ObjectId(2), new([2]))],
            [null!],
        ];
        long before = Tail(segments);
        foreach (PreparedObject[] rows in invalid) {
            Assert.Throws<ArgumentException>(() => Plan(store, parent, rows));
        }
        Assert.Throws<ArgumentException>(() => Plan(store, null, [PreparedObject.Unchanged(new ObjectId(1), parent, new([1]))]));
        Assert.Throws<ArgumentNullException>(() => Plan(null!, parent, []));
        Assert.Throws<ArgumentNullException>(() => Plan(store, parent, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => Plan(store, default(FrameAddress), []));
        Assert.Throws<ArgumentOutOfRangeException>(() => Plan(store, parent, [], 0, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => Plan(store, parent, [], 3, 0));
        Assert.Equal(before, Tail(segments));
    }

    [Fact]
    public void Base_only_can_cut_a_bad_old_Delta_chain_while_other_existing_shapes_must_validate_H() {
        using SegmentStore segments = NewStore();
        using StateRevisionStore store = new(segments);
        FrameAddress first = store.Append(StateRevision.CreateObjectHeadMapBase(null, [B(1, [1])], []));
        FrameAddress latest = store.Append(StateRevision.CreateObjectHeadMapDelta(first, [B(1, [2])], []));
        FrameAddress corrupt = AppendWrongPrior(segments, latest, first);
        Assert.Throws<InvalidDataException>(() => store.ReadObjectVersionChain(corrupt, 1));
        Assert.Throws<InvalidDataException>(() => Plan(store, corrupt, [PreparedObject.Unchanged(new ObjectId(1), corrupt, new([3]))]));
        Assert.Throws<InvalidDataException>(() => Plan(store, corrupt, [PreparedObject.Compared(new ObjectId(1), corrupt, new([3]), new(true, [3]))]));
        Assert.Throws<InvalidDataException>(() => Plan(store, corrupt, [PreparedObject.Compared(new ObjectId(1), corrupt, new([3]), new(false, [0]))]));
        PreparedObjectRevision result = Plan(store, corrupt, [PreparedObject.BaseOnlyUpdate(new ObjectId(1), corrupt, new([3]))]);
        ObjectSaveEstimate estimate = Assert.Single(result.Estimates);
        Assert.Equal(ObjectSaveChangeKind.BaseOnlyUpdate, estimate.ChangeKind);
        Assert.Null(estimate.ReconstructionPayloadBytes);
        Assert.Null(estimate.DeltaPayloadBytesUpperBound);
        FrameAddress saved = store.Append(result.Revision);
        Assert.Equal(3L, store.ReadObjectVersionChain(saved, 1).ReconstructionPayloadBytes);
        Assert.Equal(new byte[] { 3 }, store.ReadObjectBaseBody(saved, 1));
    }

    [Fact]
    public void Candidate_stays_bound_to_its_explicit_parent_when_another_branch_is_appended() {
        using SegmentStore segments = NewStore();
        using StateRevisionStore store = new(segments);
        FrameAddress parent = store.Append(StateRevision.CreateObjectHeadMapBase(null, [B(1, new byte[100])], []));
        PreparedObjectRevision result = Plan(store, parent, [PreparedObject.Compared(new ObjectId(1), parent, Base(100), new(true, [2]))]);
        FrameAddress other = store.Append(StateRevision.CreateObjectHeadMapDelta(parent, [B(1, [9])], []));
        FrameAddress saved = store.Append(result.Revision);
        Assert.Equal(parent, store.Read(saved).ParentRevisionAddress);
        Assert.Equal(new[] { parent, saved }, store.ReadObjectVersionChain(saved, 1).Records.Select(x => x.ContainingRevisionAddress));
        Assert.Equal(new byte[] { 9 }, store.ReadObjectBaseBody(other, 1));
    }

    [Fact]
    public void Factories_reject_invalid_ids_addresses_and_missing_contents_without_mutable_shape_flags() {
        FrameAddress valid = new(1, SizedPtr.Create(8, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => PreparedObject.New(new ObjectId(0), new([])));
        Assert.Throws<ArgumentOutOfRangeException>(() => PreparedObject.Unchanged(new ObjectId(1), default, new([])));
        Assert.Throws<ArgumentOutOfRangeException>(() => PreparedObject.Compared(new ObjectId(1), default, new([]), new(true, [])));
        Assert.Throws<ArgumentOutOfRangeException>(() => PreparedObject.BaseOnlyUpdate(new ObjectId(1), default, new([])));
        Assert.Throws<ArgumentNullException>(() => PreparedObject.New(new ObjectId(1), null!));
        Assert.Throws<ArgumentNullException>(() => PreparedObject.Compared(new ObjectId(1), valid, new([]), null!));
        Assert.Equal(ObjectSaveChangeKind.Update, PreparedObject.Compared(new ObjectId(1), valid, new([]), new(true, [])).ChangeKind);
        Assert.Equal(ObjectSaveChangeKind.NoChange, PreparedObject.Compared(new ObjectId(1), valid, new([]), new(false, [0])).ChangeKind);
        Assert.DoesNotContain(typeof(PreparedObject).GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic), p => p.SetMethod is not null);
    }

    private static FrameAddress AppendWrongPrior(SegmentStore segments, FrameAddress parent, FrameAddress wrongPrior) {
        using RbfSegmentWriterLease lease = segments.OpenActiveWriter();
        ArrayBufferWriter<byte> buffer = new();
        BinaryPayloadWriter writer = new(buffer);
        writer.WriteByte(3); // v3
        writer.WriteByte((byte)ObjectHeadMapKind.Delta);
        writer.WriteByte(1); // Parent present
        writer.WriteUInt32(lease.SegmentNumber - parent.FileNumber);
        writer.WriteUInt64(parent.FrameTicket.Serialize());
        writer.WriteUInt32(1);
        writer.WriteUInt32(1);
        writer.WriteByte((byte)ObjectVersionKind.Delta);
        writer.WriteUInt32(lease.SegmentNumber - wrongPrior.FileNumber);
        writer.WriteUInt64(wrongPrior.FrameTicket.Serialize());
        writer.WriteUInt32(1); // Body length
        writer.WriteByte(3);
        writer.WriteUInt32(0); // Removes
        SizedPtr ticket = lease.File.Append(0x52534744, buffer.WrittenSpan).Unwrap();
        return new(lease.SegmentNumber, ticket);
    }

    private static PreparedObjectRevision Plan(StateRevisionStore store, FrameAddress? parent,
        IEnumerable<PreparedObject> rows, int limit = 10, int percent = 25) =>
        ObjectRevisionPlanner.PrepareRevision(store, parent, rows, new(limit, percent));

    private static EncodedBaseObjectBody Base(int length) => new(new byte[length]);
    private static ObjectVersionRecord B(uint id, byte[] body) => ObjectVersionRecord.CreateBase(id, body);
    private static ObjectVersionRecord D(uint id, FrameAddress prior, byte[] body) => ObjectVersionRecord.CreateDelta(id, prior, body);
    private static long Tail(SegmentStore segments) {
        using RbfSegmentWriterLease writer = segments.OpenActiveWriter();
        return writer.File.TailOffset;
    }

    private SegmentStore NewStore(bool rollover = false) {
        string path = Path.GetFullPath(Path.Combine(_temporaryRoot, $"durable-graph-revision-planner-{Guid.NewGuid():N}"));
        _paths.Add(path);
        return SegmentStore.CreateNew(path, new() {
            NewStoreLayout = RbfSegmentStoreLayout.Flat,
            SegmentSizeThresholdBytes = rollover ? 8 : 64 * 1024 * 1024,
        });
    }

    public void Dispose() {
        foreach (string path in _paths) {
            string resolved = Path.GetFullPath(path);
            if (!string.Equals(Path.GetDirectoryName(resolved), Path.TrimEndingDirectorySeparator(_temporaryRoot), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("durable-graph-revision-planner-", StringComparison.Ordinal)) {
                throw new InvalidOperationException("Refusing to delete outside the fixture's temporary directory.");
            }
            if (Directory.Exists(resolved)) {
                Directory.Delete(resolved, recursive: true);
            }
        }
    }
}
