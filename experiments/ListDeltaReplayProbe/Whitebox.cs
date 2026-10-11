using Atelia.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.ListDeltaReplayProbe;

internal sealed record WhiteboxCase(string Name, int InsertCount, bool ChangeTail, bool Balanced) {
    internal int LcsLength(int count) => count - (Balanced ? InsertCount : ChangeTail ? 1 : 0);
    internal int EditDistance => Balanced ? 2 * InsertCount : InsertCount + (ChangeTail ? 2 : 0);

    internal void Apply(List<int> items) {
        if (!Balanced) {
            items.InsertRange(0, Enumerable.Range(-InsertCount, InsertCount));
            if (ChangeTail) { items[^1] = -1_000_000; }
            return;
        }
        int count = items.Count;
        // Unique retained originals stay ordered. Insert in the first half, delete in the second.
        // The first insertion and last deletion prevent prefix/suffix trimming from hiding edits.
        HashSet<int> insertBefore = Enumerable.Range(0, InsertCount).Select(i => i * (count / 2) / InsertCount).ToHashSet();
        HashSet<int> delete = Enumerable.Range(1, InsertCount)
            .Select(i => count / 2 + i * (count - count / 2) / InsertCount - 1).ToHashSet();
        List<int> edited = [];
        for (int index = 0; index < count; index++) {
            if (insertBefore.Contains(index)) { edited.Add(-index - 1); }
            if (!delete.Contains(index)) { edited.Add(items[index]); }
        }
        items.Clear();
        items.AddRange(edited);
    }
}

internal sealed record MatcherObservation(string Case, int Count, int Repeat, string Algorithm, int OldCount, int NewCount,
    int KnownLcsLength, int KnownEditDistance, int ExtraComparisonBudget, int StateEqualsCalls, int EqualPairs,
    int Ranges, int LiteralElements, bool SameAsPosition, int? ComparisonLimitRelaxedEqualPairs,
    int? DepthLimitRelaxedEqualPairs, double MillisecondsMedian, double MillisecondsMin, double MillisecondsMax,
    long AllocatedBytesMedian);

internal static class Whitebox {
    private static readonly DurableFieldInfo Slot = new(1, TypeTag.Int32);
    private static readonly WhiteboxCase[] Cases = [
        new("local-block32-tail-change", 32, true, false),
        new("local-block33-tail-change", 33, true, false),
        new("control-block33-intact-tail", 33, false, false),
        new("myers-balanced64", 64, false, true),
        new("myers-balanced65", 65, false, true),
    ];

