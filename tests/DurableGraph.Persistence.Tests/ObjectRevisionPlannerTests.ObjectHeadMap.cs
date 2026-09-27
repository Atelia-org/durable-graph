using System.Buffers;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed partial class ObjectRevisionPlannerTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Large_removal_uses_map_Base_with_local_Delta_and_external_heads(bool rollover) {
        using SegmentStore segments = NewStore(rollover);
        using StateRevisionStore store = new(segments);
        FrameAddress first = store.Append(StateRevision.CreateObjectHeadMapBase(null,
            Enumerable.Range(1, 1000).Select(id => B((uint)id, new byte[100])), []));
        FrameAddress parent = store.Append(StateRevision.CreateObjectHeadMapDelta(first, [D(1, first, [4])], []));
        PreparedObject[] rows = [
            PreparedObject.Compared(new(1), parent, Base(100), new(true, [5])),
            PreparedObject.Unchanged(new(2), first, Base(100)),
            PreparedObject.New(new(1001), new([8])),
        ];
        PreparedObjectRevision result = Plan(store, parent, rows);
        Assert.Equal(ObjectHeadMapKind.Base, result.Revision.ObjectHeadMapKind);
        Assert.Equal(parent, result.Revision.ParentRevisionAddress);
        Assert.Empty(result.Revision.RemovedObjectIds);
        Assert.Equal(new uint[] { 1, 1001 }, result.Revision.LocalObjectIds);
        Assert.Equal(ObjectVersionKind.Delta, result.Revision.LocalObjects[0].Kind);
        Assert.Equal(parent, result.Revision.LocalObjects[0].PriorAddress);
        Assert.Equal(new KeyValuePair<uint, FrameAddress>(2, first), Assert.Single(result.Revision.ExternalObjectHeads));

        // Same object records, different membership encoding: compare real wire bytes.
        StateRevision mapDelta = StateRevision.CreateObjectHeadMapDelta(parent, result.Revision.LocalObjects,
            Enumerable.Range(3, 998).Select(id => (uint)id));
        FrameAddress deltaAt = store.Append(mapDelta);
        FrameAddress baseAt = store.Append(result.Revision);
        Assert.True(PayloadLength(segments, baseAt) < PayloadLength(segments, deltaAt));
        Assert.Equal(store.ReadLiveObjectHeadMap(deltaAt).Keys, store.ReadLiveObjectHeadMap(baseAt).Keys);
        Assert.Equal(first, store.ReadLiveObjectHeadMap(baseAt)[2]);
        Assert.Equal(new[] { first, parent, baseAt },
            store.ReadObjectVersionChain(baseAt, 1).Records.Select(row => row.ContainingRevisionAddress));
        Assert.Equal(1000, store.ReadLiveObjectHeadMap(parent).Count);
        if (rollover) { Assert.True(baseAt.FileNumber > parent.FileNumber); }

        PreparedObjectRevision next = Plan(store, baseAt, [
            PreparedObject.Compared(new(1), baseAt, Base(100), new(true, [6])),
            PreparedObject.Unchanged(new(2), first, Base(100)),
            PreparedObject.Unchanged(new(1001), baseAt, new([8])),
        ]);
        Assert.Equal(ObjectHeadMapKind.Delta, next.Revision.ObjectHeadMapKind);
        FrameAddress nextAt = store.Append(next.Revision);
        Assert.Equal(4, store.ReadObjectVersionChain(nextAt, 1).Records.Count);
    }

    [Theory]
    [InlineData(-1, ObjectHeadMapKind.Delta)]
    [InlineData(0, ObjectHeadMapKind.Delta)]
    [InlineData(1, ObjectHeadMapKind.Base)]
    public void Selection_requires_strict_saving_against_worst_case_file_distance(int extraRemoved, ObjectHeadMapKind expected) {
        using SegmentStore segments = NewStore();
        using StateRevisionStore store = new(segments);
        FrameAddress parent = store.Append(StateRevision.CreateObjectHeadMapBase(null,
            Enumerable.Range(1, 100).Select(id => B((uint)id, [1])), []));
        // All IDs and counts fit one byte. Measure the Base suffix with the actual
        // primitive writer, independently of the planner's varint size calculation.
        ArrayBufferWriter<byte> upper = new();
        BinaryPayloadWriter writer = new(upper);
        writer.WriteUInt32(1); // One external head.
        writer.WriteUInt32(1); // Its ObjectId.
        writer.WriteUInt32(uint.MaxValue); // Maximum encoded backward file distance.
        writer.WriteUInt64(parent.FrameTicket.Serialize());
        int removedCount = upper.WrittenCount - 1 + extraRemoved;
        var rows = new List<PreparedObject> { PreparedObject.Unchanged(new(1), parent, new([1])) };
        rows.AddRange(Enumerable.Range(2, 99 - removedCount)
            .Select(id => PreparedObject.BaseOnlyUpdate(new((uint)id), parent, new([2]))));
        PreparedObjectRevision result = Plan(store, parent, rows);
        Assert.Equal(expected, result.Revision.ObjectHeadMapKind);
        FrameAddress saved = store.Append(result.Revision);
        Assert.Equal(100 - removedCount, store.ReadLiveObjectHeadMap(saved).Count);
        Assert.Equal(parent, store.ReadLiveObjectHeadMap(saved)[1]);
    }

    [Theory]
    [InlineData(127)]
    [InlineData(128)]
    public void Map_selection_accounts_for_multibyte_ids_and_count_prefixes(int survivors) {
        using SegmentStore segments = NewStore();
        using StateRevisionStore store = new(segments);
        uint[] ids = [127, 128, 16383, 16384, 2097151, 2097152, 268435455, 268435456, uint.MaxValue,
            .. Enumerable.Range(1, 991).Select(id => (uint)(id * 65537))];
        Array.Sort(ids);
        FrameAddress parent = store.Append(StateRevision.CreateObjectHeadMapBase(null, ids.Select(id => B(id, [1])), []));
        // Retain the largest IDs, including UInt32.MaxValue, as external heads.
        uint[] kept = ids.TakeLast(survivors).ToArray();
        PreparedObjectRevision result = Plan(store, parent,
            kept.Select(id => PreparedObject.Unchanged(new(id), parent, new([1]))));
        Assert.Equal(ObjectHeadMapKind.Base, result.Revision.ObjectHeadMapKind);
        Assert.Equal(kept, result.Revision.ExternalObjectHeads.Keys);
        StateRevision delta = StateRevision.CreateObjectHeadMapDelta(parent, [], ids.Except(kept));
        FrameAddress deltaAt = store.Append(delta);
        FrameAddress baseAt = store.Append(result.Revision);
        Assert.True(PayloadLength(segments, baseAt) < PayloadLength(segments, deltaAt));
        Assert.Equal(kept, store.ReadLiveObjectHeadMap(baseAt).Keys);
    }

    [Fact]
    public void No_removals_keeps_Delta_even_when_all_objects_are_local() {
        using SegmentStore segments = NewStore();
        using StateRevisionStore store = new(segments);
        FrameAddress parent = store.Append(StateRevision.CreateObjectHeadMapBase(null, [B(1, [1])], []));
        PreparedObjectRevision result = Plan(store, parent, [PreparedObject.BaseOnlyUpdate(new(1), parent, new([2]))]);
        Assert.Equal(ObjectHeadMapKind.Delta, result.Revision.ObjectHeadMapKind);
        Assert.Empty(result.Revision.RemovedObjectIds);
    }

    private static int PayloadLength(SegmentStore segments, FrameAddress address) {
        using RbfSegmentReaderLease reader = segments.OpenReader(address.FileNumber);
        using RbfPooledFrame frame = reader.File.ReadPooledFrame(address.FrameTicket).Unwrap();
        return frame.PayloadAndMeta.Length;
    }
}
