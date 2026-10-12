using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Storage;
using Atelia.EventJournal;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using JournalStore = Atelia.EventJournal.EventJournal;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;
using FrameAddress = Atelia.DurableGraph.Storage.FrameAddress;

namespace Atelia.DurableGraph;

/// <summary>Offline maintenance operations. Audit holds the existing repository lock and never modifies persisted bytes.</summary>
public static class RepositoryMaintenance {
    private static readonly AsyncLocal<Action<string>?> CheckpointSlot = new();
    // Deterministic tests interrupt dependency stages without changing the public maintenance contract.
    internal static Action<string>? AuditCheckpoint { get => CheckpointSlot.Value; set => CheckpointSlot.Value = value; }

    public static RepositoryAuditReport Audit(string path, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string root = Path.GetFullPath(path);
        var report = new RepositoryAuditReport();
        void Add(RepositoryAuditFindingKind kind, string code, string relative, long? offset, string message) {
            report.MutableFindings.Add(new(kind, code, relative.Replace('\\', '/'), offset, message));
            if (kind is RepositoryAuditFindingKind.UnsupportedFormat or RepositoryAuditFindingKind.IoFailure) { report.Completion = RepositoryAuditCompletion.Incomplete; }
        }
        bool Run(string code, string relative, long? offset, Action action) {
            try { cancellationToken.ThrowIfCancellationRequested(); action(); return true; }
            catch (OperationCanceledException) { throw; }
            catch (StorageOpenException error) {
                Add(error.Kind == StorageOpenErrorKind.FormatUnsupported ? RepositoryAuditFindingKind.UnsupportedFormat : RepositoryAuditFindingKind.InvalidData,
                    error.ReasonCode, Path.GetRelativePath(root, error.StoragePath), error.Offset ?? offset, error.Message);
                report.Completion = RepositoryAuditCompletion.Incomplete;
                return false;
            }
            catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) {
                bool unsupported = error.Data.Contains("DurableGraph.UnsupportedFormat");
                var kind = unsupported ? RepositoryAuditFindingKind.UnsupportedFormat :
                    error is InvalidDataException or EndOfStreamException or ArgumentException or InvalidOperationException ? RepositoryAuditFindingKind.InvalidData : RepositoryAuditFindingKind.IoFailure;
                Add(kind, unsupported ? "UnsupportedFormat" : code, relative, offset ?? error.Data["DurableGraph.Offset"] as long?, error.Message);
                return false;
            }
        }
        try {
            cancellationToken.ThrowIfCancellationRequested();
            FileStream? repositoryLock = null;
            if (!Run("RepositoryLockUnavailable", "repository.lock", null, () => repositoryLock = new FileStream(
                Path.Combine(root, "repository.lock"), FileMode.Open, FileAccess.Read, FileShare.None))) { return report; }
            using (repositoryLock) {
                JournalAuditReport? journalReport = null;
                Run("JournalAuditFailed", "journal", null, () => journalReport = EventJournalAudit.Audit(Path.Combine(root, "journal"), cancellationToken));
                if (journalReport is not null) {
                    foreach (AuditFinding finding in journalReport.Findings) {
                        if (finding.Kind == JournalAuditFindingKind.Cancelled) { throw new OperationCanceledException(cancellationToken); }
                        Add(finding.Kind switch {
                            JournalAuditFindingKind.Unsupported => RepositoryAuditFindingKind.UnsupportedFormat,
                            JournalAuditFindingKind.IoFailure => RepositoryAuditFindingKind.IoFailure,
                            JournalAuditFindingKind.Warning => RepositoryAuditFindingKind.Warning,
                            _ => RepositoryAuditFindingKind.InvalidData,
                        }, "Journal." + finding.Code, "journal/" + finding.RelativePath, finding.Offset, finding.Code);
                    }
                    if (!journalReport.Completed) { report.Completion = RepositoryAuditCompletion.Incomplete; }
                }
                IRbfFile? schemaFile = null;
                SchemaStore? schemas = null;
                try {
                    Run("SchemaInvalid", "schemas.rbf", null, () => {
                        schemaFile = RbfFile.OpenReadOnlyExisting(Path.Combine(root, "schemas.rbf"));
                        schemas = new SchemaStore(schemaFile, readOnly: true, cancellationToken);
                    });
                    var inventory = SegmentStoreAudit.Inspect(Path.Combine(root, "state"), cancellationToken);
                    foreach (var finding in inventory.Findings) {
                        if (finding.IsCancelled) { throw new OperationCanceledException(cancellationToken); }
                        Add(finding.IsUnsupported ? RepositoryAuditFindingKind.UnsupportedFormat : finding.IsIoFailure ? RepositoryAuditFindingKind.IoFailure : RepositoryAuditFindingKind.InvalidData,
                            "State." + finding.Code, "state/" + finding.RelativePath, finding.Offset, finding.Code);
                    }
                    if (!inventory.Completed) { report.Completion = RepositoryAuditCompletion.Incomplete; }
                    var physical = new Dictionary<FrameAddress, string>();
                    bool framingComplete = true;
                    foreach (var segment in inventory.Segments) {
                        string relative = "state/" + segment.RelativePath;
                        framingComplete &= Run("StateFramingInvalid", relative, null, () => {
                            using var file = RbfFile.OpenReadOnlyExisting(Path.Combine(root, relative));
                            var frames = file.ScanForward(showTombstone: true).GetEnumerator();
                            long? nextFrameOffset = null;
                            while (frames.MoveNext()) {
                                cancellationToken.ThrowIfCancellationRequested();
                                var info = frames.Current;
                                Run("StateFrameInvalid", relative, info.Ticket.Offset, () => {
                                    using var frame = file.ReadPooledFrame(info.Ticket).Unwrap();
                                    if (frame.IsTombstone || frame.TailMetaLength != 0 || frame.Tag != StateRevisionStore.RbfTag) { throw new InvalidDataException("Unexpected State frame."); }
                                    if (frame.PayloadAndMeta.Length > 0 && frame.PayloadAndMeta[0] != StateRevisionStore.WireVersion) { throw AuditFormat.Unsupported("Unsupported State wire version."); }
                                    physical.Add(new(segment.Number, info.Ticket), relative);
                                });
                                nextFrameOffset = file.GetPhysicalOffsetImmediatelyAfter(info.Ticket);
                            }
                            if (frames.TerminationError is { } error) {
                                var framingError = new InvalidDataException(error.Message);
                                if (nextFrameOffset is { } offset) { framingError.Data["DurableGraph.Offset"] = offset; }
                                throw framingError;
                            }
                        });
                    }
                    if (!framingComplete) { report.Completion = RepositoryAuditCompletion.Incomplete; }
                    report.StateFrames = physical.Count;
                    SegmentStore? segments = null;
                    try {
                        if (!Run("StateOpenFailed", "state", null, () => segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(root, "state")))) {
                            report.Completion = RepositoryAuditCompletion.Incomplete;
                            return report;
                        }
                        using var states = new StateRevisionStore(segments!, readCacheBudgetBytes: 0);
                        foreach (var pair in physical) {
                            Run("StateDependencyInvalid", pair.Value, pair.Key.FrameTicket.Offset, () => {
                                void RequireEdge(FrameAddress edge) {
                                    if (!physical.ContainsKey(edge) || edge.FileNumber > pair.Key.FileNumber ||
                                        edge.FileNumber == pair.Key.FileNumber && edge.FrameTicket.Offset >= pair.Key.FrameTicket.Offset) {
                                        throw new InvalidDataException("State edge is absent from preceding complete physical frames.");
                                    }
                                }
                                RepositoryGraphValidator.ValidateRevisionDependencies(states, schemas, pair.Key, RequireEdge, cancellationToken);
                            });
                        }
                        if (schemas is null || journalReport?.IsHealthy != true) {
                            report.Completion = RepositoryAuditCompletion.Incomplete;
                            Add(RepositoryAuditFindingKind.Warning, "GraphAuditSkipped", ".", null, "Graph audit requires a readable Schema catalog and healthy Journal audit.");
                        }
                        else {
                            if (!Run("JournalGraphAuditFailed", "journal/events", null, () => AuditGraphs(root, states, schemas, physical, report, Run, Add, cancellationToken))) {
                                report.Completion = RepositoryAuditCompletion.Incomplete;
                            }
                        }
                    }
                    finally { segments?.Dispose(); }
                }
                finally { schemaFile?.Dispose(); }
            }
        }
        catch (OperationCanceledException) {
            report.Completion = RepositoryAuditCompletion.Cancelled;
            report.MutableFindings.Add(new(RepositoryAuditFindingKind.Warning, "Cancelled", ".", null, "Audit was cancelled before completing all checks."));
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) {
            Add(error is InvalidDataException ? RepositoryAuditFindingKind.InvalidData : RepositoryAuditFindingKind.IoFailure, "AuditIncomplete", ".", null, error.Message);
            report.Completion = RepositoryAuditCompletion.Incomplete;
        }
        return report;
    }

    private static void AuditGraphs(string root, StateRevisionStore states, SchemaStore schemas,
        Dictionary<FrameAddress, string> physical, RepositoryAuditReport report,
        Func<string, string, long?, Action, bool> run,
        Action<RepositoryAuditFindingKind, string, string, long?, string> add, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        using var journal = JournalStore.OpenReadOnlyExisting(Path.Combine(root, "journal"));
        AuditCheckpoint?.Invoke("BeforeGraphInventory");
        var inventory = SegmentStoreAudit.Inspect(Path.Combine(root, "journal", "events"), token);
        foreach (var finding in inventory.Findings) {
            if (finding.IsCancelled) { throw new OperationCanceledException(token); }
            add(finding.IsUnsupported ? RepositoryAuditFindingKind.UnsupportedFormat : finding.IsIoFailure ? RepositoryAuditFindingKind.IoFailure : RepositoryAuditFindingKind.InvalidData,
                "JournalGraph." + finding.Code, "journal/events/" + finding.RelativePath, finding.Offset, finding.Code);
        }
        if (!inventory.IsHealthy) { report.Completion = RepositoryAuditCompletion.Incomplete; }
        token.ThrowIfCancellationRequested();
        var nearest = new Dictionary<EventAddress, HistoryGraphRecord?>();
        foreach (var segment in inventory.Segments) {
            string relative = "journal/events/" + segment.RelativePath;
            using var file = RbfFile.OpenReadOnlyExisting(Path.Combine(root, relative));
            var frames = file.ScanForward().GetEnumerator();
            while (frames.MoveNext()) {
                token.ThrowIfCancellationRequested();
                var info = frames.Current;
                run("JournalGraphInvalid", relative, info.Ticket.Offset, () => {
                    using var tail = info.ReadPooledTailMeta().Unwrap();
                    var header = EventFrameHeaderCodec.Decode(tail.TailMeta).Unwrap();
                    var address = new EventAddress(info.Ticket, segment.Number, header.Hint);
                    using var frame = journal.ReadEvent(address).Unwrap();
                    var record = HistoryJournal.Decode(address, frame);
                    HistoryGraphRecord? baseline = RepositoryGraphValidator.FindBaseline(record, nearest);
                    // Navigation derives solely from Journal, even if this graph's State dependency is invalid.
                    nearest.Add(address, record.Kind == GraphFrameKind.State ? record : baseline);
                    report.JournalEvents++;
                    if (!physical.ContainsKey(record.RevisionAddress)) { throw new InvalidDataException("Graph Revision is absent from complete physical State frames."); }
                    RepositoryGraphValidator.ValidateGraph(states, schemas, record, baseline?.RevisionAddress, token);
                });
            }
            if (frames.TerminationError is { } error) { throw new InvalidDataException(error.Message); }
        }
        foreach (string branch in journal.ListBranches()) {
            token.ThrowIfCancellationRequested();
            RefId refId = journal.OpenBranch(branch).Unwrap();
            run("JournalBranchInvalid", "journal/refs/objects/" + refId.ToHexString(), null, () => {
                var head = RepositoryGraphValidator.RequireBranchHead(journal.GetHead(refId));
                RepositoryGraphValidator.RequireBranchMembership(head, nearest.ContainsKey);
            });
        }
        token.ThrowIfCancellationRequested();
    }
}
