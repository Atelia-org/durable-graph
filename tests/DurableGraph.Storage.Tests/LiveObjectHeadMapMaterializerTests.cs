using Atelia.FrameStore;
using static Atelia.DurableGraph.Storage.Tests.StorageTestFrames;

namespace Atelia.DurableGraph.Storage.Tests;

public sealed class LiveObjectHeadMapMaterializerTests {
    [Fact]
    public void Base_binds_local_records_to_self_and_preserves_external_heads() {
        FrameAddress parent = Address(1, 32);
        FrameAddress externalHead = Address(1, 96);
        FrameAddress checkpoint = Address(2, 32);
        StateRevision revision = StateRevision.CreateObjectHeadMapBase(
            parent,
            localObjects: Records(3, 1),
            externalObjectHeads: [
                new KeyValuePair<uint, FrameAddress>(2, externalHead),
            ]);

        IReadOnlyDictionary<uint, FrameAddress> heads =
            LiveObjectHeadMapMaterializer.Materialize(
                checkpoint,
                address => address == checkpoint
                    ? revision
                    : throw new KeyNotFoundException());

        AssertHeads(
            heads,
            (1, checkpoint),
            (2, externalHead),
            (3, checkpoint));
    }

    [Fact]
    public void Delta_inherits_updates_adds_and_removes_against_exact_parent() {
        FrameAddress f1 = Address(1, 32);
        FrameAddress f2 = Address(1, 96);
        Dictionary<FrameAddress, StateRevision> revisions = new() {
            [f1] = StateRevision.CreateObjectHeadMapBase(
                null,
                localObjects: Records(1, 2, 3),
                externalObjectHeads: []),
            [f2] = StateRevision.CreateObjectHeadMapDelta(
                f1,
                localObjects: Records(4, 1),
                removedObjectIds: [2]),
        };

        IReadOnlyDictionary<uint, FrameAddress> heads =
            LiveObjectHeadMapMaterializer.Materialize(
                f2,
                revisions.GetValueOrDefault!);

        AssertHeads(heads, (1, f2), (3, f1), (4, f2));
    }

    [Fact]
    public void Empty_Delta_preserves_every_inherited_head() {
        FrameAddress externalHead = Address(1, 32);
        FrameAddress checkpoint = Address(2, 32);
        FrameAddress noChange = Address(2, 96);
        Dictionary<FrameAddress, StateRevision> revisions = new() {
            [checkpoint] = StateRevision.CreateObjectHeadMapBase(
                externalHead,
                localObjects: Records(2),
                externalObjectHeads: [
                    new KeyValuePair<uint, FrameAddress>(1, externalHead),
                ]),
            [noChange] = StateRevision.CreateObjectHeadMapDelta(
                checkpoint,
                localObjects: Records(),
                removedObjectIds: []),
        };

        IReadOnlyDictionary<uint, FrameAddress> heads =
            LiveObjectHeadMapMaterializer.Materialize(
                noChange,
                revisions.GetValueOrDefault!);

        AssertHeads(heads, (1, externalHead), (2, checkpoint));
    }

    [Fact]
    public void Repeated_local_overwrite_moves_only_that_Object_head() {
        FrameAddress f1 = Address(1, 32);
        FrameAddress f2 = Address(1, 96);
        FrameAddress f3 = Address(1, 160);
        Dictionary<FrameAddress, StateRevision> revisions = new() {
            [f1] = StateRevision.CreateObjectHeadMapBase(null, Records(1, 2), []),
            [f2] = StateRevision.CreateObjectHeadMapDelta(f1, Records(1), []),
            [f3] = StateRevision.CreateObjectHeadMapDelta(f2, Records(1), []),
        };

        IReadOnlyDictionary<uint, FrameAddress> heads =
            LiveObjectHeadMapMaterializer.Materialize(
                f3,
                revisions.GetValueOrDefault!);

        AssertHeads(heads, (1, f3), (2, f1));
    }

