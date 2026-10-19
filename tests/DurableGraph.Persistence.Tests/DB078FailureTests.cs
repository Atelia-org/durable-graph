using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;

using System.Reflection;
using Atelia.Data;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using SegmentStore = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed partial class EventHistoryRepositoryTests {
    [Theory]
    [InlineData((int)RepositorySaveStage.AfterBusinessAppend, false)]
    [InlineData((int)RepositorySaveStage.BeforePublication, false)]
    [InlineData((int)RepositorySaveStage.AfterPublication, true)]
    [InlineData((int)RepositorySaveStage.BeforeInstall, true)]
    public void InitialEventPublicationFailureReopensWithEitherNoBranchOrEventOnlyBranch(int point, bool published) {
        using (Repository repository = CreateRepository()) {
            repository.SaveStageObserver = checkpoint => {
                if (checkpoint == (RepositorySaveStage)point) throw new IOException("Interrupted initial Event.");
            };
            var failure = Assert.Throws<RepositoryPublicationException>(() =>
                repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("main", new Node { Value = 7 }, parameters: NoRebase).Id));
            Assert.Equal(published ? RepositoryPublicationOutcome.Published : RepositoryPublicationOutcome.NotPublished, failure.Outcome);
            Assert.True(repository.IsFaulted);
            Assert.NotNull(failure.CandidateRevisionAddress);
            Assert.Throws<InvalidOperationException>(() => repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id);
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        Assert.Equal(published, reopened.ListBranches().Any(branch => branch.DisplayLabel == "main"));
        if (published) {
            using WorkingCopy checkout = reopened.OpenWorkingCopy(BranchNamed(reopened, "main"));
            Assert.Null(checkout.State);
            Assert.Equal((byte)7, Assert.IsType<Node>(reopened.LoadEvent(checkout.BaseCommitId)).Value);
            checkout.CommitState(new Node { Value = 9 }, parameters: NoRebase);
            Assert.Equal((byte)9, Assert.IsType<Node>(checkout.State).Value);
        }
    }


    [Theory]
    [InlineData((int)RepositorySaveStage.AfterBusinessAppend, false)]
    [InlineData((int)RepositorySaveStage.BeforePublication, false)]
    [InlineData((int)RepositorySaveStage.AfterPublication, true)]
    [InlineData((int)RepositorySaveStage.BeforeInstall, true)]
    public void FirstStateFailureNeverInstallsCandidateAndColdCheckoutUsesActualPublishedHead(int point, bool published) {
        using (Repository repository = CreateRepository()) {
            using WorkingCopy checkout = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("main", new Node { Value = 7 }, parameters: NoRebase).Id);
            var eventHead = checkout.BaseCommitId;
            repository.SaveStageObserver = checkpoint => {
                Assert.Null(checkout.State);
                Assert.Equal(eventHead, checkout.BaseCommitId);
                if (checkpoint == (RepositorySaveStage)point) throw new IOException("Interrupted first State.");
            };
            var failure = Assert.Throws<RepositoryPublicationException>(() => checkout.CommitState(new Node { Value = 9 }, parameters: NoRebase).Id);
            Assert.Equal(published ? RepositoryPublicationOutcome.Published : RepositoryPublicationOutcome.NotPublished, failure.Outcome);
            Assert.Null(checkout.State);
            Assert.Equal(eventHead, checkout.BaseCommitId);
            Assert.True(checkout.IsFaulted);
            Assert.Throws<InvalidOperationException>(() => checkout.CommitState(new Node(), parameters: NoRebase).Id);
            Assert.Throws<InvalidOperationException>(() => checkout.CommitEvent(new Node(), parameters: NoRebase).Id);
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        using WorkingCopy recovered = reopened.OpenWorkingCopy(BranchNamed(reopened, "main"));
        if (published) {
            Assert.Equal((byte)9, Assert.IsType<Node>(recovered.State).Value);
            Assert.Equal(CommitKind.State, reopened.ReadCommitInfo(recovered.BaseCommitId).Kind);
        } else {
            Assert.Null(recovered.State);
            Assert.Equal((byte)7, Assert.IsType<Node>(reopened.LoadEvent(recovered.BaseCommitId)).Value);
            recovered.CommitState(new Node { Value = 11 }, parameters: NoRebase);
            Assert.Equal((byte)11, Assert.IsType<Node>(recovered.State).Value);
        }
    }

    [Fact]
    public void FirstStateUnknownRootAppendNeverInstallsCandidateAndFreshOpenFindsActualState() {
        using (Repository repository = CreateRepository()) {
            using WorkingCopy checkout = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("main", new Node { Value = 7 }, parameters: NoRebase).Id);
            var eventHead = checkout.BaseCommitId;
            InstallPublicationFault(ResourcesOf(repository).Heads, "AfterAppend", () => throw new IOException("After root record append"));
            var failure = Assert.Throws<RepositoryPublicationException>(() => checkout.CommitState(new Node { Value = 9 }, parameters: NoRebase).Id);
            Assert.Equal(RepositoryPublicationOutcome.Unknown, failure.Outcome);
            Assert.True(repository.IsFaulted);
            Assert.Null(checkout.State);
            Assert.Equal(eventHead, checkout.BaseCommitId);
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        using WorkingCopy recovered = reopened.OpenWorkingCopy(BranchNamed(reopened, "main"));
        Assert.Equal((byte)9, Assert.IsType<Node>(recovered.State).Value);
        Assert.Equal(CommitKind.State, reopened.ReadCommitInfo(recovered.BaseCommitId).Kind);
        recovered.CommitState(new Node { Value = 11 }, parameters: NoRebase);
        Assert.Equal((byte)11, Assert.IsType<Node>(recovered.State).Value);
    }

    [Fact]
    public void EventOnlyCaptureReentryAndDisposeRejectionKeepCheckoutUsable() {
        Action? duringCapture = null;
        using Repository repository = CreateRepository(Models(_ => duringCapture?.Invoke()));
        using WorkingCopy checkout = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("main", new Node(), parameters: NoRebase).Id);
        var initial = checkout.BaseCommitId;
        duringCapture = () => checkout.CommitEvent(new Node(), parameters: NoRebase);
        Assert.Throws<InvalidOperationException>(() => checkout.CommitState(new Node(), parameters: NoRebase).Id);
        Assert.False(repository.IsFaulted);
        Assert.Null(checkout.State);
        Assert.Equal(initial, checkout.BaseCommitId);
        duringCapture = () => {
            Assert.Throws<InvalidOperationException>(() => checkout.Dispose());
            Assert.Throws<InvalidOperationException>(() => repository.Dispose());
            Assert.Throws<InvalidOperationException>(() => repository.OpenWorkingCopy(BranchNamed(repository, "main")));
        };
        Node state = new() { Value = 3 };
        checkout.CommitState(state, parameters: NoRebase);
        Assert.Same(state, checkout.State);
        duringCapture = null;
        checkout.CommitState(parameters: NoRebase);
        checkout.Dispose();
        checkout.Dispose();
        Assert.Throws<ObjectDisposedException>(() => checkout.CommitEvent(new Node(), parameters: NoRebase).Id);
        Assert.Throws<ObjectDisposedException>(() => checkout.CommitState(new Node(), parameters: NoRebase).Id);
        using WorkingCopy replacement = repository.OpenWorkingCopy(BranchNamed(repository, "main"));
        Assert.Equal((byte)3, Assert.IsType<Node>(replacement.State).Value);
    }

    [Fact]
    public void InitialEventCaptureReentryAndReadonlyCreationRejectWithoutPublishingOrWriting() {
        Repository? current = null;
        bool reenter = true;
        using (Repository repository = CreateRepository(Models(_ => {
            if (reenter) current!.OpenWorkingCopy(current!.CreateRootBranchFromEvent("nested", new Node(), parameters: NoRebase).Id);
        }))) {
            current = repository;
            Assert.Throws<InvalidOperationException>(() => repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("main", new Node(), parameters: NoRebase).Id));
            Assert.False(repository.IsFaulted);
            Assert.Empty(repository.ListBranches());
            reenter = false;
            using WorkingCopy checkout = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("main", new Node { Value = 4 }, parameters: NoRebase).Id);
            Assert.Null(checkout.State);
        }
        var before = SnapshotFiles();
        using (Repository reader = Repository.OpenReadOnlyExisting(_root, Models())) {
            Assert.Throws<InvalidOperationException>(() => reader.OpenWorkingCopy(reader.CreateRootBranchFromEvent("forbidden", new Node(), parameters: NoRebase).Id));
            Assert.Throws<InvalidOperationException>(() => reader.OpenWorkingCopy(BranchNamed(reader, "main")));
            Assert.Equal((byte)4, Assert.IsType<Node>(reader.LoadEvent(reader.ReadCurrentCommit(BranchNamed(reader, "main")).Commit.Id)).Value);
        }
        AssertFiles(before);
    }


}
