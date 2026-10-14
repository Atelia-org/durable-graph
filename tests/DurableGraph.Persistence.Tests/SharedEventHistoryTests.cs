using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using SegmentStore = Atelia.FrameStore.FrameStore;
using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class SharedEventHistoryTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"durable-shared-history-{Guid.NewGuid():N}");
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1000000, 1);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitEventReadKeepsCheckoutChildrenAndListsIndependent(bool editState) {
        string text = new string('s', 19);
        Node child = new() { Value = 2, Text = text };
        List<Node> links = [child, child];
        Node world = new() { Next = child, Alias = child, Text = text, Links = links };
        Node domainEvent = new() { Next = child, Alias = child, Text = text, Links = links, Value = 7 };
        FrameAddress initial, eventRevision, saved;
        ObjectId stateId;
        using (Repository repository = Repository.CreateNew(_root, SharedReadModel.Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            using BranchCheckout session = repository.CreateBranch("main", world, NoRebase);
            initial = session.StateRevisionAddress!.Value;
            stateId = session.StateId!.Value;
            eventRevision = session.CommitEvent(domainEvent, NoRebase).RevisionAddress;
        }

        using (Repository repository = Repository.OpenExisting(_root, SharedReadModel.Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            using BranchCheckout resumed = repository.Checkout("main");
            Node pending = ((Node)repository.ReadEvent(resumed.Head));
            Node restoredState = Assert.IsType<Node>(resumed.State);
            Assert.NotNull(restoredState.Next);
            Assert.NotNull(restoredState.Links);
            Assert.Equal(stateId, resumed.StateId!.Value);
            Assert.NotSame(restoredState, pending);
            Assert.NotSame(restoredState.Next, pending.Next);
            Assert.NotSame(restoredState.Links, pending.Links);
            Assert.Same(restoredState.Next, restoredState.Alias);
            Assert.Same(restoredState.Next, restoredState.Links![0]);
            Assert.Same(pending.Next, pending.Alias);
            Assert.Same(pending.Next, pending.Links![0]);
            Assert.Same(restoredState.Text, restoredState.Next!.Text);
            Assert.Same(pending.Text, pending.Next!.Text);
            // Separate read operations preserve values without promising shared string instances.
            Assert.Equal(restoredState.Text, pending.Text);
            if (editState) {
                restoredState.Next.Value = 9;
                restoredState.Links.Clear();
            }
            Assert.Equal((byte)2, pending.Next.Value);
            Assert.Equal(2, pending.Links.Count);
            Assert.Same(pending.Links[0], pending.Links[1]);
            saved = resumed.CommitState(NoRebase).RevisionAddress;
            Assert.Equal(GraphFrameKind.State, resumed.Head.Kind);
        }

        using (GraphResources resources = GraphResources.OpenReadOnlyExisting(_root)) {
            GraphRecordStore store = resources.GraphStore;
            StoredGraphRevision next = store.ReadGraph(saved);
            IReadOnlyList<ObjectVersionRecord> writes = StorageTestData.LocalRecords(store, saved);
            Assert.Equal(initial, next.SourceGraph);
            Assert.Equal(initial, store.ReadGraph(eventRevision).SourceGraph);
            Assert.Equal(store.ReadGraph(initial).ObjectHeads.Keys.Order(), store.ReadGraph(saved).ObjectHeads.Keys.Order());
            if (editState) {
                // Only the existing child and List changed; string IDs and World reference slots remain stable.
                Assert.Equal(2, writes.Count);
                Assert.DoesNotContain(writes, row => row.ObjectId == stateId.Value);
            } else {
                // Reusing decoded strings must not create a fresh ID or an extra string Base on first save.
                Assert.Empty(writes);
            }
        }

        using Repository reader = Repository.OpenReadOnlyExisting(_root, SharedReadModel.Models());
        Node coldState = ((Node)reader.ReadState(reader.GetHead("main")));
        Node coldEvent = ((Node)reader.ReadEvent(Assert.Single(reader.EnumerateEvents(reader.GetHead("main"), HistoryOrder.OldestFirst).ToArray())));
        Assert.Equal(editState ? (byte)9 : (byte)2, coldState.Next!.Value);
        Assert.Equal(editState ? 0 : 2, coldState.Links!.Count);
        Assert.Equal((byte)2, coldEvent.Next!.Value);
        Assert.Equal(2, coldEvent.Links!.Count);
        Assert.Equal(text, coldState.Text);
    }

    [Fact]
    public void CheckoutRejectsASingletonAllocatorBeforeHydratingItsMutableGraph() {
        using (Repository repository = Repository.CreateNew(_root, SharedReadModel.Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            using BranchCheckout session = repository.CreateBranch("main", new Node { Value = 1, Next = new Node { Value = 2 } }, NoRebase);
            session.CommitEvent(new Node { Value = 7 }, NoRebase);
        }

        Node singleton = new();
        List<byte> hydratedValues = [];
        StateModelRegistry broken = SharedReadModel.Models(allocate: () => singleton,
            hydrate: node => hydratedValues.Add(node.Value));
        using Repository reopened = Repository.OpenExisting(_root, broken, schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared);
        BranchCheckout? delivered = null;

        Assert.Throws<InvalidDataException>(() => delivered = reopened.Checkout("main"));

        Assert.Null(delivered);
        Assert.Empty(hydratedValues);
        Assert.Equal((byte)0, singleton.Value);
        Assert.False(reopened.IsFaulted);
        reopened.Dispose();
        using Repository healthy = Repository.OpenExisting(_root, SharedReadModel.Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared);
        using BranchCheckout retry = healthy.Checkout("main");
        Assert.Equal((byte)1, ((Node)retry.State!).Value);
        Assert.Equal((byte)7, ((Node)healthy.ReadEvent(retry.Head)).Value);
    }

    public void Dispose() => SharedReadModel.DeleteFixture(_root, "durable-shared-history-");
}
