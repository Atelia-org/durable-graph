using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.MetaBootstrapP0;

/// <summary>Real Capture producer; the P0 graph writer receives only selected records and Removes.</summary>
internal sealed class P0BusinessWorkspace {
    private readonly StateModelSnapshot _models;
    private readonly Action _ensureEditingAllowed;
    private readonly CaptureSession _capture;
    private IReadOnlyDictionary<ObjectId, ObjectStateRecord> _rows;
    private IReadOnlyDictionary<ObjectId, long> _h;
    private IReadOnlySet<ObjectId> _rewrite;

    internal P0BusinessWorkspace(StateModelRegistry models, Action ensureEditingAllowed)
        : this(models.Snapshot(), ensureEditingAllowed, new(), null, new Dictionary<ObjectId, ObjectStateRecord>(),
            new Dictionary<ObjectId, long>(), new HashSet<ObjectId>(), null) { }

    private P0BusinessWorkspace(StateModelSnapshot models, Action ensureEditingAllowed, CaptureSession capture,
        P0GraphSnapshot? source, IReadOnlyDictionary<ObjectId, ObjectStateRecord> rows,
        IReadOnlyDictionary<ObjectId, long> h, IReadOnlySet<ObjectId> rewrite, IDurableObject? state) {
        _models = models;
        _ensureEditingAllowed = ensureEditingAllowed;
        _capture = capture;
        Source = source;
        _rows = rows;
        _h = h;
        _rewrite = rewrite;
        State = state;
    }

    internal P0GraphSnapshot? Source { get; private set; }
    internal IDurableObject? State { get; private set; }

    internal P0PreparedBusiness Prepare(IDurableObject root, P0CatalogView catalog,
        ReadAmplificationBaseBudgetParameters? parameters = null) {
        _ensureEditingAllowed();
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(catalog);
        CaptureContext context = _capture.BeginCapture(_models);
        try {
            ObjectId rootId = _models.ResolveCurrentModel(root.GetType()).AddRoot(context, root);
            CapturedGraph candidate = context.Seal();
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
            return new(this, context, candidate, root, new(Source?.Address, rootId, records, removed), nextRows, nextH);
        } catch {
            context.Dispose();
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
        StateModelRegistry models, Action ensureEditingAllowed) {
        StateModelSnapshot snapshot = models.Snapshot();
        Restored restored = Restore(store, state, snapshot);
        if (restored.Decoded.Snapshot.Kind != P0GraphKind.State) { throw new InvalidDataException("Editable State must select a State graph."); }
        Dictionary<ObjectId, long> h = restored.Decoded.Storage.ToDictionary(static pair => pair.Key, static pair => pair.Value.ReconstructionPayloadBytes);
        return new(snapshot, ensureEditingAllowed, new(restored.NextId, restored.Bindings), restored.Decoded.Snapshot,
            restored.Rows, h, restored.Rewrite, restored.Root);
    }

    internal static IDurableObject ReadRoot(P0GraphStore store, FrameAddress graph, StateModelRegistry models) =>
        Restore(store, graph, models.Snapshot()).Root;

    private static Restored Restore(P0GraphStore store, FrameAddress graph, StateModelSnapshot models) {
        P0GraphSnapshot metadata = store.ReadGraph(graph);
        if (metadata.Kind == P0GraphKind.Meta || metadata.SchemaRoot is not { } schemaRoot) {
            throw new InvalidDataException("A typed business graph requires its exact meta root.");
        }
        P0CatalogView selected = P0CatalogView.Load(store, schemaRoot);
        P0DecodedRows decoded = P0TypedReader.Read(store, graph, models, selected);
        Dictionary<ObjectId, ObjectStateRecord> rows = [];
        Dictionary<ObjectId, ObjectBinding> bindings = [];
        HashSet<ObjectId> rewrite = [];
        foreach (ObjectStateRecord source in decoded.Rows.Values) {
            Type type = models.GetDomainType(source.Layout.Type);
            if (!models.TryGetCurrentObjectBinding(type, out ObjectBinding? binding)) { throw new InvalidDataException("Missing current object binding."); }
            ObjectStateRecord current = binding!.Normalize(source);
            if (current.Layout.Kind != ObjectStateKind.String) { _ = selected.DescriptorId(current.Layout); }
            rows.Add(current.Id, current);
            bindings.Add(current.Id, binding);
            if (!source.Layout.Equals(current.Layout)) { rewrite.Add(current.Id); }
        }
        StateReferenceValidator validator = new(rows);
        foreach (ObjectStateRecord row in rows.Values) { bindings[row.Id].VisitReferences(row, validator); }
        Reachable visitor = new();
        visitor.Add(metadata.RootId);
        for (int index = 0; index < visitor.Ids.Count; index++) {
            ObjectId id = visitor.Ids[index];
            if (!rows.TryGetValue(id, out ObjectStateRecord? row)) { throw new InvalidDataException("Missing root or reachable object."); }
            bindings[id].VisitReferences(row, visitor);
        }
        Dictionary<ObjectId, object> instances = [];
        foreach (ObjectId id in visitor.Ids) {
            object instance = bindings[id].Allocate(rows[id]);
            if (instance.GetType() != bindings[id].DomainType) { throw new InvalidDataException("Allocation must return its exact current type."); }
            instances.Add(id, instance);
        }
        ObjectReadTable table = new(instances);
        foreach (ObjectId id in visitor.Ids) { bindings[id].Hydrate(instances[id], rows[id], table); }
        if (instances[metadata.RootId] is not IDurableObject root) { throw new InvalidDataException("The root must be a durable object."); }
        Dictionary<object, ObjectId> identities = new(ReferenceEqualityComparer.Instance);
        foreach (var item in instances.Where(item => rows[item.Key].Kind != ObjectStateKind.String)) { identities.Add(item.Value, item.Key); }
        foreach (ObjectStateRecord row in rows.Values.OrderBy(static row => row.Id)) {
            if (row.Kind == ObjectStateKind.String) { identities.TryAdd(row.StringContent, row.Id); }
        }
        return new(root, decoded, rows, rewrite, identities, (ulong)rows.Keys.Max().Value + 1);
    }

    private sealed record Restored(IDurableObject Root, P0DecodedRows Decoded,
        Dictionary<ObjectId, ObjectStateRecord> Rows, HashSet<ObjectId> Rewrite,
        Dictionary<object, ObjectId> Bindings, ulong NextId);

    private sealed class Reachable : IStateReferenceVisitor {
        private readonly HashSet<ObjectId> _ids = [];
        internal List<ObjectId> Ids { get; } = [];
        internal void Add(ObjectId id) { if (!id.IsNull && _ids.Add(id)) { Ids.Add(id); } }
        public void VisitString(ObjectId id) => Add(id);
        public void VisitDurable(ObjectId id, string nominalSchemaId) => Add(id);
        public void VisitDurable(ObjectId id, TypeExpr nominalType) => Add(id);
        public void VisitObject(ObjectId id, TypeExpr declaredType) => Add(id);
    }
}

internal sealed class P0PreparedBusiness(P0BusinessWorkspace owner, CaptureContext context,
    CapturedGraph candidate, IDurableObject root, P0PreparedGraphWrite write,
    IReadOnlyDictionary<ObjectId, ObjectStateRecord> rows, IReadOnlyDictionary<ObjectId, long> h) : IDisposable {
    internal P0PreparedGraphWrite Write { get; } = write;
    internal void Install(P0GraphSnapshot graph) => owner.Install(this, graph, candidate, root, rows, h);
    public void Dispose() => context.Dispose();
}
