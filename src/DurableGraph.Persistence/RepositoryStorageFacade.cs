using Atelia.FrameStore;
using DataStore = Atelia.FrameStore.FrameStore;
using PublicationStore = Atelia.VersionStore.VersionStore;

namespace Atelia.DurableGraph.Persistence;

// Thin storage facade. RepositoryResources owns backing resources, roots and the save lifecycle.
internal sealed class RepositoryStorageFacade : IDisposable {
    private readonly RepositoryResources _resources;
    private readonly bool _ownsResources;
    private bool _disposed;

    // An explicitly borrowed facade does not close or finish its owner's pending save.
    internal RepositoryStorageFacade(RepositoryResources resources) : this(resources, ownsResources: false) { }

    private RepositoryStorageFacade(RepositoryResources resources, bool ownsResources) {
        ArgumentNullException.ThrowIfNull(resources);
        resources.RequireStorageAvailable();
        _resources = resources;
        _ownsResources = ownsResources;
    }

    internal DataStore Data { get { RequireAvailable(); return _resources.Data; } }
    internal PublicationStore Versions { get { RequireAvailable(); return _resources.Versions; } }
    internal GraphRecordStore Store { get { RequireAvailable(); return _resources.GraphStore; } }
    internal Action<RepositorySaveStage>? SaveStageObserver {
        get => _resources.SaveStageObserver;
        set => _resources.SaveStageObserver = value;
    }
    internal bool IsFaulted => _resources.IsStorageFaulted;
    internal bool HasPending { get { RequireAvailable(); return _resources.HasPendingSave; } }
    internal FrameAddress? SharedSchemaRoot { get { RequireAvailable(); return _resources.SharedSchemaRoot; } }

    internal static RepositoryStorageFacade CreateNew(string path) =>
        new(RepositoryResources.CreateStorageNew(path), ownsResources: true);
    internal static RepositoryStorageFacade OpenExisting(string path) =>
        new(RepositoryResources.OpenStorageExisting(path), ownsResources: true);
    internal static RepositoryStorageFacade OpenReadOnlyExisting(string path) =>
        new(RepositoryResources.OpenStorageReadOnlyExisting(path), ownsResources: true);

    internal FrameAddress? GetHead(string branch) { RequireAvailable(); return _resources.GetBranchHead(branch); }
    internal FrameAddress? GetStateHead(string branch) { RequireAvailable(); return _resources.GetBranchStateHead(branch); }
    internal void EnsureEditingAllowed() { RequireAvailable(); _resources.EnsureEditingAllowed(); }

    internal PendingRepositorySave Prepare(string branch, StoredGraphKind kind,
        PreparedGraphWrite business, PreparedGraphWrite? catalog = null) {
        RequireAvailable();
        return _resources.PrepareSave(branch, kind, business, catalog);
    }

    internal PendingRepositorySave PrepareFrom(string branch, StoredGraphKind kind,
        Func<(PreparedGraphWrite Business, PreparedGraphWrite? Catalog)> produce) {
        RequireAvailable();
        return _resources.PrepareSaveFrom(branch, kind, produce);
    }

    internal PendingRepositorySave PreparePrivateFrom(string branch, StoredGraphKind kind,
        Func<(PreparedGraphWrite Business, PreparedGraphWrite? Catalog)> produce) {
        RequireAvailable();
        return _resources.PreparePrivateSaveFrom(branch, kind, produce);
    }

    internal RepositorySaveResult Commit(PendingRepositorySave pending,
        Action<StoredGraphRevision>? installMeta = null, Action<StoredGraphRevision>? installState = null,
        Action<StoredGraphRevision>? prepareMeta = null, Action<StoredGraphRevision>? prepareState = null,
        Action<StoredGraphRevision>? prepareBusinessDelivery = null) {
        RequireAvailable();
        return _resources.CommitSave(pending, installMeta, installState, prepareMeta, prepareState, prepareBusinessDelivery);
    }

    internal void Discard(PendingRepositorySave pending) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _resources.DiscardSave(pending);
    }

    private void RequireAvailable() {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _resources.RequireStorageAvailable();
    }

    public void Dispose() {
        if (_disposed) { return; }
        _resources.RequirePreparationInactive();
        if (_ownsResources) {
            try { _resources.Dispose(); }
            finally { _disposed = true; }
        } else { _disposed = true; }
    }
}
