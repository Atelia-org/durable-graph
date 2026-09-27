using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph.Persistence;

/// <summary>The publication outcome of an interrupted Commit, independently of domain mutations.</summary>
public enum GraphCommitOutcome {
    /// <summary>The attempted publication did not take effect; writes may still have faulted the repository.</summary>
    NotPublished,
    /// <summary>Publication was attempted, but its result could not be established.</summary>
    Unknown,
    /// <summary>Publication completed, but subsequent installation or delivery failed.</summary>
    Published
}

/// <summary>A save failure with an explicit publication outcome and, when known, candidate address.</summary>
/// <remarks>
/// <see cref="Outcome"/> describes publication, not repository health or rollback of domain mutations.
/// Even <see cref="GraphCommitOutcome.NotPublished"/> can accompany a faulted repository.
/// Unknown or Published outcomes must not be transparently retried. Dispose and reopen a faulted
/// repository, then Checkout the persisted branch and inspect history with the application's saved
/// processing progress. Checkout restores only the latest State, if one exists; it never replays Events.
/// Failures before an append attempt can instead propagate their original exception; absence of this
/// wrapper does not imply that domain mutations were rolled back or that the repository is healthy.
/// </remarks>
public sealed class GraphCommitException : IOException {
    internal GraphCommitException(GraphCommitOutcome outcome, FrameAddress? candidate, Exception inner)
        : base($"Graph Commit interrupted; publication outcome: {outcome}.", inner) {
        Outcome = outcome;
        CandidateRevisionAddress = candidate;
    }

    /// <summary>The known publication result, independently of the repository's IsFaulted status.</summary>
    public GraphCommitOutcome Outcome { get; }
    /// <summary>The candidate StateRevision address when known; it does not establish publication or form a bookmark.</summary>
    public FrameAddress? CandidateRevisionAddress { get; }
}

// Internal deterministic fault injection; never part of the host publication contract.
internal enum CommitCheckpoint {
    AfterPrepare, BeforeStateAppend, AfterStateDurable, BeforeJournalAppend, AfterJournalDurable,
    BeforePublication, AfterPublication, BeforeInstall
}
