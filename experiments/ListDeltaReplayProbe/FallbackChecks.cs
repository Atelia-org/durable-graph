using Atelia.Binary;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.ListDeltaReplayProbe;

/// <summary>Untimed executable acceptance for the experimental coordinators, independent of benchmark outcomes.</summary>
internal static class FallbackChecks {
    private static readonly DurableFieldInfo Slot = new(1, TypeTag.Int32);
    private static readonly ListLayout Layout = new(Slot);
    private static readonly FallbackStrategy[] Strategies = Enum.GetValues<FallbackStrategy>();

    internal static object Run() {
        const int seed = 49050;
        const int randomPairs = 512;
        Random random = new(seed);
        int planCases = 0, bodyCases = 0;
        List<(string Name, int[] Prior, int[] Current)> named = [
            ("both-empty", [], []),
            ("insert-into-empty", [], [1, 0, 1, 0]),
            ("remove-all", [1, 0, 1, 0], []),
            ("all-equal", Enumerable.Repeat(7, 256).ToArray(), Enumerable.Repeat(7, 256).ToArray()),
            ("duplicate-false-anchor", [0, 1, 0, 0], [1, 0, 0, 1]),
        ];

        int[] old = Enumerable.Range(0, 256).ToArray();
        List<int> asymmetric = old.ToList();
        asymmetric.InsertRange(0, [-3, -2, -1]);
        asymmetric.InsertRange(131, Enumerable.Range(-133, 33));
        asymmetric[^1] = -1_000_000;
        named.Add(("asymmetric-cursor-rescue", old, asymmetric.ToArray()));
        List<int> bothFail = old.ToList();
        bothFail.InsertRange(0, Enumerable.Range(-129, 129));
        bothFail[^1] = -1_000_000;
        named.Add(("both-algorithms-fail", old, bothFail.ToArray()));
        int[] smallOld = Enumerable.Range(0, 33).ToArray();
        int[] smallNew = Enumerable.Range(1_000, 33).ToArray();
        named.Add(("fully-searched-no-match-tail", smallOld, smallNew));
        int[] justOutsideWindow = Enumerable.Range(-33, 33).Concat([0, -1_000_000]).ToArray();
        named.Add(("one-tail-outside-window", [0, 1], justOutsideWindow));

        foreach (var fixture in named) {
            CheckPair(fixture.Prior, fixture.Current, fixture.Name, checkBodies: true, ref planCases, ref bodyCases);
        }
        for (int trial = 0; trial < randomPairs; trial++) {
            int[] prior = Enumerable.Range(0, random.Next(0, 49)).Select(_ => random.Next(-3, 4)).ToArray();
            int[] current = trial % 7 == 0 ? prior.ToArray() :
                Enumerable.Range(0, random.Next(0, 49)).Select(_ => random.Next(-3, 4)).ToArray();
            CheckPair(prior, current, $"random-{trial}", checkBodies: trial < 100,
                ref planCases, ref bodyCases);
        }

        List<ListDeltaRange> rescue = FallbackMatcher<int, Int32StateOps>.Plan(old, asymmetric.ToArray(), Slot,
            FallbackStrategy.LocalThenMyers, out FallbackDiagnostics rescued);
        ListDeltaRange[] expectedRescue = [new(-1, 0, 3), new(0, 3, 128), new(-1, 131, 33), new(128, 164, 128)];
        Require(rescue.SequenceEqual(expectedRescue), "Asymmetric rescue changed its independently expected coordinates.");
        Require(rescued.Triggered && rescued.MyersAttempted && rescued.MyersSucceeded &&
            rescued.LocalStatus == ListDeltaLocalStatus.WindowMiss, "The asymmetric fixture did not exercise tail rescue.");

        List<ListDeltaRange> duplicate = FallbackMatcher<int, Int32StateOps>.Plan([0, 1, 0, 0], [1, 0, 0, 1], Slot,
            FallbackStrategy.LocalThenMyers, out FallbackDiagnostics duplicateStatus);
        ListDeltaRange[] expectedDuplicate = [new(-1, 0, 1), new(0, 1, 1), new(-1, 2, 1), new(1, 3, 1)];
        Require(duplicate.SequenceEqual(expectedDuplicate) && !duplicateStatus.Triggered && !duplicateStatus.MyersAttempted,
            "The duplicate fixture no longer demonstrates the accepted detector false negative.");
        // The exact LCS is [1,0,0], length 3. The retained Local path has only the two single-element copies.
        // This diagnostic documents a limitation; improving it later is allowed to change this expectation.

        foreach (FallbackStrategy strategy in new[] { FallbackStrategy.MyersThenLocal, FallbackStrategy.LocalThenMyers }) {
            _ = FallbackMatcher<int, Int32StateOps>.Plan(old, bothFail.ToArray(), Slot, strategy, out FallbackDiagnostics failed);
            Require(failed.Triggered && failed.MyersAttempted && !failed.MyersSucceeded,
                $"The both-fail fixture did not exercise failed complementary search for {strategy}.");
        }

