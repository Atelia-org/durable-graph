using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;

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
        using (Repository repository = Repository.CreateNew(_root, SharedReadModel.Models())) {
            using WorkingCopy session = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", world, parameters: NoRebase).Id);
            initial = session.StateRevisionAddress!.Value;
            stateId = session.StateId!.Value;
            eventRevision = WorkTestAccess.Address(session, session.CommitEvent(domainEvent).Id);
        }

        using (Repository repository = Repository.OpenExisting(_root, SharedReadModel.Models())) {
            using WorkingCopy resumed = repository.OpenWorkingCopy(BranchNamed(repository, "main"));
            Node pending = ((Node)repository.LoadEvent(resumed.BaseCommitId));
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
            saved = WorkTestAccess.Address(resumed, resumed.CommitState(parameters: NoRebase).Id);
            Assert.Equal(CommitKind.State, WorkTestAccess.Kind(resumed));
        }

        using (RepositoryResources resources = RepositoryResources.OpenReadOnlyExisting(_root)) {
            GraphRecordStore store = resources.GraphStore;
            StoredGraphRevision next = MechanismFixture.Snapshot(store, saved);
            IReadOnlyList<ObjectVersionRecord> writes = StorageTestData.LocalRecords(store, saved);
            Assert.Equal(initial, next.SourceGraph);
            Assert.Null(MechanismFixture.Snapshot(store, eventRevision).SourceGraph);
            Assert.Equal(MechanismFixture.Snapshot(store, initial).ObjectHeads.Keys.Order(), MechanismFixture.Snapshot(store, saved).ObjectHeads.Keys.Order());
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
        Node coldState = ((Node)reader.LoadState(reader.ReadCurrentCommit(BranchNamed(reader, "main")).Commit.Id));
        Node coldEvent = ((Node)reader.LoadEvent(Assert.Single(reader.EnumerateEvents(reader.ReadCurrentCommit(BranchNamed(reader, "main")).Commit.Id, HistoryOrder.OldestFirst).ToArray()).Id));
        Assert.Equal(editState ? (byte)9 : (byte)2, coldState.Next!.Value);
        Assert.Equal(editState ? 0 : 2, coldState.Links!.Count);
        Assert.Equal((byte)2, coldEvent.Next!.Value);
        Assert.Equal(2, coldEvent.Links!.Count);
        Assert.Equal(text, coldState.Text);
    }

    [Fact]
    public void CheckoutRejectsASingletonAllocatorBeforeHydratingItsMutableGraph() {
        using (Repository repository = Repository.CreateNew(_root, SharedReadModel.Models())) {
            using WorkingCopy session = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", new Node { Value = 1, Next = new Node { Value = 2 } }, parameters: NoRebase).Id);
            session.CommitEvent(new Node { Value = 7 });
        }

        Node singleton = new();
        List<byte> hydratedValues = [];
        StateModelRegistry broken = SharedReadModel.Models(allocate: () => singleton,
            hydrate: node => hydratedValues.Add(node.Value));
        using Repository reopened = Repository.OpenExisting(_root, broken);
        WorkingCopy? delivered = null;

        Assert.Throws<InvalidDataException>(() => delivered = reopened.OpenWorkingCopy(BranchNamed(reopened, "main")));

        Assert.Null(delivered);
        Assert.Empty(hydratedValues);
        Assert.Equal((byte)0, singleton.Value);
        Assert.False(reopened.IsFaulted);
        reopened.Dispose();
        using Repository healthy = Repository.OpenExisting(_root, SharedReadModel.Models());
        using WorkingCopy retry = healthy.OpenWorkingCopy(BranchNamed(healthy, "main"));
        Assert.Equal((byte)1, ((Node)retry.State!).Value);
        Assert.Equal((byte)7, ((Node)healthy.LoadEvent(retry.BaseCommitId)).Value);
    }

    public void Dispose() => SharedReadModel.DeleteFixture(_root, "durable-shared-history-");
}
