using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence;

/// <summary>Split storage/source and typed-encoding adapter for the shared business workspace.</summary>
internal sealed class BusinessPersistenceAdapter {
    private readonly GraphRecordStore _store;
    private readonly BusinessWorkspace _workspace;

    internal ModelExecutionEnvironment Models => _workspace.Models;
    internal NormalizedGraphState? Baseline => _workspace.Baseline;
    internal ObjectId RootId => _workspace.RootId;
    internal DecodedRevision.SchemaViewBindingEnvironment? BindingEnvironment { get; private init; }

    internal BusinessPersistenceAdapter(GraphRecordStore store, StateModelRegistry models)
        : this(store, new BusinessWorkspace(models.CreateEnvironment()), null) { }

    internal BusinessPersistenceAdapter(GraphRecordStore store, ModelExecutionEnvironment models,
        DecodedRevision.SchemaViewBindingEnvironment? environment)
        : this(store, new BusinessWorkspace(models), null) { BindingEnvironment = environment; }

    private BusinessPersistenceAdapter(GraphRecordStore store, BusinessWorkspace workspace, StoredGraphRevision? source) {
        _store = store;
        _workspace = workspace;
        Source = source;
    }

    internal StoredGraphRevision? Source { get; private set; }
    internal IDurableObject? State => _workspace.Root;

    // RepositoryStorageFacade.PrepareFrom forwards the owner's lexical preparation guard around these two stages.
    internal BusinessSaveCandidate CaptureOwned(IDurableObject root, StoredGraphKind kind) =>
        _workspace.Capture(root, independentSnapshot: kind == StoredGraphKind.Event);

    internal PreparedBusinessWrite PrepareOwned(BusinessSaveCandidate candidate, SchemaCatalogView catalog,
        ReadAmplificationBaseBudgetParameters? parameters = null) {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(catalog);
        _workspace.RequirePreparing(candidate);
        try {
            NormalizedGraphState? baseline = _workspace.Baseline;
            if (Source?.Address != baseline?.RevisionAddress) { throw new InvalidDataException("State source and normalized baseline must agree."); }
            baseline?.RequireStoredSource(_store, _workspace.Models);
            IReadOnlyList<PreparedCapturedObject> prepared = candidate.PrepareContents();
            List<PreparedObject> rows = [];
            foreach (PreparedCapturedObject item in prepared) {
                EncodedBaseObjectBody body = new(TypedBaseBodyCodec.Encode(item.Current.Layout, item.BaseBody, catalog));
                rows.Add(ObjectWritePlanner.ClassifyCaptured(item, body,
                    item.Previous is null ? null : Source!.ObjectHeads[item.Current.Id.Value],
                    baseline is not null && baseline.Objects.TryGetValue(item.Current.Id, out NormalizedObject? prior) && prior.RequiresRewrite));
            }
            // Keep the existing full post-live Base budget. P0 does not claim total O(k).
            Dictionary<ObjectId, long> priorH = baseline?.Objects.ToDictionary(static pair => pair.Key,
                static pair => pair.Value.Storage!.Value.ReconstructionPayloadBytes) ?? [];
            var (records, _, _) = ObjectWritePlanner.PlanObjectWrites(rows, priorH, parameters ?? new(5, 5));
            NormalizedGraphState? next = candidate.IsIndependentSnapshot ? null :
                NormalizedGraphState.FromCapturedCandidate(candidate.Graph, _workspace.Models, _store, baseline);
            HashSet<ObjectId> live = candidate.Graph.Objects.Select(static row => row.Id).ToHashSet();
            uint[] removed = baseline?.Objects.Keys.Where(id => !live.Contains(id)).Select(static id => id.Value).ToArray() ?? [];
            candidate.CompletePreparation(next);
            return new(this, _store, candidate, new(Source?.Address, candidate.RootId, records, removed), catalog);
        } catch {
            candidate.AbortPreparation();
            throw;
        }
    }

