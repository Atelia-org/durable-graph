using Atelia.FrameStore;
using Atelia.DurableGraph.Persistence;

using Atelia.HeadStore;

namespace Atelia.DurableGraph;

/// <summary>Owns shared data and the HeadStore root publishing branches and immutable tags.</summary>
/// <remarks>
/// Single-threaded, one writer and at most one active checkout per branch. Different branches may have
/// live checkouts together; all repository operations remain serial.
/// Backing stores recover incomplete tails during normal writable opens; there is no transparent publication retry.
/// Persistent IDs are interpreted by the selected repository. Persisted members of read results are historical snapshots.
/// IDs can be stored and reused after reopening the same repository; they are not globally unique capabilities.
/// Operations validate branch membership, commit location and any required State/Event role before use.
/// Independent LoadState/LoadEvent calls allow application-side Transient initialization without a writer.
/// ReadCheckpoint also restores independent graphs. Use OpenWorkingCopy to continue editing and committing.
/// Each open repository freezes its model configuration once. Later registry changes affect future opens only.
/// Close all checkouts and this repository before reopening to load new registrations. First save supported
/// pending domain changes or preserve/rebuild them in the application; Dispose does not save or roll them back.
/// Successful closures may be reused within an exact Schema environment; callbacks and equality/hash policies
/// must remain semantically stable. Freezing retains delegate/comparer references, not their external mutable state.
/// Independent graph reads use each graph's exact Schema view, not an active checkout's execution environment.
/// All branches share one monotonically accepted Schema catalog; historical reads retain their exact Schema pins.
/// </remarks>
public sealed partial class Repository : IDisposable {
    private readonly RepositoryResources _resources;
    private readonly HashSet<BranchId> _activeBranches = new();
    private bool _busy;
    private bool _disposed;
    private PreparedStateRestoration? _preparedState;
    private static readonly ReadAmplificationBaseBudgetParameters DefaultPolicy = new(5, 5);
    internal Action<RepositoryOperationStage>? OperationStageObserver { get; set; }
    internal bool PreparedStateReuseEnabled { get; set; } = true;
    internal bool ImmutableLeafReuseEnabled { get; set; }
    internal GraphReadStatistics? RestorationStatistics { get; set; }
    internal PreparedStateRestoration? PreparedStateEntry => _preparedState;

    /// <summary>Whether this repository was opened without writing or editing capability.</summary>
    public bool IsReadOnly => _resources.IsReadOnly;
    /// <summary>Whether this repository has faulted and must be disposed and reopened before further use.</summary>
    /// <remarks>
    /// Independent of RepositoryPublicationException.Outcome: a NotPublished attempt can still fault the writer.
    /// False does not mean application mutations were rolled back. Recovery must inspect persisted
    /// history through a newly opened repository rather than reuse old domain objects or frame handles.
    /// </remarks>
    public bool IsFaulted => _resources.IsFaulted;
    /// <summary>Creates a repository with frozen code configuration and exact stored Schema views.</summary>
    /// <remarks>Configuration is captured after resource acquisition without eagerly closing unused models. Later builder changes affect only future opens. Rotation controls FrameStore file allocation; the separate cache budget controls owned physical reads. Opening resources does not accept business Schemas; business commits accept definitions into the shared catalog.</remarks>
    /// <exception cref="ArgumentNullException">models is null; no directory or file is acquired.</exception>
    public static Repository CreateNew(string path, StateModelRegistry models,
        long rotationThresholdBytes = RepositoryResources.DefaultRotationThresholdBytes,
        long readCacheBudgetBytes = GraphRecordStore.DefaultReadCacheBudgetBytes) =>
        OpenStorage(path, models, create: true, readOnly: false, readCacheBudgetBytes,
            rotationThresholdBytes: rotationThresholdBytes);
    /// <summary>Opens the required repository roots with frozen code configuration and exact stored Schema views.</summary>
    /// <remarks>An empty or partial registry supports inspection; materializing a graph still requires its model capabilities. Opening resources does not accept business Schemas. OpenWorkingCopy restores an editing session; its commits use the shared catalog.</remarks>
    /// <exception cref="ArgumentNullException">models is null; no directory or file is acquired.</exception>
    public static Repository OpenExisting(string path, StateModelRegistry models,
        long rotationThresholdBytes = RepositoryResources.DefaultRotationThresholdBytes,
        long readCacheBudgetBytes = GraphRecordStore.DefaultReadCacheBudgetBytes) =>
        OpenStorage(path, models, create: false, readOnly: false, readCacheBudgetBytes,
            rotationThresholdBytes: rotationThresholdBytes);
    /// <summary>Opens existing history without writing, using frozen code configuration and each graph's exact Schema view.</summary>
    /// <remarks>An empty or partial registry supports inspection; model capabilities are still required to materialize domain graphs. The cache budget controls owned physical reads.</remarks>
    /// <exception cref="ArgumentNullException">models is null; no directory or file is acquired.</exception>
    public static Repository OpenReadOnlyExisting(string path, StateModelRegistry models,
        long readCacheBudgetBytes = GraphRecordStore.DefaultReadCacheBudgetBytes) =>
        OpenStorage(path, models, create: false, readOnly: true, readCacheBudgetBytes);

