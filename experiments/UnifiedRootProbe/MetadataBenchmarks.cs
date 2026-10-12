using Atelia.Rbf;
using Atelia.Rbf.Internal;

namespace Atelia.UnifiedRootProbe;

internal sealed record MetadataBenchmarkSample(
    string Route, string Axis, int Scale, int Sample, bool Warmup, string Fixture,
    object BusinessViewAfterCommit, object ReadView,
    ProbeMeasurement Commit, ProbeMeasurement? PostCommitReadback,
    ProbeMeasurement Open, ProbeMeasurement FirstRead,
    ProbeMeasurement OpenAndFirstRead, ProbeMeasurement? Recovery,
    string RecoveryStatus, long PhysicalRbfWrittenBytes, long DirectoryAppendedBytes,
    double DirectoryBytesPerBusinessPayloadByte, int PhysicalFileCount,
    int RetainedOrphanFrameCount, int ResidualFileCount,
    string MeasuredWriteBoundary, string MetadataIoBoundary);

internal sealed record MetadataRecoveryActionSample(
    string Kind, int Sample, bool Warmup, string Fixture, string SetupMechanism,
    InventoryFile[] CompleteFactsBefore, ProbeMeasurement Recovery,
    string Status, string? Action, MetadataView DeliveredView, MetadataView ContinuedView,
    int ResidualFileCount);

internal sealed record MetadataBenchmarkReport(
    string Experiment, string FixtureParent, string[] Axes, int[] Scales,
    string MeasurementBoundary, string RecoveryBoundary, string Limitations,
    MetadataBenchmarkSample[] Samples, MetadataRecoveryActionSample[] ActionSamples);

internal static class MetadataBenchmarks {
    private static readonly int[] Scales = [0, 16, 128];

    internal static MetadataBenchmarkReport Run(string scratchParent) {
        scratchParent = Path.GetFullPath(scratchParent);
        string taskRoot = Path.GetDirectoryName(scratchParent)!;
        string expectedBase = Path.GetFullPath(@"C:\Users\gdtut\AppData\Local\Temp\db085-metadata-20260930");
        if (!Path.GetFileName(scratchParent).StartsWith("bench-", StringComparison.Ordinal) ||
            !string.Equals(Path.GetDirectoryName(taskRoot), expectedBase, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(taskRoot, ".db085-metadata-fixture")) ||
            Directory.Exists(scratchParent)) {
            throw new ArgumentException("Measurement needs a new bench-* directory in a marked task GUID.", nameof(scratchParent));
        }
        Directory.CreateDirectory(scratchParent);
        var samples = new List<MetadataBenchmarkSample>();
        var actions = new List<MetadataRecoveryActionSample>();
        foreach (string route in new[] { "candidate", "baseline" }) {
            foreach (string axis in new[] { "history", "residues", "unrelatedBranches" }) {
                foreach (int scale in Scales) {
                    for (int sample = -1; sample < 3; sample++) {
                        samples.Add(RunSample(scratchParent, route, axis, scale, sample));
                    }
                }
            }
        }
        foreach (string kind in new[] { "partialHeader", "partialDirectory" }) {
            for (int sample = -1; sample < 3; sample++) {
                actions.Add(RunActionSample(scratchParent, kind, sample));
            }
        }
        return new("DB-085 metadata-directory and real second-segment comparison", scratchParent,
            ["history", "residues", "unrelatedBranches"], Scales,
            "Every cell uses one warmup and three independent new fixtures. Commit includes fresh writer admission, event/identity/directory/root publication, invoked flushes, and writer disposal. Candidate returns a view from qualified/known data; baseline ViewReadback is a separate fresh business observation and is recorded as PostCommitReadback, not included in Commit. Open and FirstRead are separate synchronous scopes on another fresh reader; OpenAndFirstRead sums disjoint counters. The routes have different admission responsibilities, so do not use these numbers as a product speed ratio.",
            "Candidate Recovery is a separately measured full call on completed fixtures. ActionSamples use a real RBF BeforeWrite 2-byte partial write followed by an ordinary exception unwind; this is fixture setup, not a process kill. The PowerShell runner supplies real external-kill F1 witnesses. The old route has no matching S1 recovery, so Recovery is null and no recovery ratio is reported.",
            "PhysicalRbfWrittenBytes is a separate RBF AfterWrite count; candidate Commit.DirectIo.WrittenBytes is logical successful frame bytes and must not be added to it. Baseline direct write counters are RBF physical writes; active.segment and catalog.snapshot FileStream write/flush/rename are unmeasured metadata I/O. DirectoryAppendedBytes is candidate catalog.rbf file growth, not all metadata I/O. RetainedOrphanFrameCount is fixture setup and ResidualFileCount counts unselected extra files immediately after recovery, before continuation; successful matrix samples have no extra file. Thread allocation is current-thread allocation, not peak memory. Neither process restart nor new fixture controls OS/device page caches; DurableFlush wait is not a power-loss guarantee. Neither route exercises DG graph/Schema business work.",
            samples.ToArray(), actions.ToArray());
    }

