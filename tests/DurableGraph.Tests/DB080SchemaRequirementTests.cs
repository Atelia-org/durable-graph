using System.Reflection;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed class DB080SchemaRequirementTests {
    [Fact]
    public void CompletedNestedScopeMergesButAbandonedScopeAndLaterOperationsDoNotLeak() {
        TestContext context = new();
        DurableSchema first = Inline("First"), child = Inline("Child"), abandoned = Inline("Abandoned"), later = Inline("Later");
        StateBindingContext.ExactSchemaRequirementSet certificate;
        using (var outer = context.BeginSchemaRequirementCollection()) {
            context.CheckRegistered(first);
            using (var inner = context.BeginSchemaRequirementCollection()) {
                context.CheckRegistered(child);
                inner.Complete();
            }
            Assert.Throws<ArithmeticException>((Action)(() => {
                using var inner = context.BeginSchemaRequirementCollection();
                context.CheckRegistered(abandoned);
                throw new ArithmeticException();
            }));
            certificate = outer.Complete();
        }
        context.CheckRegistered(later);
        Assert.Equal(2, certificate.Count);
        context.Conflict(abandoned);
        context.Conflict(later);
        certificate.Validate(context);
        context.Conflict(child);
        Assert.Throws<InvalidDataException>(() => certificate.Validate(context));
        context.Registered.Remove((child.Type, child.Version));
        context.Conflict(first);
        Assert.Throws<InvalidDataException>(() => certificate.Validate(context));
    }

    [Fact]
    public void ValidatingCertificateInsideScopeMergesItsRequirementsAndPreservesDiagnosticPath() {
        TestContext context = new();
        DurableSchema dependency = Inline("Dependency");
        var prior = StateBindingContext.ExactSchemaRequirementSet.Create((dependency, "original intermediate"));
        StateBindingContext.ExactSchemaRequirementSet collected;
        using (var scope = context.BeginSchemaRequirementCollection()) {
            prior.Validate(context);
            prior.Validate(context);
            collected = scope.Complete();
        }
        context.Conflict(dependency);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => collected.Validate(context));
        Assert.Contains("original intermediate", error.Message);
        Assert.IsType<SchemaConflictException>(error.InnerException);
    }

    [Fact]
    public void ScopeRejectsConflictingRequirementsEvenBeforeEitherDefinitionIsRegistered() {
        TestContext context = new();
        DurableSchema schema = Inline("Duplicated");
        using (var scope = context.BeginSchemaRequirementCollection()) {
            StateBindingContext.ExactSchemaRequirementSet.Create((schema, "first path")).Validate(context);
            InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
                StateBindingContext.ExactSchemaRequirementSet.Create((Inline("Duplicated", TypeTag.Int64), "second path")).Validate(context));
            Assert.Contains("first path", error.Message);
            Assert.Contains("second path", error.Message);
        }
        using var next = context.BeginSchemaRequirementCollection();
        context.CheckRegistered(Inline("Duplicated", TypeTag.Int64));
        next.Complete().Validate(context);
    }

    [Theory]
    [InlineData("durable")]
    [InlineData("array")]
    [InlineData("list")]
    [InlineData("dictionary-key")]
    [InlineData("dictionary-value")]
    public void CompleteLayoutIncludesNullableInlineDependenciesForEveryObjectKind(string kind) {
        TestContext context = new();
        DurableSchema child = Inline("Nested");
        DurableFieldInfo slot = DurableFieldInfo.Nullable(1, new(1, TypeTag.InlineValue, inlineSchema: child));
        ObjectLayout layout = kind switch {
            "durable" => ObjectLayout.ForDurable(new("Owner", 1, slot)),
            "array" => ObjectLayout.ForArray(new(TypeExprKind.VectorArray, slot)),
            "list" => ObjectLayout.ForList(new(slot)),
            "dictionary-key" => ObjectLayout.ForDictionary(new(slot, new(2, TypeTag.Int32))),
            _ => ObjectLayout.ForDictionary(new(new(1, TypeTag.Int32), slot)),
        };
        StateBindingContext.ExactSchemaRequirementSet certificate;
        using (var scope = context.BeginSchemaRequirementCollection()) {
            context.CheckObjectLayout(layout, "full source row");
            certificate = scope.Complete();
        }
        context.Conflict(child);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => certificate.Validate(context));
        Assert.Contains("full source row", error.Message);
        Assert.IsType<SchemaConflictException>(error.InnerException);
    }

    [Fact]
    public void CompleteLayoutIncludesBaseSchemasAndTheirInlineDependencies() {
        TestContext context = new();
        DurableSchema child = Inline("BaseValue");
        DurableSchema ancestor = new("Ancestor", 1, new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: child));
        DurableSchema owner = new(TypeExpr.Named("Derived"), 1, [], ancestor);
        using var scope = context.BeginSchemaRequirementCollection();
        context.CheckObjectLayout(ObjectLayout.ForDurable(owner), "source");
        var certificate = scope.Complete();
        context.Conflict(child);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => certificate.Validate(context));
        Assert.Contains("source.base.field[1].inline", error.Message);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("nullable")]
    [InlineData("reader-factory")]
    public void PreclosedUpgradePlanContributesIntermediateAndFactoryRequirements(string conflictKind) {
        TypeExpr owner = TypeExpr.Named("Owner"), intermediate = TypeExpr.Named("Intermediate");
        TestContext context = new() { ReaderDependency = Inline("ReaderDependency") };
        context.Definitions.Add("Owner", new("Owner", SchemaKind.ReferenceObject, 0, null, [
            new("Owner", 1, SchemaKind.ReferenceObject, 0, [], stateTypeDefinition: typeof(First)),
            new("Owner", 2, SchemaKind.ReferenceObject, 0,
                [new(1, TypeExpr.Nullable(intermediate), 5)], stateTypeDefinition: typeof(Middle)),
            new("Owner", 3, SchemaKind.ReferenceObject, 0, [], stateTypeDefinition: typeof(Last)),
        ], upgrades: [
            new("Owner", 1, Method(nameof(ToMiddle)), owner),
            new("Owner", 2, Method(nameof(ToLast)), owner),
        ]));
        context.Definitions.Add("Intermediate", new("Intermediate", SchemaKind.InlineValue, 0, null, [
            new("Intermediate", 5, SchemaKind.InlineValue, 0, [new(1, TypeExpr.Builtin(TypeTag.Int32))],
                stateTypeDefinition: typeof(Intermediate)),
        ]));
        DurableSchema source = new(owner, 1), target = new(owner, 3);
        ObjectStateRecord record = new(new ObjectId(1), source, new First());
        context.Normalize<Last>(record, target); // Close and memoize before collection starts.
        StateBindingContext.ExactSchemaRequirementSet certificate;
        using (var scope = context.BeginSchemaRequirementCollection()) {
            context.Normalize<Last>(record, target);
            certificate = scope.Complete();
        }
        if (conflictKind == "owner") {
            context.Registered[(owner, 2)] = new(owner, 2, new DurableFieldInfo(1, TypeTag.Int64));
        } else if (conflictKind == "nullable") {
            context.Registered[(intermediate, 5)] = new(intermediate, 5, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int64));
        } else {
            context.Conflict(context.ReaderDependency!);
        }
        int readerCalls = context.ReaderCalls;
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => certificate.Validate(context));
        Assert.Contains(conflictKind == "reader-factory" ? "ReaderDependency" : "step[0]", error.Message);
        Assert.Equal(readerCalls, context.ReaderCalls);
        Assert.IsType<SchemaConflictException>(error.InnerException);
    }

    private static DurableSchema Inline(string name, TypeTag tag = TypeTag.Int32) =>
        new(name, 1, SchemaKind.InlineValue, new DurableFieldInfo(1, tag));
    private static MethodInfo Method(string name) => typeof(DB080SchemaRequirementTests).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
    private static void ToMiddle(in First prior, out Middle next, UpgradeContext context) => next = default;
    private static void ToLast(in Middle prior, out Last next, UpgradeContext context) => next = default;
    private readonly record struct First;
    private readonly record struct Middle(NullableState<Intermediate> Value);
    private readonly record struct Last;
    private readonly record struct Intermediate(int Value);

    private sealed class TestContext : StateBindingContext {
        internal readonly Dictionary<string, StateDefinitionBinding> Definitions = [];
        internal readonly Dictionary<(TypeExpr Type, int Version), DurableSchema> Registered = [];
        internal int ReaderCalls;
        internal DurableSchema? ReaderDependency;
        internal void Conflict(DurableSchema schema) => Registered[(schema.Type, schema.Version)] =
            new(schema.Type, schema.Version, schema.Kind, new DurableFieldInfo(1, TypeTag.Int64));
        public override bool TryGetCurrentModel(Type domainType, out StateModelBinding? model) { model = null; return false; }
        public override StateValueBinding ResolveCurrentValue(Type domainType) => throw new NotSupportedException();
        public override StateValueBinding ResolveStoredValue(DurableFieldInfo slot) => BuiltinStateValues.TryBindStored(slot, out StateValueBinding binding)
            ? binding : new(slot, GetTemplate(slot.InlineSchema!.SchemaId, slot.InlineSchema.Version).StateTypeDefinition!, typeof(NoOperations));
        public override StateReaderBinding ResolveReader(DurableSchema schema) {
            ReaderCalls++;
            if (ReaderDependency is { } dependency) { CheckRegistered(dependency); }
            BindSchema(schema);
            Type type = GetTemplate(schema.SchemaId, schema.Version).StateTypeDefinition!;
            return typeof(TestContext).GetMethod(nameof(Reader), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(type).CreateDelegate<Func<DurableSchema, StateReaderBinding>>()(schema);
        }
        public override TypeExpr GetTypeExpr(Type domainType) => throw new NotSupportedException();
        public override Type GetDomainType(TypeExpr type) => throw new NotSupportedException();
        public override StateDefinitionBinding GetDefinition(string definitionId) => Definitions[definitionId];
        public override bool TryGetSchema(TypeExpr type, int version, out DurableSchema? schema) => Registered.TryGetValue((type, version), out schema);
        private static StateReaderBinding Reader<T>(DurableSchema schema) where T : unmanaged => new StateReaderBinding<T>(schema,
            static (ref BinaryPayloadReader reader) => default,
            static (ref BinaryPayloadReader reader, in T prior) => prior,
            static (in T state, IStateReferenceVisitor visitor) => { });
        private sealed class NoOperations;
    }
}
