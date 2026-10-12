using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.DurableGraph.Cli;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class MaintenanceCliTests : IDisposable {
    private const string Prefix = "durable-audit-cli-";
    private readonly string _root = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CliJsonEqualsLibraryReportAndPreservesEverySourceByte(bool damageTail) {
        using (Repository.CreateNew(_root, new StateModelRegistry())) { }
        if (damageTail) {
            using var file = new FileStream(Path.Combine(_root, "schemas.rbf"), FileMode.Append, FileAccess.Write);
            file.WriteByte(0x7f);
        }
        var before = Snapshot();
        RepositoryAuditReport report = RepositoryMaintenance.Audit(_root);
        var output = new StringWriter();
        var error = new StringWriter();
        int exit = MaintenanceCli.Run(["audit", _root, "--json"], output, error);
        Assert.Equal(JsonSerializer.Serialize(report, JsonOptions), output.ToString().TrimEnd());
        Assert.Equal(string.Empty, error.ToString());
        Assert.Equal(report.IsHealthy ? 0 : report.Completion == RepositoryAuditCompletion.Completed ? 2 : 3, exit);
        Assert.Equal(!damageTail, report.IsHealthy);
        AssertUnchanged(before);
    }

    [Fact]
    public void CancelledInvocationHasStructuredReportAndIncompleteExitCode() {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var output = new StringWriter();
        int exit = MaintenanceCli.Run(["audit", _root, "--json"], output, new StringWriter(), cancellation.Token);
        Assert.Equal(3, exit);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("Cancelled", json.RootElement.GetProperty("completion").GetString());
        Assert.False(json.RootElement.GetProperty("isHealthy").GetBoolean());
        Assert.False(Directory.Exists(_root));
    }

    [Theory]
    [InlineData("rescue")]
    [InlineData("--unsafe")]
    public void RescueWithoutExplicitModeAndUnknownAuditOptionsAreUsageErrorsWithoutTouchingSource(string value) {
        var output = new StringWriter();
        var error = new StringWriter();
        string[] args = value == "rescue" ? [value, _root] : ["audit", _root, value];
        Assert.Equal(1, MaintenanceCli.Run(args, output, error));
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("Usage:", error.ToString());
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task ExecutableEntryPointEmitsMachineReportAndExitCode() {
        using (Repository.CreateNew(_root, new StateModelRegistry())) { }
        // ProjectReference copies the executable and its runtime configuration into the test output.
        var start = new ProcessStartInfo("dotnet") {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(typeof(MaintenanceCli).Assembly.Location);
        start.ArgumentList.Add("audit");
        start.ArgumentList.Add(_root);
        start.ArgumentList.Add("--json");
        using var process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
        Assert.Equal(string.Empty, await stderr);
        Assert.Equal(0, process.ExitCode);
        using var json = JsonDocument.Parse(await stdout);
        Assert.True(json.RootElement.GetProperty("isHealthy").GetBoolean());
    }

    private Dictionary<string, byte[]> Snapshot() => Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => path, File.ReadAllBytes);

    private void AssertUnchanged(Dictionary<string, byte[]> before) {
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order());
        foreach ((string path, byte[] bytes) in before) { Assert.Equal(bytes, File.ReadAllBytes(path)); }
    }

    public void Dispose() => SharedReadModel.DeleteFixture(_root, Prefix);
}
