using System.Buffers;
using System.Reflection;
using Atelia.Binary;

using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

// Manual one-field inline bindings reuse existing primitive operations. Actual
// generated DTO/history and enum-width qualification belongs to the SG witness.
public sealed class MetaBootstrapContainerUpgradeTests : IDisposable {
    private const string Prefix = "dg-bootstrap-container-upgrade-";
    private const string PointId = "ContainerUpgrade.Point", KeyId = "ContainerUpgrade.Key";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    [ThreadStatic] private static Calls? _calls;

    [Theory]
    [InlineData("Array", "empty")]
    [InlineData("Array", "absent")]
    [InlineData("List", "empty")]
    [InlineData("List", "absent")]
    [InlineData("Dictionary", "empty")]
    [InlineData("Dictionary", "absent")]
    public void Empty_and_all_absent_containers_still_bind_the_required_value_rule(string kind, string contents) {
        FrameAddress source = Seed(kind, contents);
        using var reader = RepositoryStorageFacade.OpenReadOnlyExisting(_path);
        _calls = new();
        ContainerRoot positive = Assert.IsType<ContainerRoot>(BusinessPersistenceAdapter.ReadRoot(reader.Store, source, Models(kind)));
        Assert.Equal(1, _calls.Allocations);
        Assert.Equal(0, _calls.Point);
        int count = contents == "empty" ? 0 : 2;
        Assert.Equal(kind == "Dictionary" ? count : 0, _calls.Key);
        AssertCurrentContent(positive.Value!, kind, count);
        _calls = new();
        StateModelRegistry missing = Models(kind, missingValue: true);
        AssertStoredSource(reader, source, missing, kind);
        Assert.Throws<InvalidDataException>(() => BusinessPersistenceAdapter.ReadRoot(reader.Store, source, missing));
        Assert.Equal(0, _calls.Point);
        Assert.Equal(0, _calls.Key); // Dictionary key provider is valid, but value binding must finish first.
        Assert.Equal(0, _calls.Allocations);
        Assert.Equal(source, reader.GetStateHead("main"));
        Assert.Equal(0, reader.Store.AppendedFrames);
    }

    [Fact]
    public void A_missing_Dictionary_value_rule_prevents_its_valid_key_callbacks_for_present_entries() {
        FrameAddress source = Seed("Dictionary", "present");
        using var reader = RepositoryStorageFacade.OpenReadOnlyExisting(_path);
        _calls = new();
        ContainerRoot positive = Assert.IsType<ContainerRoot>(BusinessPersistenceAdapter.ReadRoot(reader.Store, source, Models("Dictionary")));
        Assert.Equal(2, _calls.Key);
        Assert.Equal(2, _calls.Point);
        Assert.Equal(1, _calls.Allocations);
        var values = Assert.IsType<Dictionary<Key, Point?>>(positive.Value);
        Assert.Equal(new Point(1001), values[(Key)11]);
        Assert.Equal(new Point(1002), values[(Key)12]);
        _calls = new();
        StateModelRegistry missing = Models("Dictionary", missingValue: true);
        AssertStoredSource(reader, source, missing, "Dictionary");
        Assert.Throws<InvalidDataException>(() => BusinessPersistenceAdapter.ReadRoot(reader.Store, source, missing));
        Assert.Equal(0, _calls.Key);
        Assert.Equal(0, _calls.Point);
        Assert.Equal(0, _calls.Allocations);
    }

