using System.Diagnostics;
using System.Text;
using Atelia.DurableGraph.Build;

namespace Atelia.DurableGraph.Tests;

public sealed class SchemaHistoryPublicationConcurrencyTests {
    [Fact]
    public void WaitingPublisherReadsNewExactHistoryWithoutRewritingIt() {
        using Fixture fixture = new();
        string manifest = fixture.WriteManifest("candidate.g.cs", Record());
        using SchemaHistoryDirectoryLease lease = SchemaHistoryDirectoryLease.Acquire(fixture.History);
        using ToolProcess child = new("publish", manifest, fixture.History);
        child.AssertWaiting();
        Assert.False(Directory.Exists(fixture.History));

        // Mutex ownership is recursive on this thread. The child must read after release.
        new SchemaHistoryTool().Publish(manifest, fixture.History);
        string[] before = fixture.ReadHistory();
        lease.Dispose();

        ToolResult result = child.Complete();
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("published 0 schema-history record(s); 1 already exact", result.Output, StringComparison.Ordinal);
        Assert.Equal(before, fixture.ReadHistory());
        new SchemaHistoryTool().Verify(manifest, fixture.History);
    }

    [Theory]
    [InlineData(1, 1, 0, 3, "conflicts with manifest")]
    [InlineData(2, 2, 0, 2, "changes kind")]
    [InlineData(2, 1, 1, 2, "changes arity")]
    public void WaitingPublisherRejectsConflictsAgainstNewHistoryBeforeWriting(
        int version, int kind, int arity, int fieldTag, string diagnostic) {
        using Fixture fixture = new();
        string accepted = fixture.WriteManifest("accepted.g.cs", Record());
        string conflicting = fixture.WriteManifest("conflicting.g.cs", Record(version, kind, arity, fieldTag));
        using SchemaHistoryDirectoryLease lease = SchemaHistoryDirectoryLease.Acquire(fixture.History);
        using ToolProcess child = new("publish", conflicting, fixture.History);
        child.AssertWaiting();

        new SchemaHistoryTool().Publish(accepted, fixture.History);
        string[] before = fixture.ReadHistory();
        lease.Dispose();

        ToolResult result = child.Complete();
        Assert.Equal(2, result.ExitCode);
        Assert.Contains(diagnostic, result.Error, StringComparison.Ordinal);
        Assert.Equal(before, fixture.ReadHistory());
        new SchemaHistoryTool().Verify(accepted, fixture.History);
    }

