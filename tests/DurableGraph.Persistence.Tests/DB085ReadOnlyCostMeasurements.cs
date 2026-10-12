using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Atelia.Rbf;
using Xunit.Abstractions;
using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;

namespace Atelia.DurableGraph.Persistence.Tests;

/// <summary>Internal DB-085 experiment: time is evidence, never a pass threshold.</summary>
public sealed class DB085ReadOnlyCostMeasurements(ITestOutputHelper output) {
    private static readonly string[] Operations = ["event", "state", "checkpoint"];

    [Fact]
    public void Healthy_fixed_fixtures_are_read_only_and_unrelated_history_work_is_bounded() {
        foreach (bool after in new[] { false, true }) {
            using DB085ReadOnlyCostFixture empty = new(new("history", 0, after));
            using DB085ReadOnlyCostFixture small = new(new("history", 16, after));
            AssertFixedHistoryProperties(empty, small);
            foreach (string operation in Operations) {
                var before = small.Snapshot();
                Sample baseline = RunCore(small, candidate: false, operation);
                Sample candidate = RunCore(small, candidate: true, operation);
                Assert.Equal(baseline.Checksum, candidate.Checksum);
                Assert.True(baseline.Total.Reads.ReadCalls > 0);
                Assert.True(candidate.Total.Reads.ReadCalls > 0);
                small.AssertUnchanged(before);
            }
            // Both histories span more than a tiny initial read buffer. Raw returned bytes can differ
            // with address alignment, so the bounded-work witness checks logical requests instead.
            using DB085ReadOnlyCostFixture medium = new(new("history", 256, after));
            using DB085ReadOnlyCostFixture large = new(new("history", 1024, after));
            AssertFixedHistoryProperties(empty, medium);
            AssertFixedHistoryProperties(empty, large);
            foreach (string operation in Operations) {
                var mediumBefore = medium.Snapshot();
                var largeBefore = large.Snapshot();
                Sample first = RunCore(medium, candidate: true, operation);
                Sample second = RunCore(large, candidate: true, operation);
                Assert.Equal(first.Checksum, second.Checksum);
                Assert.True(second.Total.Reads.ReadCalls <= first.Total.Reads.ReadCalls + 8,
                    $"Unrelated history increased logical read calls: {first.Total.Reads.ReadCalls} -> {second.Total.Reads.ReadCalls}.");
                Assert.True(second.Total.Reads.RequestedBytes <= first.Total.Reads.RequestedBytes + 4096,
                    $"Unrelated history increased logical requested bytes: {first.Total.Reads.RequestedBytes} -> {second.Total.Reads.RequestedBytes}.");
                medium.AssertUnchanged(mediumBefore);
                large.AssertUnchanged(largeBefore);
            }
        }
    }

    [Fact]
    public void Ancestor_schema_and_catalog_cells_change_only_the_selected_fixture_dimension() {
        using DB085ReadOnlyCostFixture baseline = new(new("history", 0));
        foreach (string axis in new[] { "ancestors", "schema", "catalog" }) {
            using DB085ReadOnlyCostFixture fixture = new(new(axis, 4));
            Assert.Equal(3, fixture.Facts.GraphNodes);
            Assert.Equal(1, fixture.Facts.BranchCount);
            Assert.Equal(axis == "catalog" ? 6 : 2, fixture.Facts.TagCount);
            Assert.Equal(axis == "schema" ? 5 : 1, fixture.Facts.SchemaCount);
            Assert.Equal(axis == "ancestors" ? 5 : 1, fixture.Facts.StateObjectVersionChainLength);
            Assert.Equal(axis == "ancestors" ? 6L : 2L, fixture.Facts.StateFrames);
            if (axis != "schema") { Assert.Equal(baseline.Facts.SchemaSha256, fixture.Facts.SchemaSha256); }
            if (axis != "catalog") { Assert.Equal(baseline.Facts.RefRbfBytes, fixture.Facts.RefRbfBytes); }
            var before = fixture.Snapshot();
            foreach (string operation in Operations) {
                Sample original = RunCore(fixture, candidate: false, operation);
                Sample candidate = RunCore(fixture, candidate: true, operation);
                Assert.Equal(original.Checksum, candidate.Checksum);
            }
            fixture.AssertUnchanged(before);
        }
    }

