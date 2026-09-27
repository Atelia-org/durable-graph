using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.EventJournal;
using Atelia.RbfSegmentStore;
using FrameAddress = Atelia.DurableGraph.Storage.FrameAddress;

namespace Atelia.DurableGraph;

/// <summary>Owns Schema/State resources and a Journal whose named refs are the sole publication authority.</summary>
/// <remarks>
/// Single-threaded, one writer and at most one active checkout per branch. Different branches may have
/// live checkouts together; all repository operations remain serial. Close a branch's checkout before moving it.
/// Strict reopening rejects damaged tails; no automatic repair, transparent retry or power-loss guarantee.
/// Handles belong to one open repository instance. Persisted members of read results are historical snapshots.
/// Independent ReadState/ReadEvent calls allow application-side Transient initialization without a writer.
/// ReadPair may share reachable instances: its read-only constraint includes observable Transient mutation.
/// Keep per-view owner/context/cache outside paired graphs. Use Checkout to continue editing and committing.
/// Each open repository freezes its model configuration once. Later registry changes affect future opens only.
/// Successful binding/comparer closures may be reused across operations; callbacks and equality/hash policies
/// must remain semantically stable. Freezing configuration does not freeze the live Schema authority.
/// </remarks>
public sealed class Repository : IDisposable {
    private readonly HistoryJournal _history;
    private readonly GraphResources _resources;
    private readonly StateModelSnapshot _models;
    private readonly object _identity = new();
    private readonly HashSet<string> _activeBranches = new(StringComparer.Ordinal);
    private bool _busy;
    private bool _disposed;
    private PreparedStateRestoration? _preparedState;
    private static readonly ReadAmplificationBaseBudgetParameters DefaultPolicy = new(5, 5);
    internal Action<CommitCheckpoint>? Checkpoint { get; set; }
    internal bool PreparedStateReuseEnabled { get; set; } = true;
    internal bool ImmutableLeafReuseEnabled { get; set; }
    internal GraphReadStatistics? RestorationStatistics { get; set; }
    internal PreparedStateRestoration? PreparedStateEntry => _preparedState;

    private Repository(HistoryJournal history, GraphResources resources, StateModelSnapshot models) {
        _history = history;
        _resources = resources;
        _models = models;
        ValidateHistory();
        if (!resources.IsReadOnly) { history.ConfirmDurable(); }
    }

    public bool IsReadOnly => _resources.IsReadOnly;
    /// <summary>Whether this repository has faulted and must be disposed and reopened before further use.</summary>
    /// <remarks>
    /// Independent of GraphCommitException.Outcome: a NotPublished attempt can still fault the writer.
    /// False does not mean application mutations were rolled back. Recovery must inspect persisted
    /// history through a newly opened repository rather than reuse old domain objects or frame handles.
    /// </remarks>
    public bool IsFaulted => _resources.IsFaulted;
    /// <summary>Creates a repository with one frozen model environment for its entire open lifetime.</summary>
    /// <remarks>Models are captured after resource acquisition without eagerly closing unused models. Later builder changes affect only future opens.</remarks>
    /// <exception cref="ArgumentNullException">models is null; no directory or file is acquired.</exception>
    public static Repository CreateNew(string path, StateModelRegistry models, RbfSegmentStoreOptions? options = null) => Open(path, models, options, true, false);
    /// <summary>Opens existing history with one frozen model environment; physical validation does not require all current models.</summary>
    /// <remarks>An empty or partial registry supports inspection; materializing a graph still requires its model capabilities.</remarks>
    /// <exception cref="ArgumentNullException">models is null; no directory or file is acquired.</exception>
    public static Repository OpenExisting(string path, StateModelRegistry models, RbfSegmentStoreOptions? options = null) => Open(path, models, options, false, false);
    /// <summary>Opens existing history without writing, using one frozen model environment for all reads.</summary>
    /// <remarks>An empty or partial registry supports inspection; successful closures are shared within this open repository.</remarks>
    /// <exception cref="ArgumentNullException">models is null; no directory or file is acquired.</exception>
    public static Repository OpenReadOnlyExisting(string path, StateModelRegistry models, RbfSegmentStoreOptions? options = null) => Open(path, models, options, false, true);

    private static Repository Open(string path, StateModelRegistry models, RbfSegmentStoreOptions? options, bool create, bool readOnly) {
        ArgumentNullException.ThrowIfNull(models);
        HistoryJournal? history = null;
        GraphResources? resources = null;
        try {
            history = create ? HistoryJournal.Create(path) : HistoryJournal.Open(path, readOnly);
            resources = create ? GraphResources.CreateInExistingDirectory(path, options) : readOnly
                ? GraphResources.OpenReadOnlyExisting(path, options) : GraphResources.OpenExisting(path, options);
            return new(history, resources, models.Snapshot(resources.Schemas));
        } catch {
            try { resources?.Dispose(); } finally { history?.Dispose(); }
            throw;
        }
    }

