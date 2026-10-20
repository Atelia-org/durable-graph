using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;

using System.Reflection;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using SegmentStore = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed partial class EventHistoryRepositoryTests {
    [Fact]
    public void NamedForkFanoutIgnoresUncommittedSourceAndKeepsGraphIdentityAndSavingBaselinesIndependent() {
        Node island = new() { Value = 2 };
        island.Left = island;
        Node root = new() { Value = 1, Left = island, Right = island };
        CommitId initial, childOnly, nextChildOnly, removed, otherHead, mainHead;
        using (Repository repository = CreateRepository()) {
            using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", root, parameters: NoRebase).Id);
            initial = main.BaseCommitId;
            root = Assert.IsType<Node>(main.State);
            island = root.Left!;
            root.Value = 90;
            island.Value = 91;
            var beforeFork = ImmutableHistoryFiles();
            using WorkingCopy a = repository.OpenWorkingCopy(repository.ForkBranch(initial, "a").Id);
            using WorkingCopy b = repository.OpenWorkingCopy(repository.ForkBranch(initial, "b").Id);
            AssertHistoryFiles(beforeFork);
            Assert.Equal(initial, a.BaseCommitId);
            Assert.Equal(initial, b.BaseCommitId);
            Assert.Equal(initial, main.BaseCommitId);
            Assert.Equal(Describe(repository, initial).RootId, a.StateId);
            Assert.Equal(Describe(repository, initial).RootId, b.StateId);
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
            childOnly = a.CommitState(parameters: NoRebase).Id;
            Assert.Equal(initial, Previous(repository, childOnly));
            Assert.Equal(initial, b.BaseCommitId);
            Assert.Equal((byte)2, bv.Left.Value);
            Assert.Equal((byte)91, island.Value);
            b.CommitEvent(new Node { Value = 7 });
            bv.Left.Value = 8;
            otherHead = b.CommitState(parameters: NoRebase).Id;
            av.Left.Value = 4;
            nextChildOnly = a.CommitState(parameters: NoRebase).Id;
            av.Left = av.Right = null;
            removed = a.CommitState(parameters: NoRebase).Id;
            mainHead = main.CommitState(parameters: NoRebase).Id;
            Assert.Equal((byte)8, Assert.IsType<Node>(repository.LoadState(otherHead)).Left!.Value);
        }
        using (RepositoryResources resources = RepositoryResources.OpenReadOnlyExisting(_root)) {
            GraphRecordStore states = resources.GraphStore;
            uint childId = Assert.Single(Snapshot(resources, initial).ObjectHeads.Keys, id => id != Describe(resources, initial).RootId.Value);
            ObjectVersionRecord firstWrite = Assert.Single(StorageTestData.LocalRecords(states, Describe(resources, childOnly).Address));
            Assert.Equal(childId, firstWrite.ObjectId);
            Assert.Equal(ObjectVersionKind.Delta, firstWrite.Kind);
            Assert.Equal(Describe(resources, initial).Address, Snapshot(resources, childOnly).SourceGraph);
            ObjectVersionRecord nextWrite = Assert.Single(StorageTestData.LocalRecords(states, Describe(resources, nextChildOnly).Address));
            Assert.Equal(childId, nextWrite.ObjectId);
            Assert.Equal(ObjectVersionKind.Delta, nextWrite.Kind);
            Assert.Equal(Snapshot(resources, childOnly).ObjectHeads[childId], nextWrite.PriorAddress);
            Assert.Equal(new[] { Describe(resources, initial).RootId.Value }, Snapshot(resources, removed).ObjectHeads.Keys);
            Assert.Equal(Describe(resources, initial).Address, Snapshot(resources, otherHead).SourceGraph);
            Assert.Equal(Describe(resources, initial).Address, Snapshot(resources, mainHead).SourceGraph);
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        using WorkingCopy coldMain = reopened.OpenWorkingCopy(BranchNamed(reopened, "main"));
        using WorkingCopy coldA = reopened.OpenWorkingCopy(BranchNamed(reopened, "a"));
        using WorkingCopy coldB = reopened.OpenWorkingCopy(BranchNamed(reopened, "b"));
        Assert.Equal((byte)90, Assert.IsType<Node>(coldMain.State).Value);
        Assert.Equal((byte)91, Assert.IsType<Node>(coldMain.State).Left!.Value);
        Assert.Null(Assert.IsType<Node>(coldA.State).Left);
        Assert.Equal((byte)8, Assert.IsType<Node>(coldB.State).Left!.Value);
        Assert.Equal(Describe(reopened, initial).RootId, Describe(reopened, coldA.CommitState(parameters: NoRebase).Id).RootId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NamedForkMatchesRefOnlyThenCheckoutAtHistoricalStateOrEvent(bool selectEvent) {
        using Repository repository = CreateRepository();
        using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", new Node { Value = 1 }, parameters: NoRebase).Id);
        CommitId state = main.BaseCommitId;
        main.CommitEvent(new Node { Value = 6 });
        CommitId e2 = main.CommitEvent(new Node { Value = 7 }).Id;
        Assert.IsType<Node>(main.State).Value = 9;
        CommitId latest = main.CommitState(parameters: NoRebase).Id;
        CommitId source = selectEvent ? e2 : state;
        repository.ForkBranch(source, "reference");
        using WorkingCopy reference = repository.OpenWorkingCopy(BranchNamed(repository, "reference"));
        var before = ImmutableHistoryFiles();
        using WorkingCopy fork = repository.OpenWorkingCopy(repository.ForkBranch(source, "named").Id);
        AssertHistoryFiles(before);
        Assert.Equal(reference.BaseCommitId, fork.BaseCommitId);
        Assert.Equal(reference.StateId, fork.StateId);
        Assert.Equal(reference.StateRevisionAddress, fork.StateRevisionAddress);
        Assert.Equal((byte)1, Assert.IsType<Node>(fork.State).Value);
        Assert.NotSame(reference.State, fork.State);
        CommitId committed = fork.CommitEvent(new Node { Value = 8 }).Id;
        Assert.Equal(source, Previous(repository, committed));
        Assert.Equal(state, fork.StateSourceCommitId);
        Assert.Equal(latest, main.BaseCommitId);
        Assert.Equal(source, reference.BaseCommitId);
    }

    [Theory]
    [InlineData("decode")]
    [InlineData("allocate")]
    [InlineData("hydrate")]
    public void ForkRecoveryFailureKeepsRegisteredBranchAndReleasesOnlyItsOccupancy(string phase) {
        bool fail = false;
        int reads = 0, allocations = 0, hydrations = 0;
        StateModelRegistry models = Models(
            onReadValue: _ => { reads++; if (fail && phase == "decode") throw new InvalidDataException("decode"); },
            onHydrate: _ => { hydrations++; if (fail && phase == "hydrate" && hydrations == 2) throw new InvalidDataException("late hydrate"); },
            allocate: () => { allocations++; if (fail && phase == "allocate" && allocations == 2) throw new InvalidDataException("late allocate"); return new Node(); });
        using Repository repository = CreateRepository(models);
        repository.PreparedStateReuseEnabled = false; // Exercise decoding even after opening the source WC.
        using WorkingCopy source = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", new Node { Left = new Node { Value = 2 } }, parameters: NoRebase).Id);
        BranchInfo retryInfo = repository.ForkBranch(source.BaseCommitId, "retry");
        var before = SnapshotLiveFiles();
        reads = allocations = hydrations = 0;
        fail = true;
        WorkingCopy? delivered = null;
        Assert.Throws<InvalidDataException>(() => delivered = repository.OpenWorkingCopy(retryInfo.Id));
        Assert.Null(delivered);
        Assert.False(repository.IsFaulted);
        Assert.Equal(2, repository.ListBranches().Length);
        AssertLiveFiles(before);
        Assert.True(reads > 0);
        if (phase == "hydrate") Assert.Equal(2, hydrations);
        Assert.Throws<InvalidOperationException>(() => repository.OpenWorkingCopy(BranchNamed(repository, "main")));
        fail = false;
        using WorkingCopy retry = repository.OpenWorkingCopy(retryInfo.Id);
        Assert.Equal((byte)2, Assert.IsType<Node>(retry.State).Left!.Value);
        source.CommitState(parameters: NoRebase);
        retry.CommitState(parameters: NoRebase);
    }

    [Fact]
    public void OpeningForkRejectsSingletonAllocatorWithoutLosingItsRegistration() {
        Node singleton = new();
        bool violate = false;
        using Repository repository = CreateRepository(Models(allocate: () => violate ? singleton : new Node()));
        using WorkingCopy source = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", new Node { Left = new Node() }, parameters: NoRebase).Id);
        BranchInfo childInfo = repository.ForkBranch(source.BaseCommitId, "child");
        var before = SnapshotLiveFiles();
        violate = true;
        Assert.ThrowsAny<Exception>(() => repository.OpenWorkingCopy(childInfo.Id));
        Assert.Equal(2, repository.ListBranches().Length);
        Assert.False(repository.IsFaulted);
        AssertLiveFiles(before);
        violate = false;
        using WorkingCopy child = repository.OpenWorkingCopy(childInfo.Id);
        Assert.NotSame(child.State, Assert.IsType<Node>(child.State).Left);
    }

    [Fact]
    public void OpeningForkFailedUpgradeCanRetryTheRegisteredIdentity() {
        using (Repository seed = CreateRepository()) {
            using WorkingCopy main = seed.OpenWorkingCopy(seed.CreateRootBranchFromState("main", new Node { Value = 1 }, parameters: NoRebase).Id);
        }
        bool fail = true;
        int normalizationCalls = 0;
        using Repository repository = Repository.OpenExisting(_root, Models(upgrade: true, onNormalize: () => {
            normalizationCalls++;
            if (fail) throw new InvalidDataException("upgrade failed");
        }));
        CommitId source = repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id;
        BranchInfo childInfo = repository.ForkBranch(source, "child");
        var before = SnapshotLiveFiles();
        Assert.Throws<InvalidDataException>(() => repository.OpenWorkingCopy(childInfo.Id));
        Assert.True(normalizationCalls > 0);
        Assert.False(repository.IsFaulted);
        Assert.Equal(2, repository.ListBranches().Length);
        AssertLiveFiles(before);
        fail = false;
        using WorkingCopy child = repository.OpenWorkingCopy(childInfo.Id);
        Assert.Equal((byte)11, Assert.IsType<Node>(child.State).Value);
        Assert.Equal(source, child.BaseCommitId);
        child.CommitState(parameters: NoRebase);
    }

    [Theory]
    [InlineData((int)RepositoryOperationStage.BeforePublication, false)]
    [InlineData((int)RepositoryOperationStage.AfterPublication, true)]
    public void ForkRefPublicationOutcomesPreserveHistoryAndFaultAllCheckoutsWhenNeeded(int point, bool published) {
        using (Repository seed = CreateRepository()) {
            BranchInfo main = seed.CreateRootBranchFromState("main", new Node { Value = 1 }, parameters: NoRebase);
            seed.ForkBranch(seed.ReadCurrentCommit(main.Id).Commit.Id, "other");
        }
        // Compare complete existing bytes only with all owners closed. Registration may append D frames.
        Dictionary<string, byte[]> before = ImmutableHistoryPaths().ToDictionary(path => path, File.ReadAllBytes);
        using (Repository repository = Repository.OpenExisting(_root, Models())) {
            using WorkingCopy main = repository.OpenWorkingCopy(BranchNamed(repository, "main"));
            using WorkingCopy other = repository.OpenWorkingCopy(BranchNamed(repository, "other"));
            repository.OperationStageObserver = checkpoint => { if (checkpoint == (RepositoryOperationStage)point) throw new IOException("fork interruption"); };
            WorkingCopy? delivered = null;
            if (published) {
                RepositoryPublicationException failure = Assert.Throws<RepositoryPublicationException>(() => delivered = repository.OpenWorkingCopy(repository.ForkBranch(main.BaseCommitId, "child").Id));
                Assert.Null(failure.CandidateRevisionAddress);
                Assert.Equal(RepositoryPublicationOutcome.Published, failure.Outcome);
            } else {
                // This hook runs before any physical creation or append attempt.
                Assert.Throws<IOException>(() => delivered = repository.OpenWorkingCopy(repository.ForkBranch(main.BaseCommitId, "child").Id));
            }
            Assert.Null(delivered);
            Assert.Equal(published, repository.IsFaulted);
            repository.OperationStageObserver = null;
            if (published) {
                Assert.True(main.IsFaulted);
                Assert.True(other.IsFaulted);
                Assert.Throws<InvalidOperationException>(() => main.CommitState(parameters: NoRebase).Id);
                Assert.Throws<InvalidOperationException>(() => other.CommitEvent(new Node()).Id);
            } else {
                Assert.DoesNotContain(repository.ListBranches(), branch => branch.DisplayLabel == "child");
                using WorkingCopy retry = repository.OpenWorkingCopy(repository.ForkBranch(main.BaseCommitId, "child").Id);
                Assert.Equal(main.BaseCommitId, retry.BaseCommitId);
                main.CommitState(parameters: NoRebase);
                other.CommitState(parameters: NoRebase);
            }
        }
        foreach (var pair in before) {
            Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key).AsSpan(0, pair.Value.Length).ToArray());
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        using WorkingCopy recovered = reopened.OpenWorkingCopy(BranchNamed(reopened, "child"));
        Assert.Equal((byte)1, Assert.IsType<Node>(recovered.State).Value);
    }


    // Live writers hold exclusive file handles. At serial operation boundaries, observe
    // lengths/timestamps for append evidence; the cold test below compares actual bytes.
    private string[] ImmutableHistoryPaths() => Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
        .Where(path => path.StartsWith(Path.Combine(_root, "data") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(Path.Combine(_root, "data") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
)
        .ToArray();

    private Dictionary<string, (long Length, DateTime Modified)> ImmutableHistoryFiles() => ImmutableHistoryPaths()
        .ToDictionary(path => path, path => (new FileInfo(path).Length, File.GetLastWriteTimeUtc(path)));

    private void AssertHistoryFiles(Dictionary<string, (long Length, DateTime Modified)> before) {
        var after = ImmutableHistoryFiles();
        Assert.NotEmpty(before);
        Assert.Contains(before.Keys, path => path.StartsWith(Path.Combine(_root, "data"), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var pair in before) Assert.True(after[pair.Key].Length >= pair.Value.Length);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("event")]
    [InlineData("event-only")]
    public void NamedForkRegistersDirectoryWhilePreservingExistingFrameBytesAndInheritedCommit(string sourceKind) {
        using (Repository seed = CreateRepository()) {
            using WorkingCopy main = sourceKind == "event-only"
                ? seed.OpenWorkingCopy(seed.CreateRootBranchFromEvent("main", new Node()).Id)
                : seed.OpenWorkingCopy(seed.CreateRootBranchFromState("main", new Node(), parameters: NoRebase).Id);
            if (sourceKind == "event") main.CommitEvent(new Node());
        }
        Dictionary<string, byte[]> before = ImmutableHistoryPaths().ToDictionary(path => path, File.ReadAllBytes);
        Assert.Contains(before.Keys, path => path.StartsWith(Path.Combine(_root, "data"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(before.Keys, path => path.StartsWith(Path.Combine(_root, "data") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        using (Repository repository = Repository.OpenExisting(_root, Models())) {
            using WorkingCopy main = repository.OpenWorkingCopy(BranchNamed(repository, "main"));
            using WorkingCopy a = repository.OpenWorkingCopy(repository.ForkBranch(main.BaseCommitId, "a").Id);
            using WorkingCopy b = repository.OpenWorkingCopy(repository.ForkBranch(main.BaseCommitId, "b").Id);
            Assert.Equal(main.BaseCommitId, a.BaseCommitId);
            Assert.Equal(main.BaseCommitId, b.BaseCommitId);
        }
        Assert.Equal(before.Keys.Order(), ImmutableHistoryPaths().Order());
        foreach (var pair in before) Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key).AsSpan(0, pair.Value.Length).ToArray());
    }

    [Theory]
    [InlineData("decode")]
    [InlineData("normalize")]
    [InlineData("allocate")]
    [InlineData("hydrate")]
    public void FailedOpeningRegisteredForkPreservesAllPersistedBytesAcrossColdOpen(string phase) {
        using (Repository seed = CreateRepository()) {
            using WorkingCopy main = seed.OpenWorkingCopy(seed.CreateRootBranchFromState("main", new Node { Left = new Node() }, parameters: NoRebase).Id);
        }
        using (Repository registration = Repository.OpenExisting(_root, Models())) {
            registration.ForkBranch(registration.ReadCurrentCommit(BranchNamed(registration, "main")).Commit.Id, "child");
        }
        var before = SnapshotFiles();
        StateModelRegistry models = Models(upgrade: phase == "normalize",
            onReadValue: _ => { if (phase == "decode") throw new InvalidDataException("decode failed"); },
            onNormalize: () => { if (phase == "normalize") throw new InvalidDataException("normalize failed"); },
            allocate: () => phase == "allocate" ? throw new InvalidDataException("allocate failed") : new Node(),
            onHydrate: _ => { if (phase == "hydrate") throw new InvalidDataException("hydrate failed"); });
        using (Repository repository = Repository.OpenExisting(_root, models)) {
            Assert.Throws<InvalidDataException>(() => repository.OpenWorkingCopy(BranchNamed(repository, "child")));
            Assert.False(repository.IsFaulted);
            Assert.Equal(2, repository.ListBranches().Length);
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
