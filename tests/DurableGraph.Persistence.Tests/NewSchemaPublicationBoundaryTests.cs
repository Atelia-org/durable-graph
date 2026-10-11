using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed partial class DB078HistoryTests {
    [Fact]
    public void StageAcceptsNewSchemaAndBranchPublicationPreservesLatestCatalogAndOtherBranches() {
        FrameAddress otherHead;
        FrameAddress published;
        using (Repository repository = Create()) {
            using BranchCheckout main = repository.CreateBranch("main", new Alpha { Value = 1 }, NoRebase);
            otherHead = repository.CreateBranch("other", main.Head).RevisionAddress;
            GraphResources resources = ResourcesOf(repository);
            Assert.False(resources.Schemas.TryGet(new(BetaSchema.Type, BetaSchema.Version), out _));
            CheckpointAddress next = main.CommitState(new Beta { Value = 2 }, NoRebase);
            published = next.RevisionAddress;
            Assert.Equal(BetaSchema, resources.Schemas.GetRequired(BetaSchema.Type, BetaSchema.Version));
            Assert.Equal(otherHead, repository.GetHead("other").RevisionAddress);
            Assert.Equal(next, repository.GetHead("main"));
        }
        using Repository reopened = Repository.OpenReadOnlyExisting(_root, Models());
        Assert.Equal(BetaSchema, ResourcesOf(reopened).Schemas.GetRequired(BetaSchema.Type, BetaSchema.Version));
        Assert.Equal(otherHead, reopened.GetHead("other").RevisionAddress);
        Assert.Equal(published, reopened.GetHead("main").RevisionAddress);
        Assert.Equal((byte)2, Assert.IsType<Beta>(reopened.ReadState(reopened.GetHead("main"))).Value);
        Assert.Equal((byte)1, Assert.IsType<Alpha>(reopened.ReadState(reopened.GetHead("other"))).Value);
    }

    [Fact]
    public void UnknownSchemaPublicationDuringStageDoesNotAttemptBusinessGraphPublication() {
        FrameAddress previous;
        using (Repository repository = Create()) {
            using BranchCheckout main = repository.CreateBranch("main", new Alpha { Value = 1 }, NoRebase);
            previous = main.Head.RevisionAddress;
            GraphResources resources = ResourcesOf(repository);
            int graphCheckpoints = 0;
            repository.Checkpoint = _ => graphCheckpoints++;
            InstallPublicationFault(resources.Versions, "AfterAppend", () => throw new IOException("Schema root outcome unknown."));
            // Stage's independent Schema publication fails before graph append/publication starts.
            IOException error = Assert.Throws<IOException>(() => main.CommitState(new Beta { Value = 2 }, NoRebase));
            Assert.Equal("Schema root outcome unknown.", error.Message);
            Assert.Equal(0, graphCheckpoints);
            Assert.True(repository.IsFaulted);
            Assert.Equal(previous, main.Head.RevisionAddress);
            Assert.Equal((byte)1, Assert.IsType<Alpha>(main.State).Value);
            Assert.Throws<InvalidOperationException>(() => repository.GetHead("main"));
        }
        // Complete root append is visible after this process-local close; this is not a power-loss claim.
        using Repository reopened = Repository.OpenExisting(_root, Models());
        GraphResources recovered = ResourcesOf(reopened);
        Assert.Equal(BetaSchema, recovered.Schemas.GetRequired(BetaSchema.Type, BetaSchema.Version));
        Assert.Equal(previous, reopened.GetHead("main").RevisionAddress);
        Assert.Equal((byte)1, Assert.IsType<Alpha>(reopened.ReadState(reopened.GetHead("main"))).Value);
        int graphs = 0;
        recovered.Data.Inventory(frame => { if (frame.Tag == StateRevisionStore.FrameTag) { graphs++; } }).Unwrap();
        Assert.Equal(1, graphs);
    }

    [Fact]
    public void ConfirmedSchemaRemainsAcceptedWhenLaterBusinessPublicationFails() {
        FrameAddress previous;
        using (Repository repository = Create()) {
            using BranchCheckout main = repository.CreateBranch("main", new Alpha { Value = 1 }, NoRebase);
            previous = main.Head.RevisionAddress;
            repository.CreateBranch("other", main.Head);
            repository.Checkpoint = checkpoint => {
                if (checkpoint == CommitCheckpoint.BeforePublication) { throw new IOException("Business publication interrupted."); }
            };
            GraphCommitException error = Assert.Throws<GraphCommitException>(() => main.CommitState(new Beta { Value = 2 }, NoRebase));
            Assert.Equal(GraphCommitOutcome.NotPublished, error.Outcome);
            Assert.True(repository.IsFaulted);
            Assert.Equal(previous, main.Head.RevisionAddress);
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        Assert.Equal(BetaSchema, ResourcesOf(reopened).Schemas.GetRequired(BetaSchema.Type, BetaSchema.Version));
        Assert.Equal(previous, reopened.GetHead("main").RevisionAddress);
        Assert.Equal(previous, reopened.GetHead("other").RevisionAddress);
        Assert.Equal((byte)1, Assert.IsType<Alpha>(reopened.ReadState(reopened.GetHead("main"))).Value);
    }
}
