using System.Collections;
using System.Reflection;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    private static readonly ReadAmplificationBaseBudgetParameters DB082NoRebase = new(1000000, 1);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void GeneratedLeafForksPreserveIdentityAndMutableIsolationThroughRemoveAndReopen(bool family, bool reuse) {
        DB082Fixture fixture = DB082Compile(family);
        using RawBaseDirectory directory = new();
        object oldLeaf;
        BranchId firstId, secondId;
        using (Repository repository = DB082Create(directory.Path, fixture, reuse)) {
            using WorkingCopy main = DB082SeedWorkingCopy(repository, fixture.Create(), DB082NoRebase);
            CommitId source = main.BaseCommitId;
            Assert.Null(repository.PreparedStateEntry);
            GraphReadStatistics cold = DB082Observe(repository);
            using WorkingCopy first = repository.OpenWorkingCopy(repository.ForkBranch(source, "first").Id);
            firstId = first.BranchId;
            Assert.Equal(0, cold.ReusedImmutableLeaves);
            Assert.Equal(1, cold.PreparedGraphs);
            Assert.Equal(family ? 4 : 3, cold.AllocatedObjects);
            Assert.Equal(reuse ? 2 : 0, repository.PreparedStateEntry!.ImmutableLeafCount);
            GraphReadStatistics hit = DB082Observe(repository);
            using WorkingCopy second = repository.OpenWorkingCopy(repository.ForkBranch(source, "second").Id);
            secondId = second.BranchId;
            Assert.Equal(1, hit.PreparedStateHits);
            Assert.Equal(0, hit.PreparedGraphs);
            Assert.Equal(0, hit.NormalizedObjects);
            Assert.Equal(reuse ? 2 : 0, hit.ReusedImmutableLeaves);
            Assert.Equal(cold.AllocatedObjects - (reuse ? 2 : 0), hit.AllocatedObjects);
            Assert.Equal(hit.AllocatedObjects, hit.HydratedObjects);
            DB082CheckGraph(first.State!, family);
            DB082CheckGraph(second.State!, family);
            Assert.NotSame(main.State, first.State);
            Assert.NotSame(first.State, second.State);
            Assert.NotSame(DB082Member(main.State!, "Left"), DB082Member(first.State!, "Left"));
            Assert.NotSame(DB082Member(first.State!, "Left"), DB082Member(first.State!, "Right"));
            if (reuse) {
                Assert.Same(DB082Member(first.State!, "Left"), DB082Member(second.State!, "Left"));
                Assert.Same(DB082Member(first.State!, "Right"), DB082Member(second.State!, "Right"));
            } else {
                Assert.NotSame(DB082Member(first.State!, "Left"), DB082Member(second.State!, "Left"));
                Assert.NotSame(DB082Member(first.State!, "Right"), DB082Member(second.State!, "Right"));
            }
            if (family) Assert.NotSame(DB082Member(first.State!, "Links"), DB082Member(second.State!, "Links"));
            oldLeaf = DB082Member(first.State!, "Left")!;
            fixture.Change(first.State!, 11);
            fixture.RemoveRight(first.State!);
            Assert.Equal(1, DB082Value(second.State!));
            Assert.NotNull(DB082Member(second.State!, "Right"));
            if (family) Assert.Equal(3, Assert.IsAssignableFrom<IList>(DB082Member(second.State!, "Links")).Count);
            CommitId saved = first.CommitState(parameters: DB082NoRebase).Id;
            Assert.Equal(DB082Graph(repository, source).Address, DB082Graph(repository, saved).SourceGraph);
            Assert.Equal(DB082Graph(repository, source).RootId, DB082Graph(repository, saved).RootId);
            var store = DB082Resources(repository).GraphStore;
            Assert.Single(DB082Graph(repository, source).ObjectHeads.Keys.Except(DB082Graph(repository, saved).ObjectHeads.Keys));
            Assert.Equal(DB082Graph(repository, source).ObjectHeads.Count - 1,
                DB082Graph(repository, saved).ObjectHeads.Count);
            fixture.Change(second.State!, 22);
            Assert.Equal(DB082Graph(repository, source).Address,
                DB082Graph(repository, second.CommitState(parameters: DB082NoRebase).Id).SourceGraph);
            Assert.Equal(source, main.BaseCommitId);
        }
        using Repository reopened = Repository.OpenExisting(directory.Path, fixture.Models);
        reopened.PreparedStateReuseEnabled = true;
        reopened.ImmutableLeafReuseEnabled = reuse;
        Assert.Null(reopened.PreparedStateEntry);
        GraphReadStatistics reopen = DB082Observe(reopened);
        using WorkingCopy loaded = reopened.OpenWorkingCopy(firstId);
        Assert.Equal(0, reopen.PreparedStateHits);
        Assert.Equal(0, reopen.ReusedImmutableLeaves);
        // Reopen prepares a fresh graph; this counter does not claim physical frame I/O.
        Assert.Equal(1, reopen.PreparedGraphs);
        Assert.Equal(11, DB082Value(loaded.State!));
        Assert.Null(DB082Member(loaded.State!, "Right"));
        Assert.NotSame(oldLeaf, DB082Member(loaded.State!, "Left"));
        Assert.Same(DB082Member(loaded.State!, "Left"), DB082Member(loaded.State!, "Alias"));
        if (family) Assert.Equal(2, Assert.IsAssignableFrom<IList>(DB082Member(loaded.State!, "Links")).Count);
        CommitId unchanged = loaded.CommitState(parameters: DB082NoRebase).Id;
        Assert.Empty(DB082Records(reopened, unchanged));
        using WorkingCopy sibling = reopened.OpenWorkingCopy(secondId);
        Assert.Equal(22, DB082Value(sibling.State!));
        Assert.NotNull(DB082Member(sibling.State!, "Right"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void GeneratedImmutableAndEmptyRootsShareOnlySuccessfulMaterializationOfExactState(bool family, bool empty) {
        DB082Fixture fixture = DB082Compile(family);
        using RawBaseDirectory directory = new();
        using Repository repository = DB082Create(directory.Path, fixture, true);
        IDurableObject original = empty ? fixture.CreateEmpty() : fixture.CreateLeaf();
        using WorkingCopy main = DB082SeedWorkingCopy(repository, original, DB082NoRebase);
        Assert.Null(repository.PreparedStateEntry);
        using WorkingCopy first = repository.OpenWorkingCopy(repository.ForkBranch(main.BaseCommitId, "first").Id);
        Assert.NotSame(original, first.State);
        Assert.Equal(1, repository.PreparedStateEntry!.ImmutableLeafCount);
        GraphReadStatistics statistics = DB082Observe(repository);
        using WorkingCopy second = repository.OpenWorkingCopy(repository.ForkBranch(main.BaseCommitId, "second").Id);
        Assert.Same(first.State, second.State);
        Assert.NotSame(first, second);
        Assert.Equal(1, statistics.ReusedImmutableLeaves);
        Assert.Equal(0, statistics.AllocatedObjects);
        Assert.Equal(0, statistics.HydratedObjects);
        CommitId secondCommit = second.CommitState(parameters: DB082NoRebase).Id;
        Assert.Equal(DB082Graph(repository, main.BaseCommitId).Address, DB082Graph(repository, secondCommit).SourceGraph);
        Assert.Equal(DB082Graph(repository, main.BaseCommitId).RootId, DB082Graph(repository, secondCommit).RootId);
        Assert.Empty(DB082Records(repository, secondCommit));
        // New revision, identical object head: first restoration must still build a fresh table.
        statistics = DB082Observe(repository);
        using WorkingCopy newer = repository.OpenWorkingCopy(repository.ForkBranch(secondCommit, "newer").Id);
        Assert.Equal(0, statistics.ReusedImmutableLeaves);
        Assert.Equal(1, statistics.AllocatedObjects);
        Assert.NotSame(first.State, newer.State);
        second.Dispose();
        statistics = DB082Observe(repository);
        using WorkingCopy checkout = repository.OpenWorkingCopy(second.BranchId);
        Assert.Same(newer.State, checkout.State);
        Assert.Equal(1, statistics.ReusedImmutableLeaves);
        Assert.Equal(secondCommit, checkout.BaseCommitId);
        // Evicting the old leaf table did not invalidate the first branch's save identity.
        CommitId firstCommit = first.CommitState(parameters: DB082NoRebase).Id;
        Assert.Equal(DB082Graph(repository, main.BaseCommitId).Address, DB082Graph(repository, firstCommit).SourceGraph);
        Assert.Equal(DB082Graph(repository, main.BaseCommitId).RootId, DB082Graph(repository, firstCommit).RootId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedLeafReuseKeepsExactEventHeadsAndBypassesEventOnlyHistories(bool family) {
        DB082Fixture fixture = DB082Compile(family);
        using RawBaseDirectory directory = new();
        using Repository repository = DB082Create(directory.Path, fixture, true);
        using WorkingCopy main = DB082SeedWorkingCopy(repository, fixture.Create(), DB082NoRebase);
        CommitId state = main.BaseCommitId;
        using WorkingCopy first = repository.OpenWorkingCopy(repository.ForkBranch(state, "first").Id);
        PreparedStateRestoration resident = repository.PreparedStateEntry!;
        CommitId e1 = main.CommitEvent(fixture.Create()).Id;
        CommitId e2 = main.CommitEvent(fixture.Create()).Id;
        Assert.Same(resident, repository.PreparedStateEntry);
        int index = 0;
        foreach (CommitId address in new[] { state, e1, e2 }) {
            GraphReadStatistics statistics = DB082Observe(repository);
            using WorkingCopy branch = repository.OpenWorkingCopy(repository.ForkBranch(address, $"event-source-{index++}").Id);
            Assert.Equal(address, branch.BaseCommitId);
            Assert.Equal(address, repository.ReadCurrentCommit(branch.BranchId).Commit.Id);
            Assert.Equal(state, new CommitId(repository.PreparedStateEntry!.StateCheckpoint.Id));
            Assert.Equal(2, statistics.ReusedImmutableLeaves);
            Assert.Equal(family ? 2 : 1, statistics.AllocatedObjects);
            Assert.Same(DB082Member(first.State!, "Left"), DB082Member(branch.State!, "Left"));
            Assert.NotSame(first.State, branch.State);
        }
        using WorkingCopy events = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("events", fixture.Create()).Id);
        CommitId onlyEvents = events.CommitEvent(fixture.Create()).Id;
        GraphReadStatistics none = DB082Observe(repository);
        using WorkingCopy empty = repository.OpenWorkingCopy(repository.ForkBranch(onlyEvents, "event-only").Id);
        Assert.Null(empty.State);
        Assert.Equal(onlyEvents, empty.BaseCommitId);
        Assert.Equal(0, none.PreparedStateHits);
        Assert.Equal(0, none.PreparedStateMisses);
        Assert.Equal(0, none.ReusedImmutableLeaves);
        Assert.Equal(0, none.AllocatedObjects);
        Assert.Equal(0, none.HydratedObjects);
        Assert.Same(resident, repository.PreparedStateEntry);
        CommitId initialState = empty.CommitState(fixture.CreateLeaf(), parameters: DB082NoRebase).Id;
        Assert.Same(resident, repository.PreparedStateEntry);
        GraphReadStatistics cold = DB082Observe(repository);
        using WorkingCopy firstStateFork = repository.OpenWorkingCopy(repository.ForkBranch(initialState, "first-state").Id);
        Assert.Equal(0, cold.ReusedImmutableLeaves);
        Assert.Equal(1, cold.AllocatedObjects);
        Assert.NotSame(resident, repository.PreparedStateEntry);
        Assert.Equal(initialState, firstStateFork.BaseCommitId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedReadonlyViewsNeitherFillNorConsumeTheEditableLeafTable(bool family) {
        DB082Fixture fixture = DB082Compile(family);
        using RawBaseDirectory directory = new();
        using Repository repository = DB082Create(directory.Path, fixture, true);
        using WorkingCopy main = DB082SeedWorkingCopy(repository, fixture.Create(), DB082NoRebase);
        CommitId state = main.BaseCommitId;
        CommitId message = main.CommitEvent(fixture.Create()).Id;
        object[] ReadRoots() {
            EventCheckpoint checkpoint = Assert.IsType<EventCheckpoint>(repository.ReadCheckpoint(message));
            Assert.NotSame(checkpoint.Event, checkpoint.PreviousState);
            Assert.NotSame(DB082Member(checkpoint.Event, "Left"), DB082Member(checkpoint.PreviousState!, "Left"));
            var pair = repository.ReadPair(state, state);
            // Pair has its own closure-sharing contract; no assertion forbids sharing inside it.
            return [repository.LoadState(state), repository.LoadEvent(message), checkpoint.Event,
                checkpoint.PreviousState!, pair.First, pair.Second];
        }
        object[] before = ReadRoots();
        Assert.Null(repository.PreparedStateEntry);
        using WorkingCopy first = repository.OpenWorkingCopy(repository.ForkBranch(message, "first").Id);
        PreparedStateRestoration resident = repository.PreparedStateEntry!;
        object leaf = DB082Member(first.State!, "Left")!;
        foreach (object root in before.Concat(ReadRoots())) {
            DB082CheckGraph(root, family);
            Assert.NotSame(leaf, DB082Member(root, "Left"));
        }
        Assert.Same(resident, repository.PreparedStateEntry);
        Assert.Equal(2, resident.ImmutableLeafCount);
        GraphReadStatistics statistics = DB082Observe(repository);
        using WorkingCopy again = repository.OpenWorkingCopy(repository.ForkBranch(message, "again").Id);
        Assert.Equal(2, statistics.ReusedImmutableLeaves);
        Assert.Same(leaf, DB082Member(again.State!, "Left"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedLeavesDoNotCrossNewRevisionPromotedRootOrActualRootType(bool family) {
        DB082Fixture fixture = DB082Compile(family);
        using RawBaseDirectory directory = new();
        using Repository repository = DB082Create(directory.Path, fixture, true);
        using WorkingCopy main = DB082SeedWorkingCopy(repository, fixture.Create(), DB082NoRebase);
        CommitId original = main.BaseCommitId;
        using WorkingCopy first = repository.OpenWorkingCopy(repository.ForkBranch(original, "first").Id);
        object oldLeaf = DB082Member(first.State!, "Left")!;
        PreparedStateRestoration originalEntry = repository.PreparedStateEntry!;
        CommitId next = main.CommitState(parameters: DB082NoRebase).Id;
        Assert.Empty(DB082Records(repository, next));
        Assert.Same(originalEntry, repository.PreparedStateEntry);
        GraphReadStatistics statistics = DB082Observe(repository);
        using WorkingCopy nextFork = repository.OpenWorkingCopy(repository.ForkBranch(next, "next").Id);
        Assert.Equal(0, statistics.ReusedImmutableLeaves);
        Assert.NotSame(oldLeaf, DB082Member(nextFork.State!, "Left"));
        Assert.NotSame(originalEntry, repository.PreparedStateEntry);
        IDurableObject promoted = (IDurableObject)DB082Member(main.State!, "Left")!;
        CommitId promotedAddress = main.CommitState(promoted, parameters: DB082NoRebase).Id;
        Assert.NotEqual(DB082Graph(repository, next).RootId, DB082Graph(repository, promotedAddress).RootId);
        statistics = DB082Observe(repository);
        using WorkingCopy promotedFork = repository.OpenWorkingCopy(repository.ForkBranch(promotedAddress, "promoted").Id);
        Assert.Equal(0, statistics.ReusedImmutableLeaves);
        Assert.Equal(1, statistics.AllocatedObjects);
        Assert.Equal(fixture.LeafType, promotedFork.State!.GetType());
        Assert.NotSame(DB082Member(nextFork.State!, "Left"), promotedFork.State);
        Assert.NotSame(promoted, promotedFork.State);
        Assert.Equal(DB082Graph(repository, promotedAddress).RootId,
            DB082Graph(repository, promotedFork.CommitState(parameters: DB082NoRebase).Id).RootId);
        CommitId changedType = main.CommitState(fixture.CreateEmpty(), parameters: DB082NoRebase).Id;
        statistics = DB082Observe(repository);
        using WorkingCopy emptyFork = repository.OpenWorkingCopy(repository.ForkBranch(changedType, "empty-root").Id);
        Assert.Equal(0, statistics.ReusedImmutableLeaves);
        Assert.Equal(fixture.EmptyType, emptyFork.State!.GetType());
        Assert.Equal(1, repository.PreparedStateEntry!.ImmutableLeafCount);
        fixture.Change(first.State!, 39);
        CommitId oldSaved = first.CommitState(parameters: DB082NoRebase).Id;
        Assert.Equal(DB082Graph(repository, original).Address, DB082Graph(repository, oldSaved).SourceGraph);
        Assert.Equal(DB082Graph(repository, original).RootId, DB082Graph(repository, oldSaved).RootId);
        Assert.Equal(39, DB082Value(repository.LoadState(oldSaved)));
        statistics = DB082Observe(repository);
        using WorkingCopy originalAgain = repository.OpenWorkingCopy(repository.ForkBranch(original, "original-again").Id);
        Assert.Equal(0, statistics.ReusedImmutableLeaves);
        Assert.NotSame(oldLeaf, DB082Member(originalAgain.State!, "Left"));
        DB082CheckGraph(originalAgain.State!, family);
    }

    // Setup restores the newly created branch without populating the cache under test.
    // Subsequent explicit ForkBranch + OpenWorkingCopy calls measure the first cache admission.
    private static WorkingCopy DB082SeedWorkingCopy(Repository repository, IDurableObject root,
        ReadAmplificationBaseBudgetParameters policy) {
        BranchInfo branch = repository.CreateRootBranchFromState("main", root, parameters: policy);
        bool enabled = repository.PreparedStateReuseEnabled;
        repository.PreparedStateReuseEnabled = false;
        try { return repository.OpenWorkingCopy(branch.Id); }
        finally { repository.PreparedStateReuseEnabled = enabled; }
    }

    private static StoredGraphRevision DB082Graph(Repository repository, CommitId commit) {
        RepositoryResources resources = DB082Resources(repository);
        HistoryGraphRecord descriptor = resources.ReadDescriptor(commit.Value);
        FrameAddress? expectedSource = resources.FindNearestState(commit.Value, inclusive: false)?.Address;
        return resources.GraphStore.ReadBusinessGraph(descriptor.Address, expectedSource);
    }

    private static IReadOnlyList<ObjectVersionRecord> DB082Records(Repository repository, CommitId commit) {
        StoredGraphRevision graph = DB082Graph(repository, commit);
        return graph.OwnedBatch is { } batch ? DB082Resources(repository).GraphStore.ReadBatch(batch).Records : [];
    }

    private static Repository DB082Create(string path, DB082Fixture fixture, bool reuse) {
        Repository repository = Repository.CreateNew(path, fixture.Models, 64L * 1024 * 1024 * 1024);
        repository.PreparedStateReuseEnabled = true;
        repository.ImmutableLeafReuseEnabled = reuse;
        return repository;
    }

    private static GraphReadStatistics DB082Observe(Repository repository) {
        GraphReadStatistics statistics = new();
        repository.RestorationStatistics = statistics;
        return statistics;
    }

    private static object? DB082Member(object root, string member) => root.GetType().GetField(member)!.GetValue(root);
    private static int DB082Value(object root) => (int)DB082Member(root, "Value")!;
    private static RepositoryResources DB082Resources(Repository repository) => (RepositoryResources)typeof(Repository)
        .GetField("_resources", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(repository)!;

    private static void DB082CheckGraph(object root, bool family) {
        Assert.Equal(1, DB082Value(root));
        object left = DB082Member(root, "Left")!, right = DB082Member(root, "Right")!;
        Assert.Equal(7, DB082Value(left));
        Assert.Equal(7, DB082Value(right));
        Assert.NotSame(left, right);
        Assert.Same(left, DB082Member(root, "Alias"));
        Assert.Same(root, DB082Member(root, "Self"));
        if (family) {
            IList links = Assert.IsAssignableFrom<IList>(DB082Member(root, "Links"));
            Assert.Equal(3, links.Count);
            Assert.Same(left, links[0]);
            Assert.Same(right, links[1]);
            Assert.Same(left, links[2]);
        }
    }

    private static DB082Fixture DB082Compile(bool family) {
        GeneratorTestRun run = RunGenerator(DB082Source(family), [], null, family ? "true" : null);
        AssertSchemaOnlyCompiles(run);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        Type host = assembly.GetType("DB082Host")!;
        T Method<T>(string name) where T : Delegate => host.GetMethod(name)!.CreateDelegate<T>();
        StateModelRegistry models = Method<Func<StateModelRegistry>>("Models")();
        Type owner = assembly.GetType("DB082Owner")!, leaf = assembly.GetType("DB082Leaf")!, empty = assembly.GetType("DB082Empty")!;
        ModelExecutionEnvironment snapshot = models.CreateEnvironment();
        Assert.False(snapshot.ResolveCurrentModel(owner).IsImmutableLeaf);
        Assert.True(snapshot.ResolveCurrentModel(leaf).IsImmutableLeaf);
        Assert.True(snapshot.ResolveCurrentModel(empty).IsImmutableLeaf);
        // Verify the intended generator route instead of assuming an option selected it.
        Assert.Equal(!family, leaf.GetNestedType("__DurableState", BindingFlags.Public | BindingFlags.NonPublic) is not null);
        return new(models, leaf, empty, Method<Func<IDurableObject>>("Create"), Method<Func<IDurableObject>>("CreateLeaf"),
            Method<Func<IDurableObject>>("CreateEmpty"), Method<Action<IDurableObject, int>>("Change"),
            Method<Action<IDurableObject>>("RemoveRight"));
    }

    private sealed record DB082Fixture(StateModelRegistry Models, Type LeafType, Type EmptyType,
        Func<IDurableObject> Create, Func<IDurableObject> CreateLeaf, Func<IDurableObject> CreateEmpty,
        Action<IDurableObject, int> Change, Action<IDurableObject> RemoveRight);

    private static string DB082Source(bool family) => $$"""
        using System.Collections.Generic;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Persistence;
        [DurableType("db082.generated.leaf", 1)]
        public partial class DB082Leaf : IDurableObject {
            [DurableField(1)] public readonly int Value;
            public DB082Leaf(int value) { Value = value; }
        }
        [DurableType("db082.generated.empty", 1)]
        public partial class DB082Empty : IDurableObject { }
        [DurableType("db082.generated.owner", 1)]
        public partial class DB082Owner : IDurableObject {
            [DurableField(1)] public int Value;
            [DurableField(2)] public DB082Leaf Left;
            [DurableField(3)] public DB082Leaf Alias;
            [DurableField(4)] public DB082Leaf Right;
            [DurableField(5)] public DB082Owner Self;
            {{(family ? "[DurableField(6)] public List<DB082Leaf> Links;" : "")}}
        }
        public static class DB082Host {
            public static StateModelRegistry Models() {
                var models = new StateModelRegistry();
                {{(family ? "Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);" : "DB082Owner.__DurableState.RegisterModel(models); DB082Leaf.__DurableState.RegisterModel(models); DB082Empty.__DurableState.RegisterModel(models);")}}
                return models;
            }
            public static IDurableObject Create() {
                var owner = new DB082Owner { Value = 1, Left = new DB082Leaf(7), Right = new DB082Leaf(7) };
                owner.Alias = owner.Left;
                owner.Self = owner;
                {{(family ? "owner.Links = new List<DB082Leaf> { owner.Left, owner.Right, owner.Left };" : "")}}
                return owner;
            }
            public static IDurableObject CreateLeaf() => new DB082Leaf(7);
            public static IDurableObject CreateEmpty() => new DB082Empty();
            public static void Change(IDurableObject root, int value) => ((DB082Owner)root).Value = value;
            public static void RemoveRight(IDurableObject root) {
                var owner = (DB082Owner)root;
                owner.Right = null;
                {{(family ? "owner.Links.RemoveAt(1);" : "")}}
            }
        }
        """;
}
