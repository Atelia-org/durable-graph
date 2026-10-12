using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.MetaBootstrapP0;

/// <summary>Split storage/source and typed-encoding adapter for the shared business workspace.</summary>
internal sealed class P0BusinessWorkspace {
    private readonly P0GraphStore _store;
    private readonly BusinessWorkspace _workspace;

    internal StateModelSnapshot Models => _workspace.Models;
    internal NormalizedRevision? Baseline => _workspace.Baseline;
    internal ObjectId RootId => _workspace.RootId;
    internal DecodedRevision.SplitBindingEnvironment? BindingEnvironment { get; private init; }

    internal P0BusinessWorkspace(P0GraphStore store, StateModelRegistry models)
        : this(store, new BusinessWorkspace(models.Snapshot()), null) { }

    internal P0BusinessWorkspace(P0GraphStore store, StateModelSnapshot models,
        DecodedRevision.SplitBindingEnvironment? environment)
        : this(store, new BusinessWorkspace(models), null) { BindingEnvironment = environment; }

    private P0BusinessWorkspace(P0GraphStore store, BusinessWorkspace workspace, P0GraphSnapshot? source) {
        _store = store;
        _workspace = workspace;
        Source = source;
    }

    internal P0GraphSnapshot? Source { get; private set; }
    internal IDurableObject? State => _workspace.Root;

    // P0Repository.PrepareFrom owns the lexical preparation guard around these two stages.
    internal BusinessSaveCandidate CaptureOwned(IDurableObject root, P0GraphKind kind) =>
        _workspace.Capture(root, independentSnapshot: kind == P0GraphKind.Event);

    internal P0PreparedBusiness PrepareOwned(BusinessSaveCandidate candidate, P0CatalogView catalog,
        ReadAmplificationBaseBudgetParameters? parameters = null) {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(catalog);
        _workspace.RequirePreparing(candidate);
        try {
            NormalizedRevision? baseline = _workspace.Baseline;
            if (Source?.Address != baseline?.RevisionAddress) { throw new InvalidDataException("State source and normalized baseline must agree."); }
            baseline?.RequireSplitSource(_store, _workspace.Models);
            IReadOnlyList<PreparedCapturedObject> prepared = candidate.PrepareContents();
            List<PreparedObject> rows = [];
            foreach (PreparedCapturedObject item in prepared) {
                EncodedBaseObjectBody body = new(P0TypedBodyCodec.Encode(item.Current.Layout, item.BaseBody, catalog));
                rows.Add(ObjectRevisionPlanner.ClassifyCaptured(item, body,
                    item.Previous is null ? null : Source!.Objects[item.Current.Id.Value],
                    baseline is not null && baseline.Objects.TryGetValue(item.Current.Id, out NormalizedObject? prior) && prior.RequiresRewrite));
            }
            // Keep the existing full post-live Base budget. P0 does not claim total O(k).
            Dictionary<ObjectId, long> priorH = baseline?.Objects.ToDictionary(static pair => pair.Key,
                static pair => pair.Value.Storage!.Value.ReconstructionPayloadBytes) ?? [];
            var (records, _, _) = ObjectRevisionPlanner.PlanObjectWrites(rows, priorH, parameters ?? new(5, 5));
            NormalizedRevision? next = candidate.IsIndependentSnapshot ? null :
                NormalizedRevision.FromSplitCandidate(candidate.Graph, _workspace.Models, _store, baseline);
            HashSet<ObjectId> live = candidate.Graph.Objects.Select(static row => row.Id).ToHashSet();
            uint[] removed = baseline?.Objects.Keys.Where(id => !live.Contains(id)).Select(static id => id.Value).ToArray() ?? [];
            candidate.CompletePreparation(next);
            return new(this, _store, candidate, new(Source?.Address, candidate.RootId, records, removed), catalog);
        } catch {
            candidate.AbortPreparation();
            throw;
        }
    }

    internal void PrepareInstall(P0PreparedBusiness prepared, P0GraphSnapshot graph) {
        if (graph.Kind != P0GraphKind.State || graph.RootId != prepared.Write.RootId || graph.SourceGraph != Source?.Address) {
            throw new InvalidOperationException("Only the prepared State can install the business baseline.");
        }
    }

    internal void Install(P0GraphSnapshot graph, BusinessSaveCandidate candidate) {
        candidate.Install();
        Source = graph;
    }

    internal static P0BusinessWorkspace LoadState(P0GraphStore store, FrameAddress state,
        StateModelRegistry models) {
        return LoadState(store, state, models.FreezeConfiguration());
    }

    internal static P0BusinessWorkspace LoadState(P0GraphStore store, FrameAddress state,
        StateModelRegistry.FrozenConfiguration configuration) {
        P0GraphSnapshot graph = store.ReadGraph(state);
        if (graph.Kind != P0GraphKind.State) { throw new InvalidDataException("Editable State must select a State graph."); }
        DecodedRevision source = DecodedRevision.ReadSplit(store, state, configuration);
        MaterializedGraph<IDurableObject> restored = GraphReader.Read<IDurableObject>(
            source, graph.RootId, new(source.SourceModels!));
        restored.Baseline.RequireSplitSource(store, source.SourceModels!);
        return new(store, BusinessWorkspace.FromLoaded(source.SourceModels!, restored), graph) {
            BindingEnvironment = source.BindingEnvironment,
        };
    }

    internal static IDurableObject ReadRoot(P0GraphStore store, FrameAddress graph, StateModelRegistry models) {
        P0GraphSnapshot metadata = store.ReadGraph(graph);
        DecodedRevision source = DecodedRevision.ReadSplit(store, graph, models);
        return GraphReader.ReadRoot<IDurableObject>(source, metadata.RootId, new(source.SourceModels!));
    }
}

internal sealed class P0PreparedBusiness : IDisposable {
    private readonly P0BusinessWorkspace _owner;
    private readonly P0GraphStore _store;
    private readonly BusinessSaveCandidate _candidate;
    private readonly P0CatalogView _catalog;
    private P0GraphSnapshot? _graph;

    internal P0PreparedBusiness(P0BusinessWorkspace owner, P0GraphStore store, BusinessSaveCandidate candidate,
        P0PreparedGraphWrite write, P0CatalogView catalog) {
        _owner = owner;
        _store = store;
        _candidate = candidate;
        Write = write;
        _catalog = catalog;
    }

    internal P0PreparedGraphWrite Write { get; }
    internal void PrepareInstall(P0GraphSnapshot graph) {
        _candidate.RequireReady();
        _owner.PrepareInstall(this, graph);
        _candidate.PrepareInstallation(_candidate.NextBaseline.WithSplitGraph(_store, graph, Write.Records, _catalog));
        _graph = graph;
    }
    internal void Install(P0GraphSnapshot graph) {
        _candidate.RequireReady();
        if (!ReferenceEquals(_graph, graph)) { throw new InvalidOperationException("Only the prepared graph can consume this installation."); }
        _owner.Install(graph, _candidate);
    }
    public void Dispose() => _candidate.Dispose();
}
