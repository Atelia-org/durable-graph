using Atelia.DurableGraph.Persistence.MetaBootstrapP0;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed partial class DB078HistoryTests {
    [Fact]
    public void JointSaveAcceptsNewSchemaAndPreservesOtherBranchHeads() {
        FrameAddress otherHead, published;
        using (Repository repository = Create()) {
            using BranchCheckout main = repository.CreateBranch("main", new Alpha { Value = 1 }, NoRebase);
            otherHead = repository.CreateBranch("other", main.Head).RevisionAddress;
            GraphRecordStore store = repository.GraphStore;
            var key = new SchemaKey(BetaSchema.Type, BetaSchema.Version);
            Assert.False(SchemaCatalogView.Load(store, repository.SharedSchemaRoot!.Value).Schemas.ContainsKey(key));
            CheckpointAddress next = main.CommitState(new Beta { Value = 2 }, NoRebase);
            published = next.RevisionAddress;
            SchemaCatalogView catalog = SchemaCatalogView.Load(store, repository.SharedSchemaRoot!.Value);
            Assert.Equal(BetaSchema, catalog.Schemas[key]);
            Assert.Equal(catalog.Snapshot!.Address, store.ReadGraph(published).SchemaRoot);
            Assert.Equal(otherHead, repository.GetHead("other").RevisionAddress);
            Assert.Equal(next, repository.GetHead("main"));
        }
        using Repository reopened = Repository.OpenReadOnlyExisting(_root, Models());
        Assert.Equal(BetaSchema, SchemaCatalogView.Load(reopened.GraphStore, reopened.SharedSchemaRoot!.Value)
            .Schemas[new(BetaSchema.Type, BetaSchema.Version)]);
        Assert.Equal(otherHead, reopened.GetHead("other").RevisionAddress);
        Assert.Equal(published, reopened.GetHead("main").RevisionAddress);
        Assert.Equal((byte)2, Assert.IsType<Beta>(reopened.ReadState(reopened.GetHead("main"))).Value);
        Assert.Equal((byte)1, Assert.IsType<Alpha>(reopened.ReadState(reopened.GetHead("other"))).Value);
    }

    [Fact]
    public void UnknownFirstEndKeepsBothPublishedRootsOldAlthoughBothCandidateGraphsExist() {
        FrameAddress previous, oldSchema, candidate;
        using (Repository repository = Create()) {
            using BranchCheckout main = repository.CreateBranch("main", new Alpha { Value = 1 }, NoRebase);
            previous = main.Head.RevisionAddress;
            oldSchema = repository.SharedSchemaRoot!.Value;
            int graphCheckpoints = 0;
            repository.OperationStageObserver = _ => graphCheckpoints++;
            InstallPublicationFault(ResourcesOf(repository).Versions, "AfterAppend", () => throw new IOException("First joint End outcome unknown."));
            GraphCommitException error = Assert.Throws<GraphCommitException>(() => main.CommitState(new Beta { Value = 2 }, NoRebase));
            Assert.Equal(GraphCommitOutcome.Unknown, error.Outcome);
            Assert.IsType<IOException>(error.InnerException);
            candidate = Assert.IsType<FrameAddress>(error.CandidateRevisionAddress);
            Assert.Equal(1, graphCheckpoints);
            Assert.True(repository.IsFaulted);
            Assert.Equal(previous, main.Head.RevisionAddress);
            Assert.Equal((byte)1, Assert.IsType<Alpha>(main.State).Value);
            Assert.Throws<InvalidOperationException>(() => repository.GetHead("main"));
        }
        // Both graph candidates are complete; one CU End cannot expose either root.
        using Repository reopened = Repository.OpenExisting(_root, Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared);
        Assert.Equal(oldSchema, reopened.SharedSchemaRoot);
        Assert.False(SchemaCatalogView.Load(reopened.GraphStore, oldSchema).Schemas.ContainsKey(new(BetaSchema.Type, BetaSchema.Version)));
        StoredGraphRevision orphan = reopened.GraphStore.ReadGraph(candidate);
        Assert.NotEqual(oldSchema, orphan.SchemaRoot);
        Assert.Equal(BetaSchema, SchemaCatalogView.Load(reopened.GraphStore, orphan.SchemaRoot!.Value)
            .Schemas[new(BetaSchema.Type, BetaSchema.Version)]);
        Assert.Equal(previous, reopened.GetHead("main").RevisionAddress);
        Assert.Equal((byte)1, Assert.IsType<Alpha>(reopened.ReadState(reopened.GetHead("main"))).Value);
    }

    [Fact]
    public void PrepublicationFailureKeepsNewSchemaUnacceptedAlongsideTheOldBusinessRoot() {
        FrameAddress previous, oldSchema;
        using (Repository repository = Create()) {
            using BranchCheckout main = repository.CreateBranch("main", new Alpha { Value = 1 }, NoRebase);
            previous = main.Head.RevisionAddress;
            oldSchema = repository.SharedSchemaRoot!.Value;
            repository.CreateBranch("other", main.Head);
            repository.SaveStageObserver = checkpoint => {
                if (checkpoint == RepositorySaveStage.BeforePublication) { throw new IOException("Joint publication interrupted."); }
            };
            GraphCommitException error = Assert.Throws<GraphCommitException>(() => main.CommitState(new Beta { Value = 2 }, NoRebase));
            Assert.Equal(GraphCommitOutcome.NotPublished, error.Outcome);
            Assert.True(repository.IsFaulted);
            Assert.Equal(previous, main.Head.RevisionAddress);
        }
        using Repository reopened = Repository.OpenExisting(_root, Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared);
        Assert.Equal(oldSchema, reopened.SharedSchemaRoot);
        Assert.False(SchemaCatalogView.Load(reopened.GraphStore, oldSchema).Schemas.ContainsKey(new(BetaSchema.Type, BetaSchema.Version)));
        Assert.Equal(previous, reopened.GetHead("main").RevisionAddress);
        Assert.Equal(previous, reopened.GetHead("other").RevisionAddress);
        Assert.Equal((byte)1, Assert.IsType<Alpha>(reopened.ReadState(reopened.GetHead("main"))).Value);
    }
}
