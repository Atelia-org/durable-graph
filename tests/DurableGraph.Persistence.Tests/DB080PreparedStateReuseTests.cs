using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;

using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using SegmentStore = Atelia.FrameStore.FrameStore;
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
            // An Event-only opening followed by State capture leaves this State unread;
            // creating a State root and opening it would already earn a read certificate.
            IDurableObject mainInitial = Cycle();
            using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("main", mainInitial).Id);
            main.CommitState(mainInitial, parameters: NoRebase);
            CommitId source = main.BaseCommitId;
            GraphReadStatistics first = Observe(repository);
            using WorkingCopy a = repository.OpenWorkingCopy(repository.ForkBranch(source, "a").Id);
            AssertCold(first);
            GraphReadStatistics second = Observe(repository);
            using WorkingCopy b = repository.OpenWorkingCopy(repository.ForkBranch(source, "b").Id);
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
            Assert.Equal(Describe(repository, source).Address, Snapshot(repository, a.CommitState(parameters: NoRebase).Id).SourceGraph);
            Assert.Equal(Describe(repository, source).Address, Snapshot(repository, b.CommitState(parameters: NoRebase).Id).SourceGraph);
            Assert.Equal(source, main.BaseCommitId);
        }
        using Repository reopened = Repository.OpenExisting(_root, SharedReadModel.Models());
        reopened.PreparedStateReuseEnabled = reuse;
        Assert.Null(reopened.PreparedStateEntry);
        Node previous;
        GraphReadStatistics cold = Observe(reopened);
        using (WorkingCopy a = reopened.OpenWorkingCopy(BranchNamed(reopened, "a"))) {
            previous = Assert.IsType<Node>(a.State);
            Assert.Equal((byte)21, previous.Next!.Value);
            Assert.Equal(3, previous.Links!.Count);
            Assert.Same(previous, previous.Links[2]);
            AssertCold(cold);
            GraphReadStatistics occupied = Observe(reopened);
            Assert.Throws<InvalidOperationException>(() => reopened.OpenWorkingCopy(BranchNamed(reopened, "a")));
            AssertNoGraphWork(occupied);
        }
        GraphReadStatistics repeat = Observe(reopened);
        using WorkingCopy again = reopened.OpenWorkingCopy(BranchNamed(reopened, "a"));
        AssertRepeat(repeat, reuse);
        Assert.NotSame(previous, again.State);
        using WorkingCopy reopenedB = reopened.OpenWorkingCopy(BranchNamed(reopened, "b"));
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
        IDurableObject mainInitial = Cycle();
        using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("main", mainInitial).Id);
        main.CommitState(mainInitial, parameters: NoRebase);
        CommitId state = main.BaseCommitId;
        CommitId e1 = main.CommitEvent(new Node { Value = 51 }).Id;
        CommitId e2 = main.CommitEvent(new Node { Value = 52 }).Id;
        CommitId[] requests = [state, e1, e2];
        for (int i = 0; i < requests.Length; i++) {
            hydrated.Clear();
            GraphReadStatistics stats = Observe(repository);
            using WorkingCopy child = repository.OpenWorkingCopy(repository.ForkBranch(requests[i], $"child{i}").Id);
            if (i == 0) AssertCold(stats); else AssertRepeat(stats, reuse);
            Assert.Equal(requests[i], child.BaseCommitId);
            Assert.Equal(requests[i], repository.ReadCurrentCommit(BranchNamed(repository, $"child{i}")).Commit.Id);
            Assert.Equal(new byte[] { 1, 2 }, hydrated.Order());
            AssertGraph(Assert.IsType<Node>(child.State));
            if (reuse) Assert.Equal(state, new CommitId(repository.PreparedStateEntry!.StateCheckpoint.Id));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EventOnlyHistoryDoesNoGraphWorkAndLeavesExistingStateSlotAlone(bool reuse) {
        using Repository repository = Create();
        repository.PreparedStateReuseEnabled = reuse;
        IDurableObject mainInitial = Cycle();
        using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("main", mainInitial).Id);
        main.CommitState(mainInitial, parameters: NoRebase);
        using WorkingCopy first = repository.OpenWorkingCopy(repository.ForkBranch(main.BaseCommitId, "first").Id);
        var prepared = repository.PreparedStateEntry;
        using WorkingCopy events = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("events", new Node { Value = 50 }).Id);
        CommitId e2 = events.CommitEvent(new Node { Value = 51 }).Id;
        repository.ForkBranch(e2, "event-copy");
        GraphReadStatistics stats = Observe(repository);
        using WorkingCopy emptyFork = repository.OpenWorkingCopy(repository.ForkBranch(e2, "empty-fork").Id);
        using WorkingCopy emptyCheckout = repository.OpenWorkingCopy(BranchNamed(repository, "event-copy"));
        Assert.Null(emptyFork.State);
        Assert.Null(emptyCheckout.State);
        Assert.Equal(e2, emptyFork.BaseCommitId);
        Assert.Equal(e2, emptyCheckout.BaseCommitId);
        AssertNoGraphWork(stats);
        Assert.Same(prepared, repository.PreparedStateEntry);
        // The first State after the Event-only prefix must establish its own preparation.
        CommitId firstState = emptyFork.CommitState(new Node { Value = 60 }, parameters: NoRebase).Id;
        stats = Observe(repository);
        using WorkingCopy stateFork = repository.OpenWorkingCopy(repository.ForkBranch(firstState, "first-state").Id);
        AssertCold(stats);
        Assert.Equal((byte)60, Assert.IsType<Node>(stateFork.State).Value);
        Assert.Equal(firstState, stateFork.BaseCommitId);
        if (reuse) Assert.NotSame(prepared, repository.PreparedStateEntry);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewRevisionAndPromotedChildGetNewPreparationAndEvictedStateRemainsSavable(bool reuse) {
        using Repository repository = Create();
        repository.PreparedStateReuseEnabled = reuse;
        IDurableObject mainInitial = Cycle();
        using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("main", mainInitial).Id);
        main.CommitState(mainInitial, parameters: NoRebase);
        CommitId original = main.BaseCommitId;
        using WorkingCopy old = repository.OpenWorkingCopy(repository.ForkBranch(original, "old").Id);
        var firstEntry = repository.PreparedStateEntry;
        // Even an unchanged State commit has a new revision validation obligation.
        CommitId unchanged = main.CommitState(parameters: NoRebase).Id;
        Assert.NotEqual(Describe(repository, original).Address, Describe(repository, unchanged).Address);
        GraphReadStatistics stats = Observe(repository);
        using WorkingCopy sameValues = repository.OpenWorkingCopy(repository.ForkBranch(unchanged, "same-values").Id);
        AssertCold(stats);
        if (reuse) Assert.NotSame(firstEntry, repository.PreparedStateEntry);
        Node promoted = Assert.IsType<Node>(main.State).Next!;
        CommitId promotedAddress = main.CommitState(promoted, parameters: NoRebase).Id;
        Assert.NotEqual(Describe(repository, original).RootId, Describe(repository, promotedAddress).RootId);
        stats = Observe(repository);
        using WorkingCopy childRoot = repository.OpenWorkingCopy(repository.ForkBranch(promotedAddress, "child-root").Id);
        AssertCold(stats);
        Node restored = Assert.IsType<Node>(childRoot.State);
        Assert.Equal((byte)2, restored.Value);
        Assert.Same(restored, restored.Next!.Next);
        Assert.Equal(Describe(repository, promotedAddress).RootId, Describe(repository, childRoot.CommitState(parameters: NoRebase).Id).RootId);
        // An active older checkout retains its own complete save provenance after eviction.
        Assert.IsType<Node>(old.State).Value = 71;
        CommitId savedOld = old.CommitState(parameters: NoRebase).Id;
        Assert.Equal(Describe(repository, original).RootId, Describe(repository, savedOld).RootId);
        Assert.Equal(Describe(repository, original).Address, Snapshot(repository, savedOld).SourceGraph);
        stats = Observe(repository);
        using WorkingCopy originalAgain = repository.OpenWorkingCopy(repository.ForkBranch(original, "original-again").Id);
        AssertCold(stats);
        Assert.Equal((byte)1, Assert.IsType<Node>(originalAgain.State).Value);
        Assert.Equal((byte)71, Assert.IsType<Node>(repository.LoadState(savedOld)).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrossTypeStateReplacementSelectsItsActualRootBinding(bool reuse) {
        StateModelRegistry models = SharedReadModel.Models();
        models.Register(OtherModel());
        using Repository repository = Create(models);
        repository.PreparedStateReuseEnabled = reuse;
        IDurableObject mainInitial = new Node { Value = 1 };
        using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("main", mainInitial).Id);
        main.CommitState(mainInitial, parameters: NoRebase);
        using WorkingCopy old = repository.OpenWorkingCopy(repository.ForkBranch(main.BaseCommitId, "old").Id);
        var previous = repository.PreparedStateEntry;
        CommitId replaced = main.CommitState(new Other { Value = 42 }, parameters: NoRebase).Id;
        GraphReadStatistics stats = Observe(repository);
        using WorkingCopy cold = repository.OpenWorkingCopy(repository.ForkBranch(replaced, "cold").Id);
        AssertCold(stats);
        Assert.Equal((byte)42, Assert.IsType<Other>(cold.State).Value);
        if (reuse) Assert.NotSame(previous, repository.PreparedStateEntry);
        stats = Observe(repository);
        using WorkingCopy repeat = repository.OpenWorkingCopy(repository.ForkBranch(replaced, "repeat").Id);
        AssertRepeat(stats, reuse);
        Assert.NotSame(cold.State, repeat.State);
        Assert.IsType<Other>(repeat.State).Value = 43;
        Assert.Equal(Describe(repository, replaced).RootId, Describe(repository, repeat.CommitState(parameters: NoRebase).Id).RootId);
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
            IDurableObject mainInitial = Cycle();
            using WorkingCopy main = seed.OpenWorkingCopy(seed.CreateRootBranchFromEvent("main", mainInitial).Id);
            main.CommitState(mainInitial, parameters: NoRebase);
            IDurableObject otherInitial = new Node {
                Value = 10, Next = new Node { Value = 20 }
            };
            using WorkingCopy other = seed.OpenWorkingCopy(seed.CreateRootBranchFromEvent("other", otherInitial).Id);
            other.CommitState(otherInitial, parameters: NoRebase);
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
        using WorkingCopy warm = repository.OpenWorkingCopy(repository.ForkBranch(repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id, "warm").Id);
        var successfulEntry = repository.PreparedStateEntry;
        CommitId source = repository.ReadCurrentCommit(BranchNamed(repository, "other")).Commit.Id;
        fail = true;
        allocations = normalizations = 0;
        WorkingCopy? delivered = null;
        Assert.Same(failure, Assert.Throws<InvalidDataException>(() => delivered = repository.OpenWorkingCopy(repository.ForkBranch(source, "failed").Id)));
        Assert.Null(delivered);
        Assert.True(normalizations > 0);
        Assert.Same(successfulEntry, repository.PreparedStateEntry);
        Assert.Contains(repository.ListBranches(), branch => branch.DisplayLabel == "failed");
        Assert.Equal(source, repository.ReadCurrentCommit(BranchNamed(repository, "other")).Commit.Id);
        Assert.False(repository.IsFaulted);
        if (phase == "normalize") Assert.Equal(0, allocations);
        // The registered fork survives failed materialization; its edit reservation is released.
        fail = false;
        allocations = normalizations = 0;
        GraphReadStatistics stats = Observe(repository);
        using WorkingCopy retry = repository.OpenWorkingCopy(BranchNamed(repository, "failed"));
        AssertCold(stats);
        Assert.Equal(2, normalizations);
        Assert.Equal((byte)20, Assert.IsType<Node>(retry.State).Next!.Value);
        if (reuse) Assert.NotSame(successfulEntry, repository.PreparedStateEntry);
        Assert.Equal((byte)1, Assert.IsType<Node>(warm.State).Value);
        warm.CommitState(parameters: NoRebase);
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
        IDurableObject mainInitial = Cycle();
        using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("main", mainInitial).Id);
        main.CommitState(mainInitial, parameters: NoRebase);
        using WorkingCopy warm = repository.OpenWorkingCopy(repository.ForkBranch(main.BaseCommitId, "warm").Id);
        var entry = repository.PreparedStateEntry;
        Assert.NotNull(entry);
        fail = true;
        allocations = 0;
        GraphReadStatistics stats = Observe(repository);
        WorkingCopy? delivered = null;
        Assert.Same(failure, Assert.Throws<InvalidDataException>(() => delivered = repository.OpenWorkingCopy(repository.ForkBranch(main.BaseCommitId, "retry").Id)));
        Assert.Null(delivered);
        Assert.Equal(1, stats.PreparedStateHits);
        Assert.Equal(0, stats.DecodedObjects);
        Assert.Equal(0, stats.NormalizedObjects);
        Assert.Same(entry, repository.PreparedStateEntry);
        Assert.Contains(repository.ListBranches(), branch => branch.DisplayLabel == "retry");
        Assert.False(repository.IsFaulted);
        fail = false;
        stats = Observe(repository);
        using WorkingCopy retry = repository.OpenWorkingCopy(BranchNamed(repository, "retry"));
        AssertRepeat(stats, true);
        AssertGraph(Assert.IsType<Node>(retry.State));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealUpgradeNormalizeRunsOnColdOnlyAndReadonlyQueriesNeverUseTheSlot(bool reuse) {
        using (Repository seed = Create()) {
            IDurableObject mainInitial = new Node { Value = 1 };
            using WorkingCopy main = seed.OpenWorkingCopy(seed.CreateRootBranchFromEvent("main", mainInitial).Id);
            main.CommitState(mainInitial, parameters: NoRebase);
            main.CommitEvent(new Node { Value = 2 });
        }
        int normalized = 0;
        using Repository repository = Repository.OpenExisting(_root, SharedReadModel.Models(currentVersion: 2,
            normalize: row => { normalized++; return row.GetState<SharedReadModel.State>(); }));
        repository.PreparedStateReuseEnabled = reuse;
        CommitId head = repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id;
        CommitId state = repository.GetPreviousState(head)!.Value;
        using WorkingCopy warm = repository.OpenWorkingCopy(repository.ForkBranch(head, "warm").Id);
        Assert.Equal(1, normalized);
        var entry = repository.PreparedStateEntry;
        normalized = 0;
        using WorkingCopy repeat = repository.OpenWorkingCopy(repository.ForkBranch(head, "repeat").Id);
        Assert.Equal(reuse ? 0 : 1, normalized);
        normalized = 0;
        repository.ReadCheckpoint(head);
        Assert.Equal(2, normalized);
        normalized = 0;
        repository.LoadState(state);
        Assert.Equal(1, normalized);
        normalized = 0;
        repository.LoadEvent(head);
        Assert.Equal(1, normalized);
        normalized = 0;
        repository.ReadPair(state, head);
        Assert.Equal(2, normalized);
        Assert.Same(entry, repository.PreparedStateEntry);
        GraphReadStatistics stats = Observe(repository);
        using WorkingCopy afterReads = repository.OpenWorkingCopy(repository.ForkBranch(head, "after-reads").Id);
        AssertRepeat(stats, reuse);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForkOperationFailurePreservesSlotBeforeCreationOrReleasesItAfterPublication(bool afterPublication) {
        using Repository repository = Create();
        IDurableObject mainInitial = new Node { Value = 1 };
        using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("main", mainInitial).Id);
        main.CommitState(mainInitial, parameters: NoRebase);
        using WorkingCopy warm = repository.OpenWorkingCopy(repository.ForkBranch(main.BaseCommitId, "warm").Id);
        var previous = repository.PreparedStateEntry;
        Assert.NotNull(previous);
        CommitId newer = main.CommitState(new Node { Value = 2 }, parameters: NoRebase).Id;
        repository.OperationStageObserver = point => {
            if (point == (afterPublication ? RepositoryOperationStage.AfterPublication : RepositoryOperationStage.BeforePublication)) {
                throw new IOException("fork publication interruption");
            }
        };
        WorkingCopy? delivered = null;
        Exception failure = Assert.IsAssignableFrom<Exception>(Record.Exception(() =>
            delivered = repository.OpenWorkingCopy(repository.ForkBranch(newer, "failed").Id)));
        Assert.Null(delivered);
        Assert.Equal(afterPublication, repository.IsFaulted);
        repository.OperationStageObserver = null;
        if (afterPublication) {
            Assert.Equal(RepositoryPublicationOutcome.Published, Assert.IsType<RepositoryPublicationException>(failure).Outcome);
            Assert.Null(repository.PreparedStateEntry);
            Assert.Throws<InvalidOperationException>(() => repository.ForkBranch(newer, "blocked"));
            main.Dispose();
            warm.Dispose();
            repository.Dispose();
            using Repository reopened = Repository.OpenExisting(_root, SharedReadModel.Models());
            Assert.Contains(reopened.ListBranches(), branch => branch.DisplayLabel == "failed");
            GraphReadStatistics stats = Observe(reopened);
            using WorkingCopy retry = reopened.OpenWorkingCopy(BranchNamed(reopened, "failed"));
            AssertCold(stats);
            Assert.NotSame(previous, reopened.PreparedStateEntry);
        } else {
            // The public pre-operation observer runs before any physical fork or D append.
            Assert.IsType<IOException>(failure);
            Assert.Same(previous, repository.PreparedStateEntry);
            Assert.DoesNotContain(repository.ListBranches(), branch => branch.DisplayLabel == "failed");
            GraphReadStatistics stats = Observe(repository);
            using WorkingCopy retry = repository.OpenWorkingCopy(repository.ForkBranch(newer, "failed").Id);
            AssertCold(stats);
            Assert.NotSame(previous, repository.PreparedStateEntry);
        }
        Assert.Equal((byte)1, Assert.IsType<Node>(warm.State).Value);
    }

    [Fact]
    public void DisposeReleasesSlotWithoutDestroyingDeliveredDomainGraph() {
        Repository repository = Create();
        Node delivered;
        CommitId source;
        try {
            IDurableObject mainInitial = Cycle();
            using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("main", mainInitial).Id);
            main.CommitState(mainInitial, parameters: NoRebase);
            source = main.BaseCommitId;
            using WorkingCopy fork = repository.OpenWorkingCopy(repository.ForkBranch(source, "fork").Id);
            delivered = Assert.IsType<Node>(fork.State);
            Assert.NotNull(repository.PreparedStateEntry);
        } finally { repository.Dispose(); }
        Assert.Null(repository.PreparedStateEntry);
        AssertGraph(delivered);
        Assert.Throws<ObjectDisposedException>(() => repository.OpenWorkingCopy(repository.ForkBranch(source, "blocked").Id));
    }

    [Fact]
    public void NewRevisionWithInheritedRootHeadStillValidatesItsOwnMissingReference() {
        FrameAddress original;
        CommitId originalCommit;
        BranchId invalidBranch;
        ObjectId rootId;
        using (Repository seed = Create()) {
            IDurableObject mainInitial = new Node { Value = 1, Next = new Node { Value = 2 } };
            using WorkingCopy main = seed.OpenWorkingCopy(seed.CreateRootBranchFromEvent("main", mainInitial).Id);
            main.CommitState(mainInitial, parameters: NoRebase);
            originalCommit = main.BaseCommitId;
            original = Describe(seed, originalCommit).Address;
            rootId = Describe(seed, originalCommit).RootId;
            invalidBranch = seed.ForkBranch(originalCommit, "invalid").Id;
        }
        using (RepositoryResources resources = RepositoryResources.OpenExisting(_root)) {
            GraphRecordStore store = resources.GraphStore;
            StoredGraphRevision source = Snapshot(resources, originalCommit);
            uint childId = Assert.Single(source.ObjectHeads.Keys, id => id != rootId.Value);
            FrameAddress invalid = store.AppendBusiness(StoredGraphKind.State, source.SchemaRoot,
                new(original, rootId, [], [childId])).Address;
            resources.Heads.PublishHead(invalidBranch.Value, BusinessEntryPayloadCodec.Encode(invalid, default), out _).Unwrap();
        }
        // Physical history/root checks permit opening; typed graph validation must still reject.
        using Repository repository = Repository.OpenExisting(_root, SharedReadModel.Models());
        using WorkingCopy warm = repository.OpenWorkingCopy(repository.ForkBranch(repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id, "warm").Id);
        var previous = repository.PreparedStateEntry;
        Assert.NotNull(previous);
        GraphReadStatistics stats = Observe(repository);
        WorkingCopy? delivered = null;
        Assert.Throws<InvalidDataException>(() => delivered = repository.OpenWorkingCopy(repository.ForkBranch(repository.ReadCurrentCommit(BranchNamed(repository, "invalid")).Commit.Id, "failed").Id));
        Assert.Null(delivered);
        Assert.Equal(0, stats.PreparedStateHits);
        Assert.Equal(1, stats.PreparedStateMisses);
        Assert.Equal(0, stats.AllocatedObjects);
        Assert.Equal(0, stats.HydratedObjects);
        Assert.Same(previous, repository.PreparedStateEntry);
        Assert.Contains(repository.ListBranches(), branch => branch.DisplayLabel == "failed");
        Assert.False(repository.IsFaulted);
        stats = Observe(repository);
        using WorkingCopy goodAgain = repository.OpenWorkingCopy(repository.ForkBranch(repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id, "good-again").Id);
        AssertRepeat(stats, true);
        Assert.Equal((byte)2, Assert.IsType<Node>(goodAgain.State).Next!.Value);
    }

    private static GraphReadStatistics Observe(Repository repository) {
        GraphReadStatistics stats = new();
        repository.RestorationStatistics = stats;
        return stats;
    }

    private static void AssertCold(GraphReadStatistics stats) {
        // Split physical decoding is measured by its Store, independently of these
        // actual normalization/preparation/materialization counters.
        Assert.Equal(1, stats.PreparedStateMisses);
        Assert.True(stats.AllocatedObjects > 0);
        Assert.True(stats.HydratedObjects > 0);
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
        StatePreparationBinding<byte> preparation = new(schema, Base,
            static (in byte previous, in byte current) => new(previous != current, Base(current).Body));
        return new StateModelBinding<Other, byte>(preparation,
            [new StateReaderBinding<byte>(schema, static (ref BareValueReader input) => input.ReadByte(),
                static (ref BareValueReader input, in byte _) => input.ReadByte(), Visit)],
            static row => row.GetState<byte>(), static () => new Other(),
            static (Other target, in byte state, ObjectReadTable _) => target.Value = state,
            static (target, _) => target.Value, Visit);
    }

    private Repository Create(StateModelRegistry? models = null) => Repository.CreateNew(_root, models ?? SharedReadModel.Models());

    public void Dispose() => SharedReadModel.DeleteFixture(_root, "durable-db080-reuse-");
}
