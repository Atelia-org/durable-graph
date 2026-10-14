using Atelia.FrameStore;
using Atelia.DurableGraph.Persistence;

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
/// Close all checkouts and this repository before reopening to load new registrations. First save supported
/// pending domain changes or preserve/rebuild them in the application; Dispose does not save or roll them back.
/// Successful closures may be reused within an exact Schema environment; callbacks and equality/hash policies
/// must remain semantically stable. Freezing retains delegate/comparer references, not their external mutable state.
/// Independent graph reads use each graph's exact Schema view, not an active checkout's execution environment.
/// Default Shared business saves require an explicit Schema write mode selected when opening. Private branch entry
/// points select their own write destination, and ToShared commits select shared for one operation.
/// Without an explicit choice, business capture is rejected.
/// </remarks>
public sealed partial class Repository : IDisposable {
    private readonly RepositoryResources _resources;
    private readonly object _identity = new();
    private readonly HashSet<string> _activeBranches = new(StringComparer.Ordinal);
    private bool _busy;
    private bool _disposed;
    private PreparedStateRestoration? _preparedState;
    private static readonly ReadAmplificationBaseBudgetParameters DefaultPolicy = new(5, 5);
    internal Action<RepositoryOperationStage>? OperationStageObserver { get; set; }
    internal bool PreparedStateReuseEnabled { get; set; } = true;
    internal bool ImmutableLeafReuseEnabled { get; set; }
    internal GraphReadStatistics? RestorationStatistics { get; set; }
    internal PreparedStateRestoration? PreparedStateEntry => _preparedState;

    public bool IsReadOnly => _resources.IsReadOnly;
    /// <summary>The Open-level Schema write destination, or null when default business commits are not admitted.</summary>
    /// <remarks>Immutable for this open. A missing mode does not make backing resources read-only: reads, checkout restoration, ref-only operations, explicit private branch entry points and ToShared commits remain available.</remarks>
    public SchemaWriteMode? SchemaWriteMode { get; }
    /// <summary>Whether this repository has faulted and must be disposed and reopened before further use.</summary>
    /// <remarks>
    /// Independent of GraphCommitException.Outcome: a NotPublished attempt can still fault the writer.
    /// False does not mean application mutations were rolled back. Recovery must inspect persisted
    /// history through a newly opened repository rather than reuse old domain objects or frame handles.
    /// </remarks>
    public bool IsFaulted => _resources.IsFaulted;
    /// <summary>Creates a repository with frozen code configuration and exact stored Schema views.</summary>
    /// <remarks>Configuration is captured after resource acquisition without eagerly closing unused models. Later builder changes affect only future opens. Rotation controls FrameStore file allocation; the separate cache budget controls owned physical reads. Select a Schema write mode explicitly before saving business graphs; a missing mode permits inspection and ref-only operations.</remarks>
    /// <exception cref="ArgumentNullException">models is null; no directory or file is acquired.</exception>
    public static Repository CreateNew(string path, StateModelRegistry models,
        long rotationThresholdBytes = RepositoryResources.DefaultRotationThresholdBytes,
        long readCacheBudgetBytes = GraphRecordStore.DefaultReadCacheBudgetBytes, SchemaWriteMode? schemaWriteMode = null) =>
        OpenStorage(path, models, create: true, readOnly: false, readCacheBudgetBytes,
            rotationThresholdBytes: rotationThresholdBytes, schemaWriteMode: schemaWriteMode);
    /// <summary>Opens the required repository roots with frozen code configuration and exact stored Schema views.</summary>
    /// <remarks>An empty or partial registry supports inspection; materializing a graph still requires its model capabilities. Select a Schema write mode explicitly before saving business graphs. Without a mode, checkout restoration and ref-only operations remain available.</remarks>
    /// <exception cref="ArgumentNullException">models is null; no directory or file is acquired.</exception>
    public static Repository OpenExisting(string path, StateModelRegistry models,
        long rotationThresholdBytes = RepositoryResources.DefaultRotationThresholdBytes,
        long readCacheBudgetBytes = GraphRecordStore.DefaultReadCacheBudgetBytes, SchemaWriteMode? schemaWriteMode = null) =>
        OpenStorage(path, models, create: false, readOnly: false, readCacheBudgetBytes,
            rotationThresholdBytes: rotationThresholdBytes, schemaWriteMode: schemaWriteMode);
    /// <summary>Opens existing history without writing, using frozen code configuration and each graph's exact Schema view.</summary>
    /// <remarks>An empty or partial registry supports inspection; model capabilities are still required to materialize domain graphs. The cache budget controls owned physical reads.</remarks>
    /// <exception cref="ArgumentNullException">models is null; no directory or file is acquired.</exception>
    public static Repository OpenReadOnlyExisting(string path, StateModelRegistry models,
        long readCacheBudgetBytes = GraphRecordStore.DefaultReadCacheBudgetBytes) =>
        OpenStorage(path, models, create: false, readOnly: true, readCacheBudgetBytes);

