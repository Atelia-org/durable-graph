namespace Atelia.DurableGraph;

/// <summary>An exact commit boundary for reading inherited history from newer commits toward ancestors.</summary>
/// <remarks>
/// This value does not retain an open repository or a physical scanner. The selected repository validates
/// the commit when queried. It does not track a branch's later commits. The default value is not a valid start.
/// </remarks>
public readonly record struct HistoryStart {
    private HistoryStart(CommitId commitId, bool isInclusive) {
        CommitId = commitId;
        IsInclusive = isInclusive;
    }

    /// <summary>The exact commit anchoring this query in the selected repository.</summary>
    public CommitId CommitId { get; }
    /// <summary>Whether the anchored commit is included in the returned items.</summary>
    public bool IsInclusive { get; }

    /// <summary>Starts at the supplied commit, including that commit before its ancestors.</summary>
    public static HistoryStart AtOrBefore(CommitId commitId) => new(commitId, true);
    /// <summary>Starts immediately before the supplied commit, toward its ancestors.</summary>
    /// <remarks>The anchor is reread and validated even though it is excluded from the items.</remarks>
    public static HistoryStart Before(CommitId commitId) => new(commitId, false);
}

/// <summary>Positive limits for one materialized history page.</summary>
/// <param name="MaxItems">Maximum number of returned State/Event descriptions.</param>
/// <param name="MaxWorkSteps">Maximum HeadStore history traversal work steps for this call.</param>
/// <remarks>
/// Work steps include the repeated anchor for Before, head traversal, record reads and conditional-publication
/// checks. They do not bound bytes, elapsed time or all physical I/O: DG metadata reads and backing-store
/// opening/recovery have additional costs. Work-budget exhaustion fails the entire call without a partial page.
/// </remarks>
public readonly record struct HistoryPageLimits(int MaxItems, long MaxWorkSteps);
