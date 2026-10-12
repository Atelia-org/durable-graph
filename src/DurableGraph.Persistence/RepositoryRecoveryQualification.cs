using System.Diagnostics;
using Atelia.DurableGraph.Storage;
using Atelia.EventJournal;
using Atelia.Rbf;
using Atelia.Rbf.Internal;
using Atelia.RbfSegmentStore;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;
using FrameAddress = Atelia.DurableGraph.Storage.FrameAddress;

namespace Atelia.DurableGraph.Persistence;

internal enum RecoveryQualificationStatus { Qualified, Invalid, Unsupported, Unresolved, BudgetExceeded, Cancelled, IoFailure }
internal sealed record RecoveryQualificationFile(string RelativePath, long OriginalLength, long VisibleEof, RbfPrefixStatus PhysicalStatus);
internal readonly record struct RecoveryQualificationMetrics(RbfReadMetricsSnapshot Reads, TimeSpan Elapsed, long ThreadAllocatedBytes);
internal sealed class RecoveryQualificationReport {
    public RecoveryQualificationStatus Status { get; internal set; } = RecoveryQualificationStatus.Invalid;
    public string Detail { get; internal set; } = "Qualification did not complete.";
    public IReadOnlyList<RecoveryQualificationFile> Files => MutableFiles.AsReadOnly();
    internal List<RecoveryQualificationFile> MutableFiles { get; } = [];
    public RecoveryQualificationMetrics Physical { get; internal set; }
    public RecoveryQualificationMetrics Candidate { get; internal set; }
    public RecoveryQualificationMetrics Dependencies { get; internal set; }
    public RecoveryQualificationMetrics Total { get; internal set; }
    public int StateFrames { get; internal set; }
    public int JournalEvents { get; internal set; }
    public string BudgetScope => "RBF header/raw requested bytes only, including repeated reads. Directory and format/locator/snapshot metadata reads, CPU, allocations and device I/O are not bounded by this budget.";
    public string MeasurementScope => "Physical is continuous raw prefix inspection; Candidate is fresh Schema replay and State membership inventory/construction; Dependencies includes Journal fact/ref audit and all DG State/graph dependency validation. Total includes lock, metadata inventory and resource teardown beyond the three stages. Thread allocation is not peak memory; returned bytes are not device I/O.";
}

/// <summary>Lock-local, read-only evidence. No resources or action plan escape this call.</summary>
internal static class RepositoryRecoveryQualification {
    private static readonly AsyncLocal<Action<string>?> CheckpointSlot = new();
    internal static Action<string>? Checkpoint { get => CheckpointSlot.Value; set => CheckpointSlot.Value = value; }
    private sealed class Stop(RecoveryQualificationStatus status, string message) : Exception(message) {
        internal RecoveryQualificationStatus Status { get; } = status;
    }

    internal static RecoveryQualificationReport Qualify(string path, long readBudget = long.MaxValue, CancellationToken cancellationToken = default) =>
        QualifyCore(path, readBudget, cancellationToken, null);

    internal static RecoveryQualificationReport QualifyBorrowed(string path, FileStream repositoryLock, long readBudget, CancellationToken cancellationToken) =>
        QualifyCore(path, readBudget, cancellationToken, repositoryLock);

