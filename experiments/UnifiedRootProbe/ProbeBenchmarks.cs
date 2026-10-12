using Atelia.Rbf;
using Atelia.Rbf.Internal;

namespace Atelia.UnifiedRootProbe;

internal sealed record ProbeBenchmarkSample(
    string Axis, int Scale, int Sample, bool Warmup, string Fixture,
    int CompleteAFramesBefore, int CompleteBFramesBefore, int CompleteRootFramesBefore,
    RootView CommittedView, RootView ReadView,
    ProbeMeasurement Commit, ProbeMeasurement Open, ProbeMeasurement FirstRead,
    ProbeMeasurement OpenAndFirstRead, ProbeMeasurement Recovery,
    ProbeRecoveryPhases RecoveryPhases,
    string RecoveryStatus, string? RecoveryTruncatedFile);

internal sealed record ProbeRecoveryPhases(
    ProbeMeasurement QualificationIncludingDetection,
    ProbeMeasurement ActionIncludingRecheck,
    ProbeMeasurement FreshQualificationAndConfirmation);

internal sealed record ProbeRecoveryActionSample(
    string ShortFile, int Sample, bool Warmup, string Fixture,
    InventoryFile[] CompleteFactsBefore, ProbeMeasurement Recovery,
    ProbeRecoveryPhases RecoveryPhases, RootView DeliveredView, RootView ContinuedView);

internal sealed record ProbeBenchmarkReport(
    string Experiment, string FixtureParent, string[] Axes, int[] Scales,
    string MeasurementBoundary, string RecoveryBoundary, string Limitations,
    ProbeBenchmarkSample[] Samples, ProbeRecoveryActionSample[] ActionSamples);

internal static class ProbeBenchmarks {
    private sealed class ExpectedOrphanStopException : Exception { }
    private static readonly int[] Scales = [0, 32, 256];

    // Program calls this once per fresh process. No sample reuses a Store, handle,
    // repository lock, or fixture; warmup is retained in the raw report but must
    // be excluded from the three recorded samples' statistics.
    internal static ProbeBenchmarkReport Run(string scratchParent) {
        scratchParent = Path.GetFullPath(scratchParent);
        if (Path.GetPathRoot(scratchParent) != "C:\\" ||
            !Path.GetFileName(scratchParent).StartsWith("bench-", StringComparison.Ordinal) ||
            !File.Exists(Path.Combine(Path.GetDirectoryName(scratchParent)!, ".db085-unified-root-fixture"))) {
            throw new ArgumentException("Benchmark fixtures require a new bench-* directory under the marked C: task root.",
                nameof(scratchParent));
        }
        if (Directory.Exists(scratchParent)) { throw new IOException("Benchmark parent must be new."); }
        Directory.CreateDirectory(scratchParent);
        var samples = new List<ProbeBenchmarkSample>();
        var actionSamples = new List<ProbeRecoveryActionSample>();
        foreach (string axis in new[] { "history", "orphan" }) {
            foreach (int scale in Scales) {
                for (int sample = -1; sample < 3; sample++) {
                    samples.Add(RunSample(scratchParent, axis, scale, sample));
                }
            }
        }
        foreach (string shortFile in new[] { "a.rbf", "roots.rbf" }) {
            for (int sample = -1; sample < 3; sample++) {
                actionSamples.Add(RunActionSample(scratchParent, shortFile, sample));
            }
        }
        return new ProbeBenchmarkReport(
            "DB-085 fixed A/B unified-root mechanism", scratchParent, ["history", "orphan"], Scales,
            "Commit includes writer qualification, pre-append confirmation, A/B/root append and durable barriers. " +
            "Open and FirstRead are disjoint synchronous scopes on one new reader; OpenAndFirstRead is their fieldwise sum, excluding the gap and Dispose.",
            "Recovery is a separate full Recover call on completed fixtures; ActionSamples separately measure real 2-byte A/root suffix repair. " +
            "Phase windows end at CandidateResourcesDisposed, AfterAction, and BeforeDelivery; total additionally includes final delivery. " +
            "The first phase combines detection with qualification.",
            "OS and device caches are not cold. RBF and direct read bytes are software request/return counts, not device I/O. " +
            "ThreadAllocatedBytes is current-thread managed allocation, not peak or retained memory. FlushMilliseconds is observed API wait, not a power-loss guarantee.",
            samples.ToArray(), actionSamples.ToArray());
    }

