using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;
namespace Atelia.DurableGraph.Persistence.Tests;

public sealed partial class EventHistoryRepositoryTests {
    [Fact]
    public void BranchIdentityOccupancyAllowsDuplicateLabelsAndIndependentForks() {
        using Repository repository = CreateRepository();
        BranchInfo mainInfo = repository.CreateRootBranchFromState("main", new Node { Value = 1 }, parameters: NoRebase);
        using WorkingCopy main = repository.OpenWorkingCopy(mainInfo.Id);
        BranchInfo otherInfo = repository.CreateRootBranchFromState("main", new Node { Value = 2 }, parameters: NoRebase);
        using WorkingCopy other = repository.OpenWorkingCopy(otherInfo.Id);
        BranchInfo eventInfo = repository.CreateRootBranchFromEvent("events", new Node());
        using WorkingCopy eventOnly = repository.OpenWorkingCopy(eventInfo.Id);
        CommitId initial = main.BaseCommitId;
        BranchInfo fork = repository.ForkBranch(initial, "main");
        using WorkingCopy child = repository.OpenWorkingCopy(fork.Id);
        Assert.Equal(3, repository.ListBranches().Count(branch => branch.DisplayLabel == "main"));
        Assert.Throws<InvalidOperationException>(() => repository.OpenWorkingCopy(mainInfo.Id));
        Assert.Throws<InvalidOperationException>(() => repository.OpenWorkingCopy(otherInfo.Id));
        main.CommitEvent(new Node());
        child.CommitState(parameters: NoRebase);
        Assert.NotEqual(main.BaseCommitId, child.BaseCommitId);
        other.CommitEvent(new Node());
        eventOnly.CommitState(new Node(), parameters: NoRebase);
        CommitId latest = main.BaseCommitId;
        main.Dispose();
        using WorkingCopy replacement = repository.OpenWorkingCopy(mainInfo.Id);
        Assert.Equal(latest, replacement.BaseCommitId);
        Assert.Equal(initial, repository.ReadBranchInfo(fork.Id).ForkedFromCommitId);
    }

