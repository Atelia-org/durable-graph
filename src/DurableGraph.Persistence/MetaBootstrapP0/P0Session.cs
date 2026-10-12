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

    internal P0CatalogWorkspace Catalog { get; }
    internal IDurableObject? State => _business.State;
    internal P0GraphSnapshot? StateSource => _business.Source;

    private void EnsureEditingAllowed() {
        _repository.EnsureEditingAllowed();
    }

    internal P0SessionSave Prepare(IDurableObject root, P0GraphKind kind = P0GraphKind.State,
        ReadAmplificationBaseBudgetParameters? parameters = null) {
        EnsureEditingAllowed();
        P0BusinessCapture? capture = null;
        P0PreparedCatalog? catalog = null;
        P0PreparedBusiness? business = null;
        try {
            P0PendingSave pending = _repository.PrepareFrom(_branch, kind, () => {
                if (_repository.SharedSchemaRoot is { } latest && Catalog.Accepted?.Snapshot?.Address != latest) {
                    Catalog.RebaseOwned(P0CatalogView.Load(_repository.Store, latest));
                }
                capture = _business.CaptureOwned(root);
                catalog = Catalog.PrepareOwned(capture.Candidate.Objects.Select(static row => row.Layout));
                business = _business.PrepareOwned(capture, catalog.View, parameters);
                return (business.Write, catalog.Write);
            });
            return new(_repository, pending, catalog!, business!);
        } catch {
            capture?.Dispose();
            throw;
        }
    }
}

internal sealed class P0SessionSave(P0Repository repository, P0PendingSave pending,
    P0PreparedCatalog catalog, P0PreparedBusiness business) : IDisposable {
    internal P0PreparedCatalog Catalog { get; } = catalog;
    internal P0PreparedBusiness Business { get; } = business;
    internal P0PendingSave Pending { get; } = pending;
    internal P0SaveResult Commit() {
        if (!Pending.IsPrepared) { throw new InvalidOperationException("The typed save is already committing or resolved."); }
        try { return repository.Commit(Pending, Catalog.Install, Business.Install, Catalog.PrepareInstall, Business.PrepareInstall); }
        finally { Business.Dispose(); }
    }
    public void Dispose() {
        Pending.Dispose();
        Business.Dispose();
    }
}
