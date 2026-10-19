using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Persistence;

using Atelia.FrameStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void GeneratedSchemaConflictRejectsEntireClosureBeforeAppendAndPreservesTheAcceptedState() {
        GeneratorTestRun run = RunGenerator(PreparedRevisionSource);
        AssertSchemaOnlyCompiles(run);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("PreparedRevisionWitness.Host")!;
        StateModelRegistry models = host.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>()();
        IDurableObject original = (IDurableObject)host.GetMethod("NewTag")!.CreateDelegate<Func<object>>()();
        IDurableObject replacement = (IDurableObject)host.GetMethod("NewOwner")!.CreateDelegate<Func<object>>()();
        DurableSchema requested = models.CreateEnvironment().ResolveCurrentModel(replacement.GetType()).CurrentSchema;
        DurableSchema conflicting = new(requested.Type, requested.Version, []);
        using RawBaseDirectory directory = new();
        using (Repository repository = Repository.CreateNew(directory.Path, models)) {
            BranchInfo branch = repository.CreateRootBranchFromState("main", original);
            using WorkingCopy checkout = repository.OpenWorkingCopy(branch.Id);
            IDurableObject acceptedState = checkout.State!;
            CommitId head = checkout.BaseCommitId;
            FrameAddress? schemaRoot = DB082Resources(repository).SharedSchemaRoot;
            long appended = DB082Resources(repository).GraphStore.AppendedFrames;

            Assert.Throws<SchemaConflictException>(() => repository.CommitBranches([checkout.CreateStateCommitRequest(replacement)], [conflicting]));
            Assert.Equal(head, checkout.BaseCommitId);
            Assert.Equal(head, repository.ReadCurrentCommit(checkout.BranchId).Commit.Id);
            Assert.Equal(schemaRoot, DB082Resources(repository).SharedSchemaRoot);
            Assert.Equal(appended, DB082Resources(repository).GraphStore.AppendedFrames);
            Assert.Same(acceptedState, checkout.State);
            Assert.False(repository.IsFaulted);
            Assert.Equal(schemaRoot, checkout.AcceptedCatalog!.Snapshot!.Address);
            Assert.NotEmpty(DB082Graph(repository, head).ObjectHeads);

            // The same logical request can be corrected after failed capture/closure preparation.
            CommitId corrected = repository.CommitBranches([checkout.CreateStateCommitRequest(replacement)], [requested])[checkout.BranchId].Id;
            Assert.NotEqual(head, corrected);
            Assert.Same(replacement, checkout.State);

            Assert.Equal(requested, checkout.AcceptedCatalog!.Schemas[new(requested.Type, requested.Version)]);
        }
        using Repository reopened = Repository.OpenReadOnlyExisting(directory.Path, models);
        CommitId latest = reopened.ReadCurrentCommit(Assert.Single(reopened.ListBranches()).Id).Commit.Id;
        IDurableObject restored = reopened.LoadState(latest);
        Assert.Equal(replacement.GetType(), restored.GetType());
        StoredGraphRevision graph = DB082Graph(reopened, latest);
        Assert.Equal(requested, SchemaCatalogView.Load(DB082Resources(reopened).GraphStore, graph.SchemaRoot!.Value)
            .Schemas[new(requested.Type, requested.Version)]);
    }
}
