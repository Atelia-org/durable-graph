using Atelia.DurableGraph.Persistence;

namespace Atelia.DurableGraph;

/// <summary>A request to save one live working copy at its current exact baseline.</summary>
/// <remarks>
/// The root is retained by reference and captured only when CommitBranches executes. The summary is copied
/// when this request is created. Keep every participating graph stable throughout execution. This request
/// is not serializable or transferable to another repository or working copy. Successful State or Event
/// publication advances the baseline and makes this request stale. A healthy failure may be retried,
/// provided the original working copy and baseline remain valid; retry captures the then-current graph.
/// </remarks>
public sealed class CommitRequest {
    internal CommitRequest(WorkingCopy work, IDurableObject root, StoredGraphKind kind,
        ReadOnlyMemory<byte> summary, ReadAmplificationBaseBudgetParameters? parameters) {
        BusinessEntryPayloadCodec.ValidateSummaryLength(summary.Length);
        Work = work;
        Root = root;
        Kind = kind;
        Summary = summary.ToArray();
        Parameters = parameters;
        ExpectedBaseCommitId = work.BaseCommitId;
    }
    internal WorkingCopy Work { get; }
    internal IDurableObject Root { get; }
    internal StoredGraphKind Kind { get; }
    internal ReadOnlyMemory<byte> Summary { get; }
    internal ReadAmplificationBaseBudgetParameters? Parameters { get; }
    /// <summary>The existing branch to which this request will append.</summary>
    public BranchId BranchId => Work.BranchId;
    /// <summary>The exact baseline that must still belong to the original live working copy at execution.</summary>
    public CommitId ExpectedBaseCommitId { get; }
}