    [Fact]
    public void Measure_read_only_open_query_and_first_graph_read() {
        if (Environment.GetEnvironmentVariable("DURABLEGRAPH_DB085_MEASURE") != "1") { return; }
        string destination = Environment.GetEnvironmentVariable("DURABLEGRAPH_DB085_MEASURE_OUTPUT")
            ?? throw new InvalidOperationException("Set DURABLEGRAPH_DB085_MEASURE_OUTPUT to an absolute JSONL path.");
        if (!Path.IsPathFullyQualified(destination)) { throw new InvalidOperationException("Measurement output must be an absolute path."); }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using StreamWriter file = new(destination, append: false);
        Write(file, new {
            Kind = "configuration", RecordedSamples = 3, DiscardedWarmupSamples = 1,
            Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            OperatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            ProcessId = Environment.ProcessId,
            Pairing = "Same healthy fixture; baseline/candidate order alternates by sample; each operation opens fresh resources.",
            Cache = "Process/JIT warm within a test invocation; process isolation only between runner invocations; OS page cache uncontrolled.",
            Total = "Sum of API Open/Query/FirstRead stage costs. ChainWallMilliseconds also includes instrumentation and stage-end heap samples; no forced GC in that chain.",
            Reads = "RBF logical/raw requests and returned bytes, including headers; ReadAheadReturnedBytes is raw return outside logical request intervals, not device physical I/O.",
            MetadataFileStreamBytes = (long?)null,
            ReadCoverageGap = "FileStream metadata bytes (including segment/catalog locators) are not instrumented; their API time/allocation is included.",
            Allocation = "GC.GetAllocatedBytesForCurrentThread; excludes other threads/native memory.",
            Memory = "Separate fresh-open diagnostic, outside the timed core: GC.GetTotalMemory(true) deltas with repo and graphs kept alive. Includes shared model/runtime retention, not exclusive cache ownership.",
            Peak = "Stage-end GC.GetTotalMemory(false) is sampled occupied heap, may miss transient peaks. Process.PeakWorkingSet64 is cumulative process lifetime, including test host/JIT/earlier cells.",
            ExactOperationPeakBytes = (long?)null,
            DevicePhysicalReadBytes = (long?)null,
            Audit = "Independent full healthy fixture validation before samples; never inside candidate Open or any timed interval.",
            Family = "Hand-written SharedReadModel, three-node ring/aliases; no generator/package-consumer claim. No Checkout."
        });
        foreach (DB085CostCell cell in Cells()) {
            using DB085ReadOnlyCostFixture fixture = new(cell);
            Write(file, new { Kind = "fixture", Cell = cell, fixture.Facts });
            var before = fixture.Snapshot();
            foreach (string operation in Operations) {
                for (int sample = -1; sample < 3; sample++) {
                    foreach (bool candidate in sample % 2 == 0 ? new[] { false, true } : new[] { true, false }) {
                        Sample measured = RunCore(fixture, candidate, operation);
                        if (sample < 0) { continue; }
                        MemoryObservation memory = ObserveMemory(fixture, candidate, operation);
                        Write(file, new {
                            Kind = "sample", Cell = cell, Sample = sample,
                            Mode = candidate ? "candidate" : "baseline", Operation = operation,
                            measured.Open, measured.Query, measured.FirstRead, measured.Total,
                            measured.ChainWallMilliseconds, measured.Checksum,
                            measured.StageEndOccupiedManagedBytes, measured.ProcessLifetimePeakWorkingSetBytes,
                            MemoryObservationSeparateOpen = true, Memory = memory
                        });
                    }
                }
            }
            fixture.AssertUnchanged(before);
        }
    }

    private static IEnumerable<DB085CostCell> Cells() {
        string? selectedAxis = Environment.GetEnvironmentVariable("DURABLEGRAPH_DB085_MEASURE_AXIS");
        string? selectedScale = Environment.GetEnvironmentVariable("DURABLEGRAPH_DB085_MEASURE_SCALE");
        if (string.IsNullOrWhiteSpace(selectedAxis)) { selectedAxis = null; }
        if (string.IsNullOrWhiteSpace(selectedScale)) { selectedScale = null; }
        if (selectedAxis is not null && selectedAxis is not ("history" or "ancestors" or "schema" or "catalog")) {
            throw new InvalidOperationException("Measurement axis must be history, ancestors, schema or catalog.");
        }
        int? scale = selectedScale is null ? null : int.Parse(selectedScale, System.Globalization.CultureInfo.InvariantCulture);
        if (scale is < 0) { throw new InvalidOperationException("Measurement scale must be nonnegative."); }
        foreach (string axis in new[] { "history", "ancestors", "schema", "catalog" }) {
            if (selectedAxis is not null && axis != selectedAxis) { continue; }
            int[] values = scale is { } requested ? [requested] : axis switch {
                "history" => [0, 256, 1024], "ancestors" => [0, 4, 32], _ => [0, 16, 256]
            };
            foreach (int value in values) {
                if (axis == "history") {
                    yield return new(axis, value, false);
                    yield return new(axis, value, true);
                } else { yield return new(axis, value); }
            }
        }
    }

