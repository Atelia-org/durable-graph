using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;


namespace Atelia.DurableGraph.Persistence.Tests;

public sealed partial class DB078HistoryTests {
    [Fact]
    public void JointSaveAcceptsNewSchemaAndPreservesOtherBranchHeads() {
        FrameAddress otherHead, published;
        using (Repository repository = Create()) {
            using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", new Alpha { Value = 1 }, parameters: NoRebase).Id);
            BranchInfo other = repository.ForkBranch(main.BaseCommitId, "other");
            otherHead = Describe(repository, repository.ReadCurrentCommit(other.Id).Commit.Id).Address;
            GraphRecordStore store = repository.GraphStore;
            var key = new SchemaKey(BetaSchema.Type, BetaSchema.Version);
            Assert.False(SchemaCatalogView.Load(store, repository.SharedSchemaRoot!.Value).Schemas.ContainsKey(key));
            CommitId next = main.CommitState(new Beta { Value = 2 }, parameters: NoRebase).Id;
            published = Describe(repository, next).Address;
            SchemaCatalogView catalog = SchemaCatalogView.Load(store, repository.SharedSchemaRoot!.Value);
            Assert.Equal(BetaSchema, catalog.Schemas[key]);
            Assert.Equal(catalog.Snapshot!.Address, MechanismFixture.Snapshot(store, published).SchemaRoot);
            Assert.Equal(otherHead, Describe(repository, repository.ReadCurrentCommit(BranchNamed(repository, "other")).Commit.Id).Address);
            Assert.Equal(next, repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id);
        }
        using Repository reopened = Repository.OpenReadOnlyExisting(_root, Models());
        Assert.Equal(BetaSchema, SchemaCatalogView.Load(reopened.GraphStore, reopened.SharedSchemaRoot!.Value)
            .Schemas[new(BetaSchema.Type, BetaSchema.Version)]);
        Assert.Equal(otherHead, Describe(reopened, reopened.ReadCurrentCommit(BranchNamed(reopened, "other")).Commit.Id).Address);
        Assert.Equal(published, Describe(reopened, reopened.ReadCurrentCommit(BranchNamed(reopened, "main")).Commit.Id).Address);
        Assert.Equal((byte)2, Assert.IsType<Beta>(reopened.LoadState(reopened.ReadCurrentCommit(BranchNamed(reopened, "main")).Commit.Id)).Value);
        Assert.Equal((byte)1, Assert.IsType<Alpha>(reopened.LoadState(reopened.ReadCurrentCommit(BranchNamed(reopened, "other")).Commit.Id)).Value);
    }

    [Fact]
    public void UnknownFirstEndKeepsBothPublishedRootsOldAlthoughBothCandidateGraphsExist() {
        FrameAddress previous, oldSchema, candidate;
        using (Repository repository = Create()) {
            using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", new Alpha { Value = 1 }, parameters: NoRebase).Id);
            previous = main.StateRevisionAddress!.Value;
            oldSchema = repository.SharedSchemaRoot!.Value;
            int graphCheckpoints = 0;
            repository.OperationStageObserver = _ => graphCheckpoints++;
            InstallPublicationFault(ResourcesOf(repository).Heads, "AfterAppend", () => throw new IOException("First joint End outcome unknown."));
            RepositoryPublicationException error = Assert.Throws<RepositoryPublicationException>(() => main.CommitState(new Beta { Value = 2 }, parameters: NoRebase).Id);
            Assert.Equal(RepositoryPublicationOutcome.Unknown, error.Outcome);
            Assert.IsType<IOException>(error.InnerException);
            candidate = Assert.IsType<FrameAddress>(error.CandidateRevisionAddress);
            Assert.Equal(1, graphCheckpoints);
            Assert.True(repository.IsFaulted);
            Assert.Equal(previous, main.StateRevisionAddress!.Value);
            Assert.Equal((byte)1, Assert.IsType<Alpha>(main.State).Value);
            Assert.Throws<InvalidOperationException>(() => repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id);
        }
        // Both graph candidates are complete; one CU End cannot expose either root.
        using Repository reopened = Repository.OpenExisting(_root, Models());
        Assert.Equal(oldSchema, reopened.SharedSchemaRoot);
        Assert.False(SchemaCatalogView.Load(reopened.GraphStore, oldSchema).Schemas.ContainsKey(new(BetaSchema.Type, BetaSchema.Version)));
        StoredGraphRevision orphan = reopened.GraphStore.ReadBusinessGraph(candidate, previous);
        Assert.NotEqual(oldSchema, orphan.SchemaRoot);
        Assert.Equal(BetaSchema, SchemaCatalogView.Load(reopened.GraphStore, orphan.SchemaRoot!.Value)
            .Schemas[new(BetaSchema.Type, BetaSchema.Version)]);
        Assert.Equal(previous, Describe(reopened, reopened.ReadCurrentCommit(BranchNamed(reopened, "main")).Commit.Id).Address);
        Assert.Equal((byte)1, Assert.IsType<Alpha>(reopened.LoadState(reopened.ReadCurrentCommit(BranchNamed(reopened, "main")).Commit.Id)).Value);
    }

    [Fact]
    public void PrepublicationFailureKeepsNewSchemaUnacceptedAlongsideTheOldBusinessRoot() {
        FrameAddress previous, oldSchema;
        using (Repository repository = Create()) {
            using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", new Alpha { Value = 1 }, parameters: NoRebase).Id);
            previous = main.StateRevisionAddress!.Value;
            oldSchema = repository.SharedSchemaRoot!.Value;
            repository.ForkBranch(main.BaseCommitId, "other");
            repository.SaveStageObserver = checkpoint => {
                if (checkpoint == RepositorySaveStage.BeforePublication) { throw new IOException("Joint publication interrupted."); }
            };
            RepositoryPublicationException error = Assert.Throws<RepositoryPublicationException>(() => main.CommitState(new Beta { Value = 2 }, parameters: NoRebase).Id);
            Assert.Equal(RepositoryPublicationOutcome.NotPublished, error.Outcome);
            Assert.True(repository.IsFaulted);
            Assert.Equal(previous, main.StateRevisionAddress!.Value);
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        Assert.Equal(oldSchema, reopened.SharedSchemaRoot);
        Assert.False(SchemaCatalogView.Load(reopened.GraphStore, oldSchema).Schemas.ContainsKey(new(BetaSchema.Type, BetaSchema.Version)));
        Assert.Equal(previous, Describe(reopened, reopened.ReadCurrentCommit(BranchNamed(reopened, "main")).Commit.Id).Address);
        Assert.Equal(previous, Describe(reopened, reopened.ReadCurrentCommit(BranchNamed(reopened, "other")).Commit.Id).Address);
        Assert.Equal((byte)1, Assert.IsType<Alpha>(reopened.LoadState(reopened.ReadCurrentCommit(BranchNamed(reopened, "main")).Commit.Id)).Value);
    }
}
