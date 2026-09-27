using System.Collections;
using System.Reflection;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Storage;
using Atelia.RbfSegmentStore;

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
        using (Repository repository = DB082Create(directory.Path, fixture, reuse)) {
            using BranchCheckout main = repository.CreateBranch("main", fixture.Create(), DB082NoRebase);
            CheckpointAddress source = main.Head;
            Assert.Null(repository.PreparedStateEntry);
            GraphReadStatistics cold = DB082Observe(repository);
            using BranchCheckout first = repository.Fork("first", source);
            Assert.Equal(0, cold.ReusedImmutableLeaves);
            Assert.Equal(family ? 4 : 3, cold.AllocatedObjects);
            Assert.Equal(reuse ? 2 : 0, repository.PreparedStateEntry!.ImmutableLeafCount);
            GraphReadStatistics hit = DB082Observe(repository);
            using BranchCheckout second = repository.Fork("second", source);
            Assert.Equal(1, hit.PreparedStateHits);
            Assert.Equal(0, hit.DecodedObjects);
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
            CheckpointAddress saved = first.CommitState(DB082NoRebase);
            Assert.Equal(source.Address, saved.Parent);
            Assert.Equal(source.RootId, saved.RootId);
            StateRevision revision = DB082Resources(repository).States.Read(saved.RevisionAddress);
            Assert.Single(revision.RemovedObjectIds);
            Assert.Equal(DB082Resources(repository).States.ReadLiveObjectHeadMap(source.RevisionAddress).Count - 1,
                DB082Resources(repository).States.ReadLiveObjectHeadMap(saved.RevisionAddress).Count);
            fixture.Change(second.State!, 22);
            Assert.Equal(source.Address, second.CommitState(DB082NoRebase).Parent);
            Assert.Equal(source, main.Head);
        }
        using Repository reopened = Repository.OpenExisting(directory.Path, fixture.Models);
        reopened.PreparedStateReuseEnabled = true;
        reopened.ImmutableLeafReuseEnabled = reuse;
        Assert.Null(reopened.PreparedStateEntry);
        GraphReadStatistics reopen = DB082Observe(reopened);
        using BranchCheckout loaded = reopened.Checkout("first");
        Assert.Equal(0, reopen.PreparedStateHits);
        Assert.Equal(0, reopen.ReusedImmutableLeaves);
        Assert.True(reopen.DecodedObjects > 0);
        Assert.Equal(11, DB082Value(loaded.State!));
        Assert.Null(DB082Member(loaded.State!, "Right"));
        Assert.NotSame(oldLeaf, DB082Member(loaded.State!, "Left"));
        Assert.Same(DB082Member(loaded.State!, "Left"), DB082Member(loaded.State!, "Alias"));
        if (family) Assert.Equal(2, Assert.IsAssignableFrom<IList>(DB082Member(loaded.State!, "Links")).Count);
        CheckpointAddress unchanged = loaded.CommitState(DB082NoRebase);
        Assert.Empty(DB082Resources(reopened).States.Read(unchanged.RevisionAddress).LocalObjects);
        using BranchCheckout sibling = reopened.Checkout("second");
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
        using BranchCheckout main = repository.CreateBranch("main", original, DB082NoRebase);
        Assert.Null(repository.PreparedStateEntry);
        using BranchCheckout first = repository.Fork("first", main.Head);
        Assert.NotSame(original, first.State);
        Assert.Equal(1, repository.PreparedStateEntry!.ImmutableLeafCount);
        GraphReadStatistics statistics = DB082Observe(repository);
        using BranchCheckout second = repository.Fork("second", main.Head);
        Assert.Same(first.State, second.State);
        Assert.NotSame(first, second);
        Assert.Equal(1, statistics.ReusedImmutableLeaves);
        Assert.Equal(0, statistics.AllocatedObjects);
        Assert.Equal(0, statistics.HydratedObjects);
        CheckpointAddress secondCommit = second.CommitState(DB082NoRebase);
        Assert.Equal(main.Head.Address, secondCommit.Parent);
        Assert.Equal(main.Head.RootId, secondCommit.RootId);
        Assert.Empty(DB082Resources(repository).States.Read(secondCommit.RevisionAddress).LocalObjects);
        // New revision, identical object head: first restoration must still build a fresh table.
        statistics = DB082Observe(repository);
        using BranchCheckout newer = repository.Fork("newer", secondCommit);
        Assert.Equal(0, statistics.ReusedImmutableLeaves);
        Assert.Equal(1, statistics.AllocatedObjects);
        Assert.NotSame(first.State, newer.State);
        second.Dispose();
        statistics = DB082Observe(repository);
        using BranchCheckout checkout = repository.Checkout("second");
        Assert.Same(newer.State, checkout.State);
        Assert.Equal(1, statistics.ReusedImmutableLeaves);
        Assert.Equal(secondCommit, checkout.Head);
        // Evicting the old leaf table did not invalidate the first branch's save identity.
        CheckpointAddress firstCommit = first.CommitState(DB082NoRebase);
        Assert.Equal(main.Head.Address, firstCommit.Parent);
        Assert.Equal(main.Head.RootId, firstCommit.RootId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedLeafReuseKeepsExactEventHeadsAndBypassesEventOnlyHistories(bool family) {
        DB082Fixture fixture = DB082Compile(family);
        using RawBaseDirectory directory = new();
        using Repository repository = DB082Create(directory.Path, fixture, true);
        using BranchCheckout main = repository.CreateBranch("main", fixture.Create(), DB082NoRebase);
        CheckpointAddress state = main.Head;
        using BranchCheckout first = repository.Fork("first", state);
        PreparedStateRestoration resident = repository.PreparedStateEntry!;
        CheckpointAddress e1 = main.CommitEvent(fixture.Create(), DB082NoRebase);
        CheckpointAddress e2 = main.CommitEvent(fixture.Create(), DB082NoRebase);
        Assert.Same(resident, repository.PreparedStateEntry);
        int index = 0;
        foreach (CheckpointAddress address in new[] { state, e1, e2 }) {
            GraphReadStatistics statistics = DB082Observe(repository);
            using BranchCheckout branch = repository.Fork($"event-source-{index++}", address);
            Assert.Equal(address, branch.Head);
            Assert.Equal(address, repository.GetHead(branch.BranchName));
            Assert.Equal(state, repository.PreparedStateEntry!.StateCheckpoint);
            Assert.Equal(2, statistics.ReusedImmutableLeaves);
            Assert.Equal(family ? 2 : 1, statistics.AllocatedObjects);
            Assert.Same(DB082Member(first.State!, "Left"), DB082Member(branch.State!, "Left"));
            Assert.NotSame(first.State, branch.State);
        }
        using BranchCheckout events = repository.CreateBranchFromEvent("events", fixture.Create(), DB082NoRebase);
        CheckpointAddress onlyEvents = events.CommitEvent(fixture.Create(), DB082NoRebase);
        GraphReadStatistics none = DB082Observe(repository);
        using BranchCheckout empty = repository.Fork("event-only", onlyEvents);
        Assert.Null(empty.State);
        Assert.Equal(onlyEvents, empty.Head);
        Assert.Equal(0, none.PreparedStateHits);
        Assert.Equal(0, none.PreparedStateMisses);
        Assert.Equal(0, none.ReusedImmutableLeaves);
        Assert.Equal(0, none.AllocatedObjects);
        Assert.Equal(0, none.HydratedObjects);
        Assert.Same(resident, repository.PreparedStateEntry);
        CheckpointAddress initialState = empty.CommitState(fixture.CreateLeaf(), DB082NoRebase);
        Assert.Same(resident, repository.PreparedStateEntry);
        GraphReadStatistics cold = DB082Observe(repository);
        using BranchCheckout firstStateFork = repository.Fork("first-state", initialState);
        Assert.Equal(0, cold.ReusedImmutableLeaves);
        Assert.Equal(1, cold.AllocatedObjects);
        Assert.NotSame(resident, repository.PreparedStateEntry);
        Assert.Equal(initialState, firstStateFork.Head);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedReadonlyViewsNeitherFillNorConsumeTheEditableLeafTable(bool family) {
        DB082Fixture fixture = DB082Compile(family);
        using RawBaseDirectory directory = new();
        using Repository repository = DB082Create(directory.Path, fixture, true);
        using BranchCheckout main = repository.CreateBranch("main", fixture.Create(), DB082NoRebase);
        CheckpointAddress state = main.Head;
        CheckpointAddress message = main.CommitEvent(fixture.Create(), DB082NoRebase);
        object[] ReadRoots() {
            EventCheckpoint checkpoint = Assert.IsType<EventCheckpoint>(repository.ReadCheckpoint(message));
            Assert.NotSame(checkpoint.Event, checkpoint.PreviousState);
            Assert.NotSame(DB082Member(checkpoint.Event, "Left"), DB082Member(checkpoint.PreviousState!, "Left"));
            var pair = repository.ReadPair(state, state);
            // Pair has its own closure-sharing contract; no assertion forbids sharing inside it.
            return [repository.ReadState(state), repository.ReadEvent(message), checkpoint.Event,
                checkpoint.PreviousState!, pair.First, pair.Second];
        }
        object[] before = ReadRoots();
        Assert.Null(repository.PreparedStateEntry);
        using BranchCheckout first = repository.Fork("first", message);
        PreparedStateRestoration resident = repository.PreparedStateEntry!;
        object leaf = DB082Member(first.State!, "Left")!;
        foreach (object root in before.Concat(ReadRoots())) {
            DB082CheckGraph(root, family);
            Assert.NotSame(leaf, DB082Member(root, "Left"));
        }
        Assert.Same(resident, repository.PreparedStateEntry);
        Assert.Equal(2, resident.ImmutableLeafCount);
        GraphReadStatistics statistics = DB082Observe(repository);
        using BranchCheckout again = repository.Fork("again", message);
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
        using BranchCheckout main = repository.CreateBranch("main", fixture.Create(), DB082NoRebase);
        CheckpointAddress original = main.Head;
        using BranchCheckout first = repository.Fork("first", original);
        object oldLeaf = DB082Member(first.State!, "Left")!;
        PreparedStateRestoration originalEntry = repository.PreparedStateEntry!;
        CheckpointAddress next = main.CommitState(DB082NoRebase);
        Assert.Empty(DB082Resources(repository).States.Read(next.RevisionAddress).LocalObjects);
        Assert.Same(originalEntry, repository.PreparedStateEntry);
        GraphReadStatistics statistics = DB082Observe(repository);
        using BranchCheckout nextFork = repository.Fork("next", next);
        Assert.Equal(0, statistics.ReusedImmutableLeaves);
        Assert.NotSame(oldLeaf, DB082Member(nextFork.State!, "Left"));
        Assert.NotSame(originalEntry, repository.PreparedStateEntry);
        IDurableObject promoted = (IDurableObject)DB082Member(main.State!, "Left")!;
        CheckpointAddress promotedAddress = main.CommitState(promoted, DB082NoRebase);
        Assert.NotEqual(next.RootId, promotedAddress.RootId);
        statistics = DB082Observe(repository);
        using BranchCheckout promotedFork = repository.Fork("promoted", promotedAddress);
        Assert.Equal(0, statistics.ReusedImmutableLeaves);
        Assert.Equal(1, statistics.AllocatedObjects);
        Assert.Equal(fixture.LeafType, promotedFork.State!.GetType());
        Assert.NotSame(DB082Member(nextFork.State!, "Left"), promotedFork.State);
        Assert.NotSame(promoted, promotedFork.State);
        Assert.Equal(promotedAddress.RootId, promotedFork.CommitState(DB082NoRebase).RootId);
        CheckpointAddress changedType = main.CommitState(fixture.CreateEmpty(), DB082NoRebase);
        statistics = DB082Observe(repository);
        using BranchCheckout emptyFork = repository.Fork("empty-root", changedType);
        Assert.Equal(0, statistics.ReusedImmutableLeaves);
        Assert.Equal(fixture.EmptyType, emptyFork.State!.GetType());
        Assert.Equal(1, repository.PreparedStateEntry!.ImmutableLeafCount);
        fixture.Change(first.State!, 39);
        CheckpointAddress oldSaved = first.CommitState(DB082NoRebase);
        Assert.Equal(original.Address, oldSaved.Parent);
        Assert.Equal(original.RootId, oldSaved.RootId);
        Assert.Equal(39, DB082Value(repository.ReadState(oldSaved)));
        statistics = DB082Observe(repository);
        using BranchCheckout originalAgain = repository.Fork("original-again", original);
        Assert.Equal(0, statistics.ReusedImmutableLeaves);
        Assert.NotSame(oldLeaf, DB082Member(originalAgain.State!, "Left"));
        DB082CheckGraph(originalAgain.State!, family);
    }

    private static Repository DB082Create(string path, DB082Fixture fixture, bool reuse) {
        Repository repository = Repository.CreateNew(path, fixture.Models, new() { NewStoreLayout = RbfSegmentStoreLayout.Flat });
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
    private static GraphResources DB082Resources(Repository repository) => (GraphResources)typeof(Repository)
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
        StateModelSnapshot snapshot = models.Snapshot();
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
