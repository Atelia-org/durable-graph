using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
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
        using (Repository repository = Repository.CreateNew(directory.Path, models, schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            using BranchCheckout checkout = repository.CreateBranch("main", original);
            CheckpointAddress head = checkout.Head;
            FrameAddress? schemaRoot = repository.SharedSchemaRoot;
            long appended = repository.GraphStore.AppendedFrames;
            checkout.Catalog.Set(conflicting);

            Assert.Throws<SchemaConflictException>(() => checkout.CommitState(replacement));
            Assert.Equal(head, checkout.Head);
            Assert.Equal(head, repository.GetHead("main"));
            Assert.Equal(schemaRoot, repository.SharedSchemaRoot);
            Assert.Equal(appended, repository.GraphStore.AppendedFrames);
            Assert.Same(original, checkout.State);
            Assert.False(repository.IsFaulted);
            Assert.Equal(1, checkout.Catalog.DirtyCount);
            Assert.NotEmpty(repository.GraphStore.ReadGraph(head.RevisionAddress).ObjectHeads);

            // The same logical request can be corrected after failed capture/closure preparation.
            checkout.Catalog.Set(requested);
            CheckpointAddress corrected = checkout.CommitState(replacement);
            Assert.NotEqual(head, corrected);
            Assert.Same(replacement, checkout.State);
            Assert.Equal(0, checkout.Catalog.DirtyCount);
            Assert.Equal(requested, checkout.Catalog.Accepted!.Schemas[new(requested.Type, requested.Version)]);
        }
        using Repository reopened = Repository.OpenReadOnlyExisting(directory.Path, models);
        IDurableObject restored = reopened.ReadState(reopened.GetHead("main"));
        Assert.Equal(replacement.GetType(), restored.GetType());
        StoredGraphRevision graph = reopened.GraphStore.ReadGraph(reopened.GetHead("main").RevisionAddress);
        Assert.Equal(requested, SchemaCatalogView.Load(reopened.GraphStore, graph.SchemaRoot!.Value)
            .Schemas[new(requested.Type, requested.Version)]);
    }
}
