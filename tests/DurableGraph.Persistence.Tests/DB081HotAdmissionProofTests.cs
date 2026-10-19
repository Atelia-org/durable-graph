using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;
using System.Buffers;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;

using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

/// <summary>Executable counterexamples to deriving a new revision's read proof from successful capture or factory closure.</summary>
public sealed class DB081HotAdmissionProofTests : IDisposable {
    private const string Prefix = "durable-db081-admission-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1000000, 1);
    private static readonly DurableSchema Owner = new("DB081ReaderOwner", 1,
        new DurableFieldInfo(1, TypeTag.Int32), new DurableFieldInfo(2, TypeTag.Int64),
        new DurableFieldInfo(3, TypeTag.Int64), new DurableFieldInfo(4, TypeTag.Int64), new DurableFieldInfo(5, TypeTag.Int64));
    private static readonly DurableSchema Dependency = new("DB081ReadOnlyDependency", 1, SchemaKind.InlineValue,
        new DurableFieldInfo(1, TypeTag.Int32));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void SuccessfulCommitAndClosedReaderDoNotProveUnexecutedBodyDependencies(bool preclose, bool delta) {
        using Scenario scenario = Seed(preclose, delta, conflict: true);
        Repository repository = scenario.Repository;
        var previous = repository.PreparedStateEntry;
        Assert.NotNull(previous);
        GraphReadStatistics statistics = new();
        repository.RestorationStatistics = statistics;
        WorkingCopy? delivered = null;

        InvalidDataException failure = Assert.Throws<InvalidDataException>(() => delivered = repository.OpenWorkingCopy(repository.ForkBranch(scenario.Selected, "rejected").Id));

        Assert.Contains(Dependency.SchemaId, failure.Message);
        Assert.Null(delivered);
        Assert.Equal(1, scenario.Counts.BaseReads);
        Assert.Equal(delta ? 1 : 0, scenario.Counts.DeltaReads);
        Assert.Equal(1, scenario.Counts.DependencyChecks);
        Assert.Equal(0, scenario.Counts.Allocations);
        Assert.Equal(0, scenario.Counts.Hydrations);
        Assert.Equal(0, statistics.PreparedStateHits);
        Assert.Equal(1, statistics.PreparedStateMisses);
        Assert.Equal(0, statistics.AllocatedObjects);
        Assert.Equal(0, statistics.HydratedObjects);
        Assert.Same(previous, repository.PreparedStateEntry);
        Assert.Contains(repository.ListBranches(), branch => branch.DisplayLabel == "rejected");
        Assert.Equal(scenario.Selected, repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id);
        Assert.False(repository.IsFaulted);
        // The failed request neither poisons a different State certificate nor faults the repository.
        repository.RestorationStatistics = new();
        using WorkingCopy unrelated = repository.OpenWorkingCopy(repository.ForkBranch(scenario.Warm.BaseCommitId, "unrelated-again").Id);
        Assert.Equal(1, repository.RestorationStatistics.PreparedStateHits);
        Assert.Equal((byte)31, Assert.IsType<SharedReadModel.Node>(unrelated.State).Value);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void SameProviderRestoresCommittedValuesAndEarnsReuseOnlyAfterColdPreparation(bool preclose, bool delta) {
        using Scenario scenario = Seed(preclose, delta, conflict: false);
        Repository repository = scenario.Repository;
        int committed = delta ? 8 : 7;
        // The persisted candidate must not follow later edits to the original CLR instance.
        Assert.IsType<Root>(scenario.Main.State).Value = 77;
        repository.RestorationStatistics = new();
        using WorkingCopy first = repository.OpenWorkingCopy(repository.ForkBranch(scenario.Selected, "first").Id);
        Assert.Equal(committed, Assert.IsType<Root>(first.State).Value);
        Assert.Equal(1, repository.RestorationStatistics.PreparedStateMisses);
        Assert.Equal(0, repository.RestorationStatistics.PreparedStateHits);
        Assert.Equal(1, scenario.Counts.BaseReads);
        Assert.Equal(delta ? 1 : 0, scenario.Counts.DeltaReads);
        Assert.Equal(1, scenario.Counts.DependencyChecks);
        Assert.Equal(1, scenario.Counts.Allocations);
        Assert.Equal(1, scenario.Counts.Hydrations);
        PreparedStateRestoration prepared = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
        Assert.Equal(scenario.Selected, new CommitId(prepared.StateCheckpoint.Id));

        Assert.IsType<Root>(first.State).Value = 91;
        repository.RestorationStatistics = new();
        using WorkingCopy second = repository.OpenWorkingCopy(repository.ForkBranch(scenario.Selected, "second").Id);
        Assert.Equal(committed, Assert.IsType<Root>(second.State).Value);
        Assert.NotSame(first.State, second.State);
        Assert.NotSame(scenario.Main.State, second.State);
        Assert.Equal(1, repository.RestorationStatistics.PreparedStateHits);
        Assert.Equal(0, repository.RestorationStatistics.DecodedObjects);
        Assert.Equal(0, repository.RestorationStatistics.NormalizedObjects);
        Assert.Equal(1, scenario.Counts.BaseReads);
        Assert.Equal(delta ? 1 : 0, scenario.Counts.DeltaReads);
        Assert.Equal(1, scenario.Counts.DependencyChecks);
        Assert.Equal(2, scenario.Counts.Allocations);
        Assert.Equal(2, scenario.Counts.Hydrations);
        Assert.Same(prepared, repository.PreparedStateEntry);

        CommitId saved = first.CommitState(parameters: NoRebase).Id;
        Assert.Equal(Describe(repository, scenario.Selected).Address, Snapshot(repository, saved).SourceGraph);
        Assert.Equal(Describe(repository, scenario.Selected).RootId, Describe(repository, saved).RootId);
        Assert.Equal(91, Assert.IsType<Root>(repository.LoadState(saved)).Value);
        Assert.Equal(committed, Assert.IsType<Root>(second.State).Value);
        Assert.Equal(77, Assert.IsType<Root>(scenario.Main.State).Value);
    }

    [Fact]
    public void PriorSuccessfulReadOfSameReaderDoesNotProveNewValuesConditionalDependency() {
        Counters counts = new();
        using Repository repository = Repository.CreateNew(_path, Models(DependencyMode.BaseForSeven, counts));
        // An Event-only opening followed by State capture leaves this State unread;
        // creating a State root and opening it would already earn a read certificate.
        IDurableObject mainInitial = new Root { Value = 1 };
        using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("main", new SharedReadModel.Node(), parameters: NoRebase).Id);
        main.CommitState(mainInitial, parameters: NoRebase);
        using WorkingCopy first = repository.OpenWorkingCopy(repository.ForkBranch(main.BaseCommitId, "first").Id);
        Assert.Equal(1, counts.BaseReads);
        Assert.Equal(0, counts.DependencyChecks);
        PreparedStateRestoration old = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
        Assert.IsType<Root>(main.State).Value = 7;
        repository.CommitBranches([main.CreateStateCommitRequest(parameters: NoRebase)], [WrongDependency]);
        CommitId newer = main.BaseCommitId;
        Assert.Equal(1, counts.BaseReads);
        Assert.Equal(0, counts.DeltaReads);
        Assert.Equal(0, counts.DependencyChecks);
        Assert.Equal(1, counts.Allocations);
        Assert.Equal(1, counts.Hydrations);
        Assert.Same(old, repository.PreparedStateEntry);
        // Request the new current DTO through a Base, so the Value == 7 dependency
        // is witnessed in ReadBase rather than accidentally skipped by sparse Delta replay.
        // BaseForSeven's preparation deliberately emits a full-body Delta of Base size;
        // the normal cost selector consequently stores a Base.
        Assert.Equal(ObjectVersionKind.Base, Assert.Single(Records(repository, newer)).Kind);
        repository.RestorationStatistics = new();
        Assert.Throws<InvalidDataException>(() => repository.OpenWorkingCopy(repository.ForkBranch(newer, "rejected").Id));
        Assert.Equal(2, counts.BaseReads);
        Assert.Equal(1, counts.DependencyChecks);
        Assert.Equal(1, counts.Allocations);
        Assert.Equal(1, counts.Hydrations);
        Assert.Same(old, repository.PreparedStateEntry);
        Assert.Contains(repository.ListBranches(), branch => branch.DisplayLabel == "rejected");
        Assert.False(repository.IsFaulted);
        repository.RestorationStatistics = new();
        using WorkingCopy older = repository.OpenWorkingCopy(repository.ForkBranch(new CommitId(old.StateCheckpoint.Id), "older").Id);
        Assert.Equal(1, repository.RestorationStatistics.PreparedStateHits);
        Assert.Equal(1, Assert.IsType<Root>(older.State).Value);
    }

