using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.MetaBootstrapP0;

/// <summary>Real Capture producer; the P0 graph writer receives only selected records and Removes.</summary>
internal sealed class P0BusinessWorkspace {
    private readonly P0GraphStore _store;
    private readonly StateModelSnapshot _models;
    private readonly CaptureSession _capture;
    private NormalizedRevision? _baseline;

    internal P0BusinessWorkspace(P0GraphStore store, StateModelRegistry models)
        : this(store, models.Snapshot(), new(), null, null, null) { }

    private P0BusinessWorkspace(P0GraphStore store, StateModelSnapshot models, CaptureSession capture,
        P0GraphSnapshot? source, NormalizedRevision? baseline, IDurableObject? state) {
        _store = store;
        _models = models;
        _capture = capture;
        Source = source;
        _baseline = baseline;
        State = state;
    }

    internal P0GraphSnapshot? Source { get; private set; }
    internal IDurableObject? State { get; private set; }

    // P0Repository.PrepareFrom owns the lexical preparation guard around these two stages.
    internal P0BusinessCapture CaptureOwned(IDurableObject root) {
        ArgumentNullException.ThrowIfNull(root);
        CaptureContext context = _capture.BeginCapture(_models);
        try {
            ObjectId rootId = _models.ResolveCurrentModel(root.GetType()).AddRoot(context, root);
            CapturedGraph candidate = context.Seal();
            return new(context, candidate, root, rootId);
        } catch {
            context.Dispose();
            throw;
        }
    }

    internal P0PreparedBusiness PrepareOwned(P0BusinessCapture capture, P0CatalogView catalog,
        ReadAmplificationBaseBudgetParameters? parameters = null) {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(catalog);
        try {
            if (Source?.Address != _baseline?.RevisionAddress) { throw new InvalidDataException("State source and normalized baseline must agree."); }
            _baseline?.RequireSplitSource(_store, _models);
            CapturedGraph candidate = capture.Candidate;
            IReadOnlyList<PreparedCapturedObject> prepared = _baseline is null ? _capture.Prepare(candidate).Objects
                : _capture.PrepareAgainst(candidate, _baseline.CurrentDtos);
            List<PreparedObject> rows = [];
            foreach (PreparedCapturedObject item in prepared) {
                EncodedBaseObjectBody body = new(P0TypedBodyCodec.Encode(item.Current.Layout, item.BaseBody, catalog));
                rows.Add(ObjectRevisionPlanner.ClassifyCaptured(item, body,
                    item.Previous is null ? null : Source!.Objects[item.Current.Id.Value],
                    _baseline is not null && _baseline.Objects.TryGetValue(item.Current.Id, out NormalizedObject? prior) && prior.RequiresRewrite));
            }
            // Keep the existing full post-live Base budget. P0 does not claim total O(k).
            Dictionary<ObjectId, long> priorH = _baseline?.Objects.ToDictionary(static pair => pair.Key,
                static pair => pair.Value.Storage!.Value.ReconstructionPayloadBytes) ?? [];
            var (records, _, _) = ObjectRevisionPlanner.PlanObjectWrites(rows, priorH, parameters ?? new(5, 5));
            NormalizedRevision next = NormalizedRevision.FromSplitCandidate(candidate, _models, _store, _baseline);
            uint[] removed = _baseline?.Objects.Keys.Where(id => !next.Objects.ContainsKey(id)).Select(static id => id.Value).ToArray() ?? [];
            return new(this, _store, capture.Context, candidate, capture.Root,
                new(Source?.Address, capture.RootId, records, removed), next, catalog);
        } catch {
            capture.Dispose();
            throw;
        }
    }

    internal void PrepareInstall(P0PreparedBusiness prepared, P0GraphSnapshot graph) {
        if (graph.Kind != P0GraphKind.State || graph.RootId != prepared.Write.RootId || graph.SourceGraph != Source?.Address) {
            throw new InvalidOperationException("Only the prepared State can install the business baseline.");
        }
    }

    internal void Install(P0GraphSnapshot graph, CapturedGraph candidate, IDurableObject root, NormalizedRevision baseline) {
        _capture.Accept(candidate);
        Source = graph;
        State = root;
        _baseline = baseline;
    }

    internal static P0BusinessWorkspace LoadState(P0GraphStore store, FrameAddress state,
        StateModelRegistry models) {
        P0GraphSnapshot graph = store.ReadGraph(state);
        if (graph.Kind != P0GraphKind.State) { throw new InvalidDataException("Editable State must select a State graph."); }
        DecodedRevision source = DecodedRevision.ReadSplit(store, state, models);
        MaterializedGraph<IDurableObject> restored = GraphReader.Read<IDurableObject>(
            source, graph.RootId, new(source.SourceModels!));
        restored.Baseline.RequireSplitSource(store, source.SourceModels!);
        return new(store, source.SourceModels!, restored.CreateCaptureSession(), graph, restored.Baseline, restored.Root);
    }

    internal static IDurableObject ReadRoot(P0GraphStore store, FrameAddress graph, StateModelRegistry models) {
        P0GraphSnapshot metadata = store.ReadGraph(graph);
        DecodedRevision source = DecodedRevision.ReadSplit(store, graph, models);
        return GraphReader.ReadRoot<IDurableObject>(source, metadata.RootId, new(source.SourceModels!));
    }
}

internal sealed class P0BusinessCapture(CaptureContext context, CapturedGraph candidate,
    IDurableObject root, ObjectId rootId) : IDisposable {
    internal CaptureContext Context { get; } = context;
    internal CapturedGraph Candidate { get; } = candidate;
    internal IDurableObject Root { get; } = root;
    internal ObjectId RootId { get; } = rootId;
    public void Dispose() => Context.Dispose();
}

internal sealed class P0PreparedBusiness : IDisposable {
    private readonly P0BusinessWorkspace _owner;
    private readonly P0GraphStore _store;
    private readonly CaptureContext _context;
    private readonly CapturedGraph _candidate;
    private readonly IDurableObject _root;
    private readonly NormalizedRevision _next;
    private readonly P0CatalogView _catalog;
    private NormalizedRevision? _installation;
    private P0GraphSnapshot? _graph;

    internal P0PreparedBusiness(P0BusinessWorkspace owner, P0GraphStore store, CaptureContext context,
        CapturedGraph candidate, IDurableObject root, P0PreparedGraphWrite write, NormalizedRevision next, P0CatalogView catalog) {
        _owner = owner;
        _store = store;
        _context = context;
        _candidate = candidate;
        _root = root;
        Write = write;
        _next = next;
        _catalog = catalog;
    }

    internal P0PreparedGraphWrite Write { get; }
    internal void PrepareInstall(P0GraphSnapshot graph) {
        if (_installation is not null) { throw new InvalidOperationException("This business installation is already prepared."); }
        _owner.PrepareInstall(this, graph);
        _installation = _next.WithSplitGraph(_store, graph, Write.Records, _catalog);
        _graph = graph;
    }
    internal void Install(P0GraphSnapshot graph) {
        NormalizedRevision installation = _installation ?? throw new InvalidOperationException("Prepare the business installation before publication.");
        if (!ReferenceEquals(_graph, graph)) { throw new InvalidOperationException("Only the prepared graph can consume this installation."); }
        _owner.Install(graph, _candidate, _root, installation);
        _installation = null;
    }
    public void Dispose() => _context.Dispose();
}
