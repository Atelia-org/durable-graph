using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;

using Atelia.Rbf;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class GenericBindingCatalogTests : IDisposable {
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "durable-generic-catalog-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void RepeatedParameterMustMatchCompleteInlineSchemaBeforeReaderFactory() {
        int factories = 0;
        StateModelRegistry registry = new();
        registry.Register(PointDefinition());
        registry.Register(new StateDefinitionBinding("Pair", SchemaKind.ReferenceObject, 1, null,
            [new("Pair", 1, SchemaKind.ReferenceObject, 1,
                [new(1, TypeExpr.Parameter(0)), new(2, TypeExpr.Parameter(0))])],
            historicalReaderFactory: (schema, _) => { factories++; return Reader(schema); }));
        TypeExpr family = TypeExpr.Named("Pair", TypeExpr.Named("Point"));
        DurableSchema first = Point(1), second = Point(2);
        DurableSchema inconsistent = new(family, 1, Inline(1, first), Inline(2, second));
        ModelExecutionEnvironment snapshot = registry.CreateEnvironment();
        Assert.Throws<InvalidDataException>(() => snapshot.ResolveReader(inconsistent));
        Assert.Equal(0, factories);
        DurableSchema consistent = new(family, 1, Inline(1, first), Inline(2, first));
        StateReaderBinding reader = snapshot.ResolveReader(consistent);
        Assert.Same(reader, snapshot.ResolveReader(consistent));
        Assert.Equal(1, factories);
        Assert.Equal(first, snapshot.BindSchema(consistent).GetValue(TypeExpr.Parameter(0)).Slot.InlineSchema);
    }

    [Fact]
    public void ParameterCannotPretendAnInlineDeclarationIsAReference() {
        StateModelRegistry registry = BasicRegistry();
        DurableSchema malicious = new(TypeExpr.Named("Box", TypeExpr.Named("Point")), 1,
            DurableFieldInfo.Reference(1, TypeExpr.Named("Point")));
        Assert.Throws<InvalidDataException>(() => registry.CreateEnvironment().BindSchema(malicious));
    }

    [Fact]
    public void SameExactKeyWithDifferentLayoutsReportsBothRequirementPaths() {
        StateModelRegistry registry = BasicRegistry();
        DurableSchema first = Point(1);
        DurableSchema conflicting = new("Point", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int64));
        DurableSchema owner = new(TypeExpr.Named("Pair", TypeExpr.Named("Point")), 1,
            Inline(1, first), Inline(2, conflicting));

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => registry.CreateEnvironment().BindSchema(owner));

        Assert.Contains("field[1].inline", error.Message);
        Assert.Contains("field[2].inline", error.Message);
        Assert.Contains("Point v1", error.Message);
    }

    [Fact]
    public void FullSchemaConflictIsSnapshotLocalWhileIndependentEmptyCatalogsRemainIndependent() {
        StateModelRegistry registry = BasicRegistry();
        TypeExpr family = TypeExpr.Named("Box", TypeExpr.Named("Point"));
        DurableSchema first = new(family, 1, Inline(1, Point(1)));
        DurableSchema second = new(family, 1, Inline(1, Point(2)));
        ModelExecutionEnvironment snapshot = registry.CreateEnvironment();
        StateSchemaBinding bound = snapshot.BindSchema(first);
        Assert.Same(bound, snapshot.BindSchema(first));
        Assert.Throws<InvalidDataException>(() => snapshot.BindSchema(second));
        Assert.Equal(second, registry.CreateEnvironment().BindSchema(second).Schema);
    }

    [Fact]
    public void CachedBindingsRecheckTheirSelectedImmutableSchemaViewWithoutBorrowingAnotherViewsClosure() {
        StateModelRegistry registry = BasicRegistry();
        DurableSchema first = Point(1);
        DurableSchema owner = new(TypeExpr.Named("Box", TypeExpr.Named("Point")), 1, Inline(1, first));
        DurableSchema conflicting = new("Point", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int64));
        SchemaCatalogView selected = LoadView("matching", first), other = LoadView("conflicting", conflicting);
        var code = registry.FreezeConfiguration();
        int lookups = 0;
        ModelExecutionEnvironment snapshot = code.CreateEnvironment(key => {
            lookups++;
            return selected.Schemas.GetValueOrDefault(key);
        });
        StateSchemaBinding point = snapshot.BindSchema(first), boundOwner = snapshot.BindSchema(owner);
        StateValueBinding value = snapshot.ResolveStoredValue(Inline(1, first));
        StateReaderBinding reader = snapshot.ResolveReader(owner);
        int beforeHits = lookups;
        Assert.Same(point, snapshot.BindSchema(first));
        StateValueBinding cachedValue = snapshot.ResolveStoredValue(Inline(1, first));
        Assert.Equal(value.Slot, cachedValue.Slot);
        Assert.Equal(value.StateOpsType, cachedValue.StateOpsType);
        Assert.Same(boundOwner, snapshot.BindSchema(owner));
        Assert.Same(reader, snapshot.ResolveReader(owner));
        Assert.True(lookups > beforeHits); // Successful memo hits still perform the standard exact checks.
        ModelExecutionEnvironment rejected = code.CreateEnvironment(key => other.Schemas.GetValueOrDefault(key));
        foreach (Action action in new Action[] {
            () => rejected.BindSchema(first), () => rejected.ResolveStoredValue(Inline(1, first)),
            () => rejected.BindSchema(owner), () => rejected.ResolveReader(owner),
        }) {
            InvalidDataException error = Assert.Throws<InvalidDataException>(action);
            Assert.IsType<SchemaConflictException>(error.InnerException);
        }
        Assert.Same(reader, snapshot.ResolveReader(owner));
        Assert.Equal(first, selected.Schemas[new(first.Type, first.Version)]);
        Assert.Equal(conflicting, other.Schemas[new(first.Type, first.Version)]);

        SchemaCatalogView LoadView(string name, DurableSchema schema) {
            string path = Path.Combine(_directory, name);
            FrameAddress selectedRoot;
            using (var writer = RepositoryStorageFacade.CreateNew(path)) {
                SchemaCatalogWorkspace catalog = new(writer.EnsureEditingAllowed);
                catalog.Set(schema);
                PreparedSchemaCatalogWrite candidate = catalog.Prepare();
                using PendingRepositorySave pending = writer.Prepare("main", StoredGraphKind.State,
                    new(null, new(1), [ObjectVersionRecord.CreateBase(1,
                        TypedBaseBodyCodec.Encode(ObjectLayout.String, StringBodyPreparer.PrepareBase("catalog carrier")))]), candidate.Write);
                selectedRoot = writer.Commit(pending).Business.SchemaRoot!.Value;
            }
            using var reader = RepositoryStorageFacade.OpenReadOnlyExisting(path);
            return SchemaCatalogView.Load(reader.Store, selectedRoot);
        }
    }

    [Fact]
    public void SharedExactDagDoesNotExpandAsAnExponentialTree() {
        StateModelRegistry registry = new();
        DurableSchema child = new("Dag0", 1, SchemaKind.InlineValue);
        registry.Register(new StateDefinitionBinding("Dag0", SchemaKind.InlineValue, 0, null,
            [new("Dag0", 1, SchemaKind.InlineValue, 0, [])]));
        for (int level = 1; level <= 45; level++) {
            string id = "Dag" + level;
            registry.Register(new StateDefinitionBinding(id, SchemaKind.InlineValue, 0, null,
                [new(id, 1, SchemaKind.InlineValue, 0,
                    [new(1, child.Type, 1), new(2, child.Type, 1)])]));
            child = new(id, 1, SchemaKind.InlineValue, Inline(1, child), Inline(2, child));
        }
        registry.Register(new StateDefinitionBinding("Root", SchemaKind.ReferenceObject, 0, null,
            [new("Root", 1, SchemaKind.ReferenceObject, 0, [new(1, child.Type, 1)])]));
        DurableSchema root = new("Root", 1, Inline(1, child));
        Assert.Same(root, registry.CreateEnvironment().BindSchema(root).Schema);
    }

    [Fact]
    public void SharedChildReachedThroughLongerPathStillEnforcesExactDepthBound() {
        DurableSchema shared = new("Leaf", 1, SchemaKind.InlineValue);
        for (int index = 0; index < 199; index++) {
            shared = new("Shared" + index, 1, SchemaKind.InlineValue, Inline(1, shared));
        }
        DurableSchema longer = shared;
        for (int index = 0; index < 60; index++) {
            longer = new("Prefix" + index, 1, SchemaKind.InlineValue, Inline(1, longer));
        }
        DurableSchema root = new("Root", 1, Inline(1, shared), Inline(2, longer));
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => new StateModelRegistry().CreateEnvironment().BindSchema(root));
        Assert.Contains("depth", error.Message);
    }

    [Fact]
    public void PhantomIdentityNeedsNoValueOperandButStillChecksOwnerArity() {
        StateModelRegistry registry = new();
        registry.Register(new StateDefinitionBinding("Phantom", SchemaKind.ReferenceObject, 1, null,
            [new("Phantom", 1, SchemaKind.ReferenceObject, 1, [])]));
        DurableSchema schema = new(TypeExpr.Named("Phantom", TypeExpr.Named("RetiredArgument")), 1);
        StateSchemaBinding binding = registry.CreateEnvironment().BindSchema(schema);
        Assert.Empty(binding.FieldSlots);
        Assert.Throws<InvalidDataException>(() => binding.GetValue(TypeExpr.Parameter(0)));
        Assert.Throws<InvalidDataException>(() => registry.CreateEnvironment().BindSchema(new(TypeExpr.Named("Phantom"), 1)));
    }

    [Fact]
    public void InheritedOrdinalsAreSubstitutedBeforeBindingAndReferenceKindsStayDistinct() {
        StateModelRegistry registry = new();
        registry.Register(new StateDefinitionBinding("Node", SchemaKind.ReferenceObject, 0, null,
            [new("Node", 1, SchemaKind.ReferenceObject, 0, [])]));
        registry.Register(new StateDefinitionBinding("Base", SchemaKind.ReferenceObject, 1, null,
            [new("Base", 1, SchemaKind.ReferenceObject, 1, [new(1, TypeExpr.Parameter(0))])]));
        registry.Register(new StateDefinitionBinding("Derived", SchemaKind.ReferenceObject, 2, null,
            [new("Derived", 1, SchemaKind.ReferenceObject, 2, [new(1, TypeExpr.Parameter(0))],
                new(TypeExpr.Named("Base", TypeExpr.Parameter(1)), 1))]));
        TypeExpr node = TypeExpr.Named("Node");
        DurableSchema ancestor = new(TypeExpr.Named("Base", node), 1, DurableFieldInfo.Reference(1, node));
        DurableSchema leaf = new(TypeExpr.Named("Derived", TypeExpr.Builtin(TypeTag.String), node), 1,
            [new(1, TypeTag.String)], ancestor);
        StateSchemaBinding binding = registry.CreateEnvironment().BindSchema(leaf);
        StateValueBinding own = binding.GetValue(TypeExpr.Parameter(0));
        StateValueBinding inherited = binding.GetValue(TypeExpr.Parameter(1));
        Assert.Equal(typeof(ObjectId), own.StateType);
        Assert.Equal(typeof(ObjectId), inherited.StateType);
        Assert.Equal(TypeTag.String, own.Slot.TypeTag);
        Assert.Equal(node, inherited.Slot.TargetType);
        Assert.NotEqual(own.StateOpsType, inherited.StateOpsType);
        Assert.Equal(TypeTag.ObjectReference, binding.FieldSlots[0].TypeTag);
        Assert.Equal(TypeTag.String, binding.FieldSlots[1].TypeTag);
    }

    [Fact]
    public void FailedClosureDoesNotPublishAndLaterCatalogCanSupplyMissingCapability() {
        int attempts = 0;
        StateModelRegistry registry = new();
        registry.Register(new StateDefinitionBinding("Box", SchemaKind.ReferenceObject, 1, typeof(Box<>),
            [new("Box", 1, SchemaKind.ReferenceObject, 1, [new(1, TypeExpr.Parameter(0))])],
            currentModelFactory: (type, context) => {
                attempts++;
                context.GetDefinition("Needed");
                DurableSchema schema = new(context.GetTypeExpr(type), 1, new DurableFieldInfo(1, TypeTag.Int32));
                return Model<Box<int>>(schema, context.ResolveReader(schema));
            }, historicalReaderFactory: static (schema, _) => Reader(schema)));
        ModelExecutionEnvironment old = registry.CreateEnvironment();
        Assert.Throws<InvalidDataException>(() => old.ResolveCurrentModel(typeof(Box<int>)));
        Assert.Empty(old.Models);
        registry.Register(new StateDefinitionBinding("Needed", SchemaKind.InlineValue, 0, null,
            [new("Needed", 1, SchemaKind.InlineValue, 0, [])]));
        Assert.Throws<InvalidDataException>(() => old.ResolveCurrentModel(typeof(Box<int>)));
        ModelExecutionEnvironment next = registry.CreateEnvironment();
        StateModelBinding model = next.ResolveCurrentModel(typeof(Box<int>));
        Assert.Same(model, next.ResolveCurrentModel(typeof(Box<int>)));
        Assert.Equal(3, attempts);
    }

    [Fact]
    public void ReferenceProjectionDoesNotInitializeAnExpandingObjectBody() {
        int bodyFactories = 0;
        StateModelRegistry registry = new();
        registry.Register(new StateDefinitionBinding("Box", SchemaKind.ReferenceObject, 1, typeof(Box<>),
            [new("Box", 1, SchemaKind.ReferenceObject, 1, [])],
            currentModelFactory: (_, _) => { bodyFactories++; throw new InvalidOperationException("No object body is needed for a reference slot."); }));
        StateValueBinding value = registry.CreateEnvironment().ResolveCurrentValue(typeof(Box<Box<int>>));
        Assert.Equal(TypeExpr.Named("Box", TypeExpr.Named("Box", TypeExpr.Builtin(TypeTag.Int32))), value.Slot.TargetType);
        Assert.Equal(typeof(ObjectId), value.StateType);
        Assert.Equal(0, bodyFactories);
    }

    [Fact]
    public void DefinitionAndConcreteModelOwnershipConflictsAreRejectedInBothOrders() {
        DurableSchema schema = new("Legacy", 1, new DurableFieldInfo(1, TypeTag.Int32));
        StateModelBinding model = Model<Legacy>(schema, Reader(schema));
        StateDefinitionBinding wrong = new("Other", SchemaKind.ReferenceObject, 0, typeof(Legacy),
            [new("Other", 1, SchemaKind.ReferenceObject, 0, [])]);
        StateModelRegistry first = new();
        first.Register(model);
        Assert.Throws<InvalidOperationException>(() => first.Register(wrong));
        Assert.Same(model, first.CreateEnvironment().Types[typeof(Legacy)]);
        StateModelRegistry second = new();
        second.Register(wrong);
        Assert.Throws<InvalidOperationException>(() => second.Register(model));
        Assert.Empty(second.CreateEnvironment().Models);
    }

    private static StateModelRegistry BasicRegistry() {
        StateModelRegistry registry = new();
        registry.Register(PointDefinition());
        registry.Register(new StateDefinitionBinding("Box", SchemaKind.ReferenceObject, 1, null,
            [new("Box", 1, SchemaKind.ReferenceObject, 1, [new(1, TypeExpr.Parameter(0))])],
            historicalReaderFactory: static (schema, _) => Reader(schema)));
        return registry;
    }

    private static StateDefinitionBinding PointDefinition() => new("Point", SchemaKind.InlineValue, 0, null,
        [new("Point", 1, SchemaKind.InlineValue, 0, [new(1, TypeExpr.Builtin(TypeTag.Int32))]),
         new("Point", 2, SchemaKind.InlineValue, 0, [new(1, TypeExpr.Builtin(TypeTag.Int32)), new(2, TypeExpr.Builtin(TypeTag.Int32))])],
        historicalValueFactory: static (schema, _) => new(Inline(1, schema), typeof(int), typeof(Int32StateOps)));

    private static DurableSchema Point(int version) => version == 1
        ? new("Point", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int32))
        : new("Point", 2, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int32), new DurableFieldInfo(2, TypeTag.Int32));

    private static DurableFieldInfo Inline(int id, DurableSchema schema) => new(id, TypeTag.InlineValue, inlineSchema: schema);

    // These catalog tests execute closure and validation only; body delegates remain unused.
    private static StateReaderBinding<int> Reader(DurableSchema schema) => new(schema,
        static (ref BareValueReader reader) => reader.ReadVarInt32(),
        static (ref BareValueReader reader, in int prior) => reader.ReadVarInt32(),
        static (in int state, IStateReferenceVisitor visitor) => { });

    private static StateModelBinding<T, int> Model<T>(DurableSchema schema, StateReaderBinding reader) where T : class, IDurableObject, new() => new(
        new StatePreparationBinding<int>(schema, static (in int state) => new([]),
            static (in int prior, in int next) => new(false, [])), [reader],
        static row => row.GetState<int>(), static () => new T(),
        static (T target, in int state, ObjectReadTable objects) => { },
        static (T source, CaptureContext context) => 0,
        static (in int state, IStateReferenceVisitor visitor) => { });

    private sealed class Box<T> : IDurableObject { public Box() { } }
    private sealed class Legacy : IDurableObject { public Legacy() { } }

    public void Dispose() {
        string resolved = Path.GetFullPath(_directory);
        if (!string.Equals(Path.GetDirectoryName(resolved), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("durable-generic-catalog-", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Refusing to delete outside the temporary test directory.");
        }
        if (Directory.Exists(resolved)) { Directory.Delete(resolved, recursive: true); }
    }
}