    /// <summary>Publishes the first State and registers a new root branch.</summary>
    /// <remarks>
    /// No preceding Event is required. Labels need not be unique; the returned ID identifies the branch.
    /// The branch is durable on normal return. OpenWorkingCopy separately restores new domain instances for editing;
    /// this call does not retain the supplied root as a working copy. Summary bytes are copied; omission stores empty bytes.
    /// Publication outcome and IsFaulted are independent; a failed call does not imply that nothing was published.
    /// </remarks>
    public BranchInfo CreateRootBranchFromState(string displayLabel, IDurableObject initialState,
        ReadOnlyMemory<byte> summary = default, ReadAmplificationBaseBudgetParameters? parameters = null) =>
        CreateBranchCore(displayLabel, initialState, StoredGraphKind.State, summary, parameters);

    /// <summary>Publishes the first Event and registers a new root branch without a saved State.</summary>
    /// <remarks>
    /// The branch is durable on normal return; labels need not be unique. OpenWorkingCopy separately acquires editing
    /// capability with State absent until explicitly committed. No Event handlers are replayed automatically.
    /// The Event owns its complete reachable graph as Base records with local identities and no State content source.
    /// Summary bytes are copied; omission stores empty bytes. Publication outcome and IsFaulted are independent.
    /// </remarks>
    public BranchInfo CreateRootBranchFromEvent(string displayLabel, IDurableObject initialEvent,
        ReadOnlyMemory<byte> summary = default) =>
        CreateBranchCore(displayLabel, initialEvent, StoredGraphKind.Event, summary, null);

    /// <summary>Acquires this branch for editing and restores its nearest saved State as new domain instances.</summary>
    /// <remarks>
    /// Requires a writable repository and no other live working copy for this branch. State is null if the selected
    /// history contains only Events; Events are never replayed. An inherited current commit does not change the target
    /// branch for future commits. Dispose the working copy to release occupancy; restoration failure also releases it.
    /// </remarks>
    public WorkingCopy OpenWorkingCopy(BranchId branchId) => CheckoutCore(branchId);

    /// <summary>Lists registered business branches and their fixed creation origins without restoring domain graphs.</summary>
    /// <remarks>
    /// Internal management and unregistered heads are excluded. Labels may repeat; no ordering is promised.
    /// Reads the whole registered forest and validates current/source entry metadata and creation origins.
    /// Business model registration is not required for this query; malformed metadata or origins still fail.
    /// </remarks>
    public BranchInfo[] ListBranches() {
        RequireAvailable();
        _busy = true;
        try { return ReadBranchForestCore(); }
        finally { EndOperation(); }
    }

    /// <summary>Lists registered branches that have no fork source.</summary>
    /// <remarks>
    /// Reads and validates the whole registered forest, including current/source entry metadata, without
    /// restoring domain objects or requiring business model registrations. Internal and unregistered heads
    /// are excluded. Missing or corrupt parents and cycles fail instead of being treated as roots.
    /// Labels may repeat and result ordering is unspecified.
    /// </remarks>
    public BranchInfo[] ListRootBranches() {
        RequireAvailable();
        _busy = true;
        try { return ReadBranchForestCore().Where(static branch => branch.ForkedFromCommitId is null).ToArray(); }
        finally { EndOperation(); }
    }

