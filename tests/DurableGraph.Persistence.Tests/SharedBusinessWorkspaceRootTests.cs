using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;

using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

// A second actual root type, compiled by the real generator alongside DB087Widget.
[DurableType("P0Envelope", 1)]
internal sealed partial class DB087EnvelopeRoot : IDurableObject {
    [DurableField(1)] internal DB087Widget? Link = null;
    [DurableField(2)] internal List<int> Numbers = [];
}

public sealed class SharedBusinessWorkspaceRootTests : IDisposable {
    private const string Prefix = "dg-shared-workspace-root-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Fact]
    public void RepositoryPreservesActualRootIdentityAcrossPromotionEventDiscardAndColdDelta() {
        Saved initial, promoted, eventOnly, envelopeState, continued, trimmed, unchanged;
        ObjectId originalId, childId, listId, envelopeId;
        List<int> numbers = Enumerable.Repeat(0, 128).ToList(); // A sparse update must be an actual default-budget Delta.
        DB087Widget original = new() { Value = 1, Numbers = numbers };
        DB087Widget child = new() { Value = 2, Numbers = numbers, Child = original };
        original.Child = child;
        using (Backend backend = Backend.Create(_path, original)) {
            Assert.NotSame(original, backend.State);
            original = (DB087Widget)backend.State;
            child = original.Child!;
            numbers = original.Numbers;
            initial = backend.Initial;
            Assert.Equal(3, initial.Records.Length);
            Assert.All(initial.Records, row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
            Assert.Same(original, backend.State);
            originalId = backend.RootId;
            var originalDto = backend.Baseline.Objects[originalId].Current
                .GetState<global::Atelia.DurableGraph.Generated.Family_5030576964676574.V1>();
            childId = originalDto.Segment0Field3;
            listId = originalDto.Segment0Field2;
            Assert.Equal(3, backend.Baseline.Objects.Count);
            Dictionary<ObjectId, ObjectStorageInfo> originalStorage = backend.Storage();
            backend.AssertStoredBaseline();

            // An already accepted child becomes root; the former root becomes its child.
            promoted = backend.Save(child);
            Assert.Equal(childId, promoted.RootId);
            Assert.Empty(promoted.Records);
            Assert.Empty(promoted.Removed);
            Assert.Same(child, backend.State);
            Assert.Same(original, child.Child);
            AssertStorageEqual(originalStorage, backend.Storage());
            Assert.Equal(initial.DirectoryRoot, promoted.DirectoryRoot);

            DB087EnvelopeRoot envelope = new() { Link = child, Numbers = numbers };
            NormalizedGraphState prior = backend.Baseline;
            ObjectId discardedId = backend.Discard(envelope);
            Assert.Same(prior, backend.Baseline);
            Assert.Same(child, backend.State);
            Assert.Equal(childId, backend.RootId);
            Assert.Equal(promoted.Address, backend.Baseline.RevisionAddress);
            AssertStorageEqual(originalStorage, backend.Storage());

            eventOnly = backend.Save(envelope, isEvent: true);
            Assert.True(eventOnly.RootId.Value > discardedId.Value);
            ObjectVersionRecord eventBase = Assert.Single(eventOnly.Records);
            Assert.Equal(eventOnly.RootId.Value, eventBase.ObjectId);
            Assert.Equal(ObjectVersionKind.Base, eventBase.Kind);
            Assert.Empty(eventOnly.Removed);
            Assert.Same(prior, backend.Baseline);
            Assert.Same(child, backend.State);
            Assert.Equal(childId, backend.RootId);
            AssertStorageEqual(originalStorage, backend.Storage());
            AssertRing(Assert.IsType<DB087EnvelopeRoot>(backend.Read(eventOnly)), changed: false);

            // The Event's new instance-to-ID binding was never accepted into State.
            envelopeState = backend.Save(envelope);
            envelopeId = envelopeState.RootId;
            Assert.True(envelopeId.Value > eventOnly.RootId.Value);
            ObjectVersionRecord stateBase = Assert.Single(envelopeState.Records);
            Assert.Equal(envelopeId.Value, stateBase.ObjectId);
            Assert.Equal(ObjectVersionKind.Base, stateBase.Kind);
            Assert.Empty(envelopeState.Removed);
            Assert.Same(envelope, backend.State);
            Assert.Equal(envelopeId, backend.RootId);
            Assert.Equal(4, backend.Baseline.Objects.Count);
            foreach ((ObjectId id, ObjectStorageInfo info) in originalStorage) {
                Assert.Equal(info, backend.Storage()[id]);
            }
            Assert.DoesNotContain(eventOnly.RootId, backend.Baseline.Objects.Keys);
            backend.AssertStoredBaseline();
        }

        // No previous graph, Store facade, snapshot or capture identity table survives this open.
        using (Backend backend = Backend.Open(_path)) {
            Assert.Equal(envelopeState.Address, backend.Baseline.RevisionAddress);
            Assert.Equal(envelopeId, backend.RootId);
            DB087EnvelopeRoot envelope = Assert.IsType<DB087EnvelopeRoot>(backend.State);
            AssertRing(envelope, changed: false);
            Dictionary<ObjectId, ObjectStorageInfo> source = backend.Storage();
            backend.AssertStoredBaseline();
            envelope.Numbers[7] = 9;
            continued = backend.Save(envelope);
            ObjectVersionRecord delta = Assert.Single(continued.Records);
            Assert.Equal(listId.Value, delta.ObjectId);
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
            Assert.Equal(source[listId].Head, delta.PriorAddress);
            Assert.Empty(continued.Removed);
            long d = ObjectVersionPayloadSize.GetDeltaPayloadBytes(delta.Body.Length, source[listId].Head);
            Assert.Equal(source[listId].ReconstructionPayloadBytes + d,
                backend.Storage()[listId].ReconstructionPayloadBytes);
            Assert.NotEqual(source[listId].Head, backend.Storage()[listId].Head);
            foreach (ObjectId retained in new[] { originalId, childId, envelopeId }) {
                Assert.Equal(source[retained], backend.Storage()[retained]);
            }
            backend.AssertStoredBaseline();
            AssertRing(Assert.IsType<DB087EnvelopeRoot>(backend.Read(continued)), changed: true);

            // Complete old membership is used for Removes even when two former roots vanish together.
            envelope.Link = null;
            trimmed = backend.Save(envelope);
            Assert.Equal(new[] { originalId.Value, childId.Value }.Order(), trimmed.Removed.Order());
            Assert.Equal(new[] { envelopeId, listId }.Order(), backend.Baseline.Objects.Keys.Order());
            Assert.Equal(envelopeId, backend.RootId);
            Assert.Same(envelope, backend.State);
            Assert.Null(Assert.IsType<DB087EnvelopeRoot>(backend.Read(trimmed)).Link);
            backend.AssertStoredBaseline();

            Dictionary<ObjectId, ObjectStorageInfo> trimmedStorage = backend.Storage();
            long beforeFrames = backend.AppendedFrames;
            unchanged = backend.Save(envelope);
            Assert.Empty(unchanged.Records);
            Assert.Empty(unchanged.Removed);
            AssertStorageEqual(trimmedStorage, backend.Storage());
            Assert.Equal(trimmed.DirectoryRoot, unchanged.DirectoryRoot);
            Assert.Null(unchanged.OwnedBatch);
            Assert.Equal(beforeFrames + 1, backend.AppendedFrames); // Only the actual graph frame.
            backend.AssertStoredBaseline();
        }

        using (Backend backend = Backend.Open(_path)) {
            Assert.Equal(unchanged.Address, backend.Baseline.RevisionAddress);
            Assert.Equal(envelopeId, backend.RootId);
            DB087EnvelopeRoot current = Assert.IsType<DB087EnvelopeRoot>(backend.State);
            Assert.Null(current.Link);
            AssertNumbers(current.Numbers, changed: true);
            DB087Widget oldRoot = Assert.IsType<DB087Widget>(backend.Read(initial));
            Assert.Equal(1, oldRoot.Value);
            Assert.Equal(2, oldRoot.Child!.Value);
            Assert.Same(oldRoot, oldRoot.Child.Child);
            Assert.Same(oldRoot.Numbers, oldRoot.Child.Numbers);
            AssertNumbers(oldRoot.Numbers, changed: false);
            DB087Widget oldPromoted = Assert.IsType<DB087Widget>(backend.Read(promoted));
            Assert.Equal(2, oldPromoted.Value);
            Assert.Same(oldPromoted, oldPromoted.Child!.Child);
            AssertRing(Assert.IsType<DB087EnvelopeRoot>(backend.Read(eventOnly)), changed: false);
            AssertRing(Assert.IsType<DB087EnvelopeRoot>(backend.Read(envelopeState)), changed: false);
            AssertRing(Assert.IsType<DB087EnvelopeRoot>(backend.Read(continued)), changed: true);
            backend.AssertStoredBaseline();
        }
    }

    private static void AssertNumbers(List<int> numbers, bool changed) {
        Assert.Equal(128, numbers.Count);
        for (int i = 0; i < numbers.Count; i++) { Assert.Equal(changed && i == 7 ? 9 : 0, numbers[i]); }
    }

    private static void AssertRing(DB087EnvelopeRoot envelope, bool changed) {
        DB087Widget child = Assert.IsType<DB087Widget>(envelope.Link);
        Assert.Equal(2, child.Value);
        DB087Widget original = Assert.IsType<DB087Widget>(child.Child);
        Assert.Equal(1, original.Value);
        Assert.Same(child, original.Child);
        Assert.Same(envelope.Numbers, child.Numbers);
        Assert.Same(child.Numbers, original.Numbers);
        AssertNumbers(envelope.Numbers, changed);
    }

    private static void AssertStorageEqual(IReadOnlyDictionary<ObjectId, ObjectStorageInfo> expected,
        IReadOnlyDictionary<ObjectId, ObjectStorageInfo> actual) {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach ((ObjectId id, ObjectStorageInfo info) in expected) { Assert.Equal(info, actual[id]); }
    }

    private static StateModelRegistry Models() {
        StateModelRegistry models = new();
        models.Register(global::Atelia.DurableGraph.Generated.Family_5030576964676574.Definition);
        models.Register(global::Atelia.DurableGraph.Generated.Family_5030456E76656C6F7065.Definition);
        return models;
    }

    private sealed record Saved(FrameAddress Address, ObjectId RootId, ObjectVersionRecord[] Records,
        uint[] Removed, FrameAddress? DirectoryRoot = null, FrameAddress? OwnedBatch = null);

    // One real public Repository/checkout; diagnostics read its actual split writer results.
    private sealed class Backend : IDisposable {
        private readonly Repository _repository;
        private readonly WorkingCopy _checkout;
        internal Saved Initial { get; }
        private Backend(string path, DB087Widget? initial) {
            _repository = initial is not null ? Repository.CreateNew(path, Models()) : Repository.OpenExisting(path, Models());
            _checkout = initial is not null ? _repository.OpenWorkingCopy(_repository.CreateRootBranchFromState("main", initial).Id) : _repository.OpenWorkingCopy(BranchNamed(_repository, "main"));
            Initial = ReadSaved(Describe(_repository, _checkout.BaseCommitId).Address);
        }
        internal static Backend Create(string path, DB087Widget initial) => new(path, initial);
        internal static Backend Open(string path) => new(path, null);
        internal IDurableObject State => _checkout.State!;
        internal ObjectId RootId => _checkout.StateId!.Value;
        internal NormalizedGraphState Baseline => _checkout.Baseline!;
        internal long AppendedFrames => _repository.GraphStore.AppendedFrames;
        internal Saved Save(IDurableObject root, bool isEvent = false) {
            CommitId committed = isEvent ? _checkout.CommitEvent(root).Id : _checkout.CommitState(root).Id;
            Assert.Equal(committed, _repository.ReadCurrentCommit(_checkout.BranchId).Commit.Id);
            return ReadSaved(Describe(_repository, committed).Address);
        }
        internal ObjectId Discard(IDurableObject root) {
            CommitId head = _checkout.BaseCommitId;
            long frames = AppendedFrames;
            ObjectId rootId;
            using (PreparedBranchSave save = _checkout.SaveSession.Prepare(root)) { rootId = save.Business.Write.RootId; }
            Assert.Equal(head, _repository.ReadCurrentCommit(_checkout.BranchId).Commit.Id);
            Assert.Equal(frames, AppendedFrames);
            return rootId;
        }
        internal Dictionary<ObjectId, ObjectStorageInfo> Storage() => Baseline.Objects
            .ToDictionary(pair => pair.Key, pair => pair.Value.Storage!.Value);
        internal void AssertStoredBaseline() {
            GraphRecordStore store = _repository.GraphStore;
            NormalizedGraphState baseline = Baseline;
            IReadOnlyDictionary<uint, FrameAddress> heads = MechanismFixture.Snapshot(store, baseline.RevisionAddress).ObjectHeads;
            Assert.Equal(baseline.Objects.Keys.Select(id => id.Value).Order(), heads.Keys.Order());
            foreach ((ObjectId id, NormalizedObject row) in baseline.Objects) {
                ObjectStorageInfo info = row.Storage!.Value;
                Assert.Equal(heads[id.Value], info.Head);
                Assert.Equal(store.ReadObjectChain(MechanismFixture.Snapshot(store, baseline.RevisionAddress), id.Value).ReconstructionPayloadBytes,
                    info.ReconstructionPayloadBytes);
            }
        }
        internal IDurableObject Read(Saved saved) =>
            BusinessPersistenceAdapter.ReadRoot(_repository.GraphStore, MechanismFixture.Snapshot(_repository.GraphStore, saved.Address), Models());
        private Saved ReadSaved(FrameAddress address) {
            GraphRecordStore store = _repository.GraphStore;
            StoredGraphRevision graph = MechanismFixture.Snapshot(store, address);
            ObjectVersionRecord[] records = graph.OwnedBatch is { } batch ? store.ReadBatch(batch).Records.ToArray() : [];
            uint[] removed = graph.SourceGraph is { } source
                ? MechanismFixture.Snapshot(store, source).ObjectHeads.Keys.Except(graph.ObjectHeads.Keys).ToArray() : [];
            return new(address, graph.RootId, records, removed, graph.DirectoryRoot, graph.OwnedBatch);
        }
        public void Dispose() {
            _checkout.Dispose();
            _repository.Dispose();
        }
    }

    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
