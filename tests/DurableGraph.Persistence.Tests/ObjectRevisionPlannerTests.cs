using System.Collections;

using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Frames = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

// Object planning owns content and policy only. The producer authenticates the
// complete post-live set and actual heads/H; the physical saver owns membership.
public sealed class ObjectRevisionPlannerTests : IDisposable {
    private const string Prefix = "durable-graph-revision-planner-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Initial_rows_produce_owned_canonical_Base_after_inputs_are_changed() {
        byte[] bytes = [10, 20];
        List<PreparedObject> rows = [PreparedObject.New(new(2), new(bytes)), PreparedObject.New(new(1), new([]))];
        var result = Plan(rows, []);
        Assert.Equal(new uint[] { 2, 1 }, rows.Select(static row => row.ObjectId.Value));
        bytes.AsSpan().Fill(99);
        rows.Clear();
        Assert.Equal(new uint[] { 1, 2 }, result.Records.Select(static row => row.ObjectId));
        Assert.Equal(new byte[] { 10, 20 }, result.Records[1].Body.ToArray());
        Assert.Equal(new long[] { 2, 4 }, result.Estimates.Select(static row => row.BasePayloadBytes));
        Assert.All(result.Estimates, static row => Assert.Equal(ObjectSaveChangeKind.Insert, row.ChangeKind));
        Assert.All(result.Records, static row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
        Assert.False(result.RepresentationPlan.Writes is ICollection);

        using Frames frames = Frames.Create(_path);
        using GraphRecordStore store = new(frames);
        StoredGraphRevision saved = store.AppendBusiness(StoredGraphKind.State, null, new(null, new(1), result.Records));
        Assert.Equal(new byte[] { 10, 20 }, Assert.Single(store.ReadObjectChain(saved, 2).Entries).Record.Body.ToArray());
    }

    [Fact]
    public void Compared_classifies_by_HasChanges_even_when_unchanged_payload_is_nonempty() {
        FrameAddress prior = TestAddress();
        var result = Plan([
            PreparedObject.Compared(new(1), prior, Base(100), new(false, [0, 0])),
            PreparedObject.Unchanged(new(2), prior, new([7])),
        ], new() { [new(1)] = 102, [new(2)] = 3 });
        Assert.Empty(result.Records);
        Assert.All(result.Estimates, static row => {
            Assert.Equal(ObjectSaveChangeKind.NoChange, row.ChangeKind);
            Assert.Null(row.DeltaPayloadBytes);
            Assert.NotNull(row.ReconstructionPayloadBytes);
        });
    }

    [Fact]
    public void Mixed_rows_use_actual_P0_heads_and_H_and_preserve_owned_Delta() {
        using Frames frames = Frames.Create(_path);
        using GraphRecordStore store = new(frames);
        StoredGraphRevision first = store.AppendBusiness(StoredGraphKind.State, null,
            new(null, new(1), [B(1, new byte[100]), B(2, [2]), B(3, [3])]));
        StoredGraphRevision state = store.AppendBusiness(StoredGraphKind.State, null,
            new(first.Address, new(1), [D(1, first.ObjectHeads[1], [4])]));
        byte[] deltaBytes = [5];
        var result = Plan([
            PreparedObject.Compared(new(1), state.ObjectHeads[1], Base(100), new(true, deltaBytes)),
            PreparedObject.Unchanged(new(2), state.ObjectHeads[2], new([2])),
            PreparedObject.New(new(4), new([4])),
        ], new() {
            [new(1)] = store.ReadObjectChain(state, 1).ReconstructionPayloadBytes,
            [new(2)] = store.ReadObjectChain(state, 2).ReconstructionPayloadBytes,
        });
        deltaBytes[0] = 99;
        Assert.Equal(new uint[] { 1, 4 }, result.Records.Select(static row => row.ObjectId));
        Assert.Equal(ObjectVersionKind.Delta, result.Records[0].Kind);
        Assert.Equal(state.ObjectHeads[1], result.Records[0].PriorAddress);
        Assert.Equal(new byte[] { 5 }, result.Records[0].Body.ToArray());
        Assert.Equal(store.ReadObjectChain(state, 1).ReconstructionPayloadBytes, result.Estimates[0].ReconstructionPayloadBytes);

        // Membership is supplied by the producer, independently of object writes.
        StoredGraphRevision saved = store.AppendBusiness(StoredGraphKind.State, null,
            new(state.Address, new(1), result.Records, [3]));
        Assert.Equal(new uint[] { 1, 2, 4 }, saved.ObjectHeads.Keys.Order());
        Assert.Equal(first.ObjectHeads[2], saved.ObjectHeads[2]);
        Assert.Equal(3, store.ReadObjectChain(saved, 1).Entries.Count);
        Assert.Equal(new byte[] { 3 }, Assert.Single(store.ReadObjectChain(state, 3).Entries).Record.Body.ToArray());
    }

