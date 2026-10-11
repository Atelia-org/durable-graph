using System.Collections;
using Atelia.FrameStore;
using static Atelia.DurableGraph.Storage.Tests.StorageTestFrames;

namespace Atelia.DurableGraph.Storage.Tests;

public sealed class StateRevisionTests {
    private static readonly FrameAddress Parent = Address(1, 32);

    [Fact]
    public void Head_map_Base_freezes_canonical_records_and_external_collections() {
        ObjectVersionRecord nine = ObjectVersionRecord.CreateBase(9, [90]);
        ObjectVersionRecord one = ObjectVersionRecord.CreateBase(1, [10]);
        List<ObjectVersionRecord> records = [nine, one];
        Dictionary<uint, FrameAddress> external = new() {
            [8] = Address(1, 64),
            [2] = Address(1, 96),
        };

        StateRevision revision = StateRevision.CreateObjectHeadMapBase(Parent, records, external);
        records.Clear();
        external.Clear();

        Assert.Equal(ObjectHeadMapKind.Base, revision.ObjectHeadMapKind);
        Assert.Equal([1u, 9u], revision.LocalObjectIds);
        Assert.Equal([one, nine], revision.LocalObjects);
        Assert.Same(one, revision.LocalObjects[0]);
        Assert.Equal([2u, 8u], revision.ExternalObjectHeads.Keys);
        Assert.Equal([Address(1, 96), Address(1, 64)], revision.ExternalObjectHeads.Values);
        Assert.Empty(revision.RemovedObjectIds);
    }

    [Fact]
    public void Head_map_Delta_freezes_canonical_removed_ids_and_requires_parent() {
        List<uint> removed = [9, 2];
        StateRevision revision = StateRevision.CreateObjectHeadMapDelta(Parent, [ObjectVersionRecord.CreateBase(3, [30]), ObjectVersionRecord.CreateBase(7, [])], removed);
        removed.Clear();

        Assert.Equal(ObjectHeadMapKind.Delta, revision.ObjectHeadMapKind);
        Assert.Equal(Parent, revision.ParentRevisionAddress);
        Assert.Equal([3u, 7u], revision.LocalObjectIds);
        Assert.Equal([2u, 9u], revision.RemovedObjectIds);
        Assert.Empty(revision.ExternalObjectHeads);
        Assert.Throws<ArgumentOutOfRangeException>(() => StateRevision.CreateObjectHeadMapDelta(default, [], []));
    }

    [Fact]
    public void Collections_do_not_expose_mutable_interfaces_or_SyncRoot_backing() {
        StateRevision revision = StateRevision.CreateObjectHeadMapBase(Parent, [ObjectVersionRecord.CreateBase(3, [30])], [new(4, Parent)]);
        StateRevision delta = StateRevision.CreateObjectHeadMapDelta(Parent, [], [3]);
        object[] collections = [
            revision.LocalObjects, revision.LocalObjectIds, revision.ExternalObjectHeads,
            revision.ExternalObjectHeads.Keys, revision.ExternalObjectHeads.Values,
            delta.RemovedObjectIds,
        ];
        foreach (object collection in collections) {
            Assert.False(collection is ICollection);
            Assert.False(collection is IList);
            Assert.False(collection is IDictionary);
        }

        Assert.False(revision.LocalObjects is ICollection<ObjectVersionRecord>);
        Assert.False(revision.LocalObjectIds is ICollection<uint>);
        Assert.False(revision.ExternalObjectHeads is IDictionary<uint, FrameAddress>);
        Assert.False(delta.RemovedObjectIds is ICollection<uint>);
        ObjectVersionRecord[] returnedCopy = revision.LocalObjects.ToArray();
        returnedCopy[0] = ObjectVersionRecord.CreateBase(99, [99]);
        Assert.Equal(3u, revision.LocalObjects[0].ObjectId);
        Assert.Equal([3u], revision.LocalObjectIds);
    }

