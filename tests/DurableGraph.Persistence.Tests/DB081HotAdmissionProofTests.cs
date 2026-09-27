using System.Buffers;
using System.Reflection;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.RbfSegmentStore;

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
    public void SuccessfulCommitAndClosedReaderDoNotProveUnexecutedBodyDependencies(bool family, bool delta) {
        using Scenario scenario = Seed(family, delta);
        Repository repository = scenario.Repository;
        var previous = repository.PreparedStateEntry;
        Assert.NotNull(previous);
        RegisterConflict(repository);
        GraphReadStatistics statistics = new();
        repository.RestorationStatistics = statistics;
        BranchCheckout? delivered = null;

        InvalidDataException failure = Assert.Throws<InvalidDataException>(() => delivered = repository.Fork("rejected", scenario.Selected));

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
        Assert.DoesNotContain("rejected", repository.ListBranches());
        Assert.Equal(scenario.Selected, repository.GetHead("main"));
        Assert.False(repository.IsFaulted);
        // The failed request neither poisons a different State certificate nor faults the repository.
        repository.RestorationStatistics = new();
        using BranchCheckout unrelated = repository.Fork("unrelated-again", scenario.Warm.Head);
        Assert.Equal(1, repository.RestorationStatistics.PreparedStateHits);
        Assert.Equal((byte)31, Assert.IsType<SharedReadModel.Node>(unrelated.State).Value);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void SameProviderRestoresCommittedValuesAndEarnsReuseOnlyAfterColdPreparation(bool family, bool delta) {
        using Scenario scenario = Seed(family, delta);
        Repository repository = scenario.Repository;
        int committed = delta ? 8 : 7;
        // The persisted candidate must not follow later edits to the original CLR instance.
        Assert.IsType<Root>(scenario.Main.State).Value = 77;
        repository.RestorationStatistics = new();
        using BranchCheckout first = repository.Fork("first", scenario.Selected);
        Assert.Equal(committed, Assert.IsType<Root>(first.State).Value);
        Assert.Equal(1, repository.RestorationStatistics.PreparedStateMisses);
        Assert.Equal(0, repository.RestorationStatistics.PreparedStateHits);
        Assert.Equal(1, scenario.Counts.BaseReads);
        Assert.Equal(delta ? 1 : 0, scenario.Counts.DeltaReads);
        Assert.Equal(1, scenario.Counts.DependencyChecks);
        Assert.Equal(1, scenario.Counts.Allocations);
        Assert.Equal(1, scenario.Counts.Hydrations);
        PreparedStateRestoration prepared = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
        Assert.Equal(scenario.Selected, prepared.StateCheckpoint);

        Assert.IsType<Root>(first.State).Value = 91;
        repository.RestorationStatistics = new();
        using BranchCheckout second = repository.Fork("second", scenario.Selected);
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

        CheckpointAddress saved = first.CommitState(NoRebase);
        Assert.Equal(scenario.Selected.Address, saved.Parent);
        Assert.Equal(scenario.Selected.RootId, saved.RootId);
        Assert.Equal(91, Assert.IsType<Root>(repository.ReadState(saved)).Value);
        Assert.Equal(committed, Assert.IsType<Root>(second.State).Value);
        Assert.Equal(77, Assert.IsType<Root>(scenario.Main.State).Value);
    }

    [Fact]
    public void PriorSuccessfulReadOfSameReaderDoesNotProveNewValuesConditionalDependency() {
        Counters counts = new();
        using Repository repository = Repository.CreateNew(_path, Models(false, DependencyMode.BaseForSeven, counts),
            new() { NewStoreLayout = RbfSegmentStoreLayout.Flat });
        counts.Context = Field<StateModelSnapshot>(repository, "_models");
        using BranchCheckout main = repository.CreateBranch("main", new Root { Value = 1 }, NoRebase);
        using BranchCheckout first = repository.Fork("first", main.Head);
        Assert.Equal(1, counts.BaseReads);
        Assert.Equal(0, counts.DependencyChecks);
        PreparedStateRestoration old = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
        Assert.IsType<Root>(main.State).Value = 7;
        CheckpointAddress newer = main.CommitState(NoRebase);
        Assert.Equal(1, counts.BaseReads);
        Assert.Equal(0, counts.DeltaReads);
        Assert.Equal(0, counts.DependencyChecks);
        Assert.Equal(1, counts.Allocations);
        Assert.Equal(1, counts.Hydrations);
        Assert.Same(old, repository.PreparedStateEntry);
        RegisterConflict(repository);
        // Request the new current DTO through a Base, so the Value == 7 dependency
        // is witnessed in ReadBase rather than accidentally skipped by sparse Delta replay.
        // BaseForSeven's preparation deliberately emits a full-body Delta of Base size;
        // the normal cost selector consequently stores a Base.
        Assert.Equal(ObjectVersionKind.Base, Assert.Single(Resources(repository).States.Read(newer.RevisionAddress).LocalObjects).Kind);
        repository.RestorationStatistics = new();
        Assert.Throws<InvalidDataException>(() => repository.Fork("rejected", newer));
        Assert.Equal(2, counts.BaseReads);
        Assert.Equal(1, counts.DependencyChecks);
        Assert.Equal(1, counts.Allocations);
        Assert.Equal(1, counts.Hydrations);
        Assert.Same(old, repository.PreparedStateEntry);
        Assert.DoesNotContain("rejected", repository.ListBranches());
        Assert.False(repository.IsFaulted);
        repository.RestorationStatistics = new();
        using BranchCheckout older = repository.Fork("older", old.StateCheckpoint);
        Assert.Equal(1, repository.RestorationStatistics.PreparedStateHits);
        Assert.Equal(1, Assert.IsType<Root>(older.State).Value);
    }

    private Scenario Seed(bool family, bool delta) {
        Counters counts = new();
        Repository repository = Repository.CreateNew(_path, Models(family,
            delta ? DependencyMode.DeltaAlways : DependencyMode.BaseAlways, counts),
            new() { NewStoreLayout = RbfSegmentStoreLayout.Flat });
        try {
            counts.Context = Field<StateModelSnapshot>(repository, "_models");
            // Explicit preclosure must not be mistaken for executing the reader body.
            counts.Context.ResolveReader(Owner);
            Assert.Equal(1, counts.ReaderFactories);
            Assert.Equal(0, counts.DependencyChecks);
            using BranchCheckout unrelated = repository.CreateBranch("unrelated", new SharedReadModel.Node { Value = 31 }, NoRebase);
            BranchCheckout warm = repository.Fork("warm", unrelated.Head);
            var previous = repository.PreparedStateEntry;
            Assert.NotNull(previous);
            BranchCheckout main = repository.CreateBranch("main", new Root { Value = 7 }, NoRebase);
            CheckpointAddress selected = main.Head;
            if (delta) {
                Assert.IsType<Root>(main.State).Value = 8;
                selected = main.CommitState(NoRebase);
                Assert.Equal(ObjectVersionKind.Delta, Assert.Single(Resources(repository).States.Read(selected.RevisionAddress).LocalObjects).Kind);
            }
            Assert.Equal(delta ? 2 : 1, counts.Captures);
            Assert.Equal(0, counts.BaseReads);
            Assert.Equal(0, counts.DeltaReads);
            Assert.Equal(0, counts.DependencyChecks);
            Assert.Equal(0, counts.Allocations);
            Assert.Equal(0, counts.Hydrations);
            Assert.Same(previous, repository.PreparedStateEntry);
            Assert.Equal(selected, repository.GetHead("main"));
            Assert.False(repository.IsFaulted);
            return new(repository, main, warm, selected, counts);
        } catch {
            repository.Dispose();
            throw;
        }
    }

    private static StateModelRegistry Models(bool family, DependencyMode mode, Counters counts) {
        StateModelRegistry registry = SharedReadModel.Models();
        registry.Register(new StateDefinitionBinding(Dependency.SchemaId, SchemaKind.InlineValue, 0, null,
            [new(Dependency.SchemaId, 1, SchemaKind.InlineValue, 0, [new(1, TypeExpr.Builtin(TypeTag.Int32))])]));
        StateReaderBinding Reader(StateBindingContext? context) {
            counts.ReaderFactories++;
            return new StateReaderBinding<State>(Owner,
                (ref BinaryPayloadReader input) => {
                    counts.BaseReads++;
                    State state = Read(ref input);
                    if (mode == DependencyMode.BaseAlways || mode == DependencyMode.BaseForSeven && state.Value == 7) {
                        counts.DependencyChecks++;
                        (context ?? counts.Context!).BindSchema(Dependency);
                    }
                    return state;
                }, (ref BinaryPayloadReader input, in State prior) => {
                    counts.DeltaReads++;
                    State state = mode == DependencyMode.BaseForSeven ? Read(ref input) : prior with { Value = input.ReadInt32() };
                    if (mode == DependencyMode.DeltaAlways) {
                        counts.DependencyChecks++;
                        (context ?? counts.Context!).BindSchema(Dependency);
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
        if (family) {
            registry.Register(new StateDefinitionBinding(Owner.SchemaId, SchemaKind.ReferenceObject, 0, typeof(Root),
                [new(Owner.SchemaId, 1, SchemaKind.ReferenceObject, 0,
                    [new(1, TypeExpr.Builtin(TypeTag.Int32)), new(2, TypeExpr.Builtin(TypeTag.Int64)),
                        new(3, TypeExpr.Builtin(TypeTag.Int64)), new(4, TypeExpr.Builtin(TypeTag.Int64)), new(5, TypeExpr.Builtin(TypeTag.Int64))],
                    stateTypeDefinition: typeof(State))],
                currentModelFactory: (_, context) => Model(context.ResolveReader(Owner)),
                historicalReaderFactory: (_, context) => Reader(context)));
        } else {
            registry.Register(Model(Reader(null)));
        }
        return registry;
    }

    private static void Visit(in State _, IStateReferenceVisitor __) { }
    private static State Read(ref BinaryPayloadReader input) => new(input.ReadInt32(), input.ReadInt64(), input.ReadInt64(), input.ReadInt64(), input.ReadInt64());
    private static PreparedBaseBody Base(in State state) {
        ArrayBufferWriter<byte> bytes = new();
        BinaryPayloadWriter output = new(bytes);
        output.WriteInt32(state.Value);
        output.WriteInt64(state.A);
        output.WriteInt64(state.B);
        output.WriteInt64(state.C);
        output.WriteInt64(state.D);
        return new(bytes.WrittenSpan);
    }
    private static byte[] ValueBody(int value) {
        ArrayBufferWriter<byte> bytes = new();
        BinaryPayloadWriter output = new(bytes);
        output.WriteInt32(value);
        return bytes.WrittenSpan.ToArray();
    }
    private static void RegisterConflict(Repository repository) => Resources(repository).Schemas.Register(new DurableSchema(
        Dependency.SchemaId, 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int64)));
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static GraphResources Resources(Repository repository) => Field<GraphResources>(repository, "_resources");
    private enum DependencyMode { BaseAlways, DeltaAlways, BaseForSeven }
    private sealed class Root : IDurableObject { internal int Value; }
    private readonly record struct State(int Value, long A, long B, long C, long D);
    private sealed class Counters {
        internal StateModelSnapshot? Context;
        internal int ReaderFactories, Captures, BaseReads, DeltaReads, DependencyChecks, Allocations, Hydrations;
    }
    private sealed record Scenario(Repository Repository, BranchCheckout Main, BranchCheckout Warm,
        CheckpointAddress Selected, Counters Counts) : IDisposable {
        public void Dispose() {
            Main.Dispose();
            Warm.Dispose();
            Repository.Dispose();
        }
    }
    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
