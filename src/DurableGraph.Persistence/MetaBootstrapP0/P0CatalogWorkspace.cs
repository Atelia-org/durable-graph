using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using CatalogDto = Atelia.DurableGraph.Generated.Family_503043.V1;
using DescriptorDto = Atelia.DurableGraph.Generated.Family_503044.V1;
using FieldDto = Atelia.DurableGraph.Generated.Family_503046.V1;

namespace Atelia.DurableGraph.Persistence.MetaBootstrapP0;

/// <summary>Only logical dirty keys survive Discard; candidate IDs are allocated during Prepare.</summary>
internal sealed class P0CatalogWorkspace {
    private readonly Action _ensureEditingAllowed;
    private readonly Dictionary<SchemaKey, DurableSchema> _schemas = [];
    private readonly HashSet<ObjectLayout> _containers = [];
    private P0CatalogView? _accepted;

    internal P0CatalogWorkspace(Action ensureEditingAllowed, P0CatalogView? accepted = null) {
        _ensureEditingAllowed = ensureEditingAllowed;
        _accepted = accepted;
    }

    internal P0CatalogView? Accepted => _accepted;
    internal int DirtyCount => _schemas.Count + _containers.Count;

    internal void Set(DurableSchema schema) {
        _ensureEditingAllowed();
        Validate(schema);
        SchemaKey key = new(schema.Type, schema.Version);
        if (_accepted is not null && _accepted.Schemas.TryGetValue(key, out DurableSchema? prior) && !prior.Equals(schema)) {
            throw new SchemaConflictException(prior, schema);
        }
        _schemas[key] = schema;
    }

    internal void Set(ObjectLayout layout) {
        _ensureEditingAllowed();
        if (layout.Schema is { } schema) { Set(schema); return; }
        if (layout.Kind != ObjectStateKind.List || layout.List!.ElementSlot.TypeTag != TypeTag.Int32) {
            throw new NotSupportedException("P0 only accepts reference Schemas and List<int> descriptors.");
        }
        _containers.Add(layout);
    }

    internal P0PreparedCatalog Prepare() {
        _ensureEditingAllowed();
        Builder builder = new(_accepted);
        foreach (DurableSchema schema in _schemas.Values.OrderBy(static value => value.Type).ThenBy(static value => value.Version)) {
            ObjectLayout layout = ObjectLayout.ForDurable(schema);
            if (!builder.Contains(layout)) { builder.AddSchema(schema); }
        }
        foreach (ObjectLayout layout in _containers) {
            if (!builder.Contains(layout)) { builder.AddList(layout.List!); }
        }
        return builder.Finish(this);
    }

    internal static void Validate(DurableSchema schema) {
        ArgumentNullException.ThrowIfNull(schema);
        if (schema.Kind != SchemaKind.ReferenceObject || schema.BaseSchema is not null || schema.Type.Arguments.Length != 0 ||
            schema.SchemaId is "P0C" or "P0D" or "P0F" or "P0H") {
            throw new NotSupportedException("P0 requires an unreserved, non-generic reference Schema without ancestry.");
        }
        foreach (DurableFieldInfo field in schema.Fields) {
            if (TypeTagFacts.IsBuiltin(field.TypeTag)) { continue; }
            if (field.TypeTag == TypeTag.ObjectReference && field.TargetType is { } target &&
                (target.Kind == TypeExprKind.Named && target.Arguments.Length == 0 ||
                 target == TypeExpr.List(TypeExpr.Builtin(TypeTag.Int32)))) { continue; }
            throw new NotSupportedException("The field is outside P0's explicitly finite Schema closure.");
        }
    }

    internal void Rebase(P0CatalogView accepted) {
        _ensureEditingAllowed();
        foreach (DurableSchema dirty in _schemas.Values) {
            if (accepted.Schemas.TryGetValue(new(dirty.Type, dirty.Version), out DurableSchema? prior) && !prior.Equals(dirty)) {
                throw new SchemaConflictException(prior, dirty);
            }
        }
        _accepted = accepted;
    }

