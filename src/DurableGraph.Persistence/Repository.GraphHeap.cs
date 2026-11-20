using Atelia.DurableGraph.Persistence;
using Atelia.FrameStore;

namespace Atelia.DurableGraph;

public sealed partial class Repository {
    internal DecodedRevision.LoadedSchemaCatalog? LastHeapCatalog { get; private set; }

    private DecodedRevision.SchemaViewBindingEnvironment? BindHeapSchema(FrameAddress? root) =>
        root is { } schema ? DecodedRevision.SchemaViewBindingEnvironment.Load(
            _storageFacade.Store, _modelConfiguration, schema, LastHeapCatalog) : null;

    /// <summary>Creates an unsaved complete graph without registering a business branch.</summary>
    /// <remarks>
    /// Does not capture, clone or persist the supplied root. Multiple builders may exist together;
    /// this repository remains their only resource owner. SaveGraph publishes their snapshots.
    /// </remarks>
    public GraphBuilder CreateGraph(IDurableObject root) {
        RequireAvailable();
        _busy = true;
        try {
            ArgumentNullException.ThrowIfNull(root);
            var environment = BindHeapSchema(_resources.SharedSchemaRoot);
            ModelExecutionEnvironment models = environment?.Models ?? _modelConfiguration.CreateEnvironment();
            if (!models.TryGetCurrentModel(root.GetType(), out _)) {
                throw new ArgumentException("Root must have its actual domain type registered.", nameof(root));
            }
            var builder = new GraphBuilder(this, new BusinessPersistenceAdapter(_storageFacade.Store, models, environment), root);
            LastHeapCatalog = environment?.Catalog;
            return builder;
        } finally { EndOperation(); }
    }

    /// <summary>Restores one exact saved heap graph as an independent editable builder.</summary>
    /// <remarks>
    /// Select the repository that issued the reference. The reference uses CommitId's probabilistic
    /// cross-repository misuse detection. A read-only repository can open and edit the in-memory
    /// graph, but cannot save it. No "latest graph" lookup or business branch is involved.
    /// </remarks>
    public GraphBuilder OpenGraph(GraphRef reference) {
        RequireAvailable();
        _busy = true;
        try {
            HistoryGraphRecord record = _resources.ReadHeapDescriptor(reference.CommitId.Value);
            StoredGraphRevision graph = _storageFacade.Store.ReadBusinessGraph(record.Address);
            BusinessPersistenceAdapter business = BusinessPersistenceAdapter.LoadState(_storageFacade.Store, graph, _modelConfiguration,
                BindHeapSchema(graph.SchemaRoot));
            var builder = new GraphBuilder(this, business, business.State!);
            LastHeapCatalog = business.BindingEnvironment!.Catalog;
            return builder;
        } finally { EndOperation(); }
    }

    /// <summary>Publishes a complete graph's incremental snapshot and advances only this builder's baseline.</summary>
    /// <remarks>
    /// The graph heap's current append position is selected during this operation; other builders
    /// saving in between do not invalidate this builder. Even an unchanged graph receives a new
    /// reference. The shared Schema and graph are jointly published before normal return.
    /// Exceptions do not imply rollback: inspect RepositoryPublicationException.Outcome and
    /// IsFaulted, and reopen a faulted repository. Caller-owned domain changes are never reverted.
    /// Resume from GraphRefs in the application's saved state; a failed delivery may leave an
    /// unselected graph. Exception positions do not constitute a returned GraphRef or business branch.
    /// </remarks>
    public GraphRef SaveGraph(GraphBuilder builder) {
        RequireWriter();
        ArgumentNullException.ThrowIfNull(builder);
        builder.RequireLive(this);
        _busy = true;
        BusinessSaveCandidate? capture = null;
        PreparedSchemaCatalogWrite? catalog = null;
        PreparedBusinessWrite? business = null;
        bool resolved = false;
        void Cleanup() {
            if (resolved) { return; }
            resolved = true;
            capture?.AbortPreparation();
        }
        try {
            PendingRepositorySave pending = _storageFacade.PrepareGroup(() => {
                var tip = _resources.ReadHeapTip();
                capture = builder.Business.CaptureOwned(builder.RootForSave, StoredGraphKind.State);
                _storageFacade.EnsureSaveUsable();
                catalog = _storageFacade.CatalogForPreparation.PrepareOwned(capture.Graph.Objects.Select(static row => row.Layout));
                _storageFacade.EnsureSaveUsable();
                business = builder.Business.PrepareOwned(capture, catalog.View, DefaultPolicy);
                _storageFacade.EnsureSaveUsable();
                return new([new(tip.HeadId, tip, null, StoredGraphKind.State, default, business.Write)], catalog.Write);
            }, Cleanup);
            using PreparedBranchSave save = new(_storageFacade, pending, catalog!, business!, Cleanup);
            OperationStageObserver?.Invoke(RepositoryOperationStage.AfterPrepare);
            GraphRef reference = default;
            save.Commit(deliver: result => reference = new GraphRef(new CommitId(result.CommitId)));
            return reference;
        } finally { EndOperation(); }
    }

    internal void RequireGraphAccess() => RequireAvailable();
    internal void RequireGraphRelease() {
        if (_busy) { throw new InvalidOperationException("Cannot dispose a graph builder during a repository operation."); }
    }
}
