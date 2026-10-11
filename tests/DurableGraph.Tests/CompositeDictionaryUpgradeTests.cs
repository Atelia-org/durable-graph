using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using System.Reflection;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed class CompositeDictionaryUpgradeTests {
    private static readonly List<UpgradeContext> Calls = [];

    [Theory]
    [InlineData(DictionaryComparerKind.CurrentDefault)]
    [InlineData(DictionaryComparerKind.Application)]
    public void HistoricalDtoOnlyReadAndExplicitTwoSlotUpgradePreserveIdentityCountAndMode(DictionaryComparerKind kind) {
        Reset();
        int resolverCalls = 0;
        ProbeContext context = new();
        DictionaryObjectBinding target = CompositeBinding(() => { resolverCalls++; throw new InvalidOperationException("Normalize must not request application behavior."); });
        ObjectStateRecord source = ReadHistorical(kind, [new(new(7, 90), new(3)), new(new(8, 91), new(4))]);
        Assert.Equal(typeof(Key1), context.ResolveStoredValue(source.Layout.Dictionary!.KeySlot).StateType);
        Assert.Null(context.ResolveStoredValue(source.Layout.Dictionary.KeySlot).DomainType);
        Assert.Null(context.GetDefinition("CompositeKey").DomainTypeDefinition);

        ObjectStateRecord upgraded = context.NormalizeDictionary(source, target);
        target.ValidateLookupKeys(upgraded, _ => throw new InvalidOperationException("These values contain no references."));
        FrozenDictionaryState<Key2, Value2> current = upgraded.GetDictionaryState<Key2, Value2>();
        Assert.Equal(source.Id, upgraded.Id);
        Assert.Equal(target.CurrentLayout, upgraded.Layout);
        Assert.Equal(kind, current.ComparerKind);
        Assert.Equal(2, current.Count);
        Assert.Equal(new DictionaryEntryState<Key2, Value2>[] { new(new(107, 90), new(30)), new(new(108, 91), new(40)) }, current.Entries.ToArray());
        Assert.Equal(new DictionaryEntryState<Key1, Value1>[] { new(new(7, 90), new(3)), new(new(8, 91), new(4)) }, source.GetDictionaryState<Key1, Value1>().Entries.ToArray());
        Assert.Equal(4, Calls.Count);
        Assert.All(Calls, call => {
            Assert.Equal(source.Id, call.ObjectId);
            Assert.Equal(2, call.DictionaryCount);
            Assert.Equal(source.Layout, call.SourceObjectLayout);
            Assert.Equal(target.CurrentLayout, call.TargetObjectLayout);
        });
        Assert.Equal(0, resolverCalls);
        Assert.Equal(0, CurrentKey.ComparisonCalls);
    }

    [Theory]
    [InlineData(DictionaryComparerKind.CurrentDefault)]
    [InlineData(DictionaryComparerKind.Application)]
    public void UpgradedCanonicalKeyCollisionFailsBeforeCurrentBehaviorIsRequested(DictionaryComparerKind kind) {
        Reset();
        int resolverCalls = 0;
        ProbeContext context = new(keyMethod: nameof(CollapseCanonicalKey));
        DictionaryObjectBinding target = CompositeBinding(() => { resolverCalls++; return new KeyComparer(); });
        ObjectStateRecord source = ReadHistorical(kind, [new(new(7, 90), new(3)), new(new(8, 91), new(4))]);
        Assert.Throws<InvalidDataException>(() => context.NormalizeDictionary(source, target));
        Assert.Equal(4, Calls.Count);
        Assert.Equal(0, resolverCalls);
        Assert.Equal(0, CurrentKey.ComparisonCalls);
        Assert.Equal(new Key1[] { new(7, 90), new(8, 91) }, source.GetDictionaryState<Key1, Value1>().Entries.ToArray().Select(entry => entry.Key));
    }

    [Theory]
    [InlineData(DictionaryComparerKind.CurrentDefault)]
    [InlineData(DictionaryComparerKind.Application)]
    public void UpgradedDistinctPersistentKeysThatCurrentBehaviorMergesFailOnlyDuringHydrate(DictionaryComparerKind kind) {
        Reset();
        int resolverCalls = 0;
        ProbeContext context = new(keyMethod: nameof(CollapseLookupKey));
        DictionaryObjectBinding target = CompositeBinding(() => { resolverCalls++; return new KeyComparer(); });
        ObjectStateRecord source = ReadHistorical(kind, [new(new(7, 90), new(3)), new(new(8, 91), new(4))]);
        ObjectStateRecord upgraded = context.NormalizeDictionary(source, target);
        target.ValidateLookupKeys(upgraded, _ => throw new InvalidOperationException("These values contain no references."));
        Assert.Equal(new Key2[] { new(10, 90), new(10, 91) }, upgraded.GetDictionaryState<Key2, Value2>().Entries.ToArray().Select(entry => entry.Key));
        Assert.Equal(0, CurrentKey.ComparisonCalls);
        Assert.Equal(0, resolverCalls);

        Dictionary<CurrentKey, Value2> domain = Assert.IsType<Dictionary<CurrentKey, Value2>>(target.Allocate(upgraded));
        Assert.Equal(kind == DictionaryComparerKind.Application ? 1 : 0, resolverCalls);
        ObjectReadTable objects = new(new Dictionary<ObjectId, object> { [upgraded.Id] = domain });
        Assert.Throws<InvalidDataException>(() => target.Hydrate(domain, upgraded, objects));
        Assert.True(CurrentKey.ComparisonCalls > 0);
        Assert.Equal(2, upgraded.GetDictionaryState<Key2, Value2>().Count);
        // Hydrate operates on an unpublished instance; it must not overwrite either frozen DTO view.
        Assert.Equal(new Key1[] { new(7, 90), new(8, 91) }, source.GetDictionaryState<Key1, Value1>().Entries.ToArray().Select(entry => entry.Key));
    }

    [Theory]
    [InlineData(DictionaryComparerKind.CurrentDefault, false)]
    [InlineData(DictionaryComparerKind.Application, false)]
    [InlineData(DictionaryComparerKind.CurrentDefault, true)]
    [InlineData(DictionaryComparerKind.Application, true)]
    public void EmptyDictionaryStillRequiresBothExplicitUpgradeCapabilities(DictionaryComparerKind kind, bool missingKey) {
        Reset();
        ProbeContext context = new() { SelectKeys = !missingKey, SelectValues = missingKey };
        Assert.Throws<InvalidDataException>(() => context.NormalizeDictionary(ReadHistorical(kind, []), CompositeBinding()));
        Assert.Empty(Calls);
        Assert.Equal(0, CurrentKey.ComparisonCalls);
    }

    [Theory]
    [InlineData(DictionaryComparerKind.CurrentDefault, "CompositeKey", 1)]
    [InlineData(DictionaryComparerKind.Application, "CompositeKey", 2)]
    [InlineData(DictionaryComparerKind.CurrentDefault, "CompositeValue", 2)]
    [InlineData(DictionaryComparerKind.Application, "CompositeValue", 1)]
    public void CachedUpgradeStillRejectsLateExactConflictForEmptyDictionary(DictionaryComparerKind kind, string id, int version) {
        Reset();
        ProbeContext context = new();
        context.NormalizeDictionary(ReadHistorical(kind, [new(new(7, 90), new(3))]), CompositeBinding());
        Calls.Clear();
        context.Registered[(TypeExpr.Named(id), version)] = new(id, version, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.UInt32));
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => context.NormalizeDictionary(ReadHistorical(kind, []), CompositeBinding()));
        Assert.IsType<SchemaConflictException>(error.InnerException);
        Assert.Empty(Calls);
        Assert.Equal(0, CurrentKey.ComparisonCalls);
    }

    [Fact]
    public void CurrentDefaultHistoricalInlineCanBecomeEnumWithoutChangingExactLayoutOrCaptureMode() {
        Reset();
        DictionaryLayout layout = NumericLayout();
        // A retained inline DTO has no persisted enum/struct provenance. No old domain declaration is supplied.
        ObjectStateRecord stored = ReadNumeric(DictionaryComparerKind.CurrentDefault);
        StateValueBinding key = new(layout.KeySlot, typeof(NumericState), typeof(NumericOps), typeof(CurrentEnum), typeof(EnumProjection));
        BuiltinStateValues.TryBindCurrent(typeof(int), out StateValueBinding value);
        DictionaryObjectBinding current = DictionaryObjectBinding.Create(typeof(Dictionary<CurrentEnum, int>), layout, key, value,
            getApplicationComparer: () => throw new InvalidOperationException("Mode 4 must not request application behavior."));
        ObjectStateRecord normalized = new ProbeContext().NormalizeDictionary(stored, current);
        Assert.Same(stored.GetDictionaryState<NumericState, int>(), normalized.GetDictionaryState<NumericState, int>());
        Dictionary<CurrentEnum, int> domain = Assert.IsType<Dictionary<CurrentEnum, int>>(current.Allocate(normalized));
        current.Hydrate(domain, normalized, new ObjectReadTable(new Dictionary<ObjectId, object> { [stored.Id] = domain }));
        Assert.Equal(9, domain[(CurrentEnum)123]);
        Assert.Equal(DictionaryComparerKind.ScalarDefault, DictionaryKeyPolicy.Identify(EqualityComparer<CurrentEnum>.Default, layout.KeySlot));
        Assert.Equal(DictionaryComparerKind.CurrentDefault, DictionaryKeyPolicy.Identify(domain.Comparer, layout.KeySlot));

        using CaptureContext capture = new CaptureSession().BeginCapture();
        ObjectStateRecord recaptured = current.Capture(stored.Id, domain, capture);
        Assert.Equal(DictionaryComparerKind.CurrentDefault, recaptured.GetDictionaryState<NumericState, int>().ComparerKind);
        Assert.False(((ICapturedStatePreparation)current).PrepareDelta(normalized, recaptured).HasChanges);
        Assert.Empty(Calls);
    }

    [Fact]
    public void HistoricalScalarModeCanReadAndNormalizeAgainstSameShapeStructButCannotAllocateItsDefault() {
        Reset();
        DictionaryLayout layout = NumericLayout();
        ObjectStateRecord stored = ReadNumeric(DictionaryComparerKind.ScalarDefault);
        BuiltinStateValues.TryBindCurrent(typeof(int), out StateValueBinding value);
        StateValueBinding key = new(layout.KeySlot, typeof(NumericState), typeof(NumericOps), typeof(CurrentNumericStruct), typeof(NumericStructProjection));
        DictionaryObjectBinding current = DictionaryObjectBinding.Create(typeof(Dictionary<CurrentNumericStruct, int>), layout, key, value,
            getApplicationComparer: () => throw new InvalidOperationException("Standard mode cannot ask application behavior to replace it."));
        ObjectStateRecord normalized = new ProbeContext().NormalizeDictionary(stored, current);
        current.ValidateLookupKeys(normalized, _ => throw new InvalidOperationException("There are no reference targets."));
        Assert.Same(stored.GetDictionaryState<NumericState, int>(), normalized.GetDictionaryState<NumericState, int>());
        Assert.Equal(DictionaryComparerKind.ScalarDefault, normalized.GetDictionaryState<NumericState, int>().ComparerKind);
        Assert.Throws<InvalidDataException>(() => current.Allocate(normalized));
        Assert.Equal(0, CurrentNumericStruct.ComparisonCalls);
        Assert.Empty(Calls);
    }

    [Theory]
    [InlineData(DictionaryComparerKind.CurrentDefault)]
    [InlineData(DictionaryComparerKind.Application)]
    public void SameExactDtoWithChangedCurrentBehaviorNeedsNoUpgradeButCanFailTryAdd(DictionaryComparerKind kind) {
        Reset();
        int resolverCalls = 0;
        DictionaryObjectBinding current = CompositeBinding(() => { resolverCalls++; return new KeyComparer(); });
        FrozenDictionaryState<Key2, Value2> frozen = new(kind, [new(new(7, 90), new(3)), new(new(7, 91), new(4))]);
        PreparedBaseBody body = DictionaryStateBody<Key2, Value2, Key2Ops, Value2Ops>.PrepareBase(frozen, CompositeLayout(2));
        DictionaryStateReader reader = DictionaryStateReader.Create(CompositeLayout(2), StoredKey(2), StoredValue(2));
        ObjectStateRecord exact = reader.Read(new(71), new Bodies(body.Body.ToArray()));
        ProbeContext context = new() { SelectKeys = false, SelectValues = false };
        ObjectStateRecord normalized = context.NormalizeDictionary(exact, current);
        current.ValidateLookupKeys(normalized, _ => throw new InvalidOperationException());
        Assert.Equal(0, resolverCalls);
        Assert.Equal(0, CurrentKey.ComparisonCalls);
        Assert.Empty(Calls);
        object allocated = current.Allocate(normalized);
        Assert.Throws<InvalidDataException>(() => current.Hydrate(allocated, normalized,
            new ObjectReadTable(new Dictionary<ObjectId, object> { [normalized.Id] = allocated })));
        Assert.Equal(2, exact.GetDictionaryState<Key2, Value2>().Count);
        Assert.Empty(Calls);
    }

    private static void Reset() { Calls.Clear(); CurrentKey.ComparisonCalls = 0; CurrentNumericStruct.ComparisonCalls = 0; }
    private static DurableSchema CompositeSchema(string id, int version) => id == "CompositeKey"
        ? new(id, version, SchemaKind.InlineValue, new DurableFieldInfo(1, version == 1 ? TypeTag.Int32 : TypeTag.Int64), new DurableFieldInfo(2, TypeTag.Int32))
        : new(id, version, SchemaKind.InlineValue, new DurableFieldInfo(1, version == 1 ? TypeTag.Int32 : TypeTag.Int64));
    private static DictionaryLayout CompositeLayout(int version) => new(
        new(1, TypeTag.InlineValue, inlineSchema: CompositeSchema("CompositeKey", version)),
        new(2, TypeTag.InlineValue, inlineSchema: CompositeSchema("CompositeValue", version)));
    private static StateValueBinding StoredKey(int version) => new(CompositeLayout(version).KeySlot,
        version == 1 ? typeof(Key1) : typeof(Key2), version == 1 ? typeof(Key1Ops) : typeof(Key2Ops));
    private static StateValueBinding StoredValue(int version) => new(CompositeLayout(version).ValueSlot,
        version == 1 ? typeof(Value1) : typeof(Value2), version == 1 ? typeof(Value1Ops) : typeof(Value2Ops));
    private static DictionaryObjectBinding CompositeBinding(Func<object?>? resolver = null) {
        DictionaryLayout layout = CompositeLayout(2);
        return DictionaryObjectBinding.Create(typeof(Dictionary<CurrentKey, Value2>), layout,
            new(layout.KeySlot, typeof(Key2), typeof(Key2Ops), typeof(CurrentKey), typeof(KeyProjection)),
            new(layout.ValueSlot, typeof(Value2), typeof(Value2Ops), typeof(Value2), typeof(ValueProjection)), getApplicationComparer: resolver);
    }
    private static ObjectStateRecord ReadHistorical(DictionaryComparerKind kind, DictionaryEntryState<Key1, Value1>[] entries) {
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        writer.WriteByte((byte)kind);
        writer.WriteVarUInt32((uint)entries.Length);
        foreach (DictionaryEntryState<Key1, Value1> entry in entries) {
            writer.WriteVarInt32(entry.Key.Code);
            writer.WriteVarInt32(entry.Key.Stamp);
            writer.WriteVarInt32(entry.Value.Number);
        }
        DictionaryStateReader reader = DictionaryStateReader.Create(CompositeLayout(1), StoredKey(1), StoredValue(1));
        ObjectStateRecord result = reader.Read(new(71), new Bodies(buffer.WrittenSpan.ToArray()));
        reader.ValidateLookupKeys(result, _ => throw new InvalidOperationException("No reference targets exist."));
        return result;
    }
    private static DurableSchema NumericSchema() => new("NumericKey", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int32));
    private static DictionaryLayout NumericLayout() => new(new(1, TypeTag.InlineValue, inlineSchema: NumericSchema()), new(2, TypeTag.Int32));
    private static ObjectStateRecord ReadNumeric(DictionaryComparerKind kind) {
        DictionaryLayout layout = NumericLayout();
        DictionaryStateReader reader = DictionaryStateReader.Create(layout, new(layout.KeySlot, typeof(NumericState), typeof(NumericOps)),
            new(layout.ValueSlot, typeof(int), typeof(Int32StateOps)));
        return reader.Read(new(33), new Bodies([(byte)kind, 1, 246, 1, 18]));
    }
    private static MethodInfo Method(string name) => typeof(CompositeDictionaryUpgradeTests).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
    private static void UpgradeKey(in Key1 prior, out Key2 next, UpgradeContext context) { Calls.Add(context); next = new(prior.Code + 100L, prior.Stamp); }
    private static void CollapseCanonicalKey(in Key1 prior, out Key2 next, UpgradeContext context) { Calls.Add(context); next = new(10, 0); }
    private static void CollapseLookupKey(in Key1 prior, out Key2 next, UpgradeContext context) { Calls.Add(context); next = new(10, prior.Stamp); }
    private static void UpgradeValue(in Value1 prior, out Value2 next, UpgradeContext context) { Calls.Add(context); next = new(prior.Number * 10L); }
    private sealed class KeyRules;
    private sealed class ValueRules;

    private sealed class ProbeContext(string keyMethod = nameof(UpgradeKey)) : StateBindingContext {
        internal bool SelectKeys { get; init; } = true;
        internal bool SelectValues { get; init; } = true;
        internal Dictionary<(TypeExpr Type, int Version), DurableSchema> Registered { get; } = [];
        public override Type? DictionaryKeyUpgradeRuleSet => SelectKeys ? typeof(KeyRules) : null;
        public override Type? DictionaryValueUpgradeRuleSet => SelectValues ? typeof(ValueRules) : null;
        public override StateValueUpgradeRuleSet GetValueUpgradeRuleSet(Type ruleSet) => ruleSet == typeof(KeyRules)
            ? new(ruleSet, [new(TypeExpr.Named("CompositeKey"), 1, TypeExpr.Named("CompositeKey"), 2, Method(keyMethod))])
            : new(ruleSet, [new(TypeExpr.Named("CompositeValue"), 1, TypeExpr.Named("CompositeValue"), 2, Method(nameof(UpgradeValue)))]);
        public override StateDefinitionBinding GetDefinition(string id) {
            if (id == "NumericKey") {
                return new(id, SchemaKind.InlineValue, 0, null,
                    [new(id, 1, SchemaKind.InlineValue, 0, [new(1, TypeExpr.Builtin(TypeTag.Int32))], stateTypeDefinition: typeof(NumericState))]);
            }
            return new(id, SchemaKind.InlineValue, 0, null, Enumerable.Range(1, 2).Select(version => {
                StateFieldTemplate number = new(1, TypeExpr.Builtin(version == 1 ? TypeTag.Int32 : TypeTag.Int64));
                return new StateSchemaTemplate(id, version, SchemaKind.InlineValue, 0,
                    id == "CompositeKey" ? [number, new(2, TypeExpr.Builtin(TypeTag.Int32))] : [number],
                    stateTypeDefinition: id == "CompositeKey" ? version == 1 ? typeof(Key1) : typeof(Key2) : version == 1 ? typeof(Value1) : typeof(Value2));
            }));
        }
        public override bool TryGetSchema(TypeExpr type, int version, out DurableSchema? schema) => Registered.TryGetValue((type, version), out schema);
        public override StateValueBinding ResolveStoredValue(DurableFieldInfo slot) {
            if (BuiltinStateValues.TryBindStored(slot, out StateValueBinding builtin)) { return builtin; }
            DurableSchema schema = slot.InlineSchema!;
            return schema.SchemaId == "NumericKey" ? new(slot, typeof(NumericState), typeof(NumericOps))
                : (schema.SchemaId == "CompositeKey" ? StoredKey(schema.Version) : StoredValue(schema.Version)).WithFieldId(slot.FieldId);
        }
        public override bool TryGetCurrentModel(Type domainType, out StateModelBinding? model) { model = null; return false; }
        public override StateValueBinding ResolveCurrentValue(Type domainType) => throw new NotSupportedException();
        public override StateReaderBinding ResolveReader(DurableSchema schema) => throw new NotSupportedException();
        public override TypeExpr GetTypeExpr(Type domainType) => throw new NotSupportedException();
        public override Type GetDomainType(TypeExpr type) => throw new NotSupportedException();
    }

    private readonly record struct Key1(int Code, int Stamp);
    private readonly record struct Key2(long Code, int Stamp);
    private readonly record struct Value1(int Number);
    private readonly record struct Value2(long Number);
    private readonly record struct NumericState(int Value);
    private enum CurrentEnum { Zero }
    private readonly struct CurrentKey(long code, int stamp) : IEquatable<CurrentKey> {
        internal static int ComparisonCalls;
        internal long Code { get; } = code;
        internal int Stamp { get; } = stamp;
        public bool Equals(CurrentKey other) { ComparisonCalls++; return Code == other.Code; }
        public override bool Equals(object? obj) => obj is CurrentKey other && Equals(other);
        public override int GetHashCode() { ComparisonCalls++; return Code.GetHashCode(); }
    }
    private readonly struct CurrentNumericStruct(int value) : IEquatable<CurrentNumericStruct> {
        internal static int ComparisonCalls;
        internal int Value { get; } = value;
        public bool Equals(CurrentNumericStruct other) { ComparisonCalls++; return Value % 10 == other.Value % 10; }
        public override bool Equals(object? obj) => obj is CurrentNumericStruct other && Equals(other);
        public override int GetHashCode() { ComparisonCalls++; return Value % 10; }
    }
    private sealed class KeyComparer : IEqualityComparer<CurrentKey> {
        public bool Equals(CurrentKey left, CurrentKey right) => left.Equals(right);
        public int GetHashCode(CurrentKey key) => key.GetHashCode();
    }
    private sealed class KeyProjection : IValueProjection<CurrentKey, Key2> {
        public static Key2 Capture(in CurrentKey value, CaptureContext context, DurableFieldInfo slot) => new(value.Code, value.Stamp);
        public static void Hydrate(ref CurrentKey target, in Key2 state, ObjectReadTable objects, DurableFieldInfo slot) => target = new(state.Code, state.Stamp);
    }
    private sealed class ValueProjection : IValueProjection<Value2, Value2> {
        public static Value2 Capture(in Value2 value, CaptureContext context, DurableFieldInfo slot) => value;
        public static void Hydrate(ref Value2 target, in Value2 state, ObjectReadTable objects, DurableFieldInfo slot) => target = state;
    }
    private sealed class EnumProjection : IValueProjection<CurrentEnum, NumericState> {
        public static NumericState Capture(in CurrentEnum value, CaptureContext context, DurableFieldInfo slot) => new((int)value);
        public static void Hydrate(ref CurrentEnum target, in NumericState state, ObjectReadTable objects, DurableFieldInfo slot) => target = (CurrentEnum)state.Value;
    }
    private sealed class NumericStructProjection : IValueProjection<CurrentNumericStruct, NumericState> {
        public static NumericState Capture(in CurrentNumericStruct value, CaptureContext context, DurableFieldInfo slot) => new(value.Value);
        public static void Hydrate(ref CurrentNumericStruct target, in NumericState state, ObjectReadTable objects, DurableFieldInfo slot) => target = new(state.Value);
    }
    private sealed class Bodies(params byte[][] bodies) : IStateBodySource {
        public int Count => bodies.Length;
        public ReadOnlySpan<byte> GetBody(int index) => bodies[index];
    }
    private sealed class Key1Ops : IStateOps<Key1> {
        public static bool StateEquals(in Key1 left, in Key1 right, DurableFieldInfo slot) => left == right;
        public static void WriteBase(ref BareValueWriter writer, in Key1 state, DurableFieldInfo slot) { writer.WriteVarInt32(state.Code); writer.WriteVarInt32(state.Stamp); }
        public static Key1 ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => new(reader.ReadVarInt32(), reader.ReadVarInt32());
        public static PreparedDeltaBody PrepareDelta(in Key1 prior, in Key1 current, DurableFieldInfo slot) => throw new NotSupportedException();
        public static Key1 ApplyDelta(ref BareValueReader reader, in Key1 prior, DurableFieldInfo slot) => throw new NotSupportedException();
        public static void VisitReferences(in Key1 state, IStateReferenceVisitor visitor, DurableFieldInfo slot) { }
    }
    private sealed class Key2Ops : IStateOps<Key2> {
        public static bool StateEquals(in Key2 left, in Key2 right, DurableFieldInfo slot) => left == right;
        public static void WriteBase(ref BareValueWriter writer, in Key2 state, DurableFieldInfo slot) { writer.WriteVarInt64(state.Code); writer.WriteVarInt32(state.Stamp); }
        public static Key2 ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => new(reader.ReadVarInt64(), reader.ReadVarInt32());
        public static PreparedDeltaBody PrepareDelta(in Key2 prior, in Key2 current, DurableFieldInfo slot) => throw new NotSupportedException();
        public static Key2 ApplyDelta(ref BareValueReader reader, in Key2 prior, DurableFieldInfo slot) => throw new NotSupportedException();
        public static void VisitReferences(in Key2 state, IStateReferenceVisitor visitor, DurableFieldInfo slot) { }
    }
    private sealed class Value1Ops : IStateOps<Value1> {
        public static bool StateEquals(in Value1 left, in Value1 right, DurableFieldInfo slot) => left == right;
        public static void WriteBase(ref BareValueWriter writer, in Value1 state, DurableFieldInfo slot) => writer.WriteVarInt32(state.Number);
        public static Value1 ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => new(reader.ReadVarInt32());
        public static PreparedDeltaBody PrepareDelta(in Value1 prior, in Value1 current, DurableFieldInfo slot) => throw new NotSupportedException();
        public static Value1 ApplyDelta(ref BareValueReader reader, in Value1 prior, DurableFieldInfo slot) => throw new NotSupportedException();
        public static void VisitReferences(in Value1 state, IStateReferenceVisitor visitor, DurableFieldInfo slot) { }
    }
    private sealed class Value2Ops : IStateOps<Value2> {
        public static bool StateEquals(in Value2 left, in Value2 right, DurableFieldInfo slot) => left == right;
        public static void WriteBase(ref BareValueWriter writer, in Value2 state, DurableFieldInfo slot) => writer.WriteVarInt64(state.Number);
        public static Value2 ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => new(reader.ReadVarInt64());
        public static PreparedDeltaBody PrepareDelta(in Value2 prior, in Value2 current, DurableFieldInfo slot) => throw new NotSupportedException();
        public static Value2 ApplyDelta(ref BareValueReader reader, in Value2 prior, DurableFieldInfo slot) => throw new NotSupportedException();
        public static void VisitReferences(in Value2 state, IStateReferenceVisitor visitor, DurableFieldInfo slot) { }
    }
    private sealed class NumericOps : IStateOps<NumericState> {
        public static bool StateEquals(in NumericState left, in NumericState right, DurableFieldInfo slot) => left == right;
        public static void WriteBase(ref BareValueWriter writer, in NumericState state, DurableFieldInfo slot) => writer.WriteVarInt32(state.Value);
        public static NumericState ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => new(reader.ReadVarInt32());
        public static PreparedDeltaBody PrepareDelta(in NumericState prior, in NumericState current, DurableFieldInfo slot) => throw new NotSupportedException();
        public static NumericState ApplyDelta(ref BareValueReader reader, in NumericState prior, DurableFieldInfo slot) => throw new NotSupportedException();
        public static void VisitReferences(in NumericState state, IStateReferenceVisitor visitor, DurableFieldInfo slot) { }
    }
}
