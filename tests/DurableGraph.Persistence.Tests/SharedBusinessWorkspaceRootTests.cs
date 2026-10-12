using System.Reflection;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothBackendsPreserveActualRootIdentityAcrossPromotionEventDiscardAndColdDelta(bool split) {
        Saved initial, promoted, eventOnly, envelopeState, continued, trimmed, unchanged;
        ObjectId originalId, childId, listId, envelopeId;
        List<int> numbers = Enumerable.Repeat(0, 128).ToList(); // A sparse update must be an actual default-budget Delta.
        DB087Widget original = new() { Value = 1, Numbers = numbers };
        DB087Widget child = new() { Value = 2, Numbers = numbers, Child = original };
        original.Child = child;
        using (Backend backend = Backend.Create(_path, split, original)) {
            initial = backend.Initial;
            Assert.Equal(3, initial.Records.Length);
            Assert.All(initial.Records, row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
            Assert.Same(original, backend.Core.Root);
            originalId = backend.Core.RootId;
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
            Assert.Same(child, backend.Core.Root);
            Assert.Same(original, child.Child);
            AssertStorageEqual(originalStorage, backend.Storage());
            if (split) { Assert.Equal(initial.DirectoryRoot, promoted.DirectoryRoot); }

            DB087EnvelopeRoot envelope = new() { Link = child, Numbers = numbers };
            NormalizedRevision prior = backend.Baseline;
            ObjectId discardedId = backend.Discard(envelope);
            Assert.Same(prior, backend.Baseline);
            Assert.Same(child, backend.Core.Root);
            Assert.Equal(childId, backend.Core.RootId);
            Assert.Equal(promoted.Address, backend.Baseline.RevisionAddress);
            AssertStorageEqual(originalStorage, backend.Storage());

            eventOnly = backend.Save(envelope, isEvent: true);
            Assert.True(eventOnly.RootId.Value > discardedId.Value);
            ObjectVersionRecord eventBase = Assert.Single(eventOnly.Records);
            Assert.Equal(eventOnly.RootId.Value, eventBase.ObjectId);
            Assert.Equal(ObjectVersionKind.Base, eventBase.Kind);
            Assert.Empty(eventOnly.Removed);
            Assert.Same(prior, backend.Baseline);
            Assert.Same(child, backend.Core.Root);
            Assert.Equal(childId, backend.Core.RootId);
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
            Assert.Same(envelope, backend.Core.Root);
            Assert.Equal(envelopeId, backend.Core.RootId);
            Assert.Equal(4, backend.Baseline.Objects.Count);
            foreach ((ObjectId id, ObjectStorageInfo info) in originalStorage) {
                Assert.Equal(info, backend.Storage()[id]);
            }
            Assert.DoesNotContain(eventOnly.RootId, backend.Baseline.Objects.Keys);
            backend.AssertStoredBaseline();
        }

        // No previous graph, Store facade, snapshot or capture identity table survives this open.
        using (Backend backend = Backend.Open(_path, split)) {
            Assert.Equal(envelopeState.Address, backend.Baseline.RevisionAddress);
            Assert.Equal(envelopeId, backend.Core.RootId);
            DB087EnvelopeRoot envelope = Assert.IsType<DB087EnvelopeRoot>(backend.Core.Root);
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
            Assert.Equal(envelopeId, backend.Core.RootId);
            Assert.Same(envelope, backend.Core.Root);
            Assert.Null(Assert.IsType<DB087EnvelopeRoot>(backend.Read(trimmed)).Link);
            backend.AssertStoredBaseline();

            Dictionary<ObjectId, ObjectStorageInfo> trimmedStorage = backend.Storage();
            long beforeFrames = backend.AppendedFrames;
            unchanged = backend.Save(envelope);
            Assert.Empty(unchanged.Records);
            Assert.Empty(unchanged.Removed);
            AssertStorageEqual(trimmedStorage, backend.Storage());
            if (split) {
                Assert.Equal(trimmed.DirectoryRoot, unchanged.DirectoryRoot);
                Assert.Null(unchanged.OwnedBatch);
                Assert.Equal(beforeFrames + 1, backend.AppendedFrames); // Only the actual graph frame.
            } else {
                // Legacy still writes its own new StateRevision/map; it has no P0 directory-address promise.
                Assert.NotEqual(trimmed.Address, unchanged.Address);
            }
            backend.AssertStoredBaseline();
        }

        using (Backend backend = Backend.Open(_path, split)) {
            Assert.Equal(unchanged.Address, backend.Baseline.RevisionAddress);
            Assert.Equal(envelopeId, backend.Core.RootId);
            DB087EnvelopeRoot current = Assert.IsType<DB087EnvelopeRoot>(backend.Core.Root);
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

    // Local fixture only: each branch calls its real owner/session and examines its actual persisted heads.
    private sealed class Backend : IDisposable {
        private readonly Repository? _legacy;
        private readonly BranchCheckout? _checkout;
        private readonly P0Repository? _split;
        private readonly P0Session? _session;
        internal Saved Initial { get; }

        private Backend(string path, bool split, DB087Widget? initial) {
            if (split) {
                _split = initial is not null ? P0Repository.CreateNew(path) : P0Repository.OpenExisting(path);
                _session = new(_split, "main", Models());
                Initial = initial is not null ? Save(initial) : ReadSaved(_session.StateSource!.Address);
            } else {
                _legacy = initial is not null ? Repository.CreateNew(path, Models()) : Repository.OpenExisting(path, Models());
                _checkout = initial is not null ? _legacy.CreateBranch("main", initial) : _legacy.Checkout("main");
                Initial = ReadSaved(_checkout.StateRevisionAddress!.Value, _checkout.StateId!.Value);
            }
        }

        internal static Backend Create(string path, bool split, DB087Widget initial) => new(path, split, initial);
        internal static Backend Open(string path, bool split) => new(path, split, null);
        internal BusinessWorkspace Core {
            get {
                object adapter = _session is not null ? typeof(P0Session)
                    .GetField("_business", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_session)!
                    : _checkout!.Workspace;
                return Assert.IsType<BusinessWorkspace>(adapter.GetType()
                    .GetField("_workspace", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter));
            }
        }
        internal NormalizedRevision Baseline => Assert.IsType<NormalizedRevision>(Core.Baseline);
        internal long AppendedFrames => _split?.Store.AppendedFrames ?? 0;
        private GraphResources LegacyResources => Assert.IsType<GraphResources>(typeof(Repository)
            .GetField("_resources", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_legacy));

        internal Saved Save(IDurableObject root, bool isEvent = false) {
            if (_session is not null) {
                using P0SessionSave save = _session.Prepare(root, isEvent ? P0GraphKind.Event : P0GraphKind.State);
                ObjectVersionRecord[] records = save.Business.Write.Records.ToArray();
                uint[] removed = save.Business.Write.RemovedIds.ToArray();
                P0GraphSnapshot graph = save.Commit().Business;
                Assert.Equal(graph.Address, _split!.GetHead("main"));
                Assert.Equal(_session.StateSource!.Address, _split.GetStateHead("main"));
                return new(graph.Address, graph.RootId, records, removed, graph.DirectoryRoot, graph.OwnedBatch);
            }
            CheckpointAddress checkpoint = isEvent ? _checkout!.CommitEvent(root) : _checkout!.CommitState(root);
            Assert.Equal(checkpoint, _checkout.Head);
            return ReadSaved(checkpoint.RevisionAddress, checkpoint.RootId);
        }

        internal ObjectId Discard(IDurableObject root) {
            if (_session is not null) {
                FrameAddress? head = _split!.GetHead("main");
                long frames = _split.Store.AppendedFrames;
                ObjectId rootId;
                using (P0SessionSave save = _session.Prepare(root)) { rootId = save.Business.Write.RootId; }
                Assert.Equal(head, _split.GetHead("main"));
                Assert.Equal(frames, _split.Store.AppendedFrames);
                return rootId;
            }
            CheckpointAddress before = _checkout!.Head;
            ObjectId id;
            using (PreparedWorldSave save = _checkout.Workspace.Stage(root, new(5, 5))) { id = save.RootId; }
            Assert.Equal(before, _checkout.Head);
            return id;
        }

        internal Dictionary<ObjectId, ObjectStorageInfo> Storage() => Baseline.Objects
            .ToDictionary(pair => pair.Key, pair => pair.Value.Storage!.Value);

        internal void AssertStoredBaseline() {
            NormalizedRevision baseline = Baseline;
            IReadOnlyDictionary<uint, FrameAddress> heads = _split is not null
                ? _split.Store.ReadGraph(baseline.RevisionAddress).Objects
                : LegacyResources.States.ReadLiveObjectHeadMap(baseline.RevisionAddress);
            Assert.Equal(baseline.Objects.Keys.Select(id => id.Value).Order(), heads.Keys.Order());
            foreach ((ObjectId id, NormalizedObject row) in baseline.Objects) {
                ObjectStorageInfo info = row.Storage!.Value;
                Assert.Equal(heads[id.Value], info.Head);
                long h = _split is not null
                    ? _split.Store.ReadObjectChain(baseline.RevisionAddress, id.Value).ReconstructionPayloadBytes
                    : LegacyResources.States.ReadObjectVersionChain(baseline.RevisionAddress, id.Value).ReconstructionPayloadBytes;
                Assert.Equal(h, info.ReconstructionPayloadBytes);
            }
        }

        internal IDurableObject Read(Saved saved) {
            if (_split is not null) { return P0BusinessWorkspace.ReadRoot(_split.Store, saved.Address, Models()); }
            GraphResources resources = LegacyResources;
            RevisionReadSession reads = new(resources.States, resources.Schemas, Models().Snapshot(resources.Schemas));
            return GraphReader.ReadRoot<IDurableObject>(reads, saved.Address, saved.RootId);
        }

        private Saved ReadSaved(FrameAddress address, ObjectId rootId = default) {
            if (_split is not null) {
                P0GraphSnapshot graph = _split.Store.ReadGraph(address);
                return new(address, graph.RootId, [], [], graph.DirectoryRoot, graph.OwnedBatch);
            }
            StateRevision revision = LegacyResources.States.Read(address);
            return new(address, rootId, revision.LocalObjects.ToArray(), revision.RemovedObjectIds.ToArray());
        }

        public void Dispose() {
            _checkout?.Dispose();
            _legacy?.Dispose();
            _split?.Dispose();
        }
    }

    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
