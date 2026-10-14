using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;

using Atelia.Rbf;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class ListBindingCatalogTests {
    [Fact]
    public void AlgorithmSelectionIsFrozenBeforeLazyBindingAndDoesNotAffectLayoutOrReader() {
        StateModelRegistry registry = new();
        ModelExecutionEnvironment initial = registry.CreateEnvironment();
        registry.UseListDeltaAlgorithm(ListDeltaAlgorithm.LocalResync);
        ModelExecutionEnvironment local = registry.CreateEnvironment();
        registry.UseListDeltaAlgorithm(ListDeltaAlgorithm.Position);
        ModelExecutionEnvironment position = registry.CreateEnvironment();
        registry.UseListDeltaAlgorithm(ListDeltaAlgorithm.BoundedMyers);
        ModelExecutionEnvironment myers = registry.CreateEnvironment();
        Assert.Throws<ArgumentOutOfRangeException>(() => registry.UseListDeltaAlgorithm((ListDeltaAlgorithm)(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => registry.UseListDeltaAlgorithm((ListDeltaAlgorithm)999));

        ListObjectBinding Bind(ModelExecutionEnvironment snapshot) {
            Assert.True(snapshot.TryGetCurrentObjectBinding(typeof(List<int>), out ObjectBinding? binding));
            return Assert.IsAssignableFrom<ListObjectBinding>(binding);
        }
        ListObjectBinding a = Bind(initial), b = Bind(position), c = Bind(myers), d = Bind(local);
        Assert.Equal(ListDeltaAlgorithm.Adaptive, a.DeltaAlgorithm);
        Assert.Equal(ListDeltaAlgorithm.Position, b.DeltaAlgorithm);
        Assert.Equal(ListDeltaAlgorithm.BoundedMyers, c.DeltaAlgorithm);
        Assert.Equal(ListDeltaAlgorithm.LocalResync, d.DeltaAlgorithm);
        Assert.Same(a, Bind(initial));
        Assert.Equal(a.CurrentLayout, b.CurrentLayout);
        Assert.Equal(a.CurrentLayout, c.CurrentLayout);
        Assert.Equal(a.CurrentLayout, d.CurrentLayout);
        Assert.Equal(a.CurrentLayout, myers.ResolveObjectReader(a.CurrentLayout).Layout);

        // Cover the constructor's independent default as well as the registry field:
        // neither entry point may silently retain the previous Local default.
        Assert.Equal(ListDeltaAlgorithm.Adaptive, Bind(new ModelExecutionEnvironment([], [], [])).DeltaAlgorithm);
        registry.UseListDeltaAlgorithm(ListDeltaAlgorithm.Adaptive);
        Assert.Equal(ListDeltaAlgorithm.Adaptive, Bind(registry.CreateEnvironment()).DeltaAlgorithm);
        Assert.Equal(ListDeltaAlgorithm.Position, Bind(position).DeltaAlgorithm);

        StateValueBinding element = initial.ResolveCurrentValue(typeof(int));
        Assert.Throws<ArgumentOutOfRangeException>(() => ListObjectBinding.Create(typeof(List<int>),
            new(element.Slot), element, algorithm: (ListDeltaAlgorithm)999));
        Assert.Equal(ListDeltaAlgorithm.Adaptive, ListObjectBinding.Create(typeof(List<int>), new(element.Slot), element).DeltaAlgorithm);
        foreach (ListDeltaAlgorithm algorithm in Enum.GetValues<ListDeltaAlgorithm>()) {
            ListObjectBinding binding = ListObjectBinding.Create(typeof(List<int>), new(element.Slot), element, algorithm: algorithm);
            Assert.Equal(algorithm, binding.DeltaAlgorithm);
            Assert.Equal(a.CurrentLayout, binding.CurrentLayout);
        }
    }

    [Theory]
    [InlineData(typeof(List<int>))]
    [InlineData(typeof(List<string>))]
    [InlineData(typeof(List<List<int>>))]
    [InlineData(typeof(List<int[,]>))]
    [InlineData(typeof(List<string>[,,]))]
    [InlineData(typeof(List<List<int>[]>))]
    public void BuiltinCompositionsRoundTripAndBindObjectIdSlots(Type domain) {
        ModelExecutionEnvironment snapshot = new StateModelRegistry().CreateEnvironment();
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
    public void ReferenceOperandsAndPhantomGenericArgumentsDoNotCloseBodies() {
        int factories = 0;
        StateModelRegistry registry = new();
        registry.Register(new StateDefinitionBinding("Box", SchemaKind.ReferenceObject, 1, typeof(Box<>),
            [new("Box", 1, SchemaKind.ReferenceObject, 1, [])],
            currentModelFactory: (_, _) => { factories++; throw new InvalidOperationException("Unexpected closure."); }));
        ModelExecutionEnvironment snapshot = registry.CreateEnvironment();
        Type type = typeof(List<Box<List<int>>>);
        Assert.Equal(type, snapshot.GetDomainType(snapshot.GetTypeExpr(type)));
        Assert.True(snapshot.TryGetCurrentObjectBinding(type, out _));
        Assert.Equal(0, factories);
    }

    [Fact]
    public void UnsupportedElementsOpenTypesInterfacesAndSubclassesFailClosed() {
        ModelExecutionEnvironment snapshot = new StateModelRegistry().CreateEnvironment();
        Type[] unsupported = [typeof(List<>), typeof(List<object>), typeof(List<DateTime>),
            typeof(List<DayOfWeek>), typeof(IList<int>), typeof(DerivedList), typeof(List<DerivedList>)];
        foreach (Type type in unsupported) {
            Assert.Throws<InvalidDataException>(() => snapshot.GetTypeExpr(type));
            Exception? failure = Record.Exception(() => {
                if (snapshot.TryGetCurrentObjectBinding(type, out _)) {
                    throw new InvalidOperationException("Unsupported binding was accepted.");
                }
            });
            Assert.True(failure is null or InvalidDataException, $"{type}: {failure}");
        }
    }

    [Fact]
    public void HistoricalReaderOnlyNeedsRetainedStateAndChecksEachImmutableSelectedView() {
        string path = Path.Combine(Path.GetTempPath(), $"list-binding-{Guid.NewGuid():N}.rbf");
        try {
            StateReaderRegistry registry = new();
            registry.Register(new StateDefinitionBinding("RetiredPoint", SchemaKind.InlineValue, 0, null,
                [new("RetiredPoint", 1, SchemaKind.InlineValue, 0, [new(1, TypeExpr.Builtin(TypeTag.Int32))])],
                historicalValueFactory: static (schema, _) => new(
                    new(1, TypeTag.InlineValue, inlineSchema: schema), typeof(int), typeof(Int32StateOps))));
            DurableSchema exact = new("RetiredPoint", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int32));
            ObjectLayout layout = ObjectLayout.ForList(new(new(1, TypeTag.InlineValue, inlineSchema: exact)));
            DurableSchema conflict = new("RetiredPoint", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int64));
            SchemaCatalogView selected = LoadView("matching", exact), other = LoadView("conflicting", conflict);
            int lookups = 0;
            ModelExecutionEnvironment snapshot = registry.CreateEnvironment(schemaLookup: key => {
                lookups++;
                return selected.Schemas.GetValueOrDefault(key);
            });
            ObjectReaderBinding reader = snapshot.ResolveObjectReader(layout);
            int beforeHit = lookups;
            Assert.Same(reader, snapshot.ResolveObjectReader(layout));
            Assert.True(lookups > beforeHit);
            Assert.Equal(layout, reader.Layout);
            Assert.Throws<InvalidDataException>(() => snapshot.GetDomainType(layout.Type));
            ModelExecutionEnvironment rejected = registry.CreateEnvironment(schemaLookup: key => other.Schemas.GetValueOrDefault(key));
            InvalidDataException error = Assert.Throws<InvalidDataException>(() => rejected.ResolveObjectReader(layout));
            Assert.IsType<SchemaConflictException>(error.InnerException);
            Assert.Same(reader, snapshot.ResolveObjectReader(layout));
            Assert.Equal(exact, selected.Schemas[new(exact.Type, exact.Version)]);
            Assert.Equal(conflict, other.Schemas[new(exact.Type, exact.Version)]);

            SchemaCatalogView LoadView(string name, DurableSchema schema) {
                string repositoryPath = Path.Combine(path, name);
                FrameAddress selectedRoot;
                using (var writer = RepositoryStorageFacade.CreateNew(repositoryPath)) {
                    SchemaCatalogWorkspace catalog = new(writer.EnsureEditingAllowed);
                    catalog.Set(schema);
                    PreparedSchemaCatalogWrite candidate = catalog.Prepare();
                    using PendingRepositorySave pending = writer.Prepare("main", StoredGraphKind.State,
                        new(null, new(1), [ObjectVersionRecord.CreateBase(1,
                            TypedBaseBodyCodec.Encode(ObjectLayout.String, StringBodyPreparer.PrepareBase("catalog carrier")))]), candidate.Write);
                    selectedRoot = writer.Commit(pending).Business.SchemaRoot!.Value;
                }
                using var reopened = RepositoryStorageFacade.OpenReadOnlyExisting(repositoryPath);
                return SchemaCatalogView.Load(reopened.Store, selectedRoot);
            }
        } finally { DeleteTestPath(path); }
    }

    [Fact]
    public void RuleSelectionIsIndependentExplicitAndFrozen() {
        StateModelRegistry registry = new();
        ModelExecutionEnvironment before = registry.CreateEnvironment();
        Assert.Throws<InvalidOperationException>(() => registry.UseListElementUpgrades(typeof(ListRules)));
        registry.Register(new StateValueUpgradeRuleSet(typeof(ListRules), []));
        registry.Register(new StateValueUpgradeRuleSet(typeof(ArrayRules), []));
        registry.UseListElementUpgrades(typeof(ListRules));
        registry.UseListElementUpgrades(typeof(ListRules));
        registry.UseArrayElementUpgrades(typeof(ArrayRules));
        Assert.Throws<InvalidOperationException>(() => registry.UseListElementUpgrades(typeof(ArrayRules)));
        ModelExecutionEnvironment after = registry.CreateEnvironment();
        Assert.Null(before.ListElementUpgradeRuleSet);
        Assert.Equal(typeof(ListRules), after.ListElementUpgradeRuleSet);
        Assert.Equal(typeof(ArrayRules), after.ArrayElementUpgradeRuleSet);
    }

    [Fact]
    public void EmptyListReaderStillChecksNestedNominalKindAndArity() {
        StateModelRegistry registry = new();
        registry.Register(new StateDefinitionBinding("Point", SchemaKind.InlineValue, 0, null,
            [new("Point", 1, SchemaKind.InlineValue, 0, [])]));
        registry.Register(new StateDefinitionBinding("Node", SchemaKind.ReferenceObject, 0, null,
            [new("Node", 1, SchemaKind.ReferenceObject, 0, [])]));
        ModelExecutionEnvironment snapshot = registry.CreateEnvironment();
        TypeExpr[] invalid = [TypeExpr.Named("Point"), TypeExpr.Named("Missing"),
            TypeExpr.List(TypeExpr.Named("Missing")),
            TypeExpr.List(TypeExpr.Named("Point", TypeExpr.Builtin(TypeTag.Int32))),
            TypeExpr.Named("Node", TypeExpr.Builtin(TypeTag.Int32))];
        foreach (TypeExpr target in invalid) {
            ObjectLayout layout = ObjectLayout.ForList(new(DurableFieldInfo.Reference(1, target)));
            Assert.Throws<InvalidDataException>(() => snapshot.ResolveObjectReader(layout));
        }
        // The referenced inner List owns Point's exact version; the outer reader
        // only needs its nominal declaration, with no Point historical body.
        ObjectLayout valid = ObjectLayout.ForList(new(DurableFieldInfo.Reference(1, TypeExpr.List(TypeExpr.Named("Point")))));
        Assert.Equal(valid, snapshot.ResolveObjectReader(valid).Layout);
    }

    private sealed class Box<T> : IDurableObject;
    private sealed class DerivedList : List<int>;
    private sealed class ListRules;
    private sealed class ArrayRules;
}
