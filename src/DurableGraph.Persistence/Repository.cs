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
/// Independent LoadState/LoadEvent calls allow application-side Transient initialization without a writer.
/// ReadPair may share reachable instances: its read-only constraint includes observable Transient mutation.
/// Keep per-view owner/context/cache outside paired graphs. Use OpenWorkingCopy to continue editing and committing.
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

    public BranchInfo CreateRootBranchFromState(string displayLabel, IDurableObject initialState,
        ReadOnlyMemory<byte> summary = default, ReadAmplificationBaseBudgetParameters? parameters = null) =>
        CreateBranchCore(displayLabel, initialState, StoredGraphKind.State, summary, parameters);

    public BranchInfo CreateRootBranchFromEvent(string displayLabel, IDurableObject initialEvent,
        ReadOnlyMemory<byte> summary = default, ReadAmplificationBaseBudgetParameters? parameters = null) =>
        CreateBranchCore(displayLabel, initialEvent, StoredGraphKind.Event, summary, parameters);

    public WorkingCopy OpenWorkingCopy(BranchId branchId) => CheckoutCore(branchId);

    public BranchInfo[] ListBranches() {
        RequireAvailable();
        _busy = true;
        try { return _resources.ListBranches().Select(pair => ReadBranchInfoCore(new(pair.Key))).ToArray(); }
        finally { EndOperation(); }
    }

    public BranchInfo ReadBranchInfo(BranchId branchId) {
        RequireAvailable();
        _busy = true;
        try { return ReadBranchInfoCore(branchId); }
        finally { EndOperation(); }
    }

    private BranchInfo ReadBranchInfoCore(BranchId branchId) {
        if (!_resources.ListBranches().TryGetValue(branchId.Value, out string? label)) {
            throw new KeyNotFoundException("Branch is not registered in this repository.");
        }
        // Isolated B2 adapter. Formal merge requires a bounded target-origin query upstream.
        var origin = _resources.ReadOrigin(branchId.Value);
        return new(branchId, label, origin is { } source ? new(source) : null);
    }

    public CurrentCommit ReadCurrentCommit(BranchId branchId) {
        RequireAvailable();
        _busy = true;
        try { return new(branchId, Issue(_resources.ReadCurrent(branchId.Value))); }
        finally { EndOperation(); }
    }

    public CommitInfo ReadCommitInfo(CommitId commitId) {
        RequireAvailable();
        _busy = true;
        try { return Issue(CheckCommit(commitId)); }
        finally { EndOperation(); }
    }

    public IReadOnlyList<CommitInfo> ReadFrames(BranchId branchId) {
        RequireAvailable();
        _busy = true;
        try {
            var tip = _resources.ReadCurrent(branchId.Value);
            return _resources.ReadHistory(tip.Id).Reverse().Select(Issue).ToArray();
        } finally { EndOperation(); }
    }

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

    public CommitId? GetPreviousState(CommitId eventCommitId) {
        RequireAvailable();
        _busy = true;
        try {
            var record = CheckCommit(eventCommitId, CommitKind.Event);
            return _resources.FindNearestState(record.Id, inclusive: false) is { } state ? new(state.Id) : null;
        } finally { EndOperation(); }
    }

    public IDurableObject LoadEvent(CommitId eventCommitId) => ReadGraph(eventCommitId, CommitKind.Event);
    public IDurableObject LoadState(CommitId stateCommitId) => ReadGraph(stateCommitId, CommitKind.State);

    public LoadedState LoadLatestSavedState(CommitId atOrBefore) {
        RequireAvailable();
        _busy = true;
        try {
            CheckCommit(atOrBefore);
            var state = _resources.FindNearestState(atOrBefore.Value, inclusive: true);
            return state is null ? new(atOrBefore, null, null) : new(atOrBefore, new(state.Id), ReadGraphRootCore(state));
        } finally { EndOperation(); }
    }

    public Checkpoint ReadCheckpoint(CommitId commitId) {
        RequireAvailable();
        _busy = true;
        try { return ReadHistoryCheckpointCore(CheckCommit(commitId)); }
        finally { EndOperation(); }
    }

    /// <summary>Returns a pair in input order. Both graphs, including Transient fields, must be treated as read-only.</summary>
    public (IDurableObject First, IDurableObject Second) ReadPair(CommitId first, CommitId second) {
        RequireAvailable();
        _busy = true;
        try { return ReadGraphPairCore(CheckCommit(first), CheckCommit(second)); }
        finally { EndOperation(); }
    }

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

    public void CreateTag(string name, CommitId commitId) {
        RequireWriter();
        _busy = true;
        try { CheckCommit(commitId); CreateTagCore(name, commitId.Value); }
        finally { EndOperation(); }
    }

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
    public void Dispose() {
        if (_disposed) { return; }
        if (_busy) { throw new InvalidOperationException("Cannot dispose a busy repository."); }
        _disposed = true;
        _preparedState = null;
        _activeBranches.Clear();
        _resources.Dispose();
    }
}
