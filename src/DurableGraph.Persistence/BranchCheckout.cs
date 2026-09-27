using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph;

/// <summary>One branch's editable State at an exact committed history position.</summary>
/// <remarks>
/// Single-threaded. Keep graphs stable during Commit. Dispose does not save or roll back domain changes.
/// State is absent only while this history has no committed State. Events never install a State or replay
/// business handlers; the application interprets its own processing progress. At most one checkout per branch
/// may be active; different branches may have live checkouts together, with serial repository operations.
/// Dispose explicitly to release the branch. Losing this object does not automatically release occupancy.
/// </remarks>
public sealed class BranchCheckout : IDisposable {
    private readonly Repository _repository;
    internal WorldWorkspace Workspace { get; }
    private bool _disposed;
    internal bool IsLiveFor(Repository repository) => !_disposed && ReferenceEquals(_repository, repository);

    internal BranchCheckout(Repository repository, string branchName, WorldWorkspace workspace, CheckpointAddress? head) {
        _repository = repository;
        BranchName = branchName;
        Workspace = workspace;
        Head = head!; // Initial creation publishes before this checkout is delivered.
    }

    public string BranchName { get; }
    /// <summary>The current editable State root, or null before the history's first State.</summary>
    /// <remarks>Successful replacement installs the supplied instance; previously obtained CLR references do not follow it.</remarks>
    public IDurableObject? State => Workspace.World;
    /// <summary>The committed State root identity, or null before the first State.</summary>
    public ObjectId? StateId => State is null ? null : Workspace.WorldId;
    /// <summary>The committed State baseline address, or null before the first State.</summary>
    public FrameAddress? StateRevisionAddress => Workspace.ParentRevisionAddress;
    public CheckpointAddress Head { get; internal set; }
    /// <summary>Whether the owning repository has faulted and must be disposed and reopened.</summary>
    /// <remarks>
    /// Independent of GraphCommitException.Outcome: NotPublished can still be faulted. A healthy repository
    /// does not imply that application mutations were rolled back.
    /// </remarks>
    public bool IsFaulted => _repository.IsFaulted;

    /// <summary>Publishes an Event after the exact current Head without advancing the State baseline.</summary>
    /// <param name="domainEvent">The nonnull registered Event root; keep its reachable content stable during capture.</param>
    /// <param name="parameters">Policy for this call only; null uses the library default.</param>
    /// <remarks>
    /// Any State/Event history position can be followed by an Event. Normal return advances Head without
    /// accepting Event identity bindings or replacing State. Capturing does not freeze caller-owned objects.
    /// Publication can succeed before delivery fails. Inspect outcome and IsFaulted independently, and reopen
    /// a faulted repository to inspect history. Pre-append failures may propagate their original exception.
    /// Domain mutations are never automatically rolled back.
    /// </remarks>
    public CheckpointAddress CommitEvent(IDurableObject domainEvent, ReadAmplificationBaseBudgetParameters? parameters = null) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(domainEvent);
        return _repository.Commit(this, domainEvent, null, parameters);
    }

    /// <summary>Publishes the current State and installs its new saving baseline.</summary>
    /// <remarks>
    /// Requires an existing State; otherwise rejects before capture or append. A preceding Event is not required.
    /// Normal return retains the current domain instances. Publication can succeed before installation or delivery
    /// fails; inspect outcome and IsFaulted separately and recover using a newly opened repository.
    /// </remarks>
    public CheckpointAddress CommitState(ReadAmplificationBaseBudgetParameters? parameters = null) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IDurableObject state = State ?? throw new InvalidOperationException("There is no State to commit; supply the first State explicitly.");
        return _repository.Commit(this, null, state, parameters);
    }

    /// <summary>Publishes and installs the supplied State root, including a different actual domain type.</summary>
    /// <param name="nextState">The nonnull registered root; normal return installs this original instance.</param>
    /// <param name="parameters">Policy for this call only; null uses the library default.</param>
    /// <remarks>
    /// Establishes the first State when none exists. Existing reachable instances retain their identities;
    /// only newly encountered instances receive new identities. A root type change is not a Schema Upgrade.
    /// Candidates are prepared before publication and installed only afterward. Publication may succeed before
    /// installation or delivery fails; do not infer rollback or retry against old references. Inspect IsFaulted
    /// independently of GraphCommitException.Outcome and reopen a faulted repository to inspect its history.
    /// </remarks>
    public CheckpointAddress CommitState(IDurableObject nextState, ReadAmplificationBaseBudgetParameters? parameters = null) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(nextState);
        return _repository.Commit(this, null, nextState, parameters);
    }

    /// <summary>Releases this branch's editing occupancy without saving or removing its durable ref.</summary>
    /// <remarks>Idempotent. Reentrant disposal during a repository operation is rejected without releasing occupancy.</remarks>
    public void Dispose() {
        if (_disposed) { return; }
        _repository.Release(this);
        _disposed = true;
    }
}