    /// <summary>Publishes an initial State and returns a checkout retaining the supplied domain instances.</summary>
    /// <remarks>
    /// Requires a writable repository, an explicit Schema write mode and a new branch name. The root must be nonnull and registered.
    /// Other branches may have active checkouts. Dispose the returned checkout to release its branch.
    /// Failure can occur after publication but before delivery; inspect the outcome and IsFaulted independently.
    /// Reopen a faulted repository to inspect persisted history. Domain mutations are not rolled back.
    /// </remarks>
    public BranchCheckout CreateBranch(string branchName, IDurableObject initialState,
        ReadAmplificationBaseBudgetParameters? parameters = null) =>
        CreateBranchCore(branchName, initialState, StoredGraphKind.State, parameters);

    /// <summary>Publishes an initial Event and returns a checkout with no State.</summary>
    /// <remarks>
    /// Requires a writable repository, an explicit Schema write mode and a new branch name; other branches may have active checkouts.
    /// The Event is captured without installing a State or a saving baseline. No empty-head branch is created.
    /// Publication may succeed before delivery fails; inspect the outcome and reopen a faulted repository.
    /// </remarks>
    public BranchCheckout CreateBranchFromEvent(string branchName, IDurableObject initialEvent,
        ReadAmplificationBaseBudgetParameters? parameters = null) =>
        CreateBranchCore(branchName, initialEvent, StoredGraphKind.Event, parameters);

    /// <summary>Restores the nearest State at the exact branch head, without materializing or replaying Events.</summary>
    /// <remarks>
    /// Requires a writable repository and no active checkout for this branch. Other branches remain available.
    /// A valid history with no State yields State=null. Dispose explicitly to release the branch's occupancy.
    /// An existing State's missing model or restoration failure propagates; there is no older-State fallback.
    /// Restoration runs decoding, Upgrade and materialization, but no business handler. Constructors and field
    /// initializers do not run; application code rebuilds Transient state and interprets its own event progress.
    /// The checkout keeps this starting execution environment across commits. Re-checkout does not reload
    /// model registrations or select the latest shared catalog instead of the nearest State's Schema view.
    /// </remarks>
    public BranchCheckout Checkout(string branchName) => CheckoutCore(branchName);

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

