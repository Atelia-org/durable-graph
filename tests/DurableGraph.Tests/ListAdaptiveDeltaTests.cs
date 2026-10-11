using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed class ListAdaptiveDeltaTests {
    private static readonly ListLayout IntLayout = new(new(1, TypeTag.Int32));

    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    public void NoChangeStopsAfterTheExactSequenceComparison(int count) {
        FrozenListState<int> prior = new(Enumerable.Range(0, count).ToArray());
        FrozenListState<int> current = new(prior.Elements);
        CountingOps.Reset();
        PreparedDeltaBody result = ListStateBody<int, CountingOps>.PrepareDeltaObserved(prior, current, IntLayout,
            out ListDeltaCompetitionObservation observation, planFactory: (_, _, _) => throw new InvalidOperationException("NoChange called the matcher."));
        Assert.False(result.HasChanges);
        Assert.Equal(ListDeltaCompetitionOutcome.NoChange, observation.Outcome);
        Assert.False(observation.Triggered);
        Assert.Equal(count, CountingOps.EqualCalls);
        Assert.Equal(0, CountingOps.BaseCalls);
        Assert.Equal(0, CountingOps.DeltaCalls);
        Assert.Equal(prior.Elements.ToArray(), Apply<int, Int32StateOps>(prior, result, IntLayout).Elements.ToArray());
    }

    [Fact]
    public void ExplicitPlanFactoryBypassesAdaptiveMatchingAndCompetesWithNothing() {
        FrozenListState<int> prior = new(Enumerable.Range(0, 100).ToArray());
        FrozenListState<int> current = new(Enumerable.Range(1000, 100).ToArray());
        int factoryCalls = 0;
        CountingOps.Reset();
        PreparedDeltaBody result = ListStateBody<int, CountingOps>.PrepareDeltaObserved(prior, current, IntLayout,
            out ListDeltaCompetitionObservation observation, planFactory: (oldValues, newValues, slot) => {
                factoryCalls++;
                Assert.Equal(100, oldValues.Length);
                Assert.Equal(100, newValues.Length);
                Assert.Equal(IntLayout.ElementSlot, slot);
                return [new(0, 0, 100)];
            });
        Assert.Equal(1, factoryCalls);
        Assert.Equal(101, CountingOps.EqualCalls); // First NoChange probe and exactly one comparison per encoded pair.
        Assert.Equal(100, CountingOps.DeltaCalls);
        Assert.Equal(0, CountingOps.BaseCalls);
        Assert.Equal(ListDeltaCompetitionOutcome.ExplicitPlan, observation.Outcome);
        Assert.False(observation.Triggered);
        Assert.Equal(ListStateBody<int, Int32StateOps>.PrepareDelta(prior, current, IntLayout, ListDeltaAlgorithm.Position).Body.ToArray(),
            result.Body.ToArray());
        Assert.Equal(current.Elements.ToArray(), Apply<int, Int32StateOps>(prior, result, IntLayout).Elements.ToArray());
    }

    [Theory]
    [InlineData(ListDeltaAlgorithm.Position)]
    [InlineData(ListDeltaAlgorithm.LocalResync)]
    [InlineData(ListDeltaAlgorithm.BoundedMyers)]
    [InlineData(ListDeltaAlgorithm.Adaptive)]
    public void ObservationDoesNotChangeWriterOutput(ListDeltaAlgorithm algorithm) {
        int[] oldValues = Enumerable.Range(0, 512).ToArray();
        int[] newValues = Enumerable.Range(-33, 33).Concat(oldValues).ToArray();
        newValues[^1] = -1_000_000;
        FrozenListState<int> prior = new(oldValues), current = new(newValues);
        PreparedDeltaBody observed = ListStateBody<int, Int32StateOps>.PrepareDeltaObserved(prior, current, IntLayout,
            out ListDeltaCompetitionObservation observation, algorithm);
        PreparedDeltaBody ordinary = ListStateBody<int, Int32StateOps>.PrepareDelta(prior, current, IntLayout, algorithm);
        Assert.Equal(ordinary.HasChanges, observed.HasChanges);
        Assert.Equal(ordinary.Body.ToArray(), observed.Body.ToArray());
        Assert.Equal(algorithm == ListDeltaAlgorithm.Adaptive, observation.Triggered);
    }

    [Fact]
    public void Insert33AndChangedTailRescuesTheWholeSequenceWithMyers() {
        int[] prior = Enumerable.Range(0, 4096).ToArray();
        int[] current = Enumerable.Range(-33, 33).Concat(prior).ToArray();
        current[^1] = -1_000_000;
        var (result, observation) = VerifyPair<int, Int32StateOps>(prior, current, IntLayout);
        Assert.Equal(ListDeltaCompetitionOutcome.MyersWon, observation.Outcome);
        Assert.True(observation.Triggered);
        Assert.True(result.Body.Length < observation.IncumbentBytes);
        Assert.Equal(result.Body.Length, observation.ChallengerWrittenBytes);
        Assert.Equal(4096 + 8 * (prior.Length + current.Length), observation.SearchBudgetPerMatcher);
        Assert.Equal(ListStateBody<int, Int32StateOps>.PrepareDelta(new(prior), new(current), IntLayout,
            ListDeltaAlgorithm.BoundedMyers).Body.ToArray(), result.Body.ToArray());
    }

    [Fact]
    public void Balanced65EditsKeepLocalWithoutInvokingTheMyersDepthCliff() {
        const int count = 4096, edits = 65;
        int[] prior = Enumerable.Range(0, count).ToArray();
        HashSet<int> insertBefore = Enumerable.Range(0, edits).Select(i => i * (count / 2) / edits).ToHashSet();
        HashSet<int> delete = Enumerable.Range(1, edits)
            .Select(i => count / 2 + i * (count - count / 2) / edits - 1).ToHashSet();
        List<int> current = [];
        for (int index = 0; index < count; index++) {
            if (insertBefore.Contains(index)) { current.Add(-index - 1); }
            if (!delete.Contains(index)) { current.Add(prior[index]); }
        }
        var (result, observation) = VerifyPair<int, Int32StateOps>(prior, current.ToArray(), IntLayout);
        Assert.Equal(ListDeltaCompetitionOutcome.NotTriggered, observation.Outcome);
        Assert.Equal(0, observation.ChallengerWrittenBytes);
        Assert.True(result.Body.Length < ListStateBody<int, Int32StateOps>.PrepareDelta(new(prior), new(current.ToArray()),
            IntLayout, ListDeltaAlgorithm.BoundedMyers).Body.Length);
    }

    [Fact]
    public void IncompleteMyersCannotReplaceTheCompleteLocalIncumbent() {
        int[] prior = Enumerable.Range(0, 512).ToArray();
        int[] current = Enumerable.Range(-129, 129).Concat(prior).ToArray();
        current[^1] = -1_000_000;
        var (result, observation) = VerifyPair<int, Int32StateOps>(prior, current, IntLayout);
        Assert.Equal(ListDeltaCompetitionOutcome.MyersIncomplete, observation.Outcome);
        Assert.Equal(observation.IncumbentBytes, result.Body.Length);
        Assert.Equal(0, observation.ChallengerWrittenBytes);
    }

    [Fact]
    public void SameCompletedPlanSkipsTheSecondEncoding() {
        int[] oldValues = Enumerable.Range(0, 34).ToArray();
        int[] newValues = Enumerable.Range(1000, 34).ToArray();
        CountingOps.Reset();
        PreparedDeltaBody result = ListStateBody<int, CountingOps>.PrepareDeltaObserved(new(oldValues), new(newValues), IntLayout,
            out ListDeltaCompetitionObservation observation);
        Assert.Equal(ListDeltaCompetitionOutcome.SamePlan, observation.Outcome);
        Assert.True(observation.Triggered);
        Assert.Equal(0, observation.ChallengerWrittenBytes);
        Assert.Equal(34, CountingOps.DeltaCalls);
        Assert.Equal(0, CountingOps.BaseCalls);
        Assert.Equal(newValues, Apply<int, Int32StateOps>(new(oldValues), result, IntLayout).Elements.ToArray());
    }

    [Fact]
    public void DifferentPlansWithEqualCompleteByteCostRetainLocalAtTheFinalCopy() {
        int[] oldValues = new[] { -1 }.Concat(Enumerable.Range(1, 33)).ToArray();
        int[] newValues = Enumerable.Range(1001, 33).Append(-1).ToArray();
        FrozenListState<int> prior = new(oldValues), current = new(newValues);
        List<ListDeltaRange> localPlan = ListDeltaMatcher<int, FixedWidthOps>.PlanLocal(
            oldValues, newValues, IntLayout.ElementSlot, out bool stalled);
        Assert.True(stalled);
        Assert.True(ListDeltaMatcher<int, FixedWidthOps>.TryPlanMyers(
            oldValues, newValues, IntLayout.ElementSlot, out List<ListDeltaRange>? myersPlan));
        Assert.False(localPlan.SequenceEqual(myersPlan!));

        PreparedDeltaBody local = ListStateBody<int, FixedWidthOps>.PrepareDelta(prior, current, IntLayout, ListDeltaAlgorithm.LocalResync);
        PreparedDeltaBody myers = ListStateBody<int, FixedWidthOps>.PrepareDelta(prior, current, IntLayout, ListDeltaAlgorithm.BoundedMyers);
        // Valid fixed-width child codecs make the two different plans tie exactly:
        // Local: 1 + 3 + 34 * (1 + 33) + 1. Myers: 1 + 2 + 33 * 35 + 3.
        Assert.Equal(1161, local.Body.Length);
        Assert.Equal(1161, myers.Body.Length);
        Assert.False(local.Body.SequenceEqual(myers.Body));
        Assert.Equal(new byte[] { 1, 0, 1 }, myers.Body[^3..].ToArray());
        Assert.Equal(newValues, Apply<int, FixedWidthOps>(prior, myers, IntLayout).Elements.ToArray());

        var (result, observation) = VerifyPair<int, FixedWidthOps>(oldValues, newValues, IntLayout);
        Assert.Equal(ListDeltaCompetitionOutcome.ByteLimitReached, observation.Outcome);
        Assert.Equal(1161, observation.IncumbentBytes);
        Assert.Equal(1161, observation.ChallengerWrittenBytes);
        Assert.Equal(local.Body.ToArray(), result.Body.ToArray());
    }

    [Fact]
    public void DuplicateAnchorFalseNegativeRemainsAnExplicitLimitOfTheTrigger() {
        int[] prior = [0, 1, 0, 0], current = [1, 0, 0, 1];
        var (result, observation) = VerifyPair<int, Int32StateOps>(prior, current, IntLayout);
        Assert.Equal(ListDeltaCompetitionOutcome.NotTriggered, observation.Outcome);
        Assert.Equal(13, result.Body.Length);
        Assert.Equal(7, ListStateBody<int, Int32StateOps>.PrepareDelta(new(prior), new(current), IntLayout,
            ListDeltaAlgorithm.BoundedMyers).Body.Length);
    }

    [Fact]
    public void RandomSequencesNeverExceedLocalAndRoundtripWithoutChangingFrozenInputs() {
        Random random = new(51031);
        for (int trial = 0; trial < 650; trial++) {
            int[] prior = Enumerable.Range(0, random.Next(0, 100)).Select(_ => random.Next(-12, 13)).ToArray();
            int[] current;
            if (trial % 5 == 0) {
                current = prior.ToArray();
            } else if (trial % 5 == 1) {
                current = Enumerable.Range(-1000, 33).Concat(prior).ToArray();
                if (current.Length > 33) { current[^1] = -2000; }
            } else {
                current = Enumerable.Range(0, random.Next(0, 100)).Select(_ => random.Next(-12, 13)).ToArray();
            }
            VerifyPair<int, Int32StateOps>(prior, current, IntLayout);
        }
    }

    [Fact]
    public void ReferenceIdentitySlotsAndExactFloatingBitsUseTheSameCompetition() {
        Random random = new(51041);
        ListLayout referenceLayout = new(new(1, TypeTag.String));
        for (int trial = 0; trial < 180; trial++) {
            ObjectId[] prior = Enumerable.Range(0, random.Next(0, 80)).Select(_ => new ObjectId((uint)random.Next(0, 12))).ToArray();
            ObjectId[] current = trial % 5 == 0 ? prior.ToArray() :
                Enumerable.Range(0, random.Next(0, 80)).Select(_ => new ObjectId((uint)random.Next(0, 12))).ToArray();
            VerifyPair<ObjectId, StringIdStateOps>(prior, current, referenceLayout);
        }
        float[] values = [0f, -0f, BitConverter.UInt32BitsToSingle(0x7fc00001), BitConverter.UInt32BitsToSingle(0x7fc00002)];
        ListLayout floatLayout = new(new(1, TypeTag.Single));
        foreach (float prior in values) {
            foreach (float current in values) {
                var (result, _) = VerifyPair<float, SingleStateOps>([prior], [current], floatLayout);
                Assert.Equal(BitConverter.SingleToUInt32Bits(prior) != BitConverter.SingleToUInt32Bits(current), result.HasChanges);
            }
        }
    }

    internal static (PreparedDeltaBody Result, ListDeltaCompetitionObservation Observation) VerifyPair<TState, TOps>(
        TState[] oldValues, TState[] newValues, ListLayout layout) where TState : unmanaged where TOps : IStateOps<TState> {
        FrozenListState<TState> prior = new(oldValues), current = new(newValues);
        byte[] oldBase = ListStateBody<TState, TOps>.PrepareBase(prior, layout).Body.ToArray();
        byte[] newBase = ListStateBody<TState, TOps>.PrepareBase(current, layout).Body.ToArray();
        PreparedDeltaBody local = ListStateBody<TState, TOps>.PrepareDelta(prior, current, layout, ListDeltaAlgorithm.LocalResync);
        PreparedDeltaBody result = ListStateBody<TState, TOps>.PrepareDeltaObserved(prior, current, layout,
            out ListDeltaCompetitionObservation observation);
        Assert.Equal(local.HasChanges, result.HasChanges);
        Assert.True(result.Body.Length <= local.Body.Length, $"Adaptive {result.Body.Length} exceeds Local {local.Body.Length}.");
        if (observation.Outcome != ListDeltaCompetitionOutcome.MyersWon) {
            Assert.Equal(local.Body.ToArray(), result.Body.ToArray());
        }
        FrozenListState<TState> applied = Apply<TState, TOps>(prior, result, layout);
        Assert.Equal(newBase, ListStateBody<TState, TOps>.PrepareBase(applied, layout).Body.ToArray());
        Assert.Equal(oldBase, ListStateBody<TState, TOps>.PrepareBase(prior, layout).Body.ToArray());
        Assert.Equal(newBase, ListStateBody<TState, TOps>.PrepareBase(current, layout).Body.ToArray());
        Assert.Equal(result.Body.ToArray(), ListStateBody<TState, TOps>.PrepareDelta(prior, current, layout).Body.ToArray());
        return (result, observation);
    }

    private static FrozenListState<TState> Apply<TState, TOps>(FrozenListState<TState> prior, PreparedDeltaBody delta,
        ListLayout layout) where TState : unmanaged where TOps : IStateOps<TState> {
        BareValueReader reader = new(delta.Body);
        FrozenListState<TState> result = ListStateBody<TState, TOps>.ApplyDelta(ref reader, prior, layout);
        reader.EnsureFullyConsumed();
        return result;
    }

    private readonly struct CountingOps : IStateOps<int> {
        internal static int EqualCalls, BaseCalls, DeltaCalls;
        internal static void Reset() => EqualCalls = BaseCalls = DeltaCalls = 0;
        public static bool StateEquals(in int left, in int right, DurableFieldInfo slot) { EqualCalls++; return left == right; }
        public static void WriteBase(ref BareValueWriter writer, in int value, DurableFieldInfo slot) {
            BaseCalls++; Int32StateOps.WriteBase(ref writer, in value, slot);
        }
        public static int ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => Int32StateOps.ReadBase(ref reader, slot);
        public static PreparedDeltaBody PrepareDelta(in int prior, in int current, DurableFieldInfo slot) {
            DeltaCalls++; return Int32StateOps.PrepareDelta(in prior, in current, slot);
        }
        public static int ApplyDelta(ref BareValueReader reader, in int prior, DurableFieldInfo slot) => Int32StateOps.ApplyDelta(ref reader, in prior, slot);
        public static void VisitReferences(in int value, IStateReferenceVisitor visitor, DurableFieldInfo slot) { }
    }

    // A real, deliberately padded test codec: four little-endian value bytes and canonical zero padding.
    // Its two widths provide an exact competing-plan tie without changing production search or encoding.
    private readonly struct FixedWidthOps : IStateOps<int> {
        public static bool StateEquals(in int left, in int right, DurableFieldInfo slot) => left == right;
        public static void WriteBase(ref BareValueWriter writer, in int value, DurableFieldInfo slot) => Write(ref writer, value, 35);
        public static int ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => Read(ref reader, 35);
        public static PreparedDeltaBody PrepareDelta(in int prior, in int current, DurableFieldInfo slot) {
            if (prior == current) { return new(false, []); }
            byte[] bytes = new byte[33];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes, current);
            return new(true, bytes);
        }
        public static int ApplyDelta(ref BareValueReader reader, in int prior, DurableFieldInfo slot) {
            int result = Read(ref reader, 33);
            if (result == prior) { throw new InvalidDataException("A child patch must change its prior value."); }
            return result;
        }
        public static void VisitReferences(in int value, IStateReferenceVisitor visitor, DurableFieldInfo slot) { }

        private static void Write(ref BareValueWriter writer, int value, int width) {
            for (int index = 0; index < 4; index++) { writer.WriteByte((byte)((uint)value >> (index * 8))); }
            for (int index = 4; index < width; index++) { writer.WriteByte(0); }
        }

        private static int Read(ref BareValueReader reader, int width) {
            uint value = 0;
            for (int index = 0; index < 4; index++) { value |= (uint)reader.ReadByte() << (index * 8); }
            for (int index = 4; index < width; index++) {
                if (reader.ReadByte() != 0) { throw new InvalidDataException("Nonzero fixed-width codec padding."); }
            }
            return unchecked((int)value);
        }
    }
}
