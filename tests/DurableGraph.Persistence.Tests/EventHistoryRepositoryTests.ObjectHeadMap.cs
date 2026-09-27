using Atelia.DurableGraph.Storage;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed partial class EventHistoryRepositoryTests {
    [Fact]
    public void LargeStateShrinkInstallsMapBaseThenHotAndColdSavesPreserveObjectChains() {
        Node world = LargeWorld();
        FrameAddress first, shrunk, hot, cold;
        ObjectId rootId;
        using (EventHistoryRepository repository = CreateRepository()) {
            using var session = repository.CreateBranch("main", world, NoRebase);
            first = session.StateRevisionAddress;
            rootId = session.StateId;
            session.CommitDomainEvent(new Node { Value = 6 }, NoRebase);
            world.Left = null;
            world.Value = 2;
            shrunk = session.CommitDomainState(NoRebase).RevisionAddress;
            Assert.Same(world, session.State);
            Assert.Equal(shrunk, session.StateRevisionAddress);
            Assert.Null(session.PendingEvent);
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
        using (EventHistoryRepository repository = EventHistoryRepository.OpenExisting(_root, Models())) {
            using var session = repository.Resume<Node>("main");
            Assert.Equal(rootId, session.StateId);
            Assert.Null(session.State.Left);
            Assert.Equal((byte)2, session.State.Value);
            Assert.Equal((byte)9, session.State.Right!.Value);
            Assert.Same(session.State.Text, session.State.Right.Text);
            session.State.Value = 3;
            cold = Save(session).RevisionAddress;
        }
        using (EventHistoryRepository repository = EventHistoryRepository.OpenReadOnlyExisting(_root, Models())) {
            GraphFrame head = repository.GetHead("main");
            Assert.Equal(cold, head.RevisionAddress);
            Node restored = repository.ReadState<Node>(head);
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
        using (EventHistoryRepository repository = CreateRepository()) {
            Node original = LargeWorld();
            using var session = repository.CreateBranch("main", original, NoRebase);
            first = session.StateRevisionAddress;
            session.CommitDomainEvent(new Node { Value = 6 }, NoRebase);
            Node replacement = new() { Value = 7, Right = original.Right, Text = original.Text };
            repository.Checkpoint = checkpoint => {
                Assert.Same(original, session.State);
                if (checkpoint == (CommitCheckpoint)point) { throw new IOException("interrupted map Base publication"); }
            };
            GraphCommitException error = Assert.Throws<GraphCommitException>(() => session.CommitDomainState(replacement, NoRebase));
            Assert.Equal(outcome, error.Outcome);
            candidate = Assert.IsType<FrameAddress>(error.CandidateRevisionAddress);
            Assert.True(repository.IsFaulted);
            Assert.Same(original, session.State);
            Assert.Equal(first, session.StateRevisionAddress);
        }
        using (SegmentStore segments = OpenState()) {
            using StateRevisionStore states = new(segments);
            Assert.Equal(ObjectHeadMapKind.Base, states.Read(candidate).ObjectHeadMapKind);
            Assert.Equal(first, states.Read(candidate).ParentRevisionAddress);
            Assert.Equal(3, states.ReadLiveObjectHeadMap(candidate).Count);
            Assert.Equal(1003, states.ReadLiveObjectHeadMap(first).Count);
        }
        using EventHistoryRepository reopened = EventHistoryRepository.OpenExisting(_root, Models());
        using var recovered = reopened.Resume<Node>("main");
        if (outcome == GraphCommitOutcome.Published) {
            Assert.Equal(candidate, recovered.StateRevisionAddress);
            Assert.Equal((byte)7, recovered.State.Value);
            Assert.Null(recovered.State.Left);
            Assert.Null(recovered.PendingEvent);
        } else {
            Assert.Equal(first, recovered.StateRevisionAddress);
            Assert.NotNull(recovered.State.Left);
            Assert.Equal((byte)6, recovered.GetPendingEvent<Node>().Value);
        }
    }

    private static Node LargeWorld() {
        string text = new('s', 20);
        Node world = new() { Text = text, Right = new Node { Value = 4, Text = text } };
        for (int i = 0; i < 1000; i++) { world.Left = new Node { Left = world.Left }; }
        return world;
    }
}