    /// <summary>Publishes an initial State and returns a checkout retaining the supplied domain instances.</summary>
    /// <remarks>
    /// Requires a writable repository and a new branch name. The root must be nonnull and registered.
    /// Other branches may have active checkouts. Dispose the returned checkout to release its branch.
    /// Failure can occur after publication but before delivery; inspect the outcome and IsFaulted independently.
    /// Reopen a faulted repository to inspect persisted history. Domain mutations are not rolled back.
    /// </remarks>
    public BranchCheckout CreateBranch(string branchName, IDurableObject initialState,
        ReadAmplificationBaseBudgetParameters? parameters = null) {
        RequireWriter();
        _busy = true;
        bool acquired = false;
        bool delivered = false;
        try {
            ArgumentNullException.ThrowIfNull(initialState);
            ValidateNewName(branchName);
            acquired = _activeBranches.Add(branchName);
            if (!acquired) { throw new InvalidOperationException("Branch already has an active checkout."); }
            var workspace = WorldWorkspace.CreateSnapshot(_resources.States, _resources.Schemas, initialState, _models);
            var checkout = new BranchCheckout(this, branchName, workspace, null);
            Publish(checkout, null, initialState, parameters ?? DefaultPolicy, initial: true);
            delivered = true;
            return checkout;
        } finally {
            if (acquired && !delivered) { _activeBranches.Remove(branchName); }
            EndOperation();
        }
    }

    /// <summary>Publishes an initial Event and returns a checkout with no State.</summary>
    /// <remarks>
    /// Requires a writable repository and a new branch name; other branches may have active checkouts.
    /// The Event is captured without installing a State or a saving baseline. No empty-head branch is created.
    /// Publication may succeed before delivery fails; inspect the outcome and reopen a faulted repository.
    /// </remarks>
    public BranchCheckout CreateBranchFromEvent(string branchName, IDurableObject initialEvent,
        ReadAmplificationBaseBudgetParameters? parameters = null) {
        RequireWriter();
        _busy = true;
        bool acquired = false;
        bool delivered = false;
        try {
            ArgumentNullException.ThrowIfNull(initialEvent);
            ValidateNewName(branchName);
            acquired = _activeBranches.Add(branchName);
            if (!acquired) { throw new InvalidOperationException("Branch already has an active checkout."); }
            var workspace = WorldWorkspace.CreateEmpty(_resources.States, _resources.Schemas, _models);
            var checkout = new BranchCheckout(this, branchName, workspace, null);
            Publish(checkout, initialEvent, null, parameters ?? DefaultPolicy, initial: true);
            delivered = true;
            return checkout;
        } finally {
            if (acquired && !delivered) { _activeBranches.Remove(branchName); }
            EndOperation();
        }
    }

    /// <summary>Restores the nearest State at the exact branch head, without materializing or replaying Events.</summary>
    /// <remarks>
    /// Requires a writable repository and no active checkout for this branch. Other branches remain available.
    /// A valid history with no State yields State=null. Dispose explicitly to release the branch's occupancy.
    /// An existing State's missing model or restoration failure propagates; there is no older-State fallback.
    /// Restoration runs decoding, Upgrade and materialization, but no business handler. Constructors and field
    /// initializers do not run; application code rebuilds Transient state and interprets its own event progress.
    /// </remarks>
    public BranchCheckout Checkout(string branchName) {
        RequireWriter();
        _busy = true;
        bool acquired = false;
        bool delivered = false;
        try {
            CheckpointAddress head = HeadCore(branchName);
            acquired = _activeBranches.Add(branchName);
            if (!acquired) { throw new InvalidOperationException("Branch already has an active checkout."); }
            BranchCheckout checkout = RestoreCheckout(branchName, head, out PreparedStateRestoration? candidate);
            if (candidate is not null) { _preparedState = candidate; }
            delivered = true;
            return checkout;
        } finally {
            if (acquired && !delivered) { _activeBranches.Remove(branchName); }
            EndOperation();
        }
    }

