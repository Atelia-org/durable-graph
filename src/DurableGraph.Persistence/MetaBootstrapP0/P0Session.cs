using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.MetaBootstrapP0;

/// <summary>Thin typed driver. P0Repository owns the only pending-save lifecycle.</summary>
internal sealed class P0Session {
    private readonly P0Repository _repository;
    private readonly string _branch;
    private readonly P0BusinessWorkspace _business;
    private bool _preparing;

    internal P0Session(P0Repository repository, string branch, StateModelRegistry models) {
        _repository = repository;
        _branch = branch;
        P0CatalogView? catalog = repository.SharedSchemaRoot is { } schema
            ? P0CatalogView.Load(repository.Store, schema) : null;
        Catalog = new(EnsureEditingAllowed, catalog);
        _business = repository.GetStateHead(branch) is { } state
            ? P0BusinessWorkspace.LoadState(repository.Store, state, models, repository.EnsureEditingAllowed)
            : new(models, repository.EnsureEditingAllowed);
    }

    internal P0CatalogWorkspace Catalog { get; }
    internal IDurableObject? State => _business.State;
    internal P0GraphSnapshot? StateSource => _business.Source;

    private void EnsureEditingAllowed() {
        _repository.EnsureEditingAllowed();
        if (_preparing) { throw new InvalidOperationException("The typed driver is preparing a frozen save."); }
    }

    internal P0SessionSave Prepare(IDurableObject root, P0GraphKind kind = P0GraphKind.State,
        ReadAmplificationBaseBudgetParameters? parameters = null) {
        EnsureEditingAllowed();
        if (_repository.SharedSchemaRoot is { } latest && Catalog.Accepted?.Snapshot?.Address != latest) {
            Catalog.Rebase(P0CatalogView.Load(_repository.Store, latest));
        }
        P0PreparedCatalog catalog = Catalog.Prepare();
        _preparing = true;
        P0PreparedBusiness? business = null;
        try {
            business = _business.Prepare(root, catalog.View, parameters);
            P0PendingSave pending = _repository.Prepare(_branch, kind, business.Write, catalog.Write);
            return new(_repository, pending, catalog, business);
        } catch {
            business?.Dispose();
            throw;
        } finally { _preparing = false; }
    }
}

internal sealed class P0SessionSave(P0Repository repository, P0PendingSave pending,
    P0PreparedCatalog catalog, P0PreparedBusiness business) : IDisposable {
    internal P0PreparedCatalog Catalog { get; } = catalog;
    internal P0PreparedBusiness Business { get; } = business;
    internal P0PendingSave Pending { get; } = pending;
    internal P0SaveResult Commit() {
        try { return repository.Commit(Pending, Catalog.Install, Business.Install); }
        finally { Business.Dispose(); }
    }
    public void Dispose() {
        try { Pending.Dispose(); }
        finally { Business.Dispose(); }
    }
}