    internal void Accept(P0PreparedCatalog candidate, P0GraphSnapshot graph) {
        if (candidate.View.Snapshot is null) { candidate.View.AttachSnapshot(graph); }
        if (candidate.View.Snapshot!.Address != graph.Address) { throw new InvalidOperationException("Catalog installation must use its actual graph."); }
        _accepted = candidate.View;
        _schemas.Clear();
        _containers.Clear();
    }

    private sealed class Builder {
        private static readonly StateModelSnapshot Models = P0FixedBindings.CreateSnapshot();
        private readonly P0CatalogView? _source;
        private readonly Dictionary<ObjectId, ObjectStateRecord> _rows;
        private readonly List<ObjectId> _declared;
        private readonly List<ObjectId> _added = [];
        private readonly List<ObjectStateRecord> _new = [];
        private readonly Dictionary<string, ObjectId> _strings = new(StringComparer.Ordinal);
        private ulong _next;
        private readonly ObjectId _root;
        private readonly ObjectId _list;

        internal Builder(P0CatalogView? source) {
            _source = source;
            _rows = source?.Rows.ToDictionary() ?? [];
            _declared = source?.DescriptorIds.ToList() ?? [];
            _next = source is null ? 3 : (ulong)_rows.Keys.Max().Value + 1;
            _root = source?.RootId ?? new(1);
            _list = source?.DescriptorListId ?? new(2);
            foreach (ObjectStateRecord row in _rows.Values.Where(static row => row.Kind == ObjectStateKind.String)) {
                _strings.TryAdd(row.StringContent, row.Id);
            }
            if (source is null) {
                Add(new ObjectStateRecord(_root, Schema(typeof(P0CatalogRecord)), new CatalogDto(_list)));
            }
        }

        internal bool Contains(ObjectLayout layout) {
            if (_source is null) { return false; }
            try { _ = _source.DescriptorId(layout); return true; }
            catch (InvalidDataException) { return false; }
        }

        internal void AddSchema(DurableSchema schema) {
            ObjectId name = String(schema.SchemaId);
            ObjectId[] fields = schema.Fields.Select(Field).ToArray();
            ObjectId list = Allocate();
            Add(new ObjectStateRecord(list, List(typeof(List<P0FieldRecord>)), new FrozenListState<ObjectId>(fields)));
            ObjectId descriptor = Allocate();
            Add(new ObjectStateRecord(descriptor, Schema(typeof(P0DescriptorRecord)),
                new DescriptorDto(1, name, schema.Version, list, default, 0)));
            _added.Add(descriptor);
            _declared.Add(descriptor);
        }

        internal void AddList(ListLayout layout) {
            ObjectId element = Field(layout.ElementSlot);
            ObjectId descriptor = Allocate();
            Add(new ObjectStateRecord(descriptor, Schema(typeof(P0DescriptorRecord)),
                new DescriptorDto(2, default, 0, default, element, layout.CodecVersion)));
            _added.Add(descriptor);
            _declared.Add(descriptor);
        }

        private ObjectId Field(DurableFieldInfo field) {
            byte kind = 0;
            ObjectId name = default;
            if (field.TypeTag == TypeTag.ObjectReference) {
                if (field.TargetType!.IsList) { kind = 2; }
                else { kind = 1; name = String(field.TargetType.DefinitionId!); }
            }
            ObjectId id = Allocate();
            Add(new ObjectStateRecord(id, Schema(typeof(P0FieldRecord)),
                new FieldDto(field.FieldId, (byte)field.TypeTag, kind, name)));
            return id;
        }

        private ObjectId String(string value) {
            if (_strings.TryGetValue(value, out ObjectId prior)) { return prior; }
            ObjectId id = Allocate();
            _strings.Add(value, id);
            Add(new ObjectStateRecord(id, value));
            return id;
        }

