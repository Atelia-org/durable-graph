using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;

using Atelia.Rbf;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class DictionaryBindingCatalogTests {
    [Theory]
    [InlineData(typeof(Dictionary<int, int>))]
    [InlineData(typeof(Dictionary<string, string>))]
    [InlineData(typeof(Dictionary<int, List<int?>>))]
    [InlineData(typeof(Dictionary<int, Dictionary<string, int[]>>))]
    [InlineData(typeof(Dictionary<List<int>, double?>))]
    [InlineData(typeof(Dictionary<string, int>[,,]))]
    [InlineData(typeof(List<Dictionary<string, int>>))]
    public void SupportedCompositionsRoundTripAndUseObjectIdSlots(Type domain) {
        ModelExecutionEnvironment snapshot = new StateModelRegistry().CreateEnvironment();
        TypeExpr nominal = snapshot.GetTypeExpr(domain);
        Assert.Equal(domain, snapshot.GetDomainType(nominal));
        StateValueBinding value = snapshot.ResolveCurrentValue(domain);
        Assert.Equal(typeof(ObjectId), value.StateType);
        Assert.Equal(typeof(ObjectIdStateOps), value.StateOpsType);
        Assert.Equal(nominal, value.Slot.TargetType);
        Assert.True(snapshot.TryGetCurrentObjectBinding(domain, out ObjectBinding? binding));
        Assert.True(snapshot.TryGetCurrentObjectBinding(domain, out ObjectBinding? cached));
        Assert.Same(binding, cached);
        Assert.Equal(nominal, binding!.CurrentLayout.Type);
        Assert.Equal(binding.CurrentLayout, snapshot.ResolveObjectReader(binding.CurrentLayout).Layout);
    }

    [Fact]
    public void KeyAndValueRulesAreIndependentExplicitAndFrozen() {
        StateModelRegistry registry = new();
        ModelExecutionEnvironment before = registry.CreateEnvironment();
        Assert.Throws<InvalidOperationException>(() => registry.UseDictionaryKeyUpgrades(typeof(KeyRules)));
        Assert.Throws<InvalidOperationException>(() => registry.UseDictionaryValueUpgrades(typeof(ValueRules)));
        registry.Register(new StateValueUpgradeRuleSet(typeof(KeyRules), []));
        registry.Register(new StateValueUpgradeRuleSet(typeof(ValueRules), []));
        registry.UseDictionaryKeyUpgrades(typeof(KeyRules));
        registry.UseDictionaryValueUpgrades(typeof(ValueRules));
        registry.UseDictionaryKeyUpgrades(typeof(KeyRules));
        registry.UseDictionaryValueUpgrades(typeof(ValueRules));
        Assert.Throws<InvalidOperationException>(() => registry.UseDictionaryKeyUpgrades(typeof(ValueRules)));
        Assert.Throws<InvalidOperationException>(() => registry.UseDictionaryValueUpgrades(typeof(KeyRules)));
        ModelExecutionEnvironment after = registry.CreateEnvironment();
        Assert.Null(before.DictionaryKeyUpgradeRuleSet);
        Assert.Null(before.DictionaryValueUpgradeRuleSet);
        Assert.Equal(typeof(KeyRules), after.DictionaryKeyUpgradeRuleSet);
        Assert.Equal(typeof(ValueRules), after.DictionaryValueUpgradeRuleSet);
        Assert.Null(after.ListElementUpgradeRuleSet);
        Assert.Null(after.ArrayElementUpgradeRuleSet);
    }

    [Fact]
    public void ReferenceOperandsDoNotCloseTheirCurrentBodies() {
        int factories = 0;
        StateModelRegistry registry = new();
        registry.Register(new StateDefinitionBinding("Box", SchemaKind.ReferenceObject, 1, typeof(Box<>),
            [new("Box", 1, SchemaKind.ReferenceObject, 1, [])],
            currentModelFactory: (_, _) => { factories++; throw new InvalidOperationException("Unexpected body closure."); }));
        ModelExecutionEnvironment snapshot = registry.CreateEnvironment();
        Type domain = typeof(Dictionary<Box<Dictionary<string, int>>, Box<List<int>>>);
        Assert.Equal(domain, snapshot.GetDomainType(snapshot.GetTypeExpr(domain)));
        Assert.True(snapshot.TryGetCurrentObjectBinding(domain, out _));
        Assert.Equal(0, factories);
    }

    [Fact]
    public void UnsupportedClosuresInterfacesAndSubclassesFailClosed() {
        ModelExecutionEnvironment snapshot = new StateModelRegistry().CreateEnvironment();
        Type[] unsupported = [typeof(Dictionary<,>), typeof(Dictionary<int, object>),
            typeof(Dictionary<DateTime, int>), typeof(Dictionary<int, DayOfWeek>),
            typeof(IDictionary<int, int>), typeof(DerivedDictionary), typeof(List<DerivedDictionary>)];
        foreach (Type domain in unsupported) {
            Assert.Throws<InvalidDataException>(() => snapshot.GetTypeExpr(domain));
            Exception? failure = Record.Exception(() => {
                if (snapshot.TryGetCurrentObjectBinding(domain, out _)) {
                    throw new InvalidOperationException("An unsupported Dictionary shape was accepted.");
                }
            });
            Assert.True(failure is null or InvalidDataException, $"{domain}: {failure}");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RetainedReaderChecksBothExactSlotsInEachImmutableSelectedView(bool keyConflict) {
        string path = Path.Combine(Path.GetTempPath(), $"dictionary-binding-{Guid.NewGuid():N}.rbf");
        try {
            StateReaderRegistry registry = new();
            foreach (string id in new[] { "RetiredKey", "RetiredValue" }) {
                registry.Register(new StateDefinitionBinding(id, SchemaKind.InlineValue, 0, null,
                    [new(id, 1, SchemaKind.InlineValue, 0, [new(1, TypeExpr.Builtin(TypeTag.Int32))])],
                    historicalValueFactory: static (schema, _) => new(
                        new(1, TypeTag.InlineValue, inlineSchema: schema), typeof(int), typeof(Int32StateOps))));
            }
            DurableSchema key = new("RetiredKey", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int32));
            DurableSchema value = new("RetiredValue", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int32));
            ObjectLayout layout = ObjectLayout.ForDictionary(new(new(1, TypeTag.InlineValue, inlineSchema: key),
                new(2, TypeTag.InlineValue, inlineSchema: value)));
            DurableSchema conflict = new(keyConflict ? "RetiredKey" : "RetiredValue", 1,
                SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int64));
            SchemaCatalogView selected = LoadView("matching", key, value), other = LoadView("conflicting",
                keyConflict ? conflict : key, keyConflict ? value : conflict);
            int lookups = 0;
            ModelExecutionEnvironment snapshot = registry.CreateEnvironment(schemaLookup: lookup => {
                lookups++;
                return selected.Schemas.GetValueOrDefault(lookup);
            });
            ObjectReaderBinding reader = snapshot.ResolveObjectReader(layout);
            int beforeHit = lookups;
            Assert.Same(reader, snapshot.ResolveObjectReader(layout));
            Assert.True(lookups > beforeHit);
            Assert.Throws<InvalidDataException>(() => snapshot.GetDomainType(layout.Type));
            ModelExecutionEnvironment rejected = registry.CreateEnvironment(schemaLookup: lookup => other.Schemas.GetValueOrDefault(lookup));
            InvalidDataException error = Assert.Throws<InvalidDataException>(() => rejected.ResolveObjectReader(layout));
            Assert.IsType<SchemaConflictException>(error.InnerException);
            Assert.Same(reader, snapshot.ResolveObjectReader(layout));
            Assert.Equal(key, selected.Schemas[new(key.Type, key.Version)]);
            Assert.Equal(value, selected.Schemas[new(value.Type, value.Version)]);
            Assert.Equal(conflict, other.Schemas[new(conflict.Type, conflict.Version)]);

            SchemaCatalogView LoadView(string name, params DurableSchema[] schemas) {
                string repositoryPath = Path.Combine(path, name);
                FrameAddress selectedRoot;
                using (var writer = RepositoryStorageFacade.CreateNew(repositoryPath)) {
                    SchemaCatalogWorkspace catalog = new(writer.EnsureEditingAllowed);
                    foreach (DurableSchema schema in schemas) { catalog.Set(schema); }
                    PreparedSchemaCatalogWrite candidate = catalog.Prepare();
                    using PendingRepositorySave pending = MechanismFixture.Prepare(writer, "main", StoredGraphKind.State,
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
    public void EmptyReaderStillValidatesBothNominalClosures() {
        StateModelRegistry registry = new();
        registry.Register(new StateDefinitionBinding("Point", SchemaKind.InlineValue, 0, null,
            [new("Point", 1, SchemaKind.InlineValue, 0, [])]));
        ModelExecutionEnvironment snapshot = registry.CreateEnvironment();
        DurableFieldInfo integer = new(1, TypeTag.Int32);
        TypeExpr invalid = TypeExpr.Dictionary(TypeExpr.Builtin(TypeTag.String), TypeExpr.Named("Missing"));
        ObjectLayout badKey = ObjectLayout.ForDictionary(new(DurableFieldInfo.Reference(1, invalid), integer));
        ObjectLayout badValue = ObjectLayout.ForDictionary(new(integer, DurableFieldInfo.Reference(2, invalid)));
        Assert.Throws<InvalidDataException>(() => snapshot.ResolveObjectReader(badKey));
        Assert.Throws<InvalidDataException>(() => snapshot.ResolveObjectReader(badValue));
        TypeExpr nested = TypeExpr.Dictionary(TypeExpr.Builtin(TypeTag.String), TypeExpr.Named("Point"));
        ObjectLayout valid = ObjectLayout.ForDictionary(new(integer, DurableFieldInfo.Reference(2, nested)));
        Assert.Equal(valid, snapshot.ResolveObjectReader(valid).Layout);
    }

    private sealed class Box<T> : IDurableObject;
    private sealed class DerivedDictionary : Dictionary<int, int>;
    private sealed class KeyRules;
    private sealed class ValueRules;
}
