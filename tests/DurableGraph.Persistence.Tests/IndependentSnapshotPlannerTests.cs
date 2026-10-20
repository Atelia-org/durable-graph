
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

// Independent Event content owns complete Base records while publication retains State.
public sealed class IndependentSnapshotPlannerTests : IDisposable {
    private const string Prefix = "durable-graph-snapshot-planner-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Events_keep_own_membership_and_complete_content_without_installing_State() {
        FrameAddress stateAddress, lastEventAddress;
        using (RepositoryStorageFacade repository = RepositoryStorageFacade.CreateNew(_path)) {
            StoredGraphRevision first = Save(repository, StoredGraphKind.State, null,
                [B(1, new byte[100]), B(2, [2]), B(3, [3])]);
            StoredGraphRevision state = Save(repository, StoredGraphKind.State, first.Address,
                [D(1, first.ObjectHeads[1], [4])]);
            int stateInstalls = 0;
            using PendingRepositorySave pending = MechanismFixture.Prepare(repository, "main", StoredGraphKind.Event,
                new(null, new(1), [B(1, [5]), B(2, [2]), B(4, [4])]));
            StoredGraphRevision firstEvent = repository.Commit(pending, installState: _ => stateInstalls++).Business;
            Assert.Equal(0, stateInstalls);
            Assert.Equal(state.Address, MechanismFixture.State(repository, "main")?.Address);
            Assert.Equal(new uint[] { 1, 2, 4 }, firstEvent.ObjectHeads.Keys.Order());
            AssertIndependent(repository, firstEvent);
            Assert.NotEqual(first.ObjectHeads[2], firstEvent.ObjectHeads[2]);
            Assert.Equal(new uint[] { 1, 2, 3 }, MechanismFixture.Snapshot(repository, state.Address).ObjectHeads.Keys.Order());

            // Consecutive Events neither inherit omitted members nor depend on the earlier Event.
            StoredGraphRevision lastEvent = Save(repository, StoredGraphKind.Event, null, [B(1, [6]), B(2, [2])]);
            Assert.Equal(firstEvent.Address, MechanismFixture.Resources(repository).ReadPrevious(MechanismFixture.Current(repository, "main").Id)?.Address);
            AssertIndependent(repository, lastEvent);
            Assert.Equal(new uint[] { 1, 2 }, lastEvent.ObjectHeads.Keys.Order());
            Assert.NotEqual(firstEvent.ObjectHeads[2], lastEvent.ObjectHeads[2]);
            Assert.Equal(state.Address, MechanismFixture.State(repository, "main")?.Address);
            stateAddress = state.Address;
            lastEventAddress = lastEvent.Address;
        }

        using RepositoryStorageFacade reopened = RepositoryStorageFacade.OpenExisting(_path);
        Assert.Equal(stateAddress, MechanismFixture.State(reopened, "main")?.Address);
        Assert.Equal(lastEventAddress, MechanismFixture.FindCurrent(reopened, "main")?.Address);
        Assert.Equal(new uint[] { 1, 2 }, MechanismFixture.Snapshot(reopened, lastEventAddress).ObjectHeads.Keys.Order());
        Assert.Equal(new uint[] { 1, 2, 3 }, MechanismFixture.Snapshot(reopened, stateAddress).ObjectHeads.Keys.Order());
        AssertIndependent(reopened, MechanismFixture.Snapshot(reopened, lastEventAddress));
    }

    [Fact]
    public void Event_identical_content_still_owns_every_Base_and_does_not_rebase_State() {
        using RepositoryStorageFacade repository = RepositoryStorageFacade.CreateNew(_path);
        StoredGraphRevision first = Save(repository, StoredGraphKind.State, null,
            [B(1, new byte[20]), B(2, new byte[20]), B(3, new byte[1000])]);
        StoredGraphRevision state = Save(repository, StoredGraphKind.State, first.Address,
            [D(1, first.ObjectHeads[1], new byte[30]), D(2, first.ObjectHeads[2], new byte[30])]);
        StoredGraphRevision saved = Save(repository, StoredGraphKind.Event, null,
            [B(1, new byte[20]), B(2, new byte[20])]);
        Assert.Equal(new uint[] { 1, 2 }, saved.ObjectHeads.Keys.Order());
        AssertIndependent(repository, saved);
        Assert.NotEqual(state.ObjectHeads[2], saved.ObjectHeads[2]);
        Assert.Equal(state.Address, MechanismFixture.State(repository, "main")?.Address);
        Assert.Equal(state.ObjectHeads[1], MechanismFixture.Snapshot(repository, state.Address).ObjectHeads[1]);
        Assert.Equal(2, repository.Store.ReadObjectChain(MechanismFixture.Snapshot(repository, state.Address), 1).Entries.Count);
    }

    private static void AssertIndependent(RepositoryStorageFacade repository, StoredGraphRevision graph) {
        Assert.Null(graph.SourceGraph);
        Assert.NotNull(graph.OwnedBatch);
        Assert.All(graph.ObjectHeads.Values, head => Assert.Equal(graph.OwnedBatch!.Value, head));
        foreach (uint id in graph.ObjectHeads.Keys) {
            var entry = Assert.Single(repository.Store.ReadObjectChain(graph, id).Entries);
            Assert.Equal(ObjectVersionKind.Base, entry.Record.Kind);
            Assert.Null(entry.Record.PriorAddress);
        }
    }

    [Fact]
    public void Events_before_any_State_have_independent_candidates_and_no_State_baseline() {
        FrameAddress head;
        using (RepositoryStorageFacade repository = RepositoryStorageFacade.CreateNew(_path)) {
            StoredGraphRevision first = Save(repository, StoredGraphKind.Event, null, [B(1, [1])]);
            using PendingRepositorySave pending = MechanismFixture.Prepare(repository, "main", StoredGraphKind.Event,
                new(null, new(2), [B(2, [2])]));
            StoredGraphRevision second = repository.Commit(pending).Business;
            Assert.Equal(first.Address, MechanismFixture.Resources(repository).ReadPrevious(MechanismFixture.Current(repository, "main").Id)?.Address);
            Assert.Null(second.SourceGraph);
            Assert.Null(MechanismFixture.State(repository, "main")?.Address);
            Assert.Equal(new uint[] { 2 }, second.ObjectHeads.Keys);
            Assert.Equal(new uint[] { 1 }, MechanismFixture.Snapshot(repository, first.Address).ObjectHeads.Keys);
            head = second.Address;
        }
        using RepositoryStorageFacade reopened = RepositoryStorageFacade.OpenExisting(_path);
        Assert.Null(MechanismFixture.State(reopened, "main")?.Address);
        Assert.Equal(head, MechanismFixture.FindCurrent(reopened, "main")?.Address);
        Assert.Equal(new uint[] { 2 }, MechanismFixture.Snapshot(reopened, head).ObjectHeads.Keys);
    }

    private static StoredGraphRevision Save(RepositoryStorageFacade repository, StoredGraphKind kind,
        FrameAddress? source, ObjectVersionRecord[] records, uint[]? removed = null) {
        using PendingRepositorySave pending = MechanismFixture.Prepare(repository, "main", kind, new(source, new(1), records, removed));
        return repository.Commit(pending).Business;
    }

    private static ObjectVersionRecord B(uint id, byte[] body) => ObjectVersionRecord.CreateBase(id, body);
    private static ObjectVersionRecord D(uint id, FrameAddress prior, byte[] body) => ObjectVersionRecord.CreateDelta(id, prior, body);
    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