    private static ProbeBenchmarkSample RunSample(string parent, string axis, int scale, int sample) {
        string fixture = Path.Combine(parent, $"{axis}-{scale}-{(sample < 0 ? "warmup" : sample.ToString())}-{Guid.NewGuid():N}");
        UnifiedRootStore.Seed(fixture, 10, 100);
        if (axis == "history") {
            for (int i = 0; i < scale; i++) {
                UnifiedRootStore.Commit(fixture, 10, 100);
            }
        }
        else {
            // A real writer appends complete A and B frames then throws before
            // either new-data barrier or root append. This is fixture setup, not
            // evidence of process crash. Reopen/confirm once before measuring.
            for (int i = 0; i < scale; i++) {
                try {
                    UnifiedRootStore.Commit(fixture, 1000 + i, 2000 + i, point => {
                        if (point == "AfterAppendB") { throw new ExpectedOrphanStopException(); }
                    });
                    throw new InvalidOperationException("The orphan setup did not stop before publication.");
                }
                catch (ExpectedOrphanStopException) { }
            }
            if (scale > 0) {
                RecoveryResult setupRecovery = UnifiedRootStore.Recover(fixture);
                if (setupRecovery.Status != "Succeeded" || setupRecovery.TruncatedFile is not null ||
                    setupRecovery.View is not { Generation: 0, AValue: 10, BValue: 100 }) {
                    throw new InvalidDataException("Orphan setup changed the published old view or needed a tail action.");
                }
            }
        }
        InventoryFile[] before = UnifiedRootStore.Inventory(fixture);
        int countA = Count(before, "a.rbf"), countB = Count(before, "b.rbf"), countRoot = Count(before, "roots.rbf");
        if (axis == "history" && (countA != scale + 2 || countB != scale + 2 || countRoot != scale + 1) ||
            axis == "orphan" && (countA != scale + 2 || countB != scale + 2 || countRoot != 1)) {
            throw new InvalidDataException("Benchmark history/orphan axis did not produce its expected complete-frame counts.");
        }
        var commit = ProbeMeasurements.Measure(() => UnifiedRootStore.Commit(fixture, 20, 200));
        var open = ProbeMeasurements.Measure(() => UnifiedRootStore.Open(fixture));
        RootView view;
        ProbeMeasurement firstRead;
        using (open.Result) {
            var read = ProbeMeasurements.Measure(open.Result.Read);
            view = read.Result;
            firstRead = read.Measurement;
        }
        if (view != commit.Result) { throw new InvalidDataException("Open/first read differs from committed pair and addresses."); }
        var recovery = MeasureRecovery(fixture);
        if (recovery.Result.Status != "Succeeded" || recovery.Result.TruncatedFile is not null ||
            recovery.Result.View != view) {
            throw new InvalidDataException("Benchmark recovery failed or changed the published pair.");
        }
        return new ProbeBenchmarkSample(axis, scale, sample, sample < 0, fixture,
            countA, countB, countRoot, commit.Result, view,
            commit.Measurement, open.Measurement, firstRead,
            Sum(open.Measurement, firstRead), recovery.Measurement, recovery.Phases,
            recovery.Result.Status, recovery.Result.TruncatedFile);
    }

