using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Reflection;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed class ArrayUpgradeTests {
    private static readonly List<UpgradeContext> Calls = [];

    [Fact]
    public void DirectEndpointConvertsOncePerElementAndPreservesArrayOwnerFacts() {
        Calls.Clear();
        TestContext context = new(Rule(1, 3, nameof(ConvertPoint)));
        ArrayLayout old = Layout(1, TypeExprKind.Rank2Array);
        ArrayObjectBinding current = Binding(3, TypeExprKind.Rank2Array);
        FrozenArrayState<Point1> frozen = new(new(2, 2), [new(1), new(2), new(3), new(4)]);
        ObjectStateRecord source = new(new(41), old, frozen);
        ObjectStateRecord result = context.NormalizeArray(source, current);
        Assert.Equal(source.Id, result.Id);
        Assert.Equal(current.ArrayLayout, result.Layout.Array);
        Assert.Same(frozen.Shape, result.GetArrayState<Point3>().Shape);
        Assert.Equal(new Point3[] { new(10), new(20), new(30), new(40) }, result.GetArrayState<Point3>().Elements.ToArray());
        Assert.Equal(new Point1[] { new(1), new(2), new(3), new(4) }, frozen.Elements.ToArray());
        Assert.Equal(4, Calls.Count);
        Assert.All(Calls, call => {
            Assert.Same(Calls[0], call);
            Assert.Equal(source.Id, call.ObjectId);
            Assert.Equal(source.Layout, call.SourceObjectLayout);
            Assert.Equal(current.CurrentLayout, call.TargetObjectLayout);
            Assert.Same(frozen.Shape, call.ArrayShape);
            Assert.Throws<InvalidOperationException>(() => call.SourceObjectSchema);
            Assert.Throws<InvalidOperationException>(() => call.TargetObjectSchema);
        });
        context.NormalizeArray(new(new(42), old, frozen), current);
        Assert.NotSame(Calls[0], Calls[4]);
    }

    [Fact]
    public void EmptyArrayStillRequiresDirectRuleAndValidDependencies() {
        Calls.Clear();
        ObjectStateRecord empty = Source(1, []);
        Assert.Throws<InvalidDataException>(() => new TestContext().NormalizeArray(empty, Binding(3)));
        TestContext unselected = new(Rule(1, 3, nameof(ConvertPoint))) { SelectRules = false };
        Assert.Throws<InvalidDataException>(() => unselected.NormalizeArray(empty, Binding(3)));
        // Arrays do not synthesize a 1 -> 3 path out of adjacent providers.
        Assert.Throws<InvalidDataException>(() => new TestContext(Rule(1, 2, nameof(OneToTwo)), Rule(2, 3, nameof(TwoToThree)))
            .NormalizeArray(empty, Binding(3)));
        ObjectStateRecord upgraded = new TestContext(Rule(1, 3, nameof(ConvertPoint))).NormalizeArray(empty, Binding(3));
        Assert.Empty(upgraded.GetArrayState<Point3>().Elements.ToArray());
        Assert.NotEqual(empty.Layout, upgraded.Layout);
        StateUpgradeDependency absent = new("missing", typeof(Rules), new("Point", 99), new("Point", 1));
        Assert.Throws<InvalidDataException>(() => new TestContext(Rule(1, 3, nameof(ConvertPoint), absent)).NormalizeArray(empty, Binding(3)));
        Assert.Empty(Calls);
    }

    [Fact]
    public void SameLayoutReusesFrozenStateWithoutRequiringRules() {
        Calls.Clear();
        TestContext context = new() { SelectRules = false };
        FrozenArrayState<Point3> frozen = new(new(1), [new(3)]);
        ObjectStateRecord source = new(new(9), Layout(3), frozen);
        ObjectStateRecord result = context.NormalizeArray(source, Binding(3));
        Assert.Same(frozen, result.GetArrayState<Point3>());
        Assert.Empty(Calls);
    }

    [Fact]
    public void CachedArrayPlanRechecksExactRequirementsBeforeCallbacksIncludingEmptyArray() {
        Calls.Clear();
        TestContext context = new(Rule(1, 3, nameof(ConvertPoint)));
        context.NormalizeArray(Source(1, [new(5)]), Binding(3));
        Calls.Clear();
        context.Registered[(TypeExpr.Named("Point"), 1)] = new("Point", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.UInt32));
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => context.NormalizeArray(Source(1, []), Binding(3)));
        Assert.Contains("array upgrade.source.element", error.Message);
        Assert.IsType<SchemaConflictException>(error.InnerException);
        Assert.Empty(Calls);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 1)]
    [InlineData(0, 3)]
    [InlineData(2, 3)]
    public void CachedArrayPlanRejectsLateNestedInlineConflictAlthoughOwnerSchemasStillMatch(int count, int conflictingVersion) {
        Calls.Clear();
        TestContext context = new(EnvelopeRule(nameof(ConvertEnvelope)), Rule(1, 3, nameof(ConvertPoint)));
        ArrayObjectBinding target = EnvelopeBinding();
        context.Registered[(TypeExpr.Named("Envelope"), 1)] = EnvelopeSchema(1);
        context.Registered[(TypeExpr.Named("Envelope"), 3)] = EnvelopeSchema(3);
        ObjectStateRecord warm = context.NormalizeArray(EnvelopeSource(1), target);
        Assert.Equal(new Envelope3(new(50)), warm.GetArrayState<Envelope3>()[0]);
        Assert.Equal(2, Calls.Count);
        Calls.Clear();

        // The array element's own exact Schema remains unchanged. A later registration
        // conflicts only with a nested inline requirement of the already cached plan.
        context.Registered[(TypeExpr.Named("Point"), conflictingVersion)] =
            new("Point", conflictingVersion, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.UInt32));
        ObjectStateRecord source = EnvelopeSource(count);
        Assert.Equal(source.Layout.Array!.ElementSlot.InlineSchema, context.Registered[(TypeExpr.Named("Envelope"), 1)]);
        Assert.Equal(target.ArrayLayout.ElementSlot.InlineSchema, context.Registered[(TypeExpr.Named("Envelope"), 3)]);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => context.NormalizeArray(source, target));
        Assert.Contains("field[1].inline", error.Message);
        Assert.IsType<SchemaConflictException>(error.InnerException);
        Assert.Empty(Calls);
        Assert.All(source.GetArrayState<Envelope1>().Elements.ToArray(), item => Assert.Equal(new Point1(5), item.Value));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(2, true)]
    public void UnusedBrokenChildToolPreventsEveryArrayCallback(int count, bool badGrandchildSelector) {
        Calls.Clear();
        StateUpgradeDependency nonexistent = new("never-used", typeof(Rules), new("Point", 99), new("Point", 1));
        StateValueUpgradeProvider child = badGrandchildSelector
            ? Rule(1, 3, nameof(ConvertPoint), nonexistent)
            : Rule(1, 3, nameof(TwoToThree)); // Nominally selected, but the input DTO signature is wrong.
        TestContext context = new(EnvelopeRule(nameof(IgnoreEnvelopeChild)), child);
        ObjectStateRecord source = EnvelopeSource(count);
        Assert.Throws<InvalidDataException>(() => context.NormalizeArray(source, EnvelopeBinding()));
        Assert.Empty(Calls);
        Assert.All(source.GetArrayState<Envelope1>().Elements.ToArray(), item => Assert.Equal(new Point1(5), item.Value));
    }

    [Fact]
    public void NestedValueToolReceivesArrayFactsAndLocalToolScope() {
        Calls.Clear();
        StateUpgradeDependency dependency = new("x", typeof(Rules), new("Point", 1), new("Point", 1));
        StateValueUpgradeProvider scalar = new(TypeExpr.Builtin(TypeTag.Int32), null, TypeExpr.Builtin(TypeTag.Int64), null, Method(nameof(ConvertNumber)));
        TestContext context = new(Rule(1, 3, nameof(ConvertWithTool), dependency), scalar);
        ObjectStateRecord result = context.NormalizeArray(Source(1, [new(3), new(4)]), Binding(3));
        Assert.Equal(new Point3[] { new(103), new(104) }, result.GetArrayState<Point3>().Elements.ToArray());
        Assert.Equal(4, Calls.Count);
        Assert.Same(Calls[0], Calls[2]);
        Assert.Same(Calls[1], Calls[3]);
        Assert.NotSame(Calls[0], Calls[1]);
        Assert.All(Calls, call => Assert.Equal(ObjectStateKind.Array, call.SourceObjectLayout.Kind));
    }

    [Fact]
    public void AmbiguousOrBrokenProviderFailsWithoutFallbackOrAnyCallback() {
        Calls.Clear();
        StateValueUpgradeProvider first = Rule(1, 3, nameof(ConvertPoint));
        StateValueUpgradeProvider other = Rule(1, 3, nameof(ConvertPointOther));
        Assert.Throws<InvalidDataException>(() => new TestContext(first, other).NormalizeArray(Source(1, [new(1)]), Binding(3)));
        Assert.Throws<InvalidDataException>(() => new TestContext(Rule(1, 3, nameof(TwoToThree)))
            .NormalizeArray(Source(1, [new(1)]), Binding(3)));
        Assert.Empty(Calls);
    }

    [Fact]
    public void CallbackFailureLeavesSourceUnmodified() {
        Calls.Clear();
        ObjectStateRecord source = Source(1, [new(1), new(2)]);
        Assert.Throws<ArithmeticException>(() => new TestContext(Rule(1, 3, nameof(Throwing))).NormalizeArray(source, Binding(3)));
        Assert.Equal(new Point1[] { new(1), new(2) }, source.GetArrayState<Point1>().Elements.ToArray());
        Assert.Single(Calls);
    }

    [Fact]
    public void ArrayValueRulePatternsCanMatchReferenceSlotsWithoutInlineVersion() {
        TypeExpr pattern = TypeExpr.VectorArray(TypeExpr.Parameter(0));
        _ = new StateValueUpgradeProvider(pattern, null, pattern, null, Method(nameof(CopyId)));
        Assert.Throws<ArgumentException>(() => new StateValueUpgradeProvider(pattern, 1, pattern, null, Method(nameof(CopyId))));
    }

    private static ObjectStateRecord Source(int version, Point1[] values) => new(new(7), Layout(version), new FrozenArrayState<Point1>(new(values.Length), values));
    private static ArrayLayout Layout(int version, TypeExprKind kind = TypeExprKind.VectorArray) => new(kind, new(1, TypeTag.InlineValue, inlineSchema: Schema(version)));
    private static DurableSchema Schema(int version) => new("Point", version, SchemaKind.InlineValue,
        new DurableFieldInfo(1, version == 1 ? TypeTag.Int32 : TypeTag.Int64));
    private static ArrayObjectBinding Binding(int version, TypeExprKind kind = TypeExprKind.VectorArray) {
        Type domain = kind == TypeExprKind.VectorArray ? typeof(Point3[]) : typeof(Point3).MakeArrayType((int)kind - 3);
        ArrayLayout layout = Layout(version, kind);
        return ArrayObjectBinding.Create(domain, layout, new(layout.ElementSlot, typeof(Point3), typeof(NoOps<Point3>), typeof(Point3), typeof(Projection<Point3>)));
    }
    private static StateValueUpgradeProvider Rule(int from, int to, string method, params StateUpgradeDependency[] dependencies) =>
        new(TypeExpr.Named("Point"), from, TypeExpr.Named("Point"), to, Method(method), dependencies);
    private static DurableSchema EnvelopeSchema(int version) => new("Envelope", version, SchemaKind.InlineValue,
        new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: Schema(version)));
    private static StateValueUpgradeProvider EnvelopeRule(string method) => new(TypeExpr.Named("Envelope"), 1, TypeExpr.Named("Envelope"), 3,
        Method(method), [new("value", typeof(Rules), new("Envelope", 1), new("Envelope", 1))]);
    private static ObjectStateRecord EnvelopeSource(int count) => new(new(71),
        new ArrayLayout(TypeExprKind.VectorArray, new(1, TypeTag.InlineValue, inlineSchema: EnvelopeSchema(1))),
        new FrozenArrayState<Envelope1>(new(count), Enumerable.Repeat(new Envelope1(new(5)), count).ToArray()));
    private static ArrayObjectBinding EnvelopeBinding() {
        ArrayLayout layout = new(TypeExprKind.VectorArray, new(1, TypeTag.InlineValue, inlineSchema: EnvelopeSchema(3)));
        return ArrayObjectBinding.Create(typeof(Envelope3[]), layout,
            new(layout.ElementSlot, typeof(Envelope3), typeof(NoOps<Envelope3>), typeof(Envelope3), typeof(Projection<Envelope3>)));
    }
    private static MethodInfo Method(string method) => typeof(ArrayUpgradeTests).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!;

    private static void ConvertPoint(in Point1 prior, out Point3 next, UpgradeContext context) { Calls.Add(context); next = new(prior.X * 10L); }
    private static void ConvertPointOther(in Point1 prior, out Point3 next, UpgradeContext context) { Calls.Add(context); next = new(prior.X); }
    private static void OneToTwo(in Point1 prior, out Point2 next, UpgradeContext context) { Calls.Add(context); next = new(prior.X); }
    private static void TwoToThree(in Point2 prior, out Point3 next, UpgradeContext context) { Calls.Add(context); next = new(prior.X); }
    private static void ConvertWithTool(in Point1 prior, out Point3 next, UpgradeContext context) {
        Calls.Add(context);
        next = new(context.GetValueUpgrade<int, long>("x")(prior.X));
    }
    private static void ConvertNumber(in int prior, out long next, UpgradeContext context) {
        Calls.Add(context);
        Assert.Throws<InvalidOperationException>(() => context.GetValueUpgrade<int, long>("x"));
        next = prior + 100L;
    }
    private static void Throwing(in Point1 prior, out Point3 next, UpgradeContext context) { Calls.Add(context); throw new ArithmeticException(); }
    private static void ConvertEnvelope(in Envelope1 prior, out Envelope3 next, UpgradeContext context) {
        Calls.Add(context);
        next = new(context.GetValueUpgrade<Point1, Point3>("value")(prior.Value));
    }
    private static void IgnoreEnvelopeChild(in Envelope1 prior, out Envelope3 next, UpgradeContext context) {
        Calls.Add(context);
        next = default;
    }
    private static void CopyId(in ObjectId prior, out ObjectId next, UpgradeContext context) => next = prior;
    private sealed class Rules;
    private readonly record struct Point1(int X);
    private readonly record struct Point2(long X);
    private readonly record struct Point3(long X);
    private readonly record struct Envelope1(Point1 Value);
    private readonly record struct Envelope3(Point3 Value);

    private sealed class TestContext(params StateValueUpgradeProvider[] providers) : StateBindingContext {
        internal bool SelectRules { get; init; } = true;
        internal Dictionary<(TypeExpr Type, int Version), DurableSchema> Registered { get; } = [];
        public override Type? ArrayElementUpgradeRuleSet => SelectRules ? typeof(Rules) : null;
        public override StateValueUpgradeRuleSet GetValueUpgradeRuleSet(Type ruleSet) => new(typeof(Rules), providers);
        public override StateDefinitionBinding GetDefinition(string id) => id == "Envelope"
            ? new("Envelope", SchemaKind.InlineValue, 0, null, new[] { 1, 3 }.Select(version =>
                new StateSchemaTemplate("Envelope", version, SchemaKind.InlineValue, 0,
                    [new(1, TypeExpr.Named("Point"), version)], stateTypeDefinition: version == 1 ? typeof(Envelope1) : typeof(Envelope3))))
            : new("Point", SchemaKind.InlineValue, 0, null,
            Enumerable.Range(1, 3).Select(version => new StateSchemaTemplate("Point", version, SchemaKind.InlineValue, 0,
                [new(1, TypeExpr.Builtin(version == 1 ? TypeTag.Int32 : TypeTag.Int64))],
                stateTypeDefinition: version == 1 ? typeof(Point1) : version == 2 ? typeof(Point2) : typeof(Point3))));
        protected override bool TryGetSchema(TypeExpr type, int version, out DurableSchema? schema) => Registered.TryGetValue((type, version), out schema);
        public override StateValueBinding ResolveStoredValue(DurableFieldInfo slot) => BuiltinStateValues.TryBindStored(slot, out StateValueBinding value) ? value :
            slot.InlineSchema!.SchemaId == "Envelope"
                ? new(slot, slot.InlineSchema.Version == 1 ? typeof(Envelope1) : typeof(Envelope3), typeof(NoOps<Envelope3>))
                : new(slot, slot.InlineSchema.Version == 1 ? typeof(Point1) : slot.InlineSchema.Version == 2 ? typeof(Point2) : typeof(Point3), typeof(NoOps<Point3>));
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
    private sealed class NoOps<T> : IStateOps<T> where T : unmanaged {
        public static bool StateEquals(in T left, in T right, DurableFieldInfo slot) => throw new NotSupportedException();
        public static void WriteBase(ref BareValueWriter writer, in T state, DurableFieldInfo slot) => throw new NotSupportedException();
        public static T ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => throw new NotSupportedException();
        public static PreparedDeltaBody PrepareDelta(in T prior, in T current, DurableFieldInfo slot) => throw new NotSupportedException();
        public static T ApplyDelta(ref BareValueReader reader, in T prior, DurableFieldInfo slot) => throw new NotSupportedException();
        public static void VisitReferences(in T state, IStateReferenceVisitor visitor, DurableFieldInfo slot) => throw new NotSupportedException();
    }
}
