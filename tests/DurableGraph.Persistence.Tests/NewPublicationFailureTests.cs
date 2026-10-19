using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;

using Atelia.HeadStore;
using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed partial class EventHistoryRepositoryTests {
    [Theory]
    [InlineData("BeforeDataConfirm", RepositoryPublicationOutcome.NotPublished, false)]
    [InlineData("AfterAppend", RepositoryPublicationOutcome.Unknown, false)]
    [InlineData("AfterConfirmed", RepositoryPublicationOutcome.Published, true)]
    public void ActualRootPublicationStagePreservesEvidenceAcrossFreshOpen(string stage, RepositoryPublicationOutcome outcome, bool visible) {
        FrameAddress candidate;
        using (Repository repository = CreateRepository()) {
            repository.SaveStageObserver = point => {
                if (point == RepositorySaveStage.BeforePublication) InstallPublicationFault(ResourcesOf(repository).Heads, stage, () => throw new IOException(stage));
            };
            var error = Assert.Throws<RepositoryPublicationException>(() => repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", new Node(), parameters: NoRebase).Id));
            Assert.Equal(outcome, error.Outcome);
            Assert.True(repository.IsFaulted);
            candidate = Assert.IsType<FrameAddress>(error.CandidateRevisionAddress);
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        Assert.Equal(visible, reopened.ListBranches().Any(branch => branch.DisplayLabel == "main"));
        StoredGraphRevision appended = reopened.GraphStore.ReadBusinessGraph(candidate, null);
        SchemaCatalogView candidateCatalog = SchemaCatalogView.Load(reopened.GraphStore, appended.SchemaRoot!.Value);
        Assert.Equal(Schema, candidateCatalog.Schemas[new(Schema.Type, Schema.Version)]);
        if (visible) {
            Assert.Equal(appended.SchemaRoot, reopened.SharedSchemaRoot);
            using WorkingCopy main = reopened.OpenWorkingCopy(BranchNamed(reopened, "main"));
            Assert.Equal(CommitKind.State, WorkTestAccess.Kind(main));
            Assert.Equal(candidate, WorkTestAccess.Address(main, main.BaseCommitId));
        } else {
            Assert.Null(reopened.SharedSchemaRoot); // The first CU End cannot publish only meta or only business.
        }
    }

    [Theory]
    [InlineData((int)RepositorySaveStage.AfterPublication)]
    [InlineData((int)RepositorySaveStage.BeforeInstall)]
    public void DgOnlyFaultInvalidatesRetainedWarmFacadesEvenWhenDataOwnerIsUsable(int point) {
        using Repository repository = CreateRepository();
        using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", new Node(), parameters: NoRebase).Id);
        RepositoryResources resources = ResourcesOf(repository);
        GraphRecordStore retained = resources.GraphStore;
        FrameAddress old = WorkTestAccess.Address(main, main.BaseCommitId);
        FrameAddress schema = repository.SharedSchemaRoot!.Value;
        MechanismFixture.Snapshot(retained, old);
        SchemaCatalogView.Load(retained, schema);
        Assert.True(retained.ReadCacheStatistics.EntryCount > 0);
        repository.SaveStageObserver = checkpoint => {
            if (checkpoint == (RepositorySaveStage)point) { throw new IOException("Post publication installation interruption"); }
        };
        RepositoryPublicationException error = Assert.Throws<RepositoryPublicationException>(() => main.CommitState(parameters: NoRebase).Id);
        Assert.Equal(RepositoryPublicationOutcome.Published, error.Outcome);
        Assert.True(repository.IsFaulted);
        resources.Data.EnsureUsable();
        Assert.Equal(0, retained.ReadCacheStatistics.EntryCount);
        Assert.Equal(0, retained.ReadCacheStatistics.ResidentChargeBytes);
        Assert.Throws<ObjectDisposedException>(() => MechanismFixture.Snapshot(retained, old));
        Assert.Throws<ObjectDisposedException>(() => SchemaCatalogView.Load(retained, schema));
        Assert.Throws<ObjectDisposedException>(() => retained.AppendBusiness(StoredGraphKind.State, null, schema, new(null, new(1), [])));
        Assert.Throws<InvalidOperationException>(resources.RequireAvailable);
    }

}

public sealed partial class RepositoryTagFailureWindowTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "durable-tag-window-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("BeforeDataConfirm", RepositoryPublicationOutcome.NotPublished, false)]
    [InlineData("AfterAppend", RepositoryPublicationOutcome.Unknown, true)]
    [InlineData("AfterConfirmed", RepositoryPublicationOutcome.Published, true)]
    public void ActualTagPublicationStagePreservesEvidenceAcrossFreshOpen(string stage, RepositoryPublicationOutcome outcome, bool visible) {
        FrameAddress saved;
        using (Repository repository = Repository.CreateNew(_root, SharedReadModel.Models())) {
            using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", new Node { Value = 17 }).Id);
            saved = WorkTestAccess.Address(main, main.BaseCommitId);
            InstallPublicationFault(ResourcesOf(repository).Heads, stage, () => throw new IOException(stage));
            var error = Assert.Throws<RepositoryPublicationException>(() => repository.CreateTag("candidate", main.BaseCommitId));
            Assert.Equal(outcome, error.Outcome);
            Assert.True(repository.IsFaulted);
            Assert.Null(error.CandidateRevisionAddress);
        }
        using Repository reopened = Repository.OpenReadOnlyExisting(_root, new StateModelRegistry());
        if (visible) { Assert.Equal(saved, Describe(reopened, reopened.ResolveTag("candidate")).Address); }
        else { Assert.ThrowsAny<Exception>(() => reopened.ResolveTag("candidate")); }
    }
    public void Dispose() => SharedReadModel.DeleteFixture(_root, "durable-tag-window-");
}