    [Fact]
    public void Null_collections_and_null_records_are_rejected() {
        Assert.Throws<ArgumentNullException>(() => StateRevision.CreateObjectHeadMapBase(null, null!, []));
        Assert.Throws<ArgumentNullException>(() => StateRevision.CreateObjectHeadMapBase(null, [], null!));
        Assert.Throws<ArgumentNullException>(() => StateRevision.CreateObjectHeadMapDelta(Parent, null!, []));
        Assert.Throws<ArgumentNullException>(() => StateRevision.CreateObjectHeadMapDelta(Parent, [], null!));
        Assert.Throws<ArgumentException>(() => StateRevision.CreateObjectHeadMapBase(null, [null!], []));
        Assert.Throws<ArgumentException>(() => StateRevision.CreateObjectHeadMapDelta(Parent, [ObjectVersionRecord.CreateBase(1, []), null!], []));
    }

    [Theory]
    [MemberData(nameof(InvalidObjectIdCollections))]
    public void ObjectId_collections_reject_zero_and_duplicates(Func<StateRevision> create) {
        Assert.ThrowsAny<ArgumentException>(() => create());
    }

    public static TheoryData<Func<StateRevision>> InvalidObjectIdCollections => new() {
        () => StateRevision.CreateObjectHeadMapBase(null, [ObjectVersionRecord.CreateBase(1, []), ObjectVersionRecord.CreateBase(1, [2])], []),
        () => StateRevision.CreateObjectHeadMapDelta(Parent, [], [0]),
        () => StateRevision.CreateObjectHeadMapDelta(Parent, [], [1, 1]),
        () => StateRevision.CreateObjectHeadMapBase(null, [], [new(0, Address(1, 32))]),
    };

    [Fact]
    public void Local_ids_must_not_overlap_membership_entries() {
        Assert.Throws<ArgumentException>(() =>
            StateRevision.CreateObjectHeadMapBase(null, [ObjectVersionRecord.CreateBase(1, [])], [new(1, Address(1, 32))]));
        Assert.Throws<ArgumentException>(() => StateRevision.CreateObjectHeadMapDelta(Parent, [ObjectVersionRecord.CreateBase(1, [])], [1]));
    }

    [Fact]
    public void External_heads_reject_duplicate_ids_and_empty_addresses() {
        Assert.Throws<ArgumentException>(() =>
            StateRevision.CreateObjectHeadMapBase(null, [], [new(1, Address(1, 32)), new(1, Address(1, 64))]));
        Assert.Throws<ArgumentOutOfRangeException>(() => StateRevision.CreateObjectHeadMapBase(null, [], [new(1, default)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => StateRevision.CreateObjectHeadMapBase(default(FrameAddress), [], []));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Both_map_kinds_accept_mixed_object_representations_and_reject_conflicts(bool mapDelta) {
        ObjectVersionRecord full = ObjectVersionRecord.CreateBase(9, [90]);
        ObjectVersionRecord delta = ObjectVersionRecord.CreateDelta(1, Parent, [10]);
        List<ObjectVersionRecord> records = [full, delta];
        StateRevision revision = mapDelta
            ? StateRevision.CreateObjectHeadMapDelta(Parent, records, [])
            : StateRevision.CreateObjectHeadMapBase(Parent, records, []);
        records.Clear();
        Assert.Equal([1u, 9u], revision.LocalObjectIds);
        Assert.Equal([delta, full], revision.LocalObjects);
        Assert.Equal(ObjectVersionKind.Delta, revision.LocalObjects[0].Kind);
        Assert.Equal(Parent, revision.LocalObjects[0].PriorAddress);
        Assert.Throws<ArgumentException>(() => mapDelta
            ? StateRevision.CreateObjectHeadMapDelta(Parent, [delta, ObjectVersionRecord.CreateBase(1, [])], [])
            : StateRevision.CreateObjectHeadMapBase(Parent, [delta, ObjectVersionRecord.CreateBase(1, [])], []));
        Assert.Throws<ArgumentException>(() => mapDelta
            ? StateRevision.CreateObjectHeadMapDelta(Parent, [delta], [1])
            : StateRevision.CreateObjectHeadMapBase(Parent, [delta], [new(1, Parent)]));
    }

    [Fact]
    public void Local_Delta_requires_parent_even_in_complete_head_map() {
        Assert.Throws<ArgumentException>(() => StateRevision.CreateObjectHeadMapBase(
            null, [ObjectVersionRecord.CreateDelta(1, Parent, [])], []));
    }

}
