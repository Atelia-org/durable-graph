using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed partial class WorldWorkspaceTests {
    [Fact]
    public void DifferentRealCandidateInstallationIsRejectedWithoutConsumingThePendingInstallation() {
        StateModelRegistry registry = Registry(Model());
        StateModelSnapshot models = registry.Snapshot(_schemas);
        World rootA = new() { Value = 17, Text = "from-A" };
        World rootB = new() { Value = 42, Text = "from-B" };
        BusinessWorkspace workspaceA = new(models, rootA);
        BusinessWorkspace workspaceB = new(models, rootB);

        using BusinessSaveCandidate candidateA = workspaceA.Capture(rootA, independentSnapshot: false);
        PreparedObjectRevision writesA = CapturedRevisionPlanner.PrepareRevision(_store, _schemas, null,
            new PreparedCapturedGraph(null, candidateA.Graph, candidateA.PrepareContents()), NoRebase);
        NormalizedRevision nextA = NormalizedRevision.FromCandidate(candidateA.Graph, models, _store, _schemas, null);
        candidateA.CompletePreparation(nextA);
        FrameAddress addressA = _store.Append(writesA.Revision);
        NormalizedRevision installationA = candidateA.NextBaseline.WithAddress(addressA, writesA.Revision);
        Assert.Equal((byte)17, Read(addressA, candidateA.RootId, registry).Value);
        candidateA.PrepareInstallation(installationA);
        candidateA.Install();
        Assert.Same(installationA, workspaceA.Baseline);
        Assert.Same(rootA, workspaceA.Root);

        using BusinessSaveCandidate candidateB = workspaceB.Capture(rootB, independentSnapshot: false);
        PreparedObjectRevision writesB = CapturedRevisionPlanner.PrepareRevision(_store, _schemas, null,
            new PreparedCapturedGraph(null, candidateB.Graph, candidateB.PrepareContents()), NoRebase);
        NormalizedRevision nextB = NormalizedRevision.FromCandidate(candidateB.Graph, models, _store, _schemas, null);
        candidateB.CompletePreparation(nextB);
        FrameAddress addressB = _store.Append(writesB.Revision);
        NormalizedRevision installationB = candidateB.NextBaseline.WithAddress(addressB, writesB.Revision);
        Assert.Equal((byte)42, Read(addressB, candidateB.RootId, registry).Value);
        Assert.NotEqual(addressA, addressB);
        Assert.Equal(candidateA.RootId, candidateB.RootId);
        // Same real backend and frozen environment, even the same numeric object IDs:
        // rejection must distinguish the actual candidates, rather than an owner/layout mismatch.
        Assert.Same(installationA.SourceStore, installationB.SourceStore);
        Assert.Same(installationA.SourceSchemas, installationB.SourceSchemas);
        Assert.Same(installationA.SourceModels, installationB.SourceModels);
        Assert.NotSame(installationA.CurrentDtos, installationB.CurrentDtos);

        Assert.Throws<InvalidDataException>(() => candidateB.PrepareInstallation(installationA));
        Assert.Null(workspaceB.Baseline);
        Assert.True(workspaceB.RootId.IsNull);
        Assert.Same(nextB, candidateB.NextBaseline); // The rejected material did not occupy the installation slot.

        candidateB.PrepareInstallation(installationB);
        candidateB.Install(); // Mechanism witness; publication remains the outer Repository's responsibility.
        Assert.Same(installationB, workspaceB.Baseline);
        Assert.Same(rootB, workspaceB.Root);
        Assert.Equal(candidateB.RootId, workspaceB.RootId);
        Assert.Equal((byte)42, workspaceB.Baseline!.CurrentDtos[workspaceB.RootId].GetState<State>().Value);
        Assert.Equal(addressB, workspaceB.Baseline!.RevisionAddress);
        Assert.Throws<InvalidOperationException>(candidateB.Install);
    }
}