        List<ListDeltaRange> noMatches = FallbackMatcher<int, Int32StateOps>.Plan(smallOld, smallNew, Slot,
            FallbackStrategy.LocalThenMyers, out FallbackDiagnostics noMatchesStatus);
        Require(noMatches.SequenceEqual(new[] { new ListDeltaRange(0, 0, 33) }) &&
            noMatchesStatus.StopReason == FallbackStopReason.NoMatchesRemaining &&
            !noMatchesStatus.Triggered && !noMatchesStatus.MyersAttempted &&
            noMatchesStatus.LocalComparisons == 33 * 33 && noMatchesStatus.MyersComparisons == 0,
            "A fully checked Cartesian tail should emit its positional gap without another search.");
        _ = FallbackMatcher<int, Int32StateOps>.PlanWithBudget(smallOld, smallNew, Slot,
            FallbackStrategy.LocalThenMyers, 33 * 33 - 1, out FallbackDiagnostics incompleteWindow);
        _ = FallbackMatcher<int, Int32StateOps>.PlanWithBudget(smallOld, smallNew, Slot,
            FallbackStrategy.LocalThenMyers, 33 * 33, out FallbackDiagnostics completeWindow);
        Require(incompleteWindow.StopReason == FallbackStopReason.LocalBudgetExhausted &&
            completeWindow.StopReason == FallbackStopReason.NoMatchesRemaining,
            "Only a complete window can prove the absence of an exact match.");
        _ = FallbackMatcher<int, Int32StateOps>.Plan([0, 1], justOutsideWindow, Slot,
            FallbackStrategy.LocalThenMyers, out FallbackDiagnostics outsideStatus);
        Require(outsideStatus.Triggered && outsideStatus.MyersAttempted && outsideStatus.MyersSucceeded,
            "A short old tail does not prove absence of a match outside the searched new window.");

        return new {
            Seed = seed, RandomPairs = randomPairs, NamedPairs = named.Count, PlanCases = planCases, BodyCases = bodyCases,
            Budgets = "0, 1, 2, 32, 1088, 1089, full; duplicate numeric budgets removed",
            Assertions = "Independent equality counts, shared-budget debit, baseline plan/count equivalence, geometry, " +
                "determinism, common-body Apply/HasChanges/input immutability, asymmetric-rescue coordinates, " +
                "known duplicate detector false negative, simultaneous failure, and proven no-match tail cutoff.",
        };
    }

    private static void CheckPair(int[] prior, int[] current, string name, bool checkBodies, ref int planCases, ref int bodyCases) {
        int fullBudget = (int)Math.Min(1_000_000L, 4096L + 8L * (prior.Length + (long)current.Length));
        int[] budgets = new[] { 0, 1, 2, 32, 1088, 1089, fullBudget }.Distinct().ToArray();
        foreach (int budget in budgets) {
            foreach (FallbackStrategy strategy in Strategies) {
                string context = $"{name}, {strategy}, budget {budget}";
                CheckPlan(prior, current, strategy, budget, context);
                planCases++;
                if (checkBodies) {
                    CheckBody(prior, current, strategy, budget, context);
                    bodyCases++;
                }
            }
        }
    }

    private static void CheckPlan(int[] prior, int[] current, FallbackStrategy strategy, int budget, string context) {
        CountingOps.Calls = 0;
        List<ListDeltaRange> counted = FallbackMatcher<int, CountingOps>.PlanWithBudget(prior, current, Slot,
            strategy, budget, out FallbackDiagnostics countedDiagnostics);
        int calls = CountingOps.Calls;
        List<ListDeltaRange> actual = FallbackMatcher<int, Int32StateOps>.PlanWithBudget(prior, current, Slot,
            strategy, budget, out FallbackDiagnostics diagnostics);
        List<ListDeltaRange> repeated = FallbackMatcher<int, Int32StateOps>.PlanWithBudget(prior, current, Slot,
            strategy, budget, out FallbackDiagnostics repeatedDiagnostics);
        Require(counted.SequenceEqual(actual) && actual.SequenceEqual(repeated) &&
            countedDiagnostics == diagnostics && diagnostics == repeatedDiagnostics, $"Nondeterministic or instrumentation-dependent plan: {context}.");
        Require(diagnostics.InitialBudget == budget && diagnostics.BudgetRemaining >= 0 && diagnostics.BudgetRemaining <= budget &&
            diagnostics.LocalComparisons >= 0 && diagnostics.MyersComparisons >= 0 &&
            diagnostics.LocalComparisons + diagnostics.MyersComparisons == budget - diagnostics.BudgetRemaining &&
            calls == diagnostics.BaselineComparisons + diagnostics.LocalComparisons + diagnostics.MyersComparisons,
            $"Incorrect comparison accounting: {context}.");
        Require(!diagnostics.MyersSucceeded || diagnostics.MyersAttempted, $"Success without Myers invocation: {context}.");
        if (strategy == FallbackStrategy.MyersThenLocal) {
            Require(diagnostics.MyersComparisons <= budget / 2, $"Myers exceeded its first-stage allowance: {context}.");
        }

        ListDeltaAlgorithm? baseline = strategy switch {
            FallbackStrategy.Position => ListDeltaAlgorithm.Position,
            FallbackStrategy.LocalResync => ListDeltaAlgorithm.LocalResync,
            FallbackStrategy.BoundedMyers => ListDeltaAlgorithm.BoundedMyers,
            _ => null,
        };
        if (baseline.HasValue) {
            CountingOps.Calls = 0;
            List<ListDeltaRange> product = ListDeltaMatcher<int, CountingOps>.PlanWithBudget(prior, current, Slot, baseline.Value, budget);
            Require(actual.SequenceEqual(product) && calls == CountingOps.Calls && !diagnostics.Triggered,
                $"Experimental baseline differs from production: {context}.");
        }

        int target = 0, previousOldEnd = 0;
        ListDeltaRange? previous = null;
        foreach (ListDeltaRange range in actual) {
            Require(range.NewStart == target && range.Count > 0 && range.Count <= current.Length - target &&
                (range.OldStart == -1 || range.OldStart >= previousOldEnd && range.OldStart <= prior.Length - range.Count),
                $"Invalid range geometry: {context}.");
            if (previous is { } before) {
                bool mergeable = before.OldStart == -1 && range.OldStart == -1 ||
                    before.OldStart >= 0 && range.OldStart >= 0 && before.OldStart + before.Count == range.OldStart;
                Require(!mergeable, $"Adjacent mergeable ranges were not coalesced: {context}.");
            }
            if (range.OldStart >= 0) { previousOldEnd = range.OldStart + range.Count; }
            previous = range;
            target += range.Count;
        }
        Require(target == current.Length, $"Incomplete target coverage: {context}.");
    }

