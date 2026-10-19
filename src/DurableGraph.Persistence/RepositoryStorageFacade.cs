using Atelia.FrameStore;
using Atelia.HeadStore;
using DataStore = Atelia.FrameStore.FrameStore;
using PublicationStore = Atelia.HeadStore.HeadStore;

namespace Atelia.DurableGraph.Persistence;

// Thin borrowed facade; the resources owner retains the only save lifecycle.
internal sealed class RepositoryStorageFacade : IDisposable {
    private readonly RepositoryResources _resources;
    private readonly bool _ownsResources;
    private bool _disposed;
    internal RepositoryStorageFacade(RepositoryResources resources) : this(resources, false) { }
    private RepositoryStorageFacade(RepositoryResources resources, bool ownsResources) {
        ArgumentNullException.ThrowIfNull(resources);
        resources.RequireStorageAvailable();
        _resources = resources;
        _ownsResources = ownsResources;
    }
    internal DataStore Data { get { RequireAvailable(); return _resources.Data; } }
    internal PublicationStore Heads { get { RequireAvailable(); return _resources.Heads; } }
    internal GraphRecordStore Store { get { RequireAvailable(); return _resources.GraphStore; } }
    internal Action<RepositorySaveStage>? SaveStageObserver { get => _resources.SaveStageObserver; set => _resources.SaveStageObserver = value; }
    internal bool IsFaulted => _resources.IsStorageFaulted;
    internal bool HasPending { get { RequireAvailable(); return _resources.HasPendingSave; } }
    internal FrameAddress? SharedSchemaRoot { get { RequireAvailable(); return _resources.SharedSchemaRoot; } }
    internal static RepositoryStorageFacade CreateNew(string path) => new(RepositoryResources.CreateStorageNew(path), true);
    internal static RepositoryStorageFacade OpenExisting(string path) => new(RepositoryResources.OpenStorageExisting(path), true);
    internal static RepositoryStorageFacade OpenReadOnlyExisting(string path) => new(RepositoryResources.OpenStorageReadOnlyExisting(path), true);
    internal void EnsureEditingAllowed() { RequireAvailable(); _resources.EnsureEditingAllowed(); }
    internal HistoryGraphRecord ReadCurrent(HeadId branch) { RequireAvailable(); return _resources.ReadCurrent(branch); }
    internal HistoryGraphRecord? FindNearestState(HeadEntryId id, bool inclusive) { RequireAvailable(); return _resources.FindNearestState(id, inclusive); }
    internal PendingRepositorySave PrepareFrom(HeadId? branch, HeadEntryId? expected, string? rootLabel,
        StoredGraphKind kind, ReadOnlyMemory<byte> summary, Func<(PreparedGraphWrite Business, PreparedGraphWrite? Catalog)> produce, Action? abort = null) {
        RequireAvailable();
        return _resources.PrepareSaveFrom(branch, expected, rootLabel, kind, summary, produce, abort);
    }
    internal RepositorySaveResult Commit(PendingRepositorySave pending,
        Action<StoredGraphRevision>? installMeta = null, Action<StoredGraphRevision>? installState = null,
        Action<StoredGraphRevision>? prepareMeta = null, Action<StoredGraphRevision>? prepareState = null,
        Action<StoredGraphRevision>? prepareBusinessDelivery = null, Action<RepositorySaveResult>? deliver = null, Action? cleanup = null) {
        RequireAvailable();
        return _resources.CommitSave(pending, installMeta, installState, prepareMeta, prepareState, prepareBusinessDelivery, deliver, cleanup);
    }
    internal SchemaCatalogWorkspace CatalogForPreparation { get { RequireAvailable(); return _resources.CatalogForPreparation; } }
    internal void EnsureSaveUsable() { RequireAvailable(); _resources.EnsureSaveUsable(); }
    internal PendingRepositorySave PrepareGroup(Func<PreparedSaveGroup> produce, Action? abort = null) {
        RequireAvailable();
        return _resources.PrepareSaveGroup(produce, abort);
    }
    internal IReadOnlyList<RepositorySaveResult> CommitGroup(PendingRepositorySave pending,
        Action<StoredGraphRevision>? installMeta, Action<StoredGraphRevision>? prepareMeta,
        Action<int, StoredGraphRevision>? prepareState, Action<int, StoredGraphRevision>? installState,
        Action<IReadOnlyList<RepositorySaveResult>>? deliver, Action? cleanup) {
        RequireAvailable();
        return _resources.CommitSaveGroup(pending, installMeta, prepareMeta, prepareState, installState,
            deliver: deliver, cleanup: cleanup);
    }
    internal void AbortCandidates(Action abort) {
        try { abort(); }
        catch { _resources.MarkFaulted(); throw; }
    }
    internal T[] MaterializeInputs<T>(IEnumerable<T> source) {
        var items = new List<T>();
        IEnumerator<T>? iterator = null;
        Exception? primary = null, cleanup = null;
        try {
            iterator = source.GetEnumerator();
            EnsureSaveUsable();
            if (iterator is null) { throw new InvalidOperationException("The input returned no enumerator."); }
            while (true) {
                bool more = iterator.MoveNext();
                EnsureSaveUsable();
                if (!more) { break; }
                T item = iterator.Current;
                EnsureSaveUsable();
                items.Add(item);
            }
        } catch (Exception error) { primary = error; }
        try { iterator?.Dispose(); }
        catch (Exception error) { cleanup = error; }
        try { EnsureSaveUsable(); }
        catch (Exception error) {
            cleanup = cleanup is null ? error : new AggregateException("Input disposal and resource validation failed.", cleanup, error);
        }
        if (primary is not null && cleanup is not null) {
            throw new AggregateException("Input enumeration and cleanup failed.", primary, cleanup);
        }
        if ((primary ?? cleanup) is { } failure) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); }
        return items.ToArray();
    }
    internal void Discard(PendingRepositorySave pending) { ObjectDisposedException.ThrowIf(_disposed, this); _resources.DiscardSave(pending); }
    private void RequireAvailable() { ObjectDisposedException.ThrowIf(_disposed, this); _resources.RequireStorageAvailable(); }
    public void Dispose() {
        if (_disposed) { return; }
        _resources.RequirePreparationInactive();
        if (_ownsResources) {
            try { _resources.Dispose(); } finally { _disposed = true; }
        } else { _disposed = true; }
    }
}