    [Theory]
    [InlineData("Array")]
    [InlineData("List")]
    [InlineData("Dictionary")]
    public void An_existing_conflicting_target_exact_definition_does_not_reinterpret_the_valid_source(string kind) {
        FrameAddress source = Seed(kind, "empty", conflictingTarget: true);
        using var reader = RepositoryStorageFacade.OpenReadOnlyExisting(_path);
        StateModelRegistry models = Models(kind);
        _calls = new();
        AssertStoredSource(reader, source, models, kind); // V1 body, descriptor and old reader are valid.
        SchemaCatalogView view = SchemaCatalogView.Load(reader.Store, reader.SharedSchemaRoot!.Value);
        Assert.Equal(TypeTag.Byte, Assert.Single(view.Schemas[new(PointId, 1)].Fields).TypeTag);
        Assert.Equal(TypeTag.UInt64, Assert.Single(view.Schemas[new(PointId, 2)].Fields).TypeTag);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => BusinessPersistenceAdapter.ReadRoot(reader.Store, source, models));
        Assert.IsType<SchemaConflictException>(error.InnerException);
        Assert.Equal(0, _calls.Key);
        Assert.Equal(0, _calls.Point);
        Assert.Equal(0, _calls.Allocations);
        Assert.Equal(source, reader.GetStateHead("main"));
        Assert.Equal(0, reader.Store.AppendedFrames);
    }

    [Fact]
    public void A_key_upgrade_canonical_collision_rejects_delivery_after_callbacks_without_mutating_stored_DTOs() {
        FrameAddress source = Seed("Dictionary", "present");
        using var reader = RepositoryStorageFacade.OpenReadOnlyExisting(_path);
        StateModelRegistry models = Models("Dictionary");
        FrozenDictionaryState<byte, NullableState<byte>> before = AssertStoredSource(reader, source, models, "Dictionary")
            .Rows[new(2)].GetDictionaryState<byte, NullableState<byte>>();
        _calls = new() { CollapseKeys = true };
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => BusinessPersistenceAdapter.ReadRoot(reader.Store, source, models));
        Assert.Contains("duplicate persistent keys", error.Message);
        Assert.Equal(2, _calls.Key);
        Assert.Equal(2, _calls.Point); // Conversion ran; this is not a promise to roll back callback effects.
        Assert.Equal(0, _calls.Allocations);
        FrozenDictionaryState<byte, NullableState<byte>> after = AssertStoredSource(reader, source, models, "Dictionary")
            .Rows[new(2)].GetDictionaryState<byte, NullableState<byte>>();
        Assert.Equal(before.Entries.ToArray(), after.Entries.ToArray());
        Assert.Equal(new byte[] { 1, 2 }, after.Entries.ToArray().Select(entry => entry.Key).ToArray());
        Assert.Equal(source, reader.GetStateHead("main"));
        Assert.Equal(0, reader.Store.AppendedFrames);
    }

    private FrameAddress Seed(string kind, string contents, bool conflictingTarget = false) {
        ObjectLayout layout = OldLayout(kind);
        NullableState<byte>[] values = contents == "empty" ? [] : contents == "absent" ? [default, default] : [new(1), new(2)];
        PreparedBaseBody body = kind switch {
            "Array" => ArrayStateBody<NullableState<byte>, NullableStateOps<byte, ByteStateOps>>.PrepareBase(new(new(values.Length), values), layout.Array!),
            "List" => ListStateBody<NullableState<byte>, NullableStateOps<byte, ByteStateOps>>.PrepareBase(new(values), layout.List!),
            _ => DictionaryStateBody<byte, NullableState<byte>, ByteStateOps, NullableStateOps<byte, ByteStateOps>>.PrepareBase(
                new(DictionaryComparerKind.ScalarDefault, values.Select((value, index) => new DictionaryEntryState<byte, NullableState<byte>>((byte)(index + 1), value)).ToArray()), layout.Dictionary!),
        };
        SchemaCatalogWorkspace catalog = new(static () => { });
        catalog.Set(RootSchema(kind));
        if (conflictingTarget) { catalog.Set(new DurableSchema(PointId, 2, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.UInt64))); }
        PreparedSchemaCatalogWrite prepared = catalog.PrepareOwned([layout]);
        using var repository = RepositoryStorageFacade.CreateNew(_path);
        using PendingRepositorySave pending = repository.Prepare("main", StoredGraphKind.State,
            new(null, new(1), [ObjectVersionRecord.CreateBase(1, TypedBaseBodyCodec.Encode(ObjectLayout.ForDurable(RootSchema(kind)), new PreparedBaseBody([2]), prepared.View)),
                ObjectVersionRecord.CreateBase(2, TypedBaseBodyCodec.Encode(layout, body, prepared.View))]), prepared.Write);
        return repository.Commit(pending, prepared.Install, prepareMeta: prepared.PrepareInstall).Business.Address;
    }

    private static DecodedGraphRows AssertStoredSource(RepositoryStorageFacade reader, FrameAddress source, StateModelRegistry models, string kind) {
        SchemaCatalogView view = SchemaCatalogView.Load(reader.Store, reader.Store.ReadGraph(source).SchemaRoot!.Value);
        DecodedGraphRows rows = StoredGraphDecoder.Read(reader.Store, source, models.CreateEnvironment(schemaLookup: key => view.Schemas.GetValueOrDefault(key)), view);
        Assert.Equal(OldLayout(kind), rows.Rows[new(2)].Layout);
        Assert.Equal(new ObjectId(2), rows.Rows[new(1)].GetState<ObjectId>());
        return rows;
    }

    private static StateModelRegistry Models(string kind, bool missingValue = false) {
        StateModelRegistry registry = new();
        registry.Register(Definition(PointId, typeof(Point), typeof(PointProjection)));
        registry.Register(Definition(KeyId, typeof(Key), typeof(KeyProjection)));
        registry.Register(new StateValueUpgradeRuleSet(typeof(PointRules), missingValue ? [] : [Provider(PointId, nameof(ConvertPoint))], allowNullableLifting: true));
        registry.Register(new StateValueUpgradeRuleSet(typeof(KeyRules), [Provider(KeyId, nameof(ConvertKey))]));
        switch (kind) {
            case "Array": registry.UseArrayElementUpgrades(typeof(PointRules)); break;
            case "List": registry.UseListElementUpgrades(typeof(PointRules)); break;
            default:
                registry.UseDictionaryKeyUpgrades(typeof(KeyRules));
                registry.UseDictionaryValueUpgrades(typeof(PointRules));
                break;
        }
        DurableSchema root = RootSchema(kind);
        StatePreparationBinding<ObjectId> preparation = new(root, RootBody,
            static (in ObjectId prior, in ObjectId next) => new(prior != next, RootBody(in next).Body));
        void Visit(in ObjectId state, IStateReferenceVisitor visitor) => visitor.VisitObject(state, OldLayout(kind).Type);
        StateReaderBinding<ObjectId> reader = new(root,
            static (ref BareValueReader bytes) => new(bytes.ReadVarUInt32()),
            static (ref BareValueReader bytes, in ObjectId prior) => new(bytes.ReadVarUInt32()), Visit);
        registry.Register(new StateModelBinding<ContainerRoot, ObjectId>(preparation, [reader],
            static row => row.GetState<ObjectId>(), () => { _calls!.Allocations++; return new(); },
            static (ContainerRoot target, in ObjectId state, ObjectReadTable objects) => target.Value = objects.ResolveObject<object>(state),
            (target, context) => context.CaptureObject(target.Value, OldLayout(kind).Type), Visit));
        return registry;
    }

    private static StateDefinitionBinding Definition(string id, Type domain, Type projection) => new(id, SchemaKind.InlineValue, 0, domain, [
        new(id, 1, SchemaKind.InlineValue, 0, [new(1, TypeExpr.Builtin(TypeTag.Byte))], stateTypeDefinition: typeof(byte)),
        new(id, 2, SchemaKind.InlineValue, 0, [new(1, TypeExpr.Builtin(TypeTag.UInt32))], stateTypeDefinition: typeof(uint)),
    ], currentValueFactory: (_, _) => new(Inline(id, 2), typeof(uint), typeof(UInt32StateOps), domain, projection),
        historicalValueFactory: (schema, _) => new(new(1, TypeTag.InlineValue, inlineSchema: schema),
            schema.Version == 1 ? typeof(byte) : typeof(uint), schema.Version == 1 ? typeof(ByteStateOps) : typeof(UInt32StateOps)));

    private static StateValueUpgradeProvider Provider(string id, string method) => new(TypeExpr.Named(id), 1, TypeExpr.Named(id), 2,
        typeof(MetaBootstrapContainerUpgradeTests).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!);
    private static void ConvertPoint(in byte prior, out uint next, UpgradeContext context) {
        Assert.Equal(new ObjectId(2), context.ObjectId);
        _calls!.Point++;
        next = 1000u + prior;
    }
    private static void ConvertKey(in byte prior, out uint next, UpgradeContext context) {
        Assert.Equal(new ObjectId(2), context.ObjectId);
        _calls!.Key++;
        next = _calls.CollapseKeys ? 0 : 10u + prior;
    }
    private static DurableSchema RootSchema(string kind) => new("ContainerUpgrade.Root." + kind, 1, DurableFieldInfo.Reference(1, OldLayout(kind).Type));
    private static DurableFieldInfo Inline(string id, int version) => new(1, TypeTag.InlineValue,
        inlineSchema: new DurableSchema(id, version, SchemaKind.InlineValue, new DurableFieldInfo(1, version == 1 ? TypeTag.Byte : TypeTag.UInt32)));
    private static ObjectLayout OldLayout(string kind) {
        DurableFieldInfo point = DurableFieldInfo.Nullable(1, Inline(PointId, 1));
        return kind switch {
            "Array" => ObjectLayout.ForArray(new(TypeExprKind.VectorArray, point)),
            "List" => ObjectLayout.ForList(new(point)),
            _ => ObjectLayout.ForDictionary(new(Inline(KeyId, 1), StateBindingContext.WithFieldId(point, 2))),
        };
    }
    private static PreparedBaseBody RootBody(in ObjectId state) {
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        writer.WriteVarUInt32(state.Value);
        return new(buffer.WrittenSpan);
    }
    private static void AssertCurrentContent(object current, string kind, int count) {
        Point?[] values = kind switch {
            "Array" => Assert.IsType<Point?[]>(current),
            "List" => Assert.IsType<List<Point?>>(current).ToArray(),
            _ => Assert.IsType<Dictionary<Key, Point?>>(current).Values.ToArray(),
        };
        Assert.Equal(count, values.Length);
        Assert.All(values, value => Assert.Null(value));
    }
    private sealed class Calls { internal int Point, Key, Allocations; internal bool CollapseKeys; }
    private sealed class PointRules;
    private sealed class KeyRules;
    private sealed class ContainerRoot : IDurableObject { internal object? Value; }
    private readonly record struct Point(uint X);
    private enum Key : uint { }
    private readonly struct PointProjection : IValueProjection<Point, uint> {
        public static uint Capture(in Point value, CaptureContext context, DurableFieldInfo slot) => value.X;
        public static void Hydrate(ref Point target, in uint state, ObjectReadTable objects, DurableFieldInfo slot) => target = new(state);
    }
    private readonly struct KeyProjection : IValueProjection<Key, uint> {
        public static uint Capture(in Key value, CaptureContext context, DurableFieldInfo slot) => (uint)value;
        public static void Hydrate(ref Key target, in uint state, ObjectReadTable objects, DurableFieldInfo slot) => target = (Key)state;
    }
    public void Dispose() {
        _calls = null;
        SharedReadModel.DeleteFixture(_path, Prefix);
    }
}
