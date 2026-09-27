namespace Atelia.DurableGraph.Persistence.Tests;

public sealed partial class EventHistoryRepositoryTests {
    [Fact]
    public void MultipleBranchCreatorsAndRefOperationsOnlyExcludeTheirOwnActiveBranch() {
        int captures = 0, hydrations = 0;
        using Repository repository = CreateRepository(Models(onCapture: _ => captures++, onHydrate: _ => hydrations++));
        using BranchCheckout main = repository.CreateBranch("main", new Node { Value = 1 }, NoRebase);
        using BranchCheckout other = repository.CreateBranch("other", new Node { Value = 2 }, NoRebase);
        using BranchCheckout eventOnly = repository.CreateBranchFromEvent("events", new Node { Value = 3 }, NoRebase);
        CheckpointAddress initial = main.Head;
        repository.CreateBranch("movable", initial);
        CheckpointAddress next = main.CommitEvent(new Node(), NoRebase);
        var before = SnapshotLiveFiles();
        int priorCaptures = captures, priorHydrations = hydrations;
        Assert.Throws<InvalidOperationException>(() => repository.Checkout("main"));
        Assert.Throws<InvalidOperationException>(() => repository.CreateBranch("main", new Node(), NoRebase));
        Assert.Throws<InvalidOperationException>(() => repository.CreateBranchFromEvent("main", new Node(), NoRebase));
        Assert.Throws<InvalidOperationException>(() => repository.Fork("main", initial));
        Assert.Throws<InvalidOperationException>(() => repository.MoveBranch("main", next, initial));
        Assert.Throws<InvalidOperationException>(() => repository.MoveBranch("movable", next, initial));
        Assert.Equal(priorCaptures, captures);
        Assert.Equal(priorHydrations, hydrations);
        AssertLiveFiles(before);
        repository.MoveBranch("movable", initial, next);
        Assert.Equal(next, repository.GetHead("movable"));
        main.CommitState(NoRebase);
        other.CommitEvent(new Node(), NoRebase);
        eventOnly.CommitState(new Node(), NoRebase);
        main.Dispose();
        repository.MoveBranch("main", main.Head, initial);
        using BranchCheckout replacement = repository.Checkout("main");
        Assert.Equal(initial, replacement.Head);
        Assert.Throws<InvalidOperationException>(() => repository.Checkout("other"));
    }

