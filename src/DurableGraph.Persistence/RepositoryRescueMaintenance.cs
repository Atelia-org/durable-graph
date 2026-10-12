using System.Diagnostics;
using Atelia.Rbf;
using Atelia.Rbf.Internal;

namespace Atelia.DurableGraph.Persistence;

internal enum RepositoryRescueStatus {
    Qualified, Succeeded, QualificationRefused, PrecheckIncomplete, ActionFailed,
    StrictOpenFailed, DeliveryFailed, CloseFailed
}

/// <summary>Detached structural maintenance facts. No model materialization or live resource is retained.</summary>
internal sealed class RepositoryRescueReport {
    public string Mode { get; internal set; } = "check";
    public RepositoryRescueStatus Status { get; internal set; } = RepositoryRescueStatus.PrecheckIncomplete;
    public string Detail { get; internal set; } = "Maintenance did not complete.";
    public RepositoryRecoveryStatus? CoreStatus { get; internal set; }
    public RepositoryRecoveryStatus? CleanupPreviousStatus { get; internal set; }
    public RecoveryQualificationReport? Qualification { get; internal set; }
    public RecoveryQualificationFile? SelectedFile { get; internal set; }
    public bool ActionAttempted { get; internal set; }
    public bool LengthChanged { get; internal set; }
    public bool BarrierCompleted { get; internal set; }
    public bool SourceMayHaveChanged => ActionAttempted;
    public bool Cancelled { get; internal set; }
    public bool CancellationAfterMutation => Cancelled && SourceMayHaveChanged;
    public bool NoAction => Mode == "apply" && Status == RepositoryRescueStatus.Succeeded && SelectedFile is null;
    // Null means cleanup completion is unproven, not that a live handle is known to remain.
    public bool? ResourcesClosed { get; internal set; }
    public string? CleanupDetail { get; internal set; }
    public RecoveryQualificationMetrics Action { get; internal set; }
    public RecoveryQualificationMetrics StrictOpen { get; internal set; }
    public RecoveryQualificationMetrics Confirmation { get; internal set; }
    public RecoveryQualificationMetrics CoreTotal { get; internal set; }
    public RecoveryQualificationMetrics Close { get; internal set; }
    public RecoveryQualificationMetrics Total { get; internal set; }
    public string BudgetScope => "The request-byte budget covers qualification RBF reads only. Metadata, action boundary reinspection, strict reopen, confirmation, CPU, memory and device I/O are outside it.";
    public string MeasurementScope => "CoreTotal is the existing qualification/recovery entry measurement. Total covers the maintenance entry through resource close and report boundary. Close measures delivered resource disposal and its hooks. Confirmation is nested in StrictOpen; never add it again. Output is outside this report. Thread allocation is not peak memory; RBF reads are not device I/O.";
}

/// <summary>Thin internal maintenance ownership adapter over the same S0 qualification and recovery core.</summary>
internal static class RepositoryRescueMaintenance {
    private static readonly AsyncLocal<Action<string>?> CheckpointSlot = new();
    internal static Action<string>? Checkpoint { get => CheckpointSlot.Value; set => CheckpointSlot.Value = value; }

    internal static RepositoryRescueReport Check(string path, long readBudget = long.MaxValue,
        CancellationToken cancellationToken = default) => Run(path, readBudget, cancellationToken, apply: false);

    internal static RepositoryRescueReport Apply(string path, long readBudget = long.MaxValue,
        CancellationToken cancellationToken = default) => Run(path, readBudget, cancellationToken, apply: true);

