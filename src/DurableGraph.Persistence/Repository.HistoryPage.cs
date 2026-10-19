using Atelia.HeadStore;

namespace Atelia.DurableGraph;

public sealed partial class Repository {
    // Internal failure injection after checked metadata, before any filtering or page accumulation.
    internal Action<CommitId>? HistoryPageItemObserver { get; set; }

    /// <summary>Reads a bounded page of State/Event descriptions from an exact commit toward its ancestors.</summary>
    /// <param name="start">An inclusive or exclusive exact commit boundary in this repository.</param>
    /// <param name="limits">Positive item and HeadStore traversal-work limits for this call.</param>
    /// <param name="cancellationToken">Cancels the entire query, including after its final visited item.</param>
    /// <remarks>
    /// Reads one fixed inherited prefix, including across forks, without restoring domain objects or requiring
    /// business model registrations. It does not read today's branch tip or scan newer commits to find the anchor.
    /// Before rereads and validates the excluded anchor and charges its work. Items include all States and Events;
    /// use the returned Continuation even if application-side filtering hides the page's final items.
    /// A full page may end exactly at the root and have an empty completed continuation page. No HasMore promise
    /// or total-history count is made. Work-budget exhaustion, corruption, cancellation and cleanup failures throw;
    /// no partial page is returned. The work budget does not cover all DG metadata or backing-store I/O costs.
    /// </remarks>
    public CommitHistoryPage ReadHistoryPage(HistoryStart start, HistoryPageLimits limits,
        CancellationToken cancellationToken = default) {
        RequireAvailable();
        _busy = true;
        try {
            if (start.CommitId == default) { throw new ArgumentException("A history start requires an exact nondefault commit.", nameof(start)); }
            if (limits.MaxItems <= 0) { throw new ArgumentOutOfRangeException(nameof(limits), limits, "MaxItems must be positive."); }
            if (limits.MaxWorkSteps <= 0) { throw new ArgumentOutOfRangeException(nameof(limits), limits, "MaxWorkSteps must be positive."); }
            cancellationToken.ThrowIfCancellationRequested();
            List<CommitInfo> items = [];
            bool first = true;
            CommitId lastProcessed = default;
            long entryAllowance = (long)limits.MaxItems + (start.IsInclusive ? 0 : 1);
            HeadHistoryEnd end = _resources.ScanHistoryPage(start.CommitId.Value,
                new(entryAllowance, limits.MaxWorkSteps), record => {
                    HistoryPageItemObserver?.Invoke(new(record.Id));
                    _resources.EnsureSaveUsable();
                    cancellationToken.ThrowIfCancellationRequested();
                    if (first) {
                        first = false;
                        if (record.Id != start.CommitId.Value) { throw new InvalidDataException("History did not begin at the exact requested commit."); }
                        if (!start.IsInclusive) { return true; }
                    }
                    items.Add(Issue(record));
                    lastProcessed = new(record.Id);
                    return items.Count < limits.MaxItems;
                }, cancellationToken);
            if (first) { throw new InvalidDataException("History completed without validating its exact anchor."); }
            CommitHistoryPage page = end switch {
                HeadHistoryEnd.Complete => new(items, null, HistoryPageStopReason.ReachedRoot),
                HeadHistoryEnd.VisitorStopped when items.Count == limits.MaxItems =>
                    new(items, HistoryStart.Before(lastProcessed), HistoryPageStopReason.PageFull),
                _ => throw new InvalidDataException("History returned an unexpected termination reason."),
            };
            _resources.EnsureSaveUsable();
            cancellationToken.ThrowIfCancellationRequested();
            return page;
        } finally { EndOperation(); }
    }
}
