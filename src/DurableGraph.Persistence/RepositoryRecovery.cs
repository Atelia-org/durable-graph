using System.Diagnostics;
using Atelia.DurableGraph.Schema;
using Atelia.Rbf;
using Atelia.Rbf.Internal;

namespace Atelia.DurableGraph.Persistence;

internal enum RepositoryRecoveryStatus {
    Succeeded, QualificationRefused, PrecheckRefused, ActionFailed, StrictOpenFailed, DeliveryFailed
}

internal sealed class RepositoryRecoveryReport {
    public RepositoryRecoveryStatus Status { get; internal set; } = RepositoryRecoveryStatus.PrecheckRefused;
    public string Detail { get; internal set; } = "Recovery did not complete.";
    public RecoveryQualificationReport? Qualification { get; internal set; }
    public Repository? Repository { get; internal set; }
    public RecoveryQualificationFile? SelectedFile { get; internal set; }
    public bool ActionAttempted { get; internal set; }
    public bool LengthChanged { get; internal set; }
    public bool BarrierCompleted { get; internal set; }
    public bool SourceMayHaveChanged => ActionAttempted;
    public bool Cancelled { get; internal set; }
    public string? CleanupDetail { get; internal set; }
    public RepositoryRecoveryStatus? CleanupPreviousStatus { get; internal set; }
    public bool CancellationAfterMutation => Cancelled && SourceMayHaveChanged;
    public bool NoAction => SelectedFile is null && Status == RepositoryRecoveryStatus.Succeeded;
    public RecoveryQualificationMetrics Action { get; internal set; }
    public RecoveryQualificationMetrics StrictOpen { get; internal set; }
    public RecoveryQualificationMetrics Confirmation { get; internal set; }
    public RecoveryQualificationMetrics Total { get; internal set; }
    public string BudgetScope => "The request-byte budget covers qualification RBF reads only. Action boundary reinspection, strict reopen, confirmation, metadata and directory operations are outside it.";
    public string MeasurementScope => "Action includes inventory/length and selected physical-boundary rechecks plus the one truncation/barrier. StrictOpen includes all fresh resources, validation and confirmation. Confirmation is a nested StrictOpen subset of the actual file barriers and their hooks; never add it again. Total includes qualification, action, strict open, lock, delivery and cleanup. Thread allocation is not peak memory; RBF bytes are not device I/O.";
}

/// <summary>Internal S0-C experiment. Qualification and action are derived under one continuously held lock.</summary>
internal static class RepositoryRecovery {
    private static readonly AsyncLocal<Action<string>?> CheckpointSlot = new();
    internal static Action<string>? Checkpoint { get => CheckpointSlot.Value; set => CheckpointSlot.Value = value; }

