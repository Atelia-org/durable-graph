using Atelia.RbfSegmentStore;
using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class DB078CheckpointTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"durable-db078-checkpoint-{Guid.NewGuid():N}");
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1000000, 1);

    [Fact]
    public void PublicCheckpointsAreNonGenericReadOnlyRootViews() {
        Assert.True(typeof(Checkpoint).IsAbstract);
        foreach (Type type in new[] { typeof(Checkpoint), typeof(EventCheckpoint), typeof(StateCheckpoint) }) {
            Assert.Equal("Atelia.DurableGraph", type.Namespace);
            Assert.False(type.IsGenericType);
            Assert.Empty(type.GetConstructors());
            Assert.All(type.GetProperties(), property => Assert.Null(property.SetMethod));
        }
        Assert.True(typeof(EventCheckpoint).IsSealed);
        Assert.True(typeof(StateCheckpoint).IsSealed);
        Assert.DoesNotContain(typeof(Repository).GetMethods(), method => method.Name == "ReadEvents");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NearestOppositeRoleUsesStrictAncestorsAndRestoresAtMostTwoGraphs(bool eventFirst) {
        int allocations = 0, hydrations = 0;
        using Repository repository = Create(SharedReadModel.Models(
            allocate: () => { allocations++; return new Node(); }, hydrate: _ => hydrations++));
        using BranchCheckout branch = eventFirst
            ? repository.CreateBranchFromEvent("main", new Node { Value = 1 }, NoRebase)
            : repository.CreateBranch("main", new Node { Value = 1 }, NoRebase);
        CheckpointAddress first = branch.Head;
        CheckpointAddress e1 = branch.CommitEvent(new Node { Value = 2 }, NoRebase);
        CheckpointAddress e2 = branch.CommitEvent(new Node { Value = 3 }, NoRebase);
        CheckpointAddress s1 = branch.CommitState(new Node { Value = 4 }, NoRebase);
        CheckpointAddress s2 = branch.CommitState(new Node { Value = 5 }, NoRebase);
        allocations = hydrations = 0;
        Checkpoint original = repository.ReadCheckpoint(first);
        Assert.Equal(first, original.Address);
        if (original is EventCheckpoint initialEvent) {
            Assert.Null(initialEvent.PreviousState);
            Assert.Null(initialEvent.PreviousStateAddress);
        } else {
            StateCheckpoint initialState = Assert.IsType<StateCheckpoint>(original);
            Assert.Null(initialState.PreviousEvent);
            Assert.Null(initialState.PreviousEventAddress);
        }
        Assert.Equal(1, allocations);
        Assert.Equal(1, hydrations);
        foreach (CheckpointAddress address in new[] { e1, e2 }) {
            allocations = hydrations = 0;
            EventCheckpoint view = Assert.IsType<EventCheckpoint>(repository.ReadCheckpoint(address));
            Assert.Equal(address, view.Address);
            Assert.Equal(eventFirst ? null : first, view.PreviousStateAddress);
            if (eventFirst) {
                Assert.Null(view.PreviousState);
            } else {
                Assert.Equal((byte)1, Assert.IsType<Node>(view.PreviousState).Value);
            }
            Assert.Equal(eventFirst ? 1 : 2, allocations);
            Assert.Equal(allocations, hydrations);
        }
        foreach (CheckpointAddress address in new[] { s1, s2 }) {
            allocations = hydrations = 0;
            StateCheckpoint view = Assert.IsType<StateCheckpoint>(repository.ReadCheckpoint(address));
            Assert.Equal(address, view.Address);
            Assert.Equal(e2, view.PreviousEventAddress);
            Assert.Equal((byte)3, Assert.IsType<Node>(view.PreviousEvent).Value);
            Assert.Equal(2, allocations);
            Assert.Equal(2, hydrations);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CheckpointGraphsKeepCyclesAliasesAndListsButIsolateAllMutableViews(bool stateCheckpoint) {
        Node child = new() { Value = 2 };
        child.Next = child;
        Node state = new() { Value = 1, Next = child, Alias = child, Links = [child, child] };
        Node message = new() { Value = 3, Next = child, Alias = child, Links = state.Links };
        using Repository repository = Create();
        using BranchCheckout branch = repository.CreateBranch("main", state, NoRebase);
        CheckpointAddress eventAddress = branch.CommitEvent(message, NoRebase);
        CheckpointAddress selected = stateCheckpoint ? branch.CommitState(NoRebase) : eventAddress;
        Checkpoint checkpoint = repository.ReadCheckpoint(selected);
        Node first = Root(checkpoint), previous = Previous(checkpoint);
        Checkpoint independent = repository.ReadCheckpoint(selected);
        Node separate = Assert.IsType<Node>(repository.ReadEvent(eventAddress));
        foreach (Node graph in new[] { first, previous, Root(independent), Previous(independent), separate, state }) {
            Assert.Same(graph.Next, graph.Alias);
            Assert.Same(graph.Next, graph.Next!.Next);
            Assert.Same(graph.Next, graph.Links![0]);
            Assert.Same(graph.Links[0], graph.Links[1]);
        }
        Node[] roots = [first, previous, Root(independent), Previous(independent), separate, state];
        for (int i = 0; i < roots.Length; i++) {
            for (int j = i + 1; j < roots.Length; j++) {
                Assert.NotSame(roots[i], roots[j]);
                Assert.NotSame(roots[i].Next, roots[j].Next);
                Assert.NotSame(roots[i].Links, roots[j].Links);
            }
        }
        previous.Next!.Value = 90;
        previous.Links!.Clear();
        first.Next!.Value = 91;
        Assert.Same(first, Root(checkpoint));
        Assert.Same(previous, Previous(checkpoint));
        Assert.Equal((byte)90, Previous(checkpoint).Next!.Value);
        Assert.Empty(Previous(checkpoint).Links!);
        Assert.Equal((byte)91, Root(checkpoint).Next!.Value);
        foreach (Node graph in roots.Skip(2)) {
            Assert.Equal((byte)2, graph.Next!.Value);
            Assert.Equal(2, graph.Links!.Count);
        }
        Assert.Equal(selected, branch.Head);
        Assert.Equal((byte)2, Root(repository.ReadCheckpoint(selected)).Next!.Value);
    }

    [Fact]
    public void ReadEventAllocatesAndHydratesOnlyItsReachableGraph() {
        int allocations = 0;
        List<byte> hydrated = [];
        using Repository repository = Create(SharedReadModel.Models(
            allocate: () => { allocations++; return new Node(); }, hydrate: node => hydrated.Add(node.Value)));
        using BranchCheckout branch = repository.CreateBranch("main", new Node { Value = 1, Next = new Node { Value = 2 } }, NoRebase);
        CheckpointAddress address = branch.CommitEvent(new Node { Value = 7 }, NoRebase);
        allocations = 0;
        hydrated.Clear();
        Assert.Equal((byte)7, Assert.IsType<Node>(repository.ReadEvent(address)).Value);
        Assert.Equal(1, allocations);
        Assert.Equal(new byte[] { 7 }, hydrated);
        allocations = 0;
        hydrated.Clear();
        repository.ReadCheckpoint(address);
        Assert.Equal(3, allocations);
        Assert.Equal(new byte[] { 1, 2, 7 }, hydrated.Order());
    }

    [Fact]
    public void SingletonAcrossTwoSingleObjectGraphsIsRejectedWithinOneReadOperation() {
        Node singleton = new();
        bool violate = true;
        using Repository repository = Create(SharedReadModel.Models(allocate: () => violate ? singleton : new Node()));
        using BranchCheckout branch = repository.CreateBranch("main", new Node { Value = 1 }, NoRebase);
        CheckpointAddress address = branch.CommitEvent(new Node { Value = 2 }, NoRebase);
        Checkpoint? delivered = null;
        Assert.Throws<InvalidDataException>(() => delivered = repository.ReadCheckpoint(address));
        Assert.Null(delivered);
        Assert.False(repository.IsFaulted);
        violate = false;
        EventCheckpoint recovered = Assert.IsType<EventCheckpoint>(repository.ReadCheckpoint(address));
        Assert.Equal((byte)2, Assert.IsType<Node>(recovered.Event).Value);
        Assert.Equal((byte)1, Assert.IsType<Node>(recovered.PreviousState).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LaterGraphFailureOrReentryDeliversNoCheckpointAndReleasesBusy(bool reenter) {
        int hydration = 0;
        bool enabled = false;
        Repository? current = null;
        using Repository repository = Create(SharedReadModel.Models(hydrate: _ => {
            if (!enabled || ++hydration != 2) { return; }
            if (reenter) {
                current!.ReadCheckpoint(current.GetHead("main"));
            } else {
                throw new InvalidDataException("Second graph hydration failed.");
            }
        }));
        current = repository;
        using BranchCheckout branch = repository.CreateBranch("main", new Node { Value = 1 }, NoRebase);
        CheckpointAddress address = branch.CommitEvent(new Node { Value = 2 }, NoRebase);
        enabled = true;
        Checkpoint? delivered = null;
        if (reenter) {
            Assert.Throws<InvalidOperationException>(() => delivered = repository.ReadCheckpoint(address));
        } else {
            Assert.Throws<InvalidDataException>(() => delivered = repository.ReadCheckpoint(address));
        }
        Assert.Equal(2, hydration);
        Assert.Null(delivered);
        Assert.False(repository.IsFaulted);
        enabled = false;
        Assert.Equal(address, repository.ReadCheckpoint(address).Address);
        Assert.Equal((byte)2, Assert.IsType<Node>(repository.ReadEvent(address)).Value);
    }

    [Fact]
    public void AllocationCallbacksCannotDisposeOrReenterReadOrAdvanceAnEnumerator() {
        Action? duringAllocate = null;
        using Repository repository = Create(SharedReadModel.Models(allocate: () => { duringAllocate?.Invoke(); return new Node(); }));
        using BranchCheckout branch = repository.CreateBranch("main", new Node { Value = 1 }, NoRebase);
        CheckpointAddress address = branch.CommitEvent(new Node { Value = 2 }, NoRebase);
        using IEnumerator<CheckpointAddress> events = repository.EnumerateEvents(address).GetEnumerator();
        duringAllocate = () => {
            Assert.Throws<InvalidOperationException>(() => repository.Dispose());
            Assert.Throws<InvalidOperationException>(() => branch.Dispose());
            Assert.Throws<InvalidOperationException>(() => repository.ReadEvent(address));
            Assert.Throws<InvalidOperationException>(() => events.MoveNext());
        };
        Assert.Equal(address, repository.ReadCheckpoint(address).Address);
        duringAllocate = null;
        Assert.Equal(GraphFrameKind.State, branch.CommitState(NoRebase).Kind);
    }

    [Fact]
    public void MissingGraphCapabilityDoesNotBecomeAnAbsentPreviousGraph() {
        using (Repository repository = Create()) {
            using BranchCheckout branch = repository.CreateBranch("main", new Node { Value = 1 }, NoRebase);
            branch.CommitEvent(new Node { Value = 2 }, NoRebase);
        }
        using Repository empty = Repository.OpenExisting(_root, new StateModelRegistry());
        Checkpoint? delivered = null;
        Assert.ThrowsAny<Exception>(() => delivered = empty.ReadCheckpoint(empty.GetHead("main")));
        Assert.Null(delivered);
        Assert.False(empty.IsFaulted);
        Assert.Single(empty.EnumerateEvents(empty.GetHead("main")));
    }

    private static Node Root(Checkpoint checkpoint) => Assert.IsType<Node>(checkpoint is EventCheckpoint e ? e.Event : ((StateCheckpoint)checkpoint).State);
    private static Node Previous(Checkpoint checkpoint) => Assert.IsType<Node>(checkpoint is EventCheckpoint e ? e.PreviousState : ((StateCheckpoint)checkpoint).PreviousEvent);
    private Repository Create(StateModelRegistry? models = null) => Repository.CreateNew(_root, models ?? SharedReadModel.Models(),
        new() { NewStoreLayout = RbfSegmentStoreLayout.Flat });
    public void Dispose() => SharedReadModel.DeleteFixture(_root, "durable-db078-checkpoint-");
}