    private static MetadataBenchmarkSample RunSample(string parent, string route, string axis, int scale, int sample) {
        string fixture = Path.Combine(parent, $"{route}-{axis}-{scale}-{SampleName(sample)}-{Guid.NewGuid():N}");
        int history = axis == "history" ? scale : 0;
        int residues = axis == "residues" ? scale : 0;
        int unrelated = axis == "unrelatedBranches" ? scale : 0;
        if (route == "candidate") {
            MetadataStore.Seed(fixture, unrelated, history);
            if (residues > 0) { MetadataStore.PopulateOrphans(fixture, residues); }
            InventoryFile[] before = MetadataStore.Inventory(fixture);
            long physicalBytes = 0;
            RbfWriteHooks? previous = RbfWriteInstrumentation.Current;
            try {
                RbfWriteInstrumentation.Current = new RbfWriteHooks {
                    AfterWrite = observation => physicalBytes += observation.WrittenBytes
                };
                var commit = ProbeMeasurements.Measure(() => MetadataStore.CreateB(fixture));
                RbfWriteInstrumentation.Current = previous;
                var opened = ProbeMeasurements.Measure(() => MetadataStore.Open(fixture));
                MetadataView view;
                ProbeMeasurement firstRead;
                using (opened.Result) {
                    var read = ProbeMeasurements.Measure(opened.Result.ReadAll);
                    view = read.Result;
                    firstRead = read.Measurement;
                }
                if (!SameView(commit.Result, view)) { throw new InvalidDataException("Candidate commit/readback differs."); }
                InventoryFile[] after = MetadataStore.Inventory(fixture);
                long directoryBytes = Growth(before, after, "catalog.rbf");
                var recovered = ProbeMeasurements.Measure(() => MetadataStore.Recover(fixture));
                if (recovered.Result.Status != "Succeeded" || !SameView(recovered.Result.View, view)) {
                    throw new InvalidDataException("Completed candidate fixture did not recover its selected directory.");
                }
                return new(route, axis, scale, sample, sample < 0, fixture,
                    commit.Result, view, commit.Measurement, null,
                    opened.Measurement, firstRead,
                    Sum(opened.Measurement, firstRead), recovered.Measurement,
                    recovered.Result.Status, physicalBytes, directoryBytes,
                    directoryBytes / 32.0, CountFiles(fixture), residues, 0,
                    "PhysicalRbfWrittenBytes from RBF AfterWrite; DirectIo.WrittenBytes in Commit is logical frame append bytes and overlaps, not additive.",
                    "RBF frame writes and flush waits counted; file create/name and directory-entry persistence uncounted.");
            }
            finally { RbfWriteInstrumentation.Current = previous; }
        }
        else {
            MetadataBaseline.Seed(fixture, unrelated, history, residues);
            MetadataBaselineWriteResult commit = MetadataBaseline.CreateB(fixture);
            MetadataBaselineInspectResult readback = MetadataBaseline.Inspect(fixture);
            if (!SameBusinessView(commit.View, readback.View)) {
                throw new InvalidDataException("Baseline commit/readback differs.");
            }
            return new(route, axis, scale, sample, sample < 0, fixture,
                commit.View, readback.View, commit.Measurement, commit.ViewReadback,
                readback.Open, readback.FirstRead,
                Sum(readback.Open, readback.FirstRead), null, "NotImplemented",
                commit.Measurement.DirectIo.WrittenBytes, 0, 0, CountFiles(fixture), residues, 0,
                "Commit.DirectIo.WrittenBytes counts actual RBF writes; this includes EventJournal RBF frames, not external metadata files.",
                "active.segment and catalog.snapshot temp writes/flushes/moves omitted. Their elapsed time is included. No directory amplification ratio is claimed for the old protocol.");
        }
    }

