using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed class ListDeltaMatcherTests {
    private static readonly DurableFieldInfo IntSlot = new(1, TypeTag.Int32);

    [Theory]
    [InlineData(ListDeltaAlgorithm.Position)]
    [InlineData(ListDeltaAlgorithm.LocalResync)]
    [InlineData(ListDeltaAlgorithm.BoundedMyers)]
    public void RandomPlansCoverTargetWithValidMonotoneSourceAndCanonicalMerging(ListDeltaAlgorithm algorithm) {
        Random random = new(490017);
        for (int sample = 0; sample < 1500; sample++) {
            int[] prior = Enumerable.Range(0, random.Next(65)).Select(_ => random.Next(7)).ToArray();
            int[] current = Enumerable.Range(0, random.Next(65)).Select(_ => random.Next(7)).ToArray();
            List<ListDeltaRange> plan = Plan(prior, current, algorithm);
            CheckRanges(plan, prior, current);
            Assert.Equal(plan, Plan(prior, current, algorithm));
        }
    }

    [Fact]
    public void MyersMatchesIndependentLcsOptimumForEverySmallBinarySequencePair() {
        int[][] sequences = AllBinarySequences(5).ToArray();
        foreach (int[] prior in sequences) {
            foreach (int[] current in sequences) {
                List<ListDeltaRange> plan = Plan(prior, current, ListDeltaAlgorithm.BoundedMyers);
                CheckRanges(plan, prior, current);
                Assert.Equal(LcsLength(prior, current), EqualPairs(plan, prior, current));
            }
        }
        // The oracle is a rectangular dynamic program, independent of the frontier/trace implementation.
        Random random = new(490031);
        for (int sample = 0; sample < 1000; sample++) {
            int[] prior = Enumerable.Range(0, random.Next(25)).Select(_ => random.Next(8)).ToArray();
            int[] current = Enumerable.Range(0, random.Next(25)).Select(_ => random.Next(8)).ToArray();
            Assert.Equal(LcsLength(prior, current), EqualPairs(Plan(prior, current, ListDeltaAlgorithm.BoundedMyers), prior, current));
        }
    }

    [Theory]
    [InlineData(ListDeltaAlgorithm.LocalResync, 0)]
    [InlineData(ListDeltaAlgorithm.LocalResync, 100)]
    [InlineData(ListDeltaAlgorithm.LocalResync, 200)]
    [InlineData(ListDeltaAlgorithm.BoundedMyers, 0)]
    [InlineData(ListDeltaAlgorithm.BoundedMyers, 100)]
    [InlineData(ListDeltaAlgorithm.BoundedMyers, 200)]
    public void SingleInsertDeleteRetainsEveryUneditedElement(ListDeltaAlgorithm algorithm, int at) {
        int[] prior = Enumerable.Range(1, 200).ToArray();
        int[] inserted = prior.Take(at).Concat(new[] { -1, -2 }).Concat(prior.Skip(at)).ToArray();
        List<ListDeltaRange> insert = Plan(prior, inserted, algorithm);
        CheckRanges(insert, prior, inserted);
        Assert.Equal(prior.Length, EqualPairs(insert, prior, inserted));
        Assert.Equal(2, insert.Where(range => range.OldStart == -1).Sum(range => range.Count));
        List<ListDeltaRange> delete = Plan(inserted, prior, algorithm);
        CheckRanges(delete, inserted, prior);
        Assert.Equal(prior.Length, EqualPairs(delete, inserted, prior));
        Assert.DoesNotContain(delete, range => range.OldStart == -1);
    }

    [Fact]
    public void PositionDoesNotCompareAndPairsOnlyTheCommonLength() {
        CountingOps.Calls = 0;
        int[] old = [1, 2, 3], current = [8, 1, 2, 3, 9];
        List<ListDeltaRange> plan = ListDeltaMatcher<int, CountingOps>.Plan(old, current, IntSlot, ListDeltaAlgorithm.Position);
        Assert.Equal(0, CountingOps.Calls);
        Assert.Equal(new[] { new ListDeltaRange(0, 0, 3), new ListDeltaRange(-1, 3, 2) }, plan);
        Assert.Equal(new[] { new ListDeltaRange(0, 0, 2) }, Plan(old, [8, 9], ListDeltaAlgorithm.Position));
    }

    [Theory]
    [InlineData(ListDeltaAlgorithm.LocalResync)]
    [InlineData(ListDeltaAlgorithm.BoundedMyers)]
    public void ZeroComparisonBudgetFallsBackOnlyAfterKeepingDisjointPrefixAndSuffix(ListDeltaAlgorithm algorithm) {
        int[] prior = [1, 2, 3, 4], current = [1, 8, 9, 10, 4];
        List<ListDeltaRange> plan = ListDeltaMatcher<int, Int32StateOps>.PlanWithBudget(prior, current, IntSlot, algorithm, 0);
        Assert.Equal(new[] { new ListDeltaRange(0, 0, 3), new ListDeltaRange(-1, 3, 1), new ListDeltaRange(3, 4, 1) }, plan);
        CheckRanges(plan, prior, current);
        Assert.Equal(new[] { new ListDeltaRange(0, 0, 4) },
            ListDeltaMatcher<int, Int32StateOps>.PlanWithBudget(prior, prior, IntSlot, algorithm, 0));
        Assert.Empty(ListDeltaMatcher<int, Int32StateOps>.PlanWithBudget(prior, [], IntSlot, algorithm, 0));
    }

    [Theory]
    [InlineData(ListDeltaAlgorithm.LocalResync)]
    [InlineData(ListDeltaAlgorithm.BoundedMyers)]
    public void EverySearchComparisonIncludingMismatchesConsumesBudget(ListDeltaAlgorithm algorithm) {
        int[] prior = Enumerable.Range(0, 100).ToArray();
        int[] current = Enumerable.Range(1000, 100).ToArray();
        foreach (int budget in new[] { 0, 1, 2, 31, 100, 257 }) {
            CountingOps.Calls = 0;
            List<ListDeltaRange> plan = ListDeltaMatcher<int, CountingOps>.PlanWithBudget(prior, current, IntSlot, algorithm, budget);
            Assert.Equal(budget + 2, CountingOps.Calls); // One failed prefix and suffix comparison are baseline work.
            CheckRanges(plan, prior, current);
            Assert.Equal(new[] { new ListDeltaRange(0, 0, 100) }, plan);
        }
    }

    [Fact]
    public void LocalTiePrefersSmallerOldOffsetAndLookaheadIncludesThirtyTwo() {
        Assert.Equal(new[] { new ListDeltaRange(-1, 0, 1), new ListDeltaRange(0, 1, 2) },
            Plan([1, 2, 3], [2, 1, 4], ListDeltaAlgorithm.LocalResync));
        int[] prior = Enumerable.Range(0, 100).Append(600).ToArray();
        int[] current = Enumerable.Range(-32, 32).Concat(Enumerable.Range(0, 100)).Append(700).ToArray();
        List<ListDeltaRange> plan = Plan(prior, current, ListDeltaAlgorithm.LocalResync);
        Assert.Equal(new[] { new ListDeltaRange(-1, 0, 32), new ListDeltaRange(0, 32, 101) }, plan);
        Assert.Equal(100, EqualPairs(plan, prior, current));
        List<ListDeltaRange> deletion = Plan(current, prior, ListDeltaAlgorithm.LocalResync);
        Assert.Equal(new[] { new ListDeltaRange(32, 0, 101) }, deletion);
    }

    [Fact]
    public void MyersDepthAndTraceBoundariesProduceDeterministicCompleteFallbacks() {
        int[] prior = [1, 2], current = [2, 1];
        List<ListDeltaRange> positional = [new(0, 0, 2)];
        Assert.Equal(positional, ListDeltaMatcher<int, Int32StateOps>.PlanWithBudget(prior, current, IntSlot,
            ListDeltaAlgorithm.BoundedMyers, 1000, maximumEditDepth: 1));
        Assert.Equal(1, EqualPairs(ListDeltaMatcher<int, Int32StateOps>.PlanWithBudget(prior, current, IntSlot,
            ListDeltaAlgorithm.BoundedMyers, 1000, maximumEditDepth: 2), prior, current));
        // Three active frontiers have 1 + 2 + 3 Int32 coordinates: exactly 24 trace payload bytes.
        Assert.Equal(positional, ListDeltaMatcher<int, Int32StateOps>.PlanWithBudget(prior, current, IntSlot,
            ListDeltaAlgorithm.BoundedMyers, 1000, maximumTraceBytes: 23));
        Assert.Equal(1, EqualPairs(ListDeltaMatcher<int, Int32StateOps>.PlanWithBudget(prior, current, IntSlot,
            ListDeltaAlgorithm.BoundedMyers, 1000, maximumTraceBytes: 24), prior, current));
        CountingOps.Calls = 0;
        ListDeltaMatcher<int, CountingOps>.PlanWithBudget([1], new int[140], IntSlot, ListDeltaAlgorithm.BoundedMyers, 1000);
        Assert.Equal(2, CountingOps.Calls); // Length difference exceeds edit depth: no search begins.
    }

    [Fact]
    public void RepeatedObjectIdsIncludingNullUseIdentitySemanticsWithoutPayloadWork() {
        ObjectId[] prior = [new(1), default, new(2), new(1), default, new(2)];
        ObjectId[] current = [new(1), default, new(3), new(2), new(1), default, new(2)];
        DurableFieldInfo slot = DurableFieldInfo.Reference(1, TypeExpr.Named("Node"));
        foreach (ListDeltaAlgorithm algorithm in new[] { ListDeltaAlgorithm.LocalResync, ListDeltaAlgorithm.BoundedMyers }) {
            List<ListDeltaRange> plan = ListDeltaMatcher<ObjectId, IdentityOps>.Plan(prior, current, slot, algorithm);
            Assert.Equal(6, plan.Where(range => range.OldStart >= 0).Sum(range => range.Count));
            Assert.Equal(new ListDeltaRange(-1, 2, 1), Assert.Single(plan, range => range.OldStart == -1));
            foreach (ListDeltaRange range in plan.Where(range => range.OldStart >= 0)) {
                for (int index = 0; index < range.Count; index++) {
                    Assert.Equal(prior[range.OldStart + index], current[range.NewStart + index]);
                }
            }
        }
    }

    [Fact]
    public void MatchingRetainsUnresolvedPairsForLaterSparseStructPatches() {
        List<ListDeltaRange> plan = Plan([1, 2, 3], [7, 8, 9], ListDeltaAlgorithm.BoundedMyers);
        Assert.Equal(new[] { new ListDeltaRange(0, 0, 3) }, plan);
        Assert.Throws<ArgumentOutOfRangeException>(() => Plan([], [], (ListDeltaAlgorithm)123));
    }

    private static List<ListDeltaRange> Plan(int[] prior, int[] current, ListDeltaAlgorithm algorithm) =>
        ListDeltaMatcher<int, Int32StateOps>.Plan(prior, current, IntSlot, algorithm);

    private static void CheckRanges(IReadOnlyList<ListDeltaRange> plan, int[] prior, int[] current) {
        int target = 0, lastSourceEnd = 0;
        ListDeltaRange? previous = null;
        foreach (ListDeltaRange range in plan) {
            Assert.Equal(target, range.NewStart);
            Assert.True(range.Count > 0);
            Assert.InRange(range.Count, 1, current.Length - target);
            Assert.True(range.OldStart >= -1);
            if (range.OldStart >= 0) {
                Assert.InRange(range.OldStart, lastSourceEnd, prior.Length - range.Count);
                lastSourceEnd = range.OldStart + range.Count;
            }
            if (previous is { } before) {
                Assert.False(before.OldStart == -1 && range.OldStart == -1);
                Assert.False(before.OldStart >= 0 && range.OldStart >= 0 && before.OldStart + before.Count == range.OldStart);
            }
            target += range.Count;
            previous = range;
        }
        Assert.Equal(current.Length, target);
    }

    private static int EqualPairs(IEnumerable<ListDeltaRange> plan, int[] prior, int[] current) {
        int count = 0;
        foreach (ListDeltaRange range in plan.Where(range => range.OldStart >= 0)) {
            for (int index = 0; index < range.Count; index++) {
                if (prior[range.OldStart + index] == current[range.NewStart + index]) { count++; }
            }
        }
        return count;
    }

    private static int LcsLength(int[] prior, int[] current) {
        int[,] table = new int[prior.Length + 1, current.Length + 1];
        for (int oldIndex = 0; oldIndex < prior.Length; oldIndex++) {
            for (int newIndex = 0; newIndex < current.Length; newIndex++) {
                table[oldIndex + 1, newIndex + 1] = prior[oldIndex] == current[newIndex]
                    ? table[oldIndex, newIndex] + 1
                    : Math.Max(table[oldIndex, newIndex + 1], table[oldIndex + 1, newIndex]);
            }
        }
        return table[prior.Length, current.Length];
    }

    private static IEnumerable<int[]> AllBinarySequences(int maximumLength) {
        for (int length = 0; length <= maximumLength; length++) {
            for (int bits = 0; bits < 1 << length; bits++) {
                yield return Enumerable.Range(0, length).Select(index => (bits >> index) & 1).ToArray();
            }
        }
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
    private readonly struct IdentityOps : IStateOps<ObjectId> {
        public static bool StateEquals(in ObjectId left, in ObjectId right, DurableFieldInfo slot) => left == right;
        public static void WriteBase(ref BareValueWriter writer, in ObjectId state, DurableFieldInfo slot) => throw new InvalidOperationException();
        public static ObjectId ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => throw new InvalidOperationException();
        public static PreparedDeltaBody PrepareDelta(in ObjectId prior, in ObjectId current, DurableFieldInfo slot) => throw new InvalidOperationException();
        public static ObjectId ApplyDelta(ref BareValueReader reader, in ObjectId prior, DurableFieldInfo slot) => throw new InvalidOperationException();
        public static void VisitReferences(in ObjectId state, IStateReferenceVisitor visitor, DurableFieldInfo slot) => throw new InvalidOperationException();
    }
}
