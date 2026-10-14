using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
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
            var failure = Assert.Throws<GraphCommitException>(() =>
                repository.CreateBranchFromEvent("main", new Node { Value = 7 }, NoRebase));
            Assert.Equal(published ? GraphCommitOutcome.Published : GraphCommitOutcome.NotPublished, failure.Outcome);
            Assert.True(repository.IsFaulted);
            Assert.NotNull(failure.CandidateRevisionAddress);
            Assert.Throws<InvalidOperationException>(() => repository.GetHead("main"));
        }
        using Repository reopened = Repository.OpenExisting(_root, Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared);
        Assert.Equal(published, reopened.ListBranches().Contains("main"));
        if (published) {
            using BranchCheckout checkout = reopened.Checkout("main");
            Assert.Null(checkout.State);
            Assert.Equal((byte)7, Assert.IsType<Node>(reopened.ReadEvent(checkout.Head)).Value);
            checkout.CommitState(new Node { Value = 9 }, NoRebase);
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
            using BranchCheckout checkout = repository.CreateBranchFromEvent("main", new Node { Value = 7 }, NoRebase);
            var eventHead = checkout.Head;
            repository.SaveStageObserver = checkpoint => {
                Assert.Null(checkout.State);
                Assert.Equal(eventHead, checkout.Head);
                if (checkpoint == (RepositorySaveStage)point) throw new IOException("Interrupted first State.");
            };
            var failure = Assert.Throws<GraphCommitException>(() => checkout.CommitState(new Node { Value = 9 }, NoRebase));
            Assert.Equal(published ? GraphCommitOutcome.Published : GraphCommitOutcome.NotPublished, failure.Outcome);
            Assert.Null(checkout.State);
            Assert.Equal(eventHead, checkout.Head);
            Assert.True(checkout.IsFaulted);
            Assert.Throws<InvalidOperationException>(() => checkout.CommitState(new Node(), NoRebase));
            Assert.Throws<InvalidOperationException>(() => checkout.CommitEvent(new Node(), NoRebase));
        }
        using Repository reopened = Repository.OpenExisting(_root, Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared);
        using BranchCheckout recovered = reopened.Checkout("main");
        if (published) {
            Assert.Equal((byte)9, Assert.IsType<Node>(recovered.State).Value);
            Assert.Equal(GraphFrameKind.State, recovered.Head.Kind);
        } else {
            Assert.Null(recovered.State);
            Assert.Equal((byte)7, Assert.IsType<Node>(reopened.ReadEvent(recovered.Head)).Value);
            recovered.CommitState(new Node { Value = 11 }, NoRebase);
            Assert.Equal((byte)11, Assert.IsType<Node>(recovered.State).Value);
        }
    }

    [Fact]
    public void FirstStateUnknownRootAppendNeverInstallsCandidateAndFreshOpenFindsActualState() {
        using (Repository repository = CreateRepository()) {
            using BranchCheckout checkout = repository.CreateBranchFromEvent("main", new Node { Value = 7 }, NoRebase);
            var eventHead = checkout.Head;
            InstallPublicationFault(ResourcesOf(repository).Versions, "AfterAppend", () => throw new IOException("After root record append"));
            var failure = Assert.Throws<GraphCommitException>(() => checkout.CommitState(new Node { Value = 9 }, NoRebase));
            Assert.Equal(GraphCommitOutcome.Unknown, failure.Outcome);
            Assert.True(repository.IsFaulted);
            Assert.Null(checkout.State);
            Assert.Equal(eventHead, checkout.Head);
        }
        using Repository reopened = Repository.OpenExisting(_root, Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared);
        using BranchCheckout recovered = reopened.Checkout("main");
        Assert.Equal((byte)9, Assert.IsType<Node>(recovered.State).Value);
        Assert.Equal(GraphFrameKind.State, recovered.Head.Kind);
        recovered.CommitState(new Node { Value = 11 }, NoRebase);
        Assert.Equal((byte)11, Assert.IsType<Node>(recovered.State).Value);
    }

    [Fact]
    public void EventOnlyCaptureReentryAndDisposeRejectionKeepCheckoutUsable() {
        Action? duringCapture = null;
        using Repository repository = CreateRepository(Models(_ => duringCapture?.Invoke()));
        using BranchCheckout checkout = repository.CreateBranchFromEvent("main", new Node(), NoRebase);
        var initial = checkout.Head;
        duringCapture = () => checkout.CommitEvent(new Node(), NoRebase);
        Assert.Throws<InvalidOperationException>(() => checkout.CommitState(new Node(), NoRebase));
        Assert.False(repository.IsFaulted);
        Assert.Null(checkout.State);
        Assert.Equal(initial, checkout.Head);
        duringCapture = () => {
            Assert.Throws<InvalidOperationException>(() => checkout.Dispose());
            Assert.Throws<InvalidOperationException>(() => repository.Dispose());
            Assert.Throws<InvalidOperationException>(() => repository.Checkout("main"));
        };
        Node state = new() { Value = 3 };
        checkout.CommitState(state, NoRebase);
        Assert.Same(state, checkout.State);
        duringCapture = null;
        checkout.CommitState(NoRebase);
        checkout.Dispose();
        checkout.Dispose();
        Assert.Throws<ObjectDisposedException>(() => checkout.CommitEvent(new Node(), NoRebase));
        Assert.Throws<ObjectDisposedException>(() => checkout.CommitState(new Node(), NoRebase));
        using BranchCheckout replacement = repository.Checkout("main");
        Assert.Equal((byte)3, Assert.IsType<Node>(replacement.State).Value);
    }

    [Fact]
    public void InitialEventCaptureReentryAndReadonlyCreationRejectWithoutPublishingOrWriting() {
        Repository? current = null;
        bool reenter = true;
        using (Repository repository = CreateRepository(Models(_ => {
            if (reenter) current!.CreateBranchFromEvent("nested", new Node(), NoRebase);
        }))) {
            current = repository;
            Assert.Throws<InvalidOperationException>(() => repository.CreateBranchFromEvent("main", new Node(), NoRebase));
            Assert.False(repository.IsFaulted);
            Assert.Empty(repository.ListBranches());
            reenter = false;
            using BranchCheckout checkout = repository.CreateBranchFromEvent("main", new Node { Value = 4 }, NoRebase);
            Assert.Null(checkout.State);
        }
        var before = SnapshotFiles();
        using (Repository reader = Repository.OpenReadOnlyExisting(_root, Models())) {
            Assert.Throws<InvalidOperationException>(() => reader.CreateBranchFromEvent("forbidden", new Node(), NoRebase));
            Assert.Throws<InvalidOperationException>(() => reader.Checkout("main"));
            Assert.Equal((byte)4, Assert.IsType<Node>(reader.ReadEvent(reader.GetHead("main"))).Value);
        }
        AssertFiles(before);
    }


}
