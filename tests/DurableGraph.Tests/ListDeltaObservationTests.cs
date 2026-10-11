using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed class ListDeltaObservationTests {
    private static readonly DurableFieldInfo IntSlot = new(1, TypeTag.Int32);

    [Fact]
    public void OrdinaryAnchorKeepsPreObservationCoordinatesAndComparisonOrder() {
        int[] prior = [1, 2, 3, 4], current = [1, 9, 2, 3, 8];
        CountingOps.Comparisons.Clear();
        List<ListDeltaRange> plan = ListDeltaMatcher<int, CountingOps>.PlanLocal(prior, current, IntSlot, out bool stalled);
        Assert.False(stalled);
        Assert.Equal(new[] { new ListDeltaRange(0, 0, 1), new ListDeltaRange(-1, 1, 1), new ListDeltaRange(1, 2, 3) }, plan);
        // Independent golden from the original Local path: prefix, suffix, then the middle search.
        Assert.Equal(new[] { (1, 1), (2, 9), (4, 8), (2, 9), (2, 2), (3, 3), (4, 8) }, CountingOps.Comparisons);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1088)]
    [InlineData(1089)]
    [InlineData(1090)]
    public void BudgetTruncationKeepsOriginalCompleteFallbackAndExactComparisonCount(int budget) {
        int[] prior = Enumerable.Range(0, 64).ToArray();
        int[] current = Enumerable.Range(1000, 64).ToArray();
        CountingOps.Comparisons.Clear();
        List<ListDeltaRange> plan = ListDeltaMatcher<int, CountingOps>.PlanLocalWithBudget(
            prior, current, IntSlot, budget, out bool stalled);
        Assert.True(stalled);
        Assert.Equal(new[] { new ListDeltaRange(0, 0, 64) }, plan);
        Assert.Equal(budget + 2, CountingOps.Comparisons.Count);
    }

    [Theory]
    [InlineData(33, 12531, false)]
    [InlineData(34, 13620, true)]
    public void CompleteWindowMissObservesRemainingLengthWithoutShorteningOriginalLocalWork(int length, int calls, bool expectedStall) {
        int[] prior = Enumerable.Range(0, length).ToArray();
        int[] current = Enumerable.Range(1000, length).ToArray();
        CountingOps.Comparisons.Clear();
        List<ListDeltaRange> plan = ListDeltaMatcher<int, CountingOps>.PlanLocalWithBudget(
            prior, current, IntSlot, 20_000, out bool stalled);
        Assert.Equal(expectedStall, stalled);
        Assert.Equal(new[] { new ListDeltaRange(0, 0, length) }, plan);
        // 33: 2 + sum(k^2, k=1..33). 34 adds one capped 33x33 window before that short tail.
        Assert.Equal(calls, CountingOps.Comparisons.Count);
    }

    [Theory]
    [InlineData(1, 33, false)]
    [InlineData(1, 34, true)]
    [InlineData(33, 1, false)]
    [InlineData(34, 1, true)]
    public void OnlyOneRemainingSideNeedsToExtendBeyondTheCompleteWindow(int oldLength, int newLength, bool expectedStall) {
        int[] prior = Enumerable.Range(0, oldLength).ToArray();
        int[] current = Enumerable.Range(1000, newLength).ToArray();
        CountingOps.Comparisons.Clear();
        ListDeltaMatcher<int, CountingOps>.PlanLocalWithBudget(prior, current, IntSlot, 2000, out bool stalled);
        Assert.Equal(expectedStall, stalled);
        Assert.Equal(35, CountingOps.Comparisons.Count); // Failed prefix/suffix probes + one complete 33-pair window.
    }

    [Fact]
    public void InterruptedShortWindowAndLaterExhaustionRemainConservativeTriggers() {
        int[] prior = [1, 2], current = [3, 4];
        foreach (int budget in new[] { 3, 4, 5 }) {
            CountingOps.Comparisons.Clear();
            List<ListDeltaRange> plan = ListDeltaMatcher<int, CountingOps>.PlanLocalWithBudget(
                prior, current, IntSlot, budget, out bool stalled);
            Assert.Equal(new[] { new ListDeltaRange(0, 0, 2) }, plan);
            Assert.Equal(budget < 5, stalled);
            Assert.Equal(budget + 2, CountingOps.Comparisons.Count);
        }
        // Budget 3 interrupts the first 2x2 window. Budget 4 proves the miss, consumes one pair,
        // then exhausts at the remaining pair. Budget 5 finishes that pair with no extra trigger.
    }

    [Fact]
    public void TrimmedEndsAndOneSidedGapsCannotCreateAFalseBudgetExhaustion() {
        foreach ((int[] prior, int[] current) in new (int[], int[])[] {
            ([], []), ([], [1, 2]), ([1, 2], []), ([1, 2], [1, 2]), ([1, 2], [1, 9, 2]), ([1, 9, 2], [1, 2])
        }) {
            Assert.False(Observe(prior, current, 0, out _));
        }
        Assert.True(Observe([1, 2, 3, 4], [1, 8, 9, 10, 4], 0, out List<ListDeltaRange> fallback));
        Assert.Equal(new[] { new ListDeltaRange(0, 0, 3), new ListDeltaRange(-1, 3, 1), new ListDeltaRange(3, 4, 1) }, fallback);
    }

    [Fact]
    public void ObservedLocalMatchesOriginalCoordinatesAndEveryComparedPairAcrossBudgets() {
        Random random = new(510017);
        for (int sample = 0; sample < 600; sample++) {
            int[] prior = Enumerable.Range(0, random.Next(80)).Select(_ => random.Next(sample % 3 == 0 ? 1000 : 9)).ToArray();
            int[] current = Enumerable.Range(0, random.Next(80)).Select(_ => random.Next(sample % 3 == 0 ? 1000 : 9)).ToArray();
            int budget = (sample % 6) switch { 0 => 0, 1 => 1, 2 => 1088, 3 => 1089, 4 => 20_000,
                _ => ListDeltaMatcher<int, CountingOps>.GetComparisonBudget(prior.Length, current.Length) };
            CountingOps.Comparisons.Clear();
            List<ListDeltaRange> original = ListDeltaMatcher<int, CountingOps>.PlanWithBudget(
                prior, current, IntSlot, ListDeltaAlgorithm.LocalResync, budget);
            (int, int)[] comparisons = CountingOps.Comparisons.ToArray();
            CountingOps.Comparisons.Clear();
            List<ListDeltaRange> observed = ListDeltaMatcher<int, CountingOps>.PlanLocalWithBudget(
                prior, current, IntSlot, budget, out _);
            Assert.Equal(original, observed);
            Assert.Equal(comparisons, CountingOps.Comparisons);
        }
    }

    [Theory]
    [InlineData(0, 128, 1048576)]
    [InlineData(1, 128, 1048576)]
    [InlineData(1000, 1, 1048576)]
    [InlineData(1000, 128, 23)]
    public void FailedMyersDoesNotReturnAPositionalFallbackOrOnlyTheTrimmedEnds(int budget, int depth, int traceBytes) {
        int[] prior = [9, 1, 2, 8], current = [9, 2, 1, 8];
        CountingOps.Comparisons.Clear();
        Assert.False(ListDeltaMatcher<int, CountingOps>.TryPlanMyersWithBudget(
            prior, current, IntSlot, budget, out List<ListDeltaRange>? plan, depth, traceBytes));
        Assert.Null(plan);
        (int, int)[] comparisons = CountingOps.Comparisons.ToArray();
        CountingOps.Comparisons.Clear();
        Assert.Equal(new[] { new ListDeltaRange(0, 0, 4) }, ListDeltaMatcher<int, CountingOps>.PlanWithBudget(
            prior, current, IntSlot, ListDeltaAlgorithm.BoundedMyers, budget, depth, traceBytes));
        Assert.Equal(comparisons, CountingOps.Comparisons);
    }

    [Fact]
    public void CompletedMyersKeepsTheExistingPlanAndExactComparisonOrder() {
        Random random = new(510023);
        for (int sample = 0; sample < 400; sample++) {
            int[] prior = Enumerable.Range(0, random.Next(25)).Select(_ => random.Next(7)).ToArray();
            int[] current = Enumerable.Range(0, random.Next(25)).Select(_ => random.Next(7)).ToArray();
            int budget = sample % 4 == 0 ? 2 : 10_000;
            CountingOps.Comparisons.Clear();
            List<ListDeltaRange> original = ListDeltaMatcher<int, CountingOps>.PlanWithBudget(
                prior, current, IntSlot, ListDeltaAlgorithm.BoundedMyers, budget);
            (int, int)[] comparisons = CountingOps.Comparisons.ToArray();
            CountingOps.Comparisons.Clear();
            bool completed = ListDeltaMatcher<int, CountingOps>.TryPlanMyersWithBudget(
                prior, current, IntSlot, budget, out List<ListDeltaRange>? plan);
            if (completed) { Assert.Equal(original, plan); } else { Assert.Null(plan); }
            Assert.Equal(comparisons, CountingOps.Comparisons);
        }
    }

    [Fact]
    public void IndependentDefaultMyersBudgetSurvivesCompleteLocalExhaustion() {
        int[] prior = Enumerable.Range(0, 128).ToArray();
        int[] current = Enumerable.Range(-33, 33).Concat(prior).ToArray();
        current[^1] = 1000;
        int budget = ListDeltaMatcher<int, CountingOps>.GetComparisonBudget(prior.Length, current.Length);
        CountingOps.Comparisons.Clear();
        ListDeltaMatcher<int, CountingOps>.PlanLocal(prior, current, IntSlot, out bool stalled);
        Assert.True(stalled);
        Assert.Equal(budget + 2, CountingOps.Comparisons.Count);
        CountingOps.Comparisons.Clear();
        Assert.True(ListDeltaMatcher<int, CountingOps>.TryPlanMyers(prior, current, IntSlot, out List<ListDeltaRange>? challenger));
        Assert.InRange(CountingOps.Comparisons.Count, 3, budget + 2);
        Assert.Equal(new[] { new ListDeltaRange(-1, 0, 33), new ListDeltaRange(0, 33, 128) }, challenger);
    }

    [Theory]
    [InlineData(0, 0, 4096)]
    [InlineData(128, 161, 6408)]
    [InlineData(100000, 100000, 1000000)]
    [InlineData(int.MaxValue, int.MaxValue, 1000000)]
    public void IndependentSearchBudgetRetainsOriginalFormulaWithoutIntegerOverflow(int oldCount, int newCount, int budget) =>
        Assert.Equal(budget, ListDeltaMatcher<int, CountingOps>.GetComparisonBudget(oldCount, newCount));

    [Theory]
    [InlineData(3)] // Adaptive is a writer strategy; adding it to the enum must not route it to Myers.
    [InlineData(123)]
    public void PureMatcherRejectsWriterCoordinationAndUnknownValues(int value) {
        CountingOps.Comparisons.Clear();
        Assert.Throws<ArgumentOutOfRangeException>(() => ListDeltaMatcher<int, CountingOps>.Plan([1], [2], IntSlot, (ListDeltaAlgorithm)value));
        Assert.Throws<ArgumentOutOfRangeException>(() => ListDeltaMatcher<int, CountingOps>.PlanWithBudget([1], [2], IntSlot, (ListDeltaAlgorithm)value, 0));
        Assert.Empty(CountingOps.Comparisons);
    }

    private static bool Observe(int[] prior, int[] current, int budget, out List<ListDeltaRange> plan) {
        plan = ListDeltaMatcher<int, CountingOps>.PlanLocalWithBudget(prior, current, IntSlot, budget, out bool stalled);
        return stalled;
    }

    private readonly struct CountingOps : IStateOps<int> {
        public static List<(int, int)> Comparisons { get; } = [];
        public static bool StateEquals(in int left, in int right, DurableFieldInfo slot) {
            Comparisons.Add((left, right));
            return left == right;
        }
        public static void WriteBase(ref BareValueWriter writer, in int state, DurableFieldInfo slot) => throw new InvalidOperationException();
        public static int ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => throw new InvalidOperationException();
        public static PreparedDeltaBody PrepareDelta(in int prior, in int current, DurableFieldInfo slot) => throw new InvalidOperationException();
        public static int ApplyDelta(ref BareValueReader reader, in int prior, DurableFieldInfo slot) => throw new InvalidOperationException();
        public static void VisitReferences(in int state, IStateReferenceVisitor visitor, DurableFieldInfo slot) => throw new InvalidOperationException();
    }
}
