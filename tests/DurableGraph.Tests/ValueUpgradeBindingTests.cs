using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Reflection;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed class ValueUpgradeBindingTests {
    private static readonly TypeExpr Parameter = TypeExpr.Parameter(0);
    private static readonly List<(string Provider, UpgradeContext Context)> Calls = [];

    [Fact]
    public void NestedGenericRulesUseLocalScopesAndFreshOwnerContextsAcrossObjectsAndEdges() {
        Calls.Clear();
        TestContext context = CreateContext(
            Owner(nameof(UpgradeBox), 1, Dependency("value", typeof(OuterRules), "Box")),
            Owner(nameof(UpgradeSecondBox), 2, Dependency("value", typeof(KeepRules), "Box")));
        context.AddRules(new(typeof(OuterRules), [InlineRule("Pair", 1, 2, nameof(UpgradePair),
            Dependency("value", typeof(CoordinateRules), "Pair"))]));
        context.AddRules(new(typeof(CoordinateRules), [InlineRule("Point", 1, 2, nameof(ScalePoint))]));
        context.AddRules(new(typeof(KeepRules), [], allowKeepExact: true));
        DurableSchema oldPair = PairSchema(1, PointSchema(1)), nextPair = PairSchema(2, PointSchema(2));
        DurableSchema source = BoxSchema(1, Inline(oldPair)), target = BoxSchema(3, Inline(nextPair));
        context.Registered[(source.Type, 2)] = BoxSchema(2, Inline(nextPair));
        foreach (uint id in new uint[] { 11, 22 }) {
            var result = context.Normalize<Box3<Pair2<Point2>>>(new(new ObjectId(id), source,
                new Box1<Pair1<Point1>>(new(new(3), new(4)))), target);
            Assert.Equal(new Pair2<Point2>(new(30), new(40)), result.Value);
        }
        Assert.Equal(new[] { "box", "pair", "point", "point", "second", "box", "pair", "point", "point", "second" }, Calls.Select(item => item.Provider));
        Assert.Equal(new uint[] { 11, 11, 11, 11, 11, 22, 22, 22, 22, 22 }, Calls.Select(item => item.Context.ObjectId.Value));
        Assert.All(Calls.Where(item => item.Provider != "second"), item => {
            Assert.Equal(1, item.Context.SourceObjectSchema.Version);
            Assert.Equal(2, item.Context.TargetObjectSchema.Version);
            Assert.Equal(source, item.Context.SourceObjectSchema);
        });
        Assert.All(Calls.Where(item => item.Provider == "second"), item => {
            Assert.Equal(2, item.Context.SourceObjectSchema.Version);
            Assert.Equal(3, item.Context.TargetObjectSchema.Version);
            Assert.Equal(target, item.Context.TargetObjectSchema);
        });
        Assert.NotSame(Calls[0].Context, Calls[5].Context);
        Assert.NotSame(Calls[1].Context, Calls[6].Context);
        Assert.NotSame(Calls[2].Context, Calls[7].Context);
    }

    [Fact]
    public void KeepExactRequiresExplicitOptInAndDoesNotOverrideAnExplicitSameSchemaRule() {
        Calls.Clear();
        DurableSchema source = BoxSchema(1, new(1, TypeTag.Int32)), target = BoxSchema(2, new(1, TypeTag.Int32));
        TestContext absent = CreateContext(Owner(nameof(UpgradeBox), 1, Dependency("value", typeof(KeepRules), "Box")));
        absent.AddRules(new(typeof(KeepRules), []));
        Assert.Throws<InvalidDataException>(() => absent.Normalize<Box2<int>>(new(new ObjectId(1), source, new Box1<int>(5)), target));
        Assert.Empty(Calls);

        TestContext identity = CreateContext(Owner(nameof(UpgradeBox), 1, Dependency("value", typeof(KeepRules), "Box")));
        identity.AddRules(new(typeof(KeepRules), [], allowKeepExact: true));
        Assert.Equal(5, identity.Normalize<Box2<int>>(new(new ObjectId(1), source, new Box1<int>(5)), target).Value);

        TestContext selected = CreateContext(Owner(nameof(UpgradeBox), 1, Dependency("value", typeof(KeepRules), "Box")));
        selected.AddRules(new(typeof(KeepRules), [BuiltinRule(TypeTag.Int32, TypeTag.Int32, nameof(AddOne))], allowKeepExact: true));
        Assert.Equal(6, selected.Normalize<Box2<int>>(new(new ObjectId(1), source, new Box1<int>(5)), target).Value);
        Assert.Contains(Calls, item => item.Provider == "add");
    }

    [Fact]
    public void MissingUnusedDependencyAndMissingLaterEdgeDependencyPrecedeEveryOwnerCallback() {
        Calls.Clear();
        StateUpgradeDependency dependency = Dependency("unused", typeof(KeepRules), "Box");
        TestContext unused = CreateContext(Owner(nameof(IgnoreBoxValue), 1, dependency));
        DurableSchema source = BoxSchema(1, new(1, TypeTag.Int32)), target = BoxSchema(2, new(1, TypeTag.Int32));
        Assert.Throws<InvalidDataException>(() => unused.Normalize<Box2<int>>(new(new ObjectId(1), source, new Box1<int>(3)), target));
        Assert.Empty(Calls);

        TestContext later = CreateContext(Owner(nameof(IgnoreBoxValue), 1), Owner(nameof(UpgradeSecondBox), 2, dependency));
        target = BoxSchema(3, new(1, TypeTag.Int32));
        Assert.Throws<InvalidDataException>(() => later.Normalize<Box3<int>>(new(new ObjectId(1), source, new Box1<int>(3)), target));
        Assert.Empty(Calls);

        TestContext nested = CreateContext(Owner(nameof(UpgradeBox), 1, Dependency("value", typeof(OuterRules), "Box")));
        nested.AddRules(new(typeof(OuterRules), [InlineRule("Pair", 2, 2, nameof(KeepPair),
            Dependency("value", typeof(CoordinateRules), "Pair"))], allowKeepExact: true));
        DurableFieldInfo pair = Inline(PairSchema(2, PointSchema(2)));
        Assert.Throws<InvalidDataException>(() => nested.Normalize<Box2<Pair2<Point2>>>(
            new(new ObjectId(1), BoxSchema(1, pair), new Box1<Pair2<Point2>>(default)), BoxSchema(2, pair)));
        Assert.Empty(Calls);
    }

    [Fact]
    public void WrongCompleteSchemaSignatureConstraintAndAmbiguityNeverBecomeKeepExact() {
        Calls.Clear();
        DurableSchema source = BoxSchema(1, Inline(PointSchema(2))), target = BoxSchema(2, Inline(PointSchema(2)));
        DurableFieldInfo malformed = Inline(new("Point", 2, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int32)));
        StateValueUpgradeProvider wrongSchema = new(TypeExpr.Named("Point"), 2, TypeExpr.Named("Point"), 2,
            Method(nameof(KeepPoint)), expectedSource: malformed);
        StateValueUpgradeProvider wrongSignature = InlineRule("Point", 2, 2, nameof(ScalePoint));
        foreach (StateValueUpgradeProvider provider in new[] { wrongSchema, wrongSignature }) {
            TestContext context = CreateContext(Owner(nameof(UpgradeBox), 1, Dependency("value", typeof(CoordinateRules), "Box")));
            context.AddRules(new(typeof(CoordinateRules), [provider], allowKeepExact: true));
            Assert.Throws<InvalidDataException>(() => context.Normalize<Box2<Point2>>(new(new ObjectId(1), source, new Box1<Point2>(new(3))), target));
            Assert.Empty(Calls);
        }

        TestContext constrained = CreateContext(Owner(nameof(UpgradeBox), 1, Dependency("value", typeof(KeepRules), "Box")));
        constrained.AddRules(new(typeof(KeepRules), [BuiltinRule(TypeTag.Int32, TypeTag.Int32, nameof(Constrained))], allowKeepExact: true));
        Assert.Throws<InvalidDataException>(() => constrained.Normalize<Box2<int>>(
            new(new ObjectId(1), BoxSchema(1, new(1, TypeTag.Int32)), new Box1<int>(3)), BoxSchema(2, new(1, TypeTag.Int32))));
        Assert.Empty(Calls);

        TestContext ambiguous = CreateContext(Owner(nameof(UpgradeBox), 1, Dependency("value", typeof(OuterRules), "Box")));
        StateValueUpgradeProvider open = InlineRule("Pair", 1, 2, nameof(UpgradePair), Dependency("value", typeof(CoordinateRules), "Pair"));
        TypeExpr closed = TypeExpr.Named("Pair", TypeExpr.Named("Point"));
        StateValueUpgradeProvider exact = new(closed, 1, closed, 2, Method(nameof(ClosedPair)));
        ambiguous.AddRules(new(typeof(OuterRules), [open, exact], allowKeepExact: true));
        Assert.Throws<InvalidDataException>(() => ambiguous.Normalize<Box2<Pair2<Point2>>>(new(new ObjectId(1), BoxSchema(1, Inline(PairSchema(1, PointSchema(1)))), new Box1<Pair1<Point1>>(default)),
            BoxSchema(2, Inline(PairSchema(2, PointSchema(2))))));
        Assert.Empty(Calls);
    }

    [Fact]
    public void ObjectIdRepresentationPreservesExplicitNumericStringAndReferenceConversions() {
        Calls.Clear();
        TypeExpr node = TypeExpr.Named("Node");
        DurableFieldInfo[] slots = [new(1, TypeTag.UInt32), new(1, TypeTag.String), DurableFieldInfo.Reference(1, node)];
        foreach (DurableFieldInfo prior in slots) {
            foreach (DurableFieldInfo next in slots.Where(slot => slot != prior)) {
                TestContext context = SemanticContext(prior, next);
                context.AddRules(new(typeof(KeepRules), [], allowKeepExact: true));
                ObjectStateRecord source = new(new(1), new DurableSchema("Semantic", 1, prior),
                    prior.TypeTag == TypeTag.UInt32 ? (object)new Box1<uint>(7) : new Box1<ObjectId>(new(7)));
                DurableSchema target = new("Semantic", 2, next);
                if (next.TypeTag == TypeTag.UInt32) {
                    Assert.Throws<InvalidDataException>(() => context.Normalize<Box2<uint>>(source, target));
                } else {
                    Assert.Throws<InvalidDataException>(() => context.Normalize<Box2<ObjectId>>(source, target));
                }
            }
        }
        Assert.Empty(Calls);
        TestContext explicitConversion = SemanticContext(new(1, TypeTag.String), new(1, TypeTag.UInt32));
        explicitConversion.AddRules(new(typeof(KeepRules), [BuiltinRule(TypeTag.String, TypeTag.UInt32, nameof(CopyId))]));
        Assert.Equal(7u, explicitConversion.Normalize<Box2<uint>>(
            new(new ObjectId(1), new DurableSchema("Semantic", 1, new DurableFieldInfo(1, TypeTag.String)), new Box1<ObjectId>(new(7))),
            new("Semantic", 2, new DurableFieldInfo(1, TypeTag.UInt32))).Value);
    }

    [Fact]
    public void SameStateTypesCanSelectDifferentBusinessKeysAndInheritedDeclarationSegments() {
        Calls.Clear();
        TestContext context = CreateContext();
        TypeExpr number = TypeExpr.Builtin(TypeTag.Int32);
        context.Definitions.Add("Base", new("Base", SchemaKind.ReferenceObject, 0, null, [
            new("Base", 1, SchemaKind.ReferenceObject, 0, [new(1, number)]),
            new("Base", 2, SchemaKind.ReferenceObject, 0, [new(1, number)]),
        ]));
        context.Definitions.Add("Derived", new("Derived", SchemaKind.ReferenceObject, 0, null, [
            new("Derived", 1, SchemaKind.ReferenceObject, 0, [new(1, number)], new(TypeExpr.Named("Base"), 1), typeof(Two1)),
            new("Derived", 2, SchemaKind.ReferenceObject, 0, [new(1, number)], new(TypeExpr.Named("Base"), 2), typeof(Two2)),
        ], upgrades: [new("Derived", 1, Method(nameof(UpgradeTwo)), dependencies: [
            Dependency("base", typeof(KeepRules), "Base"), Dependency("own", typeof(OtherRules), "Derived"),
        ])]));
        context.AddRules(new(typeof(KeepRules), [BuiltinRule(TypeTag.Int32, TypeTag.Int32, nameof(AddOne))]));
        context.AddRules(new(typeof(OtherRules), [BuiltinRule(TypeTag.Int32, TypeTag.Int32, nameof(TimesTen))]));
        DurableSchema source = new("Derived", 1, [new(1, TypeTag.Int32)], new("Base", 1, new DurableFieldInfo(1, TypeTag.Int32)));
        DurableSchema target = new("Derived", 2, [new(1, TypeTag.Int32)], new("Base", 2, new DurableFieldInfo(1, TypeTag.Int32)));
        Assert.Equal(new Two2(4, 40), context.Normalize<Two2>(new(new ObjectId(3), source, new Two1(3, 4)), target));

        TestContext wrong = CreateContext(Owner(nameof(UpgradeBox), 1, Dependency("value", typeof(KeepRules), "Absent")));
        wrong.AddRules(new(typeof(KeepRules), [], allowKeepExact: true));
        Calls.Clear();
        Assert.Throws<InvalidDataException>(() => wrong.Normalize<Box2<int>>(
            new(new ObjectId(1), BoxSchema(1, new(1, TypeTag.Int32)), new Box1<int>(1)), BoxSchema(2, new(1, TypeTag.Int32))));
        Assert.Empty(Calls);
    }

    [Fact]
    public void UndeclaredAndWrongTypedQueriesFailAtInvocationAndBusinessExceptionsDoNotRetry() {
        Calls.Clear();
        TestContext unknown = CreateContext(Owner(nameof(UnknownKey), 1));
        DurableSchema source = BoxSchema(1, new(1, TypeTag.Int32)), target = BoxSchema(2, new(1, TypeTag.Int32));
        Assert.Throws<InvalidOperationException>(() => unknown.Normalize<Box2<int>>(new(new ObjectId(1), source, new Box1<int>(5)), target));
        Assert.Single(Calls);
        Calls.Clear();
        TestContext wrongType = CreateContext(Owner(nameof(WrongTypedKey), 1, Dependency("value", typeof(KeepRules), "Box")));
        wrongType.AddRules(new(typeof(KeepRules), [], allowKeepExact: true));
        Assert.Throws<InvalidOperationException>(() => wrongType.Normalize<Box2<int>>(new(new ObjectId(1), source, new Box1<int>(5)), target));
        Assert.Single(Calls);
        Calls.Clear();
        TestContext throwing = CreateContext(Owner(nameof(UpgradeBox), 1, Dependency("value", typeof(KeepRules), "Box")));
        throwing.AddRules(new(typeof(KeepRules), [BuiltinRule(TypeTag.Int32, TypeTag.Int32, nameof(Throwing))], allowKeepExact: true));
        Assert.Throws<ArithmeticException>(() => throwing.Normalize<Box2<int>>(new(new ObjectId(1), source, new Box1<int>(5)), target));
        Assert.Equal(new[] { "box", "throw" }, Calls.Select(item => item.Provider));
    }

    [Fact]
    public void DependencyIntentParticipatesInCapabilityIdentityAndLegacyMethodsRejectTools() {
        StateUpgradeDependency dependency = Dependency("value", typeof(KeepRules), "Box");
        Assert.Throws<ArgumentException>(() => Owner(nameof(UpgradeBox), 1, dependency, dependency));
        Assert.Throws<ArgumentException>(() => new StateUpgradeProvider("Box", 1, Method(nameof(Legacy)),
            allowLegacyTwoParameter: true, dependencies: [dependency]));
        Assert.Throws<ArgumentException>(() => CreateContext(Owner(nameof(UpgradeBox), 1, dependency),
            Owner(nameof(UpgradeBox), 1, Dependency("value", typeof(OtherRules), "Box"))));
        StateValueUpgradeProvider one = InlineRule("Pair", 1, 2, nameof(UpgradePair), Dependency("value", typeof(KeepRules), "Pair"));
        StateValueUpgradeProvider two = InlineRule("Pair", 1, 2, nameof(UpgradePair), Dependency("value", typeof(OtherRules), "Pair"));
        Assert.Equal(2, new StateValueUpgradeRuleSet(typeof(OuterRules), [one, one, two]).Providers.Length);
    }

    [Fact]
    public void SharedDependencyPlansCreateOneToolPerInvocationInsteadOfExpandingTheDag() {
        Calls.Clear();
        TestContext context = CreateContext(Owner(nameof(UpgradeBox), 1, Dependency("value", typeof(OuterRules), "Box")));
        context.Definitions.Add("Nest", new("Nest", SchemaKind.InlineValue, 1, null, [
            new("Nest", 1, SchemaKind.InlineValue, 1, [new(1, Parameter)], stateTypeDefinition: typeof(Nest1<>), stateParameters: [new(Parameter)]),
            new("Nest", 2, SchemaKind.InlineValue, 1, [new(1, Parameter)], stateTypeDefinition: typeof(Nest2<>), stateParameters: [new(Parameter)]),
        ]));
        TypeExpr pattern = TypeExpr.Named("Nest", Parameter);
        context.AddRules(new(typeof(OuterRules), [new(pattern, 1, pattern, 2, Method(nameof(UpgradeNest)), [
            Dependency("left", typeof(OuterRules), "Nest"), Dependency("right", typeof(OuterRules), "Nest"),
        ])], allowKeepExact: true));
        DurableFieldInfo oldSlot = new(1, TypeTag.Int32), newSlot = oldSlot;
        for (int depth = 0; depth < 28; depth++) {
            TypeExpr nominal = TypeExpr.Named("Nest", StateBindingContext.NominalType(oldSlot));
            oldSlot = Inline(new(nominal, 1, SchemaKind.InlineValue, oldSlot));
            newSlot = Inline(new(nominal, 2, SchemaKind.InlineValue, newSlot));
        }
        DurableSchema source = BoxSchema(1, oldSlot), target = BoxSchema(2, newSlot);
        Type priorType = context.ResolveReader(source).StateType, nextType = context.ResolveReader(target).StateType;
        ObjectStateRecord row = new(new ObjectId(51), source, Activator.CreateInstance(priorType)!);
        MethodInfo normalize = typeof(ValueUpgradeBindingTests).GetMethod(nameof(NormalizeUnknown), BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(nextType);
        var run = normalize.CreateDelegate<Action<TestContext, ObjectStateRecord, DurableSchema>>();
        run(context, row, target);
        Assert.Equal(28, Calls.Count(item => item.Provider == "nest"));
        Calls.Clear();
        run(context, new(new ObjectId(52), source, Activator.CreateInstance(priorType)!), target);
        Assert.All(Calls, item => Assert.Equal(new ObjectId(52), item.Context.ObjectId));
    }

    [Fact]
    public void CachedPlansRecheckRegisteredInlineClosuresBeforeCallbacksAndFailedBindingsCanRetry() {
        Calls.Clear();
        TestContext context = CreateContext(Owner(nameof(IgnoreConvertedBoxValue), 1,
            Dependency("value", typeof(CoordinateRules), "Box")));
        DurableSchema source = BoxSchema(1, Inline(PointSchema(1))), target = BoxSchema(2, Inline(PointSchema(2)));
        Assert.Throws<InvalidDataException>(() => context.Normalize<Box2<Point2>>(new(new ObjectId(1), source, new Box1<Point1>(new(2))), target));
        Assert.Empty(Calls);
        context.AddRules(new(typeof(CoordinateRules), [InlineRule("Point", 1, 2, nameof(ScalePoint))]));
        Assert.Equal(0, context.Normalize<Box2<Point2>>(new(new ObjectId(1), source, new Box1<Point1>(new(2))), target).Value.X);
        Assert.Equal(new[] { "ignored" }, Calls.Select(item => item.Provider));
        Calls.Clear();
        context.Registered[(TypeExpr.Named("Point"), 1)] = new("Point", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int64));
        InvalidDataException conflict = Assert.Throws<InvalidDataException>(() =>
            context.Normalize<Box2<Point2>>(new(new ObjectId(2), source, new Box1<Point1>(new(2))), target));
        Assert.Contains("Schema requirement", conflict.Message);
        Assert.Contains("source", conflict.Message);
        Assert.Contains("field[1]", conflict.Message);
        Assert.Contains("Point", conflict.Message);
        Assert.Contains("v1", conflict.Message);
        Assert.Empty(Calls);
    }

    [Fact]
    public void CachedZeroStepPlanRechecksLateRegisteredInlineConflict() {
        Calls.Clear();
        TestContext context = CreateContext();
        DurableSchema schema = BoxSchema(1, Inline(PointSchema(1)));
        ObjectStateRecord source = new(new ObjectId(3), schema, new Box1<Point1>(new(5)));
        Assert.Equal(new Point1(5), context.Normalize<Box1<Point1>>(source, schema).Value);

        context.Registered[(TypeExpr.Named("Point"), 1)] =
            new("Point", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int64));
        InvalidDataException conflict = Assert.Throws<InvalidDataException>(() => context.Normalize<Box1<Point1>>(source, schema));

        Assert.Contains("Schema requirement", conflict.Message);
        Assert.Contains("source", conflict.Message);
        Assert.Contains("field[1]", conflict.Message);
        Assert.Contains("Point", conflict.Message);
        Assert.Contains("v1", conflict.Message);
        Assert.Empty(Calls);
    }

    private static void NormalizeUnknown<T>(TestContext context, ObjectStateRecord source, DurableSchema target) where T : unmanaged => context.Normalize<T>(source, target);
    private static StateUpgradeDependency Dependency(string key, Type rules, string declaration, int field = 1) => new(key, rules, new(declaration, field), new(declaration, field));
    private static StateUpgradeProvider Owner(string method, int version, params StateUpgradeDependency[] dependencies) => new("Box", version, Method(method), dependencies: dependencies);
    private static MethodInfo Method(string name) => typeof(ValueUpgradeBindingTests).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
    private static StateValueUpgradeProvider InlineRule(string definition, int from, int to, string method, params StateUpgradeDependency[] dependencies) {
        TypeExpr type = definition == "Pair" ? TypeExpr.Named(definition, Parameter) : TypeExpr.Named(definition);
        return new(type, from, type, to, Method(method), dependencies);
    }
    private static StateValueUpgradeProvider BuiltinRule(TypeTag source, TypeTag target, string method) => new(TypeExpr.Builtin(source), null, TypeExpr.Builtin(target), null, Method(method));
    private static DurableFieldInfo Inline(DurableSchema schema) => new(1, TypeTag.InlineValue, inlineSchema: schema);
    private static DurableSchema PointSchema(int version) => new("Point", version, SchemaKind.InlineValue, new DurableFieldInfo(1, version == 1 ? TypeTag.Int32 : TypeTag.Int64));
    private static DurableSchema PairSchema(int version, DurableSchema value) => new(TypeExpr.Named("Pair", value.Type), version, SchemaKind.InlineValue,
        Inline(value), new(2, TypeTag.InlineValue, inlineSchema: value));
    private static DurableSchema BoxSchema(int version, DurableFieldInfo slot) => new(TypeExpr.Named("Box", StateBindingContext.NominalType(slot)), version,
        version == 1 ? [slot] : [slot, new(2, TypeTag.Int32)]);

    private static TestContext CreateContext(params StateUpgradeProvider[] providers) {
        StateSchemaTemplate[] boxes = Enumerable.Range(1, 3).Select(version => new StateSchemaTemplate("Box", version, SchemaKind.ReferenceObject, 1,
            version == 1 ? [new(1, Parameter)] : [new(1, Parameter), new(2, TypeExpr.Builtin(TypeTag.Int32))],
            stateTypeDefinition: version == 1 ? typeof(Box1<>) : version == 2 ? typeof(Box2<>) : typeof(Box3<>), stateParameters: [new(Parameter)])).ToArray();
        return new(new("Box", SchemaKind.ReferenceObject, 1, null, boxes, upgrades: providers),
            new("Point", SchemaKind.InlineValue, 0, null, [
                new("Point", 1, SchemaKind.InlineValue, 0, [new(1, TypeExpr.Builtin(TypeTag.Int32))], stateTypeDefinition: typeof(Point1)),
                new("Point", 2, SchemaKind.InlineValue, 0, [new(1, TypeExpr.Builtin(TypeTag.Int64))], stateTypeDefinition: typeof(Point2)),
            ]), new("Pair", SchemaKind.InlineValue, 1, null, [
                new("Pair", 1, SchemaKind.InlineValue, 1, [new(1, Parameter), new(2, Parameter)], stateTypeDefinition: typeof(Pair1<>), stateParameters: [new(Parameter)]),
                new("Pair", 2, SchemaKind.InlineValue, 1, [new(1, Parameter), new(2, Parameter)], stateTypeDefinition: typeof(Pair2<>), stateParameters: [new(Parameter)]),
            ]));
    }

    private static TestContext SemanticContext(DurableFieldInfo prior, DurableFieldInfo next) {
        TestContext context = CreateContext();
        context.Definitions.Add("Node", new("Node", SchemaKind.ReferenceObject, 0, null, [new("Node", 1, SchemaKind.ReferenceObject, 0, [])]));
        context.Definitions.Add("Semantic", new("Semantic", SchemaKind.ReferenceObject, 0, null, [
            new("Semantic", 1, SchemaKind.ReferenceObject, 0, [new(1, StateBindingContext.NominalType(prior))], stateTypeDefinition: prior.TypeTag == TypeTag.UInt32 ? typeof(Box1<uint>) : typeof(Box1<ObjectId>)),
            new("Semantic", 2, SchemaKind.ReferenceObject, 0, [new(1, StateBindingContext.NominalType(next))], stateTypeDefinition: next.TypeTag == TypeTag.UInt32 ? typeof(Box2<uint>) : typeof(Box2<ObjectId>)),
        ], upgrades: [new("Semantic", 1, Method(nameof(UpgradeBox)), dependencies: [Dependency("value", typeof(KeepRules), "Semantic")])]));
        return context;
    }

    private static void UpgradeBox<A, B>(in Box1<A> prior, out Box2<B> next, UpgradeContext context) where A : unmanaged where B : unmanaged {
        Calls.Add(("box", context)); next = new(context.GetValueUpgrade<A, B>("value")(prior.Value), 1);
    }
    private static void UpgradeSecondBox<A, B>(in Box2<A> prior, out Box3<B> next, UpgradeContext context) where A : unmanaged where B : unmanaged {
        Calls.Add(("second", context)); next = new(context.GetValueUpgrade<A, B>("value")(prior.Value), 2);
    }
    private static void UpgradePair<A, B>(in Pair1<A> prior, out Pair2<B> next, UpgradeContext context) where A : unmanaged where B : unmanaged {
        Calls.Add(("pair", context));
        ValueUpgrade<A, B> element = context.GetValueUpgrade<A, B>("value");
        Assert.Throws<InvalidOperationException>(() => context.GetValueUpgrade<Pair1<A>, Pair2<B>>("value"));
        next = new(element(prior.Left), element(prior.Right));
    }
    private static void UpgradeNest<A, B>(in Nest1<A> prior, out Nest2<B> next, UpgradeContext context) where A : unmanaged where B : unmanaged {
        Calls.Add(("nest", context));
        ValueUpgrade<A, B> left = context.GetValueUpgrade<A, B>("left"), right = context.GetValueUpgrade<A, B>("right");
        Assert.Same(left, right);
        next = new(left(prior.Value));
    }
    private static void ClosedPair(in Pair1<Point1> prior, out Pair2<Point2> next, UpgradeContext context) { Calls.Add(("closed", context)); next = default; }
    private static void KeepPair<T>(in Pair2<T> prior, out Pair2<T> next, UpgradeContext context) where T : unmanaged { Calls.Add(("keep-pair", context)); next = prior; }
    private static void ScalePoint(in Point1 prior, out Point2 next, UpgradeContext context) {
        Calls.Add(("point", context));
        Assert.Throws<InvalidOperationException>(() => context.GetValueUpgrade<Point1, Point2>("value"));
        next = new(prior.X * 10);
    }
    private static void KeepPoint(in Point2 prior, out Point2 next, UpgradeContext context) { Calls.Add(("point", context)); next = prior; }
    private static void AddOne(in int prior, out int next, UpgradeContext context) { Calls.Add(("add", context)); next = prior + 1; }
    private static void TimesTen(in int prior, out int next, UpgradeContext context) { Calls.Add(("times", context)); next = prior * 10; }
    private static void CopyId(in ObjectId prior, out uint next, UpgradeContext context) { Calls.Add(("copy", context)); next = prior.Value; }
    private static void Constrained<T>(in T prior, out T next, UpgradeContext context) where T : unmanaged, IMarker { Calls.Add(("constraint", context)); next = prior; }
    private static void IgnoreBoxValue<T>(in Box1<T> prior, out Box2<T> next, UpgradeContext context) where T : unmanaged { Calls.Add(("ignored", context)); next = new(prior.Value, 1); }
    private static void IgnoreConvertedBoxValue<A, B>(in Box1<A> prior, out Box2<B> next, UpgradeContext context)
        where A : unmanaged where B : unmanaged { Calls.Add(("ignored", context)); next = new(default, 1); }
    private static void Legacy(in int prior, out int next) => next = prior;
    private static void UpgradeTwo(in Two1 prior, out Two2 next, UpgradeContext context) {
        Calls.Add(("two", context)); next = new(context.GetValueUpgrade<int, int>("base")(prior.Base), context.GetValueUpgrade<int, int>("own")(prior.Own));
    }
    private static void UnknownKey<T>(in Box1<T> prior, out Box2<T> next, UpgradeContext context) where T : unmanaged {
        Calls.Add(("unknown", context)); next = new(context.GetValueUpgrade<T, T>("unknown")(prior.Value), 1);
    }
    private static void WrongTypedKey<T>(in Box1<T> prior, out Box2<T> next, UpgradeContext context) where T : unmanaged {
        Calls.Add(("wrong", context)); _ = context.GetValueUpgrade<long, long>("value"); next = default;
    }
    private static void Throwing(in int prior, out int next, UpgradeContext context) { Calls.Add(("throw", context)); throw new ArithmeticException(); }

    private sealed class OuterRules;
    private sealed class CoordinateRules;
    private sealed class KeepRules;
    private sealed class OtherRules;
    private interface IMarker;
    private readonly record struct Point1(int X);
    private readonly record struct Point2(long X);
    private readonly record struct Box1<T>(T Value) where T : unmanaged;
    private readonly record struct Box2<T>(T Value, int Generation) where T : unmanaged;
    private readonly record struct Box3<T>(T Value, int Generation) where T : unmanaged;
    private readonly record struct Pair1<T>(T Left, T Right) where T : unmanaged;
    private readonly record struct Pair2<T>(T Left, T Right) where T : unmanaged;
    private readonly record struct Nest1<T>(T Value) where T : unmanaged;
    private readonly record struct Nest2<T>(T Value) where T : unmanaged;
    private readonly record struct Two1(int Base, int Own);
    private readonly record struct Two2(int Base, int Own);

    private sealed class TestContext(params StateDefinitionBinding[] definitions) : StateBindingContext {
        internal readonly Dictionary<string, StateDefinitionBinding> Definitions = definitions.ToDictionary(item => item.DefinitionId);
        internal readonly Dictionary<(TypeExpr Type, int Version), DurableSchema> Registered = [];
        private readonly Dictionary<Type, StateValueUpgradeRuleSet> _rules = [];
        internal void AddRules(StateValueUpgradeRuleSet rules) => _rules.Add(rules.RuleSet, rules);
        public override StateValueUpgradeRuleSet GetValueUpgradeRuleSet(Type ruleSet) => _rules.TryGetValue(ruleSet, out StateValueUpgradeRuleSet? value)
            ? value : throw new InvalidDataException("Missing test rule set.");
        public override bool TryGetCurrentModel(Type domainType, out StateModelBinding? model) { model = null; return false; }
        public override StateValueBinding ResolveCurrentValue(Type domainType) => throw new NotSupportedException();
        public override TypeExpr GetTypeExpr(Type domainType) => throw new NotSupportedException();
        public override Type GetDomainType(TypeExpr type) => throw new NotSupportedException();
        public override StateDefinitionBinding GetDefinition(string definitionId) => Definitions[definitionId];
        protected override bool TryGetSchema(TypeExpr type, int version, out DurableSchema? schema) => Registered.TryGetValue((type, version), out schema);
        public override StateValueBinding ResolveStoredValue(DurableFieldInfo slot) => BuiltinStateValues.TryBindStored(slot, out StateValueBinding value)
            ? value : new(slot, StateType(slot.InlineSchema!), typeof(NoOperations));
        public override StateReaderBinding ResolveReader(DurableSchema schema) {
            MethodInfo factory = typeof(TestContext).GetMethod(nameof(Reader), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(StateType(schema));
            return factory.CreateDelegate<Func<DurableSchema, StateReaderBinding>>()(schema);
        }
        private Type StateType(DurableSchema schema) {
            StateSchemaTemplate template = GetTemplate(schema.SchemaId, schema.Version);
            StateSchemaBinding binding = BindSchema(schema);
            return template.StateParameters.IsEmpty ? template.StateTypeDefinition! : template.StateTypeDefinition!.MakeGenericType(template.StateParameters
                .Select(parameter => binding.GetValue(parameter.Expression, parameter.InlineVersion).StateType).ToArray());
        }
        private static StateReaderBinding Reader<T>(DurableSchema schema) where T : unmanaged => new StateReaderBinding<T>(schema,
            static (ref BareValueReader reader) => default,
            static (ref BareValueReader reader, in T prior) => prior,
            static (in T state, IStateReferenceVisitor visitor) => { });
        private sealed class NoOperations;
    }
}