    /// <summary>Lists the selected registered branch and all branches descended from it through fixed fork origins.</summary>
    /// <param name="branchId">A registered business branch in this repository; an unknown or default ID fails.</param>
    /// <remarks>
    /// Includes the starting branch and forks with no local commits. Parentage follows the branch on which
    /// the fork source commit was published, even if that commit was selected through another branch.
    /// Reads and validates the whole registered forest; it does not restrict disk access to the requested
    /// subtree. Temporary adjacency construction and traversal use linear memory and work in the number
    /// of registered branches, apart from underlying metadata reads. No persistent index is created.
    /// Does not restore domain objects or require business model registrations. Invalid origins anywhere
    /// in the registered forest fail the query. Labels may repeat and result ordering is unspecified.
    /// </remarks>
    public BranchInfo[] ListBranchSubtree(BranchId branchId) {
        RequireAvailable();
        _busy = true;
        try {
            _ = _resources.GetBranchLabel(branchId.Value);
            BranchInfo[] branches = ReadBranchForestCore();
            Dictionary<BranchId, List<BranchInfo>> children = [];
            BranchInfo? start = null;
            foreach (BranchInfo branch in branches) {
                if (branch.Id == branchId) { start = branch; }
                if (branch.ForkedFromCommitId is not { } source) { continue; }
                BranchId parent = new(source.Value.HeadId);
                if (!children.TryGetValue(parent, out var siblings)) { children.Add(parent, siblings = []); }
                siblings.Add(branch);
            }
            if (start is null) { throw new InvalidDataException("The registered branch is absent from the validated forest."); }
            List<BranchInfo> result = [];
            Stack<BranchInfo> pending = new();
            pending.Push(start);
            while (pending.TryPop(out BranchInfo? branch)) {
                result.Add(branch);
                if (children.TryGetValue(branch.Id, out var descendants)) {
                    foreach (BranchInfo child in descendants) { pending.Push(child); }
                }
            }
            return result.ToArray();
        } finally { EndOperation(); }
    }

    // Each public list owns one operation guard; this shared reader never reenters the public facade.
    private BranchInfo[] ReadBranchForestCore() {
        BranchInfo[] branches = _resources.ListBranches().Select(pair => ReadBranchInfoCore(new(pair.Key), pair.Value)).ToArray();
        ValidateBranchForest(branches);
        return branches;
    }

    // A single-target header query does not validate the declared forest. Check only
    // the registered set already read for this list, without touching other Heads.
    private static void ValidateBranchForest(BranchInfo[] branches) {
        var byId = branches.ToDictionary(branch => branch.Id);
        Dictionary<BranchId, byte> marks = [];
        List<BranchId> path = [];
        foreach (BranchInfo start in branches) {
            path.Clear();
            BranchInfo? current = start;
            while (current is not null) {
                if (marks.TryGetValue(current.Id, out byte mark)) {
                    if (mark == 1) { throw new InvalidDataException("The registered branch origins contain a cycle."); }
                    break;
                }
                marks.Add(current.Id, 1);
                path.Add(current.Id);
                if (current.ForkedFromCommitId is not { } source) { break; }
                if (!byId.TryGetValue(new(source.Value.HeadId), out current)) {
                    throw new InvalidDataException("A branch origin refers to an unregistered parent.");
                }
            }
            foreach (BranchId id in path) { marks[id] = 2; }
        }
    }

    /// <summary>Reads the registered branch's label and fixed creation origin without restoring a domain graph.</summary>
    /// <remarks>The ID must identify a business branch in this repository; labels are not lookup keys.</remarks>
    public BranchInfo ReadBranchInfo(BranchId branchId) {
        RequireAvailable();
        _busy = true;
        try { return ReadBranchInfoCore(branchId); }
        finally { EndOperation(); }
    }

    private BranchInfo ReadBranchInfoCore(BranchId branchId) =>
        ReadBranchInfoCore(branchId, _resources.GetBranchLabel(branchId.Value));

    private BranchInfo ReadBranchInfoCore(BranchId branchId, string label) {
        var origin = _resources.ReadOrigin(branchId.Value);
        return new(branchId, label, origin is { } source ? new(source) : null);
    }

