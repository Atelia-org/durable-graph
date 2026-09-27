using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Persistence;

namespace Atelia.ListDeltaReplayProbe;

internal sealed record Settings(int[] Counts, int Repeats, int Rounds, int DiffRepeats, int Seed,
    int ReadAmplification, int BaseBudgetPercent, string Output, string Baseline, string Suite) {
    internal static Settings Parse(string[] args) {
        Dictionary<string, string> values = [];
        if (args.Length % 2 != 0) { throw new ArgumentException("Arguments must be --name value pairs; see README."); }
        for (int index = 0; index < args.Length; index += 2) {
            if (!values.TryAdd(args[index], args[index + 1])) { throw new ArgumentException("Repeated argument."); }
        }
        string Get(string key, string fallback) => values.Remove(key, out string? value) ? value : fallback;
        string suite = Get("--suite", "ordinary");
        int[] counts = Get("--counts", suite == "whitebox" ? "4096" : "32").Split(',').Select(int.Parse).Distinct().Order().ToArray();
        int repeats = int.Parse(Get("--repeats", "1"));
        int rounds = int.Parse(Get("--rounds", "1"));
        int diffRepeats = int.Parse(Get("--diff-repeats", "3"));
        int seed = int.Parse(Get("--seed", "49001"));
        int x = int.Parse(Get("--x", "8"));
        int y = int.Parse(Get("--y", "5"));
        string output = Path.GetFullPath(Get("--output", Path.Combine("experiments", "ListDeltaReplayProbe", "obj", "run-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..8])));
        string baseline = Get("--baseline", "not-supplied");
        if (values.Count != 0 || suite is not ("ordinary" or "whitebox" or "fallback" or "adaptive") ||
            (suite == "whitebox" && (counts.Any(count => count < 512) || rounds != 1)) ||
            counts.Length == 0 || counts.Any(count => count is < 8 or > 50_000) ||
            repeats is < 1 or > 20 || rounds is < 1 or > 20 || diffRepeats is < 1 or > 100 || x < 1 || y is < 1 or > 100) {
            throw new ArgumentException("Unknown argument or out-of-range counts/repeats/rounds/policy; see README.");
        }
        return new(counts, repeats, rounds, diffRepeats, seed, x, y, output, baseline, suite);
    }
}

internal static class Program {
    private static void Main(string[] args) {
#if DEBUG
        throw new InvalidOperationException("This experiment requires a Release build.");
#else
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Settings settings = Settings.Parse(args);
        if (Directory.Exists(settings.Output) || File.Exists(settings.Output)) { throw new IOException("Output must be a fresh path; no previous repository may be reused."); }
        Directory.CreateDirectory(settings.Output);
        if (settings.Suite == "whitebox") { Whitebox.Run(settings); return; }
        if (settings.Suite == "fallback") { FallbackTrial.Run(settings); return; }
        if (settings.Suite == "adaptive") { AdaptiveTrial.Run(settings); return; }
        Edit[] script = Scripts.Create(settings.Seed, settings.Rounds);
        List<RunResult> runs = [];
        List<object> warmups = [];
        int ordinal = 0;
        RunWorkload(Workloads.Integers, settings.Counts);
        RunWorkload(Workloads.Alternating, settings.Counts);
        RunWorkload(Workloads.References, settings.Counts);
        RunWorkload(Workloads.Small, settings.Counts);
        // Bound total DTO size instead of crossing the widest shape with every largest count.
        RunWorkload(Workloads.Large, settings.Counts.Select(count => Math.Min(count, 512)).Distinct().ToArray());
        Report.Write(settings, script, runs, warmups);
        Console.WriteLine($"Replay passed: {runs.Count} independent measured repositories; {runs.Sum(run => run.Steps.Length)} validated revisions.");
        Console.WriteLine($"Reports: {settings.Output}");

        void RunWorkload<T>(Workload<T> workload, int[] counts) {
            ListDeltaAlgorithm[] algorithms = Enum.GetValues<ListDeltaAlgorithm>();
            foreach (ListDeltaAlgorithm algorithm in algorithms) {
                long started = Stopwatch.GetTimestamp();
                // Warmup includes first bindings, JIT, a real initial Commit and three edits.
                RunResult warm = Replay.Run(workload, 8, -1, algorithm,
                    Path.Combine(settings.Output, $"warmup-{workload.Name}-{algorithm}"), script, settings, operationLimit: 3);
                warmups.Add(new { Workload = workload.Name, Algorithm = algorithm.ToString(), TotalMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    warm.SetupMs, InitialCommitMs = warm.Steps[0].CommitMs });
            }
            foreach (int count in counts) {
                for (int repeat = 0; repeat < settings.Repeats; repeat++) {
                    // Rotate which algorithm runs first between repetitions and workloads.
                    for (int offset = 0; offset < algorithms.Length; offset++) {
                        ListDeltaAlgorithm algorithm = algorithms[(offset + repeat + ordinal) % algorithms.Length];
                        string path = Path.Combine(settings.Output, $"{workload.Name}-n{count}-r{repeat}-{algorithm}");
                        RunResult run = Replay.Run(workload, count, repeat, algorithm, path, script, settings);
                        runs.Add(run);
                        Console.WriteLine($"{workload.Name} n={count} repeat={repeat} {algorithm}: commit-edit total={run.Steps.Skip(1).Sum(step => step.CommitMs):F3} ms, state={run.StateFileBytes} bytes");
                    }
                }
                ordinal++;
            }
        }
#endif
    }
}

internal static class Statistics {
    internal static double Median(double[] sorted) => sorted.Length % 2 != 0 ? sorted[sorted.Length / 2] :
        (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    internal static object Describe(IEnumerable<double> values) {
        double[] sorted = values.Order().ToArray();
        return new { Median = Median(sorted), Min = sorted[0], Max = sorted[^1], Samples = sorted.Length };
    }
}

internal static class Report {
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static void Write(Settings settings, Edit[] script, List<RunResult> runs, List<object> warmups) {
        // Cross-algorithm comparison uses only application-level content fingerprints.
        foreach (var group in runs.GroupBy(run => (run.Workload, run.Count))) {
            string[] expected = group.First().Steps.Select(step => step.StateFingerprint).ToArray();
            if (group.Any(run => !run.Steps.Select(step => step.StateFingerprint).SequenceEqual(expected))) {
                throw new InvalidOperationException("Independent repositories did not replay identical domain histories.");
            }
        }
        var summaries = runs.GroupBy(run => (run.Workload, run.Count, run.Algorithm)).Select(group => new {
            group.Key.Workload, group.Key.Count, group.Key.Algorithm,
            // Initial publication/binding is reported separately from edits.
            InitialCommitMs = Statistics.Describe(group.Select(run => run.Steps[0].CommitMs)),
            EditCommitTotalMs = Statistics.Describe(group.Select(run => run.Steps.Skip(1).Sum(step => step.CommitMs))),
            EditCommitAllocatedBytes = Statistics.Describe(group.Select(run => (double)run.Steps.Skip(1).Sum(step => step.CommitAllocatedBytes))),
            IsolatedDiffTotalMs = Statistics.Describe(group.Select(run => run.Steps.Skip(1).Sum(step => step.Diff!.MillisecondsMedian))),
            IsolatedDiffAllocatedBytes = Statistics.Describe(group.Select(run => (double)run.Steps.Skip(1).Sum(step => step.Diff!.AllocatedBytesMedian))),
            CandidateDeltaBodyBytes = Statistics.Describe(group.Select(run => (double)run.Steps.Skip(1).Sum(step => step.Diff!.DeltaBodyBytes))),
            ActualObjectPayloadBytes = Statistics.Describe(group.Select(run => (double)run.Steps.Skip(1).Sum(step => step.BasePayloadBytes + step.DeltaPayloadBytes))),
            BaseWrites = Statistics.Describe(group.Select(run => (double)run.Steps.Skip(1).Sum(step => step.BaseWrites))),
            DeltaWrites = Statistics.Describe(group.Select(run => (double)run.Steps.Skip(1).Sum(step => step.DeltaWrites))),
            StateFileBytes = Statistics.Describe(group.Select(run => (double)run.StateFileBytes)),
            ReopenAndLoadMs = Statistics.Describe(group.Select(run => run.ReopenAndLoadMs)),
        }).ToArray();
        object report = new {
            Status = "All stored histories and all isolated candidate deltas validated; no performance winner required.",
            Settings = settings, GeneratedUtc = DateTime.UtcNow,
            Environment = new {
                Runtime = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture, Environment.ProcessorCount, Stopwatch.Frequency,
                ServerGC = System.Runtime.GCSettings.IsServerGC, TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "runtime-default",
                Binaries = new[] { typeof(Program).Assembly, typeof(DurableSchema).Assembly, typeof(Repository).Assembly }
                    .Select(assembly => new { assembly.FullName, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))) }).ToArray(),
            },
            AlgorithmBudgets = new { LocalLookahead = ListDeltaMatcher<int, Int32StateOps>.LocalLookahead,
                MyersMaxEditDepth = ListDeltaMatcher<int, Int32StateOps>.MaximumMyersDepth,
                MyersTraceLimitBytes = ListDeltaMatcher<int, Int32StateOps>.MaximumTraceBytes,
                ExtraComparisons = "min(1000000, 4096 + 8 * (oldCount + newCount))", Source = "Product constants and ListDeltaMatcher.Plan formula; compiled binaries fingerprinted above" },
            Measurement = new { Version = 2, Commit = "Initial: CreateBranch S0. Edits: CommitEvent(marker) plus CommitState, including both captures, graph writes and Journal ref barriers; domain edits and validation excluded",
                FileBytes = "StateFileBytes includes marker Event revisions; JournalFileBytes sums the journal directory; per-step payload counters describe only State revisions",
                Allocation = "GC.GetAllocatedBytesForCurrentThread, excludes other-thread allocations and unmanaged memory",
                ObjectBytes = "actual decoded ObjectVersion payload, excluding ObjectId/membership/shared frames; physical file sizes separate",
                Cold = "fresh file handles and bindings, not a flushed OS page cache; writable Repository reopen includes integrity validation and flush",
                IsolatedDiff = "same frozen states read from each actual repository; direct product writer, one untimed warmup per pair; all candidates Apply-verified including Base-selected cases. Untimed diagnostics count complete writer equality/element calls; Adaptive must not exceed explicit Local body bytes." },
            Script = script, Warmups = warmups, Summary = summaries, Runs = runs,
        };
        File.WriteAllText(Path.Combine(settings.Output, "report.json"), JsonSerializer.Serialize(report, JsonOptions));
        List<string> csv = ["workload,count,repeat,algorithm,step,edit,commit_ms,commit_allocated,base_count,delta_count,base_payload,delta_payload,list_kind,list_payload,candidate_base_body,candidate_delta_body,diff_ms_median,diff_ms_min,diff_ms_max,diff_allocated"];
        foreach (StepResult step in runs.SelectMany(run => run.Steps)) {
            csv.Add(string.Join(',', new object?[] { step.Workload, step.Count, step.Repeat, step.Algorithm, step.Step, step.Edit,
                step.CommitMs, step.CommitAllocatedBytes, step.BaseWrites, step.DeltaWrites, step.BasePayloadBytes, step.DeltaPayloadBytes,
                step.ListWriteKind, step.ListPayloadBytes, step.Diff?.BaseBodyBytes, step.Diff?.DeltaBodyBytes, step.Diff?.MillisecondsMedian,
                step.Diff?.MillisecondsMin, step.Diff?.MillisecondsMax, step.Diff?.AllocatedBytesMedian }.Select(value => Convert.ToString(value, CultureInfo.InvariantCulture))));
        }
        File.WriteAllLines(Path.Combine(settings.Output, "steps.csv"), csv);
        StringBuilder text = new("# List Delta replay result\n\nAll domain histories, identities, exact states and candidate roundtrips passed.\n\n");
        text.AppendLine("The JSON summary reports median/min/max across fresh-repository repetitions. Initial Commit and warmup are separate. Whole edit Commit includes marker Event plus State publication and both Journal ref barriers; isolated Diff explains only the matcher/body contribution. No automatic ranking or cold-read optimization gate is applied.");
        text.AppendLine().AppendLine("| Workload | Count | Algorithm | Edit Commit total ms (median / min / max) | Candidate Delta bytes | Actual edit payload bytes |")
            .AppendLine("|---|---:|---|---:|---:|---:|");
        foreach (var group in runs.GroupBy(run => (run.Workload, run.Count, run.Algorithm))) {
            double[] times = group.Select(run => run.Steps.Skip(1).Sum(step => step.CommitMs)).Order().ToArray();
            RunResult first = group.First();
            text.AppendLine($"| {group.Key.Workload} | {group.Key.Count} | {group.Key.Algorithm} | {Statistics.Median(times):F3} / {times[0]:F3} / {times[^1]:F3} | {first.Steps.Skip(1).Sum(step => step.Diff!.DeltaBodyBytes)} | {first.Steps.Skip(1).Sum(step => step.BasePayloadBytes + step.DeltaPayloadBytes)} |");
        }
        File.WriteAllText(Path.Combine(settings.Output, "summary.md"), text.ToString());
    }
}
