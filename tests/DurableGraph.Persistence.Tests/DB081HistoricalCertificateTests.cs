using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;
using System.Buffers;
using System.Reflection;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;

using Atelia.FrameStore;

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
        using (Repository seed = Repository.CreateNew(_root, Models(current: false, new()))) {
            seed.CreateRootBranchFromState("main", new Root { Value = 17 }, parameters: NoRebase);
        }
        Counters counts = new();
        using Repository repository = Repository.OpenExisting(_root, Models(current: true, counts));
        GraphReadStatistics statistics = new();
        repository.RestorationStatistics = statistics;
        CommitId oldAddress = repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id;
        using WorkingCopy upgraded = repository.OpenWorkingCopy(BranchNamed(repository, "main"));
        PreparedStateRestoration oldEntry = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
        Assert.Equal(1, counts.Normalizes);
        Assert.Equal(17, Assert.IsType<Root>(upgraded.State).Value);
        Assert.True(Baseline(upgraded).Objects[upgraded.StateId!.Value].RequiresRewrite);

        // An Event-only opening followed by State capture leaves this State unread;
        // creating a State root and opening it would already earn a read certificate.
        IDurableObject otherInitial = new Root { Value = 44 };
        using WorkingCopy other = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("other", otherInitial, parameters: NoRebase).Id);
        other.CommitState(otherInitial, parameters: NoRebase);
        using WorkingCopy otherFork = repository.OpenWorkingCopy(repository.ForkBranch(other.BaseCommitId, "other-fork").Id);
        PreparedStateRestoration otherEntry = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
        Assert.NotSame(oldEntry, otherEntry);
        NormalizedGraphState originalBaseline = Baseline(upgraded);
        var beforeEvent = Counts(statistics);
        CommitId eventAddress = upgraded.CommitEvent(upgraded.State!, parameters: NoRebase).Id;
        Assert.Same(otherEntry, repository.PreparedStateEntry);
        Assert.Equal(beforeEvent, Counts(statistics));
        Assert.Same(originalBaseline, Baseline(upgraded));
        Assert.True(Baseline(upgraded).Objects[upgraded.StateId!.Value].RequiresRewrite);

        int misses = statistics.PreparedStateMisses;
        using WorkingCopy eventFork = repository.OpenWorkingCopy(repository.ForkBranch(eventAddress, "event-fork").Id);
        Assert.Equal(eventAddress, eventFork.BaseCommitId);
        Assert.Equal(Describe(repository, oldAddress).Address, eventFork.StateRevisionAddress);
        Assert.Equal(misses + 1, statistics.PreparedStateMisses);
        Assert.Equal(2, counts.Normalizes);
        PreparedStateRestoration renewedOldEntry = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
        Assert.NotSame(oldEntry, renewedOldEntry);

        CommitId newAddress = upgraded.CommitState(parameters: NoRebase).Id;
        StoredGraphRevision saved = Snapshot(repository, newAddress);
        Assert.Equal(Describe(repository, oldAddress).Address, saved.SourceGraph);
        Assert.Equal(ObjectVersionKind.Base, Assert.Single(repository.GraphStore.ReadBatch(saved.OwnedBatch!.Value).Records).Kind);
        Assert.False(Baseline(upgraded).Objects[upgraded.StateId!.Value].RequiresRewrite);
        // DB-080 has no hot admission. Retaining this old entry is valid; installing
        // its historical certificate under newAddress would be an incorrect optimization.
        Assert.Same(renewedOldEntry, repository.PreparedStateEntry);
        repository.CommitBranches(
            [upgraded.CreateEventCommitRequest(upgraded.State!, parameters: NoRebase)],
            [new DurableSchema(Intermediate, 5, SchemaKind.InlineValue,
                new DurableFieldInfo(1, TypeTag.Int64))]);
        CommitId catalogEvent = upgraded.BaseCommitId;
        var configuration = Field<StateModelRegistry.FrozenConfiguration>(repository, "_modelConfiguration");
        var wrongEnvironment = DecodedRevision.SchemaViewBindingEnvironment.Load(repository.GraphStore, configuration,
            repository.GraphStore.ReadMetadata(Describe(repository, catalogEvent).Address).SchemaRoot!.Value);
        InvalidDataException conflict = Assert.Throws<InvalidDataException>(() => renewedOldEntry.Requirements.Validate(wrongEnvironment.Models));
        Assert.Contains(Intermediate, conflict.Message);

        int normalized = counts.Normalizes;
        misses = statistics.PreparedStateMisses;
        using WorkingCopy currentCold = repository.OpenWorkingCopy(repository.ForkBranch(newAddress, "current-cold").Id);
        Assert.Equal(17, Assert.IsType<Root>(currentCold.State).Value);
        Assert.Equal(Describe(repository, newAddress).Address, currentCold.StateRevisionAddress);
        Assert.Equal(misses + 1, statistics.PreparedStateMisses);
        Assert.Equal(normalized, counts.Normalizes);
        PreparedStateRestoration currentEntry = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
        Assert.NotSame(renewedOldEntry, currentEntry);
        Assert.Equal(newAddress, new CommitId(currentEntry.StateCheckpoint.Id));
        int decoded = statistics.DecodedObjects, hits = statistics.PreparedStateHits;
        using WorkingCopy currentHit = repository.OpenWorkingCopy(repository.ForkBranch(newAddress, "current-hit").Id);
        Assert.Equal(hits + 1, statistics.PreparedStateHits);
        Assert.Equal(decoded, statistics.DecodedObjects);
        Assert.Equal(normalized, counts.Normalizes);
        Assert.NotSame(currentCold.State, currentHit.State);
        Assert.Equal(17, Assert.IsType<Root>(currentHit.State).Value);

        // Current stored DTOs no longer depend on the intermediate Upgrade layout.
        // That smaller certificate succeeds against the real later S, while a
        // historical read still uses its own immutable S and remains valid.
        Assert.True(renewedOldEntry.SchemaRequirementCount > currentEntry.SchemaRequirementCount);
        currentEntry.Requirements.Validate(wrongEnvironment.Models);
        using WorkingCopy oldAgain = repository.OpenWorkingCopy(repository.ForkBranch(oldAddress, "old-again").Id);
        Assert.Equal(17, Assert.IsType<Root>(oldAgain.State).Value);
        Assert.True(Baseline(oldAgain).Objects[oldAgain.StateId!.Value].RequiresRewrite);
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
                return new StateReaderBinding<int>(schema, static (ref BareValueReader reader) => reader.ReadVarInt32(),
                    static (ref BareValueReader reader, in int _) => reader.ReadVarInt32(),
                    static (in int _, IStateReferenceVisitor _) => { });
            }, upgrades: current ? [new(Owner, 1, Method(nameof(Keep))), new(Owner, 2, Method(nameof(Keep)))] : []));
        return registry;
    }

    private static PreparedBaseBody Body(in int value) {
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        writer.WriteVarInt32(value);
        return new(bytes.WrittenSpan);
    }
    private static void Keep(in int prior, out int next, UpgradeContext _) => next = prior;
    private static MethodInfo Method(string name) => typeof(DB081HistoricalCertificateTests).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
    private static NormalizedGraphState Baseline(WorkingCopy branch) => branch.Baseline!;
    private static (int Decoded, int Normalized, int Prepared, int Allocated, int Hydrated, int Hits, int Misses) Counts(GraphReadStatistics stats) =>
        (stats.DecodedObjects, stats.NormalizedObjects, stats.PreparedGraphs, stats.AllocatedObjects, stats.HydratedObjects,
            stats.PreparedStateHits, stats.PreparedStateMisses);
    private sealed class Root : IDurableObject { internal int Value; }
    private sealed class Counters { internal int Normalizes; internal int Allocations; }
    public void Dispose() => SharedReadModel.DeleteFixture(_root, "durable-db081-history-");
}
