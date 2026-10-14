
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using DescriptorDto = Atelia.DurableGraph.Generated.Family_503044.V1;
using FieldDto = Atelia.DurableGraph.Generated.Family_503046.V1;
using TypeDto = Atelia.DurableGraph.Generated.Family_503054.V1;

namespace Atelia.DurableGraph.Persistence.Tests;

// Small malformed catalogs use complete cold frames. The 256-level witness is
// owned DTO projection, not a qualification of uncached full-store read cost.
public sealed class MetaBootstrapArrayTests : IDisposable {
    private const string Prefix = "dg-bootstrap-array-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Builtin_nullable_arrays_preserve_rank_shape_and_explicit_codec_without_a_Schema_lookup(int rank) {
        int[] lengths = Enumerable.Repeat(1, rank).ToArray();
        lengths[^1] = 3;
        ArrayLayout layout = new((TypeExprKind)(rank + 3), DurableFieldInfo.Nullable(1, new(1, TypeTag.Int32)), codecVersion: 1);
        NullableState<int>[] values = [default, new(int.MinValue), new(int.MaxValue)];
        FrozenArrayState<NullableState<int>> state = new(new(lengths), values);
        PreparedBaseBody raw = ArrayStateBody<NullableState<int>, NullableStateOps<int, Int32StateOps>>.PrepareBase(state, layout);
        byte[] bytes = TypedBaseBodyCodec.Encode(ObjectLayout.ForArray(layout), raw);
        Assert.Equal((byte)5, bytes[0]);
        Assert.Equal((byte)2, bytes[1]);
        var decoded = TypedBaseBodyCodec.Decode(bytes, null);
        Assert.Equal(ObjectLayout.ForArray(layout), decoded.Layout);
        Assert.Equal(1u, decoded.Layout.Array!.CodecVersion);
        ModelExecutionEnvironment models = NoSchemas();
        ObjectStateRecord row = BootstrapBindings.Reader(decoded.Layout, models).Read(new(1), new SingleBody(decoded.Body));
        Assert.Equal(new ArrayShape(lengths), row.GetArrayState<NullableState<int>>().Shape);
        Assert.Equal(values, row.GetArrayState<NullableState<int>>().Elements.ToArray());
        ObjectStateRecord prepared = BootstrapBindings.WithPreparation(row, models);
        Assert.Equal(raw.Body, prepared.Preparation!.PrepareBase(prepared).Body);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ArrayLayout(layout.Constructor, layout.ElementSlot, codecVersion: 2));
    }

    [Fact]
    public void Identical_valid_body_bytes_do_not_allow_a_vector_reference_to_accept_a_zero_width_matrix() {
        ArrayLayout vector = new(TypeExprKind.VectorArray, new(1, TypeTag.Int32), codecVersion: 1);
        ArrayLayout matrix = new(TypeExprKind.Rank2Array, new(1, TypeTag.Int32), codecVersion: 1);
        byte[] raw = [1, 0]; // Vector { 0 }, or matrix dimensions [1, 0] and no elements.
        ObjectStateRecord one = BootstrapBindings.Reader(ObjectLayout.ForArray(vector), NoSchemas()).Read(new(2), new SingleBody(raw));
        ObjectStateRecord zero = BootstrapBindings.Reader(ObjectLayout.ForArray(matrix), NoSchemas()).Read(new(2), new SingleBody(raw));
        Assert.Equal(new[] { 0 }, one.GetArrayState<int>().Elements.ToArray());
        Assert.Equal(new ArrayShape(1, 0), zero.GetArrayState<int>().Shape);
        Assert.Empty(zero.GetArrayState<int>().Elements.ToArray());
        ListLayout outer = new(DurableFieldInfo.Reference(1, vector.Type), codecVersion: 2);
        byte[] outerBody = TypedBaseBodyCodec.Encode(ObjectLayout.ForList(outer),
            ListStateBody<ObjectId, ObjectIdStateOps>.PrepareBase(new([new(2)]), outer));
        FrameAddress good, bad;
        using (var repository = RepositoryStorageFacade.CreateNew(_path)) {
            good = Publish(repository, "good", [ObjectVersionRecord.CreateBase(1, outerBody),
                ObjectVersionRecord.CreateBase(2, TypedBaseBodyCodec.Encode(ObjectLayout.ForArray(vector), new(raw)))]).Address;
            bad = Publish(repository, "bad", [ObjectVersionRecord.CreateBase(1, outerBody),
                ObjectVersionRecord.CreateBase(2, TypedBaseBodyCodec.Encode(ObjectLayout.ForArray(matrix), new(raw)))]).Address;
        }
        using var reader = RepositoryStorageFacade.OpenReadOnlyExisting(_path);
        Assert.Equal(good, reader.GetStateHead("good"));
        Assert.Equal(bad, reader.GetStateHead("bad"));
        Assert.Null(reader.SharedSchemaRoot);
        Assert.Equal(new[] { 0 }, StoredGraphDecoder.Read(reader.Store, good, NoSchemas()).Rows[new(2)].GetArrayState<int>().Elements.ToArray());
        Assert.Equal(2, reader.Store.ReadGraph(bad).ObjectHeads.Count);
        Assert.Throws<InvalidDataException>(() => StoredGraphDecoder.Read(reader.Store, bad, NoSchemas()));
    }

    [Theory]
    [InlineData("elementMismatch")]
    [InlineData("nonArrayType")]
    [InlineData("wrongCodec")]
    [InlineData("inactiveFields")]
    [InlineData("elementId")]
    public void Valid_employee_bodies_do_not_authorize_an_inconsistent_Array_descriptor(string corruption) {
        ObjectLayout array = UserArray(Point(TypeTag.Int32));
        SchemaCatalogView original = Prepare(array).View;
        Dictionary<ObjectId, ObjectStateRecord> rows = original.Rows.ToDictionary();
        ObjectId id = original.DescriptorId(array);
        DescriptorDto descriptor = rows[id].GetState<DescriptorDto>();
        FieldDto element = rows[descriptor.Segment0Field5].GetState<FieldDto>();
        switch (corruption) {
            case "elementMismatch":
                Replace(rows, id, new DescriptorDto(4, descriptor.Segment0Field2, 0, default, element.Segment0Field5, 1, default));
                break;
            case "nonArrayType":
                TypeDto type = rows[descriptor.Segment0Field2].GetState<TypeDto>();
                Replace(rows, descriptor.Segment0Field2, new TypeDto((byte)TypeExprKind.List, 0, default, type.Segment0Field4));
                break;
            case "wrongCodec":
                Replace(rows, id, new DescriptorDto(4, descriptor.Segment0Field2, 0, default, descriptor.Segment0Field5, 2, default));
                break;
            case "inactiveFields":
                ObjectId validFields = rows[original.SchemaNodeId(Point(TypeTag.Int32))].GetState<DescriptorDto>().Segment0Field4;
                Replace(rows, id, new DescriptorDto(4, descriptor.Segment0Field2, 0, validFields, descriptor.Segment0Field5, 1, default));
                break;
            default:
                Replace(rows, descriptor.Segment0Field5, new FieldDto(2, element.Segment0Field2,
                    element.Segment0Field3, element.Segment0Field4, element.Segment0Field5));
                break;
        }
        FrameAddress meta = WriteMeta(original.RootId, rows);
        using var reader = RepositoryStorageFacade.OpenReadOnlyExisting(_path);
        Assert.Equal((byte)4, StoredGraphDecoder.Read(reader.Store, meta).Rows[id].GetState<DescriptorDto>().Segment0Field1);
        Assert.Throws<InvalidDataException>(() => SchemaCatalogView.Load(reader.Store, meta));
    }

    [Fact]
    public void Named_reference_arrays_require_a_dynamic_descriptor_despite_having_no_inline_dependency() {
        ObjectLayout layout = ObjectLayout.ForArray(new(TypeExprKind.VectorArray,
            DurableFieldInfo.Reference(1, TypeExpr.Named("Array.UnknownUser")), codecVersion: 1));
        Assert.Null(layout.Array!.ElementSlot.ValueSchema);
        Assert.Throws<InvalidDataException>(() => TypedBaseBodyCodec.Encode(layout, new PreparedBaseBody([0])));
        PreparedSchemaCatalogWrite prepared = Prepare(layout);
        FrameAddress meta = WriteMeta(prepared.View.RootId, prepared.View.Rows);
        using var reader = RepositoryStorageFacade.OpenReadOnlyExisting(_path);
        SchemaCatalogView loaded = SchemaCatalogView.Load(reader.Store, meta);
        Assert.Empty(loaded.Schemas); // Nominal references need not register the referenced user's body.
        Assert.Single(loaded.DescriptorIds);
        Assert.Equal(layout, loaded.ResolveLayout(loaded.DescriptorId(layout)));
        byte[] body = TypedBaseBodyCodec.Encode(layout, new PreparedBaseBody([0]), loaded);
        Assert.Equal((byte)5, body[0]);
        Assert.Equal((byte)1, body[1]);
        Assert.Throws<InvalidDataException>(() => TypedBaseBodyCodec.Decode(body, null));
        Assert.Equal(layout, TypedBaseBodyCodec.Decode(body, loaded).Layout);
    }

    [Fact]
    public void Equal_nominal_Array_types_keep_distinct_complete_inline_versions_after_cold_meta_projection() {
        ObjectLayout first = UserArray(Point(TypeTag.Int32)), second = UserArray(Point(TypeTag.Int32, version: 2));
        Assert.Equal(first.Type, second.Type);
        Assert.NotEqual(first, second);
        PreparedSchemaCatalogWrite prepared = Prepare(first, second);
        FrameAddress meta = WriteMeta(prepared.View.RootId, prepared.View.Rows);
        using var reader = RepositoryStorageFacade.OpenReadOnlyExisting(_path);
        SchemaCatalogView loaded = SchemaCatalogView.Load(reader.Store, meta);
        Assert.Equal(2, loaded.Schemas.Count);
        Assert.NotEqual(loaded.DescriptorId(first), loaded.DescriptorId(second));
        foreach (ObjectLayout layout in new[] { first, second }) {
            // This zero-width matrix exercises descriptor identity, not a hand-written inline body codec.
            var decoded = TypedBaseBodyCodec.Decode(TypedBaseBodyCodec.Encode(layout, new PreparedBaseBody([1, 0]), loaded), loaded);
            Assert.Equal(layout, decoded.Layout);
            Assert.Equal(new byte[] { 1, 0 }, decoded.Body);
        }
    }

    [Fact]
    public void An_unchanged_nominal_owner_does_not_authorize_conflicting_exact_Array_dependencies() {
        DurableSchema pointA = Point(TypeTag.Int32), pointB = Point(TypeTag.Int64);
        ObjectLayout arrayA = UserArray(pointA), arrayB = UserArray(pointB);
        DurableSchema root = new("Array.Owner", 1, DurableFieldInfo.Reference(1, arrayA.Type));
        SchemaCatalogWorkspace catalog = new(static () => { });
        catalog.Set(root);
        PreparedSchemaCatalogWrite initial = catalog.PrepareOwned([arrayA]);
        using var repository = RepositoryStorageFacade.CreateNew(_path);
        using (PendingRepositorySave pending = repository.Prepare("main", StoredGraphKind.State,
            new(null, new(1), [ObjectVersionRecord.CreateBase(1,
                TypedBaseBodyCodec.Encode(ObjectLayout.ForDurable(root), new PreparedBaseBody([2]), initial.View)),
                ObjectVersionRecord.CreateBase(2, TypedBaseBodyCodec.Encode(arrayA, new PreparedBaseBody([1, 0]), initial.View))]), initial.Write)) {
            repository.Commit(pending, initial.Install, prepareMeta: initial.PrepareInstall);
        }
        FrameAddress schema = repository.SharedSchemaRoot!.Value;
        long frames = repository.Store.AppendedFrames;
        catalog.Set(root);
        Assert.Throws<SchemaConflictException>(() => catalog.PrepareOwned([arrayB]));
        Assert.Equal(frames, repository.Store.AppendedFrames);
        Assert.Equal(schema, repository.SharedSchemaRoot);
        Assert.Equal(pointA, catalog.Accepted!.Schemas[new(pointA.Type, 1)]);
        Assert.Equal(arrayA, catalog.Accepted.ResolveLayout(catalog.Accepted.DescriptorId(arrayA)));
    }

    [Fact]
    public void Discard_recomputes_captured_Array_dependencies_but_preserves_other_explicit_root_intents() {
        DurableSchema pointA = Point(TypeTag.Int32), pointB = Point(TypeTag.Int64);
        ObjectLayout arrayA = UserArray(pointA), arrayB = UserArray(pointB);
        SchemaCatalogWorkspace catalog = new(static () => { });
        PreparedSchemaCatalogWrite abandoned = catalog.PrepareOwned([arrayA]);
        using var repository = RepositoryStorageFacade.CreateNew(_path);
        using (PendingRepositorySave pending = repository.Prepare("discarded", StoredGraphKind.State,
            new(null, new(1), [ObjectVersionRecord.CreateBase(1,
                TypedBaseBodyCodec.Encode(arrayA, new PreparedBaseBody([1, 0]), abandoned.View))]), abandoned.Write)) { }
        Assert.Equal(0, repository.Store.AppendedFrames);
        Assert.Null(repository.GetHead("discarded"));
        Assert.Null(catalog.Accepted);
        Assert.Equal(0, catalog.DirtyCount);
        PreparedSchemaCatalogWrite corrected = catalog.PrepareOwned([arrayB]);
        Assert.Equal(pointB, Assert.Single(corrected.View.Schemas).Value);
        Assert.Equal(pointA, Assert.Single(abandoned.View.Schemas).Value);
        Assert.Equal(arrayB, corrected.View.ResolveLayout(corrected.View.DescriptorId(arrayB)));
        catalog.Set(new DurableSchema("Array.OtherOwner", 1, new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: pointA)));
        Assert.Throws<SchemaConflictException>(() => catalog.PrepareOwned([arrayB]));
        Assert.Equal(1, catalog.DirtyCount);
        catalog.Set(new DurableSchema("Array.OtherOwner", 1, new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: pointB)));
        Assert.Equal(pointB, catalog.PrepareOwned([arrayB]).View.Schemas[new(pointB.Type, 1)]);
    }

    [Theory]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public void An_Array_wrapper_does_not_consume_the_exact_inline_Schema_depth_budget(int depth, bool valid) {
        DurableSchema inline = new("Array.Value0", 1, SchemaKind.InlineValue);
        for (int index = 1; index < depth; index++) {
            inline = new DurableSchema($"Array.Value{index}", 1, SchemaKind.InlineValue,
                new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: inline));
        }
        ObjectLayout array = UserArray(inline);
        SchemaCatalogWorkspace owner = new(static () => { });
        owner.Set(array);
        if (!valid) { Assert.Throws<InvalidDataException>(() => owner.Prepare()); return; }
        SchemaCatalogView prepared = owner.Prepare().View;
        Dictionary<ObjectId, ObjectStateRecord> rows = prepared.Rows.ToDictionary();
        ObjectStateRecord declarations = rows[prepared.DescriptorListId];
        rows[declarations.Id] = new(declarations.Id, declarations.Layout.List!, new FrozenListState<ObjectId>(prepared.DescriptorIds.Reverse().ToArray()));
        SchemaCatalogView loaded = new(prepared.RootId, rows);
        Assert.Equal(depth, loaded.Schemas.Count);
        Assert.Equal(array, loaded.ResolveLayout(loaded.DescriptorId(array)));
    }

    [Fact]
    public void A_builtin_Array_cannot_acquire_a_dynamic_descriptor_from_legal_employee_rows() {
        ObjectLayout dynamic = ObjectLayout.ForArray(new(TypeExprKind.VectorArray,
            DurableFieldInfo.Reference(1, TypeExpr.Named("Array.Nominal")), codecVersion: 1));
        SchemaCatalogView original = Prepare(dynamic).View;
        Dictionary<ObjectId, ObjectStateRecord> rows = original.Rows.ToDictionary();
        ObjectId descriptor = original.DescriptorId(dynamic);
        DescriptorDto d = rows[descriptor].GetState<DescriptorDto>();
        TypeDto array = rows[d.Segment0Field2].GetState<TypeDto>();
        ObjectId scalar = Assert.Single(rows[array.Segment0Field4].GetListState<ObjectId>().Elements.ToArray());
        Replace(rows, scalar, new TypeDto((byte)TypeExprKind.Builtin, (byte)TypeTag.Int32, default, default));
        Replace(rows, d.Segment0Field5, new FieldDto(1, (byte)TypeTag.Int32, default, default, default));
        FrameAddress meta = WriteMeta(original.RootId, rows);
        using var reader = RepositoryStorageFacade.OpenReadOnlyExisting(_path);
        Assert.Equal((byte)4, StoredGraphDecoder.Read(reader.Store, meta).Rows[descriptor].GetState<DescriptorDto>().Segment0Field1);
        Assert.Throws<InvalidDataException>(() => SchemaCatalogView.Load(reader.Store, meta));
    }

    [Fact]
    public void A_structural_Array_has_a_valid_standalone_reader_but_cannot_enter_the_finite_meta_graph() {
        PreparedSchemaCatalogWrite prepared = Prepare();
        ObjectLayout layout = ObjectLayout.ForArray(new(TypeExprKind.VectorArray, new(1, TypeTag.Int32), codecVersion: 1));
        byte[] body = TypedBaseBodyCodec.Encode(layout, new PreparedBaseBody([1, 0]));
        var decoded = TypedBaseBodyCodec.Decode(body, null);
        Assert.Equal(new[] { 0 }, BootstrapBindings.Reader(decoded.Layout).Read(new(100), new SingleBody(decoded.Body)).GetArrayState<int>().Elements.ToArray());
        FrameAddress meta = WriteMeta(prepared.View.RootId, prepared.View.Rows, ObjectVersionRecord.CreateBase(100, body));
        using var reader = RepositoryStorageFacade.OpenReadOnlyExisting(_path);
        Assert.Throws<InvalidDataException>(() => SchemaCatalogView.Load(reader.Store, meta));
    }

    private static DurableSchema Point(TypeTag tag, int version = 1) =>
        new("Array.Point", version, SchemaKind.InlineValue, new DurableFieldInfo(1, tag));
    private static ObjectLayout UserArray(DurableSchema point) => ObjectLayout.ForArray(new(TypeExprKind.Rank2Array,
        DurableFieldInfo.Nullable(1, new(1, TypeTag.InlineValue, inlineSchema: point)), codecVersion: 1));
    private static ModelExecutionEnvironment NoSchemas() => new StateModelRegistry().CreateEnvironment(
        schemaLookup: _ => throw new InvalidOperationException("A structural builtin Array must not query Schema."));
    private static PreparedSchemaCatalogWrite Prepare(params ObjectLayout[] layouts) {
        SchemaCatalogWorkspace owner = new(static () => { });
        foreach (ObjectLayout layout in layouts) { owner.Set(layout); }
        return owner.Prepare();
    }
    private static void Replace<TState>(Dictionary<ObjectId, ObjectStateRecord> rows, ObjectId id, TState state) where TState : unmanaged {
        rows[id] = new(id, rows[id].Schema!, state);
    }
    private FrameAddress WriteMeta(ObjectId root, IReadOnlyDictionary<ObjectId, ObjectStateRecord> rows, ObjectVersionRecord? extra = null) {
        ModelExecutionEnvironment employees = BootstrapBindings.CreateEnvironment();
        IEnumerable<ObjectVersionRecord> records = rows.Values.Select(row => {
            ObjectStateRecord prepared = BootstrapBindings.WithPreparation(row, employees);
            PreparedBaseBody raw = prepared.Kind == ObjectStateKind.String ? StringBodyPreparer.PrepareBase(prepared.StringContent)
                : prepared.Preparation!.PrepareBase(prepared);
            return ObjectVersionRecord.CreateBase(prepared.Id.Value, TypedBaseBodyCodec.Encode(prepared.Layout, raw));
        });
        if (extra is not null) { records = records.Append(extra); }
        using var repository = RepositoryStorageFacade.CreateNew(_path);
        FrameAddress graph = repository.Store.Append(StoredGraphKind.Meta, null, null, new(null, root, records)).Address;
        repository.Data.ConfirmDurable();
        return graph;
    }
    private static StoredGraphRevision Publish(RepositoryStorageFacade repository, string branch, IEnumerable<ObjectVersionRecord> records) {
        using PendingRepositorySave pending = repository.Prepare(branch, StoredGraphKind.State, new(null, new(1), records));
        return repository.Commit(pending).Business;
    }
    private sealed class SingleBody(byte[] body) : IStateBodySource {
        public int Count => 1;
        public ReadOnlySpan<byte> GetBody(int index) => index == 0 ? body : throw new ArgumentOutOfRangeException(nameof(index));
    }
    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