    [Fact]
    public void FailedCheckoutRecoveryAndInvalidBranchDoNotLeakOccupancy() {
        bool fail = false;
        using Repository repository = CreateRepository(Models(onHydrate: _ => {
            if (fail) throw new InvalidDataException("restore failed");
        }));
        using WorkingCopy source = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("source", new Node(), parameters: NoRebase).Id);
        repository.ForkBranch(source.BaseCommitId, "retry");
        fail = true;
        var before = SnapshotLiveFiles();
        Assert.Throws<InvalidDataException>(() => repository.OpenWorkingCopy(BranchNamed(repository, "retry")));
        Assert.Throws<ArgumentException>(() => repository.OpenWorkingCopy(default));
        Assert.False(repository.IsFaulted);
        AssertLiveFiles(before);
        fail = false;
        using WorkingCopy retry = repository.OpenWorkingCopy(BranchNamed(repository, "retry"));
        using WorkingCopy formerlyMissing = repository.OpenWorkingCopy(repository.ForkBranch(source.BaseCommitId, "missing").Id);
        Assert.Throws<InvalidOperationException>(() => repository.OpenWorkingCopy(BranchNamed(repository, "retry")));
        source.CommitState(parameters: NoRebase);
        retry.CommitState(parameters: NoRebase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedInitialCaptureReleasesReservationWhileOtherBranchesRemainLive(bool eventFirst) {
        bool fail = false;
        using Repository repository = CreateRepository(Models(onCapture: _ => {
            if (fail) throw new InvalidDataException("capture failed");
        }));
        using WorkingCopy source = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("source", new Node(), parameters: NoRebase).Id);
        fail = true;
        var before = SnapshotLiveFiles();
        Assert.Throws<InvalidDataException>(() => {
            if (eventFirst) repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("retry", new Node()).Id);
            else repository.OpenWorkingCopy(repository.CreateRootBranchFromState("retry", new Node(), parameters: NoRebase).Id);
        });
        Assert.False(repository.IsFaulted);
        AssertLiveFiles(before);
        fail = false;
        using WorkingCopy retry = eventFirst
            ? repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("retry", new Node()).Id)
            : repository.OpenWorkingCopy(repository.CreateRootBranchFromState("retry", new Node(), parameters: NoRebase).Id);
        Assert.Throws<InvalidOperationException>(() => repository.OpenWorkingCopy(BranchNamed(repository, "source")));
        source.CommitState(parameters: NoRebase);
        retry.CommitState(new Node(), parameters: NoRebase);
    }

    [Fact]
    public void RepeatedOldCheckoutDisposeCannotReleaseItsSuccessorOrOtherBranches() {
        using Repository repository = CreateRepository();
        WorkingCopy old = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", new Node(), parameters: NoRebase).Id);
        using WorkingCopy other = repository.OpenWorkingCopy(repository.ForkBranch(old.BaseCommitId, "other").Id);
        old.Dispose();
        using WorkingCopy successor = repository.OpenWorkingCopy(BranchNamed(repository, "main"));
        old.Dispose();
        Assert.Throws<InvalidOperationException>(() => repository.OpenWorkingCopy(BranchNamed(repository, "main")));
        Assert.Throws<InvalidOperationException>(() => repository.OpenWorkingCopy(BranchNamed(repository, "other")));
        Assert.Throws<ObjectDisposedException>(() => old.CommitState(parameters: NoRebase).Id);
        Assert.Throws<InvalidOperationException>(() => repository.Commit(old, null, new Node(), default, NoRebase));
        successor.CommitState(parameters: NoRebase);
        other.CommitState(parameters: NoRebase);
    }

    [Theory]
    [InlineData("allocate")]
    [InlineData("hydrate")]
    [InlineData("before-publication")]
    [InlineData("after-publication")]
    public void EntireForkRemainsGuardedIncludingCallbacksAfterRefPublication(string phase) {
        Action? duringAllocate = null, duringHydrate = null;
        using Repository repository = CreateRepository(Models(
            onHydrate: _ => duringHydrate?.Invoke(),
            allocate: () => { duringAllocate?.Invoke(); return new Node(); }));
        using WorkingCopy source = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", new Node(), parameters: NoRebase).Id);
        CommitId initial = source.BaseCommitId;
        int callbackCount = 0;
        void Reenter() {
            callbackCount++;
            Assert.Throws<InvalidOperationException>(() => repository.OpenWorkingCopy(repository.ForkBranch(initial, "nested").Id));
            Assert.Throws<InvalidOperationException>(() => repository.OpenWorkingCopy(BranchNamed(repository, "main")));
            Assert.Throws<InvalidOperationException>(() => repository.ForkBranch(initial, "ref"));
            Assert.Throws<InvalidOperationException>(() => source.CommitState(parameters: NoRebase).Id);
            Assert.Throws<InvalidOperationException>(() => source.Dispose());
            Assert.Throws<InvalidOperationException>(() => repository.Dispose());
        }
        if (phase == "allocate") duringAllocate = Reenter;
        if (phase == "hydrate") duringHydrate = Reenter;
        repository.OperationStageObserver = point => {
            if (phase == "before-publication" && point == RepositoryOperationStage.BeforePublication ||
                phase == "after-publication" && point == RepositoryOperationStage.AfterPublication) Reenter();
        };
        using WorkingCopy child = repository.OpenWorkingCopy(repository.ForkBranch(initial, "child").Id);
        Assert.True(callbackCount > 0);
        duringAllocate = duringHydrate = null;
        repository.OperationStageObserver = null;
        Assert.Throws<InvalidOperationException>(() => repository.OpenWorkingCopy(BranchNamed(repository, "main")));
        Assert.Throws<InvalidOperationException>(() => repository.OpenWorkingCopy(BranchNamed(repository, "child")));
        Assert.DoesNotContain("nested", repository.ListBranches().Select(branch => branch.DisplayLabel));
        Assert.False(repository.IsFaulted);
        source.CommitState(parameters: NoRebase);
        child.CommitState(parameters: NoRebase);
    }

    [Fact]
    public void DisposedRepositoryAndOldCheckoutsCannotInterfereWithFreshOpen() {
        Repository oldRepository = CreateRepository();
        WorkingCopy oldMain = oldRepository.OpenWorkingCopy(oldRepository.CreateRootBranchFromState("main", new Node(), parameters: NoRebase).Id);
        WorkingCopy oldOther = oldRepository.OpenWorkingCopy(oldRepository.ForkBranch(oldMain.BaseCommitId, "other").Id);
        CommitId oldAddress = oldMain.BaseCommitId;
        oldRepository.Dispose();
        using Repository reopened = Repository.OpenExisting(_root, Models());
        using WorkingCopy current = reopened.OpenWorkingCopy(BranchNamed(reopened, "main"));
        using WorkingCopy currentOther = reopened.OpenWorkingCopy(BranchNamed(reopened, "other"));
        Assert.Throws<ObjectDisposedException>(() => oldMain.CommitState(parameters: NoRebase).Id);
        Assert.Throws<ObjectDisposedException>(() => oldOther.CommitEvent(new Node()).Id);
        using WorkingCopy inherited = reopened.OpenWorkingCopy(reopened.ForkBranch(oldAddress, "persisted").Id);
        Assert.Equal(oldAddress, inherited.BaseCommitId);
        Assert.Throws<InvalidOperationException>(() => reopened.Commit(oldMain, null, new Node(), default, NoRebase));
        oldMain.Dispose();
        oldOther.Dispose();
        oldRepository.Dispose();
        Assert.Throws<InvalidOperationException>(() => reopened.OpenWorkingCopy(BranchNamed(reopened, "main")));
        Assert.Throws<InvalidOperationException>(() => reopened.OpenWorkingCopy(BranchNamed(reopened, "other")));
        current.CommitState(parameters: NoRebase);
        currentOther.CommitState(parameters: NoRebase);
    }

    [Fact]
    public void ForkUsesPersistentIdsAndRejectsDefaultForeignAndReadonlyRequestsBeforeRestore() {
        CommitId old;
        using (Repository repository = CreateRepository()) {
            BranchInfo main = repository.CreateRootBranchFromState("main", new Node(), parameters: NoRebase);
            old = repository.ReadCurrentCommit(main.Id).Commit.Id;
        }
        int hydrations = 0;
        using (Repository repository = Repository.OpenExisting(_root, Models(onHydrate: _ => hydrations++))) {
            var before = SnapshotLiveFiles();
            Assert.ThrowsAny<ArgumentException>(() => repository.ForkBranch(default, "invalid"));
            Assert.Equal(0, hydrations);
            AssertLiveFiles(before);
            using EventHistoryRepositoryTests foreignFixture = new();
            using Repository foreign = foreignFixture.CreateRepository();
            BranchInfo foreignBranch = foreign.CreateRootBranchFromState("main", new Node(), parameters: NoRebase);
            Assert.ThrowsAny<Exception>(() => repository.ForkBranch(foreign.ReadCurrentCommit(foreignBranch.Id).Commit.Id, "foreign"));
            AssertLiveFiles(before);
            BranchInfo duplicate = repository.ForkBranch(old, "main");
            Assert.Equal(0, hydrations);
            Assert.Equal(old, repository.ReadCurrentCommit(duplicate.Id).Commit.Id);
            using WorkingCopy valid = repository.OpenWorkingCopy(duplicate.Id);
            Assert.Equal(1, hydrations);
        }
        var readOnlyBefore = SnapshotFiles();
        using (Repository reader = Repository.OpenReadOnlyExisting(_root, Models())) {
            Assert.Throws<InvalidOperationException>(() => reader.ForkBranch(old, "forbidden"));
        }
        AssertFiles(readOnlyBefore);
    }
}
