using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.MetaBootstrapP0;

/// <summary>Thin typed driver. P0Repository owns the only pending-save lifecycle.</summary>
internal sealed class P0Session {
    private readonly P0Repository _repository;
    private readonly string _branch;
    private readonly P0BusinessWorkspace _business;

    internal P0Session(P0Repository repository, string branch, StateModelRegistry models) {
        _repository = repository;
        _branch = branch;
        P0CatalogView? catalog = repository.SharedSchemaRoot is { } schema
            ? P0CatalogView.Load(repository.Store, schema) : null;
        Catalog = new(EnsureEditingAllowed, catalog);
        _business = repository.GetStateHead(branch) is { } state
            ? P0BusinessWorkspace.LoadState(repository.Store, state, models)
            : new(repository.Store, models);
    }

    internal P0Session(P0Repository repository, string branch,
        StateModelRegistry.FrozenConfiguration configuration, Action ensureEditingAllowed)
        : this(repository, branch, configuration, ensureEditingAllowed, repository.GetStateHead(branch)) { }

    // The selected business head is owner-checked by Repository before restoring a historical Fork.
    // No target branch is published or consulted to choose the State being restored.
    internal P0Session(P0Repository repository, string branch, FrameAddress selectedHead,
        StateModelRegistry.FrozenConfiguration configuration, Action ensureEditingAllowed)
        : this(repository, branch, configuration, ensureEditingAllowed,
            repository.Store.ResolveContentSource(P0GraphKind.State, selectedHead)) { }

    private P0Session(P0Repository repository, string branch,
        StateModelRegistry.FrozenConfiguration configuration, Action ensureEditingAllowed, FrameAddress? sourceState) {
        _repository = repository;
        _branch = branch;
        P0CatalogView? catalog = repository.SharedSchemaRoot is { } schema
            ? P0CatalogView.Load(repository.Store, schema) : null;
        Catalog = new(ensureEditingAllowed, catalog);
        if (sourceState is { } state) {
            _business = P0BusinessWorkspace.LoadState(repository.Store, state, configuration);
        } else {
            DecodedRevision.SplitBindingEnvironment? environment = repository.SharedSchemaRoot is { } selected
                ? DecodedRevision.SplitBindingEnvironment.Load(repository.Store, configuration, selected) : null;
            _business = new(repository.Store, environment?.Models ?? configuration.CreateSnapshot(), environment);
        }
    }

    internal P0Session(P0Repository repository, string branch, P0BusinessWorkspace business,
        Action ensureEditingAllowed) {
        if (business.Source is not { } source || !ReferenceEquals(source.SourceOwner, repository.Store)) {
            throw new InvalidDataException("A restored business workspace must belong to this exact repository Store.");
        }
        _repository = repository;
        _branch = branch;
        _business = business;
        P0CatalogView? catalog = repository.SharedSchemaRoot is { } schema
            ? P0CatalogView.Load(repository.Store, schema) : null;
        Catalog = new(ensureEditingAllowed, catalog);
    }

    internal P0CatalogWorkspace Catalog { get; }
    internal IDurableObject? State => _business.State;
    internal P0GraphSnapshot? StateSource => _business.Source;
    internal StateModelSnapshot Models => _business.Models;
    internal NormalizedRevision? Baseline => _business.Baseline;
    internal ObjectId RootId => _business.RootId;
    internal DecodedRevision.SplitBindingEnvironment? BindingEnvironment => _business.BindingEnvironment;

    private void EnsureEditingAllowed() {
        _repository.EnsureEditingAllowed();
    }

    internal P0SessionSave Prepare(IDurableObject root, P0GraphKind kind = P0GraphKind.State,
        ReadAmplificationBaseBudgetParameters? parameters = null) {
        EnsureEditingAllowed();
        BusinessSaveCandidate? capture = null;
        P0PreparedCatalog? catalog = null;
        P0PreparedBusiness? business = null;
        try {
            P0PendingSave pending = _repository.PrepareFrom(_branch, kind, () => {
                if (_repository.SharedSchemaRoot is { } latest && Catalog.Accepted?.Snapshot?.Address != latest) {
                    Catalog.RebaseOwned(P0CatalogView.Load(_repository.Store, latest));
                }
                capture = _business.CaptureOwned(root, kind);
                catalog = Catalog.PrepareOwned(capture.Graph.Objects.Select(static row => row.Layout));
                business = _business.PrepareOwned(capture, catalog.View, parameters);
                return (business.Write, catalog.Write);
            });
            return new(_repository, pending, catalog!, business!);
        } catch {
            capture?.AbortPreparation();
            throw;
        }
    }
}

internal sealed class P0SessionSave(P0Repository repository, P0PendingSave pending,
    P0PreparedCatalog catalog, P0PreparedBusiness business) : IDisposable {
    internal P0PreparedCatalog Catalog { get; } = catalog;
    internal P0PreparedBusiness Business { get; } = business;
    internal P0PendingSave Pending { get; } = pending;
    internal P0SaveResult Commit(Action<P0GraphSnapshot>? prepareBusinessDelivery = null) {
        if (!Pending.IsPrepared) { throw new InvalidOperationException("The typed save is already committing or resolved."); }
        try { return repository.Commit(Pending, Catalog.Install, Business.Install, Catalog.PrepareInstall, Business.PrepareInstall, prepareBusinessDelivery); }
        finally { Business.Dispose(); }
    }
    public void Dispose() {
        Pending.Dispose();
        Business.Dispose();
    }
}