    internal static void Run(Settings settings) {
        List<RunResult> runs = [];
        List<object> warmups = [];
        List<MatcherObservation> observations = [];
        ListDeltaAlgorithm[] algorithms = Enum.GetValues<ListDeltaAlgorithm>();
        int ordinal = 0;
        foreach (WhiteboxCase scenario in Cases) {
            Workload<int> workload = new(scenario.Name, (id, _) => id, value => value,
                value => value + 10_000_000, value => value.ToString());
            Edit[] script = [new(0, scenario.Name, 0, 0)];
            foreach (ListDeltaAlgorithm algorithm in algorithms) {
                RunResult warm = Replay.Run(workload, 512, -1, algorithm,
                    Path.Combine(settings.Output, $"warmup-{scenario.Name}-{algorithm}"), script, settings,
                    applyEdit: (world, _) => scenario.Apply(world.Items));
                warmups.Add(new { Case = scenario.Name, Algorithm = algorithm.ToString(), warm.SetupMs,
                    InitialCommitMs = warm.Steps[0].CommitMs, EditCommitMs = warm.Steps[1].CommitMs });
            }
            foreach (int count in settings.Counts) {
                int[] prior = Enumerable.Range(0, count).ToArray();
                List<int> changed = prior.ToList();
                scenario.Apply(changed);
                int[] current = changed.ToArray();
                // Independent fixture check: all common values are unique and remain ordered.
                int[] retained = current.Where(value => value >= 0).ToArray();
                Require(retained.Length == scenario.LcsLength(count) && retained.SequenceEqual(retained.Order()) &&
                    current.Distinct().Count() == current.Length &&
                    prior.Length + current.Length - 2 * retained.Length == scenario.EditDistance, "Invalid adverse fixture.");
                for (int repeat = 0; repeat < settings.Repeats; repeat++) {
                    for (int offset = 0; offset < algorithms.Length; offset++) {
                        ListDeltaAlgorithm algorithm = algorithms[(offset + repeat + ordinal) % algorithms.Length];
                        string path = Path.Combine(settings.Output, $"{scenario.Name}-n{count}-r{repeat}-{algorithm}");
                        RunResult run = Replay.Run(workload, count, repeat, algorithm, path, script, settings,
                            applyEdit: (world, _) => scenario.Apply(world.Items));
                        runs.Add(run);
                        StepResult edit = run.Steps[1];
                        if (algorithm is ListDeltaAlgorithm.Position or ListDeltaAlgorithm.LocalResync or ListDeltaAlgorithm.BoundedMyers) {
                            MatcherObservation observation = Observe(scenario, prior, current, repeat, algorithm, settings.DiffRepeats);
                            observations.Add(observation);
                            Console.WriteLine($"{scenario.Name} n={count} r={repeat} {algorithm}: matches={observation.EqualPairs}/{observation.KnownLcsLength}, " +
                                $"comparisons={observation.StateEqualsCalls}, candidate={edit.Diff!.DeltaBodyBytes}, actual={edit.ListWriteKind}:{edit.ListPayloadBytes}");
                        } else {
                            Console.WriteLine($"{scenario.Name} n={count} r={repeat} {algorithm}: {edit.Diff!.Diagnostics.Outcome}, " +
                                $"candidate={edit.Diff.DeltaBodyBytes}, actual={edit.ListWriteKind}:{edit.ListPayloadBytes}");
                        }
                    }
                }
                ordinal++;
            }
        }
        Report.Write(settings, Cases.Select(c => new Edit(0, c.Name, 0, 0)).ToArray(), runs, warmups);
        File.WriteAllText(Path.Combine(settings.Output, "whitebox.json"), JsonSerializer.Serialize(new {
            Cases, Observations = observations,
            Writers = runs.Select(run => new { Case = run.Workload, run.Count, run.Repeat, run.Algorithm,
                run.Steps[1].Diff, run.Steps[1].ListWriteKind, run.Steps[1].ListPayloadBytes }).ToArray(),
            Measurement = "StateEquals counts use an untimed instrumented ops wrapper; matcher time/allocation use real Int32StateOps. " +
                "32 matcher warmups per case/algorithm precede samples. Counts include prefix/suffix, exclude body emission. " +
                "Relaxed-bound probes are diagnostic only; all saved payloads use unchanged product defaults. " +
                "Standalone matcher observations cover only Position/LocalResync/BoundedMyers. Adaptive is measured through the product writer, " +
                "with untimed whole-writer equality/child-call counts and actual competition outcome/cutoff bytes; no Plan(Adaptive) is called. " +
                "See report.json for environment, binary fingerprints, settings, real Commit and isolated full Delta measurements."
        }, new JsonSerializerOptions { WriteIndented = true }));
        StringBuilder text = new("# White-box matcher observations\n\nTwo adverse families plus boundary controls; no absolute worst-case or occurrence-rate claim.\n\n");
        text.AppendLine("| Case | N | Algorithm | Equal pairs / known LCS | Comparisons | Matcher ms median | Matcher allocated bytes | Raw Delta bytes | Actual List write |")
            .AppendLine("|---|---:|---|---:|---:|---:|---:|---:|---|");
        foreach (var group in observations.GroupBy(o => (o.Case, o.Count, o.Algorithm))) {
            MatcherObservation first = group.First();
            StepResult edit = runs.First(r => r.Workload == first.Case && r.Count == first.Count && r.Algorithm == first.Algorithm).Steps[1];
            double[] times = group.Select(o => o.MillisecondsMedian).Order().ToArray();
            text.AppendLine($"| {first.Case} | {first.Count} | {first.Algorithm} | {first.EqualPairs} / {first.KnownLcsLength} | " +
                $"{first.StateEqualsCalls} | {Statistics.Median(times):F4} | {first.AllocatedBytesMedian} | {edit.Diff!.DeltaBodyBytes} | {edit.ListWriteKind} {edit.ListPayloadBytes} |");
        }
        text.AppendLine().AppendLine("## Complete product writers").AppendLine()
            .AppendLine("Adaptive has no standalone matcher metric. Counts below include the whole writer; timing excludes instrumentation.").AppendLine()
            .AppendLine("| Case | N | Writer | Raw Delta B | Diff ms | Allocation B | Outcome | Challenger written B | Actual List write |")
            .AppendLine("|---|---:|---|---:|---:|---:|---|---:|---|");
        foreach (var group in runs.GroupBy(run => (run.Workload, run.Count, run.Algorithm))) {
            StepResult edit = group.First().Steps[1];
            DiffResult diff = edit.Diff!;
            text.AppendLine($"| {group.Key.Workload} | {group.Key.Count} | {group.Key.Algorithm} | {diff.DeltaBodyBytes} | " +
                $"{Statistics.Median(group.Select(run => run.Steps[1].Diff!.MillisecondsMedian).Order().ToArray()):F4} | {diff.AllocatedBytesMedian} | " +
                $"{diff.Diagnostics.Outcome} | {diff.Diagnostics.ChallengerWrittenBytes} | {edit.ListWriteKind} {edit.ListPayloadBytes} |");
        }
        File.WriteAllText(Path.Combine(settings.Output, "whitebox.md"), text.ToString());
        Console.WriteLine($"Whitebox passed: {runs.Count} measured repositories; {runs.Sum(r => r.Steps.Length)} validated revisions. Reports: {settings.Output}");
    }

