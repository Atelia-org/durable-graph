using Atelia.FrameStore;
using Atelia.HeadStore;
using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph;

/// <summary>The outcome of interrupted graph, branch-ref or tag publication, independently of domain mutations.</summary>
public enum RepositoryPublicationOutcome {
    /// <summary>The attempted publication did not take effect; writes may still have faulted the repository.</summary>
    NotPublished,
    /// <summary>Publication was attempted, but its result could not be established.</summary>
    Unknown,
    /// <summary>Publication completed, but subsequent installation or delivery failed.</summary>
    Published
}

/// <summary>A graph, branch-ref or tag publication failure with an explicit outcome and optional candidate address.</summary>
/// <remarks>
/// <see cref="Outcome"/> describes publication, not repository health or rollback of domain mutations.
/// Even <see cref="RepositoryPublicationOutcome.NotPublished"/> can accompany a faulted repository.
/// Unknown or Published outcomes must not be transparently retried. Dispose and reopen a faulted
/// repository. For a graph or branch-ref operation, inspect the persisted branch and its history,
/// then OpenWorkingCopy using the application's saved processing progress. A checkout restores only the latest
/// State, if one exists; it never replays Events. For tag creation, ResolveTag by name to determine
/// the persisted binding; tags do not have an editing checkout and cannot be rebound.
/// Failures before an append attempt can instead propagate their original exception; absence of this
/// wrapper does not imply that domain mutations were rolled back or that the repository is healthy.
/// </remarks>
public sealed class RepositoryPublicationException : IOException {
    internal RepositoryPublicationException(RepositoryPublicationOutcome outcome, FrameAddress? candidate, Exception inner,
        HeadId? targetBranch = null, HeadEntryId? expectedBase = null, HeadEntryId? candidateCommit = null)
        : base($"Graph publication interrupted; publication outcome: {outcome}.", inner) {
        Outcome = outcome;
        CandidateRevisionAddress = candidate;
        TargetBranchId = targetBranch is { } branch ? new(branch) : null;
        ExpectedBaseCommitId = expectedBase is { } expected ? new(expected) : null;
        CandidateCommitId = candidateCommit is { } commit ? new(commit) : null;
    }

    /// <summary>The known publication result, independently of the repository's IsFaulted status.</summary>
    public RepositoryPublicationOutcome Outcome { get; }
    /// <summary>The candidate graph frame address when known; null for ref-only and tag operations. It does not establish publication or form a bookmark.</summary>
    public FrameAddress? CandidateRevisionAddress { get; }
    public BranchId? TargetBranchId { get; }
    public CommitId? ExpectedBaseCommitId { get; }
    public CommitId? CandidateCommitId { get; }
}
