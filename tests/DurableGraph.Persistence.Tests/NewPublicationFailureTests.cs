
using Atelia.VersionStore;
using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed partial class EventHistoryRepositoryTests {
    [Theory]
    [InlineData("BeforeDataConfirm", GraphCommitOutcome.NotPublished, false)]
    [InlineData("AfterAppend", GraphCommitOutcome.Unknown, false)]
    [InlineData("AfterConfirmed", GraphCommitOutcome.Published, true)]
    public void ActualRootPublicationStagePreservesEvidenceAcrossFreshOpen(string stage, GraphCommitOutcome outcome, bool visible) {
        FrameAddress candidate;
        using (Repository repository = CreateRepository()) {
            InstallPublicationFault(ResourcesOf(repository).Versions, stage, () => throw new IOException(stage));
            var error = Assert.Throws<GraphCommitException>(() => repository.CreateBranch("main", new Node(), NoRebase));
            Assert.Equal(outcome, error.Outcome);
            Assert.True(repository.IsFaulted);
            candidate = Assert.IsType<FrameAddress>(error.CandidateRevisionAddress);
        }
        using Repository reopened = Repository.OpenExisting(_root, Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared);
        Assert.Equal(visible, reopened.ListBranches().Contains("main"));
        StoredGraphRevision appended = reopened.GraphStore.ReadGraph(candidate);
        SchemaCatalogView candidateCatalog = SchemaCatalogView.Load(reopened.GraphStore, appended.SchemaRoot!.Value);
        Assert.Equal(Schema, candidateCatalog.Schemas[new(Schema.Type, Schema.Version)]);
        if (visible) {
            Assert.Equal(appended.SchemaRoot, reopened.SharedSchemaRoot);
            using BranchCheckout main = reopened.Checkout("main");
            Assert.Equal(CheckpointKind.State, main.Head.Kind);
            Assert.Equal(candidate, main.Head.RevisionAddress);
        } else {
            Assert.Null(reopened.SharedSchemaRoot); // The first CU End cannot publish only meta or only business.
        }
    }

    [Theory]
    [InlineData((int)RepositorySaveStage.AfterPublication)]
    [InlineData((int)RepositorySaveStage.BeforeInstall)]
    public void DgOnlyFaultInvalidatesRetainedWarmFacadesEvenWhenDataOwnerIsUsable(int point) {
        using Repository repository = CreateRepository();
        using BranchCheckout main = repository.CreateBranch("main", new Node(), NoRebase);
        RepositoryResources resources = ResourcesOf(repository);
        GraphRecordStore retained = resources.GraphStore;
        FrameAddress old = main.Head.RevisionAddress;
        FrameAddress schema = repository.SharedSchemaRoot!.Value;
        retained.ReadGraph(old);
        SchemaCatalogView.Load(retained, schema);
        Assert.True(retained.ReadCacheStatistics.EntryCount > 0);
        repository.SaveStageObserver = checkpoint => {
            if (checkpoint == (RepositorySaveStage)point) { throw new IOException("Post publication installation interruption"); }
        };
        GraphCommitException error = Assert.Throws<GraphCommitException>(() => main.CommitState(NoRebase));
        Assert.Equal(GraphCommitOutcome.Published, error.Outcome);
        Assert.True(repository.IsFaulted);
        resources.Data.EnsureUsable();
        Assert.Equal(0, retained.ReadCacheStatistics.EntryCount);
        Assert.Equal(0, retained.ReadCacheStatistics.ResidentChargeBytes);
        Assert.Throws<ObjectDisposedException>(() => retained.ReadGraph(old));
        Assert.Throws<ObjectDisposedException>(() => SchemaCatalogView.Load(retained, schema));
        Assert.Throws<ObjectDisposedException>(() => retained.Append(StoredGraphKind.State, null, schema, new(null, new(1), [])));
        Assert.Throws<InvalidOperationException>(resources.RequireAvailable);
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
        using (Repository repository = Repository.CreateNew(_root, SharedReadModel.Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
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
