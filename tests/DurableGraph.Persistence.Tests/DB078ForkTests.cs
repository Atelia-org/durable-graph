using System.Reflection;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;
using Journal = Atelia.EventJournal.EventJournal;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed partial class EventHistoryRepositoryTests {
    [Fact]
    public void NamedForkFanoutIgnoresUncommittedSourceAndKeepsGraphIdentityAndSavingBaselinesIndependent() {
        Node island = new() { Value = 2 };
        island.Left = island;
        Node root = new() { Value = 1, Left = island, Right = island };
        CheckpointAddress initial, childOnly, nextChildOnly, removed, otherHead, mainHead;
        using (Repository repository = CreateRepository()) {
            using BranchCheckout main = repository.CreateBranch("main", root, NoRebase);
            initial = main.Head;
            root.Value = 90;
            island.Value = 91;
            var beforeFork = ImmutableHistoryFiles();
            using BranchCheckout a = repository.Fork("a", initial);
            using BranchCheckout b = repository.Fork("b", initial);
            AssertHistoryFiles(beforeFork);
            Assert.Equal(initial, a.Head);
            Assert.Equal(initial, b.Head);
            Assert.Equal(initial, main.Head);
            Assert.Equal(initial.RootId, a.StateId);
            Assert.Equal(initial.RootId, b.StateId);
            Node av = Assert.IsType<Node>(a.State), bv = Assert.IsType<Node>(b.State);
            Assert.Equal((byte)1, av.Value);
            Assert.Equal((byte)2, av.Left!.Value);
            Assert.Same(av.Left, av.Right);
            Assert.Same(av.Left, av.Left.Left);
            Assert.Same(bv.Left, bv.Right);
            Assert.Same(bv.Left, bv.Left!.Left);
            Assert.NotSame(root, av);
            Assert.NotSame(av, bv);
            Assert.NotSame(island, av.Left);
            Assert.NotSame(av.Left, bv.Left);
            av.Left.Value = 3;
            childOnly = a.CommitState(NoRebase);
            Assert.Equal(initial.Address, childOnly.Parent);
            Assert.Equal(initial, b.Head);
            Assert.Equal((byte)2, bv.Left.Value);
            Assert.Equal((byte)91, island.Value);
            b.CommitEvent(new Node { Value = 7 }, NoRebase);
            bv.Left.Value = 8;
            otherHead = b.CommitState(NoRebase);
            av.Left.Value = 4;
            nextChildOnly = a.CommitState(NoRebase);
            av.Left = av.Right = null;
            removed = a.CommitState(NoRebase);
            mainHead = main.CommitState(NoRebase);
            Assert.Equal((byte)8, Assert.IsType<Node>(repository.ReadState(otherHead)).Left!.Value);
        }
        using (SegmentStore segments = OpenState()) {
            using StateRevisionStore states = new(segments);
            uint childId = Assert.Single(states.ReadLiveObjectHeadMap(initial.RevisionAddress).Keys, id => id != initial.RootId.Value);
            ObjectVersionRecord firstWrite = Assert.Single(states.Read(childOnly.RevisionAddress).LocalObjects);
            Assert.Equal(childId, firstWrite.ObjectId);
            Assert.Equal(ObjectVersionKind.Delta, firstWrite.Kind);
            Assert.Equal(initial.RevisionAddress, states.Read(childOnly.RevisionAddress).ParentRevisionAddress);
            ObjectVersionRecord nextWrite = Assert.Single(states.Read(nextChildOnly.RevisionAddress).LocalObjects);
            Assert.Equal(childId, nextWrite.ObjectId);
            Assert.Equal(ObjectVersionKind.Delta, nextWrite.Kind);
            Assert.Equal(childOnly.RevisionAddress, nextWrite.PriorAddress);
            Assert.Equal(new[] { initial.RootId.Value }, states.ReadLiveObjectHeadMap(removed.RevisionAddress).Keys);
            Assert.Equal(initial.RevisionAddress, states.Read(otherHead.RevisionAddress).ParentRevisionAddress);
            Assert.Equal(initial.RevisionAddress, states.Read(mainHead.RevisionAddress).ParentRevisionAddress);
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        using BranchCheckout coldMain = reopened.Checkout("main");
        using BranchCheckout coldA = reopened.Checkout("a");
        using BranchCheckout coldB = reopened.Checkout("b");
        Assert.Equal((byte)90, Assert.IsType<Node>(coldMain.State).Value);
        Assert.Equal((byte)91, Assert.IsType<Node>(coldMain.State).Left!.Value);
        Assert.Null(Assert.IsType<Node>(coldA.State).Left);
        Assert.Equal((byte)8, Assert.IsType<Node>(coldB.State).Left!.Value);
        Assert.Equal(initial.RootId, coldA.CommitState(NoRebase).RootId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NamedForkMatchesRefOnlyThenCheckoutAtHistoricalStateOrEvent(bool selectEvent) {
        using Repository repository = CreateRepository();
        using BranchCheckout main = repository.CreateBranch("main", new Node { Value = 1 }, NoRebase);
        CheckpointAddress state = main.Head;
        main.CommitEvent(new Node { Value = 6 }, NoRebase);
        CheckpointAddress e2 = main.CommitEvent(new Node { Value = 7 }, NoRebase);
        Assert.IsType<Node>(main.State).Value = 9;
        CheckpointAddress latest = main.CommitState(NoRebase);
        CheckpointAddress source = selectEvent ? e2 : state;
        repository.CreateBranch("reference", source);
        using BranchCheckout reference = repository.Checkout("reference");
        var before = ImmutableHistoryFiles();
        using BranchCheckout fork = repository.Fork("named", source);
        AssertHistoryFiles(before);
        Assert.Equal(reference.Head, fork.Head);
        Assert.Equal(reference.StateId, fork.StateId);
        Assert.Equal(reference.StateRevisionAddress, fork.StateRevisionAddress);
        Assert.Equal((byte)1, Assert.IsType<Node>(fork.State).Value);
        Assert.NotSame(reference.State, fork.State);
        CheckpointAddress committed = fork.CommitEvent(new Node { Value = 8 }, NoRebase);
        Assert.Equal(source.Address, committed.Parent);
        Assert.Equal(state.RevisionAddress, fork.StateRevisionAddress);
        Assert.Equal(latest, main.Head);
        Assert.Equal(source, reference.Head);
    }

    [Theory]
    [InlineData("decode")]
    [InlineData("allocate")]
    [InlineData("hydrate")]
    public void ForkRecoveryFailurePublishesNothingAndReleasesOnlyItsReservation(string phase) {
        bool fail = false;
        int reads = 0, allocations = 0, hydrations = 0;
        StateModelRegistry models = Models(
            onReadValue: _ => { reads++; if (fail && phase == "decode") throw new InvalidDataException("decode"); },
            onHydrate: _ => { hydrations++; if (fail && phase == "hydrate" && hydrations == 2) throw new InvalidDataException("late hydrate"); },
            allocate: () => { allocations++; if (fail && phase == "allocate" && allocations == 2) throw new InvalidDataException("late allocate"); return new Node(); });
        using Repository repository = CreateRepository(models);
        using BranchCheckout source = repository.CreateBranch("main", new Node { Left = new Node { Value = 2 } }, NoRebase);
        var before = SnapshotLiveFiles();
        fail = true;
        BranchCheckout? delivered = null;
        Assert.Throws<InvalidDataException>(() => delivered = repository.Fork("retry", source.Head));
        Assert.Null(delivered);
        Assert.False(repository.IsFaulted);
        Assert.Equal(new[] { "main" }, repository.ListBranches());
        AssertLiveFiles(before);
        Assert.True(reads > 0);
        if (phase == "hydrate") Assert.Equal(2, hydrations);
        Assert.Throws<InvalidOperationException>(() => repository.Checkout("main"));
        fail = false;
        using BranchCheckout retry = repository.Fork("retry", source.Head);
        Assert.Equal((byte)2, Assert.IsType<Node>(retry.State).Left!.Value);
        source.CommitState(NoRebase);
        retry.CommitState(NoRebase);
    }

    [Fact]
    public void ForkRejectsSingletonAllocatorWithinItsGraphWithoutPublishing() {
        Node singleton = new();
        bool violate = false;
        using Repository repository = CreateRepository(Models(allocate: () => violate ? singleton : new Node()));
        using BranchCheckout source = repository.CreateBranch("main", new Node { Left = new Node() }, NoRebase);
        var before = SnapshotLiveFiles();
        violate = true;
        Assert.ThrowsAny<Exception>(() => repository.Fork("child", source.Head));
        Assert.Equal(new[] { "main" }, repository.ListBranches());
        Assert.False(repository.IsFaulted);
        AssertLiveFiles(before);
        violate = false;
        using BranchCheckout child = repository.Fork("child", source.Head);
        Assert.NotSame(child.State, Assert.IsType<Node>(child.State).Left);
    }

    [Fact]
    public void ForkFailedUpgradePublishesNothingAndCanRetryWithItsOriginalName() {
        using (Repository seed = CreateRepository()) {
            using BranchCheckout main = seed.CreateBranch("main", new Node { Value = 1 }, NoRebase);
        }
        bool fail = true;
        int normalizationCalls = 0;
        using Repository repository = Repository.OpenExisting(_root, Models(upgrade: true, onNormalize: () => {
            normalizationCalls++;
            if (fail) throw new InvalidDataException("upgrade failed");
        }));
        CheckpointAddress source = repository.GetHead("main");
        var before = SnapshotLiveFiles();
        Assert.Throws<InvalidDataException>(() => repository.Fork("child", source));
        Assert.True(normalizationCalls > 0);
        Assert.False(repository.IsFaulted);
        Assert.Equal(new[] { "main" }, repository.ListBranches());
        AssertLiveFiles(before);
        fail = false;
        using BranchCheckout child = repository.Fork("child", source);
        Assert.Equal((byte)11, Assert.IsType<Node>(child.State).Value);
        Assert.Equal(source, child.Head);
        child.CommitState(NoRebase);
    }

    [Theory]
    [InlineData((int)CommitCheckpoint.BeforePublication, false)]
    [InlineData((int)CommitCheckpoint.AfterPublication, true)]
    public void ForkRefPublicationOutcomesPreserveHistoryAndFaultAllCheckoutsWhenNeeded(int point, bool published) {
        using (Repository repository = CreateRepository()) {
            using BranchCheckout main = repository.CreateBranch("main", new Node { Value = 1 }, NoRebase);
            using BranchCheckout other = repository.Fork("other", main.Head);
            var before = ImmutableHistoryFiles();
            repository.Checkpoint = checkpoint => { if (checkpoint == (CommitCheckpoint)point) throw new IOException("fork interruption"); };
            BranchCheckout? delivered = null;
            GraphCommitException failure = Assert.Throws<GraphCommitException>(() => delivered = repository.Fork("child", main.Head));
            Assert.Null(delivered);
            Assert.Null(failure.CandidateRevisionAddress);
            Assert.Equal(published ? GraphCommitOutcome.Published : GraphCommitOutcome.NotPublished, failure.Outcome);
            Assert.Equal(published, repository.IsFaulted);
            AssertHistoryFiles(before);
            repository.Checkpoint = null;
            if (published) {
                Assert.True(main.IsFaulted);
                Assert.True(other.IsFaulted);
                Assert.Throws<InvalidOperationException>(() => main.CommitState(NoRebase));
                Assert.Throws<InvalidOperationException>(() => other.CommitEvent(new Node(), NoRebase));
            } else {
                Assert.DoesNotContain("child", repository.ListBranches());
                using BranchCheckout retry = repository.Fork("child", main.Head);
                Assert.Equal(main.Head, retry.Head);
                main.CommitState(NoRebase);
                other.CommitState(NoRebase);
            }
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        using BranchCheckout recovered = reopened.Checkout("child");
        Assert.Equal((byte)1, Assert.IsType<Node>(recovered.State).Value);
    }

    [Theory]
    [InlineData("before-create", false)]
    [InlineData("after-create", false)]
    [InlineData("before-bind", false)]
    [InlineData("after-bind", true)]
    public void ForkUnknownPublicationRequiresReopenAndNeverAppendsHistory(string phase, bool visible) {
        using (Repository repository = CreateRepository()) {
            using BranchCheckout main = repository.CreateBranch("main", new Node { Value = 4 }, NoRebase);
            using BranchCheckout other = repository.Fork("other", main.Head);
            Journal journal = JournalOf(repository);
            FieldInfo field = typeof(Journal).GetField("_refOpLog", BindingFlags.Instance | BindingFlags.NonPublic)!;
            IRbfFile original = (IRbfFile)field.GetValue(journal)!;
            string refObjects = (string)typeof(Journal).GetField("_refObjectsPath", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(journal)!;
            field.SetValue(journal, new RefOpFaultFile(original, phase, refObjects));
            var before = ImmutableHistoryFiles();
            GraphCommitException failure = Assert.Throws<GraphCommitException>(() => repository.Fork("child", main.Head));
            Assert.Equal(GraphCommitOutcome.Unknown, failure.Outcome);
            Assert.Null(failure.CandidateRevisionAddress);
            Assert.True(repository.IsFaulted);
            Assert.Throws<InvalidOperationException>(() => main.CommitState(NoRebase));
            Assert.Throws<InvalidOperationException>(() => other.CommitState(NoRebase));
            AssertHistoryFiles(before);
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        Assert.Equal(visible, reopened.ListBranches().Contains("child"));
        if (visible) {
            using BranchCheckout child = reopened.Checkout("child");
            Assert.Equal(reopened.GetHead("main"), child.Head);
            Assert.Equal((byte)4, Assert.IsType<Node>(child.State).Value);
        }
    }

    // Live writers hold exclusive file handles. At serial operation boundaries, observe
    // lengths/timestamps for append evidence; the cold test below compares actual bytes.
    private string[] ImmutableHistoryPaths() => Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
        .Where(path => path.StartsWith(Path.Combine(_root, "state") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, Path.Combine(_root, "schemas.rbf"), StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(Path.Combine(_root, "journal", "events") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        .ToArray();

    private Dictionary<string, (long Length, DateTime Modified)> ImmutableHistoryFiles() => ImmutableHistoryPaths()
        .ToDictionary(path => path, path => (new FileInfo(path).Length, File.GetLastWriteTimeUtc(path)));

    private void AssertHistoryFiles(Dictionary<string, (long Length, DateTime Modified)> before) {
        var after = ImmutableHistoryFiles();
        Assert.NotEmpty(before);
        Assert.Contains(Path.Combine(_root, "schemas.rbf"), before.Keys);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var pair in before) Assert.Equal(pair.Value, after[pair.Key]);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("event")]
    [InlineData("event-only")]
    public void NamedForkOnlyChangesRefsAndPreservesAllImmutableFileBytesAcrossColdOpen(string sourceKind) {
        using (Repository seed = CreateRepository()) {
            using BranchCheckout main = sourceKind == "event-only"
                ? seed.CreateBranchFromEvent("main", new Node(), NoRebase)
                : seed.CreateBranch("main", new Node(), NoRebase);
            if (sourceKind == "event") main.CommitEvent(new Node(), NoRebase);
        }
        Dictionary<string, byte[]> before = ImmutableHistoryPaths().ToDictionary(path => path, File.ReadAllBytes);
        Assert.Contains(Path.Combine(_root, "schemas.rbf"), before.Keys);
        Assert.Contains(before.Keys, path => path.StartsWith(Path.Combine(_root, "state") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(before.Keys, path => path.StartsWith(Path.Combine(_root, "journal", "events") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        using (Repository repository = Repository.OpenExisting(_root, Models())) {
            using BranchCheckout main = repository.Checkout("main");
            using BranchCheckout a = repository.Fork("a", main.Head);
            using BranchCheckout b = repository.Fork("b", main.Head);
            Assert.Equal(main.Head, a.Head);
            Assert.Equal(main.Head, b.Head);
        }
        Assert.Equal(before.Keys.Order(), ImmutableHistoryPaths().Order());
        foreach (var pair in before) Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
    }

    [Theory]
    [InlineData("decode")]
    [InlineData("normalize")]
    [InlineData("allocate")]
    [InlineData("hydrate")]
    public void FailedForkRestorationPreservesAllPersistedBytesAcrossColdOpen(string phase) {
        using (Repository seed = CreateRepository()) {
            using BranchCheckout main = seed.CreateBranch("main", new Node { Left = new Node() }, NoRebase);
        }
        var before = SnapshotFiles();
        StateModelRegistry models = Models(upgrade: phase == "normalize",
            onReadValue: _ => { if (phase == "decode") throw new InvalidDataException("decode failed"); },
            onNormalize: () => { if (phase == "normalize") throw new InvalidDataException("normalize failed"); },
            allocate: () => phase == "allocate" ? throw new InvalidDataException("allocate failed") : new Node(),
            onHydrate: _ => { if (phase == "hydrate") throw new InvalidDataException("hydrate failed"); });
        using (Repository repository = Repository.OpenExisting(_root, models)) {
            Assert.Throws<InvalidDataException>(() => repository.Fork("child", repository.GetHead("main")));
            Assert.False(repository.IsFaulted);
            Assert.Equal(new[] { "main" }, repository.ListBranches());
        }
        AssertFiles(before);
    }

    // The lock is process coordination, not persisted graph/ref content, and is exclusively held.
    private Dictionary<string, (long Length, DateTime Modified)> SnapshotLiveFiles() => Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
        .Where(path => !path.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
        .ToDictionary(path => path, path => (new FileInfo(path).Length, File.GetLastWriteTimeUtc(path)));

    private void AssertLiveFiles(Dictionary<string, (long Length, DateTime Modified)> before) {
        var after = SnapshotLiveFiles();
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var pair in before) Assert.Equal(pair.Value, after[pair.Key]);
    }
}