    private static RecoveryQualificationReport QualifyCore(string path, long readBudget, CancellationToken cancellationToken, FileStream? borrowedLock) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegative(readBudget);
        var report = new RecoveryQualificationReport();
        long remaining = readBudget;
        var totalWatch = Stopwatch.StartNew();
        long totalAlloc = GC.GetAllocatedBytesForCurrentThread();
        var guards = new List<FileStream>();
        var readers = new List<IRbfFile>();
        SegmentStore? segments = null;
        StateRevisionStore? states = null;
        FileStream? repositoryLock = null;
        RbfReadMetricsSnapshot physicalReads = default;
        void BeforeRead(int count) {
            cancellationToken.ThrowIfCancellationRequested();
            if (count > remaining) { throw new Stop(RecoveryQualificationStatus.BudgetExceeded, "Shared RBF request budget exhausted."); }
            remaining -= count;
        }
        void Stage(Action action, Action<RecoveryQualificationMetrics> save, bool physical = false) {
            using var metrics = RbfReadMetrics.Begin();
            var watch = Stopwatch.StartNew();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            try { action(); }
            finally { save(new(physical ? physicalReads : metrics.Snapshot(), watch.Elapsed, GC.GetAllocatedBytesForCurrentThread() - allocated)); }
        }
        try {
            cancellationToken.ThrowIfCancellationRequested();
            string root = Path.GetFullPath(path);
            RequireOrdinaryAncestors(root);
            repositoryLock = borrowedLock ?? new FileStream(Path.Combine(root, "repository.lock"), FileMode.Open, FileAccess.Read, FileShare.None);
            string[] inventory = Capture(root, cancellationToken);
            foreach (string relative in inventory.Where(p => !p.EndsWith('/') && p != "repository.lock")) {
                guards.Add(new FileStream(Path.Combine(root, relative), FileMode.Open, FileAccess.Read, FileShare.Read, 1));
            }
            var stateInventory = InspectStore(Path.Combine(root, "state"), cancellationToken);
            var eventInventory = InspectStore(Path.Combine(root, "journal", "events"), cancellationToken);
            string[] refRoots = Directory.GetDirectories(Path.Combine(root, "journal", "refs", "objects"));
            if (refRoots.Length != 1) { throw new Stop(RecoveryQualificationStatus.Unsupported, "S0-B requires exactly one initialized ref object."); }
            var refInventory = InspectStore(refRoots[0], cancellationToken);
            string statePath = "state/" + stateInventory.Segments[0].RelativePath;
            string eventPath = "journal/events/" + eventInventory.Segments[0].RelativePath;
            string refRelative = Path.GetRelativePath(root, refRoots[0]).Replace('\\', '/');
            var allowedFiles = new HashSet<string>(StringComparer.Ordinal) { "repository.lock", "schemas.rbf", "state/active.segment", "journal/journal.format", "journal/events/active.segment", "journal/refs/catalog.snapshot", "journal/refs/ref-op-log.rbf", refRelative + "/active.segment", statePath, eventPath, refRelative + "/" + refInventory.Segments[0].RelativePath };
            var allowedDirectories = new HashSet<string>(StringComparer.Ordinal);
            foreach (string file in allowedFiles) {
                for (string? parent = Path.GetDirectoryName(file)?.Replace('\\', '/'); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent)?.Replace('\\', '/')) { allowedDirectories.Add(parent + "/"); }
            }
            if (inventory.Any(p => !allowedFiles.Contains(p) && !allowedDirectories.Contains(p)) || allowedFiles.Any(p => !inventory.Contains(p))) {
                throw new Stop(RecoveryQualificationStatus.Unsupported, "Unknown files/directories or incomplete initialization are outside S0-B.");
            }
            var eofs = new Dictionary<string, long>(StringComparer.Ordinal);
            Stage(() => {
                foreach (string relative in allowedFiles.Where(p => p.EndsWith(".rbf", StringComparison.Ordinal)).Order(StringComparer.Ordinal)) {
                    var inspected = RbfPrefixInspector.Inspect(Path.Combine(root, relative), remaining, cancellationToken);
                    remaining -= inspected.RequestedBytes;
                    physicalReads = Add(physicalReads, new(inspected.ReadRequests, inspected.RequestedBytes, inspected.ReturnedBytes,
                        inspected.ReadRequests, inspected.RequestedBytes, inspected.ReturnedBytes, 0, inspected.OriginalLength >= 0 && inspected.ReadRequests > 0 ? 1 : 0));
                    report.MutableFiles.Add(new(relative, inspected.OriginalLength, inspected.LastCompleteBoundary, inspected.Status));
                    if (inspected.Status is not (RbfPrefixStatus.Complete or RbfPrefixStatus.IncompleteSuffix)) {
                        throw new Stop(inspected.Status switch {
                            RbfPrefixStatus.Cancelled => RecoveryQualificationStatus.Cancelled,
                            RbfPrefixStatus.BudgetExceeded => RecoveryQualificationStatus.BudgetExceeded,
                            RbfPrefixStatus.IoFailure => RecoveryQualificationStatus.IoFailure,
                            RbfPrefixStatus.Unresolved => RecoveryQualificationStatus.Unresolved,
                            RbfPrefixStatus.IncompleteHeader => RecoveryQualificationStatus.Unsupported,
                            _ => RecoveryQualificationStatus.Invalid
                        }, relative + ": " + inspected.Detail);
                    }
                    if (inspected.LastCompleteBoundary < 4 || relative == "journal/refs/ref-op-log.rbf" && inspected.Status != RbfPrefixStatus.Complete) {
                        throw new Stop(RecoveryQualificationStatus.Unsupported, "Ref catalog initialization/log must be complete.");
                    }
                    eofs.Add(Path.GetFullPath(Path.Combine(root, relative)), inspected.LastCompleteBoundary);
                }
            }, m => report.Physical = m, physical: true);
            if (report.Files.Count(f => f.PhysicalStatus == RbfPrefixStatus.IncompleteSuffix) > 1) { throw new Stop(RecoveryQualificationStatus.Unsupported, "Multiple RBF short suffixes are outside the single serial-writer S0-B scope."); }
            Checkpoint?.Invoke("PhysicalCompleted");
            IRbfFile Open(string filePath) {
                string full = Path.GetFullPath(filePath);
                if (!eofs.TryGetValue(full, out long eof)) { throw new Stop(RecoveryQualificationStatus.Unsupported, "Uninventoried candidate file."); }
                return RbfFile.OpenReadOnlyCandidate(full, eof, RbfCacheMode.Off, BeforeRead, cancellationToken);
            }
            SchemaStore? schemas = null;
            CandidateJournalAuditResult? journalAudit = null;
            var physicalStates = new HashSet<FrameAddress>();
            Stage(() => {
                var schemaFile = Open(Path.Combine(root, "schemas.rbf")); readers.Add(schemaFile);
                schemas = new SchemaStore(schemaFile, true, cancellationToken);
                var stateFile = Open(Path.Combine(root, statePath)); readers.Add(stateFile);
                Enumerate(stateFile, info => {
                    using var frame = stateFile.ReadPooledFrame(info.Ticket).Unwrap();
                    if (frame.IsTombstone || frame.TailMetaLength != 0 || frame.Tag != StateRevisionStore.RbfTag) { throw new InvalidDataException("Unexpected physical State frame."); }
                    if (frame.PayloadAndMeta.Length > 0 && frame.PayloadAndMeta[0] != StateRevisionStore.WireVersion) { throw AuditFormat.Unsupported("Unsupported State wire version."); }
                    physicalStates.Add(new(1, info.Ticket));
                }, cancellationToken);
                segments = SegmentStore.OpenReadOnlyCandidate(Path.Combine(root, "state"), stateInventory.Layout!.Value, stateFile);
                readers.Remove(stateFile); // The concrete Store now owns this fresh candidate.
                states = new StateRevisionStore(segments, readCacheBudgetBytes: 0);
                report.StateFrames = physicalStates.Count;
            }, m => report.Candidate = m);
            Checkpoint?.Invoke("CandidateConstructed");
            Stage(() => {
                Checkpoint?.Invoke("BeforeDependencies");
                journalAudit = EventJournalAudit.AuditCandidate(Path.Combine(root, "journal"), Open, cancellationToken);
                var failure = journalAudit.Report.Findings.FirstOrDefault(f => f.Kind != JournalAuditFindingKind.Warning);
                if (failure is not null) {
                    throw new Stop(failure.Kind switch {
                        JournalAuditFindingKind.Cancelled => RecoveryQualificationStatus.Cancelled,
                        JournalAuditFindingKind.IoFailure => RecoveryQualificationStatus.IoFailure,
                        JournalAuditFindingKind.Unsupported => RecoveryQualificationStatus.Unsupported,
                        _ => RecoveryQualificationStatus.Invalid
                    }, "Journal." + failure.Code + ": " + failure.RelativePath);
                }
                if (!journalAudit.Report.Completed) { throw new Stop(RecoveryQualificationStatus.Unresolved, "Journal audit did not complete."); }
                if (!journalAudit.ScopeSupported) { throw new Stop(RecoveryQualificationStatus.Unsupported, string.Join("; ", journalAudit.UnsupportedReasons)); }
                foreach (var address in physicalStates) {
                    cancellationToken.ThrowIfCancellationRequested();
                    RepositoryGraphValidator.ValidateRevisionDependencies(states!, schemas, address, edge => {
                        if (!physicalStates.Contains(edge) || edge.FileNumber != 1 || edge.FrameTicket.Offset >= address.FrameTicket.Offset) { throw new InvalidDataException("State edge is absent from preceding complete physical frames."); }
                    }, cancellationToken);
                }
                using var events = Open(Path.Combine(root, eventPath));
                var nearest = new Dictionary<EventAddress, HistoryGraphRecord?>();
                Enumerate(events, info => {
                    using var frame = EventJournalAudit.ReadCandidateEvent(events, 1, info.Ticket).Unwrap();
                    var record = HistoryJournal.Decode(frame.Address, frame);
                    var baseline = RepositoryGraphValidator.FindBaseline(record, nearest);
                    if (!physicalStates.Contains(record.RevisionAddress)) { throw new InvalidDataException("Graph Revision is absent from complete physical State frames."); }
                    RepositoryGraphValidator.ValidateGraph(states!, schemas!, record, baseline?.RevisionAddress, cancellationToken);
                    nearest.Add(record.Address, record.Kind == GraphFrameKind.State ? record : baseline);
                    report.JournalEvents++;
                }, cancellationToken);
                foreach (var head in journalAudit!.BranchHeads.Values) { RepositoryGraphValidator.RequireBranchMembership(RepositoryGraphValidator.RequireBranchHead(head), nearest.ContainsKey); }
                cancellationToken.ThrowIfCancellationRequested();
                if (!inventory.SequenceEqual(Capture(root, cancellationToken))) { throw new Stop(RecoveryQualificationStatus.IoFailure, "Repository directory inventory changed during qualification."); }
            }, m => report.Dependencies = m);
            Checkpoint?.Invoke("Completed");
            cancellationToken.ThrowIfCancellationRequested();
            report.Status = RecoveryQualificationStatus.Qualified;
            report.Detail = "All retained physical facts and dependencies qualified in the locked read-only view; no recovery action is authorized.";
        }
        catch (Stop error) { report.Status = error.Status; report.Detail = error.Message; }
        catch (OperationCanceledException error) { report.Status = RecoveryQualificationStatus.Cancelled; report.Detail = error.Message; }
        catch (StorageOpenException error) { report.Status = error.Kind == StorageOpenErrorKind.FormatUnsupported ? RecoveryQualificationStatus.Unsupported : RecoveryQualificationStatus.Invalid; report.Detail = error.Message; }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or InvalidOperationException or AteliaException) {
            report.Status = error.Data.Contains("DurableGraph.UnsupportedFormat") ? RecoveryQualificationStatus.Unsupported : RecoveryQualificationStatus.Invalid;
            report.Detail = error.Message;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { report.Status = RecoveryQualificationStatus.IoFailure; report.Detail = error.Message; }
        finally {
            try { states?.Dispose(); segments?.Dispose(); }
            finally {
                try { foreach (var reader in readers) { reader.Dispose(); } }
                finally {
                    try { foreach (var guard in guards) { guard.Dispose(); } }
                    finally { if (borrowedLock is null) { repositoryLock?.Dispose(); } }
                }
            }
            report.Total = new(Add(Add(report.Physical.Reads, report.Candidate.Reads), report.Dependencies.Reads), totalWatch.Elapsed, GC.GetAllocatedBytesForCurrentThread() - totalAlloc);
        }
        return report;
    }

    private static void Enumerate(IRbfFile file, Action<RbfFrameInfo> visit, CancellationToken token) {
        var scan = file.ScanForward(showTombstone: true).GetEnumerator();
        while (scan.MoveNext()) { token.ThrowIfCancellationRequested(); visit(scan.Current); }
        if (scan.TerminationError is { } error) { throw new InvalidDataException(error.Message); }
    }
    private static SegmentStoreAuditReport InspectStore(string path, CancellationToken token) {
        var inventory = SegmentStoreAudit.Inspect(path, token);
        if (inventory.Findings.Any(f => f.IsCancelled)) { throw new OperationCanceledException(token); }
        if (inventory.Findings.Any(f => f.IsIoFailure)) { throw new Stop(RecoveryQualificationStatus.IoFailure, "Store metadata I/O failed."); }
        if (!inventory.IsHealthy) { throw new Stop(inventory.Findings.Any(f => f.IsUnsupported) ? RecoveryQualificationStatus.Unsupported : RecoveryQualificationStatus.Invalid, "Invalid store inventory/locator: " + path); }
        if (inventory.Segments.Count != 1 || inventory.Segments[0].Number != 1) { throw new Stop(RecoveryQualificationStatus.Unsupported, "S0-B supports only initialized first segments."); }
        return inventory;
    }
    internal static void RequireOrdinaryAncestors(string path) {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current)) {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) { throw new Stop(RecoveryQualificationStatus.Unsupported, "Reparse paths are outside S0-B."); }
        }
    }
    internal static string[] Capture(string root, CancellationToken token) {
        var paths = new List<string>();
        void Walk(string directory) {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory)) {
                token.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) { throw new Stop(RecoveryQualificationStatus.Unsupported, "Reparse entries are outside S0-B."); }
                bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                paths.Add(Path.GetRelativePath(root, entry).Replace('\\', '/') + (isDirectory ? "/" : ""));
                if (isDirectory) { Walk(entry); }
            }
        }
        Walk(root);
        return paths.Order(StringComparer.Ordinal).ToArray();
    }
    internal static RbfReadMetricsSnapshot Add(RbfReadMetricsSnapshot a, RbfReadMetricsSnapshot b) => new(
        a.ReadCalls + b.ReadCalls, a.RequestedBytes + b.RequestedBytes, a.ReturnedBytes + b.ReturnedBytes,
        a.RawReadCalls + b.RawReadCalls, a.RawRequestedBytes + b.RawRequestedBytes, a.RawReturnedBytes + b.RawReturnedBytes,
        a.ReadAheadReturnedBytes + b.ReadAheadReturnedBytes, a.HeaderReadCalls + b.HeaderReadCalls);
}