    /// <summary>Creates a durable named branch at source and returns its independently restored checkout.</summary>
    /// <param name="branchName">A new branch name; the returned checkout occupies only this branch.</param>
    /// <param name="source">A committed State or Event address issued by this open repository.</param>
    /// <remarks>
    /// Requires a writable repository. Source checkouts may remain open; their uncommitted changes are not read.
    /// Restores only the nearest committed State, or an empty workspace before the first State, while retaining
    /// the exact source Head. Does not materialize Events, replay handlers, capture graphs or append history frames.
    /// Mutable instances are independent of other restorations, preserving aliases and cycles within this graph.
    /// All restoration and checkout preparation precede ref publication; ordinary restoration failures leave no
    /// new ref. Publication can succeed before delivery fails: inspect GraphCommitException.Outcome and IsFaulted
    /// independently, then reopen a faulted repository to inspect refs. Application callback effects are not rolled back.
    /// </remarks>
    public BranchCheckout Fork(string branchName, CheckpointAddress source) {
        RequireWriter();
        _busy = true;
        bool acquired = false;
        bool delivered = false;
        try {
            CheckFrame(source);
            ValidateNewName(branchName);
            acquired = _activeBranches.Add(branchName);
            if (!acquired) { throw new InvalidOperationException("Branch already has an active checkout."); }
            BranchCheckout checkout = RestoreCheckout(branchName, source, out PreparedStateRestoration? candidate);
            MutateRefCore(() => _history.Journal.CreateBranch(branchName, source.Address));
            if (candidate is not null) { _preparedState = candidate; }
            delivered = true;
            return checkout;
        } finally {
            if (acquired && !delivered) { _activeBranches.Remove(branchName); }
            EndOperation();
        }
    }

    private BranchCheckout RestoreCheckout(string branchName, CheckpointAddress head,
        out PreparedStateRestoration? candidate) {
        candidate = null;
        CheckpointAddress? state = NearestStateCore(head);
        // Event-only histories must not even inspect another branch's resident certificate.
        if (state is null) {
            return new BranchCheckout(this, branchName,
                WorldWorkspace.CreateEmpty(_resources.States, _resources.Schemas, _models), head);
        }
        CheckFrame(state, GraphFrameKind.State);
        RevisionReadSession reads = new(_resources.States, _resources.Schemas, _models, RestorationStatistics);
        if (PreparedStateReuseEnabled && _preparedState is { } prepared && prepared.Matches(state)) {
            reads.Statistics.PreparedStateHits++;
            if (ImmutableLeafReuseEnabled && !prepared.ImmutableLeavesCollected) {
                // Enabling the experiment after this entry was prepared needs a fresh
                // complete materialization certificate, including callbacks now omitted by reuse.
                using var leafRequirements = _models.BeginSchemaRequirementCollection();
                prepared.Validate(reads, state);
                var restored = GraphReader.RestorePreparedState(reads, prepared.Selection,
                    out var leaves, collectImmutableLeaves: true);
                var restoredCheckout = new BranchCheckout(this, branchName, WorldWorkspace.FromLoaded(reads, restored), head);
                candidate = new(state, prepared.Selection, leafRequirements.Complete(), leaves, ImmutableLeavesCollected: true);
                return restoredCheckout;
            }
            prepared.Validate(reads, state);
            var loaded = GraphReader.RestorePreparedState(reads, prepared.Selection, out _,
                reusedLeaves: ImmutableLeafReuseEnabled ? prepared.ImmutableLeaves : null);
            return new BranchCheckout(this, branchName, WorldWorkspace.FromLoaded(reads, loaded), head);
        }

        reads.Statistics.PreparedStateMisses++;
        using var requirements = _models.BeginSchemaRequirementCollection();
        PreparedGraphSelection selection = GraphReader.PrepareState(reads, state.RevisionAddress, state.RootId);
        // Include every source/current row, even when Upgrade cuts its last incoming edge.
        // Plans and standard checks performed throughout this lexical scope contribute too.
        foreach ((ObjectId id, NormalizedObject row) in selection.Normalized.Objects) {
            _models.CheckObjectLayout(row.SourceLayout, $"prepared State source object {id.Value}");
            _models.CheckObjectLayout(row.Current.Layout, $"prepared State current object {id.Value}");
        }
        bool collectLeaves = PreparedStateReuseEnabled && ImmutableLeafReuseEnabled;
        var materialized = GraphReader.RestorePreparedState(reads, selection, out var immutableLeaves,
            collectImmutableLeaves: collectLeaves);
        var workspace = WorldWorkspace.FromLoaded(reads, materialized);
        var checkout = new BranchCheckout(this, branchName, workspace, head);
        var certificate = requirements.Complete();
        // Candidate construction precedes Fork publication; success only installs a reference.
        if (PreparedStateReuseEnabled) { candidate = new(state, selection, certificate, immutableLeaves, collectLeaves); }
        return checkout;
    }

