using Atelia.DurableGraph.Storage;
using Atelia.RbfSegmentStore;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;
using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class SharedEventHistoryTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"durable-shared-history-{Guid.NewGuid():N}");
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1000000, 1);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PairedResumeSharesExactStringsButKeepsStateChildrenAndListsIndependent(bool editState) {
        string text = new string('s', 19);
        Node child = new() { Value = 2, Text = text };
        List<Node> links = [child, child];
        Node world = new() { Next = child, Alias = child, Text = text, Links = links };
        Node domainEvent = new() { Next = child, Alias = child, Text = text, Links = links, Value = 7 };
        FrameAddress initial, eventRevision, saved;
        ObjectId stateId;
        using (EventHistoryRepository repository = EventHistoryRepository.CreateNew(_root, SharedReadModel.Models(), new() { NewStoreLayout = RbfSegmentStoreLayout.Flat })) {
            using EventHistorySession<Node> session = repository.CreateBranch("main", world, NoRebase);
            initial = session.StateRevisionAddress;
            stateId = session.StateId;
            eventRevision = session.CommitDomainEvent(domainEvent, NoRebase).RevisionAddress;
        }

        using (EventHistoryRepository repository = EventHistoryRepository.OpenExisting(_root, SharedReadModel.Models())) {
            using EventHistorySession<Node> resumed = repository.Resume<Node>("main");
            Node pending = resumed.GetPendingEvent<Node>();
            Assert.Equal(stateId, resumed.StateId);
            Assert.NotSame(resumed.State, pending);
            Assert.NotSame(resumed.State.Next, pending.Next);
            Assert.NotSame(resumed.State.Links, pending.Links);
            Assert.Same(resumed.State.Next, resumed.State.Alias);
            Assert.Same(resumed.State.Next, resumed.State.Links![0]);
            Assert.Same(pending.Next, pending.Alias);
            Assert.Same(pending.Next, pending.Links![0]);
            Assert.Same(resumed.State.Text, resumed.State.Next!.Text);
            Assert.Same(pending.Text, pending.Next!.Text);
            // This is the operation-local string reuse witness, not a public identity promise.
            Assert.Same(resumed.State.Text, pending.Text);
            if (editState) {
                resumed.State.Next.Value = 9;
                resumed.State.Links.Clear();
            }
            Assert.Equal((byte)2, pending.Next.Value);
            Assert.Equal(2, pending.Links.Count);
            Assert.Same(pending.Links[0], pending.Links[1]);
            saved = resumed.CommitDomainState(NoRebase).RevisionAddress;
            Assert.Null(resumed.PendingEvent);
        }

        using (SegmentStore segments = SegmentStore.OpenExisting(Path.Combine(_root, "state"))) {
            using StateRevisionStore store = new(segments);
            StateRevision next = store.Read(saved);
            Assert.Equal(initial, next.ParentRevisionAddress);
            Assert.Equal(initial, store.Read(eventRevision).ParentRevisionAddress);
            Assert.Equal(store.ReadLiveObjectHeadMap(initial).Keys.Order(), store.ReadLiveObjectHeadMap(saved).Keys.Order());
            Assert.Empty(next.RemovedObjectIds);
            if (editState) {
                // Only the existing child and List changed; string IDs and World reference slots remain stable.
                Assert.Equal(2, next.LocalObjects.Count);
                Assert.DoesNotContain(next.LocalObjects, row => row.ObjectId == stateId.Value);
            } else {
                // Reusing decoded strings must not create a fresh ID or an extra string Base on first save.
                Assert.Empty(next.LocalObjects);
            }
        }

        using EventHistoryRepository reader = EventHistoryRepository.OpenReadOnlyExisting(_root, SharedReadModel.Models());
        Node coldState = reader.ReadState<Node>(reader.GetHead("main"));
        Node coldEvent = reader.ReadEvent<Node>(Assert.Single(reader.ReadEvents("main")));
        Assert.Equal(editState ? (byte)9 : (byte)2, coldState.Next!.Value);
        Assert.Equal(editState ? 0 : 2, coldState.Links!.Count);
        Assert.Equal((byte)2, coldEvent.Next!.Value);
        Assert.Equal(2, coldEvent.Links!.Count);
        Assert.Equal(text, coldState.Text);
    }

    [Fact]
    public void PairedResumeRejectsASingletonAllocatorBeforeItCanHydrateTheSecondMutableGraph() {
        using (EventHistoryRepository repository = EventHistoryRepository.CreateNew(_root, SharedReadModel.Models(), new() { NewStoreLayout = RbfSegmentStoreLayout.Flat })) {
            using EventHistorySession<Node> session = repository.CreateBranch("main", new Node { Value = 1 }, NoRebase);
            session.CommitDomainEvent(new Node { Value = 7 }, NoRebase);
        }

        Node singleton = new();
        List<byte> hydratedValues = [];
        StateModelRegistry broken = SharedReadModel.Models(allocate: () => singleton,
            hydrate: node => hydratedValues.Add(node.Value));
        using EventHistoryRepository reopened = EventHistoryRepository.OpenExisting(_root, broken);
        EventHistorySession<Node>? delivered = null;

        Assert.Throws<InvalidDataException>(() => delivered = reopened.Resume<Node>("main"));

        Assert.Null(delivered);
        Assert.Equal(new byte[] { 1 }, hydratedValues);
        Assert.Equal((byte)1, singleton.Value);
        Assert.False(reopened.IsFaulted);
        reopened.Dispose();
        using EventHistoryRepository healthy = EventHistoryRepository.OpenExisting(_root, SharedReadModel.Models());
        using EventHistorySession<Node> retry = healthy.Resume<Node>("main");
        Assert.Equal((byte)1, retry.State.Value);
        Assert.Equal((byte)7, retry.GetPendingEvent<Node>().Value);
    }

    public void Dispose() => SharedReadModel.DeleteFixture(_root, "durable-shared-history-");
}