    private static RepositoryRescueReport Run(string path, long readBudget, CancellationToken token, bool apply) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegative(readBudget);
        var report = new RepositoryRescueReport { Mode = apply ? "apply" : "check" };
        var watch = Stopwatch.StartNew();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        Repository? owned = null;
        void Point(string name) {
            // A cancellation already reported by the core must keep its original stage.
            if (!report.Cancelled) { token.ThrowIfCancellationRequested(); }
            Checkpoint?.Invoke(name);
            if (!report.Cancelled) { token.ThrowIfCancellationRequested(); }
        }
        void Fail(Exception error, RepositoryRescueStatus status, bool cleanup = false) {
            report.Status = status;
            report.Detail += " " + (cleanup ? "Close boundary failed: " : "Report boundary failed: ") + error.Message;
            report.Cancelled |= error is OperationCanceledException;
            if (cleanup) { report.CleanupDetail = report.CleanupDetail is null ? error.Message : report.CleanupDetail + "; " + error.Message; }
        }
        try {
            if (apply) {
                var core = RepositoryRecovery.Recover(path, new StateModelRegistry(), readBudget, token);
                // Take ownership before copying facts so every later failure still reaches disposal.
                owned = core.Repository;
                core.Repository = null;
                report.CoreStatus = core.Status;
                report.CleanupPreviousStatus = core.CleanupPreviousStatus;
                report.Detail = core.Detail;
                report.Qualification = core.Qualification;
                report.SelectedFile = core.SelectedFile;
                report.ActionAttempted = core.ActionAttempted;
                report.LengthChanged = core.LengthChanged;
                report.BarrierCompleted = core.BarrierCompleted;
                report.Cancelled = core.Cancelled;
                report.CleanupDetail = core.CleanupDetail;
                report.Action = core.Action;
                report.StrictOpen = core.StrictOpen;
                report.Confirmation = core.Confirmation;
                report.CoreTotal = core.Total;
                report.Status = core.Status switch {
                    RepositoryRecoveryStatus.Succeeded => RepositoryRescueStatus.Succeeded,
                    RepositoryRecoveryStatus.QualificationRefused => QualificationStatus(core.Qualification?.Status),
                    RepositoryRecoveryStatus.ActionFailed => RepositoryRescueStatus.ActionFailed,
                    RepositoryRecoveryStatus.StrictOpenFailed => RepositoryRescueStatus.StrictOpenFailed,
                    RepositoryRecoveryStatus.DeliveryFailed => RepositoryRescueStatus.DeliveryFailed,
                    _ => RepositoryRescueStatus.PrecheckIncomplete
                };
            } else {
                var qualification = RepositoryRecoveryQualification.Qualify(path, readBudget, token);
                report.Qualification = qualification;
                report.SelectedFile = qualification.Status == RecoveryQualificationStatus.Qualified
                    ? qualification.Files.SingleOrDefault(f => f.PhysicalStatus == RbfPrefixStatus.IncompleteSuffix)
                    : null;
                report.ResourcesClosed = true;
                report.Detail = qualification.Detail;
                report.Cancelled = qualification.Status == RecoveryQualificationStatus.Cancelled;
                report.CoreTotal = qualification.Total;
                report.Status = QualificationStatus(qualification.Status);
            }
        }
        catch (Exception error) {
            report.Detail = error.Message;
            report.Cancelled |= error is OperationCanceledException;
            report.Status = RepositoryRescueStatus.PrecheckIncomplete;
        }
        finally {
            // Observers and cancellation can fail this boundary, but never skip actual cleanup.
            using var reads = RbfReadMetrics.Begin();
            var closeWatch = Stopwatch.StartNew();
            long closeAllocated = GC.GetAllocatedBytesForCurrentThread();
            try { Point("BeforeClose"); }
            catch (Exception error) { Fail(error, apply ? RepositoryRescueStatus.CloseFailed : RepositoryRescueStatus.PrecheckIncomplete, cleanup: true); }
            try {
                owned?.Dispose();
                if (owned is not null) { report.ResourcesClosed = true; }
            }
            catch (Exception error) {
                report.ResourcesClosed = null;
                Fail(error, apply ? RepositoryRescueStatus.CloseFailed : RepositoryRescueStatus.PrecheckIncomplete, cleanup: true);
            }
            finally { owned = null; }
            try { Point("AfterClose"); }
            catch (Exception error) { Fail(error, apply ? RepositoryRescueStatus.CloseFailed : RepositoryRescueStatus.PrecheckIncomplete, cleanup: true); }
            report.Close = new(reads.Snapshot(), closeWatch.Elapsed, GC.GetAllocatedBytesForCurrentThread() - closeAllocated);
        }
        try { Point("BeforeReport"); }
        catch (Exception error) { Fail(error, apply ? RepositoryRescueStatus.DeliveryFailed : RepositoryRescueStatus.PrecheckIncomplete); }
        report.Total = new(RepositoryRecoveryQualification.Add(report.CoreTotal.Reads, report.Close.Reads),
            watch.Elapsed, GC.GetAllocatedBytesForCurrentThread() - allocated);
        return report;
    }

    private static RepositoryRescueStatus QualificationStatus(RecoveryQualificationStatus? status) => status switch {
        RecoveryQualificationStatus.Qualified => RepositoryRescueStatus.Qualified,
        RecoveryQualificationStatus.Invalid or RecoveryQualificationStatus.Unsupported or RecoveryQualificationStatus.Unresolved => RepositoryRescueStatus.QualificationRefused,
        _ => RepositoryRescueStatus.PrecheckIncomplete
    };
}