    public IReadOnlyList<string> ListBranches() {
        RequireAvailable();
        return _resources.ListBranches();
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
        // Metadata navigation reads graph envelopes, not directories or object bodies.
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
                            if (probe.Kind == CheckpointKind.Event) { buffered?.Add(probe); }
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
            // RepositoryResources.ReadHistory has already disposed its frame/lease. No repository guard
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
            if (frame.Kind == CheckpointKind.Event) { return frame; }
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
        CheckFrame(eventFrame, CheckpointKind.Event);
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
    public IDurableObject ReadEvent(CheckpointAddress frame) => ReadGraph(frame, CheckpointKind.Event);
    /// <summary>Independently restores the actual domain root of a State snapshot.</summary>
    /// <remarks>
    /// Each call restores its own mutable domain instances. Application code may initialize their
    /// Transient state after delivery, without opening a writer; persisted members remain a historical
    /// snapshot. Constructors and field initializers do not run. This does not install a saving baseline:
    /// use Checkout to continue editing and committing. Strings and application-owned global objects do
    /// not acquire a general deep-copy guarantee.
    /// </remarks>
    public IDurableObject ReadState(CheckpointAddress frame) => ReadGraph(frame, CheckpointKind.State);

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
        try { return ReadHistoryCheckpointCore(address); }
        finally { EndOperation(); }
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
        try { return ReadGraphPairCore(first, second); }
        finally { EndOperation(); }
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

    /// <summary>Explicit compare-and-swap movement; requires closing the target branch's checkout first.</summary>
    /// <remarks>Other branches' active checkouts do not block movement. Both addresses belong to this open repository.</remarks>
    public void MoveBranch(string branchName, CheckpointAddress expectedHead, CheckpointAddress target) {
        RequireWriter();
        _busy = true;
        try {
            CheckFrame(expectedHead);
            CheckFrame(target);
            FrameAddress head = StoredHeadCore(branchName).Address;
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
            OperationStageObserver?.Invoke(RepositoryOperationStage.BeforePublication);
            mutation(out outcome);
            OperationStageObserver?.Invoke(RepositoryOperationStage.AfterPublication);
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
        IDurableObject? nextState, ReadAmplificationBaseBudgetParameters? parameters) =>
        CommitSave(session, domainEvent, nextState, parameters);

    internal CheckpointAddress CommitToShared(BranchCheckout session, IDurableObject? domainEvent,
        IDurableObject? nextState, ReadAmplificationBaseBudgetParameters? parameters) =>
        CommitSave(session, domainEvent, nextState, parameters, toShared: true);

    private CheckpointAddress HeadCore(string branchName) => StoredHeadCore(branchName);

    private CheckpointAddress? PreviousStateCore(CheckpointAddress frame) => PreviousCore(frame, CheckpointKind.State);

    private CheckpointAddress? PreviousCore(CheckpointAddress frame, CheckpointKind kind) {
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
        while (frame.Kind != CheckpointKind.State) {
            if (frame.Parent is not { } parent) { return null; }
            if (!visited.Add(parent)) { throw new InvalidDataException("Graph business history contains a cycle."); }
            frame = Issue(ReadHistoryCore(parent));
        }
        return frame;
    }
    private CheckpointAddress Issue(HistoryGraphRecord record) => new(_identity, record);
    private void CheckFrame(CheckpointAddress frame, CheckpointKind? kind = null) {
        ArgumentNullException.ThrowIfNull(frame);
        if (!ReferenceEquals(frame.Owner, _identity)) { throw new ArgumentException("Frame was issued by another repository instance.", nameof(frame)); }
        if (kind is not null && frame.Kind != kind) { throw new ArgumentException("Frame has the wrong Event/State role.", nameof(frame)); }
    }
    private void ValidateNewName(string name) {
        RepositoryResources.ValidateBranchName(name);
        if (_storageFacade.GetHead(name) is not null) {
            throw new InvalidOperationException("Branch name already exists.");
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
    private void RequireSchemaWriter(BranchCheckout? checkout = null) {
        RequireWriter();
        if (checkout?.UsesPrivateSchemas != true && SchemaWriteMode != global::Atelia.DurableGraph.SchemaWriteMode.Shared) {
            throw new InvalidOperationException("Business saves require an explicit Schema write mode when opening the repository.");
        }
        if (checkout is not null) {
            RequireCheckoutSource(checkout);
            if (!checkout.UsesPrivateSchemas && _storageFacade.Store.ReadMetadata(checkout.Head.Address).SchemaRoot is { } selected) {
                SchemaCatalogView source = SchemaCatalogView.Load(_storageFacade.Store, selected);
                SchemaCatalogView? shared = _storageFacade.SharedSchemaRoot is { } latest
                    ? SchemaCatalogView.Load(_storageFacade.Store, latest) : null;
                // A Shared save cannot silently drop declarations from a private view.
                // Only an already-contained complete view may return to this destination.
                foreach (var (key, schema) in source.Schemas) {
                    if (shared is null || !shared.Schemas.TryGetValue(key, out var accepted)) {
                        throw new InvalidOperationException("The complete selected Schema view is not accepted by the shared catalog.");
                    }
                    if (!accepted.Equals(schema)) { throw new SchemaConflictException(accepted, schema); }
                }
                foreach (var layout in source.ObjectLayouts) {
                    if (shared is null) { throw new InvalidOperationException("The complete selected layout view is not accepted by the shared catalog."); }
                    try { shared.Require(layout); }
                    catch (InvalidDataException error) {
                        throw new InvalidOperationException("The complete selected layout view is not accepted by the shared catalog.", error);
                    }
                }
            }
        }
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
