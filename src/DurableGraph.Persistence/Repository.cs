using Atelia.FrameStore;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.VersionStore;


namespace Atelia.DurableGraph;

/// <summary>Owns shared data and the VersionStore root publishing branches and immutable tags.</summary>
/// <remarks>
/// Single-threaded, one writer and at most one active checkout per branch. Different branches may have
/// live checkouts together; all repository operations remain serial. Close a branch's checkout before moving it.
/// Backing stores recover incomplete tails during normal writable opens; there is no transparent publication retry.
/// Handles belong to one open repository instance. Persisted members of read results are historical snapshots.
/// Independent ReadState/ReadEvent calls allow application-side Transient initialization without a writer.
/// ReadPair may share reachable instances: its read-only constraint includes observable Transient mutation.
/// Keep per-view owner/context/cache outside paired graphs. Use Checkout to continue editing and committing.
/// Each open repository freezes its model configuration once. Later registry changes affect future opens only.
/// Successful binding/comparer closures may be reused across operations; callbacks and equality/hash policies
/// must remain semantically stable. Freezing configuration does not freeze the live Schema authority.
/// </remarks>
public sealed partial class Repository : IDisposable {
    private readonly GraphResources _resources;
    private readonly StateModelSnapshot? _models;
    private StateModelSnapshot LegacyModels => _models ?? throw new NotSupportedException("Legacy model access is unavailable in split storage.");
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