    private static void AssertFixedHistoryProperties(DB085ReadOnlyCostFixture baseline, DB085ReadOnlyCostFixture fixture) {
        Assert.Equal(baseline.Facts.SchemaSha256, fixture.Facts.SchemaSha256);
        Assert.Equal(baseline.Facts.SchemaCount, fixture.Facts.SchemaCount);
        Assert.Equal(baseline.Facts.GraphNodes, fixture.Facts.GraphNodes);
        Assert.Equal(baseline.Facts.StateObjectVersionChainLength, fixture.Facts.StateObjectVersionChainLength);
        Assert.Equal(baseline.Facts.BranchCount, fixture.Facts.BranchCount);
        Assert.Equal(baseline.Facts.TagCount, fixture.Facts.TagCount);
        Assert.Equal(baseline.Facts.RefRbfFiles, fixture.Facts.RefRbfFiles);
        Assert.Equal(baseline.Facts.RefRbfBytes, fixture.Facts.RefRbfBytes);
        Assert.Equal(baseline.Facts.StateFrames + fixture.Cell.UnrelatedHistory, fixture.Facts.StateFrames);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Sample RunCore(DB085ReadOnlyCostFixture fixture, bool candidate, string operation) {
        StateModelRegistry models = SharedReadModel.Models();
        using var metrics = RbfReadMetrics.Begin();
        long chainStart = Stopwatch.GetTimestamp();
        Stamp start = Stamp.Now(metrics.Snapshot());
        using Repository repository = Open(fixture.Path, models, candidate);
        Stamp opened = Stamp.Now(metrics.Snapshot());
        long openHeap = GC.GetTotalMemory(forceFullCollection: false);
        Stamp queryStart = Stamp.Now(metrics.Snapshot());
        CheckpointAddress address = Query(repository, operation);
        Stamp queried = Stamp.Now(metrics.Snapshot());
        long queryHeap = GC.GetTotalMemory(forceFullCollection: false);
        Stamp readStart = Stamp.Now(metrics.Snapshot());
        object result = Read(repository, address, operation);
        Stamp read = Stamp.Now(metrics.Snapshot());
        double chainMilliseconds = Stopwatch.GetElapsedTime(chainStart, read.Timestamp).TotalMilliseconds;
        long readHeap = GC.GetTotalMemory(forceFullCollection: false);
        ulong checksum = AssertResult(result, operation);
        Cost openCost = Between(start, opened), queryCost = Between(queryStart, queried), readCost = Between(readStart, read);
        using Process process = Process.GetCurrentProcess();
        Sample sample = new(openCost, queryCost, readCost, Add(openCost, queryCost, readCost), chainMilliseconds,
            checksum, [openHeap, queryHeap, readHeap], process.PeakWorkingSet64);
        GC.KeepAlive(result);
        GC.KeepAlive(repository);
        return sample;
    }

    private static Repository Open(string path, StateModelRegistry models, bool candidate) => candidate
        ? Repository.OpenReadOnlyCandidate(path, models)
        : Repository.OpenReadOnlyExisting(path, models);

    private static CheckpointAddress Query(Repository repository, string operation) => operation == "state"
        ? repository.ResolveTag(DB085ReadOnlyCostFixture.StateTag) : repository.GetHead("main");

    private static object Read(Repository repository, CheckpointAddress address, string operation) => operation switch {
        "state" => repository.ReadState(address), "event" => repository.ReadEvent(address),
        "checkpoint" => repository.ReadCheckpoint(address), _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private static ulong AssertResult(object result, string operation) {
        if (operation != "checkpoint") { return DB085ReadOnlyCostFixture.AssertGraph(result, operation == "state" ? (byte)11 : (byte)21); }
        EventCheckpoint checkpoint = Assert.IsType<EventCheckpoint>(result);
        Assert.NotNull(checkpoint.PreviousStateAddress);
        Node domainEvent = Assert.IsType<Node>(checkpoint.Event);
        Node state = Assert.IsType<Node>(checkpoint.PreviousState);
        Assert.NotSame(domainEvent, state);
        Assert.NotSame(domainEvent.Next, state.Next);
        return unchecked(DB085ReadOnlyCostFixture.AssertGraph(domainEvent, 21) * 31 + DB085ReadOnlyCostFixture.AssertGraph(state, 11));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static MemoryObservation ObserveMemory(DB085ReadOnlyCostFixture fixture, bool candidate, string operation) {
        StateModelRegistry models = SharedReadModel.Models();
        long baseline = GC.GetTotalMemory(forceFullCollection: true);
        using Repository repository = Open(fixture.Path, models, candidate);
        long afterOpen = GC.GetTotalMemory(forceFullCollection: true);
        long withResult = ReadAndSampleHeap(repository, operation);
        long afterResultRelease = GC.GetTotalMemory(forceFullCollection: true);
        GC.KeepAlive(repository);
        GC.KeepAlive(models);
        return new(baseline, afterOpen, withResult, afterResultRelease,
            afterOpen - baseline, withResult - baseline, afterResultRelease - baseline);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long ReadAndSampleHeap(Repository repository, string operation) {
        object result = Read(repository, Query(repository, operation), operation);
        _ = AssertResult(result, operation);
        long heap = GC.GetTotalMemory(forceFullCollection: true);
        GC.KeepAlive(result);
        return heap;
    }

    private static Cost Between(Stamp start, Stamp end) => new(
        Stopwatch.GetElapsedTime(start.Timestamp, end.Timestamp).TotalMilliseconds,
        end.AllocatedBytes - start.AllocatedBytes, Difference(start.Reads, end.Reads));

    private static RbfReadMetricsSnapshot Difference(RbfReadMetricsSnapshot start, RbfReadMetricsSnapshot end) => new(
        end.ReadCalls - start.ReadCalls, end.RequestedBytes - start.RequestedBytes, end.ReturnedBytes - start.ReturnedBytes,
        end.RawReadCalls - start.RawReadCalls, end.RawRequestedBytes - start.RawRequestedBytes, end.RawReturnedBytes - start.RawReturnedBytes,
        end.ReadAheadReturnedBytes - start.ReadAheadReturnedBytes, end.HeaderReadCalls - start.HeaderReadCalls);

    private static Cost Add(Cost first, Cost second, Cost third) => new(first.Milliseconds + second.Milliseconds + third.Milliseconds,
        first.AllocatedBytes + second.AllocatedBytes + third.AllocatedBytes,
        new(first.Reads.ReadCalls + second.Reads.ReadCalls + third.Reads.ReadCalls,
            first.Reads.RequestedBytes + second.Reads.RequestedBytes + third.Reads.RequestedBytes,
            first.Reads.ReturnedBytes + second.Reads.ReturnedBytes + third.Reads.ReturnedBytes,
            first.Reads.RawReadCalls + second.Reads.RawReadCalls + third.Reads.RawReadCalls,
            first.Reads.RawRequestedBytes + second.Reads.RawRequestedBytes + third.Reads.RawRequestedBytes,
            first.Reads.RawReturnedBytes + second.Reads.RawReturnedBytes + third.Reads.RawReturnedBytes,
            first.Reads.ReadAheadReturnedBytes + second.Reads.ReadAheadReturnedBytes + third.Reads.ReadAheadReturnedBytes,
            first.Reads.HeaderReadCalls + second.Reads.HeaderReadCalls + third.Reads.HeaderReadCalls));

    private void Write(StreamWriter file, object row) {
        string json = JsonSerializer.Serialize(row);
        file.WriteLine(json);
        file.Flush();
        output.WriteLine("DB085_MEASUREMENT " + json);
    }

    private readonly record struct Stamp(long Timestamp, long AllocatedBytes, RbfReadMetricsSnapshot Reads) {
        internal static Stamp Now(RbfReadMetricsSnapshot reads) => new(Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread(), reads);
    }
    private sealed record Cost(double Milliseconds, long AllocatedBytes, RbfReadMetricsSnapshot Reads);
    private sealed record Sample(Cost Open, Cost Query, Cost FirstRead, Cost Total, double ChainWallMilliseconds,
        ulong Checksum, long[] StageEndOccupiedManagedBytes, long ProcessLifetimePeakWorkingSetBytes);
    private sealed record MemoryObservation(long BaselineManagedBytes, long ManagedBytesAfterOpen, long ManagedBytesWithResult,
        long ManagedBytesAfterResultRelease, long RetainedOpenDeltaBytes, long RetainedWithResultDeltaBytes, long RetainedAfterResultReleaseDeltaBytes);
}
