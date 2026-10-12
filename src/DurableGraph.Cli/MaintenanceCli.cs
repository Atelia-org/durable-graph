using System.Globalization;
using Atelia.DurableGraph.Persistence;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atelia.DurableGraph.Cli;

/// <summary>Command parsing and presentation for the library maintenance API.</summary>
public static class MaintenanceCli {
    private const string Usage = "Usage: durable-graph audit <repository-path> [--json]\n       durable-graph rescue <repository-path> (--check | --apply) [--json] [--read-budget <bytes>]";
    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static int Run(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (args is ["--help"] or ["-h"] or ["audit", "--help"] or ["rescue", "--help"]) {
            return Present(() => {
                output.WriteLine(Usage);
                output.WriteLine("Close repositories and writers before maintenance. Rescue check is read-only eligibility; apply derives fresh evidence and permits bounded suffix removal.");
                output.WriteLine("Exit codes: 0 success/qualified; 1 usage; 2 rejected data; 3 incomplete/cancelled; 4 action failure; 5 open/delivery/close failure; 6 output failure.");
            }, error, 0);
        }
        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith("--", StringComparison.Ordinal)) {
            return UsageError(error);
        }
        if (args[0] == "audit") { return Audit(args, output, error, cancellationToken); }
        if (args[0] != "rescue") { return UsageError(error); }

        bool json = false, check = false, apply = false, budgetSeen = false;
        long readBudget = long.MaxValue;
        for (int i = 2; i < args.Length; i++) {
            switch (args[i]) {
                case "--json" when !json: json = true; break;
                case "--check" when !check: check = true; break;
                case "--apply" when !apply: apply = true; break;
                case "--read-budget" when !budgetSeen:
                    budgetSeen = true;
                    if (++i >= args.Length || !long.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out readBudget)) {
                        return UsageError(error);
                    }
                    break;
                default: return UsageError(error);
            }
        }
        if (check == apply) { return UsageError(error); }

        RepositoryRescueReport report;
        try {
            report = check ? RepositoryRescueMaintenance.Check(args[1], readBudget, cancellationToken)
                : RepositoryRescueMaintenance.Apply(args[1], readBudget, cancellationToken);
        } catch (ArgumentException e) {
            return Present(() => error.WriteLine(e.Message), error, 1);
        }
        int exit = report.Status switch {
            RepositoryRescueStatus.Qualified or RepositoryRescueStatus.Succeeded => 0,
            RepositoryRescueStatus.QualificationRefused => 2,
            RepositoryRescueStatus.PrecheckIncomplete => 3,
            RepositoryRescueStatus.ActionFailed => 4,
            _ => 5,
        };
        return Present(() => {
            if (json) { output.WriteLine(JsonSerializer.Serialize(report, JsonOptions)); }
            else {
                output.WriteLine($"Rescue: {report.Status}");
                // Present the same serialized facts in both formats, including nested metrics,
                // scopes and nullable observations. No source inspection belongs in rendering.
                using var document = JsonDocument.Parse(JsonSerializer.Serialize(report, JsonOptions));
                WriteFacts(document.RootElement, "", output);
            }
        }, error, exit);
    }

    private static int Audit(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken) {
        bool json = args.Length == 3 && args[2] == "--json";
        if (args.Length > 3 || args.Length == 3 && !json) { return UsageError(error); }
        RepositoryAuditReport report;
        try { report = RepositoryMaintenance.Audit(args[1], cancellationToken); }
        catch (ArgumentException e) { error.WriteLine(e.Message); return 1; }
        int exit = report.Completion != RepositoryAuditCompletion.Completed ? 3 : report.IsHealthy ? 0 : 2;
        if (json) { output.WriteLine(JsonSerializer.Serialize(report, JsonOptions)); }
        else {
            output.WriteLine($"Audit: {(report.IsHealthy ? "Healthy" : report.Completion.ToString())}");
            foreach (RepositoryAuditFinding finding in report.Findings) {
                string location = finding.Offset is { } offset ? $"{finding.RelativePath}@{offset}" : finding.RelativePath;
                output.WriteLine($"{finding.Kind} {finding.Code} {location}: {finding.Message}");
            }
        }
        return exit;
    }

    private static void WriteFacts(JsonElement value, string path, TextWriter output) {
        if (value.ValueKind == JsonValueKind.Object) {
            foreach (var property in value.EnumerateObject()) {
                WriteFacts(property.Value, path.Length == 0 ? property.Name : path + "." + property.Name, output);
            }
        } else if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() != 0) {
            int i = 0;
            foreach (var item in value.EnumerateArray()) { WriteFacts(item, path + "[" + i++ + "]", output); }
        } else { output.WriteLine(path + ": " + value.GetRawText()); }
    }

    private static int UsageError(TextWriter error) => Present(() => error.WriteLine(Usage), error, 1);

    private static int Present(Action render, TextWriter error, int exit) {
        try { render(); return exit; }
        catch (Exception) {
            // Maintenance has already released its resources. A failed presentation cannot
            // roll back an action or justify an assertion that the source was unchanged.
            try { error.WriteLine("Maintenance output failed. An apply may already have changed the source; rerun maintenance to derive fresh disk evidence."); }
            catch (Exception) { }
            return 6;
        }
    }
}
