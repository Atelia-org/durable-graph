using Atelia.VersionStore;
using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed partial class EventHistoryRepositoryTests {
    [Theory]
    [InlineData("BeforeDataConfirm", GraphCommitOutcome.NotPublished, false)]
    [InlineData("AfterAppend", GraphCommitOutcome.Unknown, true)]
    [InlineData("AfterConfirmed", GraphCommitOutcome.Published, true)]
    public void ActualRootPublicationStagePreservesEvidenceAcrossFreshOpen(string stage, GraphCommitOutcome outcome, bool visible) {
        using (Repository repository = CreateRepository()) {
            ResourcesOf(repository).Schemas.Register(Schema);
            InstallPublicationFault(ResourcesOf(repository).Versions, stage, () => throw new IOException(stage));
            var error = Assert.Throws<GraphCommitException>(() => repository.CreateBranch("main", new Node(), NoRebase));
            Assert.Equal(outcome, error.Outcome);
            Assert.True(repository.IsFaulted);
            Assert.NotNull(error.CandidateRevisionAddress);
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        Assert.Equal(visible, reopened.ListBranches().Contains("main"));
        if (visible) {
            using BranchCheckout main = reopened.Checkout("main");
            Assert.Equal(GraphFrameKind.State, main.Head.Kind);
        }
    }

    [Theory]
    [InlineData((int)CommitCheckpoint.AfterPublication)]
    [InlineData((int)CommitCheckpoint.BeforeInstall)]
    public void DgOnlyFaultInvalidatesRetainedWarmFacadesEvenWhenDataOwnerIsUsable(int point) {
        using Repository repository = CreateRepository();
        using BranchCheckout main = repository.CreateBranch("main", new Node(), NoRebase);
        GraphResources resources = ResourcesOf(repository);
        var retained = resources.States;
        FrameAddress old = main.Head.RevisionAddress;
        retained.Read(old);
        retained.ReadLiveObjectHeadMap(old);
        repository.Checkpoint = checkpoint => {
            if (checkpoint == (CommitCheckpoint)point) { throw new IOException("Post publication installation interruption"); }
        };
        GraphCommitException error = Assert.Throws<GraphCommitException>(() => main.CommitState(NoRebase));
        Assert.Equal(GraphCommitOutcome.Published, error.Outcome);
        Assert.True(repository.IsFaulted);
        resources.Data.EnsureUsable();
        Assert.Throws<ObjectDisposedException>(() => retained.Read(old));
        Assert.Throws<ObjectDisposedException>(() => retained.ReadLiveObjectHeadMap(old));
        Assert.Throws<ObjectDisposedException>(() => retained.Append(Atelia.DurableGraph.Storage.StateRevision.CreateObjectHeadMapBase(null, [], [])));
        Assert.Throws<InvalidOperationException>(() => resources.Schemas.GetRequired(Schema.SchemaId, Schema.Version));
    }

}

public sealed partial class RepositoryTagFailureWindowTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "durable-tag-window-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("BeforeDataConfirm", GraphCommitOutcome.NotPublished, false)]
    [InlineData("AfterAppend", GraphCommitOutcome.Unknown, true)]
    [InlineData("AfterConfirmed", GraphCommitOutcome.Published, true)]
    public void ActualTagPublicationStagePreservesEvidenceAcrossFreshOpen(string stage, GraphCommitOutcome outcome, bool visible) {
        FrameAddress saved;
        using (Repository repository = Repository.CreateNew(_root, SharedReadModel.Models())) {
            using BranchCheckout main = repository.CreateBranch("main", new Node { Value = 17 });
            saved = main.Head.RevisionAddress;
            InstallPublicationFault(ResourcesOf(repository).Versions, stage, () => throw new IOException(stage), tag: true);
            var error = Assert.Throws<GraphCommitException>(() => repository.CreateTag("candidate", main.Head));
            Assert.Equal(outcome, error.Outcome);
            Assert.True(repository.IsFaulted);
            Assert.Null(error.CandidateRevisionAddress);
        }
        using Repository reopened = Repository.OpenReadOnlyExisting(_root, new StateModelRegistry());
        if (visible) { Assert.Equal(saved, reopened.ResolveTag("candidate").RevisionAddress); }
        else { Assert.ThrowsAny<Exception>(() => reopened.ResolveTag("candidate")); }
    }
    public void Dispose() => SharedReadModel.DeleteFixture(_root, "durable-tag-window-");
}