    private Scenario Seed(bool preclose, bool delta, bool conflict) {
        Counters counts = new();
        Repository repository = Repository.CreateNew(_path, Models(
            delta ? DependencyMode.DeltaAlways : DependencyMode.BaseAlways, counts));
        try {
            IDurableObject unrelatedInitial = new SharedReadModel.Node { Value = 31 };
            using WorkingCopy unrelated = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("unrelated", unrelatedInitial, parameters: NoRebase).Id);
            unrelated.CommitState(unrelatedInitial, parameters: NoRebase);
            WorkingCopy warm = repository.OpenWorkingCopy(repository.ForkBranch(unrelated.BaseCommitId, "warm").Id);
            var previous = repository.PreparedStateEntry;
            Assert.NotNull(previous);
            if (conflict) {
                repository.CommitBranches(
                    [warm.CreateEventCommitRequest(warm.State!, parameters: NoRebase)], [WrongDependency]);
            }
            IDurableObject mainInitial = new Root { Value = 7 };
            WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("main", new SharedReadModel.Node(), parameters: NoRebase).Id);
            main.CommitState(mainInitial, parameters: NoRebase);
            // This is the actual checkout's frozen execution environment. Explicit
            // preclosure still does not execute ReadBase/ReadDelta's body checks.
            if (preclose) { main.Models.ResolveReader(Owner); }
            Assert.Equal(1, counts.ReaderFactories);
            Assert.Equal(0, counts.DependencyChecks);
            CommitId selected = main.BaseCommitId;
            if (delta) {
                Assert.IsType<Root>(main.State).Value = 8;
                selected = main.CommitState(parameters: NoRebase).Id;
                Assert.Equal(ObjectVersionKind.Delta, Assert.Single(Records(repository, selected)).Kind);
            }
            Assert.Equal(delta ? 2 : 1, counts.Captures);
            Assert.Equal(0, counts.BaseReads);
            Assert.Equal(0, counts.DeltaReads);
            Assert.Equal(0, counts.DependencyChecks);
            Assert.Equal(0, counts.Allocations);
            Assert.Equal(0, counts.Hydrations);
            Assert.Same(previous, repository.PreparedStateEntry);
            Assert.Equal(selected, repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id);
            Assert.False(repository.IsFaulted);
            return new(repository, main, warm, selected, counts);
        } catch {
            repository.Dispose();
            throw;
        }
    }

    private static StateModelRegistry Models(DependencyMode mode, Counters counts) {
        StateModelRegistry registry = SharedReadModel.Models();
        registry.Register(new StateDefinitionBinding(Dependency.SchemaId, SchemaKind.InlineValue, 0, null,
            [new(Dependency.SchemaId, 1, SchemaKind.InlineValue, 0, [new(1, TypeExpr.Builtin(TypeTag.Int32))])]));
        StateReaderBinding Reader(StateBindingContext context) {
            counts.ReaderFactories++;
            return new StateReaderBinding<State>(Owner,
                (ref BareValueReader input) => {
                    counts.BaseReads++;
                    State state = Read(ref input);
                    if (mode == DependencyMode.BaseAlways || mode == DependencyMode.BaseForSeven && state.Value == 7) {
                        counts.DependencyChecks++;
                        context.BindSchema(Dependency);
                    }
                    return state;
                }, (ref BareValueReader input, in State prior) => {
                    counts.DeltaReads++;
                    State state = mode == DependencyMode.BaseForSeven ? Read(ref input) : prior with { Value = input.ReadVarInt32() };
                    if (mode == DependencyMode.DeltaAlways) {
                        counts.DependencyChecks++;
                        context.BindSchema(Dependency);
                    }
                    return state;
                }, Visit);
        }
        StateModelBinding Model(StateReaderBinding reader) => new StateModelBinding<Root, State>(
            new(Owner, Base, (in State prior, in State next) => new(prior != next,
                mode == DependencyMode.BaseForSeven ? Base(next).Body : ValueBody(next.Value))),
            [reader], static row => row.GetState<State>(),
            () => { counts.Allocations++; return new Root(); },
            (Root root, in State state, ObjectReadTable _) => { counts.Hydrations++; root.Value = state.Value; },
            (root, _) => { counts.Captures++; return new(root.Value, long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue); }, Visit);
        registry.Register(new StateDefinitionBinding(Owner.SchemaId, SchemaKind.ReferenceObject, 0, typeof(Root),
                [new(Owner.SchemaId, 1, SchemaKind.ReferenceObject, 0,
                    [new(1, TypeExpr.Builtin(TypeTag.Int32)), new(2, TypeExpr.Builtin(TypeTag.Int64)),
                        new(3, TypeExpr.Builtin(TypeTag.Int64)), new(4, TypeExpr.Builtin(TypeTag.Int64)), new(5, TypeExpr.Builtin(TypeTag.Int64))],
                    stateTypeDefinition: typeof(State))],
                currentModelFactory: (_, context) => Model(context.ResolveReader(Owner)),
                historicalReaderFactory: (_, context) => Reader(context)));
        return registry;
    }

    private static void Visit(in State _, IStateReferenceVisitor __) { }
    private static State Read(ref BareValueReader input) => new(input.ReadVarInt32(), input.ReadVarInt64(), input.ReadVarInt64(), input.ReadVarInt64(), input.ReadVarInt64());
    private static PreparedBaseBody Base(in State state) {
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter output = new(bytes);
        output.WriteVarInt32(state.Value);
        output.WriteVarInt64(state.A);
        output.WriteVarInt64(state.B);
        output.WriteVarInt64(state.C);
        output.WriteVarInt64(state.D);
        return new(bytes.WrittenSpan);
    }
    private static byte[] ValueBody(int value) {
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter output = new(bytes);
        output.WriteVarInt32(value);
        return bytes.WrittenSpan.ToArray();
    }
    private static readonly DurableSchema WrongDependency = new(Dependency.SchemaId, 1, SchemaKind.InlineValue,
        new DurableFieldInfo(1, TypeTag.Int64));
    private static IReadOnlyList<ObjectVersionRecord> Records(Repository repository, CommitId address) {
        StoredGraphRevision graph = Snapshot(repository, address);
        return graph.OwnedBatch is { } batch ? repository.GraphStore.ReadBatch(batch).Records : [];
    }
    private enum DependencyMode { BaseAlways, DeltaAlways, BaseForSeven }
    private sealed class Root : IDurableObject { internal int Value; }
    private readonly record struct State(int Value, long A, long B, long C, long D);
    private sealed class Counters {
        internal int ReaderFactories, Captures, BaseReads, DeltaReads, DependencyChecks, Allocations, Hydrations;
    }
    private sealed record Scenario(Repository Repository, WorkingCopy Main, WorkingCopy Warm,
        CommitId Selected, Counters Counts) : IDisposable {
        public void Dispose() {
            Main.Dispose();
            Warm.Dispose();
            Repository.Dispose();
        }
    }
    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
