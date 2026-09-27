using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.RbfSegmentStore;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;
using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;

namespace Atelia.DurableGraph.Persistence.Tests;

/// <summary>Product-path witnesses for the single prepared State slot and its delivery boundary.</summary>
public sealed class DB080PreparedStateReuseTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"durable-db080-reuse-{Guid.NewGuid():N}");
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1000000, 1);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedForkAndCheckoutKeepCyclesContainersAndIndependentSavingBaselines(bool reuse) {
        using (Repository repository = Create()) {
            repository.PreparedStateReuseEnabled = reuse;
            using BranchCheckout main = repository.CreateBranch("main", Cycle(), NoRebase);
            CheckpointAddress source = main.Head;
            GraphReadStatistics first = Observe(repository);
            using BranchCheckout a = repository.Fork("a", source);
            AssertCold(first);
            GraphReadStatistics second = Observe(repository);
            using BranchCheckout b = repository.Fork("b", source);
            AssertRepeat(second, reuse);
            Assert.Equal(first.AllocatedObjects, second.AllocatedObjects);
            Assert.Equal(first.HydratedObjects, second.HydratedObjects);
            Node rootA = Assert.IsType<Node>(a.State), rootB = Assert.IsType<Node>(b.State);
            AssertGraph(rootA);
            AssertGraph(rootB);
            Assert.NotSame(main.State, rootA);
            Assert.NotSame(rootA, rootB);
            Assert.NotSame(rootA.Next, rootB.Next);
            Assert.NotSame(rootA.Links, rootB.Links);
            rootA.Next!.Value = 21;
            rootA.Links!.Add(rootA);
            rootB.Next!.Value = 22;
            Assert.Equal((byte)2, Assert.IsType<Node>(main.State).Next!.Value);
            Assert.Equal(source.Address, a.CommitState(NoRebase).Parent);
            Assert.Equal(source.Address, b.CommitState(NoRebase).Parent);
            Assert.Equal(source, main.Head);
        }
        using Repository reopened = Repository.OpenExisting(_root, SharedReadModel.Models());
        reopened.PreparedStateReuseEnabled = reuse;
        Assert.Null(reopened.PreparedStateEntry);
        Node previous;
        GraphReadStatistics cold = Observe(reopened);
        using (BranchCheckout a = reopened.Checkout("a")) {
            previous = Assert.IsType<Node>(a.State);
            Assert.Equal((byte)21, previous.Next!.Value);
            Assert.Equal(3, previous.Links!.Count);
            Assert.Same(previous, previous.Links[2]);
            AssertCold(cold);
            GraphReadStatistics occupied = Observe(reopened);
            Assert.Throws<InvalidOperationException>(() => reopened.Checkout("a"));
            AssertNoGraphWork(occupied);
        }
        GraphReadStatistics repeat = Observe(reopened);
        using BranchCheckout again = reopened.Checkout("a");
        AssertRepeat(repeat, reuse);
        Assert.NotSame(previous, again.State);
        using BranchCheckout reopenedB = reopened.Checkout("b");
        Assert.Equal((byte)22, Assert.IsType<Node>(reopenedB.State).Next!.Value);
        Assert.Equal(2, Assert.IsType<Node>(reopenedB.State).Links!.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConsecutiveEventsReuseOnlyNearestStateAndKeepEveryRequestedHead(bool reuse) {
        List<byte> hydrated = [];
        using Repository repository = Create(SharedReadModel.Models(hydrate: node => hydrated.Add(node.Value)));
        repository.PreparedStateReuseEnabled = reuse;
        using BranchCheckout main = repository.CreateBranch("main", Cycle(), NoRebase);
        CheckpointAddress state = main.Head;
        CheckpointAddress e1 = main.CommitEvent(new Node { Value = 51 }, NoRebase);
        CheckpointAddress e2 = main.CommitEvent(new Node { Value = 52 }, NoRebase);
        CheckpointAddress[] requests = [state, e1, e2];
        for (int i = 0; i < requests.Length; i++) {
            hydrated.Clear();
            GraphReadStatistics stats = Observe(repository);
            using BranchCheckout child = repository.Fork($"child{i}", requests[i]);
            if (i == 0) AssertCold(stats); else AssertRepeat(stats, reuse);
            Assert.Equal(requests[i], child.Head);
            Assert.Equal(requests[i], repository.GetHead($"child{i}"));
            Assert.Equal(new byte[] { 1, 2 }, hydrated.Order());
            AssertGraph(Assert.IsType<Node>(child.State));
            if (reuse) Assert.Equal(state, repository.PreparedStateEntry!.StateCheckpoint);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EventOnlyHistoryDoesNoGraphWorkAndLeavesExistingStateSlotAlone(bool reuse) {
        using Repository repository = Create();
        repository.PreparedStateReuseEnabled = reuse;
        using BranchCheckout main = repository.CreateBranch("main", Cycle(), NoRebase);
        using BranchCheckout first = repository.Fork("first", main.Head);
        var prepared = repository.PreparedStateEntry;
        using BranchCheckout events = repository.CreateBranchFromEvent("events", new Node { Value = 50 }, NoRebase);
        CheckpointAddress e2 = events.CommitEvent(new Node { Value = 51 }, NoRebase);
        repository.CreateBranch("event-copy", e2);
        GraphReadStatistics stats = Observe(repository);
        using BranchCheckout emptyFork = repository.Fork("empty-fork", e2);
        using BranchCheckout emptyCheckout = repository.Checkout("event-copy");
        Assert.Null(emptyFork.State);
        Assert.Null(emptyCheckout.State);
        Assert.Equal(e2, emptyFork.Head);
        Assert.Equal(e2, emptyCheckout.Head);
        AssertNoGraphWork(stats);
        Assert.Same(prepared, repository.PreparedStateEntry);
        // The first State after the Event-only prefix must establish its own preparation.
        CheckpointAddress firstState = emptyFork.CommitState(new Node { Value = 60 }, NoRebase);
        stats = Observe(repository);
        using BranchCheckout stateFork = repository.Fork("first-state", firstState);
        AssertCold(stats);
        Assert.Equal((byte)60, Assert.IsType<Node>(stateFork.State).Value);
        Assert.Equal(firstState, stateFork.Head);
        if (reuse) Assert.NotSame(prepared, repository.PreparedStateEntry);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewRevisionAndPromotedChildGetNewPreparationAndEvictedStateRemainsSavable(bool reuse) {
        using Repository repository = Create();
        repository.PreparedStateReuseEnabled = reuse;
        using BranchCheckout main = repository.CreateBranch("main", Cycle(), NoRebase);
        CheckpointAddress original = main.Head;
        using BranchCheckout old = repository.Fork("old", original);
        var firstEntry = repository.PreparedStateEntry;
        // Even an unchanged State commit has a new revision validation obligation.
        CheckpointAddress unchanged = main.CommitState(NoRebase);
        Assert.NotEqual(original.RevisionAddress, unchanged.RevisionAddress);
        GraphReadStatistics stats = Observe(repository);
        using BranchCheckout sameValues = repository.Fork("same-values", unchanged);
        AssertCold(stats);
        if (reuse) Assert.NotSame(firstEntry, repository.PreparedStateEntry);
        Node promoted = Assert.IsType<Node>(main.State).Next!;
        CheckpointAddress promotedAddress = main.CommitState(promoted, NoRebase);
        Assert.NotEqual(original.RootId, promotedAddress.RootId);
        stats = Observe(repository);
        using BranchCheckout childRoot = repository.Fork("child-root", promotedAddress);
        AssertCold(stats);
        Node restored = Assert.IsType<Node>(childRoot.State);
        Assert.Equal((byte)2, restored.Value);
        Assert.Same(restored, restored.Next!.Next);
        Assert.Equal(promotedAddress.RootId, childRoot.CommitState(NoRebase).RootId);
        // An active older checkout retains its own complete save provenance after eviction.
        Assert.IsType<Node>(old.State).Value = 71;
        CheckpointAddress savedOld = old.CommitState(NoRebase);
        Assert.Equal(original.RootId, savedOld.RootId);
        Assert.Equal(original.Address, savedOld.Parent);
        stats = Observe(repository);
        using BranchCheckout originalAgain = repository.Fork("original-again", original);
        AssertCold(stats);
        Assert.Equal((byte)1, Assert.IsType<Node>(originalAgain.State).Value);
        Assert.Equal((byte)71, Assert.IsType<Node>(repository.ReadState(savedOld)).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrossTypeStateReplacementSelectsItsActualRootBinding(bool reuse) {
        StateModelRegistry models = SharedReadModel.Models();
        models.Register(OtherModel());
        using Repository repository = Create(models);
        repository.PreparedStateReuseEnabled = reuse;
        using BranchCheckout main = repository.CreateBranch("main", new Node { Value = 1 }, NoRebase);
        using BranchCheckout old = repository.Fork("old", main.Head);
        var previous = repository.PreparedStateEntry;
        CheckpointAddress replaced = main.CommitState(new Other { Value = 42 }, NoRebase);
        GraphReadStatistics stats = Observe(repository);
        using BranchCheckout cold = repository.Fork("cold", replaced);
        AssertCold(stats);
        Assert.Equal((byte)42, Assert.IsType<Other>(cold.State).Value);
        if (reuse) Assert.NotSame(previous, repository.PreparedStateEntry);
        stats = Observe(repository);
        using BranchCheckout repeat = repository.Fork("repeat", replaced);
        AssertRepeat(stats, reuse);
        Assert.NotSame(cold.State, repeat.State);
        Assert.IsType<Other>(repeat.State).Value = 43;
        Assert.Equal(replaced.RootId, repeat.CommitState(NoRebase).RootId);
        Assert.Equal((byte)42, Assert.IsType<Other>(cold.State).Value);
        Assert.IsType<Node>(old.State);
    }

    [Theory]
    [InlineData(false, "normalize")]
    [InlineData(true, "normalize")]
    [InlineData(false, "allocate")]
    [InlineData(true, "allocate")]
    [InlineData(false, "hydrate")]
    [InlineData(true, "hydrate")]
    public void FailedColdReplacementPreservesOldSlotAndAllowsRetry(bool reuse, string phase) {
        using (Repository seed = Create()) {
            using BranchCheckout main = seed.CreateBranch("main", Cycle(), NoRebase);
            using BranchCheckout other = seed.CreateBranch("other", new Node {
                Value = 10, Next = new Node { Value = 20 }
            }, NoRebase);
        }
        bool fail = false;
        int allocations = 0, normalizations = 0;
        InvalidDataException failure = new($"late {phase} failure");
        using Repository repository = Repository.OpenExisting(_root, SharedReadModel.Models(currentVersion: 2,
            normalize: row => {
                normalizations++;
                var state = row.GetState<SharedReadModel.State>();
                if (fail && phase == "normalize" && state.Value == 20) throw failure;
                return state;
            }, allocate: () => {
                if (++allocations == 2 && fail && phase == "allocate") throw failure;
                return new Node();
            }, hydrate: node => {
                if (fail && phase == "hydrate" && node.Value == 20) throw failure;
            }));
        repository.PreparedStateReuseEnabled = reuse;
        using BranchCheckout warm = repository.Fork("warm", repository.GetHead("main"));
        var successfulEntry = repository.PreparedStateEntry;
        CheckpointAddress source = repository.GetHead("other");
        fail = true;
        allocations = normalizations = 0;
        BranchCheckout? delivered = null;
        Assert.Same(failure, Assert.Throws<InvalidDataException>(() => delivered = repository.Fork("failed", source)));
        Assert.Null(delivered);
        Assert.True(normalizations > 0);
        Assert.Same(successfulEntry, repository.PreparedStateEntry);
        Assert.DoesNotContain("failed", repository.ListBranches());
        Assert.Equal(source, repository.GetHead("other"));
        Assert.False(repository.IsFaulted);
        if (phase == "normalize") Assert.Equal(0, allocations);
        // A failed scope and failed branch reservation must not affect the retry.
        fail = false;
        allocations = normalizations = 0;
        GraphReadStatistics stats = Observe(repository);
        using BranchCheckout retry = repository.Fork("failed", source);
        AssertCold(stats);
        Assert.Equal(2, normalizations);
        Assert.Equal((byte)20, Assert.IsType<Node>(retry.State).Next!.Value);
        if (reuse) Assert.NotSame(successfulEntry, repository.PreparedStateEntry);
        Assert.Equal((byte)1, Assert.IsType<Node>(warm.State).Value);
        warm.CommitState(NoRebase);
    }

    [Theory]
    [InlineData("allocate")]
    [InlineData("hydrate")]
    public void FailedHitDeliversNoCheckoutAndPreservesSuccessfulSlot(string phase) {
        bool fail = false;
        int allocations = 0;
        InvalidDataException failure = new($"hit {phase} failure");
        using Repository repository = Create(SharedReadModel.Models(allocate: () => {
            if (++allocations == 2 && fail && phase == "allocate") throw failure;
            return new Node();
        }, hydrate: node => {
            if (fail && phase == "hydrate" && node.Value == 2) throw failure;
        }));
        using BranchCheckout main = repository.CreateBranch("main", Cycle(), NoRebase);
        using BranchCheckout warm = repository.Fork("warm", main.Head);
        var entry = repository.PreparedStateEntry;
        Assert.NotNull(entry);
        fail = true;
        allocations = 0;
        GraphReadStatistics stats = Observe(repository);
        BranchCheckout? delivered = null;
        Assert.Same(failure, Assert.Throws<InvalidDataException>(() => delivered = repository.Fork("retry", main.Head)));
        Assert.Null(delivered);
        Assert.Equal(1, stats.PreparedStateHits);
        Assert.Equal(0, stats.DecodedObjects);
        Assert.Equal(0, stats.NormalizedObjects);
        Assert.Same(entry, repository.PreparedStateEntry);
        Assert.DoesNotContain("retry", repository.ListBranches());
        Assert.False(repository.IsFaulted);
        fail = false;
        stats = Observe(repository);
        using BranchCheckout retry = repository.Fork("retry", main.Head);
        AssertRepeat(stats, true);
        AssertGraph(Assert.IsType<Node>(retry.State));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealUpgradeNormalizeRunsOnColdOnlyAndReadonlyQueriesNeverUseTheSlot(bool reuse) {
        using (Repository seed = Create()) {
            using BranchCheckout main = seed.CreateBranch("main", new Node { Value = 1 }, NoRebase);
            main.CommitEvent(new Node { Value = 2 }, NoRebase);
        }
        int normalized = 0;
        using Repository repository = Repository.OpenExisting(_root, SharedReadModel.Models(currentVersion: 2,
            normalize: row => { normalized++; return row.GetState<SharedReadModel.State>(); }));
        repository.PreparedStateReuseEnabled = reuse;
        CheckpointAddress head = repository.GetHead("main");
        CheckpointAddress state = repository.GetPreviousState(head)!;
        using BranchCheckout warm = repository.Fork("warm", head);
        Assert.Equal(1, normalized);
        var entry = repository.PreparedStateEntry;
        normalized = 0;
        using BranchCheckout repeat = repository.Fork("repeat", head);
        Assert.Equal(reuse ? 0 : 1, normalized);
        normalized = 0;
        repository.ReadCheckpoint(head);
        Assert.Equal(2, normalized);
        normalized = 0;
        repository.ReadState(state);
        Assert.Equal(1, normalized);
        normalized = 0;
        repository.ReadEvent(head);
        Assert.Equal(1, normalized);
        normalized = 0;
        repository.ReadPair(state, head);
        Assert.Equal(2, normalized);
        Assert.Same(entry, repository.PreparedStateEntry);
        GraphReadStatistics stats = Observe(repository);
        using BranchCheckout afterReads = repository.Fork("after-reads", head);
        AssertRepeat(stats, reuse);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForkPublicationFailurePreservesOldSlotOrReleasesItOnFault(bool afterPublication) {
        using Repository repository = Create();
        using BranchCheckout main = repository.CreateBranch("main", new Node { Value = 1 }, NoRebase);
        using BranchCheckout warm = repository.Fork("warm", main.Head);
        var previous = repository.PreparedStateEntry;
        Assert.NotNull(previous);
        CheckpointAddress newer = main.CommitState(new Node { Value = 2 }, NoRebase);
        repository.Checkpoint = point => {
            if (point == (afterPublication ? CommitCheckpoint.AfterPublication : CommitCheckpoint.BeforePublication)) {
                throw new IOException("fork publication interruption");
            }
        };
        BranchCheckout? delivered = null;
        GraphCommitException failure = Assert.Throws<GraphCommitException>(() => delivered = repository.Fork("failed", newer));
        Assert.Null(delivered);
        Assert.Equal(afterPublication ? GraphCommitOutcome.Published : GraphCommitOutcome.NotPublished, failure.Outcome);
        Assert.Equal(afterPublication, repository.IsFaulted);
        repository.Checkpoint = null;
        if (afterPublication) {
            Assert.Null(repository.PreparedStateEntry);
            Assert.Throws<InvalidOperationException>(() => repository.Fork("blocked", newer));
        } else {
            Assert.Same(previous, repository.PreparedStateEntry);
            Assert.DoesNotContain("failed", repository.ListBranches());
            GraphReadStatistics stats = Observe(repository);
            using BranchCheckout retry = repository.Fork("failed", newer);
            AssertCold(stats);
            Assert.NotSame(previous, repository.PreparedStateEntry);
        }
        Assert.Equal((byte)1, Assert.IsType<Node>(warm.State).Value);
    }

    [Fact]
    public void DisposeReleasesSlotWithoutDestroyingDeliveredDomainGraph() {
        Repository repository = Create();
        Node delivered;
        CheckpointAddress source;
        try {
            using BranchCheckout main = repository.CreateBranch("main", Cycle(), NoRebase);
            source = main.Head;
            using BranchCheckout fork = repository.Fork("fork", source);
            delivered = Assert.IsType<Node>(fork.State);
            Assert.NotNull(repository.PreparedStateEntry);
        } finally { repository.Dispose(); }
        Assert.Null(repository.PreparedStateEntry);
        AssertGraph(delivered);
        Assert.Throws<ObjectDisposedException>(() => repository.Fork("blocked", source));
    }

    [Fact]
    public void NewRevisionWithInheritedRootHeadStillValidatesItsOwnMissingReference() {
        FrameAddress original;
        ObjectId rootId;
        using (Repository seed = Create()) {
            using BranchCheckout main = seed.CreateBranch("main", new Node { Value = 1, Next = new Node { Value = 2 } }, NoRebase);
            original = main.Head.RevisionAddress;
            rootId = main.Head.RootId;
        }
        FrameAddress invalid;
        using (SegmentStore segments = SegmentStore.OpenExisting(Path.Combine(_root, "state"))) {
            using StateRevisionStore states = new(segments);
            uint childId = Assert.Single(states.Read(original).LocalObjects, row => row.ObjectId != rootId.Value).ObjectId;
            // Root's exact object head is inherited unchanged, but this revision removes its child.
            invalid = states.AppendDurably(StateRevision.CreateObjectHeadMapDelta(original, [], [childId]));
        }
        using (HistoryJournal history = HistoryJournal.Open(_root, readOnly: false)) {
            history.ConfirmDurable();
            var branch = history.Journal.OpenBranch("main").Unwrap();
            var parent = history.Journal.GetHead(branch);
            var record = history.Append(GraphFrameKind.State, invalid, rootId, parent);
            history.Journal.CreateBranch("invalid", record).Unwrap();
        }
        // Physical history/root checks permit opening; typed graph validation must still reject.
        using Repository repository = Repository.OpenExisting(_root, SharedReadModel.Models());
        using BranchCheckout warm = repository.Fork("warm", repository.GetHead("main"));
        var previous = repository.PreparedStateEntry;
        Assert.NotNull(previous);
        GraphReadStatistics stats = Observe(repository);
        BranchCheckout? delivered = null;
        Assert.Throws<InvalidDataException>(() => delivered = repository.Fork("failed", repository.GetHead("invalid")));
        Assert.Null(delivered);
        Assert.Equal(0, stats.PreparedStateHits);
        Assert.Equal(1, stats.PreparedStateMisses);
        Assert.Equal(0, stats.AllocatedObjects);
        Assert.Equal(0, stats.HydratedObjects);
        Assert.Same(previous, repository.PreparedStateEntry);
        Assert.DoesNotContain("failed", repository.ListBranches());
        Assert.False(repository.IsFaulted);
        stats = Observe(repository);
        using BranchCheckout goodAgain = repository.Fork("good-again", repository.GetHead("main"));
        AssertRepeat(stats, true);
        Assert.Equal((byte)2, Assert.IsType<Node>(goodAgain.State).Next!.Value);
    }

    private static GraphReadStatistics Observe(Repository repository) {
        GraphReadStatistics stats = new();
        repository.RestorationStatistics = stats;
        return stats;
    }

    private static void AssertCold(GraphReadStatistics stats) {
        Assert.True(stats.DecodedObjects > 0);
        Assert.True(stats.NormalizedObjects > 0);
        Assert.True(stats.PreparedGraphs > 0);
        Assert.True(stats.ReachabilityVisits > 0);
        Assert.True(stats.CurrentReferenceValidationVisits > 0);
        Assert.Equal(0, stats.PreparedStateHits);
    }

    private static void AssertRepeat(GraphReadStatistics stats, bool reuse) {
        if (!reuse) { AssertCold(stats); return; }
        Assert.Equal(1, stats.PreparedStateHits);
        Assert.Equal(0, stats.PreparedStateMisses);
        Assert.Equal(0, stats.DecodedObjects);
        Assert.Equal(0, stats.NormalizedObjects);
        Assert.Equal(0, stats.PreparedGraphs);
        Assert.Equal(0, stats.ReachabilityVisits);
        Assert.Equal(0, stats.CurrentReferenceValidationVisits);
        Assert.Equal(0, stats.DictionaryValidationCalls);
        Assert.True(stats.AllocatedObjects > 0);
        Assert.True(stats.HydratedObjects > 0);
    }

    private static void AssertNoGraphWork(GraphReadStatistics stats) {
        Assert.Equal(0, stats.PreparedStateHits);
        Assert.Equal(0, stats.PreparedStateMisses);
        Assert.Equal(0, stats.DecodedObjects);
        Assert.Equal(0, stats.NormalizedObjects);
        Assert.Equal(0, stats.PreparedGraphs);
        Assert.Equal(0, stats.ReachabilityVisits);
        Assert.Equal(0, stats.CurrentReferenceValidationVisits);
        Assert.Equal(0, stats.DictionaryValidationCalls);
        Assert.Equal(0, stats.AllocatedObjects);
        Assert.Equal(0, stats.HydratedObjects);
    }

    private static Node Cycle() {
        Node root = new() { Value = 1 };
        Node child = new() { Value = 2, Next = root, Alias = root };
        root.Next = root.Alias = child;
        root.Links = [child, child];
        return root;
    }

    private static void AssertGraph(Node root) {
        Assert.Equal((byte)1, root.Value);
        Assert.Equal((byte)2, root.Next!.Value);
        Assert.Same(root.Next, root.Alias);
        Assert.Same(root, root.Next.Next);
        Assert.Same(root, root.Next.Alias);
        Assert.Same(root.Next, root.Links![0]);
        Assert.Same(root.Links[0], root.Links[1]);
    }

    private sealed class Other : IDurableObject { internal byte Value; }

    private static StateModelBinding OtherModel() {
        DurableSchema schema = new("Db080Other", 1, new DurableFieldInfo(1, TypeTag.Byte));
        static PreparedBaseBody Base(in byte value) => new(new byte[] { value });
        static void Visit(in byte _, IStateReferenceVisitor __) { }
        CapturedStatePreparation<byte> preparation = new(schema, Base,
            static (in byte previous, in byte current) => new(previous != current, Base(current).Body));
        return new StateModelBinding<Other, byte>(preparation,
            [new StateReaderBinding<byte>(schema, static (ref BinaryPayloadReader input) => input.ReadByte(),
                static (ref BinaryPayloadReader input, in byte _) => input.ReadByte(), Visit)],
            static row => row.GetState<byte>(), static () => new Other(),
            static (Other target, in byte state, ObjectReadTable _) => target.Value = state,
            static (target, _) => target.Value, Visit);
    }

    private Repository Create(StateModelRegistry? models = null) => Repository.CreateNew(_root, models ?? SharedReadModel.Models(),
        new() { NewStoreLayout = RbfSegmentStoreLayout.Flat });

    public void Dispose() => SharedReadModel.DeleteFixture(_root, "durable-db080-reuse-");
}