    [Fact]
    public void Empty_post_live_set_has_no_object_writes_or_estimates() {
        var result = Plan([], new() { [new(1)] = 100 });
        Assert.Empty(result.Records);
        Assert.Empty(result.Estimates);
        Assert.Empty(result.RepresentationPlan.Writes);
    }

    [Fact]
    public void Both_parameters_control_optional_Base_and_input_order_does_not_affect_results() {
        FrameAddress prior = TestAddress();
        PreparedObject[] rows = [PreparedObject.Unchanged(new(1), prior, Base(20)), PreparedObject.Unchanged(new(2), prior, Base(20))];
        Dictionary<ObjectId, long> priorH = new() { [new(1)] = 66, [new(2)] = 66 };
        Assert.Empty(Plan(rows, priorH, 10, 100).Records);
        Assert.Equal(new uint[] { 1 }, Plan(rows, priorH, 1, 1).Records.Select(static row => row.ObjectId));
        var both = Plan(rows, priorH, 1, 100);
        Assert.Equal(new uint[] { 1, 2 }, both.Records.Select(static row => row.ObjectId));
        var reversed = Plan(rows.Reverse(), priorH, 1, 100);
        Assert.Equal(both.Estimates, reversed.Estimates);
        Assert.Equal(both.RepresentationPlan.Writes, reversed.RepresentationPlan.Writes);
        Assert.Equal(both.Records.Select(static row => row.ObjectId), reversed.Records.Select(static row => row.ObjectId));
        Assert.All(both.Records, static row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
    }

    [Fact]
    public void Objects_outside_post_live_set_do_not_inflate_optional_Base_budget() {
        FrameAddress prior = TestAddress();
        var result = Plan([
            PreparedObject.Unchanged(new(1), prior, Base(20)),
            PreparedObject.Unchanged(new(2), prior, Base(20)),
        ], new() { [new(1)] = 66, [new(2)] = 66, [new(3)] = 1002 }, 1, 10);
        Assert.Equal(new uint[] { 1 }, result.Records.Select(static row => row.ObjectId));
        Assert.Equal(44L, result.Estimates.Sum(static row => row.BasePayloadBytes));
    }

    [Fact]
    public void Delta_cost_matches_real_P0_record_with_fixed_opaque_prior_width() {
        using Frames frames = Frames.Create(_path);
        using GraphRecordStore store = new(frames);
        StoredGraphRevision first = store.AppendBusiness(StoredGraphKind.State, null, new(null, new(1), [B(1, [1])]));
        StoredGraphRevision changed = store.AppendBusiness(StoredGraphKind.State, null,
            new(first.Address, new(1), [D(1, first.ObjectHeads[1], [2])]));
        ObjectVersionChain chain = store.ReadObjectChain(changed, 1);
        int actual = chain.Entries[1].PayloadBytes;
        Assert.Equal(FrameAddress.EncodedSize + 3, actual);
        var result = Plan([
            PreparedObject.Compared(new(1), first.ObjectHeads[1], Base(actual - 1), new(true, [2])),
        ], new() { [new(1)] = chain.Entries[0].PayloadBytes }, int.MaxValue, 1);
        ObjectSaveEstimate estimate = Assert.Single(result.Estimates);
        Assert.True(actual < estimate.BasePayloadBytes);
        Assert.Equal(actual, estimate.DeltaPayloadBytes);
        Assert.Equal(ObjectVersionKind.Delta, Assert.Single(result.Records).Kind);
    }

    [Theory]
    [InlineData(false, false, 275, null)]
    [InlineData(false, false, 10000, ObjectVersionKind.Base)]
    [InlineData(false, true, 275, ObjectVersionKind.Base)]
    [InlineData(true, false, 275, ObjectVersionKind.Delta)]
    public void Frozen_text_encodes_its_Base_only_if_selected(bool edit, bool rewrite, long priorH, ObjectVersionKind? expected) {
        ObjectId id = new(1);
        DurableText text = new(new string('a', 130));
        DurableTextState before = text.CaptureState();
        Assert.True(text.TryInstall(text.PrepareAcceptance(before)));
        if (edit) { text.Replace(120, 1, "Ω"); }
        DurableTextState current = text.CaptureState();
        PreparedDeltaBody delta = TextBodyCodec.PrepareDelta(before, current);
        PreparedCapturedObject captured = new(new(id, current), new(id, before), null, delta);
        SchemaCatalogView catalog = new SchemaCatalogWorkspace(static () => { }).Prepare().View;
        byte[] eager = TypedBaseBodyCodec.Encode(ObjectLayout.Text, TextBodyCodec.PrepareBase(current), catalog);
        FrameAddress prior = TestAddress();

        int encodings = 0;
        TextBodyCodec.BaseEncodingObserver = () => encodings++;
        try {
            PreparedTypedBase typed = new TypedBaseBodyCodec.StatePreparation(catalog).Prepare(captured);
            PreparedObject row = ObjectWritePlanner.ClassifyCaptured(captured, typed, prior, rewrite);
            var result = Plan([row], new() { [id] = priorH }, limit: 5, percent: 5);
            long rawEstimate = BareValueEncoding.MeasureVarUInt32((uint)current.Content.Length)
                + current.Content.View.PieceCount + 2L * current.Content.Length;
            Assert.Equal(ObjectVersionPayloadSize.EstimateBasePayloadBytes(3 + rawEstimate),
                Assert.Single(result.Estimates).BasePayloadBytes); // ABI 5 + structural route + Text expression.
            Assert.NotEqual(ObjectVersionPayloadSize.GetBasePayloadBytes(eager.Length),
                Assert.Single(result.Estimates).BasePayloadBytes);
            Assert.Equal(expected == ObjectVersionKind.Base ? 1 : 0, encodings);
            if (expected is null) { Assert.Empty(result.Records); return; }
            ObjectVersionRecord record = Assert.Single(result.Records);
            Assert.Equal(expected.Value, record.Kind);
            if (record.Kind == ObjectVersionKind.Base) {
                Assert.Equal(eager, record.Body.ToArray());
            } else {
                Assert.Equal(prior, record.PriorAddress);
                Assert.Equal(delta.Body.ToArray(), record.Body.ToArray());
            }
        } finally { TextBodyCodec.BaseEncodingObserver = null; }
    }

    [Fact]
    public void Typed_Base_requires_an_allocatable_actual_length_at_the_exact_boundary() {
        const int header = 3;
        Assert.Equal(Array.MaxLength, PreparedTypedBase.EncodedLength(header, Array.MaxLength - header));
        Assert.Throws<InvalidDataException>(() => PreparedTypedBase.EncodedLength(header, Array.MaxLength - header + 1));
        Assert.Throws<InvalidDataException>(() => PreparedTypedBase.EncodedLength(header, int.MaxValue));
    }

    [Fact]
    public void State_preparation_keeps_each_frozen_eager_body_when_layouts_repeat() {
        SchemaCatalogView catalog = new SchemaCatalogWorkspace(static () => { }).Prepare().View;
        TypedBaseBodyCodec.StatePreparation bases = new(catalog);
        PreparedCapturedObject Freeze(string value, uint id) =>
            new(new(new(id), value), null, StringBodyPreparer.PrepareBase(value), null);
        PreparedCapturedObject first = Freeze("one", 1), second = Freeze("two", 2);
        DurableTextState text = new DurableText("many").CaptureState();
        PreparedCapturedObject other = new(new(new(3), text), null, TextBodyCodec.PrepareBase(text), null);
        PreparedTypedBase a = bases.Prepare(first), b = bases.Prepare(other), c = bases.Prepare(second);
        foreach (var (item, prepared) in new[] { (first, a), (other, b), (second, c) }) {
            byte[] expected = TypedBaseBodyCodec.Encode(item.Current.Layout, item.EagerBaseBody!, catalog);
            Assert.Equal(expected.Length, prepared.EstimatedBodyBytes);
            Assert.Equal(expected, prepared.Encode().ToArray());
        }
    }

    [Fact]
    public void Base_estimate_above_the_buffer_length_limit_can_still_select_Delta() {
        long estimate = ObjectVersionPayloadSize.EstimateBasePayloadBytes((long)int.MaxValue + 10);
        Assert.True(estimate > int.MaxValue);
        ObjectSaveEstimate update = new(new(1), ObjectSaveChangeKind.Update, estimate, 30, 200);
        var plan = ReadAmplificationBaseBudgetPolicy.Plan([update], new(5, 5));
        Assert.Equal(ObjectRepresentationMode.Delta, Assert.Single(plan.Writes).Mode);
    }

    [Fact]
    public void DeferredStringMeasuresCanonicalOutputAfterReadingLegalLongerForm() {
        ObjectId id = new(1);
        BareValueReader reader = new(new byte[] { 2, 65, 0 }); // UTF-16LE "A" is legal, but canonical ASCII is shorter.
        ObjectStateRecord current = new(id, reader.ReadString());
        reader.EnsureFullyConsumed();
        SchemaCatalogView catalog = new SchemaCatalogWorkspace(static () => { }).Prepare().View;
        PreparedCapturedObject item = new(current, current, null, null);
        PreparedTypedBase candidate = new TypedBaseBodyCodec.StatePreparation(catalog).Prepare(item);
        byte[] eager = TypedBaseBodyCodec.Encode(current.Layout, StringBodyPreparer.PrepareBase("A"), catalog);
        Assert.Equal(eager.Length, candidate.EstimatedBodyBytes);
        long canonical = ObjectVersionPayloadSize.GetBasePayloadBytes(eager.Length);
        long actualPrior = ObjectVersionPayloadSize.GetBasePayloadBytes(eager.Length + 1);
        PreparedObject row = ObjectWritePlanner.ClassifyCaptured(item, candidate, TestAddress());
        Assert.Empty(Plan([row], new() { [id] = canonical }, limit: 1).Records);
        var result = Plan([row], new() { [id] = actualPrior }, limit: 1);
        Assert.Equal(canonical, Assert.Single(result.Estimates).BasePayloadBytes);
        ObjectVersionRecord rewrite = Assert.Single(result.Records);
        Assert.Equal(ObjectVersionKind.Base, rewrite.Kind);
        Assert.Equal(eager, rewrite.Body.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactListBaseMatchesEagerWhenRewriteIsForcedOrUnchangedDebtIsApproved(bool forceRewrite) {
        ObjectId id = new(1);
        Assert.True(BuiltinStateValues.TryBindCurrent(typeof(int), out StateValueBinding element));
        ListObjectBinding binding = ListObjectBinding.Create(typeof(List<int>), new(element.Slot), element);
        List<int> before = Enumerable.Repeat(0, 123).ToList();
        using CaptureContext capture = new CaptureSession().BeginCapture();
        ObjectStateRecord prior = binding.Capture(id, before, capture);
        if (forceRewrite) { before[7] = short.MaxValue; }
        ObjectStateRecord current = binding.Capture(id, before, capture);
        IStatePreparationBinding writer = (IStatePreparationBinding)binding;
        PreparedCapturedObject candidate = new(current, prior, null, writer.PrepareDelta(prior, current));
        SchemaCatalogView catalog = new SchemaCatalogWorkspace(static () => { }).Prepare().View;
        byte[] expected = TypedBaseBodyCodec.Encode(current.Layout, writer.PrepareBase(current), catalog);
        PreparedTypedBase deferred = new TypedBaseBodyCodec.StatePreparation(catalog).Prepare(candidate);
        Assert.Equal(expected.Length, deferred.EstimatedBodyBytes);
        before.Clear(); // A deferred writer must read the captured array.
        FrameAddress head = TestAddress();
        PreparedObject row = ObjectWritePlanner.ClassifyCaptured(candidate, deferred, head, forceRewrite);
        var result = Plan([row], new() { [id] = 5000 });
        Assert.Equal(forceRewrite ? ObjectSaveChangeKind.BaseOnlyUpdate : ObjectSaveChangeKind.NoChange,
            Assert.Single(result.Estimates).ChangeKind);
        ObjectVersionRecord record = Assert.Single(result.Records);
        Assert.Equal(ObjectVersionKind.Base, record.Kind);
        Assert.Null(record.PriorAddress);
        Assert.Equal(expected, record.Body.ToArray());
    }

    [Fact]
    public void Invalid_rows_and_parameters_are_rejected_by_pure_planning() {
        Assert.Throws<ArgumentException>(() => Plan([PreparedObject.New(new(2), new([2])), PreparedObject.New(new(2), new([2]))], []));
        Assert.Throws<ArgumentException>(() => Plan([null!], []));
        Assert.Throws<ArgumentNullException>(() => Plan(null!, []));
        Assert.Throws<ArgumentNullException>(() => ObjectWritePlanner.PlanObjectWrites([], null!, new(10, 25)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Plan([], [], 0, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => Plan([], [], 3, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Plan([], [], 3, 101));
    }

    [Fact]
    public void Compared_and_unchanged_need_H_while_required_Base_does_not() {
        FrameAddress prior = TestAddress();
        PreparedObject[] needsH = [
            PreparedObject.Unchanged(new(1), prior, new([3])),
            PreparedObject.Compared(new(1), prior, new([3]), new(true, [3])),
            PreparedObject.Compared(new(1), prior, new([3]), new(false, [0])),
        ];
        foreach (PreparedObject row in needsH) {
            Assert.Throws<ArgumentException>(() => Plan([row], []));
            Assert.Throws<ArgumentOutOfRangeException>(() => Plan([row], new() { [new(1)] = -1 }));
        }
        var result = Plan([
            PreparedObject.BaseOnlyUpdate(new(1), prior, new([3])),
            PreparedObject.New(new(2), new([4])),
        ], []);
        Assert.All(result.Estimates, static estimate => {
            Assert.Null(estimate.ReconstructionPayloadBytes);
            Assert.Null(estimate.DeltaPayloadBytes);
        });
        Assert.Equal(ObjectSaveChangeKind.BaseOnlyUpdate, result.Estimates[0].ChangeKind);
        Assert.All(result.Records, static row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
    }

    [Fact]
    public void Candidate_keeps_its_exact_prior_when_another_branch_is_appended() {
        using Frames frames = Frames.Create(_path);
        using GraphRecordStore store = new(frames);
        StoredGraphRevision state = store.AppendBusiness(StoredGraphKind.State, null, new(null, new(1), [B(1, new byte[100])]));
        var result = Plan([
            PreparedObject.Compared(new(1), state.ObjectHeads[1], Base(100), new(true, [2])),
        ], new() { [new(1)] = store.ReadObjectChain(state, 1).ReconstructionPayloadBytes });
        StoredGraphRevision other = store.AppendBusiness(StoredGraphKind.State, null, new(state.Address, new(1), [B(1, [9])]));
        StoredGraphRevision saved = store.AppendBusiness(StoredGraphKind.State, null, new(state.Address, new(1), result.Records));

        Assert.Equal(new[] { state.OwnedBatch!.Value, saved.OwnedBatch!.Value },
            store.ReadObjectChain(saved, 1).Entries.Select(static row => row.ContainingBatch));
        Assert.Equal(new byte[] { 9 }, Assert.Single(store.ReadObjectChain(other, 1).Entries).Record.Body.ToArray());
    }

    [Fact]
    public void Factories_reject_invalid_ids_addresses_and_missing_contents_without_mutable_shape_flags() {
        FrameAddress valid = TestAddress();
        Assert.Throws<ArgumentOutOfRangeException>(() => PreparedObject.New(new(0), new([])));
        Assert.Throws<ArgumentOutOfRangeException>(() => PreparedObject.Unchanged(new(1), default, new([])));
        Assert.Throws<ArgumentOutOfRangeException>(() => PreparedObject.Compared(new(1), default, new([]), new(true, [])));
        Assert.Throws<ArgumentOutOfRangeException>(() => PreparedObject.BaseOnlyUpdate(new(1), default, new([])));
        Assert.Throws<ArgumentNullException>(() => PreparedObject.New(new(1), null!));
        Assert.Throws<ArgumentNullException>(() => PreparedObject.Compared(new(1), valid, new([]), null!));
        Assert.Equal(ObjectSaveChangeKind.Update, PreparedObject.Compared(new(1), valid, new([]), new(true, [])).ChangeKind);
        Assert.Equal(ObjectSaveChangeKind.NoChange, PreparedObject.Compared(new(1), valid, new([]), new(false, [0])).ChangeKind);
        Assert.DoesNotContain(typeof(PreparedObject).GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic), p => p.SetMethod is not null);
    }

    private static (ObjectVersionRecord[] Records, ObjectSaveEstimate[] Estimates, ObjectRepresentationPlan RepresentationPlan)
        Plan(IEnumerable<PreparedObject> rows, Dictionary<ObjectId, long> priorH, int limit = 10, int percent = 25) =>
        ObjectWritePlanner.PlanObjectWrites(rows, priorH, new(limit, percent));

    private static PreparedTypedBase Base(int length) => new(new byte[length]);
    private static ObjectVersionRecord B(uint id, byte[] body) => ObjectVersionRecord.CreateBase(id, body);
    private static ObjectVersionRecord D(uint id, FrameAddress prior, byte[] body) => ObjectVersionRecord.CreateDelta(id, prior, body);
    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