    private static MetadataRecoveryActionSample RunActionSample(string parent, string kind, int sample) {
        string fixture = Path.Combine(parent, $"action-{kind}-{SampleName(sample)}-{Guid.NewGuid():N}");
        MetadataView seed = MetadataStore.Seed(fixture);
        string target = kind == "partialHeader"
            ? Path.Combine(fixture, "events", "segments", "00000002.rbf")
            : Path.Combine(fixture, "catalog.rbf");
        RbfWriteHooks? previous = RbfWriteInstrumentation.Current;
        bool stopped = false;
        try {
            RbfWriteInstrumentation.Current = new RbfWriteHooks {
                BeforeWrite = request => request.Path is not null &&
                    string.Equals(Path.GetFullPath(request.Path), target, StringComparison.OrdinalIgnoreCase)
                    ? 2 : request.RequestedBytes
            };
            try { MetadataStore.CreateB(fixture); }
            catch (IOException error) when (error.Message.Contains("partial RBF write", StringComparison.Ordinal)) {
                stopped = true;
            }
        }
        finally { RbfWriteInstrumentation.Current = previous; }
        if (!stopped) { throw new InvalidDataException("Action setup did not create the expected real 2-byte RBF write."); }
        InventoryFile[] before = MetadataStore.Inventory(fixture);
        string relativeTarget = kind == "partialHeader" ? "events/segments/00000002.rbf" : "catalog.rbf";
        InventoryFile shortFile = before.Single(file => file.Path == relativeTarget);
        if (shortFile.Length != shortFile.CompleteLength + 2 ||
            before.Count(file => file.Length > file.CompleteLength) != 1) {
            throw new InvalidDataException("Action fixture must have exactly one 2-byte short tail.");
        }
        byte[] shortBytes = File.ReadAllBytes(target);
        var recovered = ProbeMeasurements.Measure(() => MetadataStore.Recover(fixture));
        if (recovered.Result.Status != "Succeeded" || recovered.Result.Action is null) {
            throw new InvalidDataException("Recovery did not perform the expected qualified action.");
        }
        InventoryFile[] after = MetadataStore.Inventory(fixture);
        AssertCompleteFacts(before, after);
        InventoryFile repaired = after.Single(file => file.Path == relativeTarget);
        byte[] repairedBytes = File.ReadAllBytes(target);
        if (kind == "partialHeader" &&
                (repaired.Length != 4 || repairedBytes.Length != 4 ||
                 !shortBytes.AsSpan().SequenceEqual(repairedBytes.AsSpan(0, 2))) ||
            kind == "partialDirectory" && repaired.Length != shortFile.CompleteLength) {
            throw new InvalidDataException("Recovery did not perform the expected header completion or tail repair.");
        }
        MetadataView delivered = ReadView(fixture);
        if (!SameView(seed, delivered)) { throw new InvalidDataException("Action changed the selected old root."); }
        MetadataView continued = MetadataStore.CreateB(fixture);
        if (continued.Generation != seed.Generation + 1 ||
            !SameView(ReadView(fixture), continued)) {
            throw new InvalidDataException("Recovered fixture cannot continue and read back.");
        }
        return new(kind, sample, sample < 0, fixture,
            "Real RBF 2-byte BeforeWrite partial and caught IOException; exception unwind, not external kill.",
            before, recovered.Measurement, recovered.Result.Status, recovered.Result.Action,
            delivered, continued, 1);
    }

