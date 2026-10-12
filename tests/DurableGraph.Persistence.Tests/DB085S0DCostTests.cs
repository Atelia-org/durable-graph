using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Atelia.DurableGraph.Cli;
using Atelia.Rbf;
using Xunit.Abstractions;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class DB085S0DCostTests(ITestOutputHelper output) {
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void MeasureAdapterAndBothCliFormatsAgainstTheirOwnCoreSample() {
        // Fresh sources for every invocation, including warmups. Files were just written;
        // OS/device cache is uncontrolled. Timing is evidence, never a pass threshold.
        foreach (var cell in new[] { new Cell("baseline", 0), new Cell("shortTail2B", 0, true), new Cell("history32", 32) }) {
            foreach (string entry in new[] { "adapter", "text", "json" }) {
                for (int sample = -1; sample < 3; sample++) {
                    using var fixture = new DB085S0CFixture(cell.Commits);
                    Assert.True(RepositoryMaintenance.Audit(fixture.Root).IsHealthy); // Outside measured work.
                    var completeBefore = fixture.Snapshot();
                    if (cell.ShortTail) { fixture.PartialWrite("state"); }
                    using var stdout = new StringWriter(CultureInfo.InvariantCulture);
                    using var stderr = new StringWriter(CultureInfo.InvariantCulture);
                    // Argument/writer construction is outside Run measurement. Parsing,
                    // maintenance, report serialization and writer calls are inside it.
                    string[] args = entry == "json" ? ["rescue", fixture.Root, "--apply", "--json"] : ["rescue", fixture.Root, "--apply"];
                    Costs facts;
                    long started = Stopwatch.GetTimestamp();
                    long allocated = GC.GetAllocatedBytesForCurrentThread();
                    Invocation outer;
                    if (entry == "adapter") {
                        var report = RepositoryRescueMaintenance.Apply(fixture.Root);
                        outer = new(Stopwatch.GetElapsedTime(started), GC.GetAllocatedBytesForCurrentThread() - allocated);
                        Assert.Equal("Succeeded", report.Status.ToString());
                        Assert.True(report.ResourcesClosed);
                        Assert.Equal(cell.ShortTail, report.LengthChanged);
                        facts = FromReport(report);
                    } else {
                        int exit = MaintenanceCli.Run(args, stdout, stderr);
                        outer = new(Stopwatch.GetElapsedTime(started), GC.GetAllocatedBytesForCurrentThread() - allocated);
                        Assert.Equal(0, exit);
                        Assert.Equal(string.Empty, stderr.ToString());
                        string rendered = stdout.ToString();
                        facts = entry == "json" ? FromJson(rendered, cell.ShortTail) : FromText(rendered, cell.ShortTail);
                    }
                    fixture.AssertLockReleased();
                    fixture.AssertUnchanged(completeBefore);
                    Assert.True(RepositoryMaintenance.Audit(fixture.Root).IsHealthy); // Independent, outside metrics.
                    fixture.AssertUnchanged(completeBefore);
                    Assert.Equal(RepositoryRecoveryQualification.Add(facts.CoreTotal.Reads, facts.Close.Reads), facts.Total.Reads);
                    Assert.True(facts.Confirmation.Elapsed <= facts.StrictOpen.Elapsed);
                    if (sample >= 0) {
                        string rendered = stdout.ToString();
                        output.WriteLine("S0D_COST " + JsonSerializer.Serialize(new {
                            Cell = cell, Entry = entry, Sample = sample, Mode = "apply", Facts = facts, Outer = outer,
                            AdapterAdded = Difference(facts.Total, facts.CoreTotal),
                            AdapterOutsideClose = Difference(facts.Total, facts.CoreTotal, facts.Close),
                            EntryOutsideMaintenance = Difference(outer, facts.Total),
                            OutputCharacters = rendered.Length, OutputUtf8Bytes = Encoding.UTF8.GetByteCount(rendered),
                            Scope = "Inner/outer differences belong to this same invocation. AdapterAdded includes Close; AdapterOutsideClose excludes it. EntryOutsideMaintenance is caller/CLI parsing, rendering and return overhead. Confirmation is a StrictOpen subset. Read counters are the inner report counters; outer timing/allocation is measured separately. CLI output uses StringWriter, not device I/O. ToString, UTF8 byte counting, assertions and Audit are outside timing. Thread allocation is not peak memory. OS/device cache is uncontrolled. Do not compare this run to old S0-C ratios.",
                        }));
                    }
                }
            }
        }
    }

    private static Costs FromReport(RepositoryRescueReport report) => new(
        report.Qualification!.Physical, report.Qualification.Candidate, report.Qualification.Dependencies,
        report.Action, report.StrictOpen, report.Confirmation, report.CoreTotal, report.Close, report.Total);

    private static Costs FromJson(string rendered, bool shortTail) {
        using var document = JsonDocument.Parse(rendered);
        JsonElement root = document.RootElement;
        Assert.Equal("Succeeded", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("resourcesClosed").GetBoolean());
        Assert.Equal(shortTail, root.GetProperty("lengthChanged").GetBoolean());
        RecoveryQualificationMetrics Read(JsonElement value, string name) => value.GetProperty(name).Deserialize<RecoveryQualificationMetrics>(ReadOptions);
        JsonElement qualification = root.GetProperty("qualification");
        return new(Read(qualification, "physical"), Read(qualification, "candidate"), Read(qualification, "dependencies"),
            Read(root, "action"), Read(root, "strictOpen"), Read(root, "confirmation"), Read(root, "coreTotal"), Read(root, "close"), Read(root, "total"));
    }

    private static Costs FromText(string rendered, bool shortTail) {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in rendered.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
            int separator = line.IndexOf(": ", StringComparison.Ordinal);
            if (separator >= 0) { values.Add(line[..separator], line[(separator + 2)..].TrimEnd('\r')); }
        }
        Assert.Equal("\"Succeeded\"", values["status"]);
        Assert.Equal("true", values["resourcesClosed"]);
        Assert.Equal(shortTail ? "true" : "false", values["lengthChanged"]);
        long Number(string name) => long.Parse(values[name], CultureInfo.InvariantCulture);
        RecoveryQualificationMetrics Read(string prefix) => new(new RbfReadMetricsSnapshot(
            Number(prefix + ".reads.readCalls"), Number(prefix + ".reads.requestedBytes"), Number(prefix + ".reads.returnedBytes"),
            Number(prefix + ".reads.rawReadCalls"), Number(prefix + ".reads.rawRequestedBytes"), Number(prefix + ".reads.rawReturnedBytes"),
            Number(prefix + ".reads.readAheadReturnedBytes"), Number(prefix + ".reads.headerReadCalls")),
            JsonSerializer.Deserialize<TimeSpan>(values[prefix + ".elapsed"]), Number(prefix + ".threadAllocatedBytes"));
        return new(Read("qualification.physical"), Read("qualification.candidate"), Read("qualification.dependencies"),
            Read("action"), Read("strictOpen"), Read("confirmation"), Read("coreTotal"), Read("close"), Read("total"));
    }

    private static Invocation Difference(RecoveryQualificationMetrics total, RecoveryQualificationMetrics inner) =>
        new(total.Elapsed - inner.Elapsed, total.ThreadAllocatedBytes - inner.ThreadAllocatedBytes);
    private static Invocation Difference(RecoveryQualificationMetrics total, RecoveryQualificationMetrics inner, RecoveryQualificationMetrics close) =>
        new(total.Elapsed - inner.Elapsed - close.Elapsed, total.ThreadAllocatedBytes - inner.ThreadAllocatedBytes - close.ThreadAllocatedBytes);
    private static Invocation Difference(Invocation outer, RecoveryQualificationMetrics inner) =>
        new(outer.Elapsed - inner.Elapsed, outer.ThreadAllocatedBytes - inner.ThreadAllocatedBytes);
    private sealed record Cell(string Name, int Commits, bool ShortTail = false);
    private sealed record Invocation(TimeSpan Elapsed, long ThreadAllocatedBytes);
    private sealed record Costs(RecoveryQualificationMetrics Physical, RecoveryQualificationMetrics Candidate,
        RecoveryQualificationMetrics Dependencies, RecoveryQualificationMetrics Action, RecoveryQualificationMetrics StrictOpen,
        RecoveryQualificationMetrics Confirmation, RecoveryQualificationMetrics CoreTotal, RecoveryQualificationMetrics Close,
        RecoveryQualificationMetrics Total);
}
