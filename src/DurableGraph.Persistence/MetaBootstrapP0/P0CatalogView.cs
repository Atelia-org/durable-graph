using System.Collections.Frozen;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using CatalogDto = Atelia.DurableGraph.Generated.Family_503043.V1;
using DescriptorDto = Atelia.DurableGraph.Generated.Family_503044.V1;
using FieldDto = Atelia.DurableGraph.Generated.Family_503046.V1;
using TypeDto = Atelia.DurableGraph.Generated.Family_503054.V1;

namespace Atelia.DurableGraph.Persistence.MetaBootstrapP0;

/// <summary>Owned DTO-to-factory projection; nominal references do not follow Schema construction edges.</summary>
internal sealed class P0CatalogView {
    private readonly Dictionary<ObjectLayout, ObjectId> _ids = [];
    private readonly Dictionary<ObjectId, ObjectLayout> _layouts = [];
    private readonly Dictionary<SchemaKey, DurableSchema> _schemas = [];
    private readonly Dictionary<SchemaKey, ObjectId> _schemaIds = [];
    private readonly Dictionary<ObjectId, DurableSchema> _schemaMemo = [];
    private readonly Dictionary<DurableSchema, int> _schemaHeights = [];
    private readonly Dictionary<ObjectId, DurableFieldInfo> _fieldMemo = [];
    private readonly Dictionary<ObjectId, TypeExpr> _typeMemo = [];
    private readonly Dictionary<ObjectId, int> _typeHeights = [];
    private readonly HashSet<ObjectId> _active = [];
    private readonly HashSet<ObjectId> _declared;

    internal P0CatalogView(ObjectId rootId, IReadOnlyDictionary<ObjectId, ObjectStateRecord> rows,
        P0GraphSnapshot? snapshot = null, long descriptorListH = 0) {
        RootId = rootId;
        Rows = new FrozenSortedDictionary<ObjectId, ObjectStateRecord>(new(rows.ToDictionary()));
        Snapshot = snapshot;
        CatalogDto root = Require(rootId, typeof(P0CatalogRecord)).GetState<CatalogDto>();
        DescriptorListId = root.Segment0Field1;
        ObjectStateRecord list = Require(DescriptorListId, typeof(List<P0DescriptorRecord>));
        DescriptorIds = new FrozenList<ObjectId>(list.GetListState<ObjectId>().Elements.ToArray());
        DescriptorListH = descriptorListH;
        _declared = [];
        foreach (ObjectId id in DescriptorIds) {
            if (id.IsNull || !_declared.Add(id)) { throw new InvalidDataException("Catalog declarations must be nonzero and unique."); }
        }
        foreach (ObjectId id in DescriptorIds) {
            DescriptorDto node = Require(id, typeof(P0DescriptorRecord)).GetState<DescriptorDto>();
            ObjectLayout? layout;
            if (node.Segment0Field1 is 1 or 3) {
                DurableSchema schema = Schema(id, 1);
                SchemaKey key = new(schema.Type, schema.Version);
                if (!_schemas.TryAdd(key, schema)) { throw new InvalidDataException("Duplicate exact Schema key."); }
                _schemaIds.Add(key, id);
                layout = schema.Kind == SchemaKind.ReferenceObject ? ObjectLayout.ForDurable(schema) : null;
            } else if (node.Segment0Field1 is 2 or 4) {
                if ((node.Segment0Field1 == 2 && !node.Segment0Field2.IsNull) ||
                    node.Segment0Field3 != 0 || !node.Segment0Field4.IsNull || !node.Segment0Field7.IsNull) {
                    throw new InvalidDataException("Wrong container descriptor variant.");
                }
                DurableFieldInfo element = Field(node.Segment0Field5, 0, 1);
                if (element.FieldId != 1) { throw new InvalidDataException("A container element slot must be canonically numbered one."); }
                if (node.Segment0Field1 == 2) { layout = Factory(() => ObjectLayout.ForList(new(element, node.Segment0Field6))); }
                else {
                    TypeExpr type = Expression(node.Segment0Field2, 1);
                    if (!type.IsArray) { throw new InvalidDataException("An Array descriptor requires a closed Array type."); }
                    layout = Factory(() => ObjectLayout.ForArray(new(type.Kind, element, node.Segment0Field6)));
                    if (layout.Type != type) { throw new InvalidDataException("An Array descriptor's type and exact element slot disagree."); }
                }
                if (P0FixedBindings.IsBuiltin(layout)) { throw new InvalidDataException("A fixed builtin layout cannot acquire a dynamic descriptor."); }
            } else { throw new InvalidDataException("Unknown descriptor variant."); }
            if (layout is not null) {
                if (!_ids.TryAdd(layout, id)) { throw new InvalidDataException("Duplicate complete object descriptor layout."); }
                _layouts.Add(id, layout);
            }
        }
        P0CatalogSemantics.ValidateDeclarations(_schemas.Values, _layouts.Values);
        Schemas = _schemas.ToFrozenDictionary();
    }

