using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

// Event content is a complete candidate relative to the nearest State. Directory
// encoding is selected by the shared employee saver; Event publication keeps State.
public sealed class IndependentSnapshotPlannerTests : IDisposable {
    private const string Prefix = "durable-graph-snapshot-planner-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Events_keep_candidate_membership_and_exact_State_priors_without_installing_State() {
        FrameAddress stateAddress, lastEventAddress;
        using (RepositoryStorageFacade repository = RepositoryStorageFacade.CreateNew(_path)) {
            StoredGraphRevision first = Save(repository, StoredGraphKind.State, null,
                [B(1, new byte[100]), B(2, [2]), B(3, [3])]);
            StoredGraphRevision state = Save(repository, StoredGraphKind.State, first.Address,
                [D(1, first.ObjectHeads[1], [4])]);
            Dictionary<ObjectId, long> priorH = new() {
                [new(1)] = repository.Store.ReadObjectChain(state.Address, 1).ReconstructionPayloadBytes,
                [new(2)] = repository.Store.ReadObjectChain(state.Address, 2).ReconstructionPayloadBytes,
            };
            var candidate = ObjectWritePlanner.PlanObjectWrites([
                PreparedObject.Compared(new(1), state.ObjectHeads[1], new(new byte[100]), new(true, [5])),
                PreparedObject.Unchanged(new(2), state.ObjectHeads[2], new([2])),
                PreparedObject.New(new(4), new([4])),
            ], priorH, new(10, 25));
            int stateInstalls = 0;
            using PendingRepositorySave pending = repository.Prepare("main", StoredGraphKind.Event,
                new(state.Address, new(1), candidate.Records, [3]));
            StoredGraphRevision firstEvent = repository.Commit(pending, installState: _ => stateInstalls++).Business;
            Assert.Equal(0, stateInstalls);
            Assert.Equal(state.Address, repository.GetStateHead("main"));
            Assert.Equal(new uint[] { 1, 2, 4 }, firstEvent.ObjectHeads.Keys.Order());
            Assert.Equal(state.Address, firstEvent.SourceGraph);
            Assert.Equal(first.ObjectHeads[2], firstEvent.ObjectHeads[2]);
            Assert.Equal(state.ObjectHeads[1], repository.Store.ReadObjectChain(firstEvent.Address, 1).Entries[^1].Record.PriorAddress);
            Assert.Equal(new uint[] { 1, 2, 3 }, repository.Store.ReadGraph(state.Address).ObjectHeads.Keys.Order());

            // The second candidate omits the first Event's new object. It still
            // compares against State, including its old unchanged object heads.
            var nextCandidate = ObjectWritePlanner.PlanObjectWrites([
                PreparedObject.Compared(new(1), state.ObjectHeads[1], new(new byte[100]), new(true, [6])),
                PreparedObject.Unchanged(new(2), state.ObjectHeads[2], new([2])),
            ], priorH, new(10, 25));
            StoredGraphRevision lastEvent = Save(repository, StoredGraphKind.Event, state.Address, nextCandidate.Records, [3]);
            Assert.Equal(firstEvent.Address, lastEvent.Parent);
            Assert.Equal(state.Address, lastEvent.SourceGraph);
            Assert.Equal(new uint[] { 1, 2 }, lastEvent.ObjectHeads.Keys.Order());
            Assert.Equal(new[] { first.OwnedBatch!.Value, state.OwnedBatch!.Value, lastEvent.OwnedBatch!.Value },
                repository.Store.ReadObjectChain(lastEvent.Address, 1).Entries.Select(static row => row.ContainingBatch));
            Assert.Equal(state.Address, repository.GetStateHead("main"));
            Assert.Equal(priorH[new(1)], nextCandidate.Estimates[0].ReconstructionPayloadBytes);
            stateAddress = state.Address;
            lastEventAddress = lastEvent.Address;
        }

