using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.Rbf;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class ArrayBindingCatalogTests {
    [Theory]
    [InlineData(typeof(int[]))]
    [InlineData(typeof(string[,]))]
    [InlineData(typeof(double[,,]))]
    [InlineData(typeof(uint[,,,]))]
    [InlineData(typeof(int[][]))]
    [InlineData(typeof(int[][,]))]
    public void ArrayTypesRoundTripAndUseAnObjectIdSlot(Type domain) {
        StateModelSnapshot snapshot = new StateModelRegistry().Snapshot();
        TypeExpr type = snapshot.GetTypeExpr(domain);
        Assert.Equal(domain, snapshot.GetDomainType(type));
        StateValueBinding slot = snapshot.ResolveCurrentValue(domain);
        Assert.Equal(typeof(ObjectId), slot.StateType);
        Assert.Equal(typeof(ObjectIdStateOps), slot.StateOpsType);
        Assert.Equal(type, slot.Slot.TargetType);
        Assert.True(snapshot.TryGetCurrentObjectBinding(domain, out ObjectBinding? binding));
        Assert.True(snapshot.TryGetCurrentObjectBinding(domain, out ObjectBinding? cached));
        Assert.Same(binding, cached);
        Assert.Equal(type, binding!.CurrentLayout.Type);
    }

    [Fact]
    public void ArrayReferencesAndGenericArgumentsDoNotCloseReferencedClassBodies() {
        int bodyFactories = 0;
        StateModelRegistry registry = new();
        registry.Register(new StateDefinitionBinding("Box", SchemaKind.ReferenceObject, 1, typeof(Box<>),
            [new("Box", 1, SchemaKind.ReferenceObject, 1, [])],
            currentModelFactory: (_, _) => { bodyFactories++; throw new InvalidOperationException("Unexpected class body closure."); }));
        StateModelSnapshot snapshot = registry.Snapshot();
        Type domain = typeof(Box<int[]>[][]);
        TypeExpr type = snapshot.GetTypeExpr(domain);
        Assert.Equal(domain, snapshot.GetDomainType(type));
        Assert.Equal(type, snapshot.ResolveCurrentValue(domain).Slot.TargetType);
        Assert.True(snapshot.TryGetCurrentObjectBinding(domain, out _));
        Assert.True(snapshot.TryGetCurrentObjectBinding(typeof(Box<int[]>[]), out _));
        Assert.Equal(0, bodyFactories);
    }

    [Fact]
    public void UnsupportedArrayKindsAndElementsFailAtBinding() {
        StateModelSnapshot snapshot = new StateModelRegistry().Snapshot();
        Type[] unsupported = [typeof(int).MakeArrayType(1), typeof(int).MakeArrayType(5),
            typeof(object[]), typeof(DateTime[]), typeof(Box<>).MakeArrayType()];
        foreach (Type type in unsupported) {
            Assert.Throws<InvalidDataException>(() => snapshot.GetTypeExpr(type));
            Assert.Throws<InvalidDataException>(() => snapshot.TryGetCurrentObjectBinding(type, out _));
        }
    }

    [Fact]
    public void HistoricalArrayReaderNeedsOnlyRetainedInlineStateAndRechecksSchemaAuthority() {
        string directory = Path.Combine(Path.GetTempPath(), "durable-array-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            using GraphResources file = GraphResources.CreateLegacyNew(Path.Combine(directory, "schemas.rbf"));
            SchemaStore schemas = file.Schemas;
            StateReaderRegistry registry = new();
            registry.Register(new StateDefinitionBinding("RetiredPoint", SchemaKind.InlineValue, 0, null,
                [new("RetiredPoint", 1, SchemaKind.InlineValue, 0, [new(1, TypeExpr.Builtin(TypeTag.Int32))])],
                historicalValueFactory: static (schema, _) => new(
                    new(1, TypeTag.InlineValue, inlineSchema: schema), typeof(int), typeof(Int32StateOps))));
            DurableSchema exact = new("RetiredPoint", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int32));
            ObjectLayout layout = ObjectLayout.ForArray(new(TypeExprKind.VectorArray,
                new(1, TypeTag.InlineValue, inlineSchema: exact)));
            StateModelSnapshot snapshot = registry.Snapshot(schemas);
            ObjectReaderBinding reader = snapshot.ResolveObjectReader(layout);
            Assert.Same(reader, snapshot.ResolveObjectReader(layout));
            Assert.Equal(layout, reader.Layout);
            Assert.Throws<InvalidDataException>(() => snapshot.GetDomainType(layout.Type));

            schemas.Register(new DurableSchema("RetiredPoint", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int64)));
            Assert.Throws<InvalidDataException>(() => snapshot.ResolveObjectReader(layout));
        } finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void SelectedArrayRulesAreExplicitUniqueAndFrozenPerSnapshot() {
        StateModelRegistry registry = new();
        StateModelSnapshot before = registry.Snapshot();
        Assert.Throws<InvalidOperationException>(() => registry.UseArrayElementUpgrades(typeof(Rules)));
        StateValueUpgradeRuleSet rules = new(typeof(Rules), []);
        registry.Register(rules);
        registry.Register(new StateValueUpgradeRuleSet(typeof(OtherRules), []));
        registry.UseArrayElementUpgrades(typeof(Rules));
        registry.UseArrayElementUpgrades(typeof(Rules));
        Assert.Throws<InvalidOperationException>(() => registry.UseArrayElementUpgrades(typeof(OtherRules)));
        Assert.Null(before.ArrayElementUpgradeRuleSet);
        StateModelSnapshot after = registry.Snapshot();
        Assert.Equal(typeof(Rules), after.ArrayElementUpgradeRuleSet);
        Assert.Same(rules, after.GetValueUpgradeRuleSet(after.ArrayElementUpgradeRuleSet!));
    }

    [Fact]
    public void InvalidReferenceElementMetadataIsRejectedBeforeEvenAnEmptyArrayCanBeRead() {
        StateModelSnapshot snapshot = RetainedArrayDeclarations();
        TypeExpr scalar = TypeExpr.Builtin(TypeTag.Int32);
        TypeExpr[] invalid = [
            TypeExpr.Named("Point"), // An inline family cannot occupy an ObjectId element slot.
            TypeExpr.Named("Node", scalar),
            TypeExpr.Named("Missing"),
            TypeExpr.VectorArray(TypeExpr.Named("Missing")),
            TypeExpr.MultiDimArray(TypeExpr.Named("Point", scalar), 2),
            TypeExpr.Named("Box", TypeExpr.VectorArray(TypeExpr.Named("Point", scalar))),
            TypeExpr.Named("Box", TypeExpr.Named("Missing")),
            TypeExpr.Named("Box", scalar, scalar),
        ];
        foreach (TypeExpr target in invalid) {
            ObjectLayout layout = ObjectLayout.ForArray(new(TypeExprKind.VectorArray, DurableFieldInfo.Reference(1, target)));
            Assert.Throws<InvalidDataException>(() => snapshot.ResolveObjectReader(layout));
        }
    }

    [Fact]
    public void NominalArrayOperandsNeedRetainedMetadataButNotCurrentClrTypesOrObjectBodies() {
        StateModelSnapshot snapshot = RetainedArrayDeclarations();
        TypeExpr point = TypeExpr.Named("Point");
        TypeExpr[] valid = [TypeExpr.Named("Node"), TypeExpr.Named("Box", point),
            TypeExpr.VectorArray(point), TypeExpr.Named("Box", TypeExpr.MultiDimArray(point, 4)),
            TypeExpr.VectorArray(TypeExpr.Named("Box", TypeExpr.Builtin(TypeTag.String)))];
        foreach (TypeExpr target in valid) {
            ObjectLayout layout = ObjectLayout.ForArray(new(TypeExprKind.VectorArray, DurableFieldInfo.Reference(1, target)));
            ObjectReaderBinding reader = snapshot.ResolveObjectReader(layout);
            Assert.Same(reader, snapshot.ResolveObjectReader(layout));
            Assert.Throws<InvalidDataException>(() => snapshot.GetDomainType(layout.Type));
        }
    }

    [Fact]
    public void LegacyExactReadersSupplyNominalMetadataWithoutDefinitionTemplates() {
        DurableSchema schema = new("LegacyNode", 1);
        StateReaderBinding<int> legacy = new(schema,
            static (ref BareValueReader reader) => throw new InvalidOperationException("Body must not be closed or executed."),
            static (ref BareValueReader reader, in int prior) => throw new InvalidOperationException("Body must not be executed."),
            static (in int state, IStateReferenceVisitor visitor) => { });
        StateModelSnapshot snapshot = new([], [], new() { [new(schema.Type, schema.Version)] = legacy });
        ObjectLayout layout = ObjectLayout.ForArray(new(TypeExprKind.VectorArray, DurableFieldInfo.Reference(1, schema.Type)));
        Assert.Equal(layout, snapshot.ResolveObjectReader(layout).Layout);
        ObjectLayout wrongArity = ObjectLayout.ForArray(new(TypeExprKind.VectorArray,
            DurableFieldInfo.Reference(1, TypeExpr.Named("LegacyNode", TypeExpr.Builtin(TypeTag.Int32)))));
        Assert.Throws<InvalidDataException>(() => snapshot.ResolveObjectReader(wrongArity));
    }

    private static StateModelSnapshot RetainedArrayDeclarations() {
        StateModelRegistry registry = new();
        registry.Register(new StateDefinitionBinding("Point", SchemaKind.InlineValue, 0, null,
            [new("Point", 1, SchemaKind.InlineValue, 0, [])]));
        registry.Register(new StateDefinitionBinding("Node", SchemaKind.ReferenceObject, 0, null,
            [new("Node", 1, SchemaKind.ReferenceObject, 0, [])]));
        registry.Register(new StateDefinitionBinding("Box", SchemaKind.ReferenceObject, 1, null,
            [new("Box", 1, SchemaKind.ReferenceObject, 1, [])]));
        return registry.Snapshot();
    }

    private sealed class Box<T> : IDurableObject;
    private sealed class Rules;
    private sealed class OtherRules;
}