        private ObjectId Allocate() => _next <= uint.MaxValue ? new((uint)_next++)
            : throw new InvalidOperationException("The P0 meta ObjectId domain is exhausted.");
        private static DurableSchema Schema(Type type) => Models.ResolveCurrentModel(type).CurrentSchema;
        private static ListLayout List(Type type) => Models.TryGetCurrentObjectBinding(type, out ObjectBinding? binding)
            ? binding!.CurrentLayout.List! : throw new InvalidDataException("Missing fixed List binding.");

        private void Add(ObjectStateRecord row) {
            ObjectStateRecord prepared = P0FixedBindings.WithPreparation(row);
            _rows.Add(row.Id, prepared);
            _new.Add(prepared);
        }

        internal P0PreparedCatalog Finish(P0CatalogWorkspace owner) {
            if (_source is not null && _added.Count == 0) { return new(owner, _source, null, 0, []); }
            ObjectStateRecord list = P0FixedBindings.WithPreparation(new ObjectStateRecord(_list,
                List(typeof(List<P0DescriptorRecord>)), new FrozenListState<ObjectId>(_declared.ToArray())));
            _rows[_list] = list;
            StateReferenceValidator validator = new(_rows);
            foreach (ObjectStateRecord row in _rows.Values) { P0FixedBindings.Reader(row.Layout).VisitReferences(row, validator); }
            List<ObjectVersionRecord> records = [];
            List<ObjectId> encodedIds = [];
            foreach (ObjectStateRecord row in _new) {
                PreparedBaseBody raw = row.Kind == ObjectStateKind.String ? StringPayloadCodec.PrepareBase(row.StringContent)
                    : row.Preparation!.PrepareBase(row);
                records.Add(ObjectVersionRecord.CreateBase(row.Id.Value, P0TypedBodyCodec.Encode(row.Layout, raw)));
                encodedIds.Add(row.Id);
            }
            byte[] encodedBase = P0TypedBodyCodec.Encode(list.Layout, list.Preparation!.PrepareBase(list));
            encodedIds.Add(list.Id);
            long b = ObjectVersionPayloadSize.GetBasePayloadBytes(encodedBase.Length);
            long h = b;
            if (_source is null) { records.Add(ObjectVersionRecord.CreateBase(_list.Value, encodedBase)); }
            else {
                PreparedDeltaBody delta = ListStateBody<ObjectId, ObjectIdStateOps>.PrepareAppendDelta(
                    _source.DescriptorIds.Count, _added.ToArray(), list.Layout.List!);
                var prior = _source.Snapshot!.Objects[_list.Value];
                long d = ObjectVersionPayloadSize.GetDeltaPayloadBytes(delta.Body.Length, prior);
                if (b <= d || _source.DescriptorListH + d > 5L * b) {
                    records.Add(ObjectVersionRecord.CreateBase(_list.Value, encodedBase));
                } else {
                    records.Add(ObjectVersionRecord.CreateDelta(_list.Value, prior, delta.Body));
                    h = _source.DescriptorListH + d;
                }
            }
            P0CatalogView view = new(_root, _rows, descriptorListH: h);
            return new(owner, view, new(_source?.Snapshot?.Address, _root, records), _added.Count, encodedIds,
                _source?.DescriptorIds);
        }
    }
}

internal sealed class P0PreparedCatalog(P0CatalogWorkspace owner, P0CatalogView view,
    P0PreparedGraphWrite? write, int addedDescriptors, IEnumerable<ObjectId> encodedIds,
    IEnumerable<ObjectId>? acceptedDescriptors = null) {
    internal P0CatalogView View { get; } = view;
    internal P0PreparedGraphWrite? Write { get; } = write;
    internal int AddedDescriptors { get; } = addedDescriptors;
    internal IReadOnlyList<ObjectId> EncodedIds { get; } = new FrozenList<ObjectId>(encodedIds);
    internal int EncodedRows => EncodedIds.Count;
    internal int EncodedAcceptedDescriptors => EncodedIds.Intersect(acceptedDescriptors ?? []).Count();
    internal void Install(P0GraphSnapshot graph) => owner.Accept(this, graph);
}
