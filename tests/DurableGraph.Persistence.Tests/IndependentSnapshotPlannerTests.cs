using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using SegmentStore = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class IndependentSnapshotPlannerTests : IDisposable {
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"durable-graph-snapshot-planner-{Guid.NewGuid():N}");

    [Fact]
    public void Snapshot_keeps_exact_reused_heads_and_local_Delta_without_State_Removes() {
        using SegmentStore segments = NewStore();
        using StateRevisionStore store = new(segments);
        FrameAddress first = store.Append(StateRevision.CreateObjectHeadMapBase(null, [
            ObjectVersionRecord.CreateBase(1, new byte[100]),
            ObjectVersionRecord.CreateBase(2, [2]),
            ObjectVersionRecord.CreateBase(3, [3]),
        ], []));
        FrameAddress state = store.Append(StateRevision.CreateObjectHeadMapDelta(first,
            [ObjectVersionRecord.CreateDelta(1, first, [4])], []));
        PreparedObject[] rows = [
            PreparedObject.Compared(new(1), state, new(new byte[100]), new(true, [5])),
            PreparedObject.Unchanged(new(2), first, new([2])),
            PreparedObject.New(new(4), new([4])),
        ];

        PreparedObjectRevision snapshot = ObjectRevisionPlanner.PrepareRevision(store, state, rows, new(10, 25), independentSnapshot: true);
        PreparedObjectRevision advancing = ObjectRevisionPlanner.PrepareRevision(store, state, rows, new(10, 25));

        Assert.Equal(advancing.Estimates, snapshot.Estimates);
        Assert.Equal(advancing.RepresentationPlan.Writes, snapshot.RepresentationPlan.Writes);
        Assert.Equal(ObjectHeadMapKind.Base, snapshot.Revision.ObjectHeadMapKind);
        Assert.Equal(state, snapshot.Revision.ParentRevisionAddress);
        Assert.Empty(snapshot.Revision.RemovedObjectIds);
        Assert.Equal(new uint[] { 1, 4 }, snapshot.Revision.LocalObjectIds);
        Assert.Equal(new KeyValuePair<uint, FrameAddress>(2, first), Assert.Single(snapshot.Revision.ExternalObjectHeads));
        Assert.Equal(ObjectVersionKind.Delta, snapshot.Revision.LocalObjects[0].Kind);
        Assert.Equal(state, snapshot.Revision.LocalObjects[0].PriorAddress);
        Assert.Equal(new byte[] { 5 }, snapshot.Revision.LocalObjects[0].Body.ToArray());
        Assert.Equal(ObjectHeadMapKind.Delta, advancing.Revision.ObjectHeadMapKind);
        Assert.Equal(new uint[] { 3 }, advancing.Revision.RemovedObjectIds);

        FrameAddress saved = store.Append(snapshot.Revision);
        Assert.Equal(new uint[] { 1, 2, 4 }, store.ReadLiveObjectHeadMap(saved).Keys.Order());
        Assert.Equal(first, store.ReadLiveObjectHeadMap(saved)[2]);
        Assert.Equal(new[] { first, state, saved },
            store.ReadObjectVersionChain(saved, 1).Records.Select(static row => row.ContainingRevisionAddress));
        Assert.Equal(new uint[] { 1, 2, 3 }, store.ReadLiveObjectHeadMap(state).Keys.Order());
    }

    [Fact]
    public void NoChange_optional_Base_is_local_and_only_unwritten_candidates_are_external() {
        using SegmentStore segments = NewStore();
        using StateRevisionStore store = new(segments);
        FrameAddress first = store.Append(StateRevision.CreateObjectHeadMapBase(null, [
            ObjectVersionRecord.CreateBase(1, new byte[20]),
            ObjectVersionRecord.CreateBase(2, new byte[20]),
            ObjectVersionRecord.CreateBase(3, new byte[1000]),
        ], []));
        FrameAddress state = store.Append(StateRevision.CreateObjectHeadMapDelta(first, [
            ObjectVersionRecord.CreateDelta(1, first, new byte[30]),
            ObjectVersionRecord.CreateDelta(2, first, new byte[30]),
        ], []));
        PreparedObjectRevision snapshot = ObjectRevisionPlanner.PrepareRevision(store, state, [
            PreparedObject.Unchanged(new(1), state, new(new byte[20])),
            PreparedObject.Unchanged(new(2), state, new(new byte[20])),
        ], new(1, 1), independentSnapshot: true);

        Assert.All(snapshot.Estimates, static row => Assert.Equal(ObjectSaveChangeKind.NoChange, row.ChangeKind));
        Assert.Equal(44L, snapshot.Estimates.Sum(static row => row.BasePayloadBytes));
        ObjectVersionRecord rebased = Assert.Single(snapshot.Revision.LocalObjects);
        Assert.Equal(1U, rebased.ObjectId);
        Assert.Equal(ObjectVersionKind.Base, rebased.Kind);
        Assert.Equal(new KeyValuePair<uint, FrameAddress>(2, state), Assert.Single(snapshot.Revision.ExternalObjectHeads));
        Assert.Empty(snapshot.Revision.RemovedObjectIds);

        FrameAddress saved = store.Append(snapshot.Revision);
        Assert.Equal(new uint[] { 1, 2 }, store.ReadLiveObjectHeadMap(saved).Keys.Order());
        Assert.Single(store.ReadObjectVersionChain(saved, 1).Records);
        Assert.Equal(2, store.ReadObjectVersionChain(saved, 2).Records.Count);
    }

    [Fact]
    public void Empty_snapshot_has_empty_membership_and_preserves_its_parent() {
        using SegmentStore segments = NewStore();
        using StateRevisionStore store = new(segments);
        FrameAddress state = store.Append(StateRevision.CreateObjectHeadMapBase(null,
            [ObjectVersionRecord.CreateBase(1, [1])], []));
        PreparedObjectRevision snapshot = ObjectRevisionPlanner.PrepareRevision(store, state, [], new(10, 25), independentSnapshot: true);
        Assert.Equal(state, snapshot.Revision.ParentRevisionAddress);
        Assert.Empty(snapshot.Revision.LocalObjects);
        Assert.Empty(snapshot.Revision.ExternalObjectHeads);
        Assert.Empty(snapshot.Revision.RemovedObjectIds);
        Assert.Empty(store.ReadLiveObjectHeadMap(store.Append(snapshot.Revision)));
        Assert.Single(store.ReadLiveObjectHeadMap(state));
    }

    [Fact]
    public void Snapshot_requires_State_baseline_and_validates_exact_prior_for_reused_objects() {
        using SegmentStore segments = NewStore();
        using StateRevisionStore store = new(segments);
        Assert.Throws<ArgumentException>(() => ObjectRevisionPlanner.PrepareRevision(store, null,
            [PreparedObject.New(new(1), new([1]))], new(10, 25), independentSnapshot: true));
        FrameAddress first = store.Append(StateRevision.CreateObjectHeadMapBase(null,
            [ObjectVersionRecord.CreateBase(1, [1])], []));
        FrameAddress state = store.Append(StateRevision.CreateObjectHeadMapDelta(first,
            [ObjectVersionRecord.CreateBase(1, [2])], []));
        Assert.Throws<ArgumentException>(() => ObjectRevisionPlanner.PrepareRevision(store, state,
            [PreparedObject.Unchanged(new(1), first, new([2]))], new(10, 25), independentSnapshot: true));
    }

    private SegmentStore NewStore() => SegmentStore.Create(_path);

    public void Dispose() {
        string resolved = Path.GetFullPath(_path);
        if (!string.Equals(Path.GetDirectoryName(resolved), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("durable-graph-snapshot-planner-", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Refusing to delete outside the fixture's temporary directory.");
        }
        if (Directory.Exists(resolved)) {
            Directory.Delete(resolved, recursive: true);
        }
    }
}
