using System.Buffers;
using Atelia.Binary;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using DescriptorDto = Atelia.DurableGraph.Generated.Family_503044.V1;
using FieldDto = Atelia.DurableGraph.Generated.Family_503046.V1;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class MetaBootstrapBuiltinListTests : IDisposable {
    private const string Prefix = "dg-bootstrap-builtin-list-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Nullable_long_List_has_an_exact_null_catalog_reader_and_preparation_round_trip() {
        ListLayout list = new(DurableFieldInfo.Nullable(1, new(1, TypeTag.Int64)), codecVersion: 2);
        NullableState<long>[] values = [default, new(long.MinValue), new(0), new(long.MaxValue)];
        PreparedBaseBody raw = ListStateBody<NullableState<long>, NullableStateOps<long, Int64StateOps>>.PrepareBase(new(values), list);
        byte[] encoded = P0TypedBodyCodec.Encode(ObjectLayout.ForList(list), raw);
        BareValueReader header = new(encoded);
        Assert.Equal((byte)5, header.ReadByte());
        Assert.Equal((byte)2, header.ReadByte());
        Assert.Equal(list.Type, TypeExprWireCodec.Read(ref header));
        Assert.Equal(raw.Body, encoded[header.ConsumedCount..]);
        var decoded = P0TypedBodyCodec.Decode(encoded, null);
        Assert.Equal(ObjectLayout.ForList(list), decoded.Layout);
        Assert.Equal(2u, decoded.Layout.List!.CodecVersion); // ABI5 fixes the version; no current default fills it in.
        StateModelSnapshot models = NoSchemas();
        ObjectStateRecord row = P0FixedBindings.Reader(decoded.Layout, models).Read(new(71), new SingleBody(decoded.Body));
        Assert.Equal(values, row.GetListState<NullableState<long>>().Elements.ToArray());
        ObjectStateRecord prepared = P0FixedBindings.WithPreparation(row, models);
        Assert.Equal(row.Layout, prepared.Layout);
        Assert.Equal(raw.Body, prepared.Preparation!.PrepareBase(prepared).Body);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ListLayout(list.ElementSlot, codecVersion: 1));
    }

    [Theory]
    [InlineData("list")]
    [InlineData("array")]
    [InlineData("dictionary")]
    public void Nested_builtin_reference_constructors_describe_the_List_slot_and_admit_only_supported_object_roots(string constructor) {
        TypeExpr scalar = TypeExpr.Builtin(TypeTag.Int32);
        TypeExpr nested = constructor switch {
            "list" => TypeExpr.List(scalar),
            "array" => TypeExpr.VectorArray(scalar),
            _ => TypeExpr.Dictionary(TypeExpr.Builtin(TypeTag.Int64), scalar),
        };
        ObjectLayout expected = ObjectLayout.ForList(new(DurableFieldInfo.Reference(1, nested), codecVersion: 2));
        byte[] body = P0TypedBodyCodec.Encode(expected, new PreparedBaseBody([0]));
        var decoded = P0TypedBodyCodec.Decode(body, null);
        Assert.Equal(expected, decoded.Layout);
        Assert.Equal(nested, decoded.Layout.List!.ElementSlot.TargetType);
        Assert.Null(decoded.Layout.List.ElementSlot.ValueSchema);
        ObjectStateRecord row = P0FixedBindings.Reader(decoded.Layout, NoSchemas()).Read(new(1), new SingleBody(decoded.Body));
        Assert.Empty(row.GetListState<ObjectId>().Elements.ToArray());
        Assert.NotNull(P0FixedBindings.WithPreparation(row, NoSchemas()).Preparation);
        Assert.Equal(nested, P0TypedBodyCodec.Decode(Structural(nested), null).Layout.Type);
    }

    [Theory]
    [InlineData("userReference")]
    [InlineData("enumValue")]
    [InlineData("nestedEmployee")]
    public void Named_nodes_at_any_depth_cannot_take_the_structural_route(string namedKind) {
        ObjectLayout layout;
        if (namedKind == "enumValue") {
            DurableSchema enumeration = new("BuiltinList.Enum", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.UInt16));
            layout = ObjectLayout.ForList(new(new(1, TypeTag.InlineValue, inlineSchema: enumeration), codecVersion: 2));
        } else {
            TypeExpr nominal = namedKind == "userReference" ? TypeExpr.Named("BuiltinList.User")
                : TypeExpr.List(TypeExpr.List(TypeExpr.Named("P0D")));
            layout = ObjectLayout.ForList(new(DurableFieldInfo.Reference(1, nominal), codecVersion: 2));
            Assert.Null(layout.List!.ElementSlot.ValueSchema); // Absence of an inline dependency is insufficient.
        }
        Assert.Throws<InvalidDataException>(() => P0TypedBodyCodec.Encode(layout, new PreparedBaseBody([0])));
        Assert.Throws<InvalidDataException>(() => P0TypedBodyCodec.Decode(Structural(layout.Type), null));
        if (namedKind != "nestedEmployee") {
            P0CatalogWorkspace catalog = new(static () => { });
            catalog.Set(layout);
            P0CatalogView view = catalog.Prepare().View;
            byte[] dynamic = P0TypedBodyCodec.Encode(layout, new PreparedBaseBody([0]), view);
            Assert.Equal((byte)5, dynamic[0]);
            Assert.Equal((byte)1, dynamic[1]);
            Assert.Equal(layout, P0TypedBodyCodec.Decode(dynamic, view).Layout);
            Assert.Throws<InvalidDataException>(() => P0TypedBodyCodec.Decode(dynamic, null));
        }
        Assert.Equal(ObjectLayout.ForList(new(new(1, TypeTag.Int32), codecVersion: 2)),
            P0TypedBodyCodec.Decode(Structural(TypeExpr.List(TypeExpr.Builtin(TypeTag.Int32))), null).Layout);
    }

    [Fact]
    public void Fresh_resource_reads_validate_nested_List_reference_types_even_when_every_body_and_head_is_valid() {
        ListLayout outer = new(DurableFieldInfo.Reference(1, TypeExpr.List(TypeExpr.Builtin(TypeTag.Int32))), codecVersion: 2);
        PreparedBaseBody rawOuter = ListStateBody<ObjectId, ObjectIdStateOps>.PrepareBase(new([new(2), new(2)]), outer);
        ListLayout integers = new(new(1, TypeTag.Int32), codecVersion: 2), longs = new(new(1, TypeTag.Int64), codecVersion: 2);
        PreparedBaseBody rawInt = ListStateBody<int, Int32StateOps>.PrepareBase(new([17, -29]), integers);
        PreparedBaseBody rawLong = ListStateBody<long, Int64StateOps>.PrepareBase(new([17, -29]), longs);
        FrameAddress good, bad;
        using (var repository = P0Repository.CreateNew(_path)) {
            good = Publish(repository, "good", [Base(1, outer, rawOuter), Base(2, integers, rawInt)]).Address;
            bad = Publish(repository, "bad", [Base(1, outer, rawOuter), Base(2, longs, rawLong)]).Address;
        }
        using var reader = P0Repository.OpenReadOnlyExisting(_path);
        Assert.Equal(good, reader.GetStateHead("good"));
        Assert.Equal(bad, reader.GetStateHead("bad"));
        Assert.Null(reader.SharedSchemaRoot);
        Assert.Null(reader.Store.ReadGraph(good).SchemaRoot);
        P0DecodedRows valid = P0TypedReader.Read(reader.Store, good, NoSchemas());
        Assert.Equal(new ObjectId[] { new(2), new(2) }, valid.Rows[new(1)].GetListState<ObjectId>().Elements.ToArray());
        Assert.Equal(new[] { 17, -29 }, valid.Rows[new(2)].GetListState<int>().Elements.ToArray());
        Assert.Equal(2, reader.Store.ReadGraph(bad).Objects.Count);
        P0ObjectChain inner = reader.Store.ReadObjectChain(bad, 2);
        var decoded = P0TypedBodyCodec.Decode(inner.Entries[0].Record.Body, null);
        Assert.Equal(new long[] { 17, -29 }, P0FixedBindings.Reader(decoded.Layout, NoSchemas())
            .Read(new(2), new SingleBody(decoded.Body)).GetListState<long>().Elements.ToArray());
        Assert.Throws<InvalidDataException>(() => P0TypedReader.Read(reader.Store, bad, NoSchemas()));
        Assert.Equal(0, reader.Store.AppendedFrames);
    }

    [Fact]
    public void A_valid_structural_List_body_cannot_escape_into_the_finite_meta_employee_graph() {
        P0CatalogWorkspace owner = new(static () => { });
        P0PreparedCatalog catalog = owner.Prepare();
        ListLayout list = new(new(1, TypeTag.Int64), codecVersion: 2);
        PreparedBaseBody raw = ListStateBody<long, Int64StateOps>.PrepareBase(new([47L]), list);
        ObjectVersionRecord extra = Base(100, list, raw);
        var decoded = P0TypedBodyCodec.Decode(extra.Body, null);
        Assert.Equal(new[] { 47L }, P0FixedBindings.Reader(decoded.Layout).Read(new(100), new SingleBody(decoded.Body))
            .GetListState<long>().Elements.ToArray());
        FrameAddress meta;
        using (var repository = P0Repository.CreateNew(_path)) {
            meta = repository.Store.Append(P0GraphKind.Meta, null, null,
                new(null, catalog.View.RootId, catalog.Write!.Records.Append(extra))).Address;
            repository.Data.ConfirmDurable();
        }
        using var reader = P0Repository.OpenReadOnlyExisting(_path);
        Assert.Throws<InvalidDataException>(() => P0CatalogView.Load(reader.Store, meta));
    }

    [Fact]
    public void A_builtin_List_cannot_acquire_a_dynamic_descriptor_even_from_valid_employee_bodies() {
        P0CatalogWorkspace owner = new(static () => { });
        P0CatalogView view = owner.Prepare().View;
        StateModelSnapshot employees = P0FixedBindings.CreateSnapshot();
        Dictionary<ObjectId, ObjectStateRecord> rows = view.Rows.ToDictionary();
        ObjectId field = new(10), descriptor = new(11);
        rows.Add(field, P0FixedBindings.WithPreparation(new(field, employees.ResolveCurrentModel(typeof(P0FieldRecord)).CurrentSchema,
            new FieldDto(1, (byte)TypeTag.Int64, default, default, default)), employees));
        rows.Add(descriptor, P0FixedBindings.WithPreparation(new(descriptor, employees.ResolveCurrentModel(typeof(P0DescriptorRecord)).CurrentSchema,
            new DescriptorDto(2, default, 0, default, field, 2, default)), employees));
        ObjectStateRecord declarations = rows[view.DescriptorListId];
        rows[declarations.Id] = P0FixedBindings.WithPreparation(new(declarations.Id, declarations.Layout.List!,
            new FrozenListState<ObjectId>([descriptor])), employees);
        ObjectVersionRecord[] records = rows.Values.Select(row => {
            PreparedBaseBody raw = row.Kind == ObjectStateKind.String ? StringPayloadCodec.PrepareBase(row.StringContent)
                : row.Preparation!.PrepareBase(row);
            return ObjectVersionRecord.CreateBase(row.Id.Value, P0TypedBodyCodec.Encode(row.Layout, raw));
        }).ToArray();
        FrameAddress meta;
        using (var repository = P0Repository.CreateNew(_path)) {
            meta = repository.Store.Append(P0GraphKind.Meta, null, null, new(null, view.RootId, records)).Address;
            repository.Data.ConfirmDurable();
        }
        using var reader = P0Repository.OpenReadOnlyExisting(_path);
        P0DecodedRows decoded = P0TypedReader.Read(reader.Store, meta);
        Assert.Equal((byte)2, decoded.Rows[descriptor].GetState<DescriptorDto>().Segment0Field1);
        Assert.Throws<InvalidDataException>(() => P0CatalogView.Load(reader.Store, meta));
    }

    [Theory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void Raw_structural_headers_enforce_TypeExpr_depth_before_unchecked_recursion(int depth, bool valid) {
        byte[] type = new byte[depth + 1];
        Array.Fill(type, (byte)TypeExprKind.List, 0, depth - 1);
        type[depth - 1] = (byte)TypeExprKind.Builtin;
        type[depth] = (byte)TypeTag.Int32;
        byte[] bytes = [5, 2, .. type, 0];
        if (!valid) { Assert.Throws<InvalidDataException>(() => P0TypedBodyCodec.Decode(bytes, null)); return; }
        var decoded = P0TypedBodyCodec.Decode(bytes, null);
        TypeExpr expected = TypeExpr.Builtin(TypeTag.Int32);
        for (int index = 1; index < depth; index++) { expected = TypeExpr.List(expected); }
        Assert.Equal(expected, decoded.Layout.Type);
        Assert.Equal(2u, decoded.Layout.List!.CodecVersion);
        Assert.Empty(P0FixedBindings.Reader(decoded.Layout, NoSchemas()).Read(new(1), new SingleBody(decoded.Body))
            .GetListState<ObjectId>().Elements.ToArray());
    }

    [Theory]
    [InlineData("open")]
    [InlineData("nullableString")]
    [InlineData("truncated")]
    [InlineData("unknownBuiltin")]
    [InlineData("unknownRoute")]
    [InlineData("removedListCode")]
    [InlineData("oldAbi")]
    public void New_ABI_rejects_invalid_structural_operands_or_routes_for_the_intended_reason(string corruption) {
        byte[] good = Structural(TypeExpr.List(TypeExpr.Builtin(TypeTag.Int32)));
        Assert.Equal(ObjectStateKind.List, P0TypedBodyCodec.Decode(good, null).Layout.Kind);
        byte[] bad = corruption switch {
            "open" => [5, 2, (byte)TypeExprKind.List, (byte)TypeExprKind.Parameter, 0],
            "nullableString" => [5, 2, (byte)TypeExprKind.List, (byte)TypeExprKind.Nullable, (byte)TypeExprKind.Builtin, (byte)TypeTag.String, 0],
            "truncated" => [5, 2, (byte)TypeExprKind.List],
            "unknownBuiltin" => [5, 2, (byte)TypeExprKind.List, (byte)TypeExprKind.Builtin, 255, 0],
            "unknownRoute" => [5, 255, 0],
            "removedListCode" => [5, 0, 9, 0],
            _ => [4, .. good.AsSpan(1).ToArray()],
        };
        if (corruption == "truncated") {
            Assert.Throws<EndOfStreamException>(() => P0TypedBodyCodec.Decode(bad, null));
        } else {
            Assert.Throws<InvalidDataException>(() => P0TypedBodyCodec.Decode(bad, null));
        }
    }

    private static StateModelSnapshot NoSchemas() => new StateModelRegistry().Snapshot(
        schemaLookup: _ => throw new InvalidOperationException("A structural builtin List must not query Schema."));

    private static byte[] Structural(TypeExpr type) {
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        writer.WriteByte(5);
        writer.WriteByte(2);
        TypeExprWireCodec.Write(ref writer, type);
        writer.WriteByte(0); // An empty List body; root-type rejection occurs before body interpretation.
        return buffer.WrittenSpan.ToArray();
    }

    private static ObjectVersionRecord Base(uint id, ListLayout layout, PreparedBaseBody raw) =>
        ObjectVersionRecord.CreateBase(id, P0TypedBodyCodec.Encode(ObjectLayout.ForList(layout), raw));

    private static P0GraphSnapshot Publish(P0Repository repository, string branch, IEnumerable<ObjectVersionRecord> records) {
        using P0PendingSave pending = repository.Prepare(branch, P0GraphKind.State, new(null, new(1), records));
        return repository.Commit(pending).Business;
    }

    private sealed class SingleBody(byte[] body) : IStateBodySource {
        public int Count => 1;
        public ReadOnlySpan<byte> GetBody(int index) => index == 0 ? body : throw new ArgumentOutOfRangeException(nameof(index));
    }

    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
