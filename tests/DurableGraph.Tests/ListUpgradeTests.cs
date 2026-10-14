using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Reflection;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed class ListUpgradeTests {
    private static readonly List<UpgradeContext> Calls = [];

    [Fact]
    public void DirectEndpointConvertsOncePerElementAndPreservesListOwnerFacts() {
        Calls.Clear();
        TestContext context = new(Rule(1, 3, nameof(ConvertPoint)));
        ListLayout old = Layout(1);
        ListObjectBinding current = Binding(3);
        FrozenListState<Point1> frozen = new([new(1), new(2), new(3), new(4)]);
        ObjectStateRecord source = new(new(41), old, frozen);
        ObjectStateRecord result = context.NormalizeList(source, current);
        Assert.Equal(source.Id, result.Id);
        Assert.Equal(current.ListLayout, result.Layout.List);
        Assert.Equal(frozen.Count, result.GetListState<Point3>().Count);
        Assert.Equal(new Point3[] { new(10), new(20), new(30), new(40) }, result.GetListState<Point3>().Elements.ToArray());
        Assert.Equal(new Point1[] { new(1), new(2), new(3), new(4) }, frozen.Elements.ToArray());
        Assert.Equal(4, Calls.Count);
        Assert.All(Calls, call => {
            Assert.Same(Calls[0], call);
            Assert.Equal(source.Id, call.ObjectId);
            Assert.Equal(source.Layout, call.SourceObjectLayout);
            Assert.Equal(current.CurrentLayout, call.TargetObjectLayout);
            Assert.Null(call.ArrayShape);
            Assert.Equal(frozen.Count, call.ListCount);
            Assert.Throws<InvalidOperationException>(() => call.SourceObjectSchema);
            Assert.Throws<InvalidOperationException>(() => call.TargetObjectSchema);
        });
        context.NormalizeList(new(new(42), old, frozen), current);
        Assert.NotSame(Calls[0], Calls[4]);
    }

    [Fact]
    public void EmptyListStillRequiresDirectRuleAndValidDependencies() {
        Calls.Clear();
        ObjectStateRecord empty = Source(1, []);
        Assert.Throws<InvalidDataException>(() => new TestContext().NormalizeList(empty, Binding(3)));
        TestContext unselected = new(Rule(1, 3, nameof(ConvertPoint))) { SelectRules = false };
        Assert.Throws<InvalidDataException>(() => unselected.NormalizeList(empty, Binding(3)));
        // Lists do not synthesize a 1 -> 3 path out of adjacent providers.
        Assert.Throws<InvalidDataException>(() => new TestContext(Rule(1, 2, nameof(OneToTwo)), Rule(2, 3, nameof(TwoToThree)))
            .NormalizeList(empty, Binding(3)));
        ObjectStateRecord upgraded = new TestContext(Rule(1, 3, nameof(ConvertPoint))).NormalizeList(empty, Binding(3));
        Assert.Empty(upgraded.GetListState<Point3>().Elements.ToArray());
        Assert.NotEqual(empty.Layout, upgraded.Layout);
        StateUpgradeDependency absent = new("missing", typeof(Rules), new("Point", 99), new("Point", 1));
        Assert.Throws<InvalidDataException>(() => new TestContext(Rule(1, 3, nameof(ConvertPoint), absent)).NormalizeList(empty, Binding(3)));
        Assert.Empty(Calls);
    }

    [Fact]
    public void SameLayoutReusesFrozenStateWithoutRequiringRules() {
        Calls.Clear();
        TestContext context = new() { SelectRules = false };
        FrozenListState<Point3> frozen = new([new(3)]);
        ObjectStateRecord source = new(new(9), Layout(3), frozen);
        ObjectStateRecord result = context.NormalizeList(source, Binding(3));
        Assert.Same(frozen, result.GetListState<Point3>());
        Assert.Empty(Calls);
    }

    [Fact]
    public void CachedPlanUsesEachInvocationCountAndObjectIdentity() {
        Calls.Clear();
        TestContext context = new(Rule(1, 3, nameof(ConvertPoint)));
        ListObjectBinding target = Binding(3);
        context.NormalizeList(Source(1, [new(5)]), target);
        UpgradeContext first = Assert.Single(Calls);
        Calls.Clear();
        ObjectStateRecord source = new(new(93), Layout(1), new FrozenListState<Point1>([new(2), new(3), new(4)]));
        ObjectStateRecord result = context.NormalizeList(source, target);
        Assert.Equal(3, result.GetListState<Point3>().Count);
        Assert.Equal(1, first.ListCount);
        Assert.Equal(3, Calls.Count);
        Assert.All(Calls, call => {
            Assert.NotSame(first, call);
            Assert.Equal(3, call.ListCount);
            Assert.Equal(source.Id, call.ObjectId);
        });
        Calls.Clear();
        Assert.Equal(0, context.NormalizeList(Source(1, []), target).GetListState<Point3>().Count);
        Assert.Empty(Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BindingRejectsCustomNormalizerThatChangesCountOrIdentity(bool changeIdentity) {
        ListLayout layout = Layout(3);
        StateValueBinding element = new(layout.ElementSlot, typeof(Point3), typeof(NoOps<Point3>), typeof(Point3), typeof(Projection<Point3>));
        ListObjectBinding binding = ListObjectBinding.Create(typeof(List<Point3>), layout, element,
            (_, source) => new(changeIdentity ? new(99) : source.Id, layout,
                new FrozenListState<Point3>(changeIdentity ? [new(1)] : [new(1), new(2)])));
        ObjectStateRecord prior = new(new(7), layout, new FrozenListState<Point3>([new(1)]));
        Assert.Throws<InvalidDataException>(() => binding.Normalize(prior));
        Assert.Equal(new Point3[] { new(1) }, prior.GetListState<Point3>().Elements.ToArray());
    }

    [Fact]
    public void WrongSourceKindNominalIdentityOrFrozenStateFailsBeforeCallbacks() {
        Calls.Clear();
        TestContext context = new(Rule(1, 3, nameof(ConvertPoint)));
        ListObjectBinding target = Binding(3);
        Assert.Throws<InvalidDataException>(() => context.NormalizeList(new(new(1), "value"), target));
        Assert.Throws<InvalidDataException>(() => context.NormalizeList(new(default, Layout(1), new FrozenListState<Point1>([])), target));
        Assert.Throws<InvalidDataException>(() => context.NormalizeList(new(new(1),
            new ListLayout(new(1, TypeTag.Int32)), new FrozenListState<int>([])), target));
        Assert.Throws<InvalidDataException>(() => context.NormalizeList(new(new(1), Layout(1), new FrozenListState<Point3>([])), target));
        Assert.Empty(Calls);
    }

    [Fact]
    public void CachedListPlanRechecksExactRequirementsBeforeCallbacksIncludingEmptyList() {
        Calls.Clear();
        TestContext context = new(Rule(1, 3, nameof(ConvertPoint)));
        context.NormalizeList(Source(1, [new(5)]), Binding(3));
        Calls.Clear();
        context.Registered[(TypeExpr.Named("Point"), 1)] = new("Point", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.UInt32));
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => context.NormalizeList(Source(1, []), Binding(3)));
        Assert.Contains("list upgrade.source.element", error.Message);
        Assert.IsType<SchemaConflictException>(error.InnerException);
        Assert.Empty(Calls);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 1)]
    [InlineData(0, 3)]
    [InlineData(2, 3)]
    public void CachedListPlanRejectsLateNestedInlineConflictAlthoughOwnerSchemasStillMatch(int count, int conflictingVersion) {
        Calls.Clear();
        TestContext context = new(EnvelopeRule(nameof(ConvertEnvelope)), Rule(1, 3, nameof(ConvertPoint)));
        ListObjectBinding target = EnvelopeBinding();
        context.Registered[(TypeExpr.Named("Envelope"), 1)] = EnvelopeSchema(1);
        context.Registered[(TypeExpr.Named("Envelope"), 3)] = EnvelopeSchema(3);
        ObjectStateRecord warm = context.NormalizeList(EnvelopeSource(1), target);
        Assert.Equal(new Envelope3(new(50)), warm.GetListState<Envelope3>()[0]);
        Assert.Equal(2, Calls.Count);
        Calls.Clear();

        // The list element's own exact Schema remains unchanged. A later registration
        // conflicts only with a nested inline requirement of the already cached plan.
        context.Registered[(TypeExpr.Named("Point"), conflictingVersion)] =
            new("Point", conflictingVersion, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.UInt32));
        ObjectStateRecord source = EnvelopeSource(count);
        Assert.Equal(source.Layout.List!.ElementSlot.InlineSchema, context.Registered[(TypeExpr.Named("Envelope"), 1)]);
        Assert.Equal(target.ListLayout.ElementSlot.InlineSchema, context.Registered[(TypeExpr.Named("Envelope"), 3)]);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => context.NormalizeList(source, target));
        Assert.Contains("field[1].inline", error.Message);
        Assert.IsType<SchemaConflictException>(error.InnerException);
        Assert.Empty(Calls);
        Assert.All(source.GetListState<Envelope1>().Elements.ToArray(), item => Assert.Equal(new Point1(5), item.Value));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(2, true)]
    public void UnusedBrokenChildToolPreventsEveryListCallback(int count, bool badGrandchildSelector) {
        Calls.Clear();
        StateUpgradeDependency nonexistent = new("never-used", typeof(Rules), new("Point", 99), new("Point", 1));
        StateValueUpgradeProvider child = badGrandchildSelector
            ? Rule(1, 3, nameof(ConvertPoint), nonexistent)
            : Rule(1, 3, nameof(TwoToThree)); // Nominally selected, but the input DTO signature is wrong.
        TestContext context = new(EnvelopeRule(nameof(IgnoreEnvelopeChild)), child);
        ObjectStateRecord source = EnvelopeSource(count);
        Assert.Throws<InvalidDataException>(() => context.NormalizeList(source, EnvelopeBinding()));
        Assert.Empty(Calls);
        Assert.All(source.GetListState<Envelope1>().Elements.ToArray(), item => Assert.Equal(new Point1(5), item.Value));
    }

    [Fact]
    public void NestedValueToolReceivesListFactsAndLocalToolScope() {
        Calls.Clear();
        StateUpgradeDependency dependency = new("x", typeof(Rules), new("Point", 1), new("Point", 1));
        StateValueUpgradeProvider scalar = new(TypeExpr.Builtin(TypeTag.Int32), null, TypeExpr.Builtin(TypeTag.Int64), null, Method(nameof(ConvertNumber)));
        TestContext context = new(Rule(1, 3, nameof(ConvertWithTool), dependency), scalar);
        ObjectStateRecord result = context.NormalizeList(Source(1, [new(3), new(4)]), Binding(3));
        Assert.Equal(new Point3[] { new(103), new(104) }, result.GetListState<Point3>().Elements.ToArray());
        Assert.Equal(4, Calls.Count);
        Assert.Same(Calls[0], Calls[2]);
        Assert.Same(Calls[1], Calls[3]);
        Assert.NotSame(Calls[0], Calls[1]);
        Assert.All(Calls, call => {
            Assert.Equal(ObjectStateKind.List, call.SourceObjectLayout.Kind);
            Assert.Equal(ObjectStateKind.List, call.TargetObjectLayout.Kind);
            Assert.Equal(result.Id, call.ObjectId);
            Assert.Equal(2, call.ListCount);
            Assert.Null(call.ArrayShape);
        });
    }

    [Fact]
    public void AmbiguousOrBrokenProviderFailsWithoutFallbackOrAnyCallback() {
        Calls.Clear();
        StateValueUpgradeProvider first = Rule(1, 3, nameof(ConvertPoint));
        StateValueUpgradeProvider other = Rule(1, 3, nameof(ConvertPointOther));
        Assert.Throws<InvalidDataException>(() => new TestContext(first, other).NormalizeList(Source(1, [new(1)]), Binding(3)));
        Assert.Throws<InvalidDataException>(() => new TestContext(Rule(1, 3, nameof(TwoToThree)))
            .NormalizeList(Source(1, [new(1)]), Binding(3)));
        Assert.Empty(Calls);
    }

    [Fact]
    public void CallbackFailureLeavesSourceUnmodified() {
        Calls.Clear();
        ObjectStateRecord source = Source(1, [new(1), new(2)]);
        Assert.Throws<ArithmeticException>(() => new TestContext(Rule(1, 3, nameof(Throwing))).NormalizeList(source, Binding(3)));
        Assert.Equal(new Point1[] { new(1), new(2) }, source.GetListState<Point1>().Elements.ToArray());
        Assert.Single(Calls);
    }

    [Fact]
    public void ListValueRulePatternsCanMatchReferenceSlotsWithoutInlineVersion() {
        TypeExpr pattern = TypeExpr.List(TypeExpr.Parameter(0));
        _ = new StateValueUpgradeProvider(pattern, null, pattern, null, Method(nameof(CopyId)));
        Assert.Throws<ArgumentException>(() => new StateValueUpgradeProvider(pattern, 1, pattern, null, Method(nameof(CopyId))));
    }

    private static ObjectStateRecord Source(int version, Point1[] values) => new(new(7), Layout(version), new FrozenListState<Point1>(values));
    private static ListLayout Layout(int version) => new(new(1, TypeTag.InlineValue, inlineSchema: Schema(version)));
    private static DurableSchema Schema(int version) => new("Point", version, SchemaKind.InlineValue,
        new DurableFieldInfo(1, version == 1 ? TypeTag.Int32 : TypeTag.Int64));
    private static ListObjectBinding Binding(int version) {
        Type domain = typeof(List<Point3>);
        ListLayout layout = Layout(version);
        return ListObjectBinding.Create(domain, layout, new(layout.ElementSlot, typeof(Point3), typeof(NoOps<Point3>), typeof(Point3), typeof(Projection<Point3>)));
    }
    private static StateValueUpgradeProvider Rule(int from, int to, string method, params StateUpgradeDependency[] dependencies) =>
        new(TypeExpr.Named("Point"), from, TypeExpr.Named("Point"), to, Method(method), dependencies);
    private static DurableSchema EnvelopeSchema(int version) => new("Envelope", version, SchemaKind.InlineValue,
        new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: Schema(version)));
    private static StateValueUpgradeProvider EnvelopeRule(string method) => new(TypeExpr.Named("Envelope"), 1, TypeExpr.Named("Envelope"), 3,
        Method(method), [new("value", typeof(Rules), new("Envelope", 1), new("Envelope", 1))]);
    private static ObjectStateRecord EnvelopeSource(int count) => new(new(71),
        new ListLayout(new(1, TypeTag.InlineValue, inlineSchema: EnvelopeSchema(1))),
        new FrozenListState<Envelope1>(Enumerable.Repeat(new Envelope1(new(5)), count).ToArray()));
    private static ListObjectBinding EnvelopeBinding() {
        ListLayout layout = new(new(1, TypeTag.InlineValue, inlineSchema: EnvelopeSchema(3)));
        return ListObjectBinding.Create(typeof(List<Envelope3>), layout,
            new(layout.ElementSlot, typeof(Envelope3), typeof(NoOps<Envelope3>), typeof(Envelope3), typeof(Projection<Envelope3>)));
    }
    private static MethodInfo Method(string method) => typeof(ListUpgradeTests).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!;

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
        public override Type? ListElementUpgradeRuleSet => SelectRules ? typeof(Rules) : null;
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
