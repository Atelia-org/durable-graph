using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Reflection;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.FrameStore;
using SegmentStore = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

/// <summary>Mixed ordinary-model/Family catalog contracts, independent of generated CLR assembly placement.</summary>
public sealed class CrossAssemblyNominalBindingTests {
    private static readonly TypeExpr Parameter = TypeExpr.Parameter(0);
    private static readonly TypeExpr RemoteType = TypeExpr.Named("Remote");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegisteredOrdinaryModelSuppliesOnlyNominalEvidenceInEitherRegistrationOrder(bool modelFirst) {
        StateModelBinding remote = RemoteModel<Remote>(RemoteType);
        StateDefinitionBinding owner = OwnerDefinition();
        StateModelRegistry registry = new();
        if (modelFirst) { registry.Register(remote); registry.Register(owner); }
        else { registry.Register(owner); registry.Register(remote); }
        StateModelSnapshot snapshot = registry.Snapshot();
        DurableSchema schema = OwnerSchema(RemoteType, 2);

        StateSchemaBinding bound = snapshot.BindSchema(schema);
        Assert.Equal(typeof(ObjectId), bound.GetValue(Parameter).StateType);
        Assert.Equal(schema, snapshot.InferSchemaFromState(schema.Type, 2, typeof(Owner2<ObjectId>)));
        Assert.Equal(RemoteType, Assert.Single(bound.FieldSlots).TargetType);
        Assert.Null(schema.Fields[0].InlineSchema);
        Assert.Same(remote.Readers[0], snapshot.ResolveReader(remote.CurrentSchema));

        // A v2 current model is not a substitute for its absent v1 reader or any retained template.
        Assert.Throws<InvalidDataException>(() => snapshot.ResolveReader(new DurableSchema(RemoteType, 1)));
        Assert.Throws<InvalidDataException>(() => snapshot.GetTemplate("Remote", 1));
        Assert.Throws<InvalidDataException>(() => snapshot.InferSchemaFromState(schema.Type, 2, typeof(Owner2<uint>)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrDifferentCompleteNominalModelCannotSatisfyAReference(bool registerOtherClosure) {
        StateModelRegistry registry = new();
        registry.Register(OwnerDefinition());
        if (registerOtherClosure) {
            registry.Register(RemoteModel<RemoteGeneric<int>>(TypeExpr.Named("Remote", TypeExpr.Builtin(TypeTag.Int32))));
        }
        TypeExpr missing = TypeExpr.Named("Remote", TypeExpr.Builtin(TypeTag.Int64));
        StateModelSnapshot snapshot = registry.Snapshot();
        Assert.Throws<InvalidDataException>(() => snapshot.BindSchema(OwnerSchema(missing, 2)));
        Assert.Throws<InvalidDataException>(() => snapshot.InferSchemaFromState(
            TypeExpr.Named("Owner", missing), 2, typeof(Owner2<ObjectId>)));
    }

    [Theory]
    [InlineData(SchemaKind.InlineValue, 0)]
    [InlineData(SchemaKind.ReferenceObject, 1)]
    public void PresentWrongDefinitionCannotFallBackToAConcreteModel(SchemaKind kind, int arity) {
        int factories = 0;
        StateDefinitionBinding invalid = new("Remote", kind, arity, null,
            [new("Remote", 2, kind, arity, [])],
            currentModelFactory: (_, _) => { factories++; throw new InvalidOperationException("Do not close nominal bodies."); });
        StateModelBinding model = RemoteModel<Remote>(RemoteType);
        // Production registration already rejects conflicting ownership. Also test the binding
        // boundary directly so a future catalog source cannot silently choose the model instead.
        StateModelSnapshot snapshot = new(new() { [RemoteType] = model }, new() { [typeof(Remote)] = model }, [],
            new() { ["Owner"] = OwnerDefinition(), ["Remote"] = invalid });

        Assert.Throws<InvalidDataException>(() => snapshot.BindSchema(OwnerSchema(RemoteType, 2)));
        Assert.Throws<InvalidDataException>(() => snapshot.InferSchemaFromState(
            TypeExpr.Named("Owner", RemoteType), 2, typeof(Owner2<ObjectId>)));
        Assert.Equal(0, factories);
        StateModelRegistry registry = new();
        registry.Register(model);
        Assert.Throws<InvalidOperationException>(() => registry.Register(invalid));
    }

    [Fact]
    public void DefinitionNominalChecksNeverInvokeCurrentOrHistoricalTargetFactories() {
        int factories = 0;
        StateModelRegistry registry = new();
        registry.Register(OwnerDefinition());
        registry.Register(new StateDefinitionBinding("Remote", SchemaKind.ReferenceObject, 0, typeof(Remote),
            [new("Remote", 2, SchemaKind.ReferenceObject, 0, [])],
            currentModelFactory: (_, _) => { factories++; throw new InvalidOperationException("Unexpected current target body."); },
            historicalReaderFactory: (_, _) => { factories++; throw new InvalidOperationException("Unexpected historical target body."); }));
        StateModelSnapshot snapshot = registry.Snapshot();

        DurableSchema schema = OwnerSchema(RemoteType, 2);
        snapshot.BindSchema(schema);
        Assert.Equal(schema, snapshot.InferSchemaFromState(schema.Type, 2, typeof(Owner2<ObjectId>)));
        Assert.Equal(0, factories);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IntermediateUpgradeUsesNominalEvidenceForPriorAndExplicitDtoInference(bool explicitOutput) {
        StateModelRegistry registry = new();
        registry.Register(RemoteModel<Remote>(RemoteType));
        registry.Register(OwnerDefinition(explicitOutput ? nameof(FirstClosed) : nameof(FirstGeneric)));
        StateModelSnapshot snapshot = registry.Snapshot();
        DurableSchema source = OwnerSchema(RemoteType, 1), current = OwnerSchema(RemoteType, 3);
        using CaptureContext capture = new CaptureSession().BeginCapture();
        capture.AddRoot(new Remote(), source, static (_, _) => new Owner1());
        ObjectStateRecord prior = Assert.Single(capture.Seal().Objects);
        Owner3<ObjectId> result = snapshot.Normalize<Owner3<ObjectId>>(prior, current);

        Assert.True(result.Value.IsNull);
        Assert.Equal(2, result.Generation);
        // The intermediate v2 does not exist in a SchemaStore. A generic output with an
        // unbound T uses BuildSlot; a closed ObjectId output uses InferSlotFromState.
        Assert.Equal(OwnerSchema(RemoteType, 2), snapshot.InferSchemaFromState(current.Type, 2, typeof(Owner2<ObjectId>)));
    }

    [Fact]
    public void ConcreteReferenceModelNeverSuppliesAnInlineTemplate() {
        StateModelRegistry registry = new();
        registry.Register(RemoteModel<Remote>(RemoteType));
        registry.Register(OwnerDefinition());
        StateModelSnapshot snapshot = registry.Snapshot();
        DurableSchema fakeInline = new(RemoteType, 2, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int32));
        DurableSchema owner = new(TypeExpr.Named("Owner", RemoteType), 2,
            new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: fakeInline));

        Assert.Throws<InvalidDataException>(() => snapshot.BindSchema(owner));
        Assert.Throws<InvalidDataException>(() => snapshot.ResolveStoredValue(new(1, TypeTag.InlineValue, inlineSchema: fakeInline)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublicExactReadUsesReaderNominalEvidenceButStillRequiresTheActualHistoricalReader(bool omitOldTargetReader) {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"durable-graph-nominal-reader-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(root);
        try {
            using GraphResources file = GraphResources.CreateLegacyNew(Path.Combine(root, "schemas.rbf"));
            var segments = file.Data;
            SchemaStore schemas = file.Schemas;
            using StateRevisionStore store = new(segments);
            DurableSchema owner = OwnerSchema(RemoteType, 2);
            DurableSchema remote = new(RemoteType, 1, new DurableFieldInfo(1, TypeTag.Byte));
            RepresentationId[] ids = schemas.RegisterRepresentations([ObjectLayout.ForDurable(owner), ObjectLayout.ForDurable(remote)]);
            FrameAddress revision = store.Append(StateRevision.CreateObjectHeadMapBase(null, [
                ObjectVersionRecord.CreateBase(1, BaseObjectBodyCodec.Encode(ids[0], new PreparedBaseBody([2])).Body),
                ObjectVersionRecord.CreateBase(2, BaseObjectBodyCodec.Encode(ids[1], new PreparedBaseBody([37])).Body),
            ], []));
            int ownerReads = 0, targetReads = 0;
            StateReaderRegistry readers = new();
            readers.Register(new StateDefinitionBinding("Owner", SchemaKind.ReferenceObject, 1, null,
                [new("Owner", 2, SchemaKind.ReferenceObject, 1, [new(1, Parameter)],
                    stateTypeDefinition: typeof(Owner2<>), stateParameters: [new(Parameter)])],
                historicalReaderFactory: (schema, context) => {
                    Assert.Equal(typeof(ObjectId), context.BindSchema(schema).GetValue(Parameter).StateType);
                    return new StateReaderBinding<Owner2<ObjectId>>(schema,
                        (ref BareValueReader payload) => { ownerReads++; return new(new ObjectId(payload.ReadVarUInt32())); },
                        static (ref BareValueReader payload, in Owner2<ObjectId> prior) => throw new InvalidOperationException(),
                        static (in Owner2<ObjectId> state, IStateReferenceVisitor visitor) => visitor.VisitObject(state.Value, RemoteType));
                }));
            DurableSchema availableTarget = omitOldTargetReader
                ? new(RemoteType, 2, new DurableFieldInfo(1, TypeTag.Byte)) : remote;
            readers.Register(new StateReaderBinding<byte>(availableTarget,
                (ref BareValueReader payload) => { targetReads++; return payload.ReadByte(); },
                static (ref BareValueReader payload, in byte prior) => throw new InvalidOperationException(),
                static (in byte state, IStateReferenceVisitor visitor) => { }));
            long schemaTail = DataBytes(file.Data);
            if (omitOldTargetReader) {
                Assert.Throws<InvalidDataException>(() => RevisionDecoder.Read(store, schemas, revision, readers));
                Assert.Equal(0, targetReads);
            } else {
                DecodedRevision decoded = RevisionDecoder.Read(store, schemas, revision, readers);
                Assert.Equal(new ObjectId(2), decoded.GetRequired(new ObjectId(1)).GetState<Owner2<ObjectId>>().Value);
                Assert.Equal((byte)37, decoded.GetRequired(new ObjectId(2)).GetState<byte>());
                Assert.Equal(1, targetReads);
            }
            Assert.Equal(1, ownerReads); // Nominal evidence works in both cases; exact target capability is independent.
            Assert.Equal(schemaTail, DataBytes(file.Data));
        } finally {
            string resolved = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(resolved), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("durable-graph-nominal-reader-", StringComparison.Ordinal)) {
                throw new InvalidOperationException("Refusing to delete outside the fixture's temporary directory.");
            }
            Directory.Delete(resolved, recursive: true);
        }
    }

    private static StateDefinitionBinding OwnerDefinition(string firstMethod = nameof(FirstGeneric)) {
        TypeExpr owner = TypeExpr.Named("Owner", RemoteType);
        StateUpgradeProvider first = new("Owner", 1, Method(firstMethod),
            firstMethod == nameof(FirstClosed) ? owner : null);
        return new("Owner", SchemaKind.ReferenceObject, 1, null, [
            new("Owner", 1, SchemaKind.ReferenceObject, 1, [], stateTypeDefinition: typeof(Owner1)),
            new("Owner", 2, SchemaKind.ReferenceObject, 1, [new(1, Parameter)],
                stateTypeDefinition: typeof(Owner2<>), stateParameters: [new(Parameter)]),
            new("Owner", 3, SchemaKind.ReferenceObject, 1, [new(1, Parameter), new(2, TypeExpr.Builtin(TypeTag.Int32))],
                stateTypeDefinition: typeof(Owner3<>), stateParameters: [new(Parameter)]),
        ], historicalReaderFactory: OwnerReader, upgrades: [first, new("Owner", 2, Method(nameof(Second)))]);
    }

    private static DurableSchema OwnerSchema(TypeExpr argument, int version) => new(TypeExpr.Named("Owner", argument), version,
        version == 1 ? [] : version == 2 ? [DurableFieldInfo.Reference(1, argument)] :
        [DurableFieldInfo.Reference(1, argument), new DurableFieldInfo(2, TypeTag.Int32)]);

    private static StateReaderBinding OwnerReader(DurableSchema schema, StateBindingContext context) {
        StateSchemaBinding bound = context.BindSchema(schema);
        Type type = schema.Version == 1 ? typeof(Owner1) : (schema.Version == 2 ? typeof(Owner2<>) : typeof(Owner3<>))
            .MakeGenericType(bound.GetValue(Parameter).StateType);
        return Method(nameof(Reader)).MakeGenericMethod(type).CreateDelegate<Func<DurableSchema, StateReaderBinding>>()(schema);
    }

    private static StateReaderBinding Reader<T>(DurableSchema schema) where T : unmanaged => new StateReaderBinding<T>(schema,
        static (ref BareValueReader reader) => throw new InvalidOperationException("Body reads are outside nominal binding."),
        static (ref BareValueReader reader, in T prior) => throw new InvalidOperationException("Body reads are outside nominal binding."),
        static (in T state, IStateReferenceVisitor visitor) => { });

    private static StateModelBinding RemoteModel<TDomain>(TypeExpr nominal) where TDomain : class, IDurableObject {
        // Deliberately newer, with a field unrelated to the owner's ObjectId slot.
        DurableSchema schema = new(nominal, 2, new DurableFieldInfo(1, TypeTag.Int32));
        CapturedStatePreparation<int> preparation = new(schema,
            static (in int state) => throw new InvalidOperationException("No target preparation."),
            static (in int prior, in int current) => throw new InvalidOperationException("No target preparation."));
        return new StateModelBinding<TDomain, int>(preparation, [Reader<int>(schema)],
            static _ => throw new InvalidOperationException("No target normalization."),
            static () => throw new InvalidOperationException("No target allocation."),
            static (TDomain domain, in int state, ObjectReadTable objects) => throw new InvalidOperationException("No target hydration."),
            static (_, _) => throw new InvalidOperationException("No target capture."),
            static (in int state, IStateReferenceVisitor visitor) => throw new InvalidOperationException("No target traversal."));
    }

    private static MethodInfo Method(string name) => typeof(CrossAssemblyNominalBindingTests)
        .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
    private static void FirstGeneric<T>(in Owner1 prior, out Owner2<T> next, UpgradeContext context) where T : unmanaged => next = new(default);
    private static void FirstClosed(in Owner1 prior, out Owner2<ObjectId> next, UpgradeContext context) => next = new(default);
    private static void Second<T>(in Owner2<T> prior, out Owner3<T> next, UpgradeContext context) where T : unmanaged => next = new(prior.Value, 2);
    private sealed class Remote : IDurableObject;
    private sealed class RemoteGeneric<T> : IDurableObject;
    private readonly record struct Owner1;
    private readonly record struct Owner2<T>(T Value) where T : unmanaged;
    private readonly record struct Owner3<T>(T Value, int Generation) where T : unmanaged;
}