    [Fact]
    public void WaitingVerifierReadsHistoryPublishedBeforeLeaseRelease() {
        using Fixture fixture = new();
        string manifest = fixture.WriteManifest("candidate.g.cs", Record());
        using SchemaHistoryDirectoryLease lease = SchemaHistoryDirectoryLease.Acquire(fixture.History);
        using ToolProcess child = new("verify", manifest, fixture.History);
        child.AssertWaiting();
        Assert.False(Directory.Exists(fixture.History));

        new SchemaHistoryTool().Publish(manifest, fixture.History);
        string[] before = fixture.ReadHistory();
        lease.Dispose();

        ToolResult result = child.Complete();
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("verified 1 current manifest candidate(s) against 1 schema-history record(s)", result.Output, StringComparison.Ordinal);
        Assert.Equal(before, fixture.ReadHistory());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EquivalentPathsShareLeaseAndTimeoutLeavesHistoryUntouched(bool existingHistory) {
        using Fixture fixture = new();
        if (existingHistory) {
            new SchemaHistoryTool().Publish(fixture.WriteManifest("candidate.g.cs", Record()), fixture.History);
        }
        string[] before = fixture.ReadHistory();
        string alias = Path.Combine(fixture.Root, "unused", "..", "history") + Path.DirectorySeparatorChar;
        if (OperatingSystem.IsWindows()) {
            alias = alias.ToUpperInvariant();
        }
        using SchemaHistoryDirectoryLease lease = SchemaHistoryDirectoryLease.Acquire(fixture.History);
        Exception? failure = null;
        Thread contender = new(() => {
            try {
                using SchemaHistoryDirectoryLease unexpected = SchemaHistoryDirectoryLease.Acquire(alias, TimeSpan.FromMilliseconds(150));
            } catch (Exception exception) {
                failure = exception;
            }
        }) { IsBackground = true };
        contender.Start();
        Assert.True(contender.Join(TimeSpan.FromSeconds(5)), "Lease timeout did not finish within five seconds.");

        SchemaHistoryException timeout = Assert.IsType<SchemaHistoryException>(failure);
        Assert.Contains(Path.TrimEndingDirectorySeparator(Path.GetFullPath(alias)), timeout.Message, StringComparison.Ordinal);
        Assert.Contains("serially", timeout.Message, StringComparison.Ordinal);
        Assert.Equal(before, fixture.ReadHistory());
        Assert.Equal(existingHistory, Directory.Exists(fixture.History));

        lease.Dispose();
        using SchemaHistoryDirectoryLease reacquired = SchemaHistoryDirectoryLease.Acquire(alias, TimeSpan.Zero);
    }

    private static SchemaHistoryRecord Record(int version = 1, int kind = 1, int arity = 0, int fieldTag = 2) {
        const string id = "samples.concurrent";
        return new SchemaHistoryRecord(id, Convert.ToBase64String(Encoding.UTF8.GetBytes(id)),
            version, [new SchemaHistoryField(1, fieldTag)], kind: kind, arity: arity);
    }

    private sealed record ToolResult(int ExitCode, string Output, string Error);

    private sealed class ToolProcess : IDisposable {
        private readonly Process _process;
        private readonly Task<string> _output;
        private readonly Task<string> _error;

        internal ToolProcess(string command, string manifest, string history) {
            string tool = typeof(SchemaHistoryTool).Assembly.Location;
            Assert.True(File.Exists(Path.ChangeExtension(tool, ".runtimeconfig.json")), "Build tool runtimeconfig must accompany the referenced assembly.");
            ProcessStartInfo start = new("dotnet") {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string argument in new[] { tool, command, "--manifest", manifest, "--schema-history", history }) {
                start.ArgumentList.Add(argument);
            }
            _process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the Schema-history tool.");
            _output = _process.StandardOutput.ReadToEndAsync();
            _error = _process.StandardError.ReadToEndAsync();
        }

        internal void AssertWaiting() {
            // A bounded observation, not an assumption that two starts hit the same Move.
            Assert.False(_process.WaitForExit(1000), "The tool completed while another process held its directory lease.");
        }

        internal ToolResult Complete() {
            Assert.True(_process.WaitForExit(15000), "Schema-history tool did not finish within fifteen seconds.");
            Assert.True(Task.WaitAll([_output, _error], TimeSpan.FromSeconds(5)), "Tool output did not close.");
            return new ToolResult(_process.ExitCode, _output.GetAwaiter().GetResult(), _error.GetAwaiter().GetResult());
        }

        public void Dispose() {
            if (!_process.HasExited) {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
            _process.Dispose();
        }
    }

    private sealed class Fixture : IDisposable {
        internal Fixture() {
            Root = Path.Combine(Path.GetTempPath(), "Atelia.DurableGraph.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        internal string Root { get; }
        internal string History => Path.Combine(Root, "history");

        internal string WriteManifest(string name, params SchemaHistoryRecord[] records) {
            string path = Path.Combine(Root, name);
            string contents = "// durable-graph-schema-history-manifest:9\n" + string.Concat(records.Select(record =>
                SchemaHistoryTextCodec.RenderHistory(record).Replace("// durable-graph-schema-history:9\n", "", StringComparison.Ordinal)));
            File.WriteAllText(path, contents, SchemaHistoryTextCodec.Utf8NoBom);
            return path;
        }

        internal string[] ReadHistory() => Directory.Exists(History)
            ? Directory.GetFiles(History).Order(StringComparer.Ordinal).Select(path =>
                Path.GetFileName(path) + "|" + File.GetLastWriteTimeUtc(path).Ticks + "|" + Convert.ToHexString(File.ReadAllBytes(path))).ToArray()
            : [];

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