    internal static RepositoryRecoveryReport Recover(string path, StateModelRegistry models,
        long readBudget = long.MaxValue, CancellationToken cancellationToken = default) {
        // Validate the caller's environment before acquiring resources or changing files.
        ArgumentNullException.ThrowIfNull(models);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegative(readBudget);
        var report = new RepositoryRecoveryReport();
        var totalWatch = Stopwatch.StartNew();
        long totalAllocated = GC.GetAllocatedBytesForCurrentThread();
        FileStream? repositoryLock = null;
        Repository? fresh = null;
        string root = Path.GetFullPath(path);
        void Point(string name) {
            cancellationToken.ThrowIfCancellationRequested();
            Checkpoint?.Invoke(name);
            cancellationToken.ThrowIfCancellationRequested();
        }
        void Measure(Action operation, Action<RecoveryQualificationMetrics> save,
            Func<RbfReadMetricsSnapshot>? additionalReads = null) {
            using var reads = RbfReadMetrics.Begin();
            var watch = Stopwatch.StartNew();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            try { operation(); }
            finally {
                var snapshot = reads.Snapshot();
                if (additionalReads is not null) { snapshot = RepositoryRecoveryQualification.Add(snapshot, additionalReads()); }
                save(new(snapshot, watch.Elapsed, GC.GetAllocatedBytesForCurrentThread() - allocated));
            }
        }
        try {
            cancellationToken.ThrowIfCancellationRequested();
            RepositoryRecoveryQualification.RequireOrdinaryAncestors(root);
            repositoryLock = new FileStream(Path.Combine(root, "repository.lock"), FileMode.Open,
                FileAccess.ReadWrite, FileShare.None);
            string[] inventory = RepositoryRecoveryQualification.Capture(root, cancellationToken);
            var originalLengths = inventory.Where(p => !p.EndsWith('/') && p != "repository.lock")
                .ToDictionary(p => p, p => new FileInfo(Path.Combine(root, p)).Length, StringComparer.Ordinal);
            report.Qualification = RepositoryRecoveryQualification.QualifyBorrowed(root, repositoryLock, readBudget, cancellationToken);
            if (report.Qualification.Status != RecoveryQualificationStatus.Qualified) {
                report.Status = RepositoryRecoveryStatus.QualificationRefused;
                report.Detail = report.Qualification.Detail;
                report.Cancelled = report.Qualification.Status == RecoveryQualificationStatus.Cancelled;
                return report;
            }
            report.SelectedFile = report.Qualification.Files.SingleOrDefault(f => f.PhysicalStatus == RbfPrefixStatus.IncompleteSuffix);
            Point("CandidateResourcesDisposed");
            RbfReadMetricsSnapshot recheckReads = default;
            Measure(() => {
                Point("BeforeAction");
                RepositoryRecoveryQualification.RequireOrdinaryAncestors(root);
                if (!inventory.SequenceEqual(RepositoryRecoveryQualification.Capture(root, cancellationToken))) {
                    throw new IOException("Repository inventory changed after qualification.");
                }
                foreach (var pair in originalLengths) {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (new FileInfo(Path.Combine(root, pair.Key)).Length != pair.Value) {
                        throw new IOException("Repository length changed after qualification: " + pair.Key);
                    }
                }
                foreach (var file in report.Qualification.Files) {
                    if (!originalLengths.TryGetValue(file.RelativePath, out long length) || length != file.OriginalLength) {
                        throw new IOException("Repository length changed before qualification: " + file.RelativePath);
                    }
                }
                if (report.SelectedFile is { } selected) {
                    using var target = new FileStream(Path.Combine(root, selected.RelativePath), FileMode.Open,
                        FileAccess.ReadWrite, FileShare.None, 1);
                    // Reinspect the same exclusive write handle: a stale report cannot choose a
                    // different boundary or hide complete frames behind an earlier EOF.
                    var inspected = RbfPrefixInspector.Inspect(target, long.MaxValue, cancellationToken);
                    recheckReads = new(inspected.ReadRequests, inspected.RequestedBytes, inspected.ReturnedBytes,
                        inspected.ReadRequests, inspected.RequestedBytes, inspected.ReturnedBytes, 0,
                        inspected.ReadRequests > 0 ? 1 : 0);
                    if (inspected.Status != RbfPrefixStatus.IncompleteSuffix || inspected.OriginalLength != selected.OriginalLength ||
                        inspected.LastCompleteBoundary != selected.VisibleEof || target.Length != selected.OriginalLength) {
                        if (inspected.Status == RbfPrefixStatus.Cancelled) { throw new OperationCanceledException(cancellationToken); }
                        throw new IOException("Selected physical suffix changed or no longer qualifies: " + inspected.Detail);
                    }
                    Point("BeforeSetLength");
                    // This flag deliberately precedes SetLength: a throwing syscall can have
                    // an uncertain effect even when no observed length change was returned.
                    report.Status = RepositoryRecoveryStatus.ActionFailed;
                    report.ActionAttempted = true;
                    target.SetLength(selected.VisibleEof);
                    report.LengthChanged = true;
                    Point("AfterSetLengthBeforeFlush");
                    target.Flush(flushToDisk: true);
                    report.BarrierCompleted = true;
                }
                Point("AfterAction");
            }, m => report.Action = m, () => recheckReads);

            report.Status = RepositoryRecoveryStatus.StrictOpenFailed;
            Point("BeforeStrictOpen");
            void ConfirmFile(string filePath, Action barrier) {
                Measure(() => {
                    string relative = Path.GetRelativePath(root, filePath).Replace('\\', '/');
                    Point("BeforeConfirmFile:" + relative);
                    barrier();
                    Point("AfterConfirmFile:" + relative);
                }, m => report.Confirmation = new(
                    RepositoryRecoveryQualification.Add(report.Confirmation.Reads, m.Reads),
                    report.Confirmation.Elapsed + m.Elapsed,
                    report.Confirmation.ThreadAllocatedBytes + m.ThreadAllocatedBytes));
            }
            Measure(() => {
                fresh = Repository.OpenRecoveryBorrowed(root, models, repositoryLock, Point, ConfirmFile);
                Point("AfterStrictOpen");
            }, m => report.StrictOpen = m, () => report.Confirmation.Reads);
            report.Status = RepositoryRecoveryStatus.DeliveryFailed;
            Point("BeforeDelivery");
            // Final inventory/length validation belongs to delivery, after all resources and
            // barriers exist. Unknown new files must not escape the bounded S0-C scope.
            RepositoryRecoveryQualification.RequireOrdinaryAncestors(root);
            if (!inventory.SequenceEqual(RepositoryRecoveryQualification.Capture(root, cancellationToken))) {
                throw new IOException("Repository inventory changed before delivery.");
            }
            foreach (var pair in originalLengths) {
                cancellationToken.ThrowIfCancellationRequested();
                long expected = report.SelectedFile?.RelativePath == pair.Key ? report.SelectedFile.VisibleEof : pair.Value;
                if (new FileInfo(Path.Combine(root, pair.Key)).Length != expected) {
                    throw new IOException("Repository length changed before delivery: " + pair.Key);
                }
            }
            fresh!.AdoptRecoveryLock();
            repositoryLock = null;
            report.Repository = fresh;
            fresh = null;
            report.Status = RepositoryRecoveryStatus.Succeeded;
            report.Detail = report.SelectedFile is null
                ? "No suffix action needed; fresh strict resources and all existing dependency barriers confirmed."
                : "Qualified suffix removed and flushed; fresh strict resources and all existing dependency barriers confirmed.";
        }
        catch (Exception error) {
            report.Detail = error.Message;
            report.Cancelled = error is OperationCanceledException;
        }
        finally {
            void Cleanup(Action dispose) {
                try { dispose(); }
                catch (Exception error) {
                    report.CleanupDetail = report.CleanupDetail is null ? error.Message : report.CleanupDetail + "; " + error.Message;
                    report.CleanupPreviousStatus ??= report.Status;
                    report.Status = RepositoryRecoveryStatus.DeliveryFailed;
                    report.Detail += " Cleanup failed: " + error.Message;
                    report.Cancelled |= error is OperationCanceledException;
                }
            }
            try {
                if (fresh is not null) {
                    Cleanup(() => {
                        fresh.Dispose();
                        Checkpoint?.Invoke("AfterFailedRepositoryClose");
                    });
                }
                if (repositoryLock is not null) {
                    Cleanup(() => {
                        repositoryLock.Dispose();
                        Checkpoint?.Invoke("AfterFailedLockClose");
                    });
                }
            }
            finally {
                var qualificationReads = report.Qualification?.Total.Reads ?? default;
                report.Total = new(RepositoryRecoveryQualification.Add(qualificationReads,
                    RepositoryRecoveryQualification.Add(report.Action.Reads, report.StrictOpen.Reads)),
                    totalWatch.Elapsed, GC.GetAllocatedBytesForCurrentThread() - totalAllocated);
            }
        }
        return report;
    }
}
