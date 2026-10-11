using Atelia.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.ListDeltaReplayProbe;

internal sealed record FallbackObservation(string Group, string Name, int Count, int Step, int Repeat, string Strategy,
    bool HasChanges, int BaseBytes, int DeltaBytes, int EqualPairs, int LiteralElements,
    FallbackDiagnostics? Diagnostics, double DiffMs, double DiffMinMs, double DiffMaxMs, long DiffAllocatedBytes,
    double MatcherMs, long MatcherAllocatedBytes, string PriorFingerprint, string CurrentFingerprint);

internal static class FallbackTrial {
    private delegate FallbackObservation Runner(FallbackFixture fixture, FallbackStrategy strategy, int repeat, int samples);

    internal static void Run(Settings settings) {
        object checks = FallbackChecks.Run();
        FallbackFixture[] fixtures = FallbackFixtures.Create(settings).ToArray();
        FallbackStrategy[] strategies = Enum.GetValues<FallbackStrategy>();
        Dictionary<(Type, Type), Runner> runners = [];
        List<FallbackObservation> rows = [];
        int ordinal = 0;
        foreach (FallbackFixture fixture in fixtures) {
            var key = (fixture.Element.StateType, fixture.Element.StateOpsType);
            if (!runners.TryGetValue(key, out Runner? runner)) {
                runner = typeof(FallbackTrial).GetMethod(nameof(Measure), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(key.Item1, key.Item2).CreateDelegate<Runner>();
                runners.Add(key, runner);
            }
            for (int repeat = 0; repeat < settings.Repeats; repeat++) {
                for (int offset = 0; offset < strategies.Length; offset++) {
                    FallbackStrategy strategy = strategies[(offset + repeat + ordinal) % strategies.Length];
                    rows.Add(runner(fixture, strategy, repeat, settings.DiffRepeats));
                }
            }
            ordinal++;
            Console.WriteLine($"Fallback {ordinal}/{fixtures.Length}: {fixture.Group}/{fixture.Name} n={fixture.Count} verified.");
        }
        foreach (var group in rows.GroupBy(r => (r.Group, r.Name, r.Count, r.Step))) {
            Require(group.Select(r => (r.PriorFingerprint, r.CurrentFingerprint)).Distinct().Count() == 1,
                "Strategies received different frozen states.");
        }
        var summary = rows.GroupBy(r => (r.Group, r.Name, r.Count, r.Step, r.Strategy)).Select(g => new {
            g.Key.Group, g.Key.Name, g.Key.Count, g.Key.Step, g.Key.Strategy,
            g.First().HasChanges, g.First().BaseBytes, g.First().DeltaBytes, g.First().EqualPairs, g.First().LiteralElements,
            g.First().Diagnostics, DiffMs = Statistics.Describe(g.Select(r => r.DiffMs)),
            DiffAllocatedBytes = Statistics.Describe(g.Select(r => (double)r.DiffAllocatedBytes)),
            MatcherMs = Statistics.Describe(g.Select(r => r.MatcherMs)),
            MatcherAllocatedBytes = Statistics.Describe(g.Select(r => (double)r.MatcherAllocatedBytes)),
        }).ToArray();
        object report = new {
            Settings = settings, GeneratedUtc = DateTime.UtcNow,
            Environment = new { Runtime = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture, Environment.ProcessorCount, ServerGC = System.Runtime.GCSettings.IsServerGC,
                TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "runtime-default",
                Binaries = new[] { typeof(FallbackTrial).Assembly, typeof(DurableSchema).Assembly }
                    .Select(a => new { a.FullName, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location))) }).ToArray() },
            Measurement = "All strategies consume the same generated captured frozen pair. Complete Diff is the shared product body writer with a research plan factory. " +
                "Reflection, Capture, Base encoding, eight warmups, validation and fingerprints excluded. Matcher uses sixteen warmups. " +
                "Counts from untimed diagnostics are checked against an independent StateEquals counter; timings use real element ops. " +
                "NoChange skips the matcher (null diagnostics, zero matcher metrics). Allocations are current-thread managed totals, not peak memory. " +
                "Raw body bytes only: this mode does not publish repositories, simulate cumulative policy choices or measure complete Commit. X/Y retained in Settings but unused.",
            CorrectnessChecks = checks, Fixtures = fixtures.Length, Observations = rows.Count, Summary = summary, Rows = rows,
        };
        File.WriteAllText(Path.Combine(settings.Output, "fallback.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        StringBuilder text = new("# Fallback trial\n\nAll five strategies Apply-validated on the same captured pairs. Raw payloads, not actual policy-selected writes.\n\n");
        text.AppendLine("| Group | Case | N | Strategy | Delta B | Exact pairs | Literals | Diff ms | Diff allocation B | Triggered |")
            .AppendLine("|---|---|---:|---|---:|---:|---:|---:|---:|---|");
        foreach (var g in rows.GroupBy(r => (r.Group, r.Name, r.Count, r.Step, r.Strategy))) {
            FallbackObservation first = g.First();
            text.AppendLine($"| {first.Group} | {first.Name} | {first.Count} | {first.Strategy} | {first.DeltaBytes} | {first.EqualPairs} | {first.LiteralElements} | " +
                $"{Statistics.Median(g.Select(r => r.DiffMs).Order().ToArray()):F4} | {first.DiffAllocatedBytes} | {first.Diagnostics?.Triggered} |");
        }
        File.WriteAllText(Path.Combine(settings.Output, "fallback.md"), text.ToString());
        Console.WriteLine($"Fallback passed: {fixtures.Length} pairs, {rows.Count} measured/validated observations. Reports: {settings.Output}");
    }

    private static FallbackObservation Measure<TState, TOps>(FallbackFixture fixture, FallbackStrategy strategy, int repeat, int samples)
        where TState : unmanaged where TOps : IStateOps<TState> {
        FrozenListState<TState> prior = fixture.Prior.GetListState<TState>();
        FrozenListState<TState> current = fixture.Current.GetListState<TState>();
        ListLayout layout = fixture.Current.Layout.List!;
        byte[] priorBytes = ListStateBody<TState, TOps>.PrepareBase(prior, layout).Body.ToArray();
        byte[] currentBytes = ListStateBody<TState, TOps>.PrepareBase(current, layout).Body.ToArray();
        ListDeltaPlanFactory<TState> factory = Factories<TState, TOps>.All[(int)strategy];
        PreparedDeltaBody Prepare() => ListStateBody<TState, TOps>.PrepareDelta(prior, current, layout, planFactory: factory);
        PreparedDeltaBody delta = Prepare();
        List<ListDeltaRange> plan = FallbackMatcher<TState, TOps>.Plan(prior.Elements, current.Elements, layout.ElementSlot, strategy, out FallbackDiagnostics diag);
        CountedOps<TState, TOps>.Calls = 0;
        List<ListDeltaRange> counted = FallbackMatcher<TState, CountedOps<TState, TOps>>.Plan(prior.Elements, current.Elements, layout.ElementSlot, strategy, out FallbackDiagnostics countedDiag);
        Require(plan.SequenceEqual(counted) && diag == countedDiag && CountedOps<TState, TOps>.Calls ==
            diag.BaselineComparisons + diag.LocalComparisons + diag.MyersComparisons &&
            diag.LocalComparisons + diag.MyersComparisons == diag.InitialBudget - diag.BudgetRemaining &&
            diag.BudgetRemaining >= 0, "Comparison accounting or instrumentation mismatch.");
        if ((int)strategy <= (int)FallbackStrategy.BoundedMyers) {
            ListDeltaAlgorithm original = (ListDeltaAlgorithm)(int)strategy;
            Require(plan.SequenceEqual(ListDeltaMatcher<TState, TOps>.Plan(prior.Elements, current.Elements, layout.ElementSlot, original)) &&
                delta.Body.SequenceEqual(ListStateBody<TState, TOps>.PrepareDelta(prior, current, layout, original).Body), "Baseline drift.");
        }
        int equalPairs = 0, target = 0;
        foreach (ListDeltaRange range in plan) {
            Require(range.NewStart == target && range.Count > 0 && range.Count <= current.Count - target &&
                (range.OldStart == -1 || range.OldStart >= 0 && range.OldStart <= prior.Count - range.Count), "Invalid plan.");
            if (range.OldStart >= 0) {
                for (int index = 0; index < range.Count; index++) {
                    if (TOps.StateEquals(in prior.OwnedElements[range.OldStart + index], in current.OwnedElements[range.NewStart + index], layout.ElementSlot)) { equalPairs++; }
                }
            }
            target += range.Count;
        }
        Require(target == current.Count, "Incomplete plan.");
        for (int warm = 0; warm < 8; warm++) { _ = Prepare(); }
        double[] times = new double[samples];
        long[] allocations = new long[samples];
        for (int index = 0; index < samples; index++) {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            PreparedDeltaBody sample = Prepare();
            times[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            allocations[index] = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Require(sample.HasChanges == delta.HasChanges && sample.Body.SequenceEqual(delta.Body), "Nondeterministic bytes.");
        }
        Array.Sort(times); Array.Sort(allocations);
        double matcherMs = 0;
        long matcherAllocated = 0;
        if (delta.HasChanges) {
            for (int warm = 0; warm < 16; warm++) { _ = factory(prior.Elements, current.Elements, layout.ElementSlot); }
            double[] matchTimes = new double[samples];
            long[] matchAllocations = new long[samples];
            for (int index = 0; index < samples; index++) {
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                List<ListDeltaRange> sample = factory(prior.Elements, current.Elements, layout.ElementSlot);
                matchTimes[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                matchAllocations[index] = GC.GetAllocatedBytesForCurrentThread() - allocated;
                Require(sample.SequenceEqual(plan), "Nondeterministic plan.");
            }
            Array.Sort(matchTimes); Array.Sort(matchAllocations);
            matcherMs = Statistics.Median(matchTimes); matcherAllocated = matchAllocations[samples / 2];
        }
        BareValueReader reader = new(delta.Body);
        FrozenListState<TState> applied = ListStateBody<TState, TOps>.ApplyDelta(ref reader, prior, layout);
        reader.EnsureFullyConsumed();
        Require(delta.HasChanges == !priorBytes.AsSpan().SequenceEqual(currentBytes) &&
            ListStateBody<TState, TOps>.PrepareBase(applied, layout).Body.SequenceEqual(currentBytes) &&
            ListStateBody<TState, TOps>.PrepareBase(prior, layout).Body.SequenceEqual(priorBytes) &&
            ListStateBody<TState, TOps>.PrepareBase(current, layout).Body.SequenceEqual(currentBytes), "Roundtrip or frozen input mutation.");
        return new(fixture.Group, fixture.Name, fixture.Count, fixture.Step, repeat, strategy.ToString(), delta.HasChanges,
            currentBytes.Length, delta.Body.Length, equalPairs, plan.Where(r => r.OldStart < 0).Sum(r => r.Count),
            delta.HasChanges ? diag : null, Statistics.Median(times), times[0], times[^1], allocations[samples / 2], matcherMs, matcherAllocated,
            Convert.ToHexString(SHA256.HashData(priorBytes)), Convert.ToHexString(SHA256.HashData(currentBytes)));
    }

    private static class Factories<TState, TOps> where TState : unmanaged where TOps : IStateOps<TState> {
        internal static readonly ListDeltaPlanFactory<TState>[] All = Enum.GetValues<FallbackStrategy>()
            .Select<FallbackStrategy, ListDeltaPlanFactory<TState>>(strategy => (prior, current, slot) =>
                FallbackMatcher<TState, TOps>.Plan(prior, current, slot, strategy, out _)).ToArray();
    }

    private readonly struct CountedOps<TState, TOps> : IStateOps<TState> where TState : unmanaged where TOps : IStateOps<TState> {
        internal static int Calls;
        public static bool StateEquals(in TState left, in TState right, DurableFieldInfo slot) { Calls++; return TOps.StateEquals(in left, in right, slot); }
        public static void WriteBase(ref BareValueWriter writer, in TState value, DurableFieldInfo slot) => throw new InvalidOperationException();
        public static TState ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => throw new InvalidOperationException();
        public static PreparedDeltaBody PrepareDelta(in TState prior, in TState current, DurableFieldInfo slot) => throw new InvalidOperationException();
        public static TState ApplyDelta(ref BareValueReader reader, in TState prior, DurableFieldInfo slot) => throw new InvalidOperationException();
        public static void VisitReferences(in TState state, IStateReferenceVisitor visitor, DurableFieldInfo slot) => throw new InvalidOperationException();
    }

    private static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