    public IReadOnlyList<string> ListBranches() { RequireAvailable(); return _history.Journal.ListBranches(); }
    public CheckpointAddress GetHead(string branchName) { RequireAvailable(); return HeadCore(branchName); }

    /// <summary>Fully materializes the selected logical chain from oldest to newest, including State and Event handles.</summary>
    /// <param name="branchName">The branch whose head is obtained once for this operation.</param>
    /// <returns>A fully materialized, chronological list of handles owned by this open repository.</returns>
    /// <remarks>
    /// Reads the complete ancestor chain of the selected head; excludes physical orphan appends and
    /// records unique to other branches. No domain objects are restored. Taking the last few results
    /// does not avoid full-chain reading or allocation: this is not a paginated or lazy API.
    /// Opening the repository separately validates physical Journal records and referenced revisions,
    /// including orphans; that validation cost is distinct from this logical-chain enumeration.
    /// </remarks>
    public IReadOnlyList<CheckpointAddress> ReadFrames(string branchName) {
        RequireAvailable();
        CheckpointAddress head = HeadCore(branchName);
        return _history.Journal.ReadChronologicalChain(head.Address, checkedRead: true).Unwrap()
            .Select(address => Issue(_history.Read(address))).ToArray();
    }

    /// <summary>Enumerates Event addresses in the fixed logical history ending at endInclusive.</summary>
    /// <param name="endInclusive">The committed end of the query, unaffected by later branch changes.</param>
    /// <param name="order">NewestFirst reads ancestors on demand unless lower-bound validation is required.</param>
    /// <param name="afterExclusive">An optional ancestor excluded from the range; the same end gives an empty range.</param>
    /// <returns>Event addresses only; State records are traversed without materializing domain graphs.</returns>
    /// <remarks>
    /// Both addresses must belong to this open repository. A supplied lower bound is checked against
    /// the logical ancestor chain before the first result; an unrelated position is rejected. That
    /// validation can scan the complete range. OldestFirst buffers the selected Event addresses before
    /// delivery. Neither direction materializes domain objects, and NewestFirst without a lower bound
    /// does not prepare the complete chain. Opening has separate full physical-history validation costs.
    /// Each advance requires an available repository, but no busy guard or backing lease remains held
    /// between results. Call ReadEvent or perform other serial operations inside the loop. This query
    /// describes recorded Events, not pending work or application progress.
    /// </remarks>
    public IEnumerable<CheckpointAddress> EnumerateEvents(CheckpointAddress endInclusive,
        HistoryOrder order = HistoryOrder.NewestFirst, CheckpointAddress? afterExclusive = null) {
        RequireAvailable();
        CheckFrame(endInclusive);
        if (afterExclusive is not null) { CheckFrame(afterExclusive); }
        if (order is not HistoryOrder.OldestFirst and not HistoryOrder.NewestFirst) {
            throw new ArgumentOutOfRangeException(nameof(order));
        }
        return new EventHistoryEnumerable(this, endInclusive, order, afterExclusive);
    }

    private IEnumerable<CheckpointAddress> EnumerateEventsCore(CheckpointAddress endInclusive,
        HistoryOrder order, CheckpointAddress? afterExclusive) {
        EventAddress? cursor = endInclusive.Address;
        List<CheckpointAddress>? buffered = null;
        int bufferedIndex = -1;
        bool prepared = false;
        while (true) {
            CheckpointAddress? next;
            _busy = true;
            try {
                if (!prepared) {
                    if (order == HistoryOrder.OldestFirst) { buffered = []; }
                    if (afterExclusive is not null || buffered is not null) {
                        CheckpointAddress? probe = endInclusive;
                        while (probe is not null && probe != afterExclusive) {
                            if (probe.Kind == GraphFrameKind.Event) { buffered?.Add(probe); }
                            probe = probe.Parent is { } parent ? Issue(_history.Read(parent)) : null;
                        }
                        if (afterExclusive is not null && probe is null) {
                            throw new ArgumentException("The lower bound is not an ancestor of the selected end.", nameof(afterExclusive));
                        }
                        bufferedIndex = (buffered?.Count ?? 0) - 1;
                    }
                    prepared = true;
                }
                next = buffered is not null
                    ? bufferedIndex >= 0 ? buffered[bufferedIndex--] : null
                    : ReadNextEventCore(endInclusive, afterExclusive, ref cursor);
            } finally { EndOperation(); }
            // HistoryJournal.Read has already disposed its frame/lease. No repository guard
            // survives the yield, including when the caller stops before completing the range.
            if (next is null) { yield break; }
            yield return next;
        }
    }

