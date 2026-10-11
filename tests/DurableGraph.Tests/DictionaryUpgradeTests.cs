using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Reflection;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed class DictionaryUpgradeTests {
    private static readonly List<UpgradeContext> Calls = [];

    [Fact]
    public void BothExactSlotsUpgradeWithIndependentRulesAndPreserveOwnerFacts() {
        Calls.Clear();
        TestContext context = StandardContext();
        ObjectStateRecord source = Source([new(new(1), new(3)), new(new(2), new(4))]);
        DictionaryObjectBinding target = Binding();
        ObjectStateRecord result = context.NormalizeDictionary(source, target);
        Assert.Equal(source.Id, result.Id);
        Assert.Equal(target.CurrentLayout, result.Layout);
        FrozenDictionaryState<Key3, Point3> current = result.GetDictionaryState<Key3, Point3>();
        Assert.Equal(DictionaryComparerKind.ScalarDefault, current.ComparerKind);
        Assert.Equal(new DictionaryEntryState<Key3, Point3>[] { new(new(101), new(30)), new(new(102), new(40)) }, current.Entries.ToArray());
        Assert.Equal(new DictionaryEntryState<Key1, Point1>[] { new(new(1), new(3)), new(new(2), new(4)) }, source.GetDictionaryState<Key1, Point1>().Entries.ToArray());
        Assert.Equal(4, Calls.Count);
        Assert.All(Calls, call => {
            Assert.Equal(source.Id, call.ObjectId);
            Assert.Equal(source.Layout, call.SourceObjectLayout);
            Assert.Equal(target.CurrentLayout, call.TargetObjectLayout);
            Assert.Equal(2, call.DictionaryCount);
            Assert.Null(call.ListCount);
            Assert.Null(call.ArrayShape);
            Assert.Throws<InvalidOperationException>(() => call.SourceObjectSchema);
            Assert.Throws<InvalidOperationException>(() => call.TargetObjectSchema);
        });
        Assert.Same(Calls[0], Calls[2]);
        Assert.Same(Calls[1], Calls[3]);
        Assert.NotSame(Calls[0], Calls[1]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnchangedSlotIsPreservedWithoutItsRuleSet(bool changeKey) {
        Calls.Clear();
        TestContext context = StandardContext(selectKeys: changeKey, selectValues: !changeKey);
        ObjectStateRecord source = changeKey
            ? new(new(7), Layout(1, 3), new FrozenDictionaryState<Key1, Point3>(DictionaryComparerKind.ScalarDefault, [new(new(5), new(8))]))
            : new(new(7), Layout(3, 1), new FrozenDictionaryState<Key3, Point1>(DictionaryComparerKind.ScalarDefault, [new(new(5), new(8))]));
        FrozenDictionaryState<Key3, Point3> current = context.NormalizeDictionary(source, Binding()).GetDictionaryState<Key3, Point3>();
        Assert.Equal(new Key3(changeKey ? 105 : 5), current[0].Key);
        Assert.Equal(new Point3(changeKey ? 8 : 80), current[0].Value);
        Assert.Single(Calls);
    }

    [Fact]
    public void EqualLayoutsReuseFrozenBufferWithoutRules() {
        Calls.Clear();
        FrozenDictionaryState<Key3, Point3> frozen = new(DictionaryComparerKind.ScalarDefault, [new(new(5), new(8))]);
        ObjectStateRecord source = new(new(7), Layout(3, 3), frozen);
        ObjectStateRecord result = new TestContext { SelectKeys = false, SelectValues = false }.NormalizeDictionary(source, Binding());
        Assert.Same(frozen, result.GetDictionaryState<Key3, Point3>());
        Assert.Empty(Calls);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void MissingEitherSelectedRuleSetPreventsBothCallbacksEvenForEmptyDictionary(int count, bool omitKeys) {
        Calls.Clear();
        TestContext context = StandardContext(selectKeys: !omitKeys, selectValues: omitKeys);
        Assert.Throws<InvalidDataException>(() => context.NormalizeDictionary(Source(Entries(count)), Binding()));
        Assert.Empty(Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void MissingValueRuleFailsBeforeAnyValidKeyCallback(int count) {
        Calls.Clear();
        TestContext context = new([KeyRule(nameof(ConvertKey))], []);
        Assert.Throws<InvalidDataException>(() => context.NormalizeDictionary(Source(Entries(count)), Binding()));
        Assert.Empty(Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void UnusedBrokenValueDependencyFailsBeforeAnyKeyOrValueCallback(int count) {
        Calls.Clear();
        StateUpgradeDependency missing = new("unused", typeof(ValueRules), new("Point", 99), new("Point", 1));
        TestContext context = new([KeyRule(nameof(ConvertKey))], [ValueRule(nameof(ConvertPoint), missing)]);
        Assert.Throws<InvalidDataException>(() => context.NormalizeDictionary(Source(Entries(count)), Binding()));
        Assert.Empty(Calls);
    }

    [Fact]
    public void DirectEndpointsDoNotInventAdjacentUpgradePaths() {
        Calls.Clear();
        StateValueUpgradeProvider first = new(TypeExpr.Named("Key"), 1, TypeExpr.Named("Key"), 2, Method(nameof(KeyOneToTwo)));
        StateValueUpgradeProvider second = new(TypeExpr.Named("Key"), 2, TypeExpr.Named("Key"), 3, Method(nameof(KeyTwoToThree)));
        TestContext context = new([first, second], [ValueRule(nameof(ConvertPoint))]);
        Assert.Throws<InvalidDataException>(() => context.NormalizeDictionary(Source([]), Binding()));
        Assert.Empty(Calls);
    }

    [Fact]
    public void AmbiguousOrWrongValueProviderCannotRunKeyCallbacksOrFallback() {
        Calls.Clear();
        TestContext ambiguous = new([KeyRule(nameof(ConvertKey))], [ValueRule(nameof(ConvertPoint)), ValueRule(nameof(ConvertPointOther))]);
        Assert.Throws<InvalidDataException>(() => ambiguous.NormalizeDictionary(Source(Entries(1)), Binding()));
        TestContext wrongSignature = new([KeyRule(nameof(ConvertKey))], [ValueRule(nameof(KeyTwoToThree))]);
        Assert.Throws<InvalidDataException>(() => wrongSignature.NormalizeDictionary(Source(Entries(1)), Binding()));
        Assert.Empty(Calls);
    }

    [Theory]
    [InlineData(0, "Key", 1)]
    [InlineData(2, "Key", 1)]
    [InlineData(0, "Key", 3)]
    [InlineData(2, "Key", 3)]
    [InlineData(0, "Point", 1)]
    [InlineData(2, "Point", 1)]
    [InlineData(0, "Point", 3)]
    [InlineData(2, "Point", 3)]
    public void CachedPlanRechecksBothExactEndpointsBeforeAnyCallback(int count, string id, int version) {
        Calls.Clear();
        TestContext context = StandardContext();
        context.NormalizeDictionary(Source(Entries(1)), Binding());
        Calls.Clear();
        context.Registered[(TypeExpr.Named(id), version)] = new(id, version, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.UInt32));
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => context.NormalizeDictionary(Source(Entries(count)), Binding()));
        Assert.Contains($"dictionary upgrade.{(version == 1 ? "source" : "target")}.{(id == "Key" ? "key" : "value")}", error.Message);
        Assert.IsType<SchemaConflictException>(error.InnerException);
        Assert.Empty(Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void CachedNestedValueConflictPreventsKeyCallbacksAlthoughOuterSchemaMatches(int count) {
        Calls.Clear();
        StateValueUpgradeProvider envelope = new(TypeExpr.Named("Envelope"), 1, TypeExpr.Named("Envelope"), 3, Method(nameof(ConvertEnvelope)),
            [new("point", typeof(ValueRules), new("Envelope", 1), new("Envelope", 1))]);
        TestContext context = new([KeyRule(nameof(ConvertKey))], [envelope, ValueRule(nameof(ConvertPoint))]);
        DictionaryObjectBinding target = EnvelopeBinding();
        context.Registered[(TypeExpr.Named("Envelope"), 1)] = EnvelopeSchema(1);
        context.Registered[(TypeExpr.Named("Envelope"), 3)] = EnvelopeSchema(3);
        context.NormalizeDictionary(EnvelopeSource(1), target);
        Calls.Clear();
        context.Registered[(TypeExpr.Named("Point"), 1)] = new("Point", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.UInt32));
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => context.NormalizeDictionary(EnvelopeSource(count), target));
        Assert.Contains("field[1].inline", error.Message);
        Assert.IsType<SchemaConflictException>(error.InnerException);
        Assert.Empty(Calls);
    }

    [Fact]
    public void NestedToolsInheritDictionaryFactsAndUseProviderLocalScope() {
        Calls.Clear();
        StateUpgradeDependency child = new("number", typeof(ValueRules), new("Point", 1), new("Point", 1));
        StateValueUpgradeProvider number = new(TypeExpr.Builtin(TypeTag.Int32), null, TypeExpr.Builtin(TypeTag.Int64), null, Method(nameof(ConvertNumber)));
        TestContext context = new([KeyRule(nameof(ConvertKey))], [ValueRule(nameof(ConvertWithTool), child), number]);
        ObjectStateRecord result = context.NormalizeDictionary(Source([new(new(1), new(3)), new(new(2), new(4))]), Binding());
        Assert.Equal(new Point3(1003), result.GetDictionaryState<Key3, Point3>()[0].Value);
        Assert.Equal(6, Calls.Count);
        Assert.All(Calls, call => {
            Assert.Equal(2, call.DictionaryCount);
            Assert.Null(call.ListCount);
            Assert.Null(call.ArrayShape);
            Assert.Equal(result.Id, call.ObjectId);
            Assert.Equal(ObjectStateKind.Dictionary, call.SourceObjectLayout.Kind);
        });
        Assert.Same(Calls[2], Calls[5]);
        Assert.NotSame(Calls[1], Calls[2]);
    }

    [Fact]
    public void ExplicitNullableValueLiftingPreservesAbsenceAndDictionaryOwnerFacts() {
        Calls.Clear();
        TestContext context = new([KeyRule(nameof(ConvertKey))], [ValueRule(nameof(ConvertPoint))]) { LiftValues = true };
        ObjectStateRecord source = new(new(8), NullableLayout(1),
            new FrozenDictionaryState<Key1, NullableState<Point1>>(DictionaryComparerKind.ScalarDefault,
                [new(new(1), new(new Point1(3))), new(new(2), default)]));
        FrozenDictionaryState<Key3, NullableState<Point3>> result =
            context.NormalizeDictionary(source, NullableBinding()).GetDictionaryState<Key3, NullableState<Point3>>();
        Assert.Equal(new Point3(30), result[0].Value.Value);
        Assert.False(result[1].Value.HasValue);
        Assert.Equal(new Key3(102), result[1].Key);
        Assert.Equal(3, Calls.Count);
        Assert.All(Calls, call => {
            Assert.Equal(2, call.DictionaryCount);
            Assert.Equal(new ObjectId(8), call.ObjectId);
            Assert.Null(call.ListCount);
        });
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void EmptyOrAllAbsentDictionaryStillPrebindsNullableValueCapabilityBeforeKeyCalls(int count, bool lifting) {
        Calls.Clear();
        TestContext context = new([KeyRule(nameof(ConvertKey))], lifting ? [] : [ValueRule(nameof(ConvertPoint))]) { LiftValues = lifting };
        ObjectStateRecord source = new(new(8), NullableLayout(1),
            new FrozenDictionaryState<Key1, NullableState<Point1>>(DictionaryComparerKind.ScalarDefault,
                Enumerable.Range(1, count).Select(i => new DictionaryEntryState<Key1, NullableState<Point1>>(new(i), default)).ToArray()));
        Assert.Throws<InvalidDataException>(() => context.NormalizeDictionary(source, NullableBinding()));
        Assert.Empty(Calls);
    }

    [Fact]
    public void CachedPlansDoNotRetainPreviousObjectIdOrCount() {
        Calls.Clear();
        TestContext context = StandardContext();
        context.NormalizeDictionary(Source(Entries(1)), Binding());
        UpgradeContext first = Calls[0];
        Calls.Clear();
        ObjectStateRecord source = Source(Entries(3), new(92));
        context.NormalizeDictionary(source, Binding());
        Assert.Equal(6, Calls.Count);
        Assert.All(Calls, call => {
            Assert.NotSame(first, call);
            Assert.Equal(new ObjectId(92), call.ObjectId);
            Assert.Equal(3, call.DictionaryCount);
        });
        Assert.Equal(1, first.DictionaryCount);
    }

    [Fact]
    public void KeyUpgradeCollisionIsRejectedWithoutChangingSource() {
        Calls.Clear();
        ObjectStateRecord source = Source([new(new(1), new(3)), new(new(2), new(4))]);
        TestContext context = new([KeyRule(nameof(CollapseKey))], [ValueRule(nameof(ConvertPoint))]);
        Assert.Throws<InvalidDataException>(() => context.NormalizeDictionary(source, Binding()));
        Assert.Equal(new Key1[] { new(1), new(2) }, source.GetDictionaryState<Key1, Point1>().Entries.ToArray().Select(item => item.Key));
        Assert.Equal(4, Calls.Count);
    }

    [Fact]
    public void CallbackFailureLeavesFrozenSourceUntouched() {
        Calls.Clear();
        ObjectStateRecord source = Source([new(new(1), new(3)), new(new(2), new(4))]);
        TestContext context = new([KeyRule(nameof(ConvertKey))], [ValueRule(nameof(Throwing))]);
        Assert.Throws<ArithmeticException>(() => context.NormalizeDictionary(source, Binding()));
        Assert.Equal(new DictionaryEntryState<Key1, Point1>[] { new(new(1), new(3)), new(new(2), new(4)) }, source.GetDictionaryState<Key1, Point1>().Entries.ToArray());
        Assert.Equal(2, Calls.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void BindingRejectsNormalizerThatChangesIdentityCountOrComparer(int changedFact) {
        Assert.True(BuiltinStateValues.TryBindCurrent(typeof(string), out StateValueBinding key));
        Assert.True(BuiltinStateValues.TryBindCurrent(typeof(int), out StateValueBinding value));
        DictionaryLayout layout = new(key.Slot, value.Slot);
        FrozenDictionaryState<ObjectId, int> prior = new(DictionaryComparerKind.StringOrdinal, [new(new(2), 5)]);
        DictionaryObjectBinding target = DictionaryObjectBinding.Create(typeof(Dictionary<string, int>), layout, key, value,
            (_, source) => new(changedFact == 0 ? new ObjectId(99) : source.Id, layout,
                new FrozenDictionaryState<ObjectId, int>(
                    changedFact == 2 ? DictionaryComparerKind.ReferenceIdentity : DictionaryComparerKind.StringOrdinal,
                    changedFact == 1 ? [new(new(2), 5), new(new(3), 7)] : [new(new(2), 5)])));
        ObjectStateRecord source = new(new(7), layout, prior);
        Assert.Throws<InvalidDataException>(() => target.Normalize(source));
        Assert.Equal(DictionaryComparerKind.StringOrdinal, prior.ComparerKind);
        Assert.Equal(new DictionaryEntryState<ObjectId, int>[] { new(new(2), 5) }, prior.Entries.ToArray());
    }

    [Fact]
    public void WrongSourceKindIdNominalOrStateFailsBeforeCallbacks() {
        Calls.Clear();
        TestContext context = StandardContext();
        Assert.Throws<InvalidDataException>(() => context.NormalizeDictionary(new(new(1), "value"), Binding()));
        Assert.Throws<InvalidDataException>(() => context.NormalizeDictionary(Source([], default(ObjectId)), Binding()));
        Assert.Throws<InvalidDataException>(() => context.NormalizeDictionary(new(new(1), new DictionaryLayout(new(1, TypeTag.Int32), new(2, TypeTag.Int32)),
            new FrozenDictionaryState<int, int>(DictionaryComparerKind.ScalarDefault, [])), Binding()));
        Assert.Throws<InvalidDataException>(() => context.NormalizeDictionary(new(new(1), Layout(1, 1),
            new FrozenDictionaryState<Key3, Point3>(DictionaryComparerKind.ScalarDefault, [])), Binding()));
        Assert.Empty(Calls);
    }

    private static DictionaryEntryState<Key1, Point1>[] Entries(int count) =>
        Enumerable.Range(1, count).Select(i => new DictionaryEntryState<Key1, Point1>(new(i), new(i + 2))).ToArray();
    private static ObjectStateRecord Source(DictionaryEntryState<Key1, Point1>[] entries, ObjectId? id = null) =>
        new(id ?? new ObjectId(7), Layout(1, 1), new FrozenDictionaryState<Key1, Point1>(DictionaryComparerKind.ScalarDefault, entries));
    private static DictionaryLayout Layout(int keyVersion, int valueVersion) => new(
        new(1, TypeTag.InlineValue, inlineSchema: Schema("Key", keyVersion)),
        new(2, TypeTag.InlineValue, inlineSchema: Schema("Point", valueVersion)));
    private static DurableSchema Schema(string id, int version) => new(id, version, SchemaKind.InlineValue,
        new DurableFieldInfo(1, version == 1 ? TypeTag.Int32 : TypeTag.Int64));
    private static DictionaryObjectBinding Binding() {
        DictionaryLayout layout = Layout(3, 3);
        return DictionaryObjectBinding.Create(typeof(Dictionary<CurrentKey, Point3>), layout, CurrentKeyBinding(layout.KeySlot),
            new(layout.ValueSlot, typeof(Point3), typeof(NoOps<Point3>), typeof(Point3), typeof(Projection<Point3>)));
    }
    private static StateValueBinding CurrentKeyBinding(DurableFieldInfo slot) => new(slot, typeof(Key3), typeof(Key3Ops), typeof(CurrentKey), typeof(KeyProjection));
    private static StateValueUpgradeProvider KeyRule(string method) => new(TypeExpr.Named("Key"), 1, TypeExpr.Named("Key"), 3, Method(method));
    private static StateValueUpgradeProvider ValueRule(string method, params StateUpgradeDependency[] dependencies) =>
        new(TypeExpr.Named("Point"), 1, TypeExpr.Named("Point"), 3, Method(method), dependencies);
    private static TestContext StandardContext(bool selectKeys = true, bool selectValues = true) =>
        new([KeyRule(nameof(ConvertKey))], [ValueRule(nameof(ConvertPoint))]) { SelectKeys = selectKeys, SelectValues = selectValues };
    private static DurableSchema EnvelopeSchema(int version) => new("Envelope", version, SchemaKind.InlineValue,
        new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: Schema("Point", version)));
    private static ObjectStateRecord EnvelopeSource(int count) => new(new(7), new DictionaryLayout(
        new(1, TypeTag.InlineValue, inlineSchema: Schema("Key", 1)), new(2, TypeTag.InlineValue, inlineSchema: EnvelopeSchema(1))),
        new FrozenDictionaryState<Key1, Envelope1>(DictionaryComparerKind.ScalarDefault,
            Enumerable.Range(1, count).Select(i => new DictionaryEntryState<Key1, Envelope1>(new(i), new(new(i)))).ToArray()));
    private static DictionaryObjectBinding EnvelopeBinding() {
        DictionaryLayout layout = new(new(1, TypeTag.InlineValue, inlineSchema: Schema("Key", 3)),
            new(2, TypeTag.InlineValue, inlineSchema: EnvelopeSchema(3)));
        return DictionaryObjectBinding.Create(typeof(Dictionary<CurrentKey, Envelope3>), layout, CurrentKeyBinding(layout.KeySlot),
            new(layout.ValueSlot, typeof(Envelope3), typeof(NoOps<Envelope3>), typeof(Envelope3), typeof(Projection<Envelope3>)));
    }
    private static DictionaryLayout NullableLayout(int version) => new(
        new(1, TypeTag.InlineValue, inlineSchema: Schema("Key", version)),
        DurableFieldInfo.Nullable(2, new(1, TypeTag.InlineValue, inlineSchema: Schema("Point", version))));
    private static DictionaryObjectBinding NullableBinding() {
        DictionaryLayout layout = NullableLayout(3);
        return DictionaryObjectBinding.Create(typeof(Dictionary<CurrentKey, Point3?>), layout, CurrentKeyBinding(layout.KeySlot),
            new(layout.ValueSlot, typeof(NullableState<Point3>), typeof(NullableStateOps<Point3, NoOps<Point3>>), typeof(Point3?),
                typeof(NullableValueProjection<Point3, Point3, Projection<Point3>>)));
    }
    private static MethodInfo Method(string name) => typeof(DictionaryUpgradeTests).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
    private static void ConvertKey(in Key1 prior, out Key3 next, UpgradeContext context) { Calls.Add(context); next = new(prior.Value + 100L); }
    private static void CollapseKey(in Key1 prior, out Key3 next, UpgradeContext context) { Calls.Add(context); next = new(10); }
    private static void KeyOneToTwo(in Key1 prior, out Key2 next, UpgradeContext context) { Calls.Add(context); next = new(prior.Value); }
    private static void KeyTwoToThree(in Key2 prior, out Key3 next, UpgradeContext context) { Calls.Add(context); next = new(prior.Value); }
    private static void ConvertPoint(in Point1 prior, out Point3 next, UpgradeContext context) { Calls.Add(context); next = new(prior.X * 10L); }
    private static void ConvertPointOther(in Point1 prior, out Point3 next, UpgradeContext context) { Calls.Add(context); next = new(prior.X); }
    private static void ConvertWithTool(in Point1 prior, out Point3 next, UpgradeContext context) { Calls.Add(context); next = new(context.GetValueUpgrade<int, long>("number")(prior.X)); }
    private static void ConvertNumber(in int prior, out long next, UpgradeContext context) {
        Calls.Add(context);
        Assert.Throws<InvalidOperationException>(() => context.GetValueUpgrade<int, long>("number"));
        next = prior + 1000L;
    }
    private static void ConvertEnvelope(in Envelope1 prior, out Envelope3 next, UpgradeContext context) { Calls.Add(context); next = new(context.GetValueUpgrade<Point1, Point3>("point")(prior.Value)); }
    private static void Throwing(in Point1 prior, out Point3 next, UpgradeContext context) { Calls.Add(context); throw new ArithmeticException(); }

    private sealed class KeyRules;
    private sealed class ValueRules;
    private enum CurrentKey : long { }
    private readonly record struct Key1(int Value);
    private readonly record struct Key2(long Value);
    private readonly record struct Key3(long Value);
    private readonly record struct Point1(int X);
    private readonly record struct Point2(long X);
    private readonly record struct Point3(long X);
    private readonly record struct Envelope1(Point1 Value);
    private readonly record struct Envelope3(Point3 Value);

    private sealed class TestContext(StateValueUpgradeProvider[]? keys = null, StateValueUpgradeProvider[]? values = null) : StateBindingContext {
        internal bool SelectKeys { get; init; } = true;
        internal bool SelectValues { get; init; } = true;
        internal bool LiftValues { get; init; }
        internal Dictionary<(TypeExpr Type, int Version), DurableSchema> Registered { get; } = [];
        public override Type? DictionaryKeyUpgradeRuleSet => SelectKeys ? typeof(KeyRules) : null;
        public override Type? DictionaryValueUpgradeRuleSet => SelectValues ? typeof(ValueRules) : null;
        public override StateValueUpgradeRuleSet GetValueUpgradeRuleSet(Type ruleSet) => ruleSet == typeof(KeyRules)
            ? new(ruleSet, keys ?? []) : new(ruleSet, values ?? [], allowNullableLifting: LiftValues);
        public override StateDefinitionBinding GetDefinition(string id) => id == "Envelope"
            ? new(id, SchemaKind.InlineValue, 0, null, new[] { 1, 3 }.Select(version =>
                new StateSchemaTemplate(id, version, SchemaKind.InlineValue, 0, [new(1, TypeExpr.Named("Point"), version)],
                    stateTypeDefinition: version == 1 ? typeof(Envelope1) : typeof(Envelope3))))
            : new(id, SchemaKind.InlineValue, 0, null, Enumerable.Range(1, 3).Select(version =>
                new StateSchemaTemplate(id, version, SchemaKind.InlineValue, 0, [new(1, TypeExpr.Builtin(version == 1 ? TypeTag.Int32 : TypeTag.Int64))],
                    stateTypeDefinition: StateType(id, version))));
        private static Type StateType(string id, int version) => id == "Key"
            ? version == 1 ? typeof(Key1) : version == 2 ? typeof(Key2) : typeof(Key3)
            : version == 1 ? typeof(Point1) : version == 2 ? typeof(Point2) : typeof(Point3);
        public override bool TryGetSchema(TypeExpr type, int version, out DurableSchema? schema) => Registered.TryGetValue((type, version), out schema);
        public override StateValueBinding ResolveStoredValue(DurableFieldInfo slot) {
            if (slot.NullableLayout is { } nullable) {
                Type optional = typeof(NullableState<>).MakeGenericType(ResolveStoredValue(nullable.ElementSlot).StateType);
                return new(slot, optional, typeof(NoOps<>).MakeGenericType(optional));
            }
            if (BuiltinStateValues.TryBindStored(slot, out StateValueBinding value)) { return value; }
            DurableSchema schema = slot.InlineSchema!;
            Type stateType = schema.SchemaId == "Envelope" ? schema.Version == 1 ? typeof(Envelope1) : typeof(Envelope3) : StateType(schema.SchemaId, schema.Version);
            return new(slot, stateType, typeof(NoOps<>).MakeGenericType(stateType));
        }
        public override bool TryGetCurrentModel(Type domainType, out StateModelBinding? model) { model = null; return false; }
        public override StateValueBinding ResolveCurrentValue(Type domainType) => throw new NotSupportedException();
        public override StateReaderBinding ResolveReader(DurableSchema schema) => throw new NotSupportedException();
        public override TypeExpr GetTypeExpr(Type domainType) => throw new NotSupportedException();
        public override Type GetDomainType(TypeExpr type) => throw new NotSupportedException();
    }
    private sealed class Projection<T> : IValueProjection<T, T> where T : unmanaged {
        public static T Capture(in T value, CaptureContext context, DurableFieldInfo slot) => value;
        public static void Hydrate(ref T target, in T state, ObjectReadTable objects, DurableFieldInfo slot) => target = state;
    }
    private sealed class KeyProjection : IValueProjection<CurrentKey, Key3> {
        public static Key3 Capture(in CurrentKey value, CaptureContext context, DurableFieldInfo slot) => new((long)value);
        public static void Hydrate(ref CurrentKey target, in Key3 state, ObjectReadTable objects, DurableFieldInfo slot) => target = (CurrentKey)state.Value;
    }
    private sealed class Key3Ops : IStateOps<Key3> {
        public static bool StateEquals(in Key3 left, in Key3 right, DurableFieldInfo slot) => left == right;
        public static void WriteBase(ref BareValueWriter writer, in Key3 state, DurableFieldInfo slot) => writer.WriteVarInt64(state.Value);
        public static Key3 ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => new(reader.ReadVarInt64());
        public static PreparedDeltaBody PrepareDelta(in Key3 prior, in Key3 current, DurableFieldInfo slot) => throw new NotSupportedException();
        public static Key3 ApplyDelta(ref BareValueReader reader, in Key3 prior, DurableFieldInfo slot) => throw new NotSupportedException();
        public static void VisitReferences(in Key3 state, IStateReferenceVisitor visitor, DurableFieldInfo slot) { }
    }
    private sealed class NoOps<T> : IStateOps<T> where T : unmanaged {
        public static bool StateEquals(in T left, in T right, DurableFieldInfo slot) => throw new NotSupportedException();
        public static void WriteBase(ref BareValueWriter writer, in T state, DurableFieldInfo slot) => throw new NotSupportedException();
        public static T ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => throw new NotSupportedException();
        public static PreparedDeltaBody PrepareDelta(in T prior, in T current, DurableFieldInfo slot) => throw new NotSupportedException();
        public static T ApplyDelta(ref BareValueReader reader, in T prior, DurableFieldInfo slot) => throw new NotSupportedException();
        public static void VisitReferences(in T state, IStateReferenceVisitor visitor, DurableFieldInfo slot) => throw new NotSupportedException();
    }
}