    [Fact]
    public void Local_occurrence_after_Remove_makes_the_Object_live_again() {
        FrameAddress f1 = Address(1, 32);
        FrameAddress f2 = Address(1, 96);
        FrameAddress f3 = Address(1, 160);
        Dictionary<FrameAddress, StateRevision> revisions = new() {
            [f1] = StateRevision.CreateObjectHeadMapBase(null, Records(1, 2), []),
            [f2] = StateRevision.CreateObjectHeadMapDelta(f1, Records(), [1]),
            [f3] = StateRevision.CreateObjectHeadMapDelta(f2, Records(1), []),
        };

        IReadOnlyDictionary<uint, FrameAddress> heads =
            LiveObjectHeadMapMaterializer.Materialize(
                f3,
                revisions.GetValueOrDefault!);

        AssertHeads(heads, (1, f3), (2, f1));
    }

    [Fact]
    public void Non_genesis_Base_is_complete_and_does_not_read_its_parent() {
        FrameAddress missingParent = Address(1, 32);
        FrameAddress checkpoint = Address(2, 32);
        StateRevision revision = StateRevision.CreateObjectHeadMapBase(
            missingParent,
            localObjects: Records(3, 1),
            externalObjectHeads: [
                new KeyValuePair<uint, FrameAddress>(2, Address(1, 96)),
            ]);
        List<FrameAddress> reads = [];

        IReadOnlyDictionary<uint, FrameAddress> heads =
            LiveObjectHeadMapMaterializer.Materialize(
                checkpoint,
                address => {
                    reads.Add(address);
                    return address == checkpoint
                        ? revision
                        : throw new KeyNotFoundException();
                });

        AssertHeads(
            heads,
            (1, checkpoint),
            (2, Address(1, 96)),
            (3, checkpoint));
        Assert.Equal([checkpoint], reads);
    }

    [Fact]
    public void Result_enumerates_in_ObjectId_order_and_is_immutable() {
        FrameAddress head = Address(1, 32);
        StateRevision revision = StateRevision.CreateObjectHeadMapBase(
            null,
            localObjects: Records(3, 1, 2),
            externalObjectHeads: []);

        IReadOnlyDictionary<uint, FrameAddress> heads =
            LiveObjectHeadMapMaterializer.Materialize(
                head,
                address => address == head
                    ? revision
                    : throw new KeyNotFoundException());

        Assert.Equal([1u, 2u, 3u], heads.Keys);
        Assert.Equal([head, head, head], heads.Values);
        Assert.False(heads is IDictionary<uint, FrameAddress>);
        Assert.False(heads.Keys is ICollection<uint>);
        Assert.False(heads.Values is ICollection<FrameAddress>);
        foreach (object projection in new object[] { heads, heads.Keys, heads.Values }) {
            Assert.False(projection is System.Collections.ICollection);
        }

        KeyValuePair<uint, FrameAddress>[] copy = heads.ToArray();
        copy[0] = new(99, default);
        AssertHeads(heads, (1, head), (2, head), (3, head));
    }

    [Fact]
    public void Address_numeric_order_does_not_constrain_an_acyclic_parent() {
        FrameAddress f1 = Address(1, 32);
        FrameAddress f2 = Address(1, 96);
        Dictionary<FrameAddress, StateRevision> revisions = new() {
            [f1] = StateRevision.CreateObjectHeadMapDelta(f2, Records(), []),
            [f2] = StateRevision.CreateObjectHeadMapBase(null, Records(1), []),
        };

        AssertHeads(LiveObjectHeadMapMaterializer.Materialize(f1, revisions.GetValueOrDefault!), (1, f2));
    }

    [Fact]
    public void Parent_cycle_fails_closed_without_numeric_ordering() {
        FrameAddress f1 = Address(1, 32);
        FrameAddress f2 = Address(1, 96);
        Dictionary<FrameAddress, StateRevision> revisions = new() {
            [f1] = StateRevision.CreateObjectHeadMapDelta(f2, Records(), []),
            [f2] = StateRevision.CreateObjectHeadMapDelta(f1, Records(), []),
        };

        Assert.Throws<InvalidDataException>(() =>
            LiveObjectHeadMapMaterializer.Materialize(
                f2,
                revisions.GetValueOrDefault!));
    }

    private static void AssertHeads(
        IReadOnlyDictionary<uint, FrameAddress> actual,
        params (uint ObjectId, FrameAddress Head)[] expected) {
        Assert.Equal(
            expected,
            actual.Select(pair => (pair.Key, pair.Value)).ToArray());
    }

    private static ObjectVersionRecord[] Records(params uint[] ids) =>
        ids.Select(id => ObjectVersionRecord.CreateBase(id, [(byte)id])).ToArray();

}
