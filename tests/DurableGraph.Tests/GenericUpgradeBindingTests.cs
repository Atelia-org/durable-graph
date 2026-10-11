using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Reflection;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed class GenericUpgradeBindingTests {
    private static readonly List<UpgradeContext> Invocations = [];
    private static readonly TypeExpr Parameter = TypeExpr.Parameter(0);

    [Fact]
    public void WholeChainPlansAreReusableButObjectAndAdjacentContextAreFresh() {
        Invocations.Clear();
        var context = BoxContext(Provider(nameof(First), 1), Provider(nameof(Second), 2));
        DurableSchema source = BoxSchema(TypeExpr.Builtin(TypeTag.Int32), 1, new(1, TypeTag.Int32));
        DurableSchema target = BoxSchema(TypeExpr.Builtin(TypeTag.Int32), 3, new(1, TypeTag.Int32));
        Assert.Equal(new Box3<int>(8, 2), context.Normalize<Box3<int>>(new(new ObjectId(11), source, new Box1<int>(8)), target));
        Assert.Equal(new Box3<int>(9, 2), context.Normalize<Box3<int>>(new(new ObjectId(22), source, new Box1<int>(9)), target));
        Assert.Equal(new uint[] { 11, 11, 22, 22 }, Invocations.Select(item => item.ObjectId.Value));
        Assert.Equal(new[] { 1, 2, 1, 2 }, Invocations.Select(item => item.SourceObjectSchema.Version));
        Assert.Equal(new[] { 2, 3, 2, 3 }, Invocations.Select(item => item.TargetObjectSchema.Version));
        Assert.Equal(4, Invocations.Distinct(ReferenceEqualityComparer.Instance).Count());
    }

    [Fact]
    public void CachedPlanRejectsWrongRequestedCurrentDtoBeforeCallbacks() {
        Invocations.Clear();
        var context = BoxContext(Provider(nameof(First), 1), Provider(nameof(Second), 2));
        DurableSchema source = BoxSchema(TypeExpr.Builtin(TypeTag.Int32), 1, new(1, TypeTag.Int32));
        DurableSchema target = BoxSchema(TypeExpr.Builtin(TypeTag.Int32), 3, new(1, TypeTag.Int32));
        Assert.Equal(2, context.Normalize<Box3<int>>(new(new ObjectId(11), source, new Box1<int>(8)), target).Generation);
        Invocations.Clear();

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            context.Normalize<Box3<uint>>(new(new ObjectId(22), source, new Box1<int>(8)), target));

        Assert.Contains("requested current DTO type", error.Message);
        Assert.Empty(Invocations);
    }

    [Fact]
    public void MissingLaterEdgeAndUnsatisfiedMethodConstraintFailBeforeFirstCallback() {
        Invocations.Clear();
        DurableSchema source = BoxSchema(TypeExpr.Builtin(TypeTag.Int32), 1, new(1, TypeTag.Int32));
        DurableSchema current = BoxSchema(TypeExpr.Builtin(TypeTag.Int32), 3, new(1, TypeTag.Int32));
        var missing = BoxContext(Provider(nameof(First), 1));
        Assert.Throws<InvalidDataException>(() => missing.Normalize<Box3<int>>(new(new ObjectId(1), source, new Box1<int>(3)), current));
        Assert.Empty(Invocations);
        var constrained = BoxContext(Provider(nameof(Constrained), 1), Provider(nameof(Second), 2));
        Assert.Throws<InvalidDataException>(() => constrained.Normalize<Box3<int>>(new(new ObjectId(1), source, new Box1<int>(3)), current));
        Assert.Empty(Invocations);
    }

    [Fact]
    public void ClosedProviderSelectsHistoricalInlineVersionsWithoutCurrentDomainTypes() {
        Invocations.Clear();
        TypeExpr point = TypeExpr.Named("Point");
        TypeExpr owner = TypeExpr.Named("Box", point);
        var context = BoxContext(Provider(nameof(First), 1), Provider(nameof(ClosedPoint), 1, owner));
        DurableSchema oldPoint = PointSchema(1), newPoint = PointSchema(2);
        DurableSchema source = BoxSchema(point, 1, new(1, TypeTag.InlineValue, inlineSchema: oldPoint));
        DurableSchema target = BoxSchema(point, 2, new(1, TypeTag.InlineValue, inlineSchema: newPoint));
        var value = context.Normalize<Box2<Point2>>(new(new ObjectId(7), source, new Box1<Point1>(new(3))), target);
        Assert.Equal(new Box2<Point2>(new(30), 7), value);
        Assert.Single(Invocations);
        Assert.Equal(oldPoint, Invocations[0].SourceObjectSchema.Fields[0].InlineSchema);
        Assert.Equal(newPoint, Invocations[0].TargetObjectSchema.Fields[0].InlineSchema);
    }

    [Fact]
    public void ClosedProviderMismatchAndBusinessFailureNeverFallBackToGeneric() {
        Invocations.Clear();
        TypeExpr point = TypeExpr.Named("Point"), owner = TypeExpr.Named("Box", point);
        DurableSchema source = BoxSchema(point, 1, new(1, TypeTag.InlineValue, inlineSchema: PointSchema(2)));
        DurableSchema target = BoxSchema(point, 2, new(1, TypeTag.InlineValue, inlineSchema: PointSchema(2)));
        var wrong = BoxContext(Provider(nameof(First), 1), Provider(nameof(ClosedPoint), 1, owner));
        Assert.Throws<InvalidDataException>(() => wrong.Normalize<Box2<Point2>>(new(new ObjectId(1), source, new Box1<Point2>(new(4))), target));
        Assert.Empty(Invocations);
        var fails = BoxContext(Provider(nameof(First), 1), Provider(nameof(ThrowingPoint), 1, owner));
        source = BoxSchema(point, 1, new(1, TypeTag.InlineValue, inlineSchema: PointSchema(1)));
        Assert.Throws<ArithmeticException>(() => fails.Normalize<Box2<Point2>>(new(new ObjectId(1), source, new Box1<Point1>(new(4))), target));
        Assert.Empty(Invocations);
    }

    [Fact]
    public void ConflictingProvidersRejectAndIdenticalCapabilityRegistrationIsIdempotent() {
        Invocations.Clear();
        DurableSchema source = BoxSchema(TypeExpr.Builtin(TypeTag.Int32), 1, new(1, TypeTag.Int32));
        DurableSchema target = BoxSchema(TypeExpr.Builtin(TypeTag.Int32), 2, new(1, TypeTag.Int32));
        var duplicate = BoxContext(Provider(nameof(First), 1), Provider(nameof(First), 1));
        Assert.Equal(4, duplicate.Normalize<Box2<int>>(new(new ObjectId(1), source, new Box1<int>(4)), target).Value);
        Invocations.Clear();
        Assert.Throws<ArgumentException>(() => BoxContext(Provider(nameof(First), 1), Provider(nameof(OtherFirst), 1)));
        Assert.Empty(Invocations);
    }

    [Fact]
    public void PhantomMiddleRequiresExplicitSelectionAndRegisteredConflictPrecedesCallbacks() {
        Invocations.Clear();
        TypeExpr owner = TypeExpr.Named("Phantom", TypeExpr.Named("Point"));
        StateSchemaTemplate[] templates = [
            new("Phantom", 1, SchemaKind.ReferenceObject, 1, [], stateTypeDefinition: typeof(Phantom1)),
            new("Phantom", 2, SchemaKind.ReferenceObject, 1, [new(1, Parameter)],
                stateTypeDefinition: typeof(Phantom2<>), stateParameters: [new(Parameter)]),
            new("Phantom", 3, SchemaKind.ReferenceObject, 1, [new(1, Parameter)],
                stateTypeDefinition: typeof(Phantom3<>), stateParameters: [new(Parameter)]),
        ];
        StateUpgradeProvider first = new("Phantom", 1, Method(nameof(PhantomFirst)));
        StateUpgradeProvider last = new("Phantom", 2, Method(nameof(PhantomLast)), owner);
        DurableSchema source = new(owner, 1), target = new(owner, 3, new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: PointSchema(2)));
        var missing = new TestContext(new("Phantom", SchemaKind.ReferenceObject, 1, null, templates, upgrades: [first, last]), PointDefinition());
        Assert.Throws<InvalidDataException>(() => missing.Normalize<Phantom3<Point2>>(new(new ObjectId(4), source, new Phantom1()), target));
        Assert.Empty(Invocations);

        StateUpgradeProvider selected = new("Phantom", 1, Method(nameof(PhantomFirstClosed)), owner);
        var explicitContext = new TestContext(new("Phantom", SchemaKind.ReferenceObject, 1, null, templates, upgrades: [first, selected, last]), PointDefinition());
        Assert.Equal(new Point2(50), explicitContext.Normalize<Phantom3<Point2>>(new(new ObjectId(4), source, new Phantom1()), target).Value);
        Assert.Equal(2, Invocations.Count);
        Invocations.Clear();
        // The same cached plan is no longer acceptable if the repository later registers
        // a different exact middle layout under that key.
        explicitContext.Registered[(owner, 2)] = new(owner, 2, new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: PointSchema(2)));
        InvalidDataException ownerConflict = Assert.Throws<InvalidDataException>(() =>
            explicitContext.Normalize<Phantom3<Point2>>(new(new ObjectId(8), source, new Phantom1()), target));
        Assert.Contains("Schema requirement", ownerConflict.Message);
        Assert.Contains("step[0]", ownerConflict.Message);
        Assert.Contains("target", ownerConflict.Message);
        Assert.Contains("Phantom<Point>", ownerConflict.Message);
        Assert.Contains("v2", ownerConflict.Message);
        Assert.Empty(Invocations);
        explicitContext.Registered.Remove((owner, 2));
        explicitContext.Registered[(TypeExpr.Named("Point"), 1)] = new("Point", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int64));
        InvalidDataException inlineConflict = Assert.Throws<InvalidDataException>(() =>
            explicitContext.Normalize<Phantom3<Point2>>(new(new ObjectId(9), source, new Phantom1()), target));
        Assert.Contains("Schema requirement", inlineConflict.Message);
        Assert.Contains("step[0]", inlineConflict.Message);
        Assert.Contains("target", inlineConflict.Message);
        Assert.Contains("field[1]", inlineConflict.Message);
        Assert.Contains("Point", inlineConflict.Message);
        Assert.Contains("v1", inlineConflict.Message);
        SchemaConflictException layoutConflict = Assert.IsType<SchemaConflictException>(inlineConflict.InnerException);
        Assert.Equal(new DurableFieldInfo(1, TypeTag.Int64), Assert.Single(layoutConflict.RegisteredSchema.Fields));
        Assert.Equal(new DurableFieldInfo(1, TypeTag.Int32), Assert.Single(layoutConflict.ConflictingSchema.Fields));
        Assert.Empty(Invocations);
    }

    [Fact]
    public void NominalOperandsRetainStringAndDurableSemanticsEvenWhenBothUseObjectId() {
        var context = BoxContext();
        context.Definitions.Add("Node", new("Node", SchemaKind.ReferenceObject, 0, null, [new("Node", 1, SchemaKind.ReferenceObject, 0, [])]));
        DurableSchema text = context.InferSchemaFromState(TypeExpr.Named("Box", TypeExpr.Builtin(TypeTag.String)), 1, typeof(Box1<ObjectId>));
        DurableSchema reference = context.InferSchemaFromState(TypeExpr.Named("Box", TypeExpr.Named("Node")), 1, typeof(Box1<ObjectId>));
        Assert.Equal(TypeTag.String, text.Fields[0].TypeTag);
        Assert.Equal(TypeExpr.Named("Node"), reference.Fields[0].TargetType);
        Assert.NotEqual(text, reference);
        DurableSchema number = context.InferSchemaFromState(TypeExpr.Named("Box", TypeExpr.Builtin(TypeTag.UInt32)), 1, typeof(Box1<uint>));
        Assert.Equal(TypeTag.UInt32, number.Fields[0].TypeTag);
        Assert.Throws<InvalidDataException>(() => context.InferSchemaFromState(
            TypeExpr.Named("Box", TypeExpr.Builtin(TypeTag.String)), 1, typeof(Box1<uint>)));
        Assert.Throws<InvalidDataException>(() => context.InferSchemaFromState(
            TypeExpr.Named("Box", TypeExpr.Builtin(TypeTag.UInt32)), 1, typeof(Box1<ObjectId>)));
    }

    [Fact]
    public void PhantomBuiltinIdentityProvidesAnUnambiguousMiddleOperand() {
        Invocations.Clear();
        TypeExpr owner = TypeExpr.Named("Phantom", TypeExpr.Builtin(TypeTag.Int32));
        StateSchemaTemplate[] templates = [
            new("Phantom", 1, SchemaKind.ReferenceObject, 1, [], stateTypeDefinition: typeof(Phantom1)),
            new("Phantom", 2, SchemaKind.ReferenceObject, 1, [new(1, Parameter)],
                stateTypeDefinition: typeof(Phantom2<>), stateParameters: [new(Parameter)]),
            new("Phantom", 3, SchemaKind.ReferenceObject, 1, [new(1, Parameter)],
                stateTypeDefinition: typeof(Phantom3<>), stateParameters: [new(Parameter)]),
        ];
        var context = new TestContext(new StateDefinitionBinding("Phantom", SchemaKind.ReferenceObject, 1, null, templates, upgrades: [
            new("Phantom", 1, Method(nameof(PhantomFirst))), new("Phantom", 2, Method(nameof(PhantomGenericLast))),
        ]));
        DurableSchema source = new(owner, 1), target = new(owner, 3, new DurableFieldInfo(1, TypeTag.Int32));
        Assert.Equal(0, context.Normalize<Phantom3<int>>(new(new ObjectId(2), source, new Phantom1()), target).Value);
        Assert.Equal(2, Invocations.Count);
    }

    [Fact]
    public void IntermediateInferencePropagatesPriorParameterIntoNewNestedValueExpression() {
        Invocations.Clear();
        TypeExpr pairPattern = TypeExpr.Named("Pair", Parameter);
        StateSchemaTemplate[] templates = [
            new("Box", 1, SchemaKind.ReferenceObject, 1, [new(1, Parameter)],
                stateTypeDefinition: typeof(Box1<>), stateParameters: [new(Parameter)]),
            new("Box", 2, SchemaKind.ReferenceObject, 1, [new(1, pairPattern, 1), new(2, TypeExpr.Builtin(TypeTag.Int32))],
                stateTypeDefinition: typeof(Box2<>), stateParameters: [new(pairPattern, 1)]),
            new("Box", 3, SchemaKind.ReferenceObject, 1, [new(1, pairPattern, 1), new(2, TypeExpr.Builtin(TypeTag.Int32))],
                stateTypeDefinition: typeof(Box3<>), stateParameters: [new(pairPattern, 1)]),
        ];
        StateDefinitionBinding pair = new("Pair", SchemaKind.InlineValue, 1, null, [
            new("Pair", 1, SchemaKind.InlineValue, 1, [new(1, Parameter)],
                stateTypeDefinition: typeof(Pair1<>), stateParameters: [new(Parameter)]),
        ]);
        var context = new TestContext(new("Box", SchemaKind.ReferenceObject, 1, null, templates,
            upgrades: [Provider(nameof(IndependentOutput), 1), Provider(nameof(Second), 2)]), pair, PointDefinition());
        TypeExpr point = TypeExpr.Named("Point"), owner = TypeExpr.Named("Box", point);
        DurableSchema source = new(owner, 1, new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: PointSchema(1)));
        DurableSchema pairSchema = new(TypeExpr.Named("Pair", point), 1, SchemaKind.InlineValue,
            new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: PointSchema(1)));
        DurableSchema current = new(owner, 3, new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: pairSchema), new DurableFieldInfo(2, TypeTag.Int32));
        var result = context.Normalize<Box3<Pair1<Point1>>>(new(new ObjectId(5), source, new Box1<Point1>(new(8))), current);
        Assert.Equal(2, Invocations.Count);
        Assert.Equal(2, result.Generation);
        Assert.Equal(pairSchema, Invocations[0].TargetObjectSchema.Fields[0].InlineSchema);
    }

    private static StateUpgradeProvider Provider(string method, int version, TypeExpr? owner = null) => new("Box", version, Method(method), owner);
    private static MethodInfo Method(string name) => typeof(GenericUpgradeBindingTests).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;

    private static TestContext BoxContext(params StateUpgradeProvider[] upgrades) {
        StateSchemaTemplate[] templates = Enumerable.Range(1, 3).Select(version => new StateSchemaTemplate(
            "Box", version, SchemaKind.ReferenceObject, 1,
            version == 1 ? [new(1, Parameter)] : [new(1, Parameter), new(2, TypeExpr.Builtin(TypeTag.Int32))],
            stateTypeDefinition: version == 1 ? typeof(Box1<>) : version == 2 ? typeof(Box2<>) : typeof(Box3<>),
            stateParameters: [new(Parameter)])).ToArray();
        return new(new("Box", SchemaKind.ReferenceObject, 1, null, templates, upgrades: upgrades), PointDefinition());
    }

    private static StateDefinitionBinding PointDefinition() => new("Point", SchemaKind.InlineValue, 0, null, [
        new("Point", 1, SchemaKind.InlineValue, 0, [new(1, TypeExpr.Builtin(TypeTag.Int32))], stateTypeDefinition: typeof(Point1)),
        new("Point", 2, SchemaKind.InlineValue, 0, [new(1, TypeExpr.Builtin(TypeTag.Int64))], stateTypeDefinition: typeof(Point2)),
    ]);
    private static DurableSchema PointSchema(int version) => new("Point", version, SchemaKind.InlineValue,
        new DurableFieldInfo(1, version == 1 ? TypeTag.Int32 : TypeTag.Int64));
    private static DurableSchema BoxSchema(TypeExpr argument, int version, DurableFieldInfo value) =>
        new(TypeExpr.Named("Box", argument), version, version == 1 ? [value] : [value, new(2, TypeTag.Int32)]);

    private static void First<T>(in Box1<T> prior, out Box2<T> next, UpgradeContext context) where T : unmanaged {
        Invocations.Add(context); next = new(prior.Value, 1);
    }
    private static void OtherFirst<T>(in Box1<T> prior, out Box2<T> next, UpgradeContext context) where T : unmanaged {
        Invocations.Add(context); next = new(prior.Value, 9);
    }
    private static void Second<T>(in Box2<T> prior, out Box3<T> next, UpgradeContext context) where T : unmanaged {
        Invocations.Add(context); next = new(prior.Value, prior.Generation + 1);
    }
    private static void Constrained<T>(in Box1<T> prior, out Box2<T> next, UpgradeContext context) where T : unmanaged, IMarker {
        Invocations.Add(context); next = new(prior.Value, 1);
    }
    private static void ClosedPoint(in Box1<Point1> prior, out Box2<Point2> next, UpgradeContext context) {
        Invocations.Add(context); next = new(new(prior.Value.X * 10), 7);
    }
    private static void ThrowingPoint(in Box1<Point1> prior, out Box2<Point2> next, UpgradeContext context) => throw new ArithmeticException();
    private static void PhantomFirst<T>(in Phantom1 prior, out Phantom2<T> next, UpgradeContext context) where T : unmanaged {
        Invocations.Add(context); next = new(default);
    }
    private static void PhantomFirstClosed(in Phantom1 prior, out Phantom2<Point1> next, UpgradeContext context) {
        Invocations.Add(context); next = new(new(5));
    }
    private static void PhantomLast(in Phantom2<Point1> prior, out Phantom3<Point2> next, UpgradeContext context) {
        Invocations.Add(context); next = new(new(prior.Value.X * 10));
    }
    private static void PhantomGenericLast<T>(in Phantom2<T> prior, out Phantom3<T> next, UpgradeContext context) where T : unmanaged {
        Invocations.Add(context); next = new(prior.Value);
    }
    private static void IndependentOutput<TPrior, TNext>(in Box1<TPrior> prior, out Box2<TNext> next, UpgradeContext context)
        where TPrior : unmanaged where TNext : unmanaged {
        Invocations.Add(context); next = new(default, 1);
    }

    private interface IMarker;
    private readonly record struct Point1(int X);
    private readonly record struct Point2(long X);
    private readonly record struct Box1<T>(T Value) where T : unmanaged;
    private readonly record struct Box2<T>(T Value, int Generation) where T : unmanaged;
    private readonly record struct Box3<T>(T Value, int Generation) where T : unmanaged;
    private readonly record struct Phantom1;
    private readonly record struct Phantom2<T>(T Value) where T : unmanaged;
    private readonly record struct Phantom3<T>(T Value) where T : unmanaged;
    private readonly record struct Pair1<T>(T Value) where T : unmanaged;

    private sealed class TestContext(params StateDefinitionBinding[] definitions) : StateBindingContext {
        internal readonly Dictionary<string, StateDefinitionBinding> Definitions = definitions.ToDictionary(item => item.DefinitionId);
        internal readonly Dictionary<(TypeExpr Type, int Version), DurableSchema> Registered = [];

        public override bool TryGetCurrentModel(Type domainType, out StateModelBinding? model) { model = null; return false; }
        public override StateValueBinding ResolveCurrentValue(Type domainType) => throw new NotSupportedException();
        public override TypeExpr GetTypeExpr(Type domainType) => throw new NotSupportedException();
        public override Type GetDomainType(TypeExpr type) => throw new NotSupportedException();
        public override StateDefinitionBinding GetDefinition(string definitionId) => Definitions[definitionId];
        public override bool TryGetSchema(TypeExpr type, int version, out DurableSchema? schema) => Registered.TryGetValue((type, version), out schema);
        public override StateValueBinding ResolveStoredValue(DurableFieldInfo slot) => BuiltinStateValues.TryBindStored(slot, out StateValueBinding binding)
            ? binding : new(slot, ResolveStateType(slot.InlineSchema!), typeof(NoOperations));
        public override StateReaderBinding ResolveReader(DurableSchema schema) {
            Type type = ResolveStateType(schema);
            MethodInfo method = typeof(TestContext).GetMethod(nameof(Reader), BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(type);
            return method.CreateDelegate<Func<DurableSchema, StateReaderBinding>>()(schema);
        }
        private Type ResolveStateType(DurableSchema schema) {
            StateSchemaTemplate template = GetTemplate(schema.SchemaId, schema.Version);
            StateSchemaBinding binding = BindSchema(schema);
            Type definition = template.StateTypeDefinition!;
            return template.StateParameters.IsEmpty ? definition : definition.MakeGenericType(template.StateParameters
                .Select(parameter => binding.GetValue(parameter.Expression, parameter.InlineVersion).StateType).ToArray());
        }
        private static StateReaderBinding Reader<T>(DurableSchema schema) where T : unmanaged => new StateReaderBinding<T>(schema,
            static (ref BareValueReader reader) => default,
            static (ref BareValueReader reader, in T prior) => prior,
            static (in T state, IStateReferenceVisitor visitor) => { });
        private sealed class NoOperations;
    }
}