        using RepositoryStorageFacade reopened = RepositoryStorageFacade.OpenExisting(_path);
        Assert.Equal(stateAddress, reopened.GetStateHead("main"));
        Assert.Equal(lastEventAddress, reopened.GetHead("main"));
        Assert.Equal(new uint[] { 1, 2 }, reopened.Store.ReadGraph(lastEventAddress).ObjectHeads.Keys.Order());
        Assert.Equal(new uint[] { 1, 2, 3 }, reopened.Store.ReadGraph(stateAddress).ObjectHeads.Keys.Order());
    }

    [Fact]
    public void Event_optional_NoChange_Base_is_local_and_other_candidate_heads_are_reused() {
        using RepositoryStorageFacade repository = RepositoryStorageFacade.CreateNew(_path);
        StoredGraphRevision first = Save(repository, StoredGraphKind.State, null,
            [B(1, new byte[20]), B(2, new byte[20]), B(3, new byte[1000])]);
        StoredGraphRevision state = Save(repository, StoredGraphKind.State, first.Address,
            [D(1, first.ObjectHeads[1], new byte[30]), D(2, first.ObjectHeads[2], new byte[30])]);
        var candidate = ObjectWritePlanner.PlanObjectWrites([
            PreparedObject.Unchanged(new(1), state.ObjectHeads[1], new(new byte[20])),
            PreparedObject.Unchanged(new(2), state.ObjectHeads[2], new(new byte[20])),
        ], new Dictionary<ObjectId, long> {
            [new(1)] = repository.Store.ReadObjectChain(state.Address, 1).ReconstructionPayloadBytes,
            [new(2)] = repository.Store.ReadObjectChain(state.Address, 2).ReconstructionPayloadBytes,
        }, new(1, 1));
        Assert.All(candidate.Estimates, static row => Assert.Equal(ObjectSaveChangeKind.NoChange, row.ChangeKind));
        Assert.Equal(44L, candidate.Estimates.Sum(static row => row.BasePayloadBytes));
        ObjectVersionRecord rebased = Assert.Single(candidate.Records);
        Assert.Equal(1U, rebased.ObjectId);
        Assert.Equal(ObjectVersionKind.Base, rebased.Kind);

        StoredGraphRevision saved = Save(repository, StoredGraphKind.Event, state.Address, candidate.Records, [3]);
        Assert.Equal(new uint[] { 1, 2 }, saved.ObjectHeads.Keys.Order());
        Assert.Equal(saved.OwnedBatch, saved.ObjectHeads[1]);
        Assert.Equal(state.ObjectHeads[2], saved.ObjectHeads[2]);
        Assert.Single(repository.Store.ReadObjectChain(saved.Address, 1).Entries);
        Assert.Equal(2, repository.Store.ReadObjectChain(saved.Address, 2).Entries.Count);
        Assert.Equal(state.Address, repository.GetStateHead("main"));
        Assert.Equal(state.ObjectHeads[1], repository.Store.ReadGraph(state.Address).ObjectHeads[1]);
    }

    [Fact]
    public void Events_before_any_State_have_independent_candidates_and_no_State_baseline() {
        FrameAddress head;
        using (RepositoryStorageFacade repository = RepositoryStorageFacade.CreateNew(_path)) {
            StoredGraphRevision first = Save(repository, StoredGraphKind.Event, null, [B(1, [1])]);
            using PendingRepositorySave pending = repository.Prepare("main", StoredGraphKind.Event,
                new(null, new(2), [B(2, [2])]));
            StoredGraphRevision second = repository.Commit(pending).Business;
            Assert.Equal(first.Address, second.Parent);
            Assert.Null(second.SourceGraph);
            Assert.Null(repository.GetStateHead("main"));
            Assert.Equal(new uint[] { 2 }, second.ObjectHeads.Keys);
            Assert.Equal(new uint[] { 1 }, repository.Store.ReadGraph(first.Address).ObjectHeads.Keys);
            head = second.Address;
        }
        using RepositoryStorageFacade reopened = RepositoryStorageFacade.OpenExisting(_path);
        Assert.Null(reopened.GetStateHead("main"));
        Assert.Equal(head, reopened.GetHead("main"));
        Assert.Equal(new uint[] { 2 }, reopened.Store.ReadGraph(head).ObjectHeads.Keys);
    }

    private static StoredGraphRevision Save(RepositoryStorageFacade repository, StoredGraphKind kind,
        FrameAddress? source, ObjectVersionRecord[] records, uint[]? removed = null) {
        using PendingRepositorySave pending = repository.Prepare("main", kind, new(source, new(1), records, removed));
        return repository.Commit(pending).Business;
    }

    private static ObjectVersionRecord B(uint id, byte[] body) => ObjectVersionRecord.CreateBase(id, body);
    private static ObjectVersionRecord D(uint id, FrameAddress prior, byte[] body) => ObjectVersionRecord.CreateDelta(id, prior, body);
    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
