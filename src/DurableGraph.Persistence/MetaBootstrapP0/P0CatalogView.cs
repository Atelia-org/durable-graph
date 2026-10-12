using System.Collections.Frozen;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using CatalogDto = Atelia.DurableGraph.Generated.Family_503043.V1;
using DescriptorDto = Atelia.DurableGraph.Generated.Family_503044.V1;
using FieldDto = Atelia.DurableGraph.Generated.Family_503046.V1;

namespace Atelia.DurableGraph.Persistence.MetaBootstrapP0;

/// <summary>DTO-to-factory projection for the P0 finite, explicitly checked descriptor shapes.</summary>
internal sealed class P0CatalogView {
    private static readonly StateModelSnapshot FixedModels = P0FixedBindings.CreateSnapshot();
    private readonly Dictionary<ObjectLayout, ObjectId> _ids = [];
    private readonly Dictionary<ObjectId, ObjectLayout> _layouts = [];
    private readonly Dictionary<SchemaKey, DurableSchema> _schemas = [];

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
        HashSet<ObjectId> declared = [];
        foreach (ObjectId id in DescriptorIds) {
            if (id.IsNull || !declared.Add(id)) { throw new InvalidDataException("Catalog descriptors must be nonzero and unique."); }
            DescriptorDto node = Require(id, typeof(P0DescriptorRecord)).GetState<DescriptorDto>();
            ObjectLayout layout;
            if (node.Segment0Field1 == 1) {
                if (!node.Segment0Field5.IsNull || node.Segment0Field6 != 0) { throw new InvalidDataException("Wrong Schema descriptor variant."); }
                string name = String(node.Segment0Field2);
                ObjectStateRecord fields = Require(node.Segment0Field4, typeof(List<P0FieldRecord>));
                DurableFieldInfo[] values = fields.GetListState<ObjectId>().Elements.ToArray().Select(Field).ToArray();
                DurableSchema schema = new(name, node.Segment0Field3, values);
                try { P0CatalogWorkspace.Validate(schema); }
                catch (NotSupportedException error) { throw new InvalidDataException("Unsupported P0 Schema descriptor.", error); }
                if (!_schemas.TryAdd(new(schema.Type, schema.Version), schema)) { throw new InvalidDataException("Duplicate exact Schema key."); }
                layout = ObjectLayout.ForDurable(schema);
            } else if (node.Segment0Field1 == 2) {
                if (!node.Segment0Field2.IsNull || node.Segment0Field3 != 0 || !node.Segment0Field4.IsNull ||
                    node.Segment0Field6 != 2) { throw new InvalidDataException("Wrong List descriptor variant."); }
                DurableFieldInfo element = Field(node.Segment0Field5);
                if (element.TypeTag != TypeTag.Int32 || element.FieldId != 1) { throw new InvalidDataException("P0 only accepts List<int>."); }
                layout = ObjectLayout.ForList(new(element, node.Segment0Field6));
            } else { throw new InvalidDataException("Unknown P0 descriptor variant."); }
            if (!_ids.TryAdd(layout, id)) { throw new InvalidDataException("Duplicate complete descriptor layout."); }
            _layouts.Add(id, layout);
        }
        Schemas = _schemas.ToFrozenDictionary();
    }

    internal ObjectId RootId { get; }
    internal ObjectId DescriptorListId { get; }
    internal IReadOnlyList<ObjectId> DescriptorIds { get; }
    internal IReadOnlyDictionary<ObjectId, ObjectStateRecord> Rows { get; }
    internal IReadOnlyDictionary<SchemaKey, DurableSchema> Schemas { get; }
    internal P0GraphSnapshot? Snapshot { get; private set; }
    internal long DescriptorListH { get; }
    internal void AttachSnapshot(P0GraphSnapshot snapshot) {
        if (Snapshot is not null || snapshot.RootId != RootId) { throw new InvalidOperationException("A catalog candidate can only acquire its own snapshot once."); }
        Snapshot = snapshot;
    }

    internal ObjectId DescriptorId(ObjectLayout layout) => _ids.TryGetValue(layout, out ObjectId id) ? id
        : throw new InvalidDataException("The selected catalog lacks this complete object layout.");

    internal ObjectLayout ResolveLayout(ObjectId id) => _layouts.TryGetValue(id, out ObjectLayout? layout) ? layout
        : throw new InvalidDataException("This meta ObjectId is not a declared object descriptor.");

    internal static P0CatalogView Load(P0GraphStore store, FrameAddress graph) {
        P0DecodedRows decoded = P0TypedReader.Read(store, graph);
        if (decoded.Snapshot.Kind != P0GraphKind.Meta) { throw new InvalidDataException("A Schema root must select a meta graph."); }
        CatalogDto root = decoded.Rows[decoded.Snapshot.RootId].GetState<CatalogDto>();
        long h = decoded.Storage[root.Segment0Field1].ReconstructionPayloadBytes;
        return new(decoded.Snapshot.RootId, decoded.Rows, decoded.Snapshot, h);
    }

    private ObjectStateRecord Require(ObjectId id, Type type) {
        if (id.IsNull || !Rows.TryGetValue(id, out ObjectStateRecord? row) ||
            !FixedModels.TryGetCurrentObjectBinding(type, out ObjectBinding? binding) || !binding!.CurrentLayout.Equals(row.Layout)) {
            throw new InvalidDataException("Missing or wrongly typed employee node.");
        }
        return row;
    }

    private string String(ObjectId id) => Require(id, typeof(string)).StringContent;

    private DurableFieldInfo Field(ObjectId id) {
        FieldDto dto = Require(id, typeof(P0FieldRecord)).GetState<FieldDto>();
        TypeTag tag = (TypeTag)dto.Segment0Field2;
        if (tag == TypeTag.ObjectReference) {
            TypeExpr target = dto.Segment0Field3 switch {
                1 => TypeExpr.Named(String(dto.Segment0Field4)),
                2 when dto.Segment0Field4.IsNull => TypeExpr.List(TypeExpr.Builtin(TypeTag.Int32)),
                _ => throw new InvalidDataException("Unsupported P0 reference constraint."),
            };
            return DurableFieldInfo.Reference(dto.Segment0Field1, target);
        }
        if (!TypeTagFacts.IsBuiltin(tag) || dto.Segment0Field3 != 0 || !dto.Segment0Field4.IsNull) {
            throw new InvalidDataException("Unsupported or inconsistent P0 scalar field.");
        }
        return new(dto.Segment0Field1, tag);
    }
}
