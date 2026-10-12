using System.Buffers;
using System.Reflection;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class DB080MaterializationRequirementTests : IDisposable {
    private const string Prefix = "durable-db080-materialization-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirstMaterializationStandardChecksBecomePartOfResidentCertificate(bool duringAllocate) {
        const string ownerId = "DB080MaterializationOwner", dependencyId = "DB080MaterializationDependency";
        DurableSchema owner = new(ownerId, 1, new DurableFieldInfo(1, TypeTag.Int32));
        DurableSchema dependency = new(dependencyId, 1, SchemaKind.InlineValue,
            new DurableFieldInfo(1, TypeTag.Int32));
        DurableSchema wrongDependency = new(dependencyId, 1, SchemaKind.InlineValue,
            new DurableFieldInfo(1, TypeTag.Int64));
        int allocations = 0, hydrations = 0;
        StateModelRegistry models = new();
        models.Register(new StateDefinitionBinding(dependencyId, SchemaKind.InlineValue, 0, null,
            [new(dependencyId, 1, SchemaKind.InlineValue, 0, [new(1, TypeExpr.Builtin(TypeTag.Int32))])]));
        models.Register(new StateDefinitionBinding(ownerId, SchemaKind.ReferenceObject, 0, typeof(Root),
            [new(ownerId, 1, SchemaKind.ReferenceObject, 0,
                [new(1, TypeExpr.Builtin(TypeTag.Int32))], stateTypeDefinition: typeof(int))],
            currentModelFactory: (_, context) => new StateModelBinding<Root, int>(new(owner, Body,
                static (in int prior, in int next) => new(prior != next, Body(next).Body)),
                [context.ResolveReader(owner)], static row => row.GetState<int>(), () => {
                    allocations++;
                    if (duringAllocate) { context.BindSchema(dependency); }
                    return new();
                }, (Root root, in int state, ObjectReadTable _) => {
                    hydrations++;
                    if (!duringAllocate) { context.BindSchema(dependency); }
                    root.Value = state;
                }, static (root, _) => root.Value, static (in int _, IStateReferenceVisitor _) => { }),
            historicalReaderFactory: (_, _) => new StateReaderBinding<int>(owner,
                static (ref BareValueReader reader) => reader.ReadVarInt32(),
                static (ref BareValueReader reader, in int _) => reader.ReadVarInt32(),
                static (in int _, IStateReferenceVisitor _) => { })));
        using Repository repository = Repository.CreateNew(_path, models);
        using BranchCheckout main = repository.CreateBranch("main", new Root { Value = 7 });
        CheckpointAddress original = main.Head;
        using (BranchCheckout first = repository.Fork("first", original)) {
            Assert.Equal(7, Assert.IsType<Root>(first.State).Value);
        }
        Assert.Equal(1, allocations);
        Assert.Equal(1, hydrations);
        PreparedStateRestoration prepared = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
        Assert.Equal(2, prepared.SchemaRequirementCount); // Owner plus Allocate/Hydrate-only dependency.

        main.Catalog.Set(wrongDependency); // The key is absent from S0; a later S may declare it.
        CheckpointAddress catalogEvent = main.CommitEvent(main.State!);
        var configuration = (StateModelRegistry.FrozenConfiguration)typeof(Repository)
            .GetField("_splitConfiguration", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(repository)!;
        var wrongEnvironment = DecodedRevision.SplitBindingEnvironment.Load(repository.MetaBootstrapStore,
            configuration, repository.MetaBootstrapStore.ReadMetadata(catalogEvent.Address).SchemaRoot!.Value);
        Assert.Throws<InvalidDataException>(() => prepared.Requirements.Validate(wrongEnvironment.Models));
        GraphReadStatistics statistics = new();
        repository.RestorationStatistics = statistics;
        using BranchCheckout oldHit = repository.Fork("old-hit", original);
        Assert.Equal(1, statistics.PreparedStateHits);
        Assert.Equal(0, statistics.DecodedObjects);
        Assert.Equal(0, statistics.NormalizedObjects);
        Assert.Equal(2, allocations);
        Assert.Equal(2, hydrations);
        Assert.Same(prepared, repository.PreparedStateEntry);

        // The new State selects actual S1. Neither successful capture nor the old
        // certificate proves its first materialization's callback requirements.
        CheckpointAddress newer = main.CommitState();
        int allocated = allocations, hydrated = hydrations;
        Assert.Throws<InvalidDataException>(() => repository.Fork("rejected", newer));
        Assert.Equal(allocated + 1, allocations);
        Assert.Equal(hydrated + (duringAllocate ? 0 : 1), hydrations);
        Assert.Same(prepared, repository.PreparedStateEntry);
        Assert.DoesNotContain("rejected", repository.ListBranches());
        Assert.False(repository.IsFaulted);
    }

    private static PreparedBaseBody Body(in int value) {
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        writer.WriteVarInt32(value);
        return new(bytes.WrittenSpan);
    }
    private sealed class Root : IDurableObject { internal int Value; }
    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
