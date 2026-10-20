using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;

using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using SegmentStore = Atelia.FrameStore.FrameStore;

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
        CommitId eventHead;
        using (Repository repository = Create()) {
            using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", new Alpha { Value = 1 }, parameters: NoRebase).Id);
            main.CommitEvent(new Message { Value = 2 });
            eventHead = main.CommitEvent(new Message { Value = 3 }).Id;
        }
        using (Repository repository = Repository.OpenExisting(_root, Models(includeMessage: false))) {
            using WorkingCopy main = repository.OpenWorkingCopy(BranchNamed(repository, "main"));
            CommitId source = repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id;
            using WorkingCopy child = repository.OpenWorkingCopy(repository.ForkBranch(source, "child").Id);
            Assert.Equal(eventHead, child.BaseCommitId);
            Assert.Equal(source, child.BaseCommitId);
            Assert.Equal((byte)1, Assert.IsType<Alpha>(child.State).Value);
            Assert.NotSame(main.State, child.State);
            Assert.ThrowsAny<Exception>(() => repository.LoadEvent(source));
            Assert.ThrowsAny<Exception>(() => child.CommitEvent(new Message()).Id);
            Assert.Equal(source, child.BaseCommitId);
            CommitId saved = child.CommitState(parameters: NoRebase).Id;
            Assert.Equal(source, Previous(repository, saved));
            Assert.Equal(source, main.BaseCommitId);
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        using WorkingCopy cold = reopened.OpenWorkingCopy(BranchNamed(reopened, "child"));
        Assert.Equal((byte)1, Assert.IsType<Alpha>(cold.State).Value);
        Assert.Equal(CommitKind.State, reopened.ReadCommitInfo(cold.BaseCommitId).Kind);
    }

    [Fact]
    public void EmptyRegistryForksEventOnlyHistoryThenIndependentBranchesInstallAndReplaceStateTypes() {
        using (Repository repository = Create()) {
            using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("main", new Message { Value = 1 }).Id);
            main.CommitEvent(new Message { Value = 2 });
        }
        using (Repository metadata = Repository.OpenExisting(_root, new StateModelRegistry())) {
            using WorkingCopy main = metadata.OpenWorkingCopy(BranchNamed(metadata, "main"));
            using WorkingCopy a = metadata.OpenWorkingCopy(metadata.ForkBranch(main.BaseCommitId, "a").Id);
            using WorkingCopy b = metadata.OpenWorkingCopy(metadata.ForkBranch(main.BaseCommitId, "b").Id);
            Assert.Null(a.State);
            Assert.Null(b.State);
            Assert.Equal(main.BaseCommitId, a.BaseCommitId);
            Assert.Equal(main.BaseCommitId, b.BaseCommitId);
            Assert.Throws<InvalidOperationException>(() => a.CommitState(parameters: NoRebase).Id);
            Assert.ThrowsAny<Exception>(() => metadata.LoadEvent(a.BaseCommitId));
            Assert.False(metadata.IsFaulted);
        }
        CommitId firstA, firstB, replacementA, replacementB, secondA, secondB;
        using (Repository repository = Repository.OpenExisting(_root, Models())) {
            using WorkingCopy main = repository.OpenWorkingCopy(BranchNamed(repository, "main"));
            using WorkingCopy a = repository.OpenWorkingCopy(BranchNamed(repository, "a"));
            using WorkingCopy b = repository.OpenWorkingCopy(BranchNamed(repository, "b"));
            CommitId prefix = main.BaseCommitId;
            firstA = a.CommitState(new Alpha { Value = 3 }, parameters: NoRebase).Id;
            Assert.Null(b.State);
            Assert.Equal(prefix, b.BaseCommitId);
            firstB = b.CommitState(new Beta { Value = 4 }, parameters: NoRebase).Id;
            replacementA = a.CommitState(new Beta { Value = 5 }, parameters: NoRebase).Id;
            replacementB = b.CommitState(new Alpha { Value = 6 }, parameters: NoRebase).Id;
            secondA = a.CommitState(parameters: NoRebase).Id;
            secondB = b.CommitState(parameters: NoRebase).Id;
            Assert.Equal(prefix, main.BaseCommitId);
            Assert.Null(main.State);
            Assert.Equal(prefix, Previous(repository, firstA));
            Assert.Equal(prefix, Previous(repository, firstB));
        }
        using (RepositoryResources resources = RepositoryResources.OpenReadOnlyExisting(_root)) {
            GraphRecordStore states = resources.GraphStore;
            Assert.Null(Snapshot(resources, firstA).SourceGraph);
            Assert.Null(Snapshot(resources, firstB).SourceGraph);
            Assert.Equal(Describe(resources, firstA).Address, Snapshot(resources, replacementA).SourceGraph);
            Assert.Equal(Describe(resources, firstB).Address, Snapshot(resources, replacementB).SourceGraph);
            Assert.Equal(Describe(resources, replacementA).Address, Snapshot(resources, secondA).SourceGraph);
            Assert.Equal(Describe(resources, replacementB).Address, Snapshot(resources, secondB).SourceGraph);
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        using WorkingCopy coldA = reopened.OpenWorkingCopy(BranchNamed(reopened, "a"));
        using WorkingCopy coldB = reopened.OpenWorkingCopy(BranchNamed(reopened, "b"));
        Assert.Equal((byte)5, Assert.IsType<Beta>(coldA.State).Value);
        Assert.Equal((byte)6, Assert.IsType<Alpha>(coldB.State).Value);
        Assert.Equal(Describe(reopened, secondA).RootId, Describe(reopened, coldA.CommitState(parameters: NoRebase).Id).RootId);
        Assert.Equal(Describe(reopened, secondB).RootId, Describe(reopened, coldB.CommitState(parameters: NoRebase).Id).RootId);
    }

    [Fact]
    public void MissingNearestStateModelDoesNotTurnForkIntoEmptyWorkspaceAndRefOnlyStillWorks() {
        using (Repository repository = Create()) {
            using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", new Alpha(), parameters: NoRebase).Id);
            main.CommitEvent(new Message());
        }
        using Repository repositoryWithoutModels = Repository.OpenExisting(_root, new StateModelRegistry());
        CommitId source = repositoryWithoutModels.ReadCurrentCommit(BranchNamed(repositoryWithoutModels, "main")).Commit.Id;
        BranchInfo child = repositoryWithoutModels.ForkBranch(source, "child");
        Assert.Equal(source, repositoryWithoutModels.ReadCurrentCommit(child.Id).Commit.Id);
        var before = SnapshotLiveFiles();
        WorkingCopy? delivered = null;
        Assert.ThrowsAny<Exception>(() => delivered = repositoryWithoutModels.OpenWorkingCopy(child.Id));
        Assert.Null(delivered);
        Assert.False(repositoryWithoutModels.IsFaulted);
        AssertLiveFiles(before);
        Assert.Equal(source, repositoryWithoutModels.ReadCurrentCommit(child.Id).Commit.Id);
        Assert.ThrowsAny<Exception>(() => repositoryWithoutModels.OpenWorkingCopy(child.Id));
    }
}
