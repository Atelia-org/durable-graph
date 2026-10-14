using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed partial class SplitBusinessWorkspaceTests {
    [Fact]
    public void DifferentRealCandidateInstallationIsRejectedWithTheSameStoreModelsAndNumericIds() {
        StateModelRegistry registry = Registry(Model());
        ModelExecutionEnvironment models = registry.CreateEnvironment();
        World rootA = new() { Value = 17, Text = "from-A" };
        World rootB = new() { Value = 42, Text = "from-B" };
        BusinessPersistenceAdapter workspaceA = new(Store, models, null);
        BusinessPersistenceAdapter workspaceB = new(Store, models, null);
        SchemaCatalogWorkspace catalogOwner = new(_repository.EnsureEditingAllowed);
        catalogOwner.Set(Current);
        PreparedSchemaCatalogWrite metadata = catalogOwner.Prepare();
        StoredGraphRevision schema = Store.Append(StoredGraphKind.Meta, null, null, metadata.Write!);
        SchemaCatalogView catalog = SchemaCatalogView.Load(Store, schema.Address);

        using BusinessSaveCandidate candidateA = workspaceA.CaptureOwned(rootA, StoredGraphKind.State);
        using PreparedBusinessWrite preparedA = workspaceA.PrepareOwned(candidateA, catalog, NoRebase);
        StoredGraphRevision graphA = Store.Append(StoredGraphKind.State, null, schema.Address, preparedA.Write);
        Assert.Equal((byte)17, Read(graphA.Address, candidateA.RootId, registry).Value);
        preparedA.PrepareInstall(graphA);
        preparedA.Install(graphA);
        NormalizedGraphState installationA = workspaceA.Baseline!;
        Assert.Same(rootA, workspaceA.State);

        using BusinessSaveCandidate candidateB = workspaceB.CaptureOwned(rootB, StoredGraphKind.State);
        using PreparedBusinessWrite preparedB = workspaceB.PrepareOwned(candidateB, catalog, NoRebase);
        StoredGraphRevision graphB = Store.Append(StoredGraphKind.State, null, schema.Address, preparedB.Write);
        NormalizedGraphState nextB = candidateB.NextBaseline;
        NormalizedGraphState installationB = nextB.PrepareGraphInstallation(Store, graphB, preparedB.Write.Records, catalog);
        Assert.Equal((byte)42, Read(graphB.Address, candidateB.RootId, registry).Value);
        Assert.NotEqual(graphA.Address, graphB.Address);
        Assert.Equal(candidateA.RootId, candidateB.RootId);
        Assert.Equal(installationA.Objects.Keys.Order(), installationB.Objects.Keys.Order());
        Assert.Same(installationA.GraphStore, installationB.GraphStore);
        Assert.Same(installationA.SourceModels, installationB.SourceModels);
        Assert.Same(installationA.SelectedSchemaView, installationB.SelectedSchemaView);
        Assert.NotSame(installationA.CurrentDtos, installationB.CurrentDtos);
        foreach (ObjectId id in installationA.Objects.Keys) {
            Assert.Equal(installationA.Objects[id].Current.Layout, installationB.Objects[id].Current.Layout);
        }
        // Both completions came from actual frames in the same owner/environment. The
        // rejection distinguishes the private candidate, rather than lifetime or shape.
        Assert.Throws<InvalidDataException>(() => candidateB.PrepareInstallation(installationA));
        Assert.Null(workspaceB.Baseline);
        Assert.True(workspaceB.RootId.IsNull);
        Assert.Same(nextB, candidateB.NextBaseline);

        preparedB.PrepareInstall(graphB);
        preparedB.Install(graphB); // Common-core mechanism witness; publication is the outer owner's responsibility.
        Assert.Same(rootB, workspaceB.State);
        Assert.Equal(candidateB.RootId, workspaceB.RootId);
        Assert.Equal((byte)42, workspaceB.Baseline!.CurrentDtos[workspaceB.RootId].GetState<State>().Value);
        Assert.Equal(graphB.Address, workspaceB.Baseline.RevisionAddress);
        Assert.Throws<InvalidOperationException>(() => preparedB.Install(graphB));
    }
}