    private CheckpointAddress? ReadNextEventCore(CheckpointAddress endInclusive,
        CheckpointAddress? afterExclusive, ref EventAddress? cursor) {
        while (cursor is { } address && address != afterExclusive?.Address) {
            CheckpointAddress frame = address == endInclusive.Address ? endInclusive : Issue(_history.Read(address));
            cursor = frame.Parent;
            if (frame.Kind == GraphFrameKind.Event) { return frame; }
        }
        return null;
    }

    private sealed class EventHistoryEnumerable(Repository repository, CheckpointAddress endInclusive,
        HistoryOrder order, CheckpointAddress? afterExclusive) : IEnumerable<CheckpointAddress> {
        public IEnumerator<CheckpointAddress> GetEnumerator() => new EventHistoryEnumerator(repository,
            repository.EnumerateEventsCore(endInclusive, order, afterExclusive).GetEnumerator());
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class EventHistoryEnumerator(Repository repository,
        IEnumerator<CheckpointAddress> inner) : IEnumerator<CheckpointAddress> {
        public CheckpointAddress Current => inner.Current;
        object System.Collections.IEnumerator.Current => Current;
        public bool MoveNext() {
            // Compiler-generated iterators skip their body after completion. Keep the check
            // outside that body so every advance, including an exhausted query, checks lifetime.
            repository.RequireAvailable();
            return inner.MoveNext();
        }
        public void Reset() => throw new NotSupportedException();
        public void Dispose() => inner.Dispose();
    }

    /// <summary>Finds the nearest strict ancestor State of an Event, or null in an Event-only prefix.</summary>
    public CheckpointAddress? GetPreviousState(CheckpointAddress eventFrame) {
        RequireAvailable();
        CheckFrame(eventFrame, GraphFrameKind.Event);
        return PreviousStateCore(eventFrame);
    }

    /// <summary>Independently restores the actual domain root of an Event snapshot.</summary>
    /// <remarks>
    /// Each call restores its own mutable domain instances. Application code may initialize their
    /// Transient state after delivery, without opening a writer; persisted members remain a historical
    /// snapshot. Constructors and field initializers do not run. This does not install a saving baseline:
    /// use Checkout to continue editing and committing. Strings and application-owned global objects do
    /// not acquire a general deep-copy guarantee.
    /// </remarks>
    public IDurableObject ReadEvent(CheckpointAddress frame) => Read(frame, GraphFrameKind.Event);
    /// <summary>Independently restores the actual domain root of a State snapshot.</summary>
    /// <remarks>
    /// Each call restores its own mutable domain instances. Application code may initialize their
    /// Transient state after delivery, without opening a writer; persisted members remain a historical
    /// snapshot. Constructors and field initializers do not run. This does not install a saving baseline:
    /// use Checkout to continue editing and committing. Strings and application-owned global objects do
    /// not acquire a general deep-copy guarantee.
    /// </remarks>
    public IDurableObject ReadState(CheckpointAddress frame) => Read(frame, GraphFrameKind.State);

    /// <summary>Restores the selected graph and its nearest strict ancestor of the opposite role.</summary>
    /// <remarks>
    /// Eagerly restores at most two independent graphs, preserving actual root types, aliases and cycles
    /// within each graph. Both restores must succeed before a Checkpoint is returned. PreviousState or
    /// PreviousEvent and its address are both null when no such ancestor exists; no older graphs are
    /// materialized. Repeated getters return the same roots, retaining application edits and Transient
    /// initialization. Editing a result changes memory only and does not advance a branch or checkout.
    /// Application callback side effects are not rolled back on failure. For just one Event graph,
    /// use ReadEvent; ReadPair instead has an explicit read-only sharing contract.
    /// </remarks>
    public Checkpoint ReadCheckpoint(CheckpointAddress address) {
        RequireAvailable();
        CheckFrame(address);
        _busy = true;
        try {
            GraphFrameKind previousKind = address.Kind == GraphFrameKind.Event ? GraphFrameKind.State : GraphFrameKind.Event;
            CheckpointAddress? previousAddress = PreviousCore(address, previousKind);
            var session = new RevisionReadSession(_resources.States, _resources.Schemas, _models);
            IDurableObject root;
            IDurableObject? previous;
            if (previousAddress is null) {
                root = GraphReader.ReadRoot<IDurableObject>(session, address.RevisionAddress, address.RootId);
                previous = null;
            } else {
                (root, previous) = GraphReader.ReadIndependent(session, address.RevisionAddress, address.RootId,
                    previousAddress.RevisionAddress, previousAddress.RootId);
            }
            return address.Kind == GraphFrameKind.Event
                ? new EventCheckpoint(address, root, previous, previousAddress)
                : new StateCheckpoint(address, root, previous, previousAddress);
        } finally { EndOperation(); }
    }

    private IDurableObject Read(CheckpointAddress frame, GraphFrameKind kind) {
        RequireAvailable();
        CheckFrame(frame, kind);
        _busy = true;
        try {
            var session = new RevisionReadSession(_resources.States, _resources.Schemas, _models);
            return GraphReader.ReadRoot<IDurableObject>(session, frame.RevisionAddress, frame.RootId);
        } finally { EndOperation(); }
    }

    /// <summary>Experimental read-only pair without requiring the current root types in advance.</summary>
    /// <remarks>
    /// First and Second follow input order, independently of each frame's State/Event kind.
    /// Actual root types are preserved. Both graphs must be treated as read-only; cross-graph
    /// instance identity is not guaranteed, and both reads must succeed before delivery.
    /// This includes observable Transient mutation on any reachable object. Keep per-view owner,
    /// context, indexes and caches outside the graphs, since a node may belong to both views.
    /// Use independent ReadState/ReadEvent calls for per-view Transient initialization without a writer;
    /// use Checkout to continue editing and committing. Sharing comparison does not prepare object
    /// Base/Delta payloads; ordinary read validation can still encode canonical Dictionary keys.
    /// Application callback side effects are not rolled back.
    /// </remarks>
    public (IDurableObject First, IDurableObject Second) ReadPair(CheckpointAddress first, CheckpointAddress second) {
        RequireAvailable();
        CheckFrame(first);
        CheckFrame(second);
        _busy = true;
        try {
            return GraphReader.ReadPair<IDurableObject, IDurableObject>(_resources.States, _resources.Schemas,
                first.RevisionAddress, first.RootId, second.RevisionAddress, second.RootId, _models);
        } finally { EndOperation(); }
    }

    /// <summary>Creates a named branch at any checked historical Event or State, without moving the source branch.</summary>
    /// <remarks>
    /// Requires a writable repository, a new branch name and an address issued by this open instance.
    /// Active checkouts on other branches do not prevent this operation.
    /// Creates only a persistent ref: no domain graph is restored and no editing checkout is acquired.
    /// Publication may succeed before delivery fails; inspect GraphCommitException.Outcome and IsFaulted
    /// independently, and reopen a faulted repository to inspect the actual published refs.
    /// </remarks>
    public CheckpointAddress CreateBranch(string branchName, CheckpointAddress selectedFrame) {
        RequireWriter();
        _busy = true;
        try {
            CheckFrame(selectedFrame);
            ValidateNewName(branchName);
            MutateRefCore(() => _history.Journal.CreateBranch(branchName, selectedFrame.Address));
            return selectedFrame;
        } finally { EndOperation(); }
    }

    /// <summary>Explicit compare-and-swap movement; requires closing the target branch's checkout first.</summary>
    /// <remarks>Other branches' active checkouts do not block movement. Both addresses belong to this open repository.</remarks>
    public void MoveBranch(string branchName, CheckpointAddress expectedHead, CheckpointAddress target) {
        RequireWriter();
        _busy = true;
        try {
            CheckFrame(expectedHead);
            CheckFrame(target);
            RefId branch = _history.Journal.OpenBranch(branchName).Unwrap();
            if (_activeBranches.Contains(branchName)) { throw new InvalidOperationException("Close this branch's active checkout before moving it."); }
            if (_history.Journal.GetHead(branch) != expectedHead.Address) { throw new InvalidOperationException("Branch head no longer matches expectedHead."); }
            MutateRefCore(() => _history.Journal.MoveRef(branch, expectedHead.Address, target.Address));
        } finally { EndOperation(); }
    }

    // The caller owns the complete operation's busy guard, including restoration and delivery.
    private void MutateRefCore<T>(Func<AteliaResult<T>> mutation) where T : notnull {
        bool attempted = false;
        bool published = false;
        try {
            Checkpoint?.Invoke(CommitCheckpoint.BeforePublication);
            attempted = true;
            var result = mutation();
            if (result.IsFailure && result.Error is EventJournalError { ErrorName: "RefCasMismatch" }) { attempted = false; }
            result.Unwrap();
            published = true;
            Checkpoint?.Invoke(CommitCheckpoint.AfterPublication);
        } catch (Exception error) {
            if (attempted) { _resources.MarkFaulted(); }
            throw new GraphCommitException(published ? GraphCommitOutcome.Published : attempted ? GraphCommitOutcome.Unknown : GraphCommitOutcome.NotPublished, null, error);
        }
    }

    internal CheckpointAddress Commit(BranchCheckout session, IDurableObject? domainEvent,
        IDurableObject? nextState, ReadAmplificationBaseBudgetParameters? parameters) {
        RequireAvailable();
        _resources.RequireWritable();
        _busy = true;
        try {
            if (!session.IsLiveFor(this) || !_activeBranches.Contains(session.BranchName) ||
                HeadCore(session.BranchName).Address != session.Head.Address) {
                throw new InvalidOperationException("Session does not own the expected branch head.");
            }
            CheckpointAddress? state = NearestStateCore(session.Head);
            if (session.Workspace.ParentRevisionAddress != state?.RevisionAddress ||
                (state is null ? session.Workspace.World is not null : session.Workspace.WorldId != state.RootId)) {
                throw new InvalidOperationException("Checkout State baseline does not match the Journal chain.");
            }
            return Publish(session, domainEvent, nextState, parameters ?? DefaultPolicy, initial: false);
        } finally { EndOperation(); }
    }

    private CheckpointAddress Publish(BranchCheckout session, IDurableObject? domainEvent,
        IDurableObject? nextState, ReadAmplificationBaseBudgetParameters parameters, bool initial) {
        FrameAddress? address = null;
        bool writeAttempted = false;
        bool refAttempted = false;
        bool published = false;
        try {
            using PreparedWorldSave pending = domainEvent is null
                ? session.Workspace.Stage(nextState!, parameters) : session.Workspace.StageSnapshot(domainEvent, parameters);
            Checkpoint?.Invoke(CommitCheckpoint.AfterPrepare);
            Checkpoint?.Invoke(CommitCheckpoint.BeforeStateAppend);
            writeAttempted = true;
            address = _resources.States.AppendDurably(pending.Revision);
            Checkpoint?.Invoke(CommitCheckpoint.AfterStateDurable);
            if (domainEvent is null) { pending.PrepareInstall(address.Value); }
            GraphFrameKind kind = domainEvent is null ? GraphFrameKind.State : GraphFrameKind.Event;
            Checkpoint?.Invoke(CommitCheckpoint.BeforeJournalAppend);
            EventAddress journalAddress = _history.Append(kind, address.Value, pending.RootId, initial ? null : session.Head.Address);
            Checkpoint?.Invoke(CommitCheckpoint.AfterJournalDurable);
            CheckpointAddress frame = Issue(_history.Read(journalAddress));
            RefId branch = initial ? default : _history.Journal.OpenBranch(session.BranchName).Unwrap();
            Checkpoint?.Invoke(CommitCheckpoint.BeforePublication);
            refAttempted = true;
            if (initial) { _history.Journal.CreateBranch(session.BranchName, journalAddress).Unwrap(); }
            else {
                var result = _history.Journal.AdvanceRef(branch, session.Head.Address, journalAddress);
                if (result.IsFailure && result.Error is EventJournalError { ErrorName: "RefCasMismatch" }) { refAttempted = false; }
                result.Unwrap();
            }
            published = true;
            Checkpoint?.Invoke(CommitCheckpoint.AfterPublication);
            Checkpoint?.Invoke(CommitCheckpoint.BeforeInstall);
            if (domainEvent is null) { pending.Install(); }
            session.Head = frame;
            return frame;
        } catch (Exception error) {
            if (writeAttempted || refAttempted || _resources.Schemas.IsFaulted) { _resources.MarkFaulted(); }
            if (!writeAttempted && !refAttempted && address is null) { throw; }
            throw new GraphCommitException(published ? GraphCommitOutcome.Published : refAttempted ? GraphCommitOutcome.Unknown : GraphCommitOutcome.NotPublished, address, error);
        }
    }

    private CheckpointAddress HeadCore(string branchName) {
        RefId branch = _history.Journal.OpenBranch(branchName).Unwrap();
        EventAddress head = _history.Journal.GetHead(branch) ?? throw new InvalidDataException("EventHistory branches cannot have empty heads.");
        return Issue(_history.Read(head));
    }
    private CheckpointAddress? PreviousStateCore(CheckpointAddress frame) => PreviousCore(frame, GraphFrameKind.State);

    private CheckpointAddress? PreviousCore(CheckpointAddress frame, GraphFrameKind kind) {
        while (frame.Parent is { } parent) {
            frame = Issue(_history.Read(parent));
            if (frame.Kind == kind) { return frame; }
        }
        return null;
    }

    private CheckpointAddress? NearestStateCore(CheckpointAddress frame) {
        while (frame.Kind != GraphFrameKind.State) {
            if (frame.Parent is not { } parent) { return null; }
            frame = Issue(_history.Read(parent));
        }
        return frame;
    }
    private CheckpointAddress Issue(HistoryGraphRecord record) => new(_identity, record);
    private void CheckFrame(CheckpointAddress frame, GraphFrameKind? kind = null) {
        ArgumentNullException.ThrowIfNull(frame);
        if (!ReferenceEquals(frame.Owner, _identity)) { throw new ArgumentException("Frame was issued by another repository instance.", nameof(frame)); }
        if (kind is not null && frame.Kind != kind) { throw new ArgumentException("Frame has the wrong Event/State role.", nameof(frame)); }
    }
    private void ValidateNewName(string name) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var result = _history.Journal.OpenBranch(name);
        if (result.IsSuccess) { throw new InvalidOperationException("Branch name already exists."); }
        if (result.Error is not EventJournalError { ErrorName: "BranchNotFound" }) {
            throw new ArgumentException(result.Error!.Message, nameof(name));
        }
    }

