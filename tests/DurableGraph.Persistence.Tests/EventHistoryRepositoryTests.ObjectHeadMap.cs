using Atelia.DurableGraph.Storage;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed partial class EventHistoryRepositoryTests {
    [Fact]
    public void LargeStateShrinkInstallsMapBaseThenHotAndColdSavesPreserveObjectChains() {
        Node world = LargeWorld();
        FrameAddress first, shrunk, hot, cold;
        ObjectId rootId;
        using (Repository repository = CreateRepository()) {
            using var session = repository.CreateBranch("main", world, NoRebase);
            first = session.StateRevisionAddress!.Value;
            rootId = session.StateId!.Value;
            session.CommitEvent(new Node { Value = 6 }, NoRebase);
            world.Left = null;
            world.Value = 2;
            shrunk = session.CommitState(NoRebase).RevisionAddress;
            Assert.Same(world, ((Node)session.State!));
            Assert.Equal(shrunk, session.StateRevisionAddress!.Value);
            Assert.Equal(GraphFrameKind.State, session.Head.Kind);
            world.Right!.Value = 9;
            hot = Save(session).RevisionAddress;
        }
        using (SegmentStore segments = OpenState()) {
            using StateRevisionStore states = new(segments);
            StateRevision revision = states.Read(shrunk);
            Assert.Equal(ObjectHeadMapKind.Base, revision.ObjectHeadMapKind);
            Assert.Equal(first, revision.ParentRevisionAddress);
            Assert.Empty(revision.RemovedObjectIds);
            ObjectVersionRecord root = Assert.Single(revision.LocalObjects);
            Assert.Equal(rootId.Value, root.ObjectId);
            Assert.Equal(ObjectVersionKind.Delta, root.Kind);
            Assert.Equal(first, root.PriorAddress);
            Assert.Equal(2, revision.ExternalObjectHeads.Count); // Stable child and shared string.
            Assert.Equal(3, states.ReadLiveObjectHeadMap(shrunk).Count);
            Assert.Equal(1003, states.ReadLiveObjectHeadMap(first).Count);
            Assert.Equal(ObjectHeadMapKind.Delta, states.Read(hot).ObjectHeadMapKind);
            Assert.Equal(shrunk, states.Read(hot).ParentRevisionAddress);
        }
        using (Repository repository = Repository.OpenExisting(_root, Models())) {
            using var session = repository.Checkout("main");
            Assert.Equal(rootId, session.StateId!.Value);
            Assert.Null(((Node)session.State!).Left);
            Assert.Equal((byte)2, ((Node)session.State!).Value);
            Assert.Equal((byte)9, ((Node)session.State!).Right!.Value);
            Assert.Same(((Node)session.State!).Text, ((Node)session.State!).Right!.Text);
            ((Node)session.State!).Value = 3;
            cold = Save(session).RevisionAddress;
        }
        using (Repository repository = Repository.OpenReadOnlyExisting(_root, Models())) {
            CheckpointAddress head = repository.GetHead("main");
            Assert.Equal(cold, head.RevisionAddress);
            Node restored = ((Node)repository.ReadState(head));
            Assert.Equal((byte)3, restored.Value);
            Assert.Equal((byte)9, restored.Right!.Value);
            Assert.Null(restored.Left);
            Assert.Same(restored.Text, restored.Right.Text);
        }
    }

    [Theory]
    [InlineData((int)CommitCheckpoint.BeforePublication, GraphCommitOutcome.NotPublished)]
    [InlineData((int)CommitCheckpoint.AfterPublication, GraphCommitOutcome.Published)]
    [InlineData((int)CommitCheckpoint.BeforeInstall, GraphCommitOutcome.Published)]
    public void MapBaseRootReplacementKeepsPublicationAndInstallationFailureBoundaries(int point, GraphCommitOutcome outcome) {
        FrameAddress first, candidate;
        using (Repository repository = CreateRepository()) {
            Node original = LargeWorld();
            using var session = repository.CreateBranch("main", original, NoRebase);
            first = session.StateRevisionAddress!.Value;
            session.CommitEvent(new Node { Value = 6 }, NoRebase);
            Node replacement = new() { Value = 7, Right = original.Right, Text = original.Text };
            repository.Checkpoint = checkpoint => {
                Assert.Same(original, ((Node)session.State!));
                if (checkpoint == (CommitCheckpoint)point) { throw new IOException("interrupted map Base publication"); }
            };
            GraphCommitException error = Assert.Throws<GraphCommitException>(() => session.CommitState(replacement, NoRebase));
            Assert.Equal(outcome, error.Outcome);
            candidate = Assert.IsType<FrameAddress>(error.CandidateRevisionAddress);
            Assert.True(repository.IsFaulted);
            Assert.Same(original, ((Node)session.State!));
            Assert.Equal(first, session.StateRevisionAddress!.Value);
        }
        using (SegmentStore segments = OpenState()) {
            using StateRevisionStore states = new(segments);
            Assert.Equal(ObjectHeadMapKind.Base, states.Read(candidate).ObjectHeadMapKind);
            Assert.Equal(first, states.Read(candidate).ParentRevisionAddress);
            Assert.Equal(3, states.ReadLiveObjectHeadMap(candidate).Count);
            Assert.Equal(1003, states.ReadLiveObjectHeadMap(first).Count);
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        using var recovered = reopened.Checkout("main");
        if (outcome == GraphCommitOutcome.Published) {
            Assert.Equal(candidate, recovered.StateRevisionAddress!.Value);
            Assert.Equal((byte)7, ((Node)recovered.State!).Value);
            Assert.Null(((Node)recovered.State!).Left);
            Assert.Equal(GraphFrameKind.State, recovered.Head.Kind);
        } else {
            Assert.Equal(first, recovered.StateRevisionAddress!.Value);
            Assert.NotNull(((Node)recovered.State!).Left);
            Assert.Equal((byte)6, ((Node)reopened.ReadEvent(recovered.Head)).Value);
        }
    }

    private static Node LargeWorld() {
        string text = new('s', 20);
        Node world = new() { Text = text, Right = new Node { Value = 4, Text = text } };
        for (int i = 0; i < 1000; i++) { world.Left = new Node { Left = world.Left }; }
        return world;
    }
}
