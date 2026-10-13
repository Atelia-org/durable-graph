using Atelia.FrameStore;
using DataStore = Atelia.FrameStore.FrameStore;
using PublicationStore = Atelia.VersionStore.VersionStore;

namespace Atelia.DurableGraph.Persistence.MetaBootstrapP0;

// Thin experiment entry point. GraphResources owns backing resources, roots and the save lifecycle.
internal sealed class P0Repository : IDisposable {
    private readonly GraphResources _resources;
    private readonly bool _ownsResources;
    private bool _disposed;

    // An explicitly borrowed facade does not close or finish its owner's pending save.
    internal P0Repository(GraphResources resources) : this(resources, ownsResources: false) { }

    private P0Repository(GraphResources resources, bool ownsResources) {
        ArgumentNullException.ThrowIfNull(resources);
        resources.RequireMetaBootstrapAvailable();
        _resources = resources;
        _ownsResources = ownsResources;
    }

    internal DataStore Data { get { RequireAvailable(); return _resources.Data; } }
    internal PublicationStore Versions { get { RequireAvailable(); return _resources.Versions; } }
    internal P0GraphStore Store { get { RequireAvailable(); return _resources.MetaBootstrapStore; } }
    internal Action<P0SaveCheckpoint>? Checkpoint {
        get => _resources.MetaBootstrapCheckpoint;
        set => _resources.MetaBootstrapCheckpoint = value;
    }
    internal bool IsFaulted => _resources.IsMetaBootstrapFaulted;
    internal bool HasPending { get { RequireAvailable(); return _resources.HasMetaBootstrapPending; } }
    internal FrameAddress? SharedSchemaRoot { get { RequireAvailable(); return _resources.SharedMetaBootstrapRoot; } }

    internal static P0Repository CreateNew(string path) =>
        new(GraphResources.CreateMetaBootstrapNew(path), ownsResources: true);
    internal static P0Repository OpenExisting(string path) =>
        new(GraphResources.OpenMetaBootstrapExisting(path), ownsResources: true);
    internal static P0Repository OpenReadOnlyExisting(string path) =>
        new(GraphResources.OpenMetaBootstrapReadOnlyExisting(path), ownsResources: true);

    internal FrameAddress? GetHead(string branch) { RequireAvailable(); return _resources.GetMetaBootstrapHead(branch); }
    internal FrameAddress? GetStateHead(string branch) { RequireAvailable(); return _resources.GetMetaBootstrapStateHead(branch); }
    internal void EnsureEditingAllowed() { RequireAvailable(); _resources.EnsureMetaBootstrapEditingAllowed(); }

    internal P0PendingSave Prepare(string branch, P0GraphKind kind,
        P0PreparedGraphWrite business, P0PreparedGraphWrite? catalog = null) {
        RequireAvailable();
        return _resources.PrepareMetaBootstrap(branch, kind, business, catalog);
    }

    internal P0PendingSave PrepareFrom(string branch, P0GraphKind kind,
        Func<(P0PreparedGraphWrite Business, P0PreparedGraphWrite? Catalog)> produce) {
        RequireAvailable();
        return _resources.PrepareMetaBootstrapFrom(branch, kind, produce);
    }

    internal P0PendingSave PreparePrivateFrom(string branch, P0GraphKind kind,
        Func<(P0PreparedGraphWrite Business, P0PreparedGraphWrite? Catalog)> produce) {
        RequireAvailable();
        return _resources.PreparePrivateMetaBootstrapFrom(branch, kind, produce);
    }

    internal P0SaveResult Commit(P0PendingSave pending,
        Action<P0GraphSnapshot>? installMeta = null, Action<P0GraphSnapshot>? installState = null,
        Action<P0GraphSnapshot>? prepareMeta = null, Action<P0GraphSnapshot>? prepareState = null,
        Action<P0GraphSnapshot>? prepareBusinessDelivery = null) {
        RequireAvailable();
        return _resources.CommitMetaBootstrap(pending, installMeta, installState, prepareMeta, prepareState, prepareBusinessDelivery);
    }

    internal void Discard(P0PendingSave pending) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _resources.DiscardMetaBootstrap(pending);
    }

    private void RequireAvailable() {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _resources.RequireMetaBootstrapAvailable();
    }

    public void Dispose() {
        if (_disposed) { return; }
        _resources.RequireMetaBootstrapPreparationInactive();
        if (_ownsResources) {
            try { _resources.Dispose(); }
            finally { _disposed = true; }
        } else { _disposed = true; }
    }
}
