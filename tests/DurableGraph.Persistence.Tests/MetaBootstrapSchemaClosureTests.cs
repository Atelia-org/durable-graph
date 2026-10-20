using System.Buffers;
using Atelia.Binary;

using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using CatalogDto = Atelia.DurableGraph.Generated.Family_503043.V1;
using DescriptorDto = Atelia.DurableGraph.Generated.Family_503044.V1;
using FieldDto = Atelia.DurableGraph.Generated.Family_503046.V1;
using TypeDto = Atelia.DurableGraph.Generated.Family_503054.V1;

namespace Atelia.DurableGraph.Persistence.Tests;

// Small corruption witnesses use real cold frames. Depth/expanded-size boundaries
// inspect owned DTO projection directly, without qualifying uncached Store cost.
// Construction failures attach to the declared closure, not irrelevant orphans.
public sealed class MetaBootstrapSchemaClosureTests : IDisposable {
    private const string Prefix = "dg-bootstrap-schema-closure-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Replacing_an_unpublished_root_recomputes_its_exact_dependencies_without_poisoning_the_new_candidate() {
        DurableSchema pointA = Point(TypeTag.Int32), pointB = Point(TypeTag.Int64);
        DurableSchema rootA = InlineOwner("Owner", pointA), rootB = InlineOwner("Owner", pointB);
        SchemaCatalogWorkspace owner = new(static () => { });
        owner.Set(rootA);
        PreparedSchemaCatalogWrite discarded = owner.Prepare();
        Assert.Equal(pointA, discarded.View.Schemas[new(pointA.Type, 1)]);
        owner.Set(rootB);
        PreparedSchemaCatalogWrite corrected = owner.Prepare();
        Assert.Equal(2, corrected.View.Schemas.Count);
        Assert.Equal(rootB, corrected.View.Schemas[new(rootB.Type, 1)]);
        Assert.Equal(pointB, corrected.View.Schemas[new(pointB.Type, 1)]);
        Assert.Equal(pointA, discarded.View.Schemas[new(pointA.Type, 1)]);
        Assert.Equal(rootA, discarded.View.Schemas[new(rootA.Type, 1)]);
        Assert.Null(owner.Accepted);
    }

    [Fact]
    public void Correcting_one_dirty_root_cannot_discard_a_shared_dependency_still_required_by_another_dirty_root() {
        DurableSchema pointA = Point(TypeTag.Int32), pointB = Point(TypeTag.Int64);
        DurableSchema rootA = InlineOwner("Owner", pointA), rootB = InlineOwner("Owner", pointB);
        DurableSchema other = InlineOwner("OtherOwner", pointA);
        SchemaCatalogWorkspace owner = new(static () => { });
        owner.Set(rootA);
        _ = owner.Prepare(); // Abandoning an unaccepted candidate does not clear logical roots.
        owner.Set(other);
        Assert.Throws<SchemaConflictException>(() => { owner.Set(rootB); _ = owner.Prepare(); });
        Assert.Null(owner.Accepted);
        owner.Set(rootA);
        SchemaCatalogView corrected = owner.Prepare().View;
        Assert.Equal(3, corrected.Schemas.Count);
        Assert.Equal(other, corrected.Schemas[new(other.Type, 1)]);
        Assert.Equal(pointA, corrected.Schemas[new(pointA.Type, 1)]);
    }

    [Fact]
    public void Discarded_capture_layouts_do_not_become_permanent_dirty_roots() {
        ObjectLayout listA = NullableList(Point(TypeTag.Int32)), listB = NullableList(Point(TypeTag.Int64));
        SchemaCatalogWorkspace owner = new(static () => { });
        PreparedSchemaCatalogWrite abandoned = owner.PrepareOwned([listA]);
        Assert.Equal(0, owner.DirtyCount);
        PreparedSchemaCatalogWrite next = owner.PrepareOwned([listB]);
        Assert.Equal(0, owner.DirtyCount);
        Assert.Null(owner.Accepted);
        Assert.Equal(listB, next.View.ResolveLayout(next.View.DescriptorId(listB)));
        Assert.Equal(Point(TypeTag.Int64), Assert.Single(next.View.Schemas).Value);
        Assert.Equal(listA, abandoned.View.ResolveLayout(abandoned.View.DescriptorId(listA)));
        Assert.Equal(Point(TypeTag.Int32), Assert.Single(abandoned.View.Schemas).Value);
    }

    [Fact]
    public void An_unchanged_nominal_owner_does_not_authorize_a_conflicting_exact_List_element_layout() {
        DurableSchema pointA = Point(TypeTag.Int32), pointB = Point(TypeTag.Int64);
        TypeExpr nominal = TypeExpr.List(TypeExpr.Nullable(pointA.Type));
        DurableSchema root = new("ListOwner", 1, DurableFieldInfo.Reference(1, nominal));
        ObjectLayout listA = NullableList(pointA), listB = NullableList(pointB);
        SchemaCatalogWorkspace catalog = new(static () => { });
        catalog.Set(root);
        catalog.Set(listA);
        PreparedSchemaCatalogWrite initial = catalog.Prepare();
        using var repository = RepositoryStorageFacade.CreateNew(_path);
        byte[] rootBody = TypedBaseBodyCodec.Encode(ObjectLayout.ForDurable(root), new PreparedBaseBody([0]), initial.View);
        using (var pending = MechanismFixture.Prepare(repository, "main", StoredGraphKind.State,
            new(null, new(1), [ObjectVersionRecord.CreateBase(1, rootBody)]), initial.Write)) {
            repository.Commit(pending, initial.Install, prepareMeta: initial.PrepareInstall);
        }
        FrameAddress schema = repository.SharedSchemaRoot!.Value;
        long frames = repository.Store.AppendedFrames;
        catalog.Set(root); // Its nominal List<Point?> declaration is exactly unchanged.
        Assert.Throws<SchemaConflictException>(() => { catalog.Set(listB); _ = catalog.Prepare(); });
        Assert.Equal(frames, repository.Store.AppendedFrames);
        Assert.Equal(schema, repository.SharedSchemaRoot);
        Assert.Equal(root, catalog.Accepted!.Schemas[new(root.Type, 1)]);
        Assert.Equal(pointA, catalog.Accepted.Schemas[new(pointA.Type, 1)]);
        Assert.Equal(listA, catalog.Accepted.ResolveLayout(catalog.Accepted.DescriptorId(listA)));
    }

    [Theory]
    [InlineData("base")]
    [InlineData("nullable")]
    [InlineData("typeOperand")]
    public void Cold_meta_read_rejects_construction_cycles_in_valid_employee_reference_roles(string edge) {
        DurableSchema root = new("Owner", 1, new DurableFieldInfo(1, TypeTag.Int32));
        SchemaCatalogView view = Prepare(root).View;
        Dictionary<ObjectId, ObjectStateRecord> rows = view.Rows.ToDictionary();
        ObjectId descriptor = view.DescriptorId(ObjectLayout.ForDurable(root));
        DescriptorDto d = rows[descriptor].GetState<DescriptorDto>();
        ObjectId field = RootField(rows, descriptor);
        if (edge == "base") {
            Replace(rows, descriptor, new DescriptorDto(d.Segment0Field1, d.Segment0Field2, d.Segment0Field3,
                d.Segment0Field4, d.Segment0Field5, d.Segment0Field6, descriptor));
        } else if (edge == "nullable") {
            Replace(rows, field, new FieldDto(1, (byte)TypeTag.Nullable, default, default, field));
        } else {
            ObjectId arguments = AddArguments(rows, [d.Segment0Field2]);
            Replace(rows, d.Segment0Field2, new TypeDto((byte)TypeExprKind.List, 0, default, arguments));
        }
        AssertBadCold(view.RootId, rows);
    }

    [Theory]
    [InlineData("typeToField")]
    [InlineData("inlineToReference")]
    [InlineData("nullableToString")]
    public void Cold_meta_read_rejects_wrong_typed_roles_or_exact_dependency_kinds(string error) {
        DurableSchema root = new("Owner", 1, new DurableFieldInfo(1, TypeTag.Int32));
        SchemaCatalogView view = Prepare(root).View;
        Dictionary<ObjectId, ObjectStateRecord> rows = view.Rows.ToDictionary();
        ObjectId descriptor = view.DescriptorId(ObjectLayout.ForDurable(root));
        DescriptorDto d = rows[descriptor].GetState<DescriptorDto>();
        ObjectId field = RootField(rows, descriptor);
        if (error == "typeToField") {
            Replace(rows, descriptor, new DescriptorDto(d.Segment0Field1, field, d.Segment0Field3,
                d.Segment0Field4, d.Segment0Field5, d.Segment0Field6, default));
        } else if (error == "inlineToReference") {
            Replace(rows, field, new FieldDto(1, (byte)TypeTag.InlineValue, default, descriptor, default));
        } else {
            ObjectId name = rows[d.Segment0Field2].GetState<TypeDto>().Segment0Field3;
            Replace(rows, field, new FieldDto(1, (byte)TypeTag.Nullable, default, default, name));
        }
        AssertBadCold(view.RootId, rows);
    }

    [Theory]
    [InlineData("schemaWithElement")]
    [InlineData("listTypeWithBuiltinTag")]
    [InlineData("scalarWithInline")]
    public void Cold_meta_read_rejects_populated_inactive_variant_fields_in_the_declared_closure(string error) {
        DurableSchema point = Point(TypeTag.Int32);
        DurableSchema root = error == "listTypeWithBuiltinTag"
            ? new("Owner", 1, DurableFieldInfo.Reference(1, TypeExpr.List(TypeExpr.Builtin(TypeTag.Int32))))
            : new("Owner", 1, new DurableFieldInfo(1, TypeTag.Int32));
        SchemaCatalogView view = Prepare(root, point).View;
        Dictionary<ObjectId, ObjectStateRecord> rows = view.Rows.ToDictionary();
        ObjectId descriptor = view.DescriptorId(ObjectLayout.ForDurable(root));
        DescriptorDto d = rows[descriptor].GetState<DescriptorDto>();
        ObjectId field = RootField(rows, descriptor);
        if (error == "schemaWithElement") {
            Replace(rows, descriptor, new DescriptorDto(d.Segment0Field1, d.Segment0Field2, d.Segment0Field3,
                d.Segment0Field4, field, d.Segment0Field6, default));
        } else if (error == "listTypeWithBuiltinTag") {
            ObjectId target = rows[field].GetState<FieldDto>().Segment0Field3;
            TypeDto type = rows[target].GetState<TypeDto>();
            Replace(rows, target, new TypeDto(type.Segment0Field1, (byte)TypeTag.Int32,
                type.Segment0Field3, type.Segment0Field4));
        } else {
            ObjectId inline = InlineDescriptor(view);
            Replace(rows, field, new FieldDto(1, (byte)TypeTag.Int32, default, inline, default));
        }
        AssertBadCold(view.RootId, rows);
    }

    [Theory]
    [InlineData("base")]
    [InlineData("inline")]
    public void Live_but_undeclared_schema_nodes_cannot_supply_exact_dependencies(string dependency) {
        DurableSchema exact = dependency == "base" ? new("Parent", 1) : Point(TypeTag.Int32);
        DurableSchema root = dependency == "base" ? new("Owner", 1, [], exact) : InlineOwner("Owner", exact);
        SchemaCatalogView view = Prepare(root).View;
        Dictionary<ObjectId, ObjectStateRecord> rows = view.Rows.ToDictionary();
        CatalogDto catalog = rows[view.RootId].GetState<CatalogDto>();
        ObjectStateRecord list = rows[catalog.Segment0Field1];
        ObjectId rootDescriptor = view.DescriptorId(ObjectLayout.ForDurable(root));
        rows[list.Id] = new(list.Id, list.Layout.List!, new FrozenListState<ObjectId>([rootDescriptor]));
        Assert.True(rows.Count > 2); // The exact dependency still exists as a complete live employee node.
        AssertBadCold(view.RootId, rows);
    }

    [Fact]
    public void A_known_inline_family_cannot_be_reinterpreted_as_a_nominal_reference_target() {
        DurableSchema point = Point(TypeTag.Int32), root = InlineOwner("Owner", point);
        SchemaCatalogView view = Prepare(root).View;
        Dictionary<ObjectId, ObjectStateRecord> rows = view.Rows.ToDictionary();
        ObjectId field = RootField(rows, view.DescriptorId(ObjectLayout.ForDurable(root)));
        ObjectId type = rows[InlineDescriptor(view)].GetState<DescriptorDto>().Segment0Field2;
        Replace(rows, field, new FieldDto(1, (byte)TypeTag.ObjectReference, type, default, default));
        AssertBadCold(view.RootId, rows);
    }

    [Fact]
    public void A_nominal_reference_cannot_change_the_arity_of_an_already_declared_family() {
        TypeExpr box = TypeExpr.Named("Box", TypeExpr.Builtin(TypeTag.Int32));
        DurableSchema declaration = new(box, 1);
        DurableSchema owner = new("Owner", 1, DurableFieldInfo.Reference(1, box));
        SchemaCatalogView view = Prepare(declaration, owner).View;
        Dictionary<ObjectId, ObjectStateRecord> rows = view.Rows.ToDictionary();
        ObjectId field = RootField(rows, view.DescriptorId(ObjectLayout.ForDurable(owner)));
        TypeDto original = rows[rows[field].GetState<FieldDto>().Segment0Field3].GetState<TypeDto>();
        ObjectId operand = Assert.Single(rows[original.Segment0Field4].GetListState<ObjectId>().Elements.ToArray());
        ObjectId arguments = AddArguments(rows, [operand, operand]);
        ObjectId wrongType = AddType(rows, new TypeDto((byte)TypeExprKind.Named, 0, original.Segment0Field3, arguments));
        Replace(rows, field, new FieldDto(1, (byte)TypeTag.ObjectReference, wrongType, default, default));
        AssertBadCold(view.RootId, rows);
    }

    [Theory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void Type_expression_depth_is_bounded_before_unchecked_recursive_projection(int depth, bool valid) {
        DurableSchema owner = new("Owner", 1,
            DurableFieldInfo.Reference(1, TypeExpr.List(TypeExpr.Builtin(TypeTag.Int32))));
        SchemaCatalogView view = Prepare(owner).View;
        Dictionary<ObjectId, ObjectStateRecord> rows = view.Rows.ToDictionary();
        ObjectId field = RootField(rows, view.DescriptorId(ObjectLayout.ForDurable(owner)));
        TypeDto initialList = rows[rows[field].GetState<FieldDto>().Segment0Field3].GetState<TypeDto>();
        ObjectId type = Assert.Single(rows[initialList.Segment0Field4].GetListState<ObjectId>().Elements.ToArray());
        for (int index = 1; index < depth; index++) {
            ObjectId arguments = AddArguments(rows, [type]);
            type = AddType(rows, new TypeDto((byte)TypeExprKind.List, 0, default, arguments));
        }
        Replace(rows, field, new FieldDto(1, (byte)TypeTag.ObjectReference, type, default, default));
        if (!valid) { AssertBadProjection(view.RootId, rows); return; }
        SchemaCatalogView loaded = new(view.RootId, rows);
        TypeExpr expected = TypeExpr.Builtin(TypeTag.Int32);
        for (int index = 1; index < depth; index++) { expected = TypeExpr.List(expected); }
        Assert.Equal(expected, Assert.Single(loaded.Schemas[new(owner.Type, 1)].Fields).TargetType);
    }

    [Theory]
    [InlineData(11, true)]
    [InlineData(12, false)]
    public void Shared_type_operands_count_the_expanded_tree_rather_than_only_distinct_employee_nodes(int levels, bool valid) {
        DurableSchema owner = new("Owner", 1, new DurableFieldInfo(1, TypeTag.Int32));
        SchemaCatalogView view = Prepare(owner).View;
        Dictionary<ObjectId, ObjectStateRecord> rows = view.Rows.ToDictionary();
        ObjectId name = new(rows.Keys.Max().Value + 1);
        rows.Add(name, new ObjectStateRecord(name, "Nest"));
        ObjectId type = AddType(rows, new TypeDto((byte)TypeExprKind.Builtin, (byte)TypeTag.Int32, default, default));
        for (int index = 0; index < levels; index++) {
            // Sharing one child twice is a legal DAG edge, but both occurrences
            // contribute to TypeExpr's 4096 expanded-node limit.
            ObjectId arguments = AddArguments(rows, [type, type]);
            type = AddType(rows, new TypeDto((byte)TypeExprKind.Named, 0, name, arguments));
        }
        ObjectId descriptor = view.DescriptorId(ObjectLayout.ForDurable(owner));
        DescriptorDto d = rows[descriptor].GetState<DescriptorDto>();
        Replace(rows, descriptor, new DescriptorDto(d.Segment0Field1, type, d.Segment0Field3,
            d.Segment0Field4, d.Segment0Field5, d.Segment0Field6, d.Segment0Field7));
        if (!valid) { AssertBadProjection(view.RootId, rows); return; }
        SchemaCatalogView loaded = new(view.RootId, rows);
        TypeExpr expected = TypeExpr.Builtin(TypeTag.Int32);
        for (int index = 0; index < levels; index++) { expected = TypeExpr.Named("Nest", expected, expected); }
        Assert.Equal(expected, Assert.Single(loaded.Schemas).Key.Type);
    }

    [Theory]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public void Exact_schema_depth_retains_the_existing_256_level_contract(int depth, bool valid) {
        DurableSchema root = new("Ancestor0", 1);
        for (int index = 1; index < depth; index++) { root = new DurableSchema($"Ancestor{index}", 1, [], root); }
        SchemaCatalogWorkspace owner = new(static () => { });
        if (!valid) {
            Assert.Throws<InvalidDataException>(() => { owner.Set(root); _ = owner.Prepare(); });
            Assert.Null(owner.Accepted);
            return;
        }
        owner.Set(root);
        SchemaCatalogView prepared = owner.Prepare().View;
        SchemaCatalogView loaded = new(prepared.RootId, prepared.Rows);
        Assert.Equal(depth, loaded.Schemas.Count);
        Assert.Equal(root, loaded.Schemas[new(root.Type, 1)]);
    }

    [Theory]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public void A_List_shell_does_not_consume_the_exact_inline_schema_depth_budget(int depth, bool valid) {
        DurableSchema inline = new("Value0", 1, SchemaKind.InlineValue);
        for (int index = 1; index < depth; index++) {
            inline = new DurableSchema($"Value{index}", 1, SchemaKind.InlineValue,
                new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: inline));
        }
        ObjectLayout list = ObjectLayout.ForList(new(new(1, TypeTag.InlineValue, inlineSchema: inline)));
        SchemaCatalogWorkspace owner = new(static () => { });
        if (!valid) {
            Assert.Throws<InvalidDataException>(() => { owner.Set(list); _ = owner.Prepare(); });
            Assert.Null(owner.Accepted);
            return;
        }
        owner.Set(list);
        SchemaCatalogView prepared = owner.Prepare().View;
        Dictionary<ObjectId, ObjectStateRecord> rows = prepared.Rows.ToDictionary();
        // Forward dependencies are valid. Reverse declaration order exercises
        // the bounded projection rather than only already-memoized ancestors.
        ObjectStateRecord declarations = rows[prepared.DescriptorListId];
        rows[declarations.Id] = new(declarations.Id, declarations.Layout.List!,
            new FrozenListState<ObjectId>(prepared.DescriptorIds.Reverse().ToArray()));
        SchemaCatalogView loaded = new(prepared.RootId, rows);
        Assert.Equal(depth, loaded.Schemas.Count);
        Assert.Equal(inline, loaded.Schemas[new(inline.Type, 1)]);
        Assert.Equal(list, loaded.ResolveLayout(loaded.DescriptorId(list)));
    }

    [Fact]
    public void Meta_query_without_business_models_accepts_nominal_cycles_and_unknown_reference_names() {
        DurableSchema a = new("A", 1, DurableFieldInfo.Reference(1, TypeExpr.Named("B")));
        DurableSchema b = new("B", 1, DurableFieldInfo.Reference(1, TypeExpr.Named("A")),
            DurableFieldInfo.Reference(2, TypeExpr.Named("AbsentBusinessModel")));
        SchemaCatalogView prepared = Prepare(a, b).View;
        FrameAddress graph = WriteMeta(prepared.RootId, prepared.Rows);
        using var reader = RepositoryStorageFacade.OpenReadOnlyExisting(_path);
        SchemaCatalogView loaded = SchemaCatalogView.Load(reader.Store, graph);
        Assert.Equal(2, loaded.Schemas.Count);
        Assert.Equal(a, loaded.Schemas[new(a.Type, 1)]);
        Assert.Equal(b, loaded.Schemas[new(b.Type, 1)]);
        Assert.False(loaded.Schemas.ContainsKey(new("AbsentBusinessModel", 1)));
    }

    [Fact]
    public void A_null_declaration_list_is_a_cold_catalog_error_after_valid_employee_body_decoding() {
        SchemaCatalogView prepared = Prepare().View;
        Dictionary<ObjectId, ObjectStateRecord> rows = prepared.Rows.ToDictionary();
        Replace(rows, prepared.RootId, new CatalogDto(default));
        FrameAddress graph = WriteMeta(prepared.RootId, rows);
        using var reader = RepositoryStorageFacade.OpenReadOnlyExisting(_path);
        DecodedGraphRows decoded = StoredGraphDecoder.ReadMeta(reader.Store, graph);
        Assert.True(decoded.Rows[prepared.RootId].GetState<CatalogDto>().Segment0Field1.IsNull);
        Assert.Throws<InvalidDataException>(() => SchemaCatalogView.Load(reader.Store, graph));
    }

    [Fact]
    public void Inline_schema_is_declared_and_queryable_but_cannot_be_a_dynamic_object_header() {
        DurableSchema point = Point(TypeTag.Int32);
        SchemaCatalogView prepared = Prepare(point).View;
        ObjectId id = InlineDescriptor(prepared);
        Assert.Contains(id, prepared.DescriptorIds);
        Assert.Equal(point, prepared.Schemas[new(point.Type, 1)]);
        Assert.Throws<InvalidDataException>(() => prepared.ResolveLayout(id));
        byte[] body = Header(5, 1, id.Value);
        Assert.Throws<InvalidDataException>(() => TypedBaseBodyCodec.Decode(body, prepared));
    }

    [Fact]
    public void Fixed_route_rejects_user_layouts_and_the_previous_typed_header_version() {
        DurableSchema user = new("UserLayout", 1, new DurableFieldInfo(1, TypeTag.Int32));
        Assert.Throws<InvalidDataException>(() => TypedBaseBodyCodec.Encode(ObjectLayout.ForDurable(user), new PreparedBaseBody([0])));
        Assert.Throws<InvalidDataException>(() => TypedBaseBodyCodec.Decode(Header(1, 0, 1), null));
        Assert.Throws<InvalidDataException>(() => TypedBaseBodyCodec.Decode(Header(5, 0, uint.MaxValue), null));
    }

    [Fact]
    public void Fixed_List_int_has_a_real_null_catalog_round_trip_but_user_inline_List_requires_its_descriptor_view() {
        ListLayout list = new(new(1, TypeTag.Int32));
        int[] values = [int.MinValue, -17, 0, 29, int.MaxValue];
        PreparedBaseBody raw = ListStateBody<int, Int32StateOps>.PrepareBase(new(values), list);
        byte[] body = TypedBaseBodyCodec.Encode(ObjectLayout.ForList(list), raw);
        var decoded = TypedBaseBodyCodec.Decode(body, null);
        Assert.Equal(ObjectLayout.ForList(list), decoded.Layout);
        ObjectStateRecord row = BootstrapBindings.Reader(decoded.Layout).Read(new(71), new SingleBody(decoded.Body));
        Assert.Equal(values, row.GetListState<int>().Elements.ToArray());

        ObjectLayout withUserValue = NullableList(Point(TypeTag.Int32));
        Assert.Throws<InvalidDataException>(() => TypedBaseBodyCodec.Encode(withUserValue, new PreparedBaseBody([0])));
        SchemaCatalogWorkspace catalog = new(static () => { });
        catalog.Set(withUserValue);
        SchemaCatalogView view = catalog.Prepare().View;
        byte[] dynamicBody = TypedBaseBodyCodec.Encode(withUserValue, new PreparedBaseBody([0]), view);
        Assert.Throws<InvalidDataException>(() => TypedBaseBodyCodec.Decode(dynamicBody, null));
        Assert.Equal(withUserValue, TypedBaseBodyCodec.Decode(dynamicBody, view).Layout);
    }

    [Fact]
    public void Employee_object_headers_cannot_escape_into_business_even_with_matching_employee_code_capabilities() {
        SchemaCatalogView meta = Prepare().View;
        FrameAddress graph;
        using (var repository = RepositoryStorageFacade.CreateNew(_path)) {
            ModelExecutionEnvironment employees = BootstrapBindings.CreateEnvironment();
            ObjectVersionRecord[] records = EncodeRows(meta.Rows, employees);
            StoredGraphRevision schema = repository.Store.AppendMeta(null, new(null, meta.RootId, records));
            ObjectLayout layout = BootstrapBindings.Layout(typeof(SchemaCatalogRecord));
            ObjectStateRecord employee = BootstrapBindings.WithPreparation(new(new(1), layout.Schema!, new CatalogDto(default)), employees);
            byte[] body = TypedBaseBodyCodec.Encode(layout, employee.Preparation!.PrepareBase(employee));
            graph = repository.Store.AppendBusiness(StoredGraphKind.State, schema.Address,
                new(null, new(1), [ObjectVersionRecord.CreateBase(1, body)])).Address;
            repository.Data.ConfirmDurable();
        }
        using var reader = RepositoryStorageFacade.OpenReadOnlyExisting(_path);
        // Omitting selected makes this specifically a fixed-route role check,
        // rather than an incidental failure to find an employee descriptor in S.
        Assert.Throws<InvalidDataException>(() => StoredGraphDecoder.Read(reader.Store, reader.Store.ReadBusinessGraph(graph, null), BootstrapBindings.CreateEnvironment()));
    }

    private static DurableSchema Point(TypeTag tag) => new("Point", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, tag));
    private static DurableSchema InlineOwner(string name, DurableSchema point) =>
        new(name, 1, new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: point));
    private static ObjectLayout NullableList(DurableSchema point) => ObjectLayout.ForList(new(
        DurableFieldInfo.Nullable(1, new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: point))));

    private static PreparedSchemaCatalogWrite Prepare(params DurableSchema[] roots) {
        SchemaCatalogWorkspace owner = new(static () => { });
        foreach (DurableSchema schema in roots) { owner.Set(schema); }
        return owner.Prepare();
    }

    private static ObjectId InlineDescriptor(SchemaCatalogView view) =>
        Assert.Single(view.DescriptorIds, id => view.Rows[id].GetState<DescriptorDto>().Segment0Field1 == 3);

    private static ObjectId RootField(IReadOnlyDictionary<ObjectId, ObjectStateRecord> rows, ObjectId descriptor) =>
        Assert.Single(rows[rows[descriptor].GetState<DescriptorDto>().Segment0Field4].GetListState<ObjectId>().Elements.ToArray());

    private static void Replace<T>(Dictionary<ObjectId, ObjectStateRecord> rows, ObjectId id, T dto) where T : unmanaged =>
        rows[id] = new(id, rows[id].Schema!, dto);

    private static ObjectId AddArguments(Dictionary<ObjectId, ObjectStateRecord> rows, ObjectId[] items) {
        ObjectId id = new(rows.Keys.Max().Value + 1);
        rows.Add(id, new(id, BootstrapBindings.Layout(typeof(List<TypeExprRecord>)).List!, new FrozenListState<ObjectId>(items)));
        return id;
    }

    private static ObjectId AddType(Dictionary<ObjectId, ObjectStateRecord> rows, TypeDto dto) {
        ObjectId id = new(rows.Keys.Max().Value + 1);
        rows.Add(id, new(id, BootstrapBindings.Layout(typeof(TypeExprRecord)).Schema!, dto));
        return id;
    }

    private void AssertBadCold(ObjectId root, IReadOnlyDictionary<ObjectId, ObjectStateRecord> rows) {
        FrameAddress graph = WriteMeta(root, rows);
        using var reader = RepositoryStorageFacade.OpenReadOnlyExisting(_path);
        Assert.Equal(rows.Count, reader.Store.ReadMetaGraph(graph).ObjectHeads.Count);
        Assert.Throws<InvalidDataException>(() => SchemaCatalogView.Load(reader.Store, graph));
    }

    private static void AssertBadProjection(ObjectId root, IReadOnlyDictionary<ObjectId, ObjectStateRecord> rows) =>
        Assert.Throws<InvalidDataException>(() => new SchemaCatalogView(root, rows));

    private FrameAddress WriteMeta(ObjectId root, IReadOnlyDictionary<ObjectId, ObjectStateRecord> rows) {
        using var repository = RepositoryStorageFacade.CreateNew(_path);
        ModelExecutionEnvironment employees = BootstrapBindings.CreateEnvironment();
        StoredGraphRevision graph = repository.Store.AppendMeta(null, new(null, root, EncodeRows(rows, employees)));
        repository.Data.ConfirmDurable();
        return graph.Address;
    }

    private static ObjectVersionRecord[] EncodeRows(IReadOnlyDictionary<ObjectId, ObjectStateRecord> rows,
        ModelExecutionEnvironment employees) => rows.Values.Select(row => {
            ObjectStateRecord prepared = BootstrapBindings.WithPreparation(row, employees);
            PreparedBaseBody raw = prepared.Kind == ObjectStateKind.String
                ? StringBodyPreparer.PrepareBase(prepared.StringContent) : prepared.Preparation!.PrepareBase(prepared);
            return ObjectVersionRecord.CreateBase(row.Id.Value, TypedBaseBodyCodec.Encode(row.Layout, raw));
        }).ToArray();

    private static byte[] Header(byte version, byte route, uint id) {
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        writer.WriteByte(version);
        writer.WriteByte(route);
        writer.WriteVarUInt32(id);
        return buffer.WrittenSpan.ToArray();
    }

    private sealed class SingleBody(byte[] body) : IStateBodySource {
        public int Count => 1;
        public ReadOnlySpan<byte> GetBody(int index) => index == 0 ? body : throw new ArgumentOutOfRangeException(nameof(index));
    }

    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
