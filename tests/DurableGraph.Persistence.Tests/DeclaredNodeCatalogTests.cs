using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using DescriptorDto = Atelia.DurableGraph.Generated.Family_503044.V1;
using SlotDto = Atelia.DurableGraph.Generated.Family_503046.V1;
using TypeDto = Atelia.DurableGraph.Generated.Family_503054.V1;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class DeclaredNodeCatalogTests : IDisposable {
    private const string Prefix = "dg-declared-node-catalog-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Fact]
    public void A_cold_catalog_decodes_the_positions_and_appends_a_version_without_rewriting_shared_slots() {
        TypeExpr text = TypeExpr.Builtin(TypeTag.String);
        DeclaredSlotShape[] slots = [
            new(DeclaredSlotKind.Int32), new(DeclaredSlotKind.Reference, text),
            new(DeclaredSlotKind.References, TypeExpr.VectorArray(text)), new(DeclaredSlotKind.Private, null, "Format"),
        ];
        DurableSchema owner = DurableSchema.ForDeclaredNode(TypeExpr.Named("Node"), 1, slots);
        DurableSchema peer = DurableSchema.ForDeclaredNode(TypeExpr.Named("Peer"), 1, slots);
        DurableSchema empty = DurableSchema.ForDeclaredNode(TypeExpr.Named("Empty"), 1, []);
        // This arity-one domain family deliberately shares the spelling of the private format.
        DurableSchema domain = new(TypeExpr.Named("Format", text), 1, SchemaKind.InlineValue);
        DurableSchema next = DurableSchema.ForDeclaredNode(owner.Type, 2, slots[..2]);
        FrameAddress first, second;
        using (var repository = RepositoryStorageFacade.CreateNew(_path)) {
            SchemaCatalogWorkspace workspace = new(static () => { });
            first = repository.Store.AppendMeta(null, workspace.PrepareOwned([], [owner, peer, empty, domain]).Write!).Address;
            repository.Data.ConfirmDurable();
        }
        using (var repository = RepositoryStorageFacade.OpenExisting(_path)) {
            SchemaCatalogView cold = SchemaCatalogView.Load(repository.Store, first);
            Assert.Equal(owner, cold.ResolveLayout(cold.SchemaNodeId(owner)).Schema);
            Assert.Empty(SlotIds(cold, empty));
            ObjectId[] ids = SlotIds(cold, owner);
            Assert.Equal(ids, SlotIds(cold, peer));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, ids.Select(id => cold.Rows[id].GetState<SlotDto>().Segment0Field2));
            Assert.Equal(new[] { 1, 2, 3, 4 }, ids.Select(id => cold.Rows[id].GetState<SlotDto>().Segment0Field1));
            ObjectId operand = cold.Rows[ids[3]].GetState<SlotDto>().Segment0Field3;
            TypeDto format = cold.Rows[operand].GetState<TypeDto>();
            Assert.Equal((byte)TypeExprKind.Named, format.Segment0Field1);
            Assert.Equal("Format", cold.Rows[format.Segment0Field3].StringContent);
            Assert.True(format.Segment0Field4.IsNull);

            SchemaCatalogWorkspace workspace = new(static () => { }, cold);
            Assert.Null(workspace.PrepareOwned([], [owner]).Write);
            Assert.Throws<SchemaConflictException>(() => workspace.Set(new DurableSchema(empty.Type, 1)));
            PreparedSchemaCatalogWrite update = workspace.PrepareOwned([], [next]);
            Assert.Equal(1, update.AddedDescriptors);
            Assert.Empty(update.EncodedIds.Intersect(ids));
            second = repository.Store.AppendMeta(first, update.Write!).Address;
            repository.Data.ConfirmDurable();
        }
        using var reader = RepositoryStorageFacade.OpenReadOnlyExisting(_path);
        SchemaCatalogView both = SchemaCatalogView.Load(reader.Store, second);
        ObjectId[] original = SlotIds(both, owner);
        Assert.Equal(original[..2], SlotIds(both, next));
        Assert.NotEqual(both.SchemaNodeId(owner), both.SchemaNodeId(next));
        var originalHeads = reader.Store.ReadMetaGraph(first).ObjectHeads;
        foreach (ObjectId id in original) {
            Assert.Equal(originalHeads[id.Value], both.Snapshot!.ObjectHeads[id.Value]);
        }
    }

    [Fact]
    public void A_cold_catalog_accepts_a_closed_generic_reference_target_but_rejects_it_as_a_declared_node() {
        TypeExpr generic = TypeExpr.Named("Generic", TypeExpr.Builtin(TypeTag.Int32));
        DurableSchema target = new(generic, 1);
        DurableSchema owner = DurableSchema.ForDeclaredNode(TypeExpr.Named("Owner"), 1, [
            new(DeclaredSlotKind.Reference, generic), new(DeclaredSlotKind.References, generic),
        ]);
        PreparedSchemaCatalogWrite catalog = new SchemaCatalogWorkspace(static () => { }).PrepareOwned([], [target, owner]);
        FrameAddress original, corrupt;
        using (var repository = RepositoryStorageFacade.CreateNew(_path)) {
            original = repository.Store.AppendMeta(null, catalog.Write!).Address;
            ObjectId id = catalog.View.SchemaNodeId(target);
            ObjectStateRecord row = catalog.View.Rows[id];
            DescriptorDto dto = row.GetState<DescriptorDto>();
            // Change only the descriptor grammar; its closed type and empty slot list remain valid.
            DescriptorDto changed = new(6, dto.Segment0Field2, dto.Segment0Field3, dto.Segment0Field4, default, 0, default);
            ObjectStateRecord replacement = BootstrapBindings.WithPreparation(new(id, row.Schema!, changed), BootstrapBindings.CreateEnvironment());
            byte[] body = TypedBaseBodyCodec.Encode(row.Layout, replacement.Preparation!.PrepareBase(replacement));
            corrupt = repository.Store.AppendMeta(original, new(original, catalog.View.RootId, [ObjectVersionRecord.CreateBase(id.Value, body)])).Address;
            repository.Data.ConfirmDurable();
        }
        using var reader = RepositoryStorageFacade.OpenReadOnlyExisting(_path);
        SchemaCatalogView valid = SchemaCatalogView.Load(reader.Store, original);
        Assert.Equal(target, valid.Schemas[new(generic, 1)]);
        Assert.Equal(owner, valid.Schemas[new(owner.Type, 1)]);
        var error = Assert.Throws<InvalidDataException>(() => SchemaCatalogView.Load(reader.Store, corrupt));
        Assert.Equal("type", Assert.IsType<ArgumentException>(error.InnerException).ParamName);
    }

    [Theory]
    [InlineData("absent-name")]
    [InlineData("wrong-name-row")]
    [InlineData("inactive-builtin-tag")]
    public void A_private_format_requires_a_well_formed_named_type_expression(string error) {
        DurableSchema owner = DurableSchema.ForDeclaredNode(TypeExpr.Named("Node"), 1, [new(DeclaredSlotKind.Private, formatId: "Format")]);
        SchemaCatalogView view = new SchemaCatalogWorkspace(static () => { }).PrepareOwned([], [owner]).View;
        var rows = view.Rows.ToDictionary();
        ObjectId slot = Assert.Single(SlotIds(view, owner));
        ObjectId operand = rows[slot].GetState<SlotDto>().Segment0Field3;
        TypeDto original = rows[operand].GetState<TypeDto>();
        TypeDto bad = error switch {
            "absent-name" => new((byte)TypeExprKind.Named, 0, default, default),
            "wrong-name-row" => new((byte)TypeExprKind.Named, 0, slot, default),
            "inactive-builtin-tag" => new((byte)TypeExprKind.Named, (byte)TypeTag.Int32, original.Segment0Field3, default),
            _ => throw new ArgumentOutOfRangeException(nameof(error)),
        };
        rows[operand] = new(operand, rows[operand].Schema!, bad);
        Assert.Throws<InvalidDataException>(() => new SchemaCatalogView(view.RootId, rows));
    }

    [Theory]
    [InlineData("position")]
    [InlineData("unknown-tag")]
    [InlineData("scalar-with-target")]
    [InlineData("reference-without-target")]
    [InlineData("reference-to-int")]
    [InlineData("references-to-inline")]
    [InlineData("private-with-generic-name")]
    [InlineData("private-with-inline-child")]
    [InlineData("private-with-nullable-child")]
    public void Stored_slot_projection_rejects_bad_positions_operands_and_conflicting_target_kinds(string error) {
        TypeExpr integer = TypeExpr.Builtin(TypeTag.Int32);
        DurableSchema inline = new(TypeExpr.Named("Format", integer), 1, SchemaKind.InlineValue);
        DurableSchema owner = DurableSchema.ForDeclaredNode(TypeExpr.Named("Node"), 1, [new(DeclaredSlotKind.Int32)]);
        SchemaCatalogView view = new SchemaCatalogWorkspace(static () => { }).PrepareOwned([], [inline, owner]).View;
        Dictionary<ObjectId, ObjectStateRecord> rows = view.Rows.ToDictionary();
        ObjectId id = Assert.Single(SlotIds(view, owner));
        ObjectId name = rows[view.SchemaNodeId(owner)].GetState<DescriptorDto>().Segment0Field2;
        ObjectId generic = rows[view.SchemaNodeId(inline)].GetState<DescriptorDto>().Segment0Field2;
        ObjectId args = rows[generic].GetState<TypeDto>().Segment0Field4;
        ObjectId intType = Assert.Single(rows[args].GetListState<ObjectId>().Elements.ToArray());
        SlotDto bad = error switch {
            "position" => new(2, 1, default, default, default),
            "unknown-tag" => new(1, 99, default, default, default),
            "scalar-with-target" => new(1, 1, generic, default, default),
            "reference-without-target" => new(1, 2, default, default, default),
            "reference-to-int" => new(1, 2, intType, default, default),
            "references-to-inline" => new(1, 3, generic, default, default),
            "private-with-generic-name" => new(1, 4, generic, default, default),
            "private-with-inline-child" => new(1, 4, name, view.SchemaNodeId(inline), default),
            "private-with-nullable-child" => new(1, 4, name, default, id),
            _ => throw new ArgumentOutOfRangeException(nameof(error)),
        };
        rows[id] = new(id, rows[id].Schema!, bad);
        Assert.Throws<InvalidDataException>(() => new SchemaCatalogView(view.RootId, rows));
    }

    [Theory]
    [InlineData("base")]
    [InlineData("codec-version")]
    [InlineData("element")]
    public void A_declared_descriptor_rejects_inactive_operands(string error) {
        DurableSchema schema = DurableSchema.ForDeclaredNode(TypeExpr.Named("Node"), 1, [new(DeclaredSlotKind.Int32)]);
        DurableSchema parent = new("Parent", 1);
        SchemaCatalogView view = new SchemaCatalogWorkspace(static () => { }).PrepareOwned([], [schema, parent]).View;
        var rows = view.Rows.ToDictionary();
        ObjectId id = view.SchemaNodeId(schema);
        DescriptorDto dto = rows[id].GetState<DescriptorDto>();
        DescriptorDto bad = new(6, dto.Segment0Field2, 1, dto.Segment0Field4,
            error == "element" ? Assert.Single(SlotIds(view, schema)) : default,
            error == "codec-version" ? 1u : 0u, error == "base" ? view.SchemaNodeId(parent) : default);
        rows[id] = new(id, rows[id].Schema!, bad);
        Assert.Throws<InvalidDataException>(() => new SchemaCatalogView(view.RootId, rows));
    }

    private static ObjectId[] SlotIds(SchemaCatalogView view, DurableSchema schema) {
        DescriptorDto dto = view.Rows[view.SchemaNodeId(schema)].GetState<DescriptorDto>();
        Assert.Equal((byte)6, dto.Segment0Field1);
        return view.Rows[dto.Segment0Field4].GetListState<ObjectId>().Elements.ToArray();
    }

    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