    private static void CheckBody(int[] oldValues, int[] newValues, FallbackStrategy strategy, int budget, string context) {
        FrozenListState<int> prior = new(oldValues), current = new(newValues);
        byte[] oldBase = ListStateBody<int, Int32StateOps>.PrepareBase(prior, Layout).Body.ToArray();
        byte[] newBase = ListStateBody<int, Int32StateOps>.PrepareBase(current, Layout).Body.ToArray();
        int planCalls = 0;
        ListDeltaPlanFactory<int> factory = (ReadOnlySpan<int> left, ReadOnlySpan<int> right, DurableFieldInfo slot) => {
            planCalls++;
            return FallbackMatcher<int, Int32StateOps>.PlanWithBudget(left, right, slot, strategy, budget, out _);
        };
        PreparedDeltaBody delta = ListStateBody<int, Int32StateOps>.PrepareDelta(prior, current, Layout, planFactory: factory);
        bool changed = !oldValues.SequenceEqual(newValues);
        Require(delta.HasChanges == changed && planCalls == (changed ? 1 : 0), $"Incorrect HasChanges or plan invocation: {context}.");
        BareValueReader reader = new(delta.Body);
        FrozenListState<int> applied = ListStateBody<int, Int32StateOps>.ApplyDelta(ref reader, prior, Layout);
        reader.EnsureFullyConsumed();
        Require(applied.Elements.SequenceEqual(newValues) && prior.Elements.SequenceEqual(oldValues) && current.Elements.SequenceEqual(newValues),
            $"Shared decoder failed or mutated input: {context}.");
        Require(oldBase.AsSpan().SequenceEqual(ListStateBody<int, Int32StateOps>.PrepareBase(prior, Layout).Body) &&
            newBase.AsSpan().SequenceEqual(ListStateBody<int, Int32StateOps>.PrepareBase(current, Layout).Body),
            $"Frozen Base representation changed during Diff/Apply: {context}.");
        PreparedDeltaBody repeated = ListStateBody<int, Int32StateOps>.PrepareDelta(prior, current, Layout, planFactory: factory);
        Require(repeated.HasChanges == delta.HasChanges && repeated.Body.SequenceEqual(delta.Body), $"Nondeterministic payload: {context}.");
    }

    private static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    private readonly struct CountingOps : IStateOps<int> {
        internal static int Calls;
        public static bool StateEquals(in int left, in int right, DurableFieldInfo slot) { Calls++; return left == right; }
        public static void WriteBase(ref BareValueWriter writer, in int state, DurableFieldInfo slot) => throw new InvalidOperationException("Matcher encoded payload.");
        public static int ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => throw new InvalidOperationException("Matcher decoded payload.");
        public static PreparedDeltaBody PrepareDelta(in int prior, in int current, DurableFieldInfo slot) => throw new InvalidOperationException("Matcher prepared payload.");
        public static int ApplyDelta(ref BareValueReader reader, in int prior, DurableFieldInfo slot) => throw new InvalidOperationException("Matcher applied payload.");
        public static void VisitReferences(in int state, IStateReferenceVisitor visitor, DurableFieldInfo slot) => throw new InvalidOperationException("Matcher visited references.");
    }
}