    /// <summary>Reads the selected branch's current commit and preview summary without restoring its domain graph.</summary>
    /// <remarks>A fork with no local saves inherits its source commit; the returned BranchId remains the selected branch.</remarks>
    public CurrentCommit ReadCurrentCommit(BranchId branchId) {
        RequireAvailable();
        _busy = true;
        try { return new(branchId, Issue(_resources.ReadCurrent(branchId.Value))); }
        finally { EndOperation(); }
    }

    /// <summary>Reads the exact saved commit's role and preview summary without restoring its domain graph.</summary>
    /// <remarks>The persistent ID must locate a commit on a registered business branch in this repository.</remarks>
    public CommitInfo ReadCommitInfo(CommitId commitId) {
        RequireAvailable();
        _busy = true;
        try { return Issue(CheckCommit(commitId)); }
        finally { EndOperation(); }
    }

    /// <summary>Reads all State and Event descriptors up to this branch's current commit, oldest first.</summary>
    /// <remarks>Includes inherited history, without a synthetic commit for the fork itself. Results do not track later saves.</remarks>
    public IReadOnlyList<CommitInfo> ReadFrames(BranchId branchId) {
        RequireAvailable();
        _busy = true;
        try {
            var tip = _resources.ReadCurrent(branchId.Value);
            return _resources.ReadHistory(tip.Id).Reverse().Select(Issue).ToArray();
        } finally { EndOperation(); }
    }

    /// <summary>Enumerates Event descriptors through a fixed inclusive end, optionally after an exclusive ancestor.</summary>
    /// <remarks>
    /// State commits are skipped; no domain graph is restored or replayed. A supplied lower bound must be an ancestor
    /// of the end, or equal to it for an empty result. It is checked before the first item is yielded.
    /// The repository must remain open and healthy throughout enumeration. Oldest-first enumeration buffers results.
    /// </remarks>
    public IEnumerable<CommitInfo> EnumerateEvents(CommitId endInclusive,
        HistoryOrder order = HistoryOrder.NewestFirst, CommitId? afterExclusive = null) {
        RequireAvailable();
        CheckCommit(endInclusive);
        if (afterExclusive is { } lower) { CheckCommit(lower); }
        if (order is not HistoryOrder.OldestFirst and not HistoryOrder.NewestFirst) {
            throw new ArgumentOutOfRangeException(nameof(order));
        }
        return new EventHistoryEnumerable(this, endInclusive, order, afterExclusive);
    }

    private IEnumerable<CommitInfo> EnumerateEventsCore(CommitId endInclusive,
        HistoryOrder order, CommitId? afterExclusive) {
        HeadEntryId? cursor = endInclusive.Value;
        HeadEntryId? previousOf = null;
        List<CommitInfo>? buffered = null;
        int bufferedIndex = -1;
        bool prepared = false;
        var visited = new HashSet<HeadEntryId>();
        while (true) {
            CommitInfo? next = null;
            _busy = true;
            try {
                if (!prepared) {
                    if (order == HistoryOrder.OldestFirst) { buffered = []; }
                    if (afterExclusive is not null || buffered is not null) {
                        HistoryGraphRecord? probe = CheckCommit(endInclusive);
                        var probeVisited = new HashSet<HeadEntryId>();
                        while (probe is not null && probe.Id != afterExclusive?.Value) {
                            if (!probeVisited.Add(probe.Id)) { throw new InvalidDataException("History contains a cycle."); }
                            if (probe.Kind == CommitKind.Event) { buffered?.Add(Issue(probe)); }
                            probe = _resources.ReadPrevious(probe.Id);
                        }
                        if (afterExclusive is not null && probe is null) {
                            throw new ArgumentException("The lower bound is not an ancestor of the selected end.", nameof(afterExclusive));
                        }
                        bufferedIndex = (buffered?.Count ?? 0) - 1;
                    }
                    prepared = true;
                }
                if (buffered is not null) {
                    next = bufferedIndex >= 0 ? buffered[bufferedIndex--] : null;
                } else {
                    while (true) {
                        // Do not touch the predecessor of a yielded Event until the next MoveNext.
                        if (previousOf is { } anchor) { cursor = _resources.ReadPrevious(anchor)?.Id; previousOf = null; }
                        if (cursor is not { } id || id == afterExclusive?.Value) { break; }
                        if (!visited.Add(id)) { throw new InvalidDataException("History contains a cycle."); }
                        var record = _resources.ReadDescriptor(id);
                        previousOf = id;
                        if (record.Kind == CommitKind.Event) { next = Issue(record); break; }
                    }
                }
            } finally { EndOperation(); }
            if (next is null) { yield break; }
            yield return next;
        }
    }

