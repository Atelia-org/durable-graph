using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed class ListDeltaKernelTests {
    private static readonly DurableFieldInfo IntSlot = new(1, TypeTag.Int32);

    [Theory]
    [InlineData(ListDeltaAlgorithm.LocalResync)]
    [InlineData(ListDeltaAlgorithm.BoundedMyers)]
    public void DefaultPlansKeepPositionFallbackAndTheirExactSearchBudget(ListDeltaAlgorithm algorithm) {
        int[] prior = Enumerable.Range(0, 64).ToArray();
        int[] current = Enumerable.Range(1000, 64).ToArray();
        foreach (int budget in new[] { 0, 1, 1088, 1089, 1090 }) {
            CountingOps.Calls = 0;
            List<ListDeltaRange> result = ListDeltaMatcher<int, CountingOps>.PlanWithBudget(
                prior, current, IntSlot, algorithm, budget);
            Assert.Equal(new[] { new ListDeltaRange(0, 0, 64) }, result);
            Assert.Equal(budget + 2, CountingOps.Calls); // The unsuccessful prefix/suffix probes are outside search.
        }
    }

    [Theory]
    [InlineData(0, (int)ListDeltaLocalStatus.BudgetExhausted, 0)]
    [InlineData(1, (int)ListDeltaLocalStatus.BudgetExhausted, 0)]
    [InlineData(1088, (int)ListDeltaLocalStatus.BudgetExhausted, 0)]
    [InlineData(1089, (int)ListDeltaLocalStatus.WindowMiss, 0)]
    [InlineData(1090, (int)ListDeltaLocalStatus.WindowMiss, 1)]
    public void FullWindowMissIsDistinctFromAnInterruptedSearch(int budget, int expected, int unused) {
        int[] prior = Enumerable.Range(0, 33).ToArray();
        int[] current = Enumerable.Range(1000, 33).ToArray();
        List<ListDeltaRange> result = [];
        int oldIndex = 0, newIndex = 0, remaining = budget;
        CountingOps.Calls = 0;
        ListDeltaLocalStatus status = ListDeltaMatcher<int, CountingOps>.TryLocal(prior, current, IntSlot,
            ref oldIndex, prior.Length, ref newIndex, current.Length, ref remaining, result, stopAtWindowMiss: true);
        Assert.Equal((ListDeltaLocalStatus)expected, status);
        Assert.Equal(unused, remaining);
        Assert.Equal(budget - unused, CountingOps.Calls);
        Assert.Equal(0, oldIndex);
        Assert.Equal(0, newIndex);
        Assert.Empty(result);
    }

    [Fact]
    public void PausingThenConsumingFailedPairDoesNotRepeatTheWindowOrChangeDefaultLocalPlan() {
        int[] prior = Enumerable.Range(0, 100).ToArray();
        int[] current = Enumerable.Range(1000, 100).ToArray();
        List<ListDeltaRange> direct = [];
        int oldIndex = 0, newIndex = 0, remaining = 20_000;
        CountingOps.Calls = 0;
        ListDeltaMatcher<int, CountingOps>.TryLocal(prior, current, IntSlot,
            ref oldIndex, prior.Length, ref newIndex, current.Length, ref remaining, direct);
        ListDeltaMatcher<int, CountingOps>.AddGap(direct, oldIndex, prior.Length, newIndex, current.Length);
        int directCalls = CountingOps.Calls;
        int directRemaining = remaining;

        List<ListDeltaRange> resumed = [];
        oldIndex = newIndex = 0;
        remaining = 20_000;
        CountingOps.Calls = 0;
        Assert.Equal(ListDeltaLocalStatus.WindowMiss, ListDeltaMatcher<int, CountingOps>.TryLocal(prior, current,
            IntSlot, ref oldIndex, prior.Length, ref newIndex, current.Length, ref remaining, resumed,
            stopAtWindowMiss: true));
        ListDeltaMatcher<int, CountingOps>.Add(resumed, oldIndex++, newIndex++, 1);
        ListDeltaMatcher<int, CountingOps>.TryLocal(prior, current, IntSlot,
            ref oldIndex, prior.Length, ref newIndex, current.Length, ref remaining, resumed);
        ListDeltaMatcher<int, CountingOps>.AddGap(resumed, oldIndex, prior.Length, newIndex, current.Length);

        Assert.Equal(direct, resumed);
        Assert.Equal(directCalls, CountingOps.Calls);
        Assert.Equal(directRemaining, remaining);
    }

    [Fact]
    public void LocalPauseRetainsAlreadyMatchedPrefixAndDifferentCursors() {
        int[] prior = Enumerable.Range(1, 100).ToArray();
        int[] current = new[] { -1 }.Concat(prior.Take(10)).Concat(Enumerable.Range(1000, 90)).ToArray();
        List<ListDeltaRange> result = [];
        int oldIndex = 0, newIndex = 0, remaining = 10_000;
        Assert.Equal(ListDeltaLocalStatus.WindowMiss, ListDeltaMatcher<int, CountingOps>.TryLocal(prior, current,
            IntSlot, ref oldIndex, prior.Length, ref newIndex, current.Length, ref remaining, result,
            stopAtWindowMiss: true));
        Assert.Equal(10, oldIndex);
        Assert.Equal(11, newIndex);
        Assert.Equal(new[] { new ListDeltaRange(-1, 0, 1), new ListDeltaRange(0, 1, 10) }, result);
    }

    [Fact]
    public void LocalCompletionConsumesTerminalGapEvenWithNoComparisonBudget() {
        List<ListDeltaRange> result = [];
        int oldIndex = 2, newIndex = 1, remaining = 0;
        Assert.Equal(ListDeltaLocalStatus.Complete, ListDeltaMatcher<int, CountingOps>.TryLocal([1, 2], [8, 9, 10],
            IntSlot, ref oldIndex, 2, ref newIndex, 3, ref remaining, result, stopAtWindowMiss: true));
        Assert.Equal(2, oldIndex);
        Assert.Equal(3, newIndex);
        Assert.Equal(new[] { new ListDeltaRange(-1, 1, 2) }, result);
    }

    [Fact]
    public void MyersRecoveryUsesIndependentOldAndNewOffsetsIncludingInsertedGaps() {
        List<ListDeltaRange> result = [];
        int remaining = 1000;
        Assert.True(ListDeltaMatcher<int, CountingOps>.Myers([1, 2, 3], [9, 1, 2, 8, 3], IntSlot,
            7, 11, ref remaining, 128, 1024 * 1024, result));
        Assert.Equal(new[] {
            new ListDeltaRange(-1, 11, 1), new ListDeltaRange(7, 12, 2),
            new ListDeltaRange(-1, 14, 1), new ListDeltaRange(9, 15, 1)
        }, result);
    }

    [Fact]
    public void MyersOffsetTranslationPreservesComparisonsAndCoordinates() {
        Random random = new(500013);
        for (int sample = 0; sample < 400; sample++) {
            int[] prior = Enumerable.Range(0, random.Next(25)).Select(_ => random.Next(7)).ToArray();
            int[] current = Enumerable.Range(0, random.Next(25)).Select(_ => random.Next(7)).ToArray();
            List<ListDeltaRange> unshifted = [], shifted = [];
            int initialBudget = sample % 3 == 0 ? 3 : 5000;
            int remaining = initialBudget;
            CountingOps.Calls = 0;
            bool originalSuccess = ListDeltaMatcher<int, CountingOps>.Myers(prior, current, IntSlot,
                0, 0, ref remaining, 128, 1024 * 1024, unshifted);
            int calls = CountingOps.Calls, unused = remaining;
            remaining = initialBudget;
            CountingOps.Calls = 0;
            bool shiftedSuccess = ListDeltaMatcher<int, CountingOps>.Myers(prior, current, IntSlot,
                7, 11, ref remaining, 128, 1024 * 1024, shifted);
            Assert.Equal(originalSuccess, shiftedSuccess);
            Assert.Equal(calls, CountingOps.Calls);
            Assert.Equal(unused, remaining);
            Assert.Equal(unshifted.Select(range => new ListDeltaRange(
                range.OldStart < 0 ? -1 : range.OldStart + 7, range.NewStart + 11, range.Count)), shifted);
        }
    }

    [Theory]
    [InlineData(0, 128, 1048576)]
    [InlineData(1, 128, 1048576)]
    [InlineData(1000, 1, 1048576)]
    [InlineData(1000, 128, 23)]
    public void MyersFailureLeavesPreviouslyChosenRangesUntouched(int budget, int depth, int traceBytes) {
        List<ListDeltaRange> result = [new(0, 0, 3)];
        int remaining = budget;
        CountingOps.Calls = 0;
        Assert.False(ListDeltaMatcher<int, CountingOps>.Myers([1, 2], [2, 1], IntSlot,
            3, 5, ref remaining, depth, traceBytes, result));
        Assert.Equal(new[] { new ListDeltaRange(0, 0, 3) }, result);
        Assert.Equal(budget - remaining, CountingOps.Calls);
        Assert.InRange(remaining, 0, budget);
    }

    private readonly struct CountingOps : IStateOps<int> {
        public static int Calls;
        public static bool StateEquals(in int left, in int right, DurableFieldInfo slot) { Calls++; return left == right; }
        public static void WriteBase(ref BareValueWriter writer, in int state, DurableFieldInfo slot) => throw new InvalidOperationException();
        public static int ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => throw new InvalidOperationException();
        public static PreparedDeltaBody PrepareDelta(in int prior, in int current, DurableFieldInfo slot) => throw new InvalidOperationException();
        public static int ApplyDelta(ref BareValueReader reader, in int prior, DurableFieldInfo slot) => throw new InvalidOperationException();
        public static void VisitReferences(in int state, IStateReferenceVisitor visitor, DurableFieldInfo slot) => throw new InvalidOperationException();
    }
}
