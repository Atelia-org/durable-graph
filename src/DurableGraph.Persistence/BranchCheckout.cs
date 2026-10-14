using Atelia.FrameStore;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;

namespace Atelia.DurableGraph;

/// <summary>One branch's editable State at an exact committed history position.</summary>
/// <remarks>
/// Single-threaded. Keep graphs stable during Commit. Dispose does not save or roll back domain changes.
/// State is absent only while this history has no committed State. Events never install a State or replay
/// business handlers; the application interprets its own processing progress. At most one checkout per branch
/// may be active; different branches may have live checkouts together, with serial repository operations.
/// Dispose explicitly to release the branch. Losing this object does not automatically release occupancy.
/// The execution environment stays fixed from creation or restoration, with models closed on demand.
/// An existing State selects its own exact Schema view. State commits advance the persisted view and saving
/// baseline without replacing that execution environment; normal editing needs no checkout refresh.
/// With a committed State, re-checkout restores new domain instances from that nearest State's Schema view.
/// Without State, shared creation uses the current shared view and private restoration uses the actual Head's view.
/// Registry changes require closing the repository and opening it again; re-checkout alone does not load them.
/// Restoration and in-memory editing do not select a Schema write destination. Ordinary checkouts require an
/// Open-level Schema write mode; CheckoutPrivate explicitly selects a private destination.
/// ToShared commits explicitly select shared for one operation. Other commits without a selection reject before capture.
/// </remarks>
public sealed class BranchCheckout : IDisposable {
    private readonly Repository _repository;
    private readonly BranchSaveSession _saveSession;
    internal bool UsesPrivateSchemas { get; }
    internal BranchSaveSession SaveSession => _saveSession;
    internal DecodedRevision.SchemaViewBindingEnvironment? BindingEnvironment => SaveSession.BindingEnvironment;
    internal ModelExecutionEnvironment Models => SaveSession.Models;
    internal NormalizedGraphState? Baseline => SaveSession.Baseline;
    internal SchemaCatalogWorkspace Catalog => SaveSession.Catalog;
    private bool _disposed;
    internal bool IsLiveFor(Repository repository) => !_disposed && ReferenceEquals(_repository, repository);

    internal BranchCheckout(Repository repository, string branchName, BranchSaveSession session, CheckpointAddress? head,
        bool usesPrivateSchemas = false) {
        _repository = repository;
        BranchName = branchName;
        _saveSession = session;
        UsesPrivateSchemas = usesPrivateSchemas;
        Head = head!; // Initial creation publishes before this checkout is delivered.
    }

    public string BranchName { get; }
    /// <summary>The current editable State root, or null before the history's first State.</summary>
    /// <remarks>Successful replacement installs the supplied instance; previously obtained CLR references do not follow it.</remarks>
    public IDurableObject? State => _saveSession.State;
    /// <summary>The committed State root identity, or null before the first State.</summary>
    public ObjectId? StateId => State is null ? null : _saveSession.RootId;
    /// <summary>The committed State baseline address, or null before the first State.</summary>
    public FrameAddress? StateRevisionAddress => _saveSession.StateSource?.Address;
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
    /// The new persisted Schema view does not replace this checkout's original execution environment.
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

    /// <summary>Publishes the current State while explicitly merging the complete selected Schema view into shared.</summary>
    /// <remarks>
    /// Requires an existing State and writable owning repository. Complete definitions and layouts, including
    /// declarations unused by the business graph, must be compatible with latest shared. Only confirmed
    /// publication accepts the catalog candidate. This one operation does not change the checkout's default
    /// write destination or frozen execution environment; subsequent ordinary commits retain their existing rules.
    /// </remarks>
    public CheckpointAddress CommitStateToShared(ReadAmplificationBaseBudgetParameters? parameters = null) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IDurableObject state = State ?? throw new InvalidOperationException("There is no State to commit; supply the first State explicitly.");
        return _repository.CommitToShared(this, null, state, parameters);
    }

    /// <summary>Publishes the supplied State while explicitly merging the complete selected Schema view into shared.</summary>
    /// <remarks>
    /// The complete selected catalog, final catalog editing requests and captured layouts participate in the
    /// merge. Same complete layouts reuse shared descriptors, new items are added, and conflicting definitions
    /// are rejected. This operation does not change the checkout's default write destination or execution models.
    /// Normal return installs the supplied original State instance. Outcome and fault handling follow CommitState.
    /// </remarks>
    public CheckpointAddress CommitStateToShared(IDurableObject nextState, ReadAmplificationBaseBudgetParameters? parameters = null) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(nextState);
        return _repository.CommitToShared(this, null, nextState, parameters);
    }

    /// <summary>Publishes an Event while explicitly merging the complete selected Schema view into shared.</summary>
    /// <remarks>
    /// Requires a writable owning repository; this named operation selects the shared destination even when
    /// Open has no write mode. Confirmed publication accepts meta and Head without installing State identities,
    /// advancing its baseline or consuming rewrite requirements. Complete catalog conflicts reject preparation.
    /// The checkout's default destination and frozen execution environment remain unchanged.
    /// </remarks>
    public CheckpointAddress CommitEventToShared(IDurableObject domainEvent, ReadAmplificationBaseBudgetParameters? parameters = null) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(domainEvent);
        return _repository.CommitToShared(this, domainEvent, null, parameters);
    }

    // A bounded discard witness; the GraphResources pending never escapes the repository operation.
    internal void PrepareAndDiscard(IDurableObject root, StoredGraphKind kind = StoredGraphKind.State,
        ReadAmplificationBaseBudgetParameters? parameters = null, bool toShared = false) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _repository.PrepareAndDiscardSave(this, root, kind, parameters, toShared);
    }

    /// <summary>Releases this branch's editing occupancy without saving or removing its durable ref.</summary>
    /// <remarks>Idempotent. Reentrant disposal during a repository operation is rejected without releasing occupancy.</remarks>
    public void Dispose() {
        if (_disposed) { return; }
        _repository.Release(this);
        _disposed = true;
    }
}