    private sealed class EventHistoryEnumerable(Repository repository, CommitId endInclusive,
        HistoryOrder order, CommitId? afterExclusive) : IEnumerable<CommitInfo> {
        public IEnumerator<CommitInfo> GetEnumerator() => new EventHistoryEnumerator(repository,
            repository.EnumerateEventsCore(endInclusive, order, afterExclusive).GetEnumerator());
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class EventHistoryEnumerator(Repository repository, IEnumerator<CommitInfo> inner) : IEnumerator<CommitInfo> {
        public CommitInfo Current => inner.Current;
        object System.Collections.IEnumerator.Current => Current;
        public bool MoveNext() { repository.RequireAvailable(); return inner.MoveNext(); }
        public void Reset() => throw new NotSupportedException();
        public void Dispose() => inner.Dispose();
    }

    /// <summary>Finds the nearest strict ancestor State of the selected Event, or null before the first State.</summary>
    /// <remarks>Requires an Event commit. The result describes saved history, not application processing progress.</remarks>
    public CommitId? GetPreviousState(CommitId eventCommitId) {
        RequireAvailable();
        _busy = true;
        try {
            var record = CheckCommit(eventCommitId, CommitKind.Event);
            return _resources.FindNearestState(record.Id, inclusive: false) is { } state ? new(state.Id) : null;
        } finally { EndOperation(); }
    }

    /// <summary>Independently restores the exact Event commit's domain root.</summary>
    /// <remarks>Rejects State commits. Supports application-side Transient initialization without a writer; no Event handler is invoked.</remarks>
    public IDurableObject LoadEvent(CommitId eventCommitId) => ReadGraph(eventCommitId, CommitKind.Event);
    /// <summary>Independently restores the exact State commit's domain root.</summary>
    /// <remarks>Rejects Event commits instead of searching backward. Supports application-side Transient initialization without a writer; use OpenWorkingCopy for branch editing.</remarks>
    public IDurableObject LoadState(CommitId stateCommitId) => ReadGraph(stateCommitId, CommitKind.State);

    /// <summary>Independently restores the nearest saved State at or before a State or Event commit.</summary>
    /// <remarks>Returns both the selected position and the actual State position. State and its ID are null when absent; Events are not replayed.</remarks>
    public LoadedState LoadLatestSavedState(CommitId atOrBefore) {
        RequireAvailable();
        _busy = true;
        try {
            CheckCommit(atOrBefore);
            var state = _resources.FindNearestState(atOrBefore.Value, inclusive: true);
            return state is null ? new(atOrBefore, null, null) : new(atOrBefore, new(state.Id), ReadGraphRootCore(state));
        } finally { EndOperation(); }
    }

    /// <summary>Restores the exact selected graph and its nearest strict ancestor of the opposite State/Event role.</summary>
    /// <remarks>The two graphs have independent mutable instances. Their relationship describes history, not an Event's cause or application completion.</remarks>
    public Checkpoint ReadCheckpoint(CommitId commitId) {
        RequireAvailable();
        _busy = true;
        try { return ReadHistoryCheckpointCore(CheckCommit(commitId)); }
        finally { EndOperation(); }
    }

    /// <summary>Registers a new branch inheriting the exact source commit, without creating a duplicate commit.</summary>
    /// <remarks>
    /// Registration does not restore domain objects or acquire editing occupancy. On normal return the branch can be
    /// listed and reopened even before its first local save. OpenWorkingCopy restores its nearest State and commits to
    /// the new branch; the source branch can continue independently. Uncommitted source edits are not included.
    /// Publication outcome and IsFaulted are independent; reopen a faulted repository to inspect persisted results.
    /// </remarks>
    public BranchInfo ForkBranch(CommitId sourceCommitId, string displayLabel) {
        RequireWriter();
        _busy = true;
        PublicationOutcome outcome = PublicationOutcome.NotAttempted;
        HeadId? target = null;
        try {
            CheckCommit(sourceCommitId);
            OperationStageObserver?.Invoke(RepositoryOperationStage.BeforePublication);
            target = _resources.ForkBranch(sourceCommitId.Value, displayLabel, out outcome);
            OperationStageObserver?.Invoke(RepositoryOperationStage.AfterPublication);
            return ReadBranchInfoCore(new(target.Value));
        } catch (RepositoryPublicationException) { throw; }
        catch (Exception error) {
            if (outcome != PublicationOutcome.NotAttempted) {
                _resources.MarkFaulted();
                throw new RepositoryPublicationException(MapOutcome(outcome), null, error, targetBranch: target);
            }
            throw;
        } finally { EndOperation(); }
    }

    /// <summary>Creates an immutable named bookmark for an exact saved commit.</summary>
    /// <remarks>Duplicate names are rejected. Does not move a branch or restore domain graphs. After a publication failure, inspect outcome and IsFaulted independently and resolve the name after reopening.</remarks>
    public void CreateTag(string name, CommitId commitId) {
        RequireWriter();
        _busy = true;
        try { CheckCommit(commitId); CreateTagCore(name, commitId.Value); }
        finally { EndOperation(); }
    }

    /// <summary>Resolves a named bookmark to its persistent commit ID.</summary>
    /// <remarks>Validates the stored location without restoring domain graphs or moving a branch; missing names are rejected.</remarks>
    public CommitId ResolveTag(string name) {
        RequireAvailable();
        _busy = true;
        try { var id = _resources.ResolveStoredTag(name); _resources.ReadDescriptor(id); return new(id); }
        finally { EndOperation(); }
    }

    private static RepositoryPublicationOutcome MapOutcome(PublicationOutcome outcome) => outcome switch {
        PublicationOutcome.Confirmed => RepositoryPublicationOutcome.Published,
        PublicationOutcome.Unknown => RepositoryPublicationOutcome.Unknown,
        _ => RepositoryPublicationOutcome.NotPublished,
    };

    internal CommitInfo Commit(WorkingCopy work, IDurableObject? domainEvent, IDurableObject? nextState,
        ReadOnlyMemory<byte> summary, ReadAmplificationBaseBudgetParameters? parameters) =>
        CommitSave(work, domainEvent, nextState, summary, parameters);

    private static CommitInfo Issue(HistoryGraphRecord record) => new(new(record.Id), record.Kind, record.Summary);
    private HistoryGraphRecord CheckCommit(CommitId id, CommitKind? kind = null) {
        var record = _resources.ReadDescriptor(id.Value);
        if (kind is not null && record.Kind != kind) { throw new ArgumentException("Commit has the wrong State/Event role.", nameof(id)); }
        return record;
    }

    internal void Release(WorkingCopy work) {
        if (_busy) { throw new InvalidOperationException("Cannot dispose a working copy during a repository operation."); }
        if (work.IsLiveFor(this)) { _activeBranches.Remove(work.BranchId); }
    }
    private void EndOperation() {
        _busy = false;
        if (_resources.IsFaulted) { _preparedState = null; }
    }
    private void RequireWriter() { RequireAvailable(); _resources.RequireWritable(); }
    private void RequireCheckoutWriter(WorkingCopy work) { RequireWriter(); RequireCheckoutSource(work); }
    private void RequireAvailable() {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_resources.IsFaulted) { _preparedState = null; }
        _resources.RequireAvailable();
        if (_busy) { throw new InvalidOperationException("Repository operations cannot be reentered."); }
    }
    /// <summary>Closes repository resources without saving uncommitted domain changes.</summary>
    /// <remarks>Idempotent; disposal during an active repository operation is rejected. Working copies cannot commit after disposal.</remarks>
    public void Dispose() {
        if (_disposed) { return; }
        if (_busy) { throw new InvalidOperationException("Cannot dispose a busy repository."); }
        _disposed = true;
        _preparedState = null;
        _activeBranches.Clear();
        _resources.Dispose();
    }
}
