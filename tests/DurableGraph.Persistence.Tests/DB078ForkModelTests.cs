using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed partial class DB078HistoryTests {
    // The writer owns exclusive handles; live checks observe append/file-change metadata.
    // Closed-repository tests elsewhere continue to compare full persisted bytes.
    private Dictionary<string, (long Length, DateTime Modified)> SnapshotLiveFiles() => Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
        .Where(path => !path.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
        .ToDictionary(path => path, path => (new FileInfo(path).Length, File.GetLastWriteTimeUtc(path)));

    private void AssertLiveFiles(Dictionary<string, (long Length, DateTime Modified)> before) {
        var after = SnapshotLiveFiles();
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var pair in before) Assert.Equal(pair.Value, after[pair.Key]);
    }

    [Fact]
    public void ForkFromConsecutiveEventsNeedsOnlyStateModelsAndPreservesSelectedHead() {
        CheckpointAddress eventHead;
        using (Repository repository = Create()) {
            using BranchCheckout main = repository.CreateBranch("main", new Alpha { Value = 1 }, NoRebase);
            main.CommitEvent(new Message { Value = 2 }, NoRebase);
            eventHead = main.CommitEvent(new Message { Value = 3 }, NoRebase);
        }
        using (Repository repository = Repository.OpenExisting(_root, Models(includeMessage: false))) {
            using BranchCheckout main = repository.Checkout("main");
            CheckpointAddress source = repository.GetHead("main");
            using BranchCheckout child = repository.Fork("child", source);
            Assert.Equal(eventHead.RevisionAddress, child.Head.RevisionAddress);
            Assert.Equal(source, child.Head);
            Assert.Equal((byte)1, Assert.IsType<Alpha>(child.State).Value);
            Assert.NotSame(main.State, child.State);
            Assert.ThrowsAny<Exception>(() => repository.ReadEvent(source));
            Assert.ThrowsAny<Exception>(() => child.CommitEvent(new Message(), NoRebase));
            Assert.Equal(source, child.Head);
            CheckpointAddress saved = child.CommitState(NoRebase);
            Assert.Equal(source.Address, saved.Parent);
            Assert.Equal(source, main.Head);
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        using BranchCheckout cold = reopened.Checkout("child");
        Assert.Equal((byte)1, Assert.IsType<Alpha>(cold.State).Value);
        Assert.Equal(GraphFrameKind.State, cold.Head.Kind);
    }

    [Fact]
    public void EmptyRegistryForksEventOnlyHistoryThenIndependentBranchesInstallAndReplaceStateTypes() {
        using (Repository repository = Create()) {
            using BranchCheckout main = repository.CreateBranchFromEvent("main", new Message { Value = 1 }, NoRebase);
            main.CommitEvent(new Message { Value = 2 }, NoRebase);
        }
        using (Repository metadata = Repository.OpenExisting(_root, new StateModelRegistry())) {
            using BranchCheckout main = metadata.Checkout("main");
            using BranchCheckout a = metadata.Fork("a", main.Head);
            using BranchCheckout b = metadata.Fork("b", main.Head);
            Assert.Null(a.State);
            Assert.Null(b.State);
            Assert.Equal(main.Head, a.Head);
            Assert.Equal(main.Head, b.Head);
            Assert.Throws<InvalidOperationException>(() => a.CommitState(NoRebase));
            Assert.ThrowsAny<Exception>(() => metadata.ReadEvent(a.Head));
            Assert.False(metadata.IsFaulted);
        }
        CheckpointAddress firstA, firstB, replacementA, replacementB, secondA, secondB;
        using (Repository repository = Repository.OpenExisting(_root, Models())) {
            using BranchCheckout main = repository.Checkout("main");
            using BranchCheckout a = repository.Checkout("a");
            using BranchCheckout b = repository.Checkout("b");
            CheckpointAddress prefix = main.Head;
            firstA = a.CommitState(new Alpha { Value = 3 }, NoRebase);
            Assert.Null(b.State);
            Assert.Equal(prefix, b.Head);
            firstB = b.CommitState(new Beta { Value = 4 }, NoRebase);
            replacementA = a.CommitState(new Beta { Value = 5 }, NoRebase);
            replacementB = b.CommitState(new Alpha { Value = 6 }, NoRebase);
            secondA = a.CommitState(NoRebase);
            secondB = b.CommitState(NoRebase);
            Assert.Equal(prefix, main.Head);
            Assert.Null(main.State);
            Assert.Equal(prefix.Address, firstA.Parent);
            Assert.Equal(prefix.Address, firstB.Parent);
        }
        using (SegmentStore segments = OpenState()) {
            using StateRevisionStore states = new(segments);
            Assert.Null(states.Read(firstA.RevisionAddress).ParentRevisionAddress);
            Assert.Null(states.Read(firstB.RevisionAddress).ParentRevisionAddress);
            Assert.Equal(firstA.RevisionAddress, states.Read(replacementA.RevisionAddress).ParentRevisionAddress);
            Assert.Equal(firstB.RevisionAddress, states.Read(replacementB.RevisionAddress).ParentRevisionAddress);
            Assert.Equal(replacementA.RevisionAddress, states.Read(secondA.RevisionAddress).ParentRevisionAddress);
            Assert.Equal(replacementB.RevisionAddress, states.Read(secondB.RevisionAddress).ParentRevisionAddress);
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        using BranchCheckout coldA = reopened.Checkout("a");
        using BranchCheckout coldB = reopened.Checkout("b");
        Assert.Equal((byte)5, Assert.IsType<Beta>(coldA.State).Value);
        Assert.Equal((byte)6, Assert.IsType<Alpha>(coldB.State).Value);
        Assert.Equal(secondA.RootId, coldA.CommitState(NoRebase).RootId);
        Assert.Equal(secondB.RootId, coldB.CommitState(NoRebase).RootId);
    }

    [Fact]
    public void MissingNearestStateModelDoesNotTurnForkIntoEmptyWorkspaceAndRefOnlyStillWorks() {
        using (Repository repository = Create()) {
            using BranchCheckout main = repository.CreateBranch("main", new Alpha(), NoRebase);
            main.CommitEvent(new Message(), NoRebase);
        }
        using Repository repositoryWithoutModels = Repository.OpenExisting(_root, new StateModelRegistry());
        CheckpointAddress source = repositoryWithoutModels.GetHead("main");
        var before = SnapshotLiveFiles();
        BranchCheckout? delivered = null;
        Assert.ThrowsAny<Exception>(() => delivered = repositoryWithoutModels.Fork("child", source));
        Assert.Null(delivered);
        Assert.False(repositoryWithoutModels.IsFaulted);
        AssertLiveFiles(before);
        Assert.Equal(source, repositoryWithoutModels.CreateBranch("child", source));
        Assert.Equal(source, repositoryWithoutModels.GetHead("child"));
        Assert.ThrowsAny<Exception>(() => repositoryWithoutModels.Checkout("child"));
        Assert.Equal(source, repositoryWithoutModels.GetHead("child"));
    }
}