    private Repository(GraphResources resources, StateModelSnapshot models) {
        _resources = resources;
        _models = models;
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
    public static Repository CreateNew(string path, StateModelRegistry models, long rotationThresholdBytes = 64L * 1024 * 1024 * 1024) => Open(path, models, rotationThresholdBytes, true, false);
    /// <summary>Opens the required repository root and Schema snapshot with one frozen model environment.</summary>
    /// <remarks>An empty or partial registry supports inspection; materializing a graph still requires its model capabilities.</remarks>
    /// <exception cref="ArgumentNullException">models is null; no directory or file is acquired.</exception>
    public static Repository OpenExisting(string path, StateModelRegistry models, long rotationThresholdBytes = 64L * 1024 * 1024 * 1024) => Open(path, models, rotationThresholdBytes, false, false);
    /// <summary>Opens existing history without writing, using one frozen model environment for all reads.</summary>
    /// <remarks>An empty or partial registry supports inspection; successful closures are shared within this open repository.</remarks>
    /// <exception cref="ArgumentNullException">models is null; no directory or file is acquired.</exception>
    public static Repository OpenReadOnlyExisting(string path, StateModelRegistry models) => Open(path, models, 64L * 1024 * 1024 * 1024, false, true);

    private static Repository Open(string path, StateModelRegistry models, long rotationThresholdBytes, bool create, bool readOnly) {
        ArgumentNullException.ThrowIfNull(models);
        GraphResources? resources = null;
        try {
            resources = create ? GraphResources.CreateNew(path, rotationThresholdBytes) : readOnly
                ? GraphResources.OpenReadOnlyExisting(path) : GraphResources.OpenExisting(path, rotationThresholdBytes);
            return new(resources, models.Snapshot(resources.Schemas));
        } catch (Exception primary) {
            try { resources?.Dispose(); }
            catch (Exception cleanup) {
                throw new AggregateException("Repository opening and resource cleanup failed.", primary, cleanup);
            }
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
        if (UsesMetaBootstrap) { return CreateMetaBootstrapBranch(branchName, initialState, P0GraphKind.State, parameters); }
        RequireWriter();
        _busy = true;
        bool acquired = false;
        bool delivered = false;
        try {
            ArgumentNullException.ThrowIfNull(initialState);
            ValidateNewName(branchName);
            acquired = _activeBranches.Add(branchName);
            if (!acquired) { throw new InvalidOperationException("Branch already has an active checkout."); }
            var workspace = WorldWorkspace.CreateSnapshot(_resources.States, _resources.Schemas, initialState, LegacyModels);
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
        if (UsesMetaBootstrap) { return CreateMetaBootstrapBranch(branchName, initialEvent, P0GraphKind.Event, parameters); }
        RequireWriter();
        _busy = true;
        bool acquired = false;
        bool delivered = false;
        try {
            ArgumentNullException.ThrowIfNull(initialEvent);
            ValidateNewName(branchName);
            acquired = _activeBranches.Add(branchName);
            if (!acquired) { throw new InvalidOperationException("Branch already has an active checkout."); }
            var workspace = WorldWorkspace.CreateEmpty(_resources.States, _resources.Schemas, LegacyModels);
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
        if (UsesMetaBootstrap) { return CheckoutMetaBootstrap(branchName); }
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
            MutateBranchCore(branchName, source.Address);
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
        if (UsesMetaBootstrap) { return NewMetaBootstrapCheckout(branchName, head); }
        ValidateSelectedGraphParent(head);
        CheckpointAddress? state = NearestStateCore(head);
        // Event-only histories must not even inspect another branch's resident certificate.
        if (state is null) {
            return new BranchCheckout(this, branchName,
                WorldWorkspace.CreateEmpty(_resources.States, _resources.Schemas, LegacyModels), head);
        }
        CheckFrame(state, GraphFrameKind.State);
        ValidateSelectedGraphParent(state);
        RevisionReadSession reads = new(_resources.States, _resources.Schemas, LegacyModels, RestorationStatistics);
        if (PreparedStateReuseEnabled && _preparedState is { } prepared && prepared.Matches(state)) {
            reads.Statistics.PreparedStateHits++;
            if (ImmutableLeafReuseEnabled && !prepared.ImmutableLeavesCollected) {
                // Enabling the experiment after this entry was prepared needs a fresh
                // complete materialization certificate, including callbacks now omitted by reuse.
                using var leafRequirements = LegacyModels.BeginSchemaRequirementCollection();
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
        using var requirements = LegacyModels.BeginSchemaRequirementCollection();
        PreparedGraphSelection selection = GraphReader.PrepareState(reads, state.RevisionAddress, state.RootId);
        // Include every source/current row, even when Upgrade cuts its last incoming edge.
        // Plans and standard checks performed throughout this lexical scope contribute too.
        foreach ((ObjectId id, NormalizedObject row) in selection.Normalized.Objects) {
            LegacyModels.CheckObjectLayout(row.SourceLayout, $"prepared State source object {id.Value}");
            LegacyModels.CheckObjectLayout(row.Current.Layout, $"prepared State current object {id.Value}");
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

    public IReadOnlyList<string> ListBranches() {
        RequireAvailable();
        return UsesMetaBootstrap ? _resources.ListMetaBootstrapBranches() : _resources.ListBranches();
    }
    public CheckpointAddress GetHead(string branchName) { RequireAvailable(); return HeadCore(branchName); }

    /// <summary>Fully materializes the selected logical chain from oldest to newest, including State and Event handles.</summary>
    /// <param name="branchName">The branch whose head is obtained once for this operation.</param>
    /// <returns>A fully materialized, chronological list of handles owned by this open repository.</returns>
    /// <remarks>
    /// Reads the complete ancestor chain of the selected head; excludes physical orphan appends and
    /// records unique to other branches. No domain objects are restored. Taking the last few results
    /// does not avoid full-chain reading or allocation: this is not a paginated or lazy API.
    /// Each traversed graph frame is CRC checked; unopened history is not audited.
    /// </remarks>
    public IReadOnlyList<CheckpointAddress> ReadFrames(string branchName) {
        // Split history reads graph envelopes; legacy history reads combined StateRevision frames.
        // Neither metadata path decodes object bodies.
        RequireAvailable();
        CheckpointAddress head = HeadCore(branchName);
        var frames = new List<CheckpointAddress>();
        var visited = new HashSet<FrameAddress>();
        CheckpointAddress? cursor = head;
        while (cursor is not null) {
            if (!visited.Add(cursor.Address)) { throw new InvalidDataException("Graph business history contains a cycle."); }
            frames.Add(cursor);
            cursor = cursor.Parent is { } parent ? Issue(ReadHistoryCore(parent)) : null;
        }
        frames.Reverse();
        return frames;

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
    /// does not prepare the complete chain. Each visited graph frame incurs a full-frame CRC read.
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
        FrameAddress? cursor = endInclusive.Address;
        List<CheckpointAddress>? buffered = null;
        int bufferedIndex = -1;
        bool prepared = false;
        var visited = new HashSet<FrameAddress>();
        while (true) {
            CheckpointAddress? next;
            _busy = true;
            try {
                if (!prepared) {
                    if (order == HistoryOrder.OldestFirst) { buffered = []; }
                    if (afterExclusive is not null || buffered is not null) {
                        CheckpointAddress? probe = endInclusive;
                        var probeVisited = new HashSet<FrameAddress>();
                        while (probe is not null && probe != afterExclusive) {
                            if (!probeVisited.Add(probe.Address)) { throw new InvalidDataException("Graph business history contains a cycle."); }
                            if (probe.Kind == GraphFrameKind.Event) { buffered?.Add(probe); }
                            probe = probe.Parent is { } parent ? Issue(ReadHistoryCore(parent)) : null;
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
                    : ReadNextEventCore(endInclusive, afterExclusive, ref cursor, visited);
            } finally { EndOperation(); }
            // GraphResources.ReadHistory has already disposed its frame/lease. No repository guard
            // survives the yield, including when the caller stops before completing the range.
            if (next is null) { yield break; }
            yield return next;
        }
    }

    private CheckpointAddress? ReadNextEventCore(CheckpointAddress endInclusive,
        CheckpointAddress? afterExclusive, ref FrameAddress? cursor, HashSet<FrameAddress> visited) {
        while (cursor is { } address && address != afterExclusive?.Address) {
            if (!visited.Add(address)) { throw new InvalidDataException("Graph business history contains a cycle."); }
            CheckpointAddress frame = address == endInclusive.Address ? endInclusive : Issue(ReadHistoryCore(address));
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
            if (UsesMetaBootstrap) { return ReadMetaBootstrapCheckpointCore(address); }
            ValidateSelectedGraphParent(address);
            GraphFrameKind previousKind = address.Kind == GraphFrameKind.Event ? GraphFrameKind.State : GraphFrameKind.Event;
            CheckpointAddress? previousAddress = PreviousCore(address, previousKind);
            if (previousAddress is not null) { ValidateSelectedGraphParent(previousAddress); }
            var session = new RevisionReadSession(_resources.States, _resources.Schemas, LegacyModels);
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
        if (UsesMetaBootstrap) { return ReadMetaBootstrap(frame, kind); }
        RequireAvailable();
        CheckFrame(frame, kind);
        _busy = true;
        try {
            ValidateSelectedGraphParent(frame);
            var session = new RevisionReadSession(_resources.States, _resources.Schemas, LegacyModels);
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
        RequireLegacyBackend();
        CheckFrame(first);
        CheckFrame(second);
        _busy = true;
        try {
            ValidateSelectedGraphParent(first);
            ValidateSelectedGraphParent(second);
            return GraphReader.ReadPair<IDurableObject, IDurableObject>(_resources.States, _resources.Schemas,
                first.RevisionAddress, first.RootId, second.RevisionAddress, second.RootId, LegacyModels);
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
            MutateBranchCore(branchName, selectedFrame.Address);
            return selectedFrame;
        } finally { EndOperation(); }
    }

    /// <summary>Durably binds an immutable tag name to a State or Event checkpoint.</summary>
    /// <remarks>
    /// Requires a writable repository and an address issued by this open instance. Tags use
    /// a separate ordinal namespace from branches; an existing tag name is always rejected,
    /// even for the same target. Names contain 1..1024 UTF-16 code units and use ordinal comparison.
    /// Branch movement and later commits never move the tag.
    /// No domain graph is materialized and no branch head or checkout baseline changes.
    /// Validation failures preserve their original error. Publication failures report an
    /// explicit GraphCommitException outcome; inspect IsFaulted independently. Dispose and
    /// reopen a faulted repository, then ResolveTag to inspect the actual binding. Do not
    /// blindly retry a creation whose publication may already have succeeded.
    /// </remarks>
    public void CreateTag(string name, CheckpointAddress address) {
        RequireWriter();
        _busy = true;
        try {
            CheckFrame(address);
            CreateTagCore(name, address.Address);
        } finally { EndOperation(); }
    }

    /// <summary>Resolves an immutable tag to a checkpoint owned by this open repository.</summary>
    /// <remarks>
    /// Available on read-only repositories and with an empty model registry; no domain objects
    /// are materialized. Persist the name, then resolve again after reopening: addresses from
    /// an earlier open remain invalid. Names are scoped to the selected repository, not global
    /// identifiers. A missing tag reports TagNotFound; corrupt metadata or targets remain errors.
    /// </remarks>
    public CheckpointAddress ResolveTag(string name) {
        RequireAvailable();
        _busy = true;
        try {
            return Issue(ReadHistoryCore(ResolveTagCore(name)));
        } finally { EndOperation(); }
    }

    private void CreateTagCore(string name, FrameAddress address) {
        if (UsesMetaBootstrap) { CreateMetaBootstrapTagCore(name, address); return; }
        byte[] payload = new byte[FrameAddress.EncodedSize];
        if (!address.TryWrite(payload)) { throw new InvalidDataException("Invalid graph tag address."); }
        PublicationOutcome outcome = PublicationOutcome.NotAttempted;
        try { Checkpoint?.Invoke(CommitCheckpoint.BeforePublication); }
        catch (Exception error) { throw new GraphCommitException(GraphCommitOutcome.NotPublished, null, error); }
        AteliaResult<bool> result;
        try { result = _resources.Versions.CreateTag(name, payload, out outcome); }
        catch (Exception error) {
            _resources.MarkFaulted();
            throw new GraphCommitException(MapOutcome(outcome), null, error);
        }
        result.Unwrap();
        try { Checkpoint?.Invoke(CommitCheckpoint.AfterPublication); }
        catch (Exception error) {
            _resources.MarkFaulted();
            throw new GraphCommitException(GraphCommitOutcome.Published, null, error);
        }
    }

    /// <summary>Explicit compare-and-swap movement; requires closing the target branch's checkout first.</summary>
    /// <remarks>Other branches' active checkouts do not block movement. Both addresses belong to this open repository.</remarks>
    public void MoveBranch(string branchName, CheckpointAddress expectedHead, CheckpointAddress target) {
        RequireWriter();
        _busy = true;
        try {
            CheckFrame(expectedHead);
            CheckFrame(target);
            FrameAddress head = UsesMetaBootstrap ? MetaBootstrapHeadCore(branchName).Address : _resources.GetHead(branchName);
            if (_activeBranches.Contains(branchName)) { throw new InvalidOperationException("Close this branch's active checkout before moving it."); }
            if (head != expectedHead.Address) { throw new InvalidOperationException("Branch head no longer matches expectedHead."); }
            MutateBranchCore(branchName, target.Address);
        } finally { EndOperation(); }
    }

    // The caller owns the complete operation's busy guard, including restoration and delivery.
    private delegate void RootMutation(out PublicationOutcome outcome);
    private void MutateRefCore(RootMutation mutation) {
        PublicationOutcome outcome = PublicationOutcome.NotAttempted;
        try {
            Checkpoint?.Invoke(CommitCheckpoint.BeforePublication);
            mutation(out outcome);
            Checkpoint?.Invoke(CommitCheckpoint.AfterPublication);
        } catch (Exception error) {
            if (outcome != PublicationOutcome.NotAttempted) { _resources.MarkFaulted(); }
            throw new GraphCommitException(MapOutcome(outcome), null, error);
        }
    }

    private static GraphCommitOutcome MapOutcome(PublicationOutcome outcome) => outcome switch {
        PublicationOutcome.Confirmed => GraphCommitOutcome.Published,
        PublicationOutcome.Unknown => GraphCommitOutcome.Unknown,
        _ => GraphCommitOutcome.NotPublished,
    };

    internal CheckpointAddress Commit(BranchCheckout session, IDurableObject? domainEvent,
        IDurableObject? nextState, ReadAmplificationBaseBudgetParameters? parameters) {
        if (UsesMetaBootstrap) { return CommitMetaBootstrap(session, domainEvent, nextState, parameters); }
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
                throw new InvalidOperationException("Checkout State baseline does not match the business chain.");
            }
            return Publish(session, domainEvent, nextState, parameters ?? DefaultPolicy, initial: false);
        } finally { EndOperation(); }
    }

    private CheckpointAddress Publish(BranchCheckout session, IDurableObject? domainEvent,
        IDurableObject? nextState, ReadAmplificationBaseBudgetParameters parameters, bool initial) {
        FrameAddress? address = null;
        bool writeAttempted = false;
        bool faultOnFailure = false;
        PublicationOutcome outcome = PublicationOutcome.NotAttempted;
        try {
            using PreparedWorldSave pending = domainEvent is null
                ? session.Workspace.Stage(nextState!, parameters) : session.Workspace.StageSnapshot(domainEvent, parameters);
            Checkpoint?.Invoke(CommitCheckpoint.AfterPrepare);
            GraphFrameKind kind = domainEvent is null ? GraphFrameKind.State : GraphFrameKind.Event;
            FrameAddress? parent = initial ? null : session.Head.Address;
            if (parent is { } existing) { _resources.ReadHistory(existing); }
            byte[] metadata = GraphMetadataCodec.Encode(kind, parent, pending.RootId);
            Checkpoint?.Invoke(CommitCheckpoint.BeforeStateAppend);
            writeAttempted = true;
            faultOnFailure = true;
            try { address = _resources.States.Append(pending.Revision, metadata); }
            catch {
                // Capacity/semantic rejections before backing output are ordinary failures.
                faultOnFailure = _resources.IsFaulted;
                throw;
            }
            Checkpoint?.Invoke(CommitCheckpoint.AfterStateAppend);
            if (domainEvent is null) { pending.PrepareInstall(address.Value); }
            CheckpointAddress frame = Issue(new(address.Value, parent, kind, pending.RootId));
            // Stage can publish Schema; derive this branch update from the owner's current root.
            if (!initial && _resources.GetHead(session.BranchName) != session.Head.Address) {
                throw new InvalidOperationException("Branch head no longer matches the checkout.");
            }
            faultOnFailure = false;
            GraphResources.RootUpdate update = _resources.PrepareBranchRoot(session.BranchName, address.Value);
            faultOnFailure = true;
            Checkpoint?.Invoke(CommitCheckpoint.BeforePublication);
            faultOnFailure = false;
            _resources.PublishRoot(update, out outcome);
            faultOnFailure = true;
            Checkpoint?.Invoke(CommitCheckpoint.AfterPublication);
            Checkpoint?.Invoke(CommitCheckpoint.BeforeInstall);
            if (domainEvent is null) { pending.Install(); }
            session.Head = frame;
            return frame;
        } catch (Exception error) {
            if (faultOnFailure || outcome != PublicationOutcome.NotAttempted || _resources.Schemas.IsFaulted) { _resources.MarkFaulted(); }
            // A Schema registration can fail independently, before business publication begins.
            if (!writeAttempted && address is null) { throw; }
            throw new GraphCommitException(MapOutcome(outcome), address, error);
        }
    }

    private CheckpointAddress HeadCore(string branchName) {
        if (UsesMetaBootstrap) { return MetaBootstrapHeadCore(branchName); }
        return Issue(_resources.ReadHistory(_resources.GetHead(branchName)));
    }

    private CheckpointAddress? PreviousStateCore(CheckpointAddress frame) => PreviousCore(frame, GraphFrameKind.State);

    private CheckpointAddress? PreviousCore(CheckpointAddress frame, GraphFrameKind kind) {
        var visited = new HashSet<FrameAddress> { frame.Address };
        while (frame.Parent is { } parent) {
            if (!visited.Add(parent)) { throw new InvalidDataException("Graph business history contains a cycle."); }
            frame = Issue(ReadHistoryCore(parent));
            if (frame.Kind == kind) { return frame; }
        }
        return null;
    }

    private CheckpointAddress? NearestStateCore(CheckpointAddress frame) {
        var visited = new HashSet<FrameAddress> { frame.Address };
        while (frame.Kind != GraphFrameKind.State) {
            if (frame.Parent is not { } parent) { return null; }
            if (!visited.Add(parent)) { throw new InvalidDataException("Graph business history contains a cycle."); }
            frame = Issue(ReadHistoryCore(parent));
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
        RepositoryRootCodec.ValidateBranchName(name);
        if (UsesMetaBootstrap ? _splitRepository!.GetHead(name) is not null : _resources.HasBranch(name)) {
            throw new InvalidOperationException("Branch name already exists.");
        }
    }

    // Typed graph preparation, rather than open or metadata navigation, checks this relation.
    private void ValidateSelectedGraphParent(CheckpointAddress frame) {
        FrameAddress? expectedParent = PreviousCore(frame, GraphFrameKind.State)?.RevisionAddress;
        StateRevision revision = _resources.States.Read(frame.RevisionAddress);
        if (revision.ParentRevisionAddress != expectedParent) {
            throw new InvalidDataException("Graph business history and StateRevision Parent disagree.");
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
        _resources.Dispose();
    }
}