    [Fact]
    public void FailedCheckoutRecoveryAndMissingBranchDoNotLeakOccupancy() {
        bool fail = false;
        using Repository repository = CreateRepository(Models(onHydrate: _ => {
            if (fail) throw new InvalidDataException("restore failed");
        }));
        using BranchCheckout source = repository.CreateBranch("source", new Node(), NoRebase);
        repository.CreateBranch("retry", source.Head);
        fail = true;
        var before = SnapshotLiveFiles();
        Assert.Throws<InvalidDataException>(() => repository.Checkout("retry"));
        Assert.ThrowsAny<Exception>(() => repository.Checkout("missing"));
        Assert.False(repository.IsFaulted);
        AssertLiveFiles(before);
        fail = false;
        using BranchCheckout retry = repository.Checkout("retry");
        using BranchCheckout formerlyMissing = repository.Fork("missing", source.Head);
        Assert.Throws<InvalidOperationException>(() => repository.Checkout("retry"));
        source.CommitState(NoRebase);
        retry.CommitState(NoRebase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedInitialCaptureReleasesReservationWhileOtherBranchesRemainLive(bool eventFirst) {
        bool fail = false;
        using Repository repository = CreateRepository(Models(onCapture: _ => {
            if (fail) throw new InvalidDataException("capture failed");
        }));
        using BranchCheckout source = repository.CreateBranch("source", new Node(), NoRebase);
        fail = true;
        var before = SnapshotLiveFiles();
        Assert.Throws<InvalidDataException>(() => {
            if (eventFirst) repository.CreateBranchFromEvent("retry", new Node(), NoRebase);
            else repository.CreateBranch("retry", new Node(), NoRebase);
        });
        Assert.False(repository.IsFaulted);
        AssertLiveFiles(before);
        fail = false;
        using BranchCheckout retry = eventFirst
            ? repository.CreateBranchFromEvent("retry", new Node(), NoRebase)
            : repository.CreateBranch("retry", new Node(), NoRebase);
        Assert.Throws<InvalidOperationException>(() => repository.Checkout("source"));
        source.CommitState(NoRebase);
        retry.CommitState(new Node(), NoRebase);
    }

    [Fact]
    public void RepeatedOldCheckoutDisposeCannotReleaseItsSuccessorOrOtherBranches() {
        using Repository repository = CreateRepository();
        BranchCheckout old = repository.CreateBranch("main", new Node(), NoRebase);
        using BranchCheckout other = repository.Fork("other", old.Head);
        old.Dispose();
        using BranchCheckout successor = repository.Checkout("main");
        old.Dispose();
        Assert.Throws<InvalidOperationException>(() => repository.Checkout("main"));
        Assert.Throws<InvalidOperationException>(() => repository.Checkout("other"));
        Assert.Throws<ObjectDisposedException>(() => old.CommitState(NoRebase));
        Assert.Throws<InvalidOperationException>(() => repository.Commit(old, null, new Node(), NoRebase));
        successor.CommitState(NoRebase);
        other.CommitState(NoRebase);
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
        using BranchCheckout source = repository.CreateBranch("main", new Node(), NoRebase);
        CheckpointAddress initial = source.Head;
        int callbackCount = 0;
        void Reenter() {
            callbackCount++;
            Assert.Throws<InvalidOperationException>(() => repository.Fork("nested", initial));
            Assert.Throws<InvalidOperationException>(() => repository.Checkout("main"));
            Assert.Throws<InvalidOperationException>(() => repository.CreateBranch("ref", initial));
            Assert.Throws<InvalidOperationException>(() => repository.MoveBranch("main", initial, initial));
            Assert.Throws<InvalidOperationException>(() => source.CommitState(NoRebase));
            Assert.Throws<InvalidOperationException>(() => source.Dispose());
            Assert.Throws<InvalidOperationException>(() => repository.Dispose());
        }
        if (phase == "allocate") duringAllocate = Reenter;
        if (phase == "hydrate") duringHydrate = Reenter;
        repository.Checkpoint = point => {
            if (phase == "before-publication" && point == CommitCheckpoint.BeforePublication ||
                phase == "after-publication" && point == CommitCheckpoint.AfterPublication) Reenter();
        };
        using BranchCheckout child = repository.Fork("child", initial);
        Assert.True(callbackCount > 0);
        duringAllocate = duringHydrate = null;
        repository.Checkpoint = null;
        Assert.Throws<InvalidOperationException>(() => repository.Checkout("main"));
        Assert.Throws<InvalidOperationException>(() => repository.Checkout("child"));
        Assert.DoesNotContain("nested", repository.ListBranches());
        Assert.False(repository.IsFaulted);
        source.CommitState(NoRebase);
        child.CommitState(NoRebase);
    }

    [Fact]
    public void DisposedRepositoryAndOldCheckoutsCannotInterfereWithFreshOpen() {
        Repository oldRepository = CreateRepository();
        BranchCheckout oldMain = oldRepository.CreateBranch("main", new Node(), NoRebase);
        BranchCheckout oldOther = oldRepository.Fork("other", oldMain.Head);
        CheckpointAddress oldAddress = oldMain.Head;
        oldRepository.Dispose();
        using Repository reopened = Repository.OpenExisting(_root, Models());
        using BranchCheckout current = reopened.Checkout("main");
        using BranchCheckout currentOther = reopened.Checkout("other");
        Assert.Throws<ObjectDisposedException>(() => oldMain.CommitState(NoRebase));
        Assert.Throws<ObjectDisposedException>(() => oldOther.CommitEvent(new Node(), NoRebase));
        Assert.Throws<ArgumentException>(() => reopened.Fork("stale", oldAddress));
        Assert.Throws<InvalidOperationException>(() => reopened.Commit(oldMain, null, new Node(), NoRebase));
        oldMain.Dispose();
        oldOther.Dispose();
        oldRepository.Dispose();
        Assert.Throws<InvalidOperationException>(() => reopened.Checkout("main"));
        Assert.Throws<InvalidOperationException>(() => reopened.Checkout("other"));
        current.CommitState(NoRebase);
        currentOther.CommitState(NoRebase);
    }

    [Fact]
    public void ForkRejectsForeignNullInvalidDuplicateAndReadonlyRequestsBeforeRestoreOrWrites() {
        CheckpointAddress old;
        using (Repository repository = CreateRepository()) {
            using BranchCheckout main = repository.CreateBranch("main", new Node(), NoRebase);
            old = main.Head;
        }
        int hydrations = 0;
        using (Repository repository = Repository.OpenExisting(_root, Models(onHydrate: _ => hydrations++))) {
            CheckpointAddress current = repository.GetHead("main");
            var before = SnapshotLiveFiles();
            Assert.Throws<ArgumentNullException>(() => repository.Fork("null", null!));
            Assert.Throws<ArgumentException>(() => repository.Fork("old", old));
            Assert.ThrowsAny<ArgumentException>(() => repository.Fork("bad/name", current));
            Assert.Throws<InvalidOperationException>(() => repository.Fork("main", current));
            Assert.Equal(0, hydrations);
            AssertLiveFiles(before);
            using EventHistoryRepositoryTests foreignFixture = new();
            using Repository foreign = foreignFixture.CreateRepository();
            using BranchCheckout foreignCheckout = foreign.CreateBranch("main", new Node(), NoRebase);
            Assert.Throws<ArgumentException>(() => repository.Fork("foreign", foreignCheckout.Head));
            AssertLiveFiles(before);
            using BranchCheckout valid = repository.Fork("valid", current);
            Assert.Equal(1, hydrations);
        }
        var readOnlyBefore = SnapshotFiles();
        using (Repository reader = Repository.OpenReadOnlyExisting(_root, Models())) {
            Assert.Throws<InvalidOperationException>(() => reader.Fork("forbidden", reader.GetHead("main")));
        }
        AssertFiles(readOnlyBefore);
    }
}
