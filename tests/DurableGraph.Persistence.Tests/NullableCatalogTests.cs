using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using Atelia.Binary;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class NullableCatalogTests {
    [Theory]
    [InlineData(typeof(int?))]
    [InlineData(typeof(double?))]
    [InlineData(typeof(int?[]))]
    [InlineData(typeof(int?[,,,]))]
    [InlineData(typeof(List<int?>))]
    [InlineData(typeof(List<double?[]>))]
    public void CurrentNominalCompositionsRoundTripAndUseExactNullableState(Type domain) {
        ModelExecutionEnvironment snapshot = new StateModelRegistry().CreateEnvironment();
        TypeExpr type = snapshot.GetTypeExpr(domain);
        Assert.Equal(domain, snapshot.GetDomainType(type));
        StateValueBinding binding = snapshot.ResolveCurrentValue(domain);
        Assert.Same(binding, snapshot.ResolveCurrentValue(domain));
        if (Nullable.GetUnderlyingType(domain) is { } child) {
            Assert.Equal(TypeTag.Nullable, binding.Slot.TypeTag);
            Assert.Equal(typeof(NullableState<>).MakeGenericType(child), binding.StateType);
            Assert.Equal(domain, binding.DomainType);
            Assert.Equal(1, binding.Slot.NullableLayout!.ElementSlot.FieldId);
        }
        else {
            Assert.Equal(typeof(ObjectId), binding.StateType);
            Assert.True(snapshot.TryGetCurrentObjectBinding(domain, out ObjectBinding? value));
            Assert.Equal(type, value!.CurrentLayout.Type);
            Assert.Equal(value.CurrentLayout, snapshot.ResolveObjectReader(value.CurrentLayout).Layout);
        }
    }

    [Theory]
    [InlineData(typeof(System.Numerics.BigInteger?))]
    [InlineData(typeof(DayOfWeek?))]
    [InlineData(typeof(DateTime?))]
    [InlineData(typeof(List<DateTime?>))]
    public void UnsupportedNullableChildrenFailClosed(Type domain) {
        ModelExecutionEnvironment snapshot = new StateModelRegistry().CreateEnvironment();
        Assert.Throws<InvalidDataException>(() => snapshot.GetTypeExpr(domain));
        Assert.Throws<InvalidDataException>(() => snapshot.ResolveCurrentValue(domain));
    }

    [Fact]
    public void IndependentNominalGoldenUsesTheNullableTypeConstructor() {
        TypeExpr type = TypeExpr.Named("B", TypeExpr.Nullable(TypeExpr.Named("P")),
            TypeExpr.List(TypeExpr.Nullable(TypeExpr.Builtin(TypeTag.Int32))));
        byte[] nominalGolden = Convert.FromHexString("02034202090203500008090102");
        Assert.Equal(nominalGolden, WriteType(type));
        Assert.Equal(type, ReadType(nominalGolden));
        for (int i = 0; i < nominalGolden.Length; i++) {
            byte[] prefix = nominalGolden[..i];
            Exception? error = Record.Exception(() => ReadType(prefix));
            Assert.True(error is InvalidDataException or EndOfStreamException, $"Prefix {i}: {error}");
        }
    }

    [Fact]
    public void InlineNullableProjectionPreservesCompleteChildVersionAcrossContainers() {
        DurableSchema point = Point(1);
        DurableSchema owner = new("O", 1, Slot(point));
        ObjectLayout list = ObjectLayout.ForList(new(Slot(point)));
        ObjectLayout array = ObjectLayout.ForArray(new(TypeExprKind.Rank2Array, Slot(point)));
        SchemaCatalogWorkspace workspace = new(static () => { });
        workspace.Set(owner);
        workspace.Set(list);
        workspace.Set(array);
        SchemaCatalogView view = workspace.Prepare().View;
        Assert.Equal(2, view.Schemas.Count);
        Assert.Equal(4, view.DescriptorIds.Count); // Point, owner and two dynamic containers.
        DurableSchema projectedPoint = view.Schemas[new("P", 1)];
        Assert.Equal(point, projectedPoint);
        Assert.Equal(owner, view.Schemas[new("O", 1)]);
        Assert.Same(projectedPoint, view.Schemas[new("O", 1)].Fields[0].ValueSchema);
        Assert.Equal(list, view.ResolveLayout(view.DescriptorId(list)));
        Assert.Equal(array, view.ResolveLayout(view.DescriptorId(array)));
        Assert.Same(projectedPoint, view.ResolveLayout(view.DescriptorId(list)).List!.ElementSlot.ValueSchema);
        Assert.Same(projectedPoint, view.ResolveLayout(view.DescriptorId(array)).Array!.ElementSlot.ValueSchema);
    }

    [Theory]
    [InlineData("090104")] // string.
    [InlineData("09040102")] // array.
    [InlineData("09080102")] // List.
    [InlineData("09090102")] // Nullable.
    [InlineData("090300")] // Open parameter.
    public void InvalidNullableNominalOperandsAreRejected(string hex) {
        Assert.Throws<InvalidDataException>(() => ReadType(Convert.FromHexString(hex)));
    }

    [Fact]
    public void RetainedNullableValuesNeedNoDomainAndCacheTheirCompleteChildLayout() {
        StateModelRegistry registry = RetainedPoints();
        ModelExecutionEnvironment snapshot = registry.CreateEnvironment();
        StateValueBinding first = snapshot.ResolveStoredValue(Slot(Point(1)));
        StateValueBinding second = snapshot.ResolveStoredValue(Slot(Point(2)));
        Assert.Equal(typeof(NullableState<int>), first.StateType);
        Assert.Equal(typeof(NullableState<long>), second.StateType);
        Assert.NotEqual(first.Slot, second.Slot);
        Assert.Null(first.DomainType);
        Assert.Null(second.ProjectionType);
        Assert.Equal(typeof(int), snapshot.ResolveStoredValue(first.Slot.NullableLayout!.ElementSlot).StateType);
        Assert.Equal(first.Slot, snapshot.ResolveStoredValue(first.Slot).Slot);
        Assert.Equal(27, snapshot.ResolveStoredValue(DurableFieldInfo.Nullable(27, first.Slot.NullableLayout!.ElementSlot)).Slot.FieldId);
        foreach (DurableSchema exact in new[] { Point(1), Point(2) }) {
            ObjectLayout layout = ObjectLayout.ForList(new(Slot(exact)));
            Assert.Equal(layout, snapshot.ResolveObjectReader(layout).Layout);
            Assert.Throws<InvalidDataException>(() => snapshot.GetDomainType(layout.Type));
        }
    }

    [Fact]
    public void CachedNullableValuesAndEmptyContainerReadersRecheckChangedSchemaLookup() {
        SchemaCatalogView selected = Project(Point(1));
        StateModelRegistry.FrozenConfiguration configuration = RetainedPoints().FreezeConfiguration();
        ModelExecutionEnvironment snapshot = configuration.CreateEnvironment(schemaLookup: key => selected.Schemas.GetValueOrDefault(key));
        DurableFieldInfo slot = Slot(Point(1));
        ObjectLayout list = ObjectLayout.ForList(new(slot));
        ObjectLayout array = ObjectLayout.ForArray(new(TypeExprKind.VectorArray, slot));
        snapshot.ResolveStoredValue(slot);
        snapshot.ResolveObjectReader(list);
        snapshot.ResolveObjectReader(array);
        // This code-only lookup witness checks cached dependency guards. Actual
        // reader-issued exact-S environments retain their immutable selected view.
        selected = Project(new DurableSchema("P", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Byte)));
        Assert.Throws<InvalidDataException>(() => snapshot.ResolveStoredValue(slot));
        Assert.Throws<InvalidDataException>(() => snapshot.ResolveObjectReader(list));
        Assert.Throws<InvalidDataException>(() => snapshot.ResolveObjectReader(array));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullableNominalChildEstablishesValueKindInEitherPreparationOrder(bool ownerFirst) {
        DurableSchema owner = new("O", 1, DurableFieldInfo.Reference(1,
            TypeExpr.List(TypeExpr.Nullable(TypeExpr.Named("P")))));
        DurableSchema wrongKind = new("P", 1);
        SchemaCatalogWorkspace workspace = new(static () => { });
        foreach (DurableSchema schema in ownerFirst ? new[] { owner, wrongKind } : [wrongKind, owner]) {
            workspace.Set(schema);
        }
        Assert.Throws<InvalidDataException>(() => workspace.Prepare());
    }

    [Theory]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public void NullableInlineWrappersKeepTheExactSchemaDepthBudget(int depth, bool valid) {
        DurableSchema inline = new("NullableDepth0", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int32));
        for (int index = 1; index < depth; index++) {
            inline = new DurableSchema($"NullableDepth{index}", 1, SchemaKind.InlineValue, Slot(inline));
        }
        ObjectLayout list = ObjectLayout.ForList(new(Slot(inline)));
        SchemaCatalogWorkspace workspace = new(static () => { });
        workspace.Set(inline);
        workspace.Set(list);
        if (!valid) {
            Assert.Throws<InvalidDataException>(() => workspace.Prepare());
            Assert.Null(workspace.Accepted);
            return;
        }
        SchemaCatalogView prepared = workspace.Prepare().View;
        Dictionary<ObjectId, ObjectStateRecord> rows = prepared.Rows.ToDictionary();
        ObjectStateRecord declarations = rows[prepared.DescriptorListId];
        // Project the outer container first, so a child-first memo cannot mask
        // the complete Nullable(InlineValue) chain's depth.
        rows[declarations.Id] = new(declarations.Id, declarations.Layout.List!,
            new FrozenListState<ObjectId>(prepared.DescriptorIds.Reverse().ToArray()));
        SchemaCatalogView projected = new(prepared.RootId, rows);
        Assert.Equal(depth, projected.Schemas.Count);
        Assert.Equal(inline, projected.Schemas[new(inline.Type, 1)]);
        Assert.Equal(list, projected.ResolveLayout(projected.DescriptorId(list)));
        Assert.Same(projected.Schemas[new(inline.Type, 1)],
            projected.ResolveLayout(projected.DescriptorId(list)).List!.ElementSlot.ValueSchema);
    }

    [Fact]
    public void NullableNominalWrapperKeepsWireDepthLimit() {
        TypeExpr type = TypeExpr.Nullable(TypeExpr.Builtin(TypeTag.Int32));
        for (int i = 2; i < TypeExpr.MaximumDepth; i++) { type = TypeExpr.List(type); }
        Assert.Equal(type, ReadType(WriteType(type)));
        byte[] tooDeep = [.. Enumerable.Repeat((byte)8, TypeExpr.MaximumDepth - 1), 9, 1, 2];
        Assert.Throws<InvalidDataException>(() => ReadType(tooDeep));
    }

    private static SchemaCatalogView Project(DurableSchema schema) {
        SchemaCatalogWorkspace workspace = new(static () => { });
        workspace.Set(schema);
        return workspace.Prepare().View;
    }

    private static StateModelRegistry RetainedPoints() {
        StateModelRegistry registry = new();
        registry.Register(new StateDefinitionBinding("P", SchemaKind.InlineValue, 0, null,
            [new("P", 1, SchemaKind.InlineValue, 0, [new(1, TypeExpr.Builtin(TypeTag.Int32))]),
             new("P", 2, SchemaKind.InlineValue, 0, [new(1, TypeExpr.Builtin(TypeTag.Int64))])],
            historicalValueFactory: static (schema, _) => new(new(1, TypeTag.InlineValue, inlineSchema: schema),
                schema.Version == 1 ? typeof(int) : typeof(long),
                schema.Version == 1 ? typeof(Int32StateOps) : typeof(Int64StateOps))));
        return registry;
    }

    private static DurableSchema Point(int version) => new("P", version, SchemaKind.InlineValue,
        new DurableFieldInfo(1, version == 1 ? TypeTag.Int32 : TypeTag.Int64));
    private static DurableFieldInfo Slot(DurableSchema child) => DurableFieldInfo.Nullable(1, new(1, TypeTag.InlineValue, inlineSchema: child));
    private static byte[] WriteType(TypeExpr type) {
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        TypeExprWireCodec.Write(ref writer, type);
        return bytes.WrittenSpan.ToArray();
    }
    private static TypeExpr ReadType(byte[] bytes) {
        BareValueReader reader = new(bytes);
        TypeExpr type = TypeExprWireCodec.Read(ref reader);
        reader.EnsureFullyConsumed();
        return type;
    }
}
