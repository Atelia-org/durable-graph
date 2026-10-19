using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Atelia.HeadStore;

namespace Atelia.DurableGraph.Persistence;

// History selection belongs to Repository. A session receives its already restored workspace,
// or a proved absence of State; it never navigates business history by graph addresses.
internal sealed class BranchSaveSession {
    private readonly RepositoryStorageFacade _repository;
    private readonly HeadId? _branch;
    private HeadEntryId? _expected;
    private readonly BusinessPersistenceAdapter _business;

    internal BranchSaveSession(RepositoryStorageFacade repository, HeadId? branch, HeadEntryId? expected,
        StateModelRegistry.FrozenConfiguration configuration, Action ensureEditingAllowed,
        BusinessPersistenceAdapter? business = null) {
        if (branch.HasValue != expected.HasValue) { throw new ArgumentException("Only a new root may have no branch and accepted Commit."); }
        _repository = repository;
        _branch = branch;
        _expected = expected;
        FrameAddress? catalogRoot = repository.SharedSchemaRoot;
        SchemaCatalogView? catalog = catalogRoot is { } schema ? SchemaCatalogView.Load(repository.Store, schema) : null;
        Catalog = new(ensureEditingAllowed, catalog);
        if (business is not null) {
            if (business.Source is not { } source || !ReferenceEquals(source.SourceOwner, repository.Store)) {
                throw new InvalidDataException("A restored business workspace must belong to this exact Store.");
            }
            _business = business;
        } else {
            DecodedRevision.SchemaViewBindingEnvironment? environment = catalogRoot is { } selected
                ? DecodedRevision.SchemaViewBindingEnvironment.Load(repository.Store, configuration, selected) : null;
            _business = new(repository.Store, environment?.Models ?? configuration.CreateEnvironment(), environment);
        }
    }
    internal SchemaCatalogWorkspace Catalog { get; }
    internal IDurableObject? State => _business.State;
    internal StoredGraphRevision? StateSource => _business.Source;
    internal ModelExecutionEnvironment Models => _business.Models;
    internal NormalizedGraphState? Baseline => _business.Baseline;
    internal ObjectId RootId => _business.RootId;
    internal DecodedRevision.SchemaViewBindingEnvironment? BindingEnvironment => _business.BindingEnvironment;
    internal void Advance(HeadEntryId accepted) {
        if (_branch is not { } branch || accepted.HeadId != branch) { throw new InvalidOperationException("The accepted Entry must belong to the editing Branch."); }
        _expected = accepted;
    }
    internal PreparedBranchSave Prepare(IDurableObject root, StoredGraphKind kind = StoredGraphKind.State,
        ReadAmplificationBaseBudgetParameters? parameters = null, ReadOnlyMemory<byte> summary = default, string? rootLabel = null) {
        _repository.EnsureEditingAllowed();
        BusinessSaveCandidate? capture = null;
        PreparedSchemaCatalogWrite? catalog = null;
        PreparedBusinessWrite? business = null;
        try {
            (PreparedGraphWrite Business, PreparedGraphWrite? Catalog) Produce() {
                if (_repository.SharedSchemaRoot is { } latest && Catalog.Accepted?.Snapshot?.Address != latest) {
                    Catalog.RebaseOwned(SchemaCatalogView.Load(_repository.Store, latest));
                }
                capture = _business.CaptureOwned(root, kind);
                _repository.Store.EnsureUsable();
                catalog = Catalog.PrepareOwned(capture.Graph.Objects.Select(static row => row.Layout));
                business = _business.PrepareOwned(capture, catalog.View, parameters);
                return (business.Write, catalog.Write);
            }
            PendingRepositorySave pending = _repository.PrepareFrom(_branch, _expected, rootLabel, kind, summary, Produce);
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
    internal RepositorySaveResult Commit(Action<StoredGraphRevision>? prepareBusinessDelivery = null,
        Action<RepositorySaveResult>? deliver = null) {
        if (!Pending.IsPrepared) { throw new InvalidOperationException("The typed save is already committing or resolved."); }
        try { return repository.Commit(Pending, Catalog.Install, Business.Install, Catalog.PrepareInstall, Business.PrepareInstall, prepareBusinessDelivery, deliver); }
        finally { Business.Dispose(); }
    }
    public void Dispose() { Pending.Dispose(); Business.Dispose(); }
}