    private static ProbeRecoveryActionSample RunActionSample(string parent, string shortFile, int sample) {
        string fixture = Path.Combine(parent,
            $"action-{shortFile}-{(sample < 0 ? "warmup" : sample.ToString())}-{Guid.NewGuid():N}");
        UnifiedRootStore.Seed(fixture, 10, 100);
        string target = Path.GetFullPath(Path.Combine(fixture, shortFile));
        RbfWriteHooks? previous = RbfWriteInstrumentation.Current;
        bool stopped = false;
        try {
            RbfWriteInstrumentation.Current = new RbfWriteHooks {
                BeforeWrite = request => request.Path == target ? 2 : request.RequestedBytes
            };
            try { UnifiedRootStore.Commit(fixture, 20, 200); }
            catch (IOException error) when (error.Message.Contains("partial RBF write", StringComparison.Ordinal)) {
                stopped = true;
            }
        }
        finally { RbfWriteInstrumentation.Current = previous; }
        if (!stopped) { throw new InvalidDataException("Real 2-byte write hook did not stop the expected append."); }
        InventoryFile[] before = UnifiedRootStore.Inventory(fixture);
        InventoryFile targetBefore = before.Single(file => file.Path == shortFile);
        if (targetBefore.Length != targetBefore.CompleteLength + 2 ||
            before.Count(file => file.Length > file.CompleteLength) != 1) {
            throw new InvalidDataException("Action fixture did not contain exactly one real 2-byte short suffix.");
        }
        var recovery = MeasureRecovery(fixture);
        if (recovery.Result.Status != "Succeeded" || recovery.Result.TruncatedFile != shortFile ||
            recovery.Result.View is not { Generation: 0, AValue: 10, BValue: 100 }) {
            throw new InvalidDataException("Short-tail recovery failed or delivered the wrong old pair.");
        }
        InventoryFile[] after = UnifiedRootStore.Inventory(fixture);
        foreach (InventoryFile file in before) {
            InventoryFile observed = after.Single(candidate => candidate.Path == file.Path);
            if (observed.CompleteLength < file.CompleteLength ||
                (observed.CompleteLength == file.CompleteLength && observed.Sha256 != file.Sha256) ||
                !observed.Offsets.Take(file.Offsets.Length).SequenceEqual(file.Offsets)) {
                throw new InvalidDataException("Action changed a complete prefix or record address.");
            }
        }
        RootView delivered = UnifiedRootStore.Inspect(fixture);
        RootView continued = UnifiedRootStore.Commit(fixture, 30, 300);
        if (delivered != recovery.Result.View || UnifiedRootStore.Inspect(fixture) != continued ||
            continued is not { Generation: 1, AValue: 30, BValue: 300 }) {
            throw new InvalidDataException("Action sample cannot continue and cold-read the next pair.");
        }
        return new ProbeRecoveryActionSample(shortFile, sample, sample < 0, fixture,
            before, recovery.Measurement, recovery.Phases, delivered, continued);
    }

    private static (RecoveryResult Result, ProbeMeasurement Measurement, ProbeRecoveryPhases Phases)
        MeasureRecovery(string fixture) {
        ProbeMeasurementCheckpoint? start = null;
        var points = new Dictionary<string, ProbeMeasurementCheckpoint>(StringComparer.Ordinal);
        var measured = ProbeMeasurements.Measure(() => {
            start = ProbeMeasurements.Capture();
            return UnifiedRootStore.Recover(fixture, checkpoint: point => {
                if (point is "CandidateResourcesDisposed" or "AfterAction" or "BeforeDelivery") {
                    points.Add(point, ProbeMeasurements.Capture());
                }
            });
        });
        ProbeMeasurementCheckpoint first = points["CandidateResourcesDisposed"];
        ProbeMeasurementCheckpoint action = points["AfterAction"];
        ProbeMeasurementCheckpoint confirmation = points["BeforeDelivery"];
        return (measured.Result, measured.Measurement,
            new ProbeRecoveryPhases(
                ProbeMeasurements.Difference(start!, first),
                ProbeMeasurements.Difference(first, action),
                ProbeMeasurements.Difference(action, confirmation)));
    }

    private static int Count(InventoryFile[] files, string path) =>
        files.Single(file => file.Path == path).Offsets.Length;

    // Both stages are non-overlapping. Do not sum a nested outer scope with a
    // child: direct counters propagate to parents while RBF scopes do not.
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