    private static string SampleName(int sample) => sample < 0 ? "warmup" : sample.ToString();

    private static int CountFiles(string fixture) =>
        Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories).Count();

    private static MetadataView ReadView(string fixture) {
        using var reader = MetadataStore.Open(fixture);
        return reader.ReadAll();
    }

    private static long Growth(InventoryFile[] before, InventoryFile[] after, string path) =>
        after.Single(file => file.Path == path).Length - before.Single(file => file.Path == path).Length;

    private static bool SameView(MetadataView? a, MetadataView? b) {
        if (a is null || b is null || a.Generation != b.Generation || a.ActiveSegment != b.ActiveSegment ||
            a.Segment1Eof != b.Segment1Eof || a.Segment2Eof != b.Segment2Eof ||
            a.AllocationEof != b.AllocationEof || a.Branches.Length != b.Branches.Length) { return false; }
        return a.Branches.SequenceEqual(b.Branches);
    }

    private static bool SameBusinessView(object a, object b) =>
        System.Text.Json.JsonSerializer.Serialize(a) == System.Text.Json.JsonSerializer.Serialize(b);

    private static void AssertCompleteFacts(InventoryFile[] before, InventoryFile[] after) {
        foreach (InventoryFile file in before) {
            InventoryFile observed = after.Single(candidate => candidate.Path == file.Path);
            if (observed.CompleteLength < file.CompleteLength ||
                (observed.CompleteLength == file.CompleteLength && observed.Sha256 != file.Sha256) ||
                !observed.Offsets.Take(file.Offsets.Length).SequenceEqual(file.Offsets)) {
                throw new InvalidDataException("Recovery changed a complete prefix or address: " + file.Path);
            }
        }
    }

    // Non-overlapping scopes: direct counters propagate to nested scopes, so
    // a surrounding measured scope must not be added to these two intervals.
    private static ProbeMeasurement Sum(ProbeMeasurement x, ProbeMeasurement y) {
        RbfReadMetricsSnapshot a = x.RbfReads, b = y.RbfReads;
        ProbeIoSnapshot d = x.DirectIo, e = y.DirectIo;
        return new ProbeMeasurement(x.ElapsedTicks + y.ElapsedTicks,
            x.ElapsedMilliseconds + y.ElapsedMilliseconds,
            x.ThreadAllocatedBytes + y.ThreadAllocatedBytes,
            new RbfReadMetricsSnapshot(a.ReadCalls + b.ReadCalls,
                a.RequestedBytes + b.RequestedBytes, a.ReturnedBytes + b.ReturnedBytes,
                a.RawReadCalls + b.RawReadCalls, a.RawRequestedBytes + b.RawRequestedBytes,
                a.RawReturnedBytes + b.RawReturnedBytes,
                a.ReadAheadReturnedBytes + b.ReadAheadReturnedBytes,
                a.HeaderReadCalls + b.HeaderReadCalls),
            new ProbeIoSnapshot(d.ReadCalls + e.ReadCalls,
                d.RequestedBytes + e.RequestedBytes, d.ReturnedBytes + e.ReturnedBytes,
                d.ReadAheadReturnedBytes + e.ReadAheadReturnedBytes,
                d.WriteCalls + e.WriteCalls, d.WrittenBytes + e.WrittenBytes,
                d.FlushCalls + e.FlushCalls, d.FlushTicks + e.FlushTicks,
                d.FlushMilliseconds + e.FlushMilliseconds));
    }
}
