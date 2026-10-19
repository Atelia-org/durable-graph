namespace Atelia.DurableGraph;

/// <summary>The reason a successful history page stopped scanning.</summary>
public enum HistoryPageStopReason {
    /// <summary>The selected inherited history reached its valid root; no continuation is needed.</summary>
    ReachedRoot,
    /// <summary>The page filled and scanning stopped without checking whether another commit exists.</summary>
    PageFull,
}

/// <summary>A materialized page of owned commit descriptions in newest-to-oldest inherited order.</summary>
/// <remarks>
/// Contains both States and Events. If the application filters these items, continue using this page's
/// Continuation rather than the last displayed item. Descriptions do not restore domain graphs or prove
/// that all their content, models or attachments can be loaded. The page remains usable after repository closure.
/// </remarks>
public sealed class CommitHistoryPage {
    internal CommitHistoryPage(IEnumerable<CommitInfo> items, HistoryStart? continuation, HistoryPageStopReason stopReason) {
        Items = Array.AsReadOnly(items.ToArray());
        Continuation = continuation;
        StopReason = stopReason;
    }

    /// <summary>Owned State/Event descriptions; no more than the requested MaxItems.</summary>
    public IReadOnlyList<CommitInfo> Items { get; }
    /// <summary>The exclusive boundary for the next page, or null after reaching the root.</summary>
    /// <remarks>A nonnull continuation does not promise another item. A full page ending at the root is followed by an empty completed page.</remarks>
    public HistoryStart? Continuation { get; }
    /// <summary>Whether the root was reached or the page filled. Budget exhaustion is an error, not a stop reason.</summary>
    public HistoryPageStopReason StopReason { get; }
}