    private void ValidateHistory() {
        IReadOnlyList<HistoryGraphRecord> physicalRecords = _history.ReadAllFrames();
        var records = physicalRecords.ToDictionary(record => record.Address);
        // Physical scanning is oldest-first, and HistoryJournal rejects parents at or after their child.
        // This derived index carries only navigation; Journal Parent remains the persisted authority.
        var nearestStates = new Dictionary<EventAddress, HistoryGraphRecord?>();
        foreach (HistoryGraphRecord record in physicalRecords) {
            HistoryGraphRecord? baseline = null;
            if (record.Parent is { } parentAddress) {
                if (!records.ContainsKey(parentAddress) || !nearestStates.TryGetValue(parentAddress, out baseline)) {
                    throw new InvalidDataException("Journal Parent is missing from the preceding physical history.");
                }
            }
            ValidateGraph(record, baseline?.RevisionAddress);
            nearestStates.Add(record.Address, record.Kind == GraphFrameKind.State ? record : baseline);
        }
        foreach (string branch in _history.Journal.ListBranches()) {
            if (!records.ContainsKey(HeadCore(branch).Address)) {
                throw new InvalidDataException("Branch head is absent from physical Journal history.");
            }
        }
    }

    private void ValidateGraph(HistoryGraphRecord record, FrameAddress? expectedParent) {
        StateRevision revision = _resources.States.Read(record.RevisionAddress);
        if (revision.ParentRevisionAddress != expectedParent) { throw new InvalidDataException("Journal and StateRevision Parent disagree."); }
        var objects = _resources.States.ReadLiveObjectHeadMap(record.RevisionAddress);
        if (!objects.ContainsKey(record.RootId.Value)) { throw new InvalidDataException("Graph root is absent from Revision membership."); }
        foreach (uint id in objects.Keys) {
            ObjectVersionChain chain = _resources.States.ReadObjectVersionChain(record.RevisionAddress, id);
            DecodedBaseObjectBody body = BaseObjectBodyCodec.Decode(chain.Records[0].Record.Body, _resources.Schemas);
            if (body.Kind == ObjectStateKind.Durable && body.Layout.Schema!.Kind != SchemaKind.ReferenceObject) { throw new InvalidDataException("Object Base cannot select an inline Schema."); }
            if (body.Kind == ObjectStateKind.String && chain.Records.Count != 1) { throw new InvalidDataException("String cannot have a Delta chain."); }
            if (id == record.RootId.Value && body.Kind != ObjectStateKind.Durable) { throw new InvalidDataException("Graph root must be durable."); }
        }
    }

    internal void Release(BranchCheckout session) {
        if (_busy) { throw new InvalidOperationException("Cannot dispose a session during a repository operation."); }
        if (session.IsLiveFor(this)) { _activeBranches.Remove(session.BranchName); }
    }
    private void EndOperation() {
        _busy = false;
        if (_resources.IsFaulted) { _preparedState = null; }
    }
    private void RequireWriter() {
        RequireAvailable();
        _resources.RequireWritable();
    }
    private void RequireAvailable() {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_resources.IsFaulted) { _preparedState = null; }
        _resources.RequireAvailable();
        if (_busy) { throw new InvalidOperationException("Repository operations cannot be reentered."); }
    }
    public void Dispose() {
        if (_disposed) { return; }
        if (_busy) { throw new InvalidOperationException("Cannot dispose a busy repository."); }
        _disposed = true;
        _preparedState = null;
        _activeBranches.Clear();
        try { _resources.Dispose(); } finally { _history.Dispose(); }
    }
}
