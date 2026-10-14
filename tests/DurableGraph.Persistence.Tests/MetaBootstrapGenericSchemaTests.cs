using System.Buffers;
using Atelia.Binary;

using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using DescriptorDto = Atelia.DurableGraph.Generated.Family_503044.V1;

namespace Atelia.DurableGraph.Persistence.Tests;

// Hand-built exact layouts isolate catalog identity and reader admission. They
// neither establish CLR enum identity nor qualify generated generic Upgrade.
public sealed class MetaBootstrapGenericSchemaTests : IDisposable {
    private const string Prefix = "dg-bootstrap-generic-schema-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Ordered_closed_arguments_and_versions_keep_distinct_descriptors_even_when_raw_bodies_are_identical() {
        TypeExpr first = TypeExpr.Named("Generic.Pair", TypeExpr.Builtin(TypeTag.Int32), TypeExpr.Builtin(TypeTag.Int64));
        TypeExpr second = TypeExpr.Named("Generic.Pair", TypeExpr.Builtin(TypeTag.Int64), TypeExpr.Builtin(TypeTag.Int32));
        DurableSchema[] schemas = [Plain(first, 1), Plain(second, 1), Plain(first, 2)];
        PreparedSchemaCatalogWrite prepared = Prepare(schemas);
        FrameAddress graph;
        using (var repository = RepositoryStorageFacade.CreateNew(_path)) {
            graph = repository.Store.Append(StoredGraphKind.Meta, null, null, prepared.Write!).Address;
            repository.Data.ConfirmDurable();
        }
        using var reader = RepositoryStorageFacade.OpenReadOnlyExisting(_path);
        SchemaCatalogView loaded = SchemaCatalogView.Load(reader.Store, graph); // No business registrations.
        Assert.Equal(3, loaded.Schemas.Count);
        ObjectId[] ids = schemas.Select(schema => loaded.DescriptorId(ObjectLayout.ForDurable(schema))).ToArray();
        Assert.Equal(3, ids.Distinct().Count());
        foreach (DurableSchema schema in schemas) {
            Assert.Equal(schema, loaded.Schemas[new(schema.Type, schema.Version)]);
            ObjectLayout layout = ObjectLayout.ForDurable(schema);
            var decoded = TypedBaseBodyCodec.Decode(TypedBaseBodyCodec.Encode(layout, new PreparedBaseBody([0]), loaded), loaded);
            Assert.Equal(layout, decoded.Layout);
            Assert.Equal(new byte[] { 0 }, decoded.Body);
        }
    }

    [Fact]
    public void Enum_shaped_inline_layouts_and_nullable_closed_arguments_need_no_new_employee_variant() {
        DurableSchema mode1 = Mode(1, TypeTag.UInt16), mode2 = Mode(2, TypeTag.UInt16);
        DurableSchema box1 = Box(mode1, 1), box2 = Box(mode2, 2), nullableBox = Box(mode1, 1, nullable: true);
        DurableSchema references = new("Generic.References", 1,
            DurableFieldInfo.Reference(1, box1.Type), DurableFieldInfo.Reference(2, nullableBox.Type));
        PreparedSchemaCatalogWrite prepared = Prepare(box1, box2, nullableBox, references);
        FrameAddress graph;
        using (var repository = RepositoryStorageFacade.CreateNew(_path)) {
            graph = repository.Store.Append(StoredGraphKind.Meta, null, null, prepared.Write!).Address;
            repository.Data.ConfirmDurable();
        }
        using var reader = RepositoryStorageFacade.OpenReadOnlyExisting(_path);
        SchemaCatalogView loaded = SchemaCatalogView.Load(reader.Store, graph);
        Assert.Equal(6, loaded.Schemas.Count);
        Assert.Equal(box1.Type, box2.Type); // Nominal arguments do not contain the inline version.
        Assert.NotEqual(box1.Type, nullableBox.Type);
        foreach (DurableSchema mode in new[] { mode1, mode2 }) {
            ObjectId id = loaded.SchemaNodeId(mode);
            Assert.Equal((byte)3, loaded.Rows[id].GetState<DescriptorDto>().Segment0Field1);
            Assert.Equal(mode, loaded.Schemas[new(mode.Type, mode.Version)]);
            Assert.Throws<InvalidDataException>(() => loaded.ResolveLayout(id));
        }
        Assert.Equal(mode1, Assert.Single(loaded.Schemas[new(box1.Type, 1)].Fields).InlineSchema);
        Assert.Equal(mode2, Assert.Single(loaded.Schemas[new(box2.Type, 2)].Fields).InlineSchema);
        Assert.Equal(mode1, Assert.Single(loaded.Schemas[new(nullableBox.Type, 1)].Fields).NullableLayout!.ElementSlot.InlineSchema);
        Assert.NotEqual(loaded.DescriptorId(ObjectLayout.ForDurable(box1)), loaded.DescriptorId(ObjectLayout.ForDurable(nullableBox)));
    }

    [Fact]
    public void Changing_an_inline_version_requires_a_new_owner_version_while_old_business_keeps_its_original_schema_root() {
        DurableSchema mode1 = Mode(1, TypeTag.UInt16), mode2 = Mode(2, TypeTag.UInt16);
        DurableSchema box1 = Box(mode1, 1), incompatible = Box(mode2, 1), box2 = Box(mode2, 2);
        SchemaCatalogWorkspace catalog = new(static () => { });
        catalog.Set(box1);
        using var repository = RepositoryStorageFacade.CreateNew(_path);
        RepositorySaveResult first = Publish(repository, "main", catalog.Prepare(), box1, UnsignedBody(65535));
        FrameAddress originalSchema = first.Business.SchemaRoot!.Value;
        catalog.Set(mode2); // A new inline version itself is a valid independent definition.
        long frames = repository.Store.AppendedFrames;
        Assert.Throws<SchemaConflictException>(() => catalog.Set(incompatible));
        Assert.Equal(frames, repository.Store.AppendedFrames);
        Assert.Equal(originalSchema, repository.SharedSchemaRoot);
        catalog.Set(box2);
        PreparedSchemaCatalogWrite next = catalog.Prepare();
        Assert.Equal(box1, next.View.Schemas[new(box1.Type, 1)]);
        Assert.Equal(box2, next.View.Schemas[new(box2.Type, 2)]);
        RepositorySaveResult second = Publish(repository, "other", next, box2, UnsignedBody(65535));
        Assert.NotEqual(originalSchema, second.Business.SchemaRoot);
        Assert.Equal(originalSchema, repository.Store.ReadGraph(first.Business.Address).SchemaRoot);
        Assert.Equal(second.Business.SchemaRoot, repository.SharedSchemaRoot);
        ObjectVersionChain old = repository.Store.ReadObjectChain(first.Business.Address, 1);
        SchemaCatalogView origin = SchemaCatalogView.Load(repository.Store, old.Entries[0].SchemaRoot!.Value);
        Assert.Equal(ObjectLayout.ForDurable(box1), TypedBaseBodyCodec.Decode(old.Entries[0].Record.Body, origin).Layout);
        Assert.False(origin.Schemas.ContainsKey(new(box2.Type, 2)));
    }

    [Theory]
    [InlineData("inlineVersion")]
    [InlineData("underlyingTag")]
    public void Valid_integer_bytes_with_a_wrong_complete_inline_reader_schema_fail_before_the_body_callback(string difference) {
        DurableSchema sourceMode = Mode(1, TypeTag.UInt16), source = Box(sourceMode, 1);
        DurableSchema wrongMode = difference == "inlineVersion" ? Mode(2, TypeTag.UInt16) : Mode(1, TypeTag.UInt32);
        DurableSchema wrong = Box(wrongMode, 1);
        Assert.Equal(source.Type, wrong.Type);
        Assert.Equal(source.Version, wrong.Version);
        Assert.NotEqual(source, wrong);
        FrameAddress graph, schema;
        using (var repository = RepositoryStorageFacade.CreateNew(_path)) {
            RepositorySaveResult saved = Publish(repository, "main", Prepare(source), source, UnsignedBody(65535));
            graph = saved.Business.Address;
            schema = saved.Business.SchemaRoot!.Value;
        }
        using var reader = RepositoryStorageFacade.OpenReadOnlyExisting(_path);
        SchemaCatalogView selected = SchemaCatalogView.Load(reader.Store, schema);
        Assert.Equal(sourceMode, selected.Schemas[new(sourceMode.Type, 1)]); // Metadata is valid without a CLR enum claim.
        int wrongReads = 0;
        StateReaderBinding badReader = IntegerReader(wrong, () => wrongReads++);
        Assert.Throws<InvalidDataException>(() => StoredGraphDecoder.Read(reader.Store, graph, Readers(badReader), selected));
        Assert.Equal(0, wrongReads);
        int validReads = 0;
        DecodedGraphRows decoded = StoredGraphDecoder.Read(reader.Store, graph, Readers(IntegerReader(source, () => validReads++)), selected);
        Assert.Equal(1, validReads);
        Assert.Equal((ushort)65535, decoded.Rows[new(1)].GetState<ushort>());
    }

    [Fact]
    public void A_nullable_generic_argument_cannot_claim_a_declared_reference_family_is_a_value() {
        DurableSchema reference = new("Generic.ReferenceArgument", 1, new DurableFieldInfo(1, TypeTag.Int32));
        // TypeExpr.Nullable intentionally defers the named declaration-kind check.
        TypeExpr closed = TypeExpr.Named("Generic.Phantom", TypeExpr.Nullable(reference.Type));
        DurableSchema owner = Plain(closed, 1);
        SchemaCatalogWorkspace catalog = new(static () => { });
        catalog.Set(reference);
        catalog.Set(owner);
        Assert.Throws<InvalidDataException>(() => catalog.Prepare());
        Assert.Null(catalog.Accepted);
        // A nullable argument of a declared inline value is valid even when phantom in the body.
        PreparedSchemaCatalogWrite valid = Prepare(Mode(1, TypeTag.UInt16), Plain(
            TypeExpr.Named("Generic.Phantom", TypeExpr.Nullable(Mode(1, TypeTag.UInt16).Type)), 1));
        Assert.Equal(2, valid.View.Schemas.Count);
    }

    [Fact]
    public void A_retained_base_cannot_be_reinterpreted_by_a_selected_view_with_another_closed_argument() {
        DurableSchema first = Plain(TypeExpr.Named("Generic.Phantom", TypeExpr.Builtin(TypeTag.Int32)), 1);
        DurableSchema second = Plain(TypeExpr.Named("Generic.Phantom", TypeExpr.Builtin(TypeTag.Int64)), 1);
        FrameAddress retained;
        using (var repository = RepositoryStorageFacade.CreateNew(_path)) {
            RepositorySaveResult original = Publish(repository, "main", Prepare(first), first, new PreparedBaseBody([0]));
            PreparedSchemaCatalogWrite other = Prepare(second);
            StoredGraphRevision otherView = repository.Store.Append(StoredGraphKind.Meta, null, null, other.Write!);
            retained = repository.Store.Append(StoredGraphKind.State, original.Business.Address, otherView.Address,
                new(original.Business.Address, new(1), [])).Address;
            repository.Data.ConfirmDurable();
        }
        using var reader = RepositoryStorageFacade.OpenReadOnlyExisting(_path);
        StoredGraphRevision graph = reader.Store.ReadGraph(retained);
        ObjectVersionChain originalChain = reader.Store.ReadObjectChain(retained, 1);
        SchemaCatalogView origin = SchemaCatalogView.Load(reader.Store, originalChain.Entries[0].SchemaRoot!.Value);
        SchemaCatalogView selected = SchemaCatalogView.Load(reader.Store, graph.SchemaRoot!.Value);
        Assert.Equal(first, origin.Schemas[new(first.Type, 1)]);
        Assert.Equal(second, selected.Schemas[new(second.Type, 1)]);
        Assert.Equal(origin.DescriptorId(ObjectLayout.ForDurable(first)), selected.DescriptorId(ObjectLayout.ForDurable(second)));
        Assert.False(selected.Schemas.ContainsKey(new(first.Type, 1)));
        int bodyReads = 0;
        StateReaderBinding<int> binding = new(first,
            (ref BareValueReader bytes) => { bodyReads++; return bytes.ReadVarInt32(); },
            static (ref BareValueReader bytes, in int prior) => bytes.ReadVarInt32(),
            static (in int value, IStateReferenceVisitor visitor) => { });
        Assert.Throws<InvalidDataException>(() => StoredGraphDecoder.Read(reader.Store, retained, Readers(binding), selected));
        Assert.Equal(0, bodyReads);
    }

    private static DurableSchema Plain(TypeExpr type, int version) => new(type, version, new DurableFieldInfo(1, TypeTag.Int32));
    private static DurableSchema Mode(int version, TypeTag underlying) =>
        new("Generic.Mode", version, SchemaKind.InlineValue, new DurableFieldInfo(1, underlying));
    private static DurableSchema Box(DurableSchema mode, int version, bool nullable = false) {
        DurableFieldInfo inline = new(1, TypeTag.InlineValue, inlineSchema: mode);
        return new(TypeExpr.Named("Generic.Box", nullable ? TypeExpr.Nullable(mode.Type) : mode.Type), version,
            nullable ? DurableFieldInfo.Nullable(1, inline) : inline);
    }

    private static PreparedSchemaCatalogWrite Prepare(params DurableSchema[] schemas) {
        SchemaCatalogWorkspace catalog = new(static () => { });
        foreach (DurableSchema schema in schemas) { catalog.Set(schema); }
        return catalog.Prepare();
    }

    private static RepositorySaveResult Publish(RepositoryStorageFacade repository, string branch, PreparedSchemaCatalogWrite catalog,
        DurableSchema schema, PreparedBaseBody raw) {
        byte[] body = TypedBaseBodyCodec.Encode(ObjectLayout.ForDurable(schema), raw, catalog.View);
        using PendingRepositorySave pending = repository.Prepare(branch, StoredGraphKind.State,
            new(null, new(1), [ObjectVersionRecord.CreateBase(1, body)]), catalog.Write);
        return repository.Commit(pending, catalog.Install, prepareMeta: catalog.PrepareInstall);
    }

    private static PreparedBaseBody UnsignedBody(ushort value) {
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        writer.WriteVarUInt32(value); // Canonical unsigned varint; 65535 is legal for either width.
        return new(buffer.WrittenSpan);
    }

    private static StateReaderBinding IntegerReader(DurableSchema schema, Action onRead) {
        if (Assert.Single(Assert.Single(schema.Fields).InlineSchema!.Fields).TypeTag == TypeTag.UInt16) {
            return new StateReaderBinding<ushort>(schema,
                (ref BareValueReader reader) => { onRead(); return checked((ushort)reader.ReadVarUInt32()); },
                static (ref BareValueReader reader, in ushort prior) => checked((ushort)reader.ReadVarUInt32()),
                static (in ushort value, IStateReferenceVisitor visitor) => { });
        }
        return new StateReaderBinding<uint>(schema,
            (ref BareValueReader reader) => { onRead(); return reader.ReadVarUInt32(); },
            static (ref BareValueReader reader, in uint prior) => reader.ReadVarUInt32(),
            static (in uint value, IStateReferenceVisitor visitor) => { });
    }

    private static ModelExecutionEnvironment Readers(StateReaderBinding reader) {
        StateReaderRegistry registry = new();
        registry.Register(reader);
        return new([], [], registry.Snapshot());
    }

    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
