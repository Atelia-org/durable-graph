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

internal sealed record AdaptiveObservation(string Group, string Name, int Count, int Step, int Repeat, string Writer,
    DiffResult Diff, string PriorFingerprint, string CurrentFingerprint);

/// <summary>DB-051 product writers on the exact captured fixture matrix used by the fallback research.</summary>
internal static class AdaptiveTrial {
    private delegate AdaptiveObservation Runner(FallbackFixture fixture, ListDeltaAlgorithm algorithm, int repeat, int samples);

    internal static void Run(Settings settings) {
        FallbackFixture[] fixtures = FallbackFixtures.Create(settings).ToArray();
        ListDeltaAlgorithm[] writers = Enum.GetValues<ListDeltaAlgorithm>();
        Dictionary<(Type, Type), Runner> runners = [];
        List<AdaptiveObservation> rows = [];
        int ordinal = 0;
        foreach (FallbackFixture fixture in fixtures) {
            var key = (fixture.Element.StateType, fixture.Element.StateOpsType);
            if (!runners.TryGetValue(key, out Runner? runner)) {
                runner = typeof(AdaptiveTrial).GetMethod(nameof(Measure), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(key.Item1, key.Item2).CreateDelegate<Runner>();
                runners.Add(key, runner);
            }
            for (int repeat = 0; repeat < settings.Repeats; repeat++) {
                for (int offset = 0; offset < writers.Length; offset++) {
                    ListDeltaAlgorithm writer = writers[(offset + repeat + ordinal) % writers.Length];
                    rows.Add(runner(fixture, writer, repeat, settings.DiffRepeats));
                }
            }
            ordinal++;
            Console.WriteLine($"Adaptive {ordinal}/{fixtures.Length}: {fixture.Group}/{fixture.Name} n={fixture.Count} verified.");
        }
        foreach (var group in rows.GroupBy(row => (row.Group, row.Name, row.Count, row.Step))) {
            Require(group.Select(row => (row.PriorFingerprint, row.CurrentFingerprint)).Distinct().Count() == 1,
                "Writers did not consume identical frozen states.");
            AdaptiveObservation local = group.First(row => row.Writer == nameof(ListDeltaAlgorithm.LocalResync));
            foreach (AdaptiveObservation adaptive in group.Where(row => row.Writer == nameof(ListDeltaAlgorithm.Adaptive))) {
                Require(adaptive.Diff.HasChanges == local.Diff.HasChanges && adaptive.Diff.DeltaBodyBytes <= local.Diff.DeltaBodyBytes,
                    "Adaptive regressed relative to the separately measured Local writer.");
            }
        }
        var summaries = rows.GroupBy(row => (row.Group, row.Name, row.Count, row.Step, row.Writer)).Select(group => new {
            group.Key.Group, group.Key.Name, group.Key.Count, group.Key.Step, group.Key.Writer,
            group.First().Diff.HasChanges, group.First().Diff.BaseBodyBytes, group.First().Diff.DeltaBodyBytes,
            group.First().Diff.Diagnostics,
            DiffMs = Statistics.Describe(group.Select(row => row.Diff.MillisecondsMedian)),
            DiffAllocatedBytes = Statistics.Describe(group.Select(row => (double)row.Diff.AllocatedBytesMedian)),
        }).ToArray();
        object report = new {
            Settings = settings, GeneratedUtc = DateTime.UtcNow,
            Environment = new { Runtime = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture, Environment.ProcessorCount, ServerGC = System.Runtime.GCSettings.IsServerGC,
                TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "runtime-default",
                Binaries = new[] { typeof(AdaptiveTrial).Assembly, typeof(DurableSchema).Assembly }
                    .Select(assembly => new { assembly.FullName, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))) }).ToArray() },
            Measurement = "Four product writers consume the same generated captured frozen pair. Complete Diff calls the actual product entry, " +
                "without a plan factory or research coordinator. Reflection, Capture, Base encoding, eight warmups, validation, counters and fingerprints excluded. " +
                "NoChange is included. Allocation is current-thread managed totals, not peak memory. StateEqualsCalls includes NoChange scan, matching and encoding; " +
                "ElementBaseWrites/ElementDeltaPrepares count direct element calls, not recursively expanded struct fields. " +
                "Adaptive diagnostics record the complete incumbent, independent per-matcher B, outcome and actual challenger bytes at completion/cutoff; " +
                "a child encoding can overshoot the limit. Adaptive is not measured as a standalone matcher. " +
                "Raw body bytes only: no publication, cumulative policy simulation or complete Commit. X/Y retained in Settings but unused.",
            Acceptance = "Every body Apply-validated; both inputs frozen; instrumented and timed bytes identical. " +
                "Each Adaptive HasChanges agrees with explicit Local, and its body never exceeds Local. No time/allocation nonregression gate.",
            Fixtures = fixtures.Length, Observations = rows.Count, Summary = summaries, Rows = rows,
        };
        File.WriteAllText(Path.Combine(settings.Output, "adaptive.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        StringBuilder text = new("# Adaptive product-writer trial\n\nAll four writers Apply-validated on identical captured pairs; Adaptive never exceeded explicit Local body bytes.\n\n");
        text.AppendLine("Raw body bytes, not policy-selected writes. Whole-writer counters are untimed; allocation is cumulative, not peak memory.").AppendLine()
            .AppendLine("| Group | Case | N | Writer | Delta B | Diff ms | Allocation B | Equals / Base / Delta calls | Outcome | Challenger B | B per matcher |")
            .AppendLine("|---|---|---:|---|---:|---:|---:|---|---|---:|---:|");
        foreach (var group in rows.GroupBy(row => (row.Group, row.Name, row.Count, row.Step, row.Writer))) {
            AdaptiveObservation first = group.First();
            ProductDiffDiagnostics diagnostic = first.Diff.Diagnostics;
            text.AppendLine($"| {first.Group} | {first.Name} | {first.Count} | {first.Writer} | {first.Diff.DeltaBodyBytes} | " +
                $"{Statistics.Median(group.Select(row => row.Diff.MillisecondsMedian).Order().ToArray()):F4} | {first.Diff.AllocatedBytesMedian} | " +
                $"{diagnostic.StateEqualsCalls} / {diagnostic.ElementBaseWrites} / {diagnostic.ElementDeltaPrepares} | {diagnostic.Outcome} | " +
                $"{diagnostic.ChallengerWrittenBytes} | {diagnostic.SearchBudgetPerMatcher} |");
        }
        File.WriteAllText(Path.Combine(settings.Output, "adaptive.md"), text.ToString());
        Console.WriteLine($"Adaptive passed: {fixtures.Length} frozen pairs, {rows.Count} measured/validated observations. Reports: {settings.Output}");
    }

    private static AdaptiveObservation Measure<TState, TOps>(FallbackFixture fixture, ListDeltaAlgorithm algorithm, int repeat, int samples)
        where TState : unmanaged where TOps : IStateOps<TState> {
        FrozenListState<TState> prior = fixture.Prior.GetListState<TState>();
        FrozenListState<TState> current = fixture.Current.GetListState<TState>();
        ListLayout layout = fixture.Current.Layout.List!;
        byte[] priorBytes = ListStateBody<TState, TOps>.PrepareBase(prior, layout).Body.ToArray();
        byte[] currentBytes = ListStateBody<TState, TOps>.PrepareBase(current, layout).Body.ToArray();
        PreparedDeltaBody Prepare() => ListStateBody<TState, TOps>.PrepareDelta(prior, current, layout, algorithm);
        PreparedDeltaBody delta = Prepare();
        for (int warm = 0; warm < 8; warm++) { _ = Prepare(); }
        double[] times = new double[samples];
        long[] allocations = new long[samples];
        for (int index = 0; index < samples; index++) {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            PreparedDeltaBody sample = Prepare();
            times[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            allocations[index] = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Require(sample.HasChanges == delta.HasChanges && sample.Body.SequenceEqual(delta.Body), "Nondeterministic product bytes.");
        }
        ProductDiffDiagnostics diagnostics = ProductDiagnostics.Observe<TState, TOps>(prior, current, layout, algorithm, delta);
        BareValueReader reader = new(delta.Body);
        FrozenListState<TState> applied = ListStateBody<TState, TOps>.ApplyDelta(ref reader, prior, layout);
        reader.EnsureFullyConsumed();
        Require(delta.HasChanges == !priorBytes.AsSpan().SequenceEqual(currentBytes) &&
            ListStateBody<TState, TOps>.PrepareBase(applied, layout).Body.SequenceEqual(currentBytes) &&
            ListStateBody<TState, TOps>.PrepareBase(prior, layout).Body.SequenceEqual(priorBytes) &&
            ListStateBody<TState, TOps>.PrepareBase(current, layout).Body.SequenceEqual(currentBytes), "Roundtrip or frozen input mutation.");
        Array.Sort(times); Array.Sort(allocations);
        DiffResult diff = new(currentBytes.Length, delta.Body.Length, delta.HasChanges,
            Statistics.Median(times), times[0], times[^1], allocations[samples / 2], diagnostics);
        return new(fixture.Group, fixture.Name, fixture.Count, fixture.Step, repeat, algorithm.ToString(), diff,
            Convert.ToHexString(SHA256.HashData(priorBytes)), Convert.ToHexString(SHA256.HashData(currentBytes)));
    }

    private static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
