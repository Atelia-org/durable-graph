using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence;

/// <summary>Thin typed driver. RepositoryResources owns the only pending-save lifecycle; RepositoryStorageFacade forwards it.</summary>
internal sealed class BranchSaveSession {
    private readonly RepositoryStorageFacade _repository;
    private readonly string _branch;
    private readonly BusinessPersistenceAdapter _business;

    internal BranchSaveSession(RepositoryStorageFacade repository, string branch, StateModelRegistry models) {
        _repository = repository;
        _branch = branch;
        SchemaCatalogView? catalog = repository.SharedSchemaRoot is { } schema
            ? SchemaCatalogView.Load(repository.Store, schema) : null;
        Catalog = new(EnsureEditingAllowed, catalog);
        _business = repository.GetStateHead(branch) is { } state
            ? BusinessPersistenceAdapter.LoadState(repository.Store, state, models)
            : new(repository.Store, models);
    }

    internal BranchSaveSession(RepositoryStorageFacade repository, string branch,
        StateModelRegistry.FrozenConfiguration configuration, Action ensureEditingAllowed)
        : this(repository, branch, configuration, ensureEditingAllowed, repository.GetStateHead(branch)) { }

    // The selected business head is owner-checked by Repository before restoring a historical Fork.
    // No target branch is published or consulted to choose the State being restored.
    internal BranchSaveSession(RepositoryStorageFacade repository, string branch, FrameAddress selectedHead,
        StateModelRegistry.FrozenConfiguration configuration, Action ensureEditingAllowed)
        : this(repository, branch, configuration, ensureEditingAllowed,
            repository.Store.ResolveContentSource(StoredGraphKind.State, selectedHead)) { }

    private BranchSaveSession(RepositoryStorageFacade repository, string branch,
        StateModelRegistry.FrozenConfiguration configuration, Action ensureEditingAllowed, FrameAddress? sourceState) {
        _repository = repository;
        _branch = branch;
        FrameAddress? catalogRoot = repository.SharedSchemaRoot;
        SchemaCatalogView? catalog = catalogRoot is { } schema
            ? SchemaCatalogView.Load(repository.Store, schema) : null;
        Catalog = new(ensureEditingAllowed, catalog);
        if (sourceState is { } state) {
            _business = BusinessPersistenceAdapter.LoadState(repository.Store, state, configuration);
        } else {
            DecodedRevision.SchemaViewBindingEnvironment? environment = catalogRoot is { } selected
                ? DecodedRevision.SchemaViewBindingEnvironment.Load(repository.Store, configuration, selected) : null;
            _business = new(repository.Store, environment?.Models ?? configuration.CreateEnvironment(), environment);
        }
    }

    internal BranchSaveSession(RepositoryStorageFacade repository, string branch, BusinessPersistenceAdapter business,
        Action ensureEditingAllowed) {
        if (business.Source is not { } source || !ReferenceEquals(source.SourceOwner, repository.Store)) {
            throw new InvalidDataException("A restored business workspace must belong to this exact repository Store.");
        }
        _repository = repository;
        _branch = branch;
        _business = business;
        FrameAddress? catalogRoot = repository.SharedSchemaRoot;
        SchemaCatalogView? catalog = catalogRoot is { } schema
            ? SchemaCatalogView.Load(repository.Store, schema) : null;
        Catalog = new(ensureEditingAllowed, catalog);
    }

    internal SchemaCatalogWorkspace Catalog { get; }
    internal IDurableObject? State => _business.State;
    internal StoredGraphRevision? StateSource => _business.Source;
    internal ModelExecutionEnvironment Models => _business.Models;
    internal NormalizedGraphState? Baseline => _business.Baseline;
    internal ObjectId RootId => _business.RootId;
    internal DecodedRevision.SchemaViewBindingEnvironment? BindingEnvironment => _business.BindingEnvironment;

    private void EnsureEditingAllowed() {
        _repository.EnsureEditingAllowed();
    }

    internal PreparedBranchSave Prepare(IDurableObject root, StoredGraphKind kind = StoredGraphKind.State,
        ReadAmplificationBaseBudgetParameters? parameters = null) {
        EnsureEditingAllowed();
        BusinessSaveCandidate? capture = null;
        PreparedSchemaCatalogWrite? catalog = null;
        PreparedBusinessWrite? business = null;
        try {
            (PreparedGraphWrite Business, PreparedGraphWrite? Catalog) Produce() {
                if (_repository.SharedSchemaRoot is { } latest && Catalog.Accepted?.Snapshot?.Address != latest) {
                    Catalog.RebaseOwned(SchemaCatalogView.Load(_repository.Store, latest));
                }
                capture = _business.CaptureOwned(root, kind);
                // Capture can invoke application callbacks. Recheck lifetime before
                // preparing catalog or business bodies, including Event-only saves.
                _repository.Store.EnsureUsable();
                catalog = Catalog.PrepareOwned(capture.Graph.Objects.Select(static row => row.Layout));
                business = _business.PrepareOwned(capture, catalog.View, parameters);
                return (business.Write, catalog.Write);
            }
            PendingRepositorySave pending = _repository.PrepareFrom(_branch, kind, Produce);
            return new(_repository, pending, catalog!, business!);
        } catch {
            capture?.AbortPreparation();
            throw;
        }
    }
}

internal sealed class PreparedBranchSave(RepositoryStorageFacade repository, PendingRepositorySave pending,
    PreparedSchemaCatalogWrite catalog, PreparedBusinessWrite business) : IDisposable {
    internal PreparedSchemaCatalogWrite Catalog { get; } = catalog;
    internal PreparedBusinessWrite Business { get; } = business;
    internal PendingRepositorySave Pending { get; } = pending;
    internal RepositorySaveResult Commit(Action<StoredGraphRevision>? prepareBusinessDelivery = null) {
        if (!Pending.IsPrepared) { throw new InvalidOperationException("The typed save is already committing or resolved."); }
        try { return repository.Commit(Pending, Catalog.Install, Business.Install, Catalog.PrepareInstall, Business.PrepareInstall, prepareBusinessDelivery); }
        finally { Business.Dispose(); }
    }
    public void Dispose() {
        Pending.Dispose();
        Business.Dispose();
    }
}