    internal ObjectId RootId { get; }
    internal ObjectId DescriptorListId { get; }
    // Includes inline declarations, which are descriptors but never object descriptors.
    internal IReadOnlyList<ObjectId> DescriptorIds { get; }
    internal IReadOnlyDictionary<ObjectId, ObjectStateRecord> Rows { get; }
    internal IReadOnlyDictionary<SchemaKey, DurableSchema> Schemas { get; }
    internal IEnumerable<ObjectLayout> ObjectLayouts => _layouts.Select(static pair => pair.Value);
    internal P0GraphSnapshot? Snapshot { get; private set; }
    internal long DescriptorListH { get; }

    internal void AttachSnapshot(P0GraphSnapshot snapshot) {
        if (Snapshot is not null || snapshot.RootId != RootId) { throw new InvalidOperationException("A candidate can acquire only its own snapshot once."); }
        Snapshot = snapshot;
    }
    internal ObjectId DescriptorId(ObjectLayout layout) => _ids.TryGetValue(layout, out ObjectId id) ? id
        : throw new InvalidDataException("The selected catalog lacks this complete object layout.");
    internal ObjectLayout ResolveLayout(ObjectId id) => _layouts.TryGetValue(id, out ObjectLayout? layout) ? layout
        : throw new InvalidDataException("This meta ObjectId is not a declared object descriptor.");
    internal ObjectId SchemaNodeId(DurableSchema schema) => _schemaIds.TryGetValue(new(schema.Type, schema.Version), out ObjectId id) &&
        _schemas[new(schema.Type, schema.Version)].Equals(schema) ? id : throw new InvalidDataException("Missing complete declared Schema.");

    internal static P0CatalogView Load(P0GraphStore store, FrameAddress graph) {
        P0DecodedRows decoded = P0TypedReader.Read(store, graph);
        if (decoded.Snapshot.Kind != P0GraphKind.Meta) { throw new InvalidDataException("A Schema root must select a meta graph."); }
        if (!decoded.Rows.TryGetValue(decoded.Snapshot.RootId, out ObjectStateRecord? rootRow) ||
            !P0FixedBindings.Layout(typeof(P0CatalogRecord)).Equals(rootRow.Layout)) { throw new InvalidDataException("Wrong catalog root layout."); }
        CatalogDto root = rootRow.GetState<CatalogDto>();
        if (root.Segment0Field1.IsNull || !decoded.Rows.TryGetValue(root.Segment0Field1, out ObjectStateRecord? list) ||
            !P0FixedBindings.Layout(typeof(List<P0DescriptorRecord>)).Equals(list.Layout) ||
            !decoded.Storage.TryGetValue(root.Segment0Field1, out ObjectStorageInfo storage)) {
            throw new InvalidDataException("Missing or wrongly typed catalog declaration list.");
        }
        long h = storage.ReconstructionPayloadBytes;
        return new(decoded.Snapshot.RootId, decoded.Rows, decoded.Snapshot, h);
    }

    private ObjectStateRecord Require(ObjectId id, Type type) {
        if (id.IsNull || !Rows.TryGetValue(id, out ObjectStateRecord? row) || !P0FixedBindings.Layout(type).Equals(row.Layout)) {
            throw new InvalidDataException("Missing or wrongly typed employee node.");
        }
        return row;
    }
    private string String(ObjectId id) => Require(id, typeof(string)).StringContent;
    private void Enter(ObjectId id) {
        if (!_active.Add(id)) { throw new InvalidDataException("Employee construction dependencies contain a cycle."); }
    }
    private static T Factory<T>(Func<T> create) {
        try { return create(); }
        catch (ArgumentException error) { throw new InvalidDataException("Invalid employee factory operands.", error); }
        catch (NotSupportedException error) { throw new InvalidDataException("Unsupported employee declaration.", error); }
    }

