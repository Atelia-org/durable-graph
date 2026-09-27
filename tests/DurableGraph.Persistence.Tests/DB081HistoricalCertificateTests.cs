using System.Buffers;
using System.Reflection;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.RbfSegmentStore;

namespace Atelia.DurableGraph.Persistence.Tests;

/// <summary>Cold-path witness for the certificate boundary a future hot admission must preserve.</summary>
public sealed class DB081HistoricalCertificateTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"durable-db081-history-{Guid.NewGuid():N}");
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1000000, 1);
    private const string Owner = "DB081History";
    private const string Intermediate = "DB081HistoricalOnly";
    private static readonly DurableSchema Old = new(Owner, 1, new DurableFieldInfo(1, TypeTag.Int32));
    private static readonly DurableSchema Current = new(Owner, 3, new DurableFieldInfo(1, TypeTag.Int32));

    [Fact]
    public void CurrentStateDropsHistoricalOnlyCertificateWhileEventsPreserveRewriteAndDoNotRepopulateSlot() {
        using (Repository seed = Repository.CreateNew(_root, Models(current: false, new()),
            new() { NewStoreLayout = RbfSegmentStoreLayout.Flat })) {
            using BranchCheckout initial = seed.CreateBranch("main", new Root { Value = 17 }, NoRebase);
        }
        Counters counts = new();
        using Repository repository = Repository.OpenExisting(_root, Models(current: true, counts));
        GraphReadStatistics statistics = new();
        repository.RestorationStatistics = statistics;
        CheckpointAddress oldAddress = repository.GetHead("main");
        using BranchCheckout upgraded = repository.Checkout("main");
        PreparedStateRestoration oldEntry = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
        Assert.Equal(1, counts.Normalizes);
        Assert.Equal(17, Assert.IsType<Root>(upgraded.State).Value);
        Assert.True(Baseline(upgraded).Objects[upgraded.StateId!.Value].RequiresRewrite);

        using BranchCheckout other = repository.CreateBranch("other", new Root { Value = 44 }, NoRebase);
        using BranchCheckout otherFork = repository.Fork("other-fork", other.Head);
        PreparedStateRestoration otherEntry = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
        Assert.NotSame(oldEntry, otherEntry);
        NormalizedRevision originalBaseline = Baseline(upgraded);
        var beforeEvent = Counts(statistics);
        CheckpointAddress eventAddress = upgraded.CommitEvent(upgraded.State!, NoRebase);
        Assert.Same(otherEntry, repository.PreparedStateEntry);
        Assert.Equal(beforeEvent, Counts(statistics));
        Assert.Same(originalBaseline, Baseline(upgraded));
        Assert.True(Baseline(upgraded).Objects[upgraded.StateId!.Value].RequiresRewrite);

        int misses = statistics.PreparedStateMisses;
        using BranchCheckout eventFork = repository.Fork("event-fork", eventAddress);
        Assert.Equal(eventAddress, eventFork.Head);
        Assert.Equal(oldAddress.RevisionAddress, eventFork.StateRevisionAddress);
        Assert.Equal(misses + 1, statistics.PreparedStateMisses);
        Assert.Equal(2, counts.Normalizes);
        PreparedStateRestoration renewedOldEntry = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
        Assert.NotSame(oldEntry, renewedOldEntry);

        CheckpointAddress newAddress = upgraded.CommitState(NoRebase);
        StateRevision saved = Resources(repository).States.Read(newAddress.RevisionAddress);
        Assert.Equal(oldAddress.RevisionAddress, saved.ParentRevisionAddress);
        Assert.Equal(ObjectVersionKind.Base, Assert.Single(saved.LocalObjects).Kind);
        Assert.False(Baseline(upgraded).Objects[upgraded.StateId!.Value].RequiresRewrite);
        // DB-080 has no hot admission. Retaining this old entry is valid; installing
        // its historical certificate under newAddress would be an incorrect optimization.
        Assert.Same(renewedOldEntry, repository.PreparedStateEntry);
        Resources(repository).Schemas.Register(new DurableSchema(Intermediate, 5, SchemaKind.InlineValue,
            new DurableFieldInfo(1, TypeTag.Int64)));

        int normalized = counts.Normalizes;
        misses = statistics.PreparedStateMisses;
        using BranchCheckout currentCold = repository.Fork("current-cold", newAddress);
        Assert.Equal(17, Assert.IsType<Root>(currentCold.State).Value);
        Assert.Equal(newAddress.RevisionAddress, currentCold.StateRevisionAddress);
        Assert.Equal(misses + 1, statistics.PreparedStateMisses);
        Assert.Equal(normalized, counts.Normalizes);
        PreparedStateRestoration currentEntry = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
        Assert.NotSame(renewedOldEntry, currentEntry);
        Assert.Equal(newAddress, currentEntry.StateCheckpoint);
        int decoded = statistics.DecodedObjects, hits = statistics.PreparedStateHits;
        using BranchCheckout currentHit = repository.Fork("current-hit", newAddress);
        Assert.Equal(hits + 1, statistics.PreparedStateHits);
        Assert.Equal(decoded, statistics.DecodedObjects);
        Assert.Equal(normalized, counts.Normalizes);
        Assert.NotSame(currentCold.State, currentHit.State);
        Assert.Equal(17, Assert.IsType<Root>(currentHit.State).Value);

        int allocated = counts.Allocations;
        InvalidDataException conflict = Assert.Throws<InvalidDataException>(() => repository.Fork("old-rejected", oldAddress));
        Assert.Contains(Intermediate, conflict.Message);
        Assert.Equal(allocated, counts.Allocations);
        Assert.Same(currentEntry, repository.PreparedStateEntry);
        Assert.DoesNotContain("old-rejected", repository.ListBranches());
        Assert.False(repository.IsFaulted);
    }

    private static StateModelRegistry Models(bool current, Counters counts) {
        StateModelRegistry registry = new();
        registry.Register(new StateDefinitionBinding(Intermediate, SchemaKind.InlineValue, 0, null,
            [new(Intermediate, 5, SchemaKind.InlineValue, 0, [new(1, TypeExpr.Builtin(TypeTag.Int32))], stateTypeDefinition: typeof(int))],
            historicalValueFactory: static (schema, _) => new(new(1, TypeTag.InlineValue, inlineSchema: schema), typeof(int), typeof(Int32StateOps))));
        StateSchemaTemplate[] history = [
            new(Owner, 1, SchemaKind.ReferenceObject, 0, [new(1, TypeExpr.Builtin(TypeTag.Int32))], stateTypeDefinition: typeof(int)),
            new(Owner, 2, SchemaKind.ReferenceObject, 0, [new(1, TypeExpr.Named(Intermediate), 5)], stateTypeDefinition: typeof(int)),
            new(Owner, 3, SchemaKind.ReferenceObject, 0, [new(1, TypeExpr.Builtin(TypeTag.Int32))], stateTypeDefinition: typeof(int)),
        ];
        registry.Register(new StateDefinitionBinding(Owner, SchemaKind.ReferenceObject, 0, typeof(Root),
            current ? history : [history[0]], currentModelFactory: (_, context) => {
                DurableSchema schema = current ? Current : Old;
                return new StateModelBinding<Root, int>(new(schema, Body,
                    static (in int prior, in int next) => new(prior != next, Body(next).Body)),
                    [context.ResolveReader(schema)], row => { counts.Normalizes++; return context.Normalize<int>(row, schema); },
                    () => { counts.Allocations++; return new(); },
                    static (Root root, in int state, ObjectReadTable _) => root.Value = state,
                    static (root, _) => root.Value, static (in int _, IStateReferenceVisitor _) => { },
                    sourceReaderResolver: context.ResolveReader);
            }, historicalReaderFactory: static (schema, context) => {
                if (schema.Version == 2) { context.ResolveStoredValue(schema.Fields[0]); }
                return new StateReaderBinding<int>(schema, static (ref BinaryPayloadReader reader) => reader.ReadInt32(),
                    static (ref BinaryPayloadReader reader, in int _) => reader.ReadInt32(),
                    static (in int _, IStateReferenceVisitor _) => { });
            }, upgrades: current ? [new(Owner, 1, Method(nameof(Keep))), new(Owner, 2, Method(nameof(Keep)))] : []));
        return registry;
    }

    private static PreparedBaseBody Body(in int value) {
        ArrayBufferWriter<byte> bytes = new();
        BinaryPayloadWriter writer = new(bytes);
        writer.WriteInt32(value);
        return new(bytes.WrittenSpan);
    }
    private static void Keep(in int prior, out int next, UpgradeContext _) => next = prior;
    private static MethodInfo Method(string name) => typeof(DB081HistoricalCertificateTests).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
    private static GraphResources Resources(Repository repository) => Field<GraphResources>(repository, "_resources");
    private static NormalizedRevision Baseline(BranchCheckout branch) => Field<NormalizedRevision>(branch.Workspace, "_baseline");
    private static (int Decoded, int Normalized, int Prepared, int Allocated, int Hydrated, int Hits, int Misses) Counts(GraphReadStatistics stats) =>
        (stats.DecodedObjects, stats.NormalizedObjects, stats.PreparedGraphs, stats.AllocatedObjects, stats.HydratedObjects,
            stats.PreparedStateHits, stats.PreparedStateMisses);
    private sealed class Root : IDurableObject { internal int Value; }
    private sealed class Counters { internal int Normalizes; internal int Allocations; }
    public void Dispose() => SharedReadModel.DeleteFixture(_root, "durable-db081-history-");
}
