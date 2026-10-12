using System.Globalization;
using System.Text.Json;
using Atelia.Rbf.Internal;

namespace Atelia.UnifiedRootProbe;

internal static class Program {
    private static readonly JsonSerializerOptions Json = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static void Emit(object value) {
        Console.WriteLine(JsonSerializer.Serialize(value, Json));
        Console.Out.Flush();
    }

    private static void Ready(string point) {
        Emit(new { type = "READY", pid = Environment.ProcessId, point });
        // The external controller kills this process. No managed unwind/disposal follows.
        Thread.Sleep(Timeout.Infinite);
    }

    private static long Long(string value) => long.Parse(value, CultureInfo.InvariantCulture);

    private static int Main(string[] args) {
        try {
            if (MetadataProgram.TryRun(args, out int metadataExitCode)) { return metadataExitCode; }
            if (args.Length < 2) { throw new ArgumentException("Mode and fixture path are required."); }
            string mode = args[0], path = Path.GetFullPath(args[1]);
            if (mode.StartsWith("baseline-", StringComparison.Ordinal)) {
                Emit(MetadataBaseline.Run(mode, path, args, Ready));
                return 0;
            }
            switch (mode) {
                case "seed": {
                    long a = args.Length > 2 ? Long(args[2]) : 10;
                    long b = args.Length > 3 ? Long(args[3]) : 20;
                    var (view, measurement) = ProbeMeasurements.Measure(() => UnifiedRootStore.Seed(path, a, b));
                    Emit(new { type = "seed", acknowledged = true, view, measurement });
                    return 0;
                }
                case "commit": {
                    if (args.Length < 4) { throw new ArgumentException("commit <fixture> <aValue> <bValue> [checkpoint]"); }
                    long a = Long(args[2]), b = Long(args[3]);
                    string? point = args.Length > 4 ? args[4] : null;
                    InstallWriteHooks(path, point);
                    var (view, measurement) = ProbeMeasurements.Measure(() => UnifiedRootStore.Commit(path, a, b,
                        checkpoint => { if (checkpoint == point) { Ready(checkpoint); } }));
                    RbfWriteInstrumentation.Current = null;
                    Emit(new { type = "commit", acknowledged = true, view, measurement,
                        writerAdmission = "Full physical and semantic qualification on each fresh CLI writer open." });
                    return 0;
                }
                case "inspect": {
                    long? generation = args.Length > 2 ? Long(args[2]) : null;
                    var (reader, open) = ProbeMeasurements.Measure(() => UnifiedRootStore.Open(path, generation));
                    using (reader) {
                        var (view, firstRead) = ProbeMeasurements.Measure(reader.Read);
                        Emit(new { type = "inspect", view, open, firstRead,
                            measurementScope = "Open locates and validates one root slot (plus two bounded dependency witnesses for a short root tail); FirstRead validates selected A and B identity, frame, and EOF." });
                    }
                    return 0;
                }
                case "recover":
                case "measure-recovery": {
                    string? point = mode == "recover" && args.Length > 2 ? args[2] : null;
                    long budget = mode == "recover" && args.Length > 3 ? Long(args[3]) : long.MaxValue;
                    var (report, measurement) = ProbeMeasurements.Measure(() => UnifiedRootStore.Recover(path, budget,
                        checkpoint => { if (checkpoint == point) { Ready(checkpoint); } }));
                    Emit(new { type = "recovery", report, measurement,
                        budgetScope = "Qualification physical and semantic reads only; action recheck and strict reopen are separate." });
                    return report.Status == "Succeeded" ? 0 : 2;
                }
                case "inventory": {
                    var (files, measurement) = ProbeMeasurements.Measure(() => UnifiedRootStore.Inventory(path));
                    Emit(new { type = "inventory", files, measurement });
                    return 0;
                }
                case "populate-orphans": {
                    if (args.Length < 3) { throw new ArgumentException("populate-orphans <fixture> <count>"); }
                    int count = int.Parse(args[2], CultureInfo.InvariantCulture);
                    var (_, measurement) = ProbeMeasurements.Measure(() => {
                        UnifiedRootStore.PopulateOrphans(path, count);
                        return 0;
                    });
                    Emit(new { type = "populate-orphans", count, measurement });
                    return 0;
                }
                case "selftest": {
                    Emit(new { type = "selftest", report = ProbeSelfTests.Run(path) });
                    return 0;
                }
                case "measure": {
                    Emit(new { type = "measurement", report = ProbeBenchmarks.Run(path) });
                    return 0;
                }
                default: throw new ArgumentException("Unknown mode: " + mode);
            }
        }
        catch (Exception ex) {
            RbfWriteInstrumentation.Current = null;
            Emit(new { type = "error", error = ex.ToString() });
            return 1;
        }
    }

    private static void InstallWriteHooks(string root, string? point) {
        if (point is null) { return; }
        string[] parts = point.Split(':');
        if (parts.Length != 2 || parts[0] is not ("partial" or "written" or "flushed") ||
            parts[1] is not ("A" or "B" or "Root")) { return; }
        string target = Path.GetFullPath(Path.Combine(root, parts[1] switch {
            "A" => "a.rbf", "B" => "b.rbf", _ => "roots.rbf"
        }));
        bool wroteTarget = false;
        bool Match(string? candidate) => candidate is not null &&
            string.Equals(Path.GetFullPath(candidate), target, StringComparison.OrdinalIgnoreCase);
        RbfWriteInstrumentation.Current = new RbfWriteHooks {
            BeforeWrite = request => parts[0] == "partial" && Match(request.Path) ? 2 : request.RequestedBytes,
            AfterWrite = observation => {
                if (!Match(observation.Path)) { return; }
                wroteTarget = true;
                if (parts[0] is "partial" or "written") { Ready(point); }
            },
            AfterFlush = request => {
                if (parts[0] == "flushed" && wroteTarget && Match(request.Path)) { Ready(point); }
            }
        };
    }
}