    private DurableSchema Schema(ObjectId id, int depth) {
        if (depth > P0CatalogSemantics.MaximumExactDepth) { throw new InvalidDataException("Exact Schema dependencies exceed depth 256."); }
        if (!_declared.Contains(id)) { throw new InvalidDataException("An exact Schema dependency must be declared by the catalog root."); }
        if (_schemaMemo.TryGetValue(id, out DurableSchema? known)) {
            if (depth + _schemaHeights[known] - 1 > P0CatalogSemantics.MaximumExactDepth) { throw new InvalidDataException("Exact Schema dependencies exceed depth 256."); }
            return known;
        }
        Enter(id);
        try {
            DescriptorDto dto = Require(id, typeof(P0DescriptorRecord)).GetState<DescriptorDto>();
            if (dto.Segment0Field1 is not (1 or 3) || !dto.Segment0Field5.IsNull || dto.Segment0Field6 != 0 ||
                dto.Segment0Field1 == 3 && !dto.Segment0Field7.IsNull) { throw new InvalidDataException("Wrong exact Schema variant."); }
            TypeExpr type = Expression(dto.Segment0Field2, 1);
            ObjectId[] fields = Require(dto.Segment0Field4, typeof(List<P0FieldRecord>)).GetListState<ObjectId>().Elements.ToArray();
            DurableFieldInfo[] values = fields.Select(field => Field(field, depth, 1)).ToArray();
            DurableSchema? ancestor = dto.Segment0Field7.IsNull ? null : Schema(dto.Segment0Field7, depth + 1);
            if (ancestor is not null && ancestor.Kind != SchemaKind.ReferenceObject) { throw new InvalidDataException("A base dependency must be a reference Schema."); }
            SchemaKind kind = dto.Segment0Field1 == 1 ? SchemaKind.ReferenceObject : SchemaKind.InlineValue;
            DurableSchema schema = Factory(() => new DurableSchema(type, dto.Segment0Field3, values, ancestor, kind));
            Factory(() => { P0CatalogWorkspace.Validate(schema); return schema; });
            int height = 1;
            if (ancestor is not null) { height = Math.Max(height, 1 + _schemaHeights[ancestor]); }
            foreach (DurableFieldInfo field in values) {
                if (field.ValueSchema is { } inline) { height = Math.Max(height, 1 + _schemaHeights[inline]); }
            }
            if (height > P0CatalogSemantics.MaximumExactDepth) { throw new InvalidDataException("Exact Schema dependencies exceed depth 256."); }
            _schemaMemo.Add(id, schema);
            _schemaHeights.TryAdd(schema, height);
            return schema;
        } finally { _active.Remove(id); }
    }

