using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Reflection;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed class NullableUpgradeTests {
    private static readonly TypeExpr Point = TypeExpr.Named("Point");
    private static readonly List<string> Calls = [];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TemporalScalarExplicitOffsetPolicyComposesWithNullableLifting(bool present) {
        Calls.Clear();
        DurableFieldInfo before = DurableFieldInfo.Nullable(1, new(1, TypeTag.Int64));
        DurableFieldInfo after = DurableFieldInfo.Nullable(1, new(1, TypeTag.DateTimeOffset));
        DurableSchema sourceSchema = new("TemporalOwner", 1, before), targetSchema = new("TemporalOwner", 2, after);
        StateDefinitionBinding owner = new("TemporalOwner", SchemaKind.ReferenceObject, 0, null, [
            new("TemporalOwner", 1, SchemaKind.ReferenceObject, 0, [new(1, StateBindingContext.NominalType(before))],
                stateTypeDefinition: typeof(Owner1<NullableState<long>>)),
            new("TemporalOwner", 2, SchemaKind.ReferenceObject, 0, [new(1, StateBindingContext.NominalType(after))],
                stateTypeDefinition: typeof(Owner2<NullableState<DateTimeOffset>>)),
        ], upgrades: [new("TemporalOwner", 1, Method(nameof(UpgradeOwner)), dependencies: [
            new("value", typeof(Rules), new("TemporalOwner", 1), new("TemporalOwner", 1)),
        ])]);
        ObjectStateRecord source = new(new(1), sourceSchema,
            new Owner1<NullableState<long>>(present ? new(TimeSpan.TicksPerDay) : default));
        TestContext missing = new([], lift: true) { Extra = owner };
        Assert.Throws<InvalidDataException>(() => missing.Normalize<Owner2<NullableState<DateTimeOffset>>>(source, targetSchema));
        Assert.Empty(Calls);
        StateValueUpgradeProvider rule = new(TypeExpr.Builtin(TypeTag.Int64), null,
            TypeExpr.Builtin(TypeTag.DateTimeOffset), null, Method(nameof(ToTimestamp)));
        TestContext context = new([rule], lift: true) { Extra = owner };
        var result = context.Normalize<Owner2<NullableState<DateTimeOffset>>>(source, targetSchema);
        Assert.Equal(present, result.Value.HasValue);
        if (present) { Assert.True(new DateTimeOffset(TimeSpan.TicksPerDay, TimeSpan.FromHours(8)).EqualsExact(result.Value.Value)); }
        Assert.Equal(present ? new[] { "owner", "ticks" } : new[] { "owner" }, Calls);
    }

    private static void ToTimestamp(in long ticks, out DateTimeOffset timestamp, UpgradeContext context) {
        Calls.Add("ticks");
        timestamp = new DateTimeOffset(ticks, TimeSpan.FromHours(8)); // Explicit business choice: ticks are local clock at UTC+08.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BclScalarExplicitTickConversionComposesWithNullableLifting(bool present) {
        Calls.Clear();
        DurableFieldInfo before = DurableFieldInfo.Nullable(1, new(1, TypeTag.Int64));
        DurableFieldInfo after = DurableFieldInfo.Nullable(1, new(1, TypeTag.TimeSpan));
        DurableSchema sourceSchema = new("BclOwner", 1, before), targetSchema = new("BclOwner", 2, after);
        StateDefinitionBinding owner = new("BclOwner", SchemaKind.ReferenceObject, 0, null, [
            new("BclOwner", 1, SchemaKind.ReferenceObject, 0, [new(1, StateBindingContext.NominalType(before))],
                stateTypeDefinition: typeof(Owner1<NullableState<long>>)),
            new("BclOwner", 2, SchemaKind.ReferenceObject, 0, [new(1, StateBindingContext.NominalType(after))],
                stateTypeDefinition: typeof(Owner2<NullableState<TimeSpan>>)),
        ], upgrades: [new("BclOwner", 1, Method(nameof(UpgradeOwner)), dependencies: [
            new("value", typeof(Rules), new("BclOwner", 1), new("BclOwner", 1)),
        ])]);
        ObjectStateRecord source = new(new(1), sourceSchema,
            new Owner1<NullableState<long>>(present ? new(long.MinValue) : default));
        TestContext missing = new([], lift: true) { Extra = owner };
        Assert.Throws<InvalidDataException>(() => missing.Normalize<Owner2<NullableState<TimeSpan>>>(source, targetSchema));
        Assert.Empty(Calls);
        StateValueUpgradeProvider rule = new(TypeExpr.Builtin(TypeTag.Int64), null,
            TypeExpr.Builtin(TypeTag.TimeSpan), null, Method(nameof(ToDuration)));
        TestContext context = new([rule], lift: true) { Extra = owner };
        var result = context.Normalize<Owner2<NullableState<TimeSpan>>>(source, targetSchema);
        Assert.Equal(present, result.Value.HasValue);
        if (present) { Assert.Equal(TimeSpan.MinValue, result.Value.Value); }
        Assert.Equal(present ? new[] { "owner", "ticks" } : new[] { "owner" }, Calls);
    }

    private static void ToDuration(in long ticks, out TimeSpan duration, UpgradeContext context) {
        Calls.Add("ticks");
        duration = TimeSpan.FromTicks(ticks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LiftingPreservesPresenceAndOnlyPresentInvokesChild(bool present) {
        Calls.Clear();
        TestContext context = new([ChildRule()], lift: true);
        NullableState<Point1> value = present ? new(new Point1(7)) : default;
        var result = context.Normalize<Owner2<NullableState<Point2>>>(Source(value), Owner(2));
        Assert.Equal(present, result.Value.HasValue);
        if (present) { Assert.Equal(70, result.Value.Value.X); }
        Assert.Equal(present ? new[] { "owner", "child" } : new[] { "owner" }, Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbsentValueDoesNotHideMissingChildOrDisabledLifting(bool lift) {
        Calls.Clear();
        TestContext context = new(lift ? [] : [ChildRule()], lift);
        Assert.Throws<InvalidDataException>(() => context.Normalize<Owner2<NullableState<Point2>>>(Source(default), Owner(2)));
        Assert.Empty(Calls);
    }

    [Fact]
    public void ExplicitWrapperCandidatesUseChildVersionsAndPrecedeLifting() {
        Calls.Clear();
        TestContext context = new([ChildRule(), WrapperRule(1, 2, nameof(Wrapper12)), WrapperRule(2, 3, nameof(Wrapper23))], true);
        var result = context.Normalize<Owner2<NullableState<Point2>>>(Source(default), Owner(2));
        Assert.True(result.Value.HasValue);
        Assert.Equal(99, result.Value.Value.X);
        Assert.Equal(new[] { "owner", "wrapper" }, Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void BadExplicitWrapperCannotFallBackToValidChild(int problem) {
        Calls.Clear();
        StateValueUpgradeProvider selected = problem switch {
            0 => WrapperRule(1, 2, nameof(Child)),
            1 => new(TypeExpr.Nullable(Point), 1, TypeExpr.Nullable(Point), 2, Method(nameof(Wrapper12)), expectedSource: Slot(2)),
            2 => WrapperRule(1, 2, nameof(Wrapper12), new StateUpgradeDependency("bad", typeof(Rules), new("Point", 99), new("Point", 1))),
            _ => WrapperRule(1, 2, nameof(Wrapper12)),
        };
        StateValueUpgradeProvider[] providers = problem == 3
            ? [selected, WrapperRule(1, 2, nameof(WrapperOther)), ChildRule()] : [selected, ChildRule()];
        TestContext context = new(providers, true, keep: true);
        Assert.Throws<InvalidDataException>(() => context.Normalize<Owner2<NullableState<Point2>>>(Source(default), Owner(2)));
        Assert.Empty(Calls);
    }

    [Fact]
    public void WrapperDependenciesSelectChildDeclarationFields() {
        Calls.Clear();
        StateValueUpgradeProvider wrapper = WrapperRule(1, 2, nameof(WrapperWithDependency),
            new StateUpgradeDependency("x", typeof(Rules), new("Point", 1), new("Point", 1)));
        StateValueUpgradeProvider numeric = new(TypeExpr.Builtin(TypeTag.Int32), null, TypeExpr.Builtin(TypeTag.Int64), null, Method(nameof(Number)));
        TestContext context = new([wrapper, numeric]);
        var result = context.Normalize<Owner2<NullableState<Point2>>>(Source(new(new Point1(8))), Owner(2));
        Assert.Equal(108, result.Value.Value.X);
        Assert.Equal(new[] { "owner", "number" }, Calls);
    }

    [Fact]
    public void KeepExactPrecedesLiftingButDoesNotHideExplicitSameLayoutProvider() {
        Calls.Clear();
        DurableSchema target = new(Owner(1).Type, 2, [Slot(1)]);
        TestContext keep = new([new(Point, 1, Point, 1, Method(nameof(KeepChild)))], true, true);
        Assert.Equal(7, keep.Normalize<Owner2<NullableState<Point1>>>(Source(new(new Point1(7))), target).Value.Value.X);
        Assert.Equal(new[] { "owner" }, Calls);
        Calls.Clear();
        TestContext explicitWrapper = new([WrapperRule(1, 1, nameof(WrapperKeep))], true, true);
        Assert.Equal(8, explicitWrapper.Normalize<Owner2<NullableState<Point1>>>(Source(new(new Point1(7))), target).Value.Value.X);
        Assert.Equal(new[] { "owner", "wrapper" }, Calls);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public void EmptyAndAllAbsentCollectionsPrebindChildAndRecheckCachedRequirements(bool array, int count) {
        Calls.Clear();
        NullableState<Point1>[] values = new NullableState<Point1>[count];
        ObjectStateRecord source = CollectionSource(array, values);
        TestContext missing = new([], true);
        Assert.Throws<InvalidDataException>(() => NormalizeCollection(missing, array, source));
        Assert.Empty(Calls);
        TestContext context = new([ChildRule()], true);
        ObjectStateRecord result = NormalizeCollection(context, array, source);
        Assert.Equal(array ? ObjectStateKind.Array : ObjectStateKind.List, result.Kind);
        Assert.Empty(Calls);
        context.Registered[(Point, 1)] = new("Point", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.UInt32));
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => NormalizeCollection(context, array, source));
        Assert.IsType<SchemaConflictException>(error.InnerException);
        Assert.Empty(Calls);
    }

    [Fact]
    public void CachedOwnerPlanRechecksNullableChildBeforeAbsentCallback() {
        Calls.Clear();
        TestContext context = new([ChildRule()], true);
        context.Normalize<Owner2<NullableState<Point2>>>(Source(default), Owner(2));
        Calls.Clear();
        context.Registered[(Point, 1)] = new("Point", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.UInt32));
        Assert.Throws<InvalidDataException>(() => context.Normalize<Owner2<NullableState<Point2>>>(Source(default), Owner(2)));
        Assert.Empty(Calls);
    }

    [Fact]
    public void InferredIntermediateNullableLayoutRemainsALiveRequirement() {
        Calls.Clear();
        TestContext context = new([ChildRule(), new(Point, 2, Point, 3, Method(nameof(Child23)))], true);
        TypeExpr optionalPoint = TypeExpr.Nullable(Point);
        StateUpgradeDependency dependency = new("value", typeof(Rules), new("Fixed", 1), new("Fixed", 1));
        context.Extra = new("Fixed", SchemaKind.ReferenceObject, 0, null, [
            new("Fixed", 1, SchemaKind.ReferenceObject, 0, [new(1, optionalPoint, 1)], stateTypeDefinition: typeof(Fixed1)),
            new("Fixed", 2, SchemaKind.ReferenceObject, 0, [new(1, optionalPoint, 2)], stateTypeDefinition: typeof(Fixed2)),
            new("Fixed", 3, SchemaKind.ReferenceObject, 0, [new(1, optionalPoint, 3)], stateTypeDefinition: typeof(Fixed3)),
        ], upgrades: [new("Fixed", 1, Method(nameof(Fixed12)), dependencies: [dependency]),
            new("Fixed", 2, Method(nameof(Fixed23)), dependencies: [dependency])]);
        ObjectStateRecord source = new(new(1), new DurableSchema("Fixed", 1, Slot(1)), new Fixed1(default));
        DurableSchema target = new("Fixed", 3, Slot(3));
        Assert.False(context.Normalize<Fixed3>(source, target).Value.HasValue);
        Assert.Equal(new[] { "fixed12", "fixed23" }, Calls);
        Calls.Clear();
        context.Registered[(Point, 2)] = new("Point", 2, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.UInt64));
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => context.Normalize<Fixed3>(source, target));
        Assert.IsType<SchemaConflictException>(error.InnerException);
        Assert.Empty(Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactInferencePreservesNullableParameterSourceAndHistoricalVersion(bool declaredNullable) {
        TestContext context = new();
        TypeExpr parameter = TypeExpr.Parameter(0);
        TypeExpr expression = declaredNullable ? TypeExpr.Nullable(parameter) : parameter;
        TypeExpr argument = declaredNullable ? Point : TypeExpr.Nullable(Point);
        context.Extra = new("Optional", SchemaKind.ReferenceObject, 1, null, [
            new("Optional", 1, SchemaKind.ReferenceObject, 1, [new(1, expression)],
                stateTypeDefinition: typeof(Owner1<>), stateParameters: [new(expression)]),
        ]);
        DurableSchema schema = context.InferSchemaFromState(TypeExpr.Named("Optional", argument), 1, typeof(Owner1<NullableState<Point1>>));
        Assert.Equal(Slot(1), schema.Fields[0]);
        Assert.Equal(1, schema.Fields[0].ValueSchema!.Version);
        Assert.Throws<InvalidDataException>(() => context.InferSchemaFromState(TypeExpr.Named("Optional", argument), 1, typeof(Owner1<Point1>)));
    }

    private static DurableSchema PointSchema(int version) => new("Point", version, SchemaKind.InlineValue,
        new DurableFieldInfo(1, version == 1 ? TypeTag.Int32 : TypeTag.Int64));
    private static DurableFieldInfo Slot(int version) => DurableFieldInfo.Nullable(1, new(1, TypeTag.InlineValue, inlineSchema: PointSchema(version)));
    private static DurableSchema Owner(int version) => new(TypeExpr.Named("Owner", TypeExpr.Nullable(Point)), version, [Slot(version)]);
    private static ObjectStateRecord Source(NullableState<Point1> value) => new(new(1), Owner(1), new Owner1<NullableState<Point1>>(value));
    private static StateValueUpgradeProvider ChildRule() => new(Point, 1, Point, 2, Method(nameof(Child)));
    private static StateValueUpgradeProvider WrapperRule(int from, int to, string method, params StateUpgradeDependency[] dependencies) =>
        new(TypeExpr.Nullable(Point), from, TypeExpr.Nullable(Point), to, Method(method), dependencies);
    private static MethodInfo Method(string name) => typeof(NullableUpgradeTests).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
    private static ObjectStateRecord CollectionSource(bool array, NullableState<Point1>[] values) => array
        ? new(new(2), new ArrayLayout(TypeExprKind.VectorArray, Slot(1)), new FrozenArrayState<NullableState<Point1>>(new ArrayShape(values.Length), values))
        : new(new(2), new ListLayout(Slot(1)), new FrozenListState<NullableState<Point1>>(values));
    private static ObjectStateRecord NormalizeCollection(TestContext context, bool array, ObjectStateRecord source) {
        DurableFieldInfo slot = Slot(2);
        StateValueBinding value = new(slot, typeof(NullableState<Point2>), typeof(NoOps<NullableState<Point2>>),
            typeof(NullableState<Point2>), typeof(Projection<NullableState<Point2>>));
        return array
            ? context.NormalizeArray(source, ArrayObjectBinding.Create(typeof(NullableState<Point2>[]), new(TypeExprKind.VectorArray, slot), value))
            : context.NormalizeList(source, ListObjectBinding.Create(typeof(List<NullableState<Point2>>), new(slot), value));
    }
    private static void UpgradeOwner<A, B>(in Owner1<A> prior, out Owner2<B> next, UpgradeContext context) where A : unmanaged where B : unmanaged {
        Calls.Add("owner"); next = new(context.GetValueUpgrade<A, B>("value")(prior.Value));
    }
    private static void Child(in Point1 prior, out Point2 next, UpgradeContext context) { Calls.Add("child"); next = new(prior.X * 10); }
    private static void Child23(in Point2 prior, out Point3 next, UpgradeContext context) { Calls.Add("child23"); next = new(prior.X); }
    private static void Fixed12(in Fixed1 prior, out Fixed2 next, UpgradeContext context) {
        Calls.Add("fixed12"); next = new(context.GetValueUpgrade<NullableState<Point1>, NullableState<Point2>>("value")(prior.Value));
    }
    private static void Fixed23(in Fixed2 prior, out Fixed3 next, UpgradeContext context) {
        Calls.Add("fixed23"); next = new(context.GetValueUpgrade<NullableState<Point2>, NullableState<Point3>>("value")(prior.Value));
    }
    private static void KeepChild(in Point1 prior, out Point1 next, UpgradeContext context) { throw new InvalidOperationException("KeepExact must bypass child"); }
    private static void Wrapper12(in NullableState<Point1> prior, out NullableState<Point2> next, UpgradeContext context) { Calls.Add("wrapper"); next = new(new Point2(99)); }
    private static void WrapperOther(in NullableState<Point1> prior, out NullableState<Point2> next, UpgradeContext context) { next = default; }
    private static void Wrapper23(in NullableState<Point2> prior, out NullableState<Point3> next, UpgradeContext context) { next = default; }
    private static void WrapperKeep(in NullableState<Point1> prior, out NullableState<Point1> next, UpgradeContext context) { Calls.Add("wrapper"); next = new(new Point1(prior.Value.X + 1)); }
    private static void WrapperWithDependency(in NullableState<Point1> prior, out NullableState<Point2> next, UpgradeContext context) =>
        next = new(new Point2(context.GetValueUpgrade<int, long>("x")(prior.Value.X)));
    private static void Number(in int prior, out long next, UpgradeContext context) { Calls.Add("number"); next = prior + 100; }
    private sealed class Rules;
    private readonly record struct Point1(int X);
    private readonly record struct Point2(long X);
    private readonly record struct Point3(long X);
    private readonly record struct Owner1<T>(T Value) where T : unmanaged;
    private readonly record struct Owner2<T>(T Value) where T : unmanaged;
    private readonly record struct Fixed1(NullableState<Point1> Value);
    private readonly record struct Fixed2(NullableState<Point2> Value);
    private readonly record struct Fixed3(NullableState<Point3> Value);

    private sealed class TestContext(StateValueUpgradeProvider[]? providers = null, bool lift = false, bool keep = false) : StateBindingContext {
        internal StateDefinitionBinding? Extra;
        internal readonly Dictionary<(TypeExpr Type, int Version), DurableSchema> Registered = [];
        public override Type? ArrayElementUpgradeRuleSet => typeof(Rules);
        public override Type? ListElementUpgradeRuleSet => typeof(Rules);
        public override StateValueUpgradeRuleSet GetValueUpgradeRuleSet(Type ruleSet) => new(typeof(Rules), providers ?? [], keep, lift);
        public override StateDefinitionBinding GetDefinition(string id) {
            if (Extra?.DefinitionId == id) { return Extra; }
            if (id == "Owner") {
                TypeExpr parameter = TypeExpr.Parameter(0);
                return new("Owner", SchemaKind.ReferenceObject, 1, null, [
                    new("Owner", 1, SchemaKind.ReferenceObject, 1, [new(1, parameter)], stateTypeDefinition: typeof(Owner1<>), stateParameters: [new(parameter)]),
                    new("Owner", 2, SchemaKind.ReferenceObject, 1, [new(1, parameter)], stateTypeDefinition: typeof(Owner2<>), stateParameters: [new(parameter)]),
                ], upgrades: [new("Owner", 1, Method(nameof(UpgradeOwner)), dependencies: [new("value", typeof(Rules), new("Owner", 1), new("Owner", 1))])]);
            }
            return new("Point", SchemaKind.InlineValue, 0, null, Enumerable.Range(1, 3).Select(version => new StateSchemaTemplate(
                "Point", version, SchemaKind.InlineValue, 0, [new(1, TypeExpr.Builtin(version == 1 ? TypeTag.Int32 : TypeTag.Int64))],
                stateTypeDefinition: version == 1 ? typeof(Point1) : version == 2 ? typeof(Point2) : typeof(Point3))));
        }
        public override bool TryGetSchema(TypeExpr type, int version, out DurableSchema? schema) => Registered.TryGetValue((type, version), out schema);
        public override StateValueBinding ResolveStoredValue(DurableFieldInfo slot) {
            if (slot.NullableLayout is { } nullable) {
                Type state = typeof(NullableState<>).MakeGenericType(ResolveStoredValue(nullable.ElementSlot).StateType);
                return new(slot, state, typeof(NoOps<>).MakeGenericType(state));
            }
            if (BuiltinStateValues.TryBindStored(slot, out StateValueBinding builtin)) { return builtin; }
            return new(slot, StateType(slot.InlineSchema!), typeof(NoOps<Point1>));
        }
        private Type StateType(DurableSchema schema) {
            StateSchemaTemplate template = GetTemplate(schema.SchemaId, schema.Version);
            StateSchemaBinding binding = BindSchema(schema);
            return template.StateParameters.IsEmpty ? template.StateTypeDefinition! : template.StateTypeDefinition!.MakeGenericType(
                template.StateParameters.Select(parameter => binding.GetValue(parameter.Expression, parameter.InlineVersion).StateType).ToArray());
        }
        public override StateReaderBinding ResolveReader(DurableSchema schema) => typeof(TestContext).GetMethod(nameof(Reader), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(StateType(schema)).CreateDelegate<Func<DurableSchema, StateReaderBinding>>()(schema);
        private static StateReaderBinding Reader<T>(DurableSchema schema) where T : unmanaged => new StateReaderBinding<T>(schema,
            static (ref BareValueReader reader) => default, static (ref BareValueReader reader, in T prior) => prior,
            static (in T state, IStateReferenceVisitor visitor) => { });
        public override bool TryGetCurrentModel(Type domainType, out StateModelBinding? model) { model = null; return false; }
        public override StateValueBinding ResolveCurrentValue(Type domainType) => throw new NotSupportedException();
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
