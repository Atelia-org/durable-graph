using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.MetaBootstrapP0;

/// <summary>Real Capture producer; the P0 graph writer receives only selected records and Removes.</summary>
internal sealed class P0BusinessWorkspace {
    private readonly StateModelSnapshot _models;
    private readonly CaptureSession _capture;
    private IReadOnlyDictionary<ObjectId, ObjectStateRecord> _rows;
    private IReadOnlyDictionary<ObjectId, long> _h;
    private IReadOnlySet<ObjectId> _rewrite;

    internal P0BusinessWorkspace(StateModelRegistry models)
        : this(models.Snapshot(), new(), null, new Dictionary<ObjectId, ObjectStateRecord>(),
            new Dictionary<ObjectId, long>(), new HashSet<ObjectId>(), null) { }

    private P0BusinessWorkspace(StateModelSnapshot models, CaptureSession capture,
        P0GraphSnapshot? source, IReadOnlyDictionary<ObjectId, ObjectStateRecord> rows,
        IReadOnlyDictionary<ObjectId, long> h, IReadOnlySet<ObjectId> rewrite, IDurableObject? state) {
        _models = models;
        _capture = capture;
        Source = source;
        _rows = rows;
        _h = h;
        _rewrite = rewrite;
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
            CapturedGraph candidate = capture.Candidate;
            IReadOnlyList<PreparedCapturedObject> prepared = _capture.PrepareAgainst(candidate, _rows);
            Dictionary<ObjectId, PreparedCapturedObject> contents = prepared.ToDictionary(static item => item.Current.Id);
            Dictionary<ObjectId, byte[]> bases = [];
            List<ObjectSaveEstimate> estimates = [];
            Dictionary<ObjectId, long> nextH = [];
            foreach (PreparedCapturedObject item in prepared) {
                byte[] body = P0TypedBodyCodec.Encode(item.Current.Layout, item.BaseBody, catalog);
                bases.Add(item.Current.Id, body);
                long b = ObjectVersionPayloadSize.GetBasePayloadBytes(body.Length);
                ObjectSaveChangeKind kind;
                long? d = null, h = null;
                if (item.Previous is null) { kind = ObjectSaveChangeKind.Insert; }
                else if (_rewrite.Contains(item.Current.Id)) { kind = ObjectSaveChangeKind.BaseOnlyUpdate; }
                else {
                    h = _h[item.Current.Id];
                    kind = item.DeltaBody is { HasChanges: true } ? ObjectSaveChangeKind.Update : ObjectSaveChangeKind.NoChange;
                    if (kind == ObjectSaveChangeKind.Update) {
                        d = ObjectVersionPayloadSize.GetDeltaPayloadBytes(item.DeltaBody!.Body.Length, Source!.Objects[item.Current.Id.Value]);
                    }
                    nextH.Add(item.Current.Id, h.Value);
                }
                estimates.Add(new(item.Current.Id, kind, b, d, h));
            }
            // Keep the existing full post-live Base budget. P0 does not claim total O(k).
            ObjectRepresentationPlan plan = ReadAmplificationBaseBudgetPolicy.Plan(estimates, parameters ?? new(5, 5));
            List<ObjectVersionRecord> records = [];
            foreach (ObjectWriteDecision decision in plan.Writes) {
                PreparedCapturedObject item = contents[decision.ObjectId];
                ObjectVersionRecord record = decision.Mode == ObjectRepresentationMode.Base
                    ? ObjectVersionRecord.CreateBase(decision.ObjectId.Value, bases[decision.ObjectId])
                    : ObjectVersionRecord.CreateDelta(decision.ObjectId.Value, Source!.Objects[decision.ObjectId.Value], item.DeltaBody!.Body);
                records.Add(record);
                long bytes = record.Kind == ObjectVersionKind.Base
                    ? ObjectVersionPayloadSize.GetBasePayloadBytes(record.Body.Length)
                    : ObjectVersionPayloadSize.GetDeltaPayloadBytes(record.Body.Length, record.PriorAddress!.Value);
                nextH[decision.ObjectId] = record.Kind == ObjectVersionKind.Base ? bytes : checked(_h[decision.ObjectId] + bytes);
            }
            Dictionary<ObjectId, ObjectStateRecord> nextRows = candidate.Objects.ToDictionary(static row => row.Id);
            uint[] removed = _rows.Keys.Where(id => !nextRows.ContainsKey(id)).Select(static id => id.Value).ToArray();
            return new(this, capture.Context, candidate, capture.Root,
                new(Source?.Address, capture.RootId, records, removed), nextRows, nextH);
        } catch {
            capture.Dispose();
            throw;
        }
    }

    internal void Install(P0PreparedBusiness prepared, P0GraphSnapshot graph,
        CapturedGraph candidate, IDurableObject root, IReadOnlyDictionary<ObjectId, ObjectStateRecord> rows,
        IReadOnlyDictionary<ObjectId, long> h) {
        if (graph.Kind != P0GraphKind.State || graph.RootId != prepared.Write.RootId || graph.SourceGraph != Source?.Address) {
            throw new InvalidOperationException("Only the prepared State can install the business baseline.");
        }
        _capture.Accept(candidate);
        Source = graph;
        State = root;
        _rows = rows;
        _h = h;
        _rewrite = EmptyRewrite;
    }

    private static readonly IReadOnlySet<ObjectId> EmptyRewrite = new HashSet<ObjectId>();

    internal static P0BusinessWorkspace LoadState(P0GraphStore store, FrameAddress state,
        StateModelRegistry models) {
        var source = Decode(store, state, models);
        if (source.Rows.Snapshot.Kind != P0GraphKind.State) { throw new InvalidDataException("Editable State must select a State graph."); }
        MaterializedGraph<IDurableObject> restored = GraphReader.Read<IDurableObject>(
            source.Rows.ToDecodedRevision(), source.Rows.Snapshot.RootId, new(source.Models));
        Dictionary<ObjectId, long> h = source.Rows.Storage.ToDictionary(static pair => pair.Key, static pair => pair.Value.ReconstructionPayloadBytes);
        HashSet<ObjectId> rewrite = restored.Baseline.Objects.Values.Where(static row => row.RequiresRewrite)
            .Select(static row => row.Current.Id).ToHashSet();
        return new(source.Models, restored.CreateCaptureSession(), source.Rows.Snapshot,
            restored.Baseline.CurrentDtos, h, rewrite, restored.Root);
    }

    internal static IDurableObject ReadRoot(P0GraphStore store, FrameAddress graph, StateModelRegistry models) {
        var source = Decode(store, graph, models);
        return GraphReader.ReadRoot<IDurableObject>(source.Rows.ToDecodedRevision(), source.Rows.Snapshot.RootId, new(source.Models));
    }

    private static (P0DecodedRows Rows, StateModelSnapshot Models) Decode(P0GraphStore store,
        FrameAddress graph, StateModelRegistry models) {
        P0GraphSnapshot metadata = store.ReadGraph(graph);
        if (metadata.Kind == P0GraphKind.Meta || metadata.SchemaRoot is not { } schemaRoot) {
            throw new InvalidDataException("A typed business graph requires its exact meta root.");
        }
        P0CatalogView selected = P0CatalogView.Load(store, schemaRoot);
        StateModelSnapshot snapshot = models.Snapshot(schemaLookup: key => selected.Schemas.GetValueOrDefault(key));
        return (P0TypedReader.Read(store, graph, snapshot, selected), snapshot);
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

internal sealed class P0PreparedBusiness(P0BusinessWorkspace owner, CaptureContext context,
    CapturedGraph candidate, IDurableObject root, P0PreparedGraphWrite write,
    IReadOnlyDictionary<ObjectId, ObjectStateRecord> rows, IReadOnlyDictionary<ObjectId, long> h) : IDisposable {
    internal P0PreparedGraphWrite Write { get; } = write;
    internal void Install(P0GraphSnapshot graph) => owner.Install(this, graph, candidate, root, rows, h);
    public void Dispose() => context.Dispose();
}
