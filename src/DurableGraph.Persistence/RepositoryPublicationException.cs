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
/// For multiple branches use Members; the single-member convenience properties are null.
/// </remarks>
public sealed class RepositoryPublicationException : IOException {
    internal RepositoryPublicationException(RepositoryPublicationOutcome outcome, FrameAddress? candidate, Exception inner,
        HeadId? targetBranch = null, HeadEntryId? expectedBase = null, HeadEntryId? candidateCommit = null)
        : this(outcome, inner, [new RepositoryPublicationMember(targetBranch, expectedBase, candidate, candidateCommit)]) { }

    internal RepositoryPublicationException(RepositoryPublicationOutcome outcome, Exception inner,
        IEnumerable<RepositoryPublicationMember> members)
        : base($"Graph publication interrupted; publication outcome: {outcome}.", inner) {
        Outcome = outcome;
        Members = Array.AsReadOnly(members.ToArray());
        if (Members.Count == 1) {
            CandidateRevisionAddress = Members[0].CandidateRevisionAddress;
            TargetBranchId = Members[0].TargetBranchId;
            ExpectedBaseCommitId = Members[0].ExpectedBaseCommitId;
            CandidateCommitId = Members[0].CandidateCommitId;
        }
    }

    /// <summary>Each attempted member and the actual publication identities known at failure; never a partial-success result.</summary>
    public IReadOnlyList<RepositoryPublicationMember> Members { get; }

    /// <summary>The known publication result, independently of the repository's IsFaulted status.</summary>
    public RepositoryPublicationOutcome Outcome { get; }
    /// <summary>The candidate graph frame address when known; null for ref-only, tag and multi-member operations. It does not establish publication or form a bookmark.</summary>
    public FrameAddress? CandidateRevisionAddress { get; }
    public BranchId? TargetBranchId { get; }
    public CommitId? ExpectedBaseCommitId { get; }
    public CommitId? CandidateCommitId { get; }
}

/// <summary>An attempted branch publication, its original baseline and any actually known candidate positions.</summary>
/// <remarks>A candidate frame does not establish publication. Unknown Commit IDs remain null.</remarks>
public sealed class RepositoryPublicationMember {
    internal RepositoryPublicationMember(HeadId? target, HeadEntryId? expected, FrameAddress? candidate, HeadEntryId? commit) {
        TargetBranchId = target is { } branch ? new(branch) : null;
        ExpectedBaseCommitId = expected is { } baseline ? new(baseline) : null;
        CandidateRevisionAddress = candidate;
        CandidateCommitId = commit is { } published ? new(published) : null;
    }
    public BranchId? TargetBranchId { get; }
    public CommitId? ExpectedBaseCommitId { get; }
    public FrameAddress? CandidateRevisionAddress { get; }
    public CommitId? CandidateCommitId { get; }
}
