using System.Globalization;
using System.Text.Json;
using Atelia.Rbf.Internal;

namespace Atelia.UnifiedRootProbe;

internal static class MetadataProgram {
    private static readonly JsonSerializerOptions Json = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    internal static bool TryRun(string[] args, out int exitCode) {
        exitCode = 0;
        if (args.Length == 0 || !args[0].StartsWith("metadata-", StringComparison.Ordinal)) { return false; }
        try {
            if (args.Length < 2) { throw new ArgumentException("metadata mode and fixture path required."); }
            string mode = args[0], path = Path.GetFullPath(args[1]);
            switch (mode) {
                case "metadata-seed": {
                    int unrelated = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 0;
                    int history = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 0;
                    var (view, measurement) = ProbeMeasurements.Measure(() => MetadataStore.Seed(path, unrelated, history));
                    Emit(new { type = mode, acknowledged = true, view, measurement,
                        durability = "ProcessCrashOnly; no directory durability barrier" });
                    break;
                }
                case "metadata-create-b":
                case "metadata-advance-b": {
                    string? point = args.Length > 2 ? args[2] : null;
                    InstallWriteHooks(path, point);
                    var (view, measurement) = ProbeMeasurements.Measure(() =>
                        mode == "metadata-create-b"
                            ? MetadataStore.CreateB(path, p => { if (p == point) { Ready(p); } })
                            : MetadataStore.AdvanceB(path, p => { if (p == point) { Ready(p); } }));
                    RbfWriteInstrumentation.Current = null;
                    Emit(new { type = mode, acknowledged = true, view, measurement,
                        durability = "ProcessCrashOnly; no directory durability barrier" });
                    break;
                }
                case "metadata-inspect": {
                    long? generation = args.Length > 2 ? long.Parse(args[2], CultureInfo.InvariantCulture) : null;
                    var (reader, open) = ProbeMeasurements.Measure(() => MetadataStore.Open(path, generation));
                    using (reader) {
                        var (view, firstRead) = ProbeMeasurements.Measure(reader.ReadAll);
                        Emit(new { type = mode, view, open, firstRead,
                            measurementScope = "Open locates a bounded root slot and loads its directory. A short root suffix additionally validates the latest candidate directory and all its branch dependencies, even when an older generation was requested. FirstRead validates every selected branch and its parent closure." });
                    }
                    break;
                }
                case "metadata-recover": {
                    string? point = args.Length > 2 ? args[2] : null;
                    long budget = args.Length > 3 ? long.Parse(args[3], CultureInfo.InvariantCulture) : long.MaxValue;
                    var (report, measurement) = ProbeMeasurements.Measure(() => MetadataStore.Recover(path, budget,
                        p => { if (p == point) { Ready(p); } }));
                    Emit(new { type = "metadata-recovery", report, measurement,
                        budgetScope = "First full physical and semantic qualification; pre-action recheck and fresh confirmation have no read budget." });
                    exitCode = report.Status == "Succeeded" ? 0 : 2;
                    break;
                }
                case "metadata-inventory": {
                    var (files, measurement) = ProbeMeasurements.Measure(() => MetadataStore.Inventory(path));
                    Emit(new { type = mode, files, measurement });
                    break;
                }
                case "metadata-populate-orphans": {
                    if (args.Length < 3) { throw new ArgumentException("metadata-populate-orphans <fixture> <count>"); }
                    int count = int.Parse(args[2], CultureInfo.InvariantCulture);
                    var (_, measurement) = ProbeMeasurements.Measure(() => {
                        MetadataStore.PopulateOrphans(path, count); return 0;
                    });
                    Emit(new { type = mode, count, measurement });
                    break;
                }
                case "metadata-selftest": {
                    Emit(new { type = mode, report = MetadataSelfTests.Run(path) });
                    break;
                }
                case "metadata-measure": {
                    Emit(new { type = mode, report = MetadataBenchmarks.Run(path) });
                    break;
                }
                default: throw new ArgumentException("Unknown metadata mode: " + mode);
            }
        }
        catch (Exception ex) {
            RbfWriteInstrumentation.Current = null;
            Emit(new { type = "error", error = ex.ToString() });
            exitCode = 1;
        }
        return true;
    }

    private static void Emit(object value) {
        Console.WriteLine(JsonSerializer.Serialize(value, Json));
        Console.Out.Flush();
    }

    private static void Ready(string point) {
        Emit(new { type = "READY", pid = Environment.ProcessId, point });
        Thread.Sleep(Timeout.Infinite);
    }

    private static void InstallWriteHooks(string root, string? point) {
        if (point is null) { return; }
        string[] parts = point.Split(':');
        if (parts.Length != 2) { return; }
        bool headerPrefix = parts[0].StartsWith("metadata-partial", StringComparison.Ordinal) &&
            parts[0].Length == "metadata-partial".Length + 1 &&
            parts[0][^1] is >= '0' and <= '3';
        if (parts[0] is not ("partial" or "written" or "flushed") && !headerPrefix) { return; }
        string? target = parts[1] switch {
            "Segment2" or "Event" => Path.Combine(root, "events", "segments", "00000002.rbf"),
            "Allocation" => Path.Combine(root, "allocation.rbf"),
            "Directory" => Path.Combine(root, "catalog.rbf"),
            "Root" => Path.Combine(root, "roots.rbf"),
            _ => null
        };
        if (target is null) { return; }
        target = Path.GetFullPath(target);
        bool wrote = false;
        bool Match(string? candidate, long offset) => candidate is not null &&
            string.Equals(Path.GetFullPath(candidate), target, StringComparison.OrdinalIgnoreCase) &&
            (parts[1] == "Event" ? offset >= 4 : parts[1] == "Segment2" ? offset == 0 : true);
        RbfWriteInstrumentation.Current = new RbfWriteHooks {
            BeforeWrite = request => {
                if (!Match(request.Path, request.Offset)) { return request.RequestedBytes; }
                if (headerPrefix) { return parts[0][^1] - '0'; }
                return parts[0] == "partial" ? Math.Min(2, request.RequestedBytes) : request.RequestedBytes;
            },
            AfterWrite = observation => {
                if (!Match(observation.Path, observation.Offset)) { return; }
                wrote = true;
                if (headerPrefix || parts[0] is "partial" or "written") { Ready(point); }
            },
            AfterFlush = request => {
                if (parts[0] == "flushed" && wrote && Match(request.Path,
                    parts[1] == "Segment2" ? 0 : parts[1] == "Event" ? 4 : 0)) { Ready(point); }
            }
        };
    }
}