    private static MatcherObservation Observe(WhiteboxCase scenario, int[] prior, int[] current, int repeat, ListDeltaAlgorithm algorithm, int repeats) {
        CountingOps.Calls = 0;
        List<ListDeltaRange> counted = ListDeltaMatcher<int, CountingOps>.Plan(prior, current, Slot, algorithm);
        int comparisons = CountingOps.Calls;
        List<ListDeltaRange> plan = ListDeltaMatcher<int, Int32StateOps>.Plan(prior, current, Slot, algorithm);
        Require(counted.SequenceEqual(plan), "Instrumentation changed the plan.");
        int target = 0;
        foreach (ListDeltaRange range in plan) {
            Require(range.NewStart == target && range.Count > 0 && range.Count <= current.Length - target &&
                (range.OldStart == -1 || range.OldStart >= 0 && range.OldStart <= prior.Length - range.Count), "Invalid plan coordinates.");
            target += range.Count;
        }
        Require(target == current.Length, "Incomplete target.");
        int equalPairs = EqualPairs(plan, prior, current);
        int budget = (int)Math.Min(1_000_000L, 4096L + 8L * (prior.Length + (long)current.Length));
        int? comparisonRelaxed = null, depthRelaxed = null;
        if (algorithm == ListDeltaAlgorithm.BoundedMyers && scenario.Balanced) {
            comparisonRelaxed = EqualPairs(ListDeltaMatcher<int, Int32StateOps>.PlanWithBudget(prior, current, Slot, algorithm,
                int.MaxValue), prior, current);
            depthRelaxed = EqualPairs(ListDeltaMatcher<int, Int32StateOps>.PlanWithBudget(prior, current, Slot, algorithm,
                budget, maximumEditDepth: scenario.EditDistance), prior, current);
            int bothRelaxed = EqualPairs(ListDeltaMatcher<int, Int32StateOps>.PlanWithBudget(prior, current, Slot, algorithm,
                int.MaxValue, maximumEditDepth: scenario.EditDistance), prior, current);
            Require(bothRelaxed == scenario.LcsLength(prior.Length), "Myers failed known optimum with sufficient bounds.");
            Require(comparisonRelaxed == (scenario.InsertCount == 64 ? scenario.LcsLength(prior.Length) : 0), "Unexpected depth boundary.");
        }
        if (algorithm == ListDeltaAlgorithm.LocalResync) {
            Require(equalPairs == (scenario.Name == "local-block33-tail-change" ? 0 : scenario.LcsLength(prior.Length)), "Unexpected local-window boundary.");
            if (scenario.Name == "local-block33-tail-change") {
                Require(comparisons == budget + 2, "Expected full extra budget plus two failed prefix/suffix comparisons.");
            }
        }
        if (algorithm == ListDeltaAlgorithm.BoundedMyers && !scenario.Balanced) {
            Require(equalPairs == scenario.LcsLength(prior.Length), "Myers missed the small block edit.");
        }
        // No counters in timing: run actual static ops, keep output alive, and verify determinism.
        for (int warm = 0; warm < 32; warm++) { _ = ListDeltaMatcher<int, Int32StateOps>.Plan(prior, current, Slot, algorithm); }
        double[] times = new double[repeats];
        long[] allocations = new long[repeats];
        for (int index = 0; index < repeats; index++) {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            List<ListDeltaRange> sample = ListDeltaMatcher<int, Int32StateOps>.Plan(prior, current, Slot, algorithm);
            times[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            allocations[index] = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Require(sample.SequenceEqual(plan), "Nondeterministic matcher.");
        }
        Array.Sort(times); Array.Sort(allocations);
        return new(scenario.Name, prior.Length, repeat, algorithm.ToString(), prior.Length, current.Length, scenario.LcsLength(prior.Length),
            scenario.EditDistance, budget, comparisons, equalPairs, plan.Count, plan.Where(r => r.OldStart == -1).Sum(r => r.Count),
            plan.SequenceEqual(ListDeltaMatcher<int, Int32StateOps>.Plan(prior, current, Slot, ListDeltaAlgorithm.Position)),
            comparisonRelaxed, depthRelaxed, Statistics.Median(times), times[0], times[^1], allocations[allocations.Length / 2]);
    }

    private static int EqualPairs(List<ListDeltaRange> ranges, int[] prior, int[] current) {
        int equal = 0;
        foreach (ListDeltaRange range in ranges.Where(r => r.OldStart >= 0)) {
            for (int index = 0; index < range.Count; index++) {
                if (prior[range.OldStart + index] == current[range.NewStart + index]) { equal++; }
            }
        }
        return equal;
    }

    private static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    private readonly struct CountingOps : IStateOps<int> {
        internal static int Calls;
        public static bool StateEquals(in int left, in int right, DurableFieldInfo slot) { Calls++; return left == right; }
        public static void WriteBase(ref BareValueWriter writer, in int state, DurableFieldInfo slot) => throw new InvalidOperationException("Matcher encoded payload.");
        public static int ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => throw new InvalidOperationException();
        public static PreparedDeltaBody PrepareDelta(in int prior, in int current, DurableFieldInfo slot) => throw new InvalidOperationException("Matcher prepared payload.");
        public static int ApplyDelta(ref BareValueReader reader, in int prior, DurableFieldInfo slot) => throw new InvalidOperationException();
        public static void VisitReferences(in int state, IStateReferenceVisitor visitor, DurableFieldInfo slot) => throw new InvalidOperationException();
    }
}
