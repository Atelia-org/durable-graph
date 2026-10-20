using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;

using Atelia.DurableGraph.Storage;
using SegmentStore = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed partial class EventHistoryRepositoryTests {
    [Fact]
    public void LargeStateShrinkInstallsDirectoryBaseThenHotAndColdSavesPreserveObjectChains() {
        Node world = LargeWorld();
        FrameAddress first, shrunk, hot, cold;
        ObjectId rootId;
        using (Repository repository = CreateRepository()) {
            using var session = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", world, parameters: NoRebase).Id);
            world = (Node)session.State!;
            first = session.StateRevisionAddress!.Value;
            rootId = session.StateId!.Value;
            session.CommitEvent(new Node { Value = 6 });
            world.Left = null;
            world.Value = 2;
            shrunk = WorkTestAccess.Address(session, session.CommitState(parameters: NoRebase).Id);
            Assert.Same(world, ((Node)session.State!));
            Assert.Equal(shrunk, session.StateRevisionAddress!.Value);
            Assert.Equal(CommitKind.State, WorkTestAccess.Kind(session));
            world.Right!.Value = 9;
            hot = Describe(repository, Save(session)).Address;
        }
        using (RepositoryResources resources = RepositoryResources.OpenReadOnlyExisting(_root)) {
            GraphRecordStore states = resources.GraphStore;
            StoredGraphRevision revision = MechanismFixture.Snapshot(states, shrunk);
            Assert.Equal(ObjectVersionKind.Base, states.ReadDirectoryVersion(revision.DirectoryRoot).Kind);
            Assert.Equal(first, revision.SourceGraph);
            ObjectVersionRecord root = Assert.Single(StorageTestData.LocalRecords(states, shrunk));
            Assert.Equal(rootId.Value, root.ObjectId);
            Assert.Equal(ObjectVersionKind.Delta, root.Kind);
            Assert.Equal(MechanismFixture.Snapshot(states, first).ObjectHeads[rootId.Value], root.PriorAddress);
            Assert.Equal(8, revision.ObjectHeads.Count - StorageTestData.LocalRecords(states, shrunk).Count); // Seven retained children and their shared string.
            Assert.Equal(9, MechanismFixture.Snapshot(states, shrunk).ObjectHeads.Count);
            Assert.Equal(1009, MechanismFixture.Snapshot(states, first).ObjectHeads.Count);
            Assert.Equal(ObjectVersionKind.Delta, states.ReadDirectoryVersion(MechanismFixture.Snapshot(states, hot).DirectoryRoot).Kind);
            Assert.Equal(shrunk, MechanismFixture.Snapshot(states, hot).SourceGraph);
        }
        using (Repository repository = Repository.OpenExisting(_root, Models())) {
            using var session = repository.OpenWorkingCopy(BranchNamed(repository, "main"));
            Assert.Equal(rootId, session.StateId!.Value);
            Assert.Null(((Node)session.State!).Left);
            Assert.Equal((byte)2, ((Node)session.State!).Value);
            Assert.Equal((byte)9, ((Node)session.State!).Right!.Value);
            Assert.Same(((Node)session.State!).Text, ((Node)session.State!).Right!.Text);
            ((Node)session.State!).Value = 3;
            cold = Describe(repository, Save(session)).Address;
        }
        using (Repository repository = Repository.OpenReadOnlyExisting(_root, Models())) {
            CommitId head = repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id;
            Assert.Equal(cold, Describe(repository, head).Address);
            Node restored = ((Node)repository.LoadState(head));
            Assert.Equal((byte)3, restored.Value);
            Assert.Equal((byte)9, restored.Right!.Value);
            Assert.Null(restored.Left);
            Assert.Same(restored.Text, restored.Right.Text);
        }
    }

    [Theory]
    [InlineData((int)RepositorySaveStage.BeforePublication, RepositoryPublicationOutcome.NotPublished)]
    [InlineData((int)RepositorySaveStage.AfterPublication, RepositoryPublicationOutcome.Published)]
    [InlineData((int)RepositorySaveStage.BeforeInstall, RepositoryPublicationOutcome.Published)]
    public void MapBaseRootReplacementKeepsPublicationAndInstallationFailureBoundaries(int point, RepositoryPublicationOutcome outcome) {
        FrameAddress first, candidate;
        using (Repository repository = CreateRepository()) {
            Node original = LargeWorld();
            using var session = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", original, parameters: NoRebase).Id);
            original = (Node)session.State!;
            first = session.StateRevisionAddress!.Value;
            session.CommitEvent(new Node { Value = 6 });
            Node replacement = new() { Value = 7, Right = original.Right, Text = original.Text };
            repository.SaveStageObserver = checkpoint => {
                Assert.Same(original, ((Node)session.State!));
                if (checkpoint == (RepositorySaveStage)point) { throw new IOException("interrupted map Base publication"); }
            };
            RepositoryPublicationException error = Assert.Throws<RepositoryPublicationException>(() => session.CommitState(replacement, parameters: NoRebase).Id);
            Assert.Equal(outcome, error.Outcome);
            candidate = Assert.IsType<FrameAddress>(error.CandidateRevisionAddress);
            Assert.True(repository.IsFaulted);
            Assert.Same(original, ((Node)session.State!));
            Assert.Equal(first, session.StateRevisionAddress!.Value);
        }
        using (RepositoryResources resources = RepositoryResources.OpenReadOnlyExisting(_root)) {
            GraphRecordStore states = resources.GraphStore;
            Assert.Equal(ObjectVersionKind.Base, states.ReadDirectoryVersion(states.ReadBusinessGraph(candidate, first).DirectoryRoot).Kind);
            Assert.Equal(first, states.ReadBusinessGraph(candidate, first).SourceGraph);
            Assert.Equal(9, states.ReadBusinessGraph(candidate, first).ObjectHeads.Count);
            Assert.Equal(1009, MechanismFixture.Snapshot(states, first).ObjectHeads.Count);
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        using var recovered = reopened.OpenWorkingCopy(BranchNamed(reopened, "main"));
        if (outcome == RepositoryPublicationOutcome.Published) {
            Assert.Equal(candidate, recovered.StateRevisionAddress!.Value);
            Assert.Equal((byte)7, ((Node)recovered.State!).Value);
            Assert.Null(((Node)recovered.State!).Left);
            Assert.Equal(CommitKind.State, WorkTestAccess.Kind(recovered));
        } else {
            Assert.Equal(first, recovered.StateRevisionAddress!.Value);
            Assert.NotNull(((Node)recovered.State!).Left);
            Assert.Equal((byte)6, ((Node)reopened.LoadEvent(recovered.BaseCommitId)).Value);
        }
    }

    private static Node LargeWorld() {
        string text = new('s', 20);
        Node world = new() { Text = text, Right = new Node { Value = 4, Text = text } };
        // Enough retained entries make a one-head edit measurably smaller than a directory Base.
        Node tail = world.Right!;
        for (int i = 0; i < 6; i++) { tail.Right = new Node { Text = text }; tail = tail.Right; }
        for (int i = 0; i < 1000; i++) { world.Left = new Node { Left = world.Left }; }
        return world;
    }
}
