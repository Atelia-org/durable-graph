using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.DurableGraph.Cli;
using Atelia.DurableGraph.Persistence;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class DB085S0DMaintenanceTests {
    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CheckAndApplyWithEmptyRegistryMaintainRealTypedRepository(bool shortTail) {
        using var fixture = new DB085S0CFixture(commits: 2);
        var complete = fixture.Snapshot();
        if (shortTail) { fixture.PartialWrite("state"); }
        var beforeCheck = fixture.Snapshot();
        var library = RepositoryRescueMaintenance.Check(fixture.Root);
        Assert.Equal(RepositoryRescueStatus.Qualified, library.Status);
        Assert.True(library.ResourcesClosed);
        var (exit, json) = Invoke(fixture.Root, "--check");
        Assert.Equal(0, exit);
        using (json) { Assert.Equal(Facts(library), Facts(json.RootElement)); }
        fixture.AssertUnchanged(beforeCheck);
        AssertResourcesReleased(fixture);

        var (applyExit, applied) = Invoke(fixture.Root, "--apply");
        using (applied) {
            Assert.Equal(0, applyExit);
            Assert.Equal("Succeeded", applied.RootElement.GetProperty("status").GetString());
            Assert.Equal(shortTail, applied.RootElement.GetProperty("actionAttempted").GetBoolean());
            Assert.Equal(shortTail, applied.RootElement.GetProperty("lengthChanged").GetBoolean());
            Assert.Equal(shortTail, applied.RootElement.GetProperty("barrierCompleted").GetBoolean());
            Assert.Equal(!shortTail, applied.RootElement.GetProperty("noAction").GetBoolean());
            Assert.True(applied.RootElement.GetProperty("resourcesClosed").GetBoolean());
        }
        fixture.AssertUnchanged(complete);
        AssertResourcesReleased(fixture);
        using var fresh = Repository.OpenExisting(fixture.Root, SharedReadModel.Models());
        fixture.ContinueAndColdOpen(fresh, expected: 3);
    }

    [Theory]
    [InlineData("--check")]
    [InlineData("--apply")]
    public void UnknownInventoryAndInvalidCompleteFrameRejectWithoutMutation(string mode) {
        using var fixture = new DB085S0CFixture();
        File.WriteAllText(Path.Combine(fixture.Root, "unknown"), "outside bounded scope");
        AssertRejectedUnchanged(fixture, mode, "Unsupported");
        File.Delete(Path.Combine(fixture.Root, "unknown"));
        CorruptCompleteFrame(fixture);
        AssertRejectedUnchanged(fixture, mode, "Invalid");
    }

    [Theory]
    [InlineData("--check")]
    [InlineData("--apply")]
    public void PartialTrailerIsUnresolvedAndNeverActs(string mode) {
        using var fixture = new DB085S0CFixture();
        fixture.PartialWrite("state", requested => requested - 16 + 1);
        AssertRejectedUnchanged(fixture, mode, "Unresolved");
    }

    [Fact]
    public void CheckIsNeverAnApplyPermitAfterSourceChanges() {
        using var fixture = new DB085S0CFixture();
        fixture.PartialWrite("state");
        Assert.Equal(RepositoryRescueStatus.Qualified, RepositoryRescueMaintenance.Check(fixture.Root).Status);
        CorruptCompleteFrame(fixture);
        AssertRejectedUnchanged(fixture, "--apply", "Invalid");
    }

    [Theory]
    [InlineData("--check")]
    [InlineData("--apply")]
    public void BudgetAndPreCancellationAreIncompleteAndLeaveBytesUnchanged(string mode) {
        using var fixture = new DB085S0CFixture();
        fixture.PartialWrite("state");
        var before = fixture.Snapshot();
        var (exit, budget) = Invoke(fixture.Root, mode, ["--read-budget", "0"]);
        using (budget) {
            Assert.Equal(3, exit);
            Assert.Equal("BudgetExceeded", budget.RootElement.GetProperty("qualification").GetProperty("status").GetString());
            Assert.False(budget.RootElement.GetProperty("sourceMayHaveChanged").GetBoolean());
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var (cancelExit, cancelled) = Invoke(fixture.Root, mode, token: cancellation.Token);
        using (cancelled) {
            Assert.Equal(3, cancelExit);
            Assert.True(cancelled.RootElement.GetProperty("cancelled").GetBoolean());
            Assert.False(cancelled.RootElement.GetProperty("actionAttempted").GetBoolean());
        }
        fixture.AssertUnchanged(before);
        AssertResourcesReleased(fixture);
    }

    [Theory]
    [InlineData("--check", true)]
    [InlineData("--apply", true)]
    [InlineData("--check", false)]
    [InlineData("--apply", false)]
    public void LockOrFileIoFailureIsIncompleteAndReleasesOwnedResources(string mode, bool lockRepository) {
        using var fixture = new DB085S0CFixture();
        var before = fixture.Snapshot();
        using (var blocker = new FileStream(lockRepository ? Path.Combine(fixture.Root, "repository.lock") : fixture.FileFor("state"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            var (exit, report) = Invoke(fixture.Root, mode);
            using (report) {
                Assert.Equal(3, exit);
                Assert.Equal("PrecheckIncomplete", report.RootElement.GetProperty("status").GetString());
                Assert.False(report.RootElement.GetProperty("actionAttempted").GetBoolean());
            }
        }
        fixture.AssertUnchanged(before);
        AssertResourcesReleased(fixture);
    }

    [Theory]
    [InlineData("AfterSetLengthBeforeFlush", 4, "ActionFailed", false)]
    [InlineData("BeforeStrictOpen", 5, "StrictOpenFailed", true)]
    [InlineData("BeforeDelivery", 5, "DeliveryFailed", true)]
    public void ApplyFailureRetainsKnownMutationAndReleasesResources(string stage, int expectedExit, string status, bool barrier) {
        using var fixture = new DB085S0CFixture();
        var complete = fixture.Snapshot();
        fixture.PartialWrite("state");
        RepositoryRecovery.Checkpoint = current => { if (current == stage) { throw new IOException("Injected " + stage); } };
        try {
            var (exit, report) = Invoke(fixture.Root, "--apply");
            using (report) {
                Assert.Equal(expectedExit, exit);
                Assert.Equal(status, report.RootElement.GetProperty("status").GetString());
                Assert.True(report.RootElement.GetProperty("actionAttempted").GetBoolean());
                Assert.True(report.RootElement.GetProperty("lengthChanged").GetBoolean());
                Assert.Equal(barrier, report.RootElement.GetProperty("barrierCompleted").GetBoolean());
            }
        } finally { RepositoryRecovery.Checkpoint = null; }
        fixture.AssertUnchanged(complete);
        AssertResourcesReleased(fixture);
        var retry = RepositoryRescueMaintenance.Apply(fixture.Root);
        Assert.Equal(RepositoryRescueStatus.Succeeded, retry.Status);
        Assert.True(retry.NoAction);
        AssertResourcesReleased(fixture);
    }

    [Fact]
    public void CancellationAfterActionPreservesMutationFacts() {
        using var fixture = new DB085S0CFixture();
        fixture.PartialWrite("state");
        using var cancellation = new CancellationTokenSource();
        RepositoryRecovery.Checkpoint = current => { if (current == "AfterAction") { cancellation.Cancel(); } };
        try {
            var (exit, report) = Invoke(fixture.Root, "--apply", token: cancellation.Token);
            using (report) {
                Assert.NotEqual(0, exit);
                Assert.True(report.RootElement.GetProperty("cancelled").GetBoolean());
                Assert.True(report.RootElement.GetProperty("cancellationAfterMutation").GetBoolean());
                Assert.True(report.RootElement.GetProperty("barrierCompleted").GetBoolean());
            }
        } finally { RepositoryRecovery.Checkpoint = null; }
        AssertResourcesReleased(fixture);
    }

    [Theory]
    [InlineData("BeforeClose", "CloseFailed")]
    [InlineData("AfterClose", "CloseFailed")]
    [InlineData("BeforeReport", "DeliveryFailed")]
    public void ObserverFailureAtCloseOrDeliveryStillActuallyDisposes(string stage, string status) {
        using var fixture = new DB085S0CFixture();
        fixture.PartialWrite("state");
        bool observed = false;
        RepositoryRescueMaintenance.Checkpoint = current => {
            if (current == "BeforeClose") { fixture.AssertLockHeld(); }
            if (current == "AfterClose") { AssertResourcesReleased(fixture); }
            if (current == stage) { observed = true; throw new IOException("Injected observer failure; not an OS Dispose fault"); }
        };
        try {
            var (exit, report) = Invoke(fixture.Root, "--apply");
            using (report) {
                Assert.Equal(5, exit);
                Assert.Equal(status, report.RootElement.GetProperty("status").GetString());
                Assert.True(report.RootElement.GetProperty("resourcesClosed").GetBoolean());
                Assert.True(report.RootElement.GetProperty("lengthChanged").GetBoolean());
                Assert.Contains("observer failure", report.RootElement.GetProperty("detail").GetString());
            }
            Assert.True(observed);
        } finally { RepositoryRescueMaintenance.Checkpoint = null; }
        AssertResourcesReleased(fixture);
        Assert.Equal(RepositoryRescueStatus.Succeeded, RepositoryRescueMaintenance.Apply(fixture.Root).Status);
    }

    [Theory]
    [InlineData(true, "BeforeDelivery", "AfterFailedRepositoryClose", "DeliveryFailed")]
    [InlineData(false, "BeforeStrictOpen", "AfterFailedLockClose", "StrictOpenFailed")]
    public void CoreCleanupObserverFailurePreservesPreviousStageAndCancellation(bool cancel, string failingStage, string cleanupStage, string previousStatus) {
        using var fixture = new DB085S0CFixture();
        var complete = fixture.Snapshot();
        fixture.PartialWrite("state");
        using var cancellation = new CancellationTokenSource();
        bool observed = false;
        RepositoryRecovery.Checkpoint = current => {
            if (current == failingStage) {
                if (cancel) { cancellation.Cancel(); }
                else { throw new IOException("Injected original stage failure"); }
            }
            if (current == cleanupStage) {
                observed = true;
                throw new IOException("Injected after-dispose observer failure; not an OS Dispose fault");
            }
        };
        try {
            var (exit, report) = Invoke(fixture.Root, "--apply", token: cancellation.Token);
            using (report) {
                Assert.Equal(5, exit);
                Assert.Equal("DeliveryFailed", report.RootElement.GetProperty("status").GetString());
                Assert.Equal(previousStatus, report.RootElement.GetProperty("cleanupPreviousStatus").GetString());
                Assert.Equal(cancel, report.RootElement.GetProperty("cancelled").GetBoolean());
                Assert.Equal(cancel, report.RootElement.GetProperty("cancellationAfterMutation").GetBoolean());
                Assert.True(report.RootElement.GetProperty("actionAttempted").GetBoolean());
                Assert.True(report.RootElement.GetProperty("lengthChanged").GetBoolean());
                Assert.True(report.RootElement.GetProperty("barrierCompleted").GetBoolean());
                Assert.Contains("after-dispose observer failure", report.RootElement.GetProperty("cleanupDetail").GetString());
            }
            Assert.True(observed);
        } finally { RepositoryRecovery.Checkpoint = null; }
        fixture.AssertUnchanged(complete);
        AssertResourcesReleased(fixture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OutputFailureOccursAfterCloseAndAllowsSafeFreshRerun(bool json) {
        using var fixture = new DB085S0CFixture();
        var complete = fixture.Snapshot();
        fixture.PartialWrite("state");
        var argv = new List<string> { "rescue", fixture.Root, "--apply" };
        if (json) { argv.Add("--json"); }
        var error = new StringWriter();
        Assert.Equal(6, MaintenanceCli.Run(argv.ToArray(), new FailingWriter(() => AssertResourcesReleased(fixture)), error));
        Assert.Contains("may already have changed", error.ToString());
        fixture.AssertUnchanged(complete);
        AssertResourcesReleased(fixture);
        Assert.Equal(6, MaintenanceCli.Run(argv.ToArray(), new FailingWriter(() => AssertResourcesReleased(fixture)), new FailingWriter(() => { })));
        var (exit, retry) = Invoke(fixture.Root, "--apply");
        using (retry) { Assert.Equal(0, exit); Assert.True(retry.RootElement.GetProperty("noAction").GetBoolean()); }
    }

    [Theory]
    [InlineData("--check")]
    [InlineData("--apply")]
    public void TextAndJsonContainSameReportFactsIncludingScopesAndMetrics(string mode) {
        using var fixture = new DB085S0CFixture();
        var (exit, json) = Invoke(fixture.Root, mode);
        using (json) {
            var output = new StringWriter();
            Assert.Equal(exit, MaintenanceCli.Run(["rescue", fixture.Root, mode], output, new StringWriter()));
            var textFacts = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1)
                .Select(line => line.TrimEnd('\r')).Select(line => line.Split(": ", 2))
                .Where(pair => !IsVariableMetric(pair[0])).ToDictionary(pair => pair[0], pair => pair[1]);
            Assert.Equal(Facts(json.RootElement), textFacts.OrderBy(pair => pair.Key).ToArray());
            Assert.Contains("total.threadAllocatedBytes:", output.ToString());
            Assert.Contains("total.elapsed:", output.ToString());
            Assert.Contains("budgetScope:", output.ToString());
            Assert.Contains("measurementScope:", output.ToString());
        }
    }

    public static IEnumerable<object[]> InvalidArguments() {
        yield return [Array.Empty<string>()];
        yield return [new[] { "--check", "--apply" }];
        yield return [new[] { "--apply", "--unknown" }];
        yield return [new[] { "--apply", "--apply" }];
        yield return [new[] { "--check", "--check" }];
        yield return [new[] { "--apply", "--json", "--json" }];
        yield return [new[] { "--apply", "--read-budget" }];
        foreach (string value in new[] { "-1", "9223372036854775808", "abc", "1.0", "+1" }) {
            yield return [new[] { "--apply", "--read-budget", value }];
        }
        yield return [new[] { "--apply", "--read-budget", "0", "--read-budget", "1" }];
    }

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void FullArgumentValidationPrecedesAnyRecovery(string[] options) {
        using var fixture = new DB085S0CFixture();
        fixture.PartialWrite("state");
        var before = fixture.Snapshot();
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(1, MaintenanceCli.Run(["rescue", fixture.Root, .. options], output, error));
        Assert.Equal("", output.ToString());
        Assert.Contains("Usage:", error.ToString());
        fixture.AssertUnchanged(before);
        AssertResourcesReleased(fixture);
    }

    private static void AssertResourcesReleased(DB085S0CFixture fixture) {
        foreach (string path in Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories)) {
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
    }

    private static void CorruptCompleteFrame(DB085S0CFixture fixture) {
        string path = fixture.FileFor("state");
        byte[] bytes = File.ReadAllBytes(path);
        bytes[12] ^= 0xff;
        File.WriteAllBytes(path, bytes);
    }

    private static void AssertRejectedUnchanged(DB085S0CFixture fixture, string mode, string qualification) {
        var before = fixture.Snapshot();
        var (exit, report) = Invoke(fixture.Root, mode);
        using (report) {
            Assert.Equal(2, exit);
            Assert.Equal("QualificationRefused", report.RootElement.GetProperty("status").GetString());
            Assert.Equal(qualification, report.RootElement.GetProperty("qualification").GetProperty("status").GetString());
            Assert.False(report.RootElement.GetProperty("actionAttempted").GetBoolean());
            if (mode == "--check") { Assert.True(report.RootElement.GetProperty("resourcesClosed").GetBoolean()); }
        }
        fixture.AssertUnchanged(before);
        AssertResourcesReleased(fixture);
    }

    private static (int Exit, JsonDocument Json) Invoke(string root, string mode, string[]? extra = null, CancellationToken token = default) {
        var output = new StringWriter();
        var error = new StringWriter();
        int exit = MaintenanceCli.Run(["rescue", root, mode, "--json", .. (extra ?? [])], output, error, token);
        Assert.Equal("", error.ToString());
        return (exit, JsonDocument.Parse(output.ToString()));
    }

    private static KeyValuePair<string, string>[] Facts(RepositoryRescueReport report) {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(report, JsonOptions));
        return Facts(json.RootElement);
    }

    private static KeyValuePair<string, string>[] Facts(JsonElement value) {
        var facts = new Dictionary<string, string>();
        void Visit(JsonElement item, string path) {
            if (item.ValueKind == JsonValueKind.Object) {
                foreach (var property in item.EnumerateObject()) { Visit(property.Value, path.Length == 0 ? property.Name : path + "." + property.Name); }
            } else if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() != 0) {
                int i = 0;
                foreach (var element in item.EnumerateArray()) { Visit(element, path + "[" + i++ + "]"); }
            } else if (!IsVariableMetric(path)) { facts[path] = item.GetRawText(); }
        }
        Visit(value, "");
        return facts.OrderBy(pair => pair.Key).ToArray();
    }

    private static bool IsVariableMetric(string path) => path.EndsWith(".elapsed", StringComparison.Ordinal) || path.EndsWith(".threadAllocatedBytes", StringComparison.Ordinal);
    private sealed class FailingWriter(Action beforeFailure) : TextWriter {
        public override Encoding Encoding => Encoding.UTF8;
        public override void WriteLine(string? value) { beforeFailure(); throw new IOException("Injected output failure"); }
    }
}