    private DurableFieldInfo Field(ObjectId id, int schemaDepth, int fieldDepth) {
        if (fieldDepth > P0CatalogSemantics.MaximumExactDepth) { throw new InvalidDataException("Field construction exceeds depth 256."); }
        if (_fieldMemo.TryGetValue(id, out DurableFieldInfo known)) {
            if (known.ValueSchema is { } inline && schemaDepth + _schemaHeights[inline] > P0CatalogSemantics.MaximumExactDepth) {
                throw new InvalidDataException("Exact Schema dependencies exceed depth 256.");
            }
            return known;
        }
        Enter(id);
        try {
            FieldDto dto = Require(id, typeof(P0FieldRecord)).GetState<FieldDto>();
            TypeTag tag = (TypeTag)dto.Segment0Field2;
            DurableFieldInfo field;
            if (tag == TypeTag.ObjectReference) {
                if (!dto.Segment0Field4.IsNull || !dto.Segment0Field5.IsNull) { throw new InvalidDataException("Wrong reference slot variant."); }
                field = Factory(() => DurableFieldInfo.Reference(dto.Segment0Field1, Expression(dto.Segment0Field3, 1)));
            } else if (tag == TypeTag.InlineValue) {
                if (!dto.Segment0Field3.IsNull || !dto.Segment0Field5.IsNull) { throw new InvalidDataException("Wrong inline slot variant."); }
                DurableSchema inline = Schema(dto.Segment0Field4, schemaDepth + 1);
                field = Factory(() => new DurableFieldInfo(dto.Segment0Field1, tag, inlineSchema: inline));
            } else if (tag == TypeTag.Nullable) {
                if (!dto.Segment0Field3.IsNull || !dto.Segment0Field4.IsNull) { throw new InvalidDataException("Wrong Nullable slot variant."); }
                DurableFieldInfo child = Field(dto.Segment0Field5, schemaDepth, fieldDepth + 1);
                if (child.FieldId != 1) { throw new InvalidDataException("A Nullable child must be canonically numbered one."); }
                field = Factory(() => DurableFieldInfo.Nullable(dto.Segment0Field1, child));
            } else {
                if (!TypeTagFacts.IsBuiltin(tag) || !dto.Segment0Field3.IsNull || !dto.Segment0Field4.IsNull || !dto.Segment0Field5.IsNull) {
                    throw new InvalidDataException("Wrong scalar slot variant.");
                }
                field = Factory(() => new DurableFieldInfo(dto.Segment0Field1, tag));
            }
            _fieldMemo.Add(id, field);
            return field;
        } finally { _active.Remove(id); }
    }

    private TypeExpr Expression(ObjectId id, int depth) {
        if (depth > TypeExpr.MaximumDepth) { throw new InvalidDataException("TypeExpr exceeds depth 64."); }
        if (_typeMemo.TryGetValue(id, out TypeExpr? known)) {
            if (depth + _typeHeights[id] - 1 > TypeExpr.MaximumDepth) { throw new InvalidDataException("TypeExpr exceeds depth 64."); }
            return known;
        }
        Enter(id);
        try {
            TypeDto dto = Require(id, typeof(P0TypeExprRecord)).GetState<TypeDto>();
            TypeExprKind kind = (TypeExprKind)dto.Segment0Field1;
            ObjectId[] operands = dto.Segment0Field4.IsNull ? [] :
                Require(dto.Segment0Field4, typeof(List<P0TypeExprRecord>)).GetListState<ObjectId>().Elements.ToArray();
            if (operands.Length > TypeExpr.MaximumArity) { throw new InvalidDataException("TypeExpr exceeds arity 32."); }
            if (kind != TypeExprKind.Named && !dto.Segment0Field3.IsNull || kind != TypeExprKind.Builtin && dto.Segment0Field2 != 0) {
                throw new InvalidDataException("Wrong TypeExpr variant operands.");
            }
            TypeExpr[] arguments = operands.Select(child => Expression(child, depth + 1)).ToArray();
            TypeExpr type = Factory(() => kind switch {
                TypeExprKind.Builtin when arguments.Length == 0 => TypeExpr.Builtin((TypeTag)dto.Segment0Field2),
                TypeExprKind.Named => TypeExpr.Named(String(dto.Segment0Field3), arguments),
                TypeExprKind.VectorArray when arguments.Length == 1 => TypeExpr.VectorArray(arguments[0]),
                TypeExprKind.Rank2Array or TypeExprKind.Rank3Array or TypeExprKind.Rank4Array when arguments.Length == 1 =>
                    TypeExpr.MultiDimArray(arguments[0], (int)kind - (int)TypeExprKind.VectorArray + 1),
                TypeExprKind.List when arguments.Length == 1 => TypeExpr.List(arguments[0]),
                TypeExprKind.Nullable when arguments.Length == 1 => TypeExpr.Nullable(arguments[0]),
                TypeExprKind.Dictionary when arguments.Length == 2 => TypeExpr.Dictionary(arguments[0], arguments[1]),
                _ => throw new InvalidDataException("Unsupported or nonclosed TypeExpr constructor/arity."),
            });
            _typeMemo.Add(id, type);
            _typeHeights.Add(id, 1 + (operands.Length == 0 ? 0 : operands.Max(child => _typeHeights[child])));
            return type;
        } finally { _active.Remove(id); }
    }
}