    internal void PrepareInstall(PreparedBusinessWrite prepared, StoredGraphRevision graph) {
        if (graph.Kind != StoredGraphKind.State || graph.RootId != prepared.Write.RootId || graph.SourceGraph != Source?.Address) {
            throw new InvalidOperationException("Only the prepared State can install the business baseline.");
        }
    }

    internal void Install(StoredGraphRevision graph, BusinessSaveCandidate candidate) {
        candidate.Install();
        Source = graph;
    }

    internal static BusinessPersistenceAdapter LoadState(GraphRecordStore store, FrameAddress state,
        StateModelRegistry models) {
        return LoadState(store, state, models.FreezeConfiguration());
    }

    internal static BusinessPersistenceAdapter LoadState(GraphRecordStore store, FrameAddress state,
        StateModelRegistry.FrozenConfiguration configuration) {
        GraphEnvelope graph = store.ReadMetadata(state);
        if (graph.Kind != StoredGraphKind.State) { throw new InvalidDataException("Editable State must select a State graph."); }
        DecodedRevision source = DecodedRevision.ReadStoredGraph(store, state, configuration);
        EditableGraphRestoration<IDurableObject> restored = GraphReader.Read<IDurableObject>(
            source, graph.RootId, new(source.SourceModels!));
        return FromLoaded(store, source.BindingEnvironment!, restored);
    }

    internal static BusinessPersistenceAdapter FromLoaded(GraphRecordStore store,
        DecodedRevision.SchemaViewBindingEnvironment environment, EditableGraphRestoration<IDurableObject> restored) {
        StoredGraphRevision graph = restored.Baseline.RequireStoredSource(store, environment.Models);
        if (graph.RootId != restored.RootId || !ReferenceEquals(restored.Baseline.SelectedSchemaView, environment.SelectedSchemaView)) {
            throw new InvalidDataException("Editable State must retain its actual root and reader-issued Schema environment.");
        }
        return new(store, BusinessWorkspace.FromLoaded(environment.Models, restored), graph) {
            BindingEnvironment = environment,
        };
    }

    internal static IDurableObject ReadRoot(GraphRecordStore store, FrameAddress graph, StateModelRegistry models) {
        StoredGraphRevision metadata = store.ReadGraph(graph);
        DecodedRevision source = DecodedRevision.ReadStoredGraph(store, metadata, models.FreezeConfiguration());
        return GraphReader.ReadRoot<IDurableObject>(source, metadata.RootId, new(source.SourceModels!));
    }
}

internal sealed class PreparedBusinessWrite : IDisposable {
    private readonly BusinessPersistenceAdapter _owner;
    private readonly GraphRecordStore _store;
    private readonly BusinessSaveCandidate _candidate;
    private readonly SchemaCatalogView _catalog;
    private StoredGraphRevision? _graph;

    internal PreparedBusinessWrite(BusinessPersistenceAdapter owner, GraphRecordStore store, BusinessSaveCandidate candidate,
        PreparedGraphWrite write, SchemaCatalogView catalog) {
        _owner = owner;
        _store = store;
        _candidate = candidate;
        Write = write;
        _catalog = catalog;
    }

    internal PreparedGraphWrite Write { get; }
    internal void PrepareInstall(StoredGraphRevision graph) {
        _candidate.RequireReady();
        _owner.PrepareInstall(this, graph);
        _candidate.PrepareInstallation(_candidate.NextBaseline.PrepareGraphInstallation(_store, graph, Write.Records, _catalog));
        _graph = graph;
    }
    internal void Install(StoredGraphRevision graph) {
        _candidate.RequireReady();
        if (!ReferenceEquals(_graph, graph)) { throw new InvalidOperationException("Only the prepared graph can consume this installation."); }
        _owner.Install(graph, _candidate);
    }
    public void Dispose() => _candidate.Dispose();
}
