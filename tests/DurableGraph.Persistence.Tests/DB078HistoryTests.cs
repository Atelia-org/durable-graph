using System.Buffers;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.RbfSegmentStore;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace Atelia.DurableGraph.Persistence.Tests;

/// <summary>DB-078-A's independent histories and root-type transitions.</summary>
public sealed class DB078HistoryTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"durable-graph-db078-history-{Guid.NewGuid():N}");
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1000000, 1);
    private static readonly DurableSchema AlphaSchema = new("DB078Alpha", 1,
        new DurableFieldInfo(1, TypeTag.Byte), new DurableFieldInfo(2, TypeTag.ObjectReference, "DB078Beta"));
    private static readonly DurableSchema BetaSchema = new("DB078Beta", 1,
        new DurableFieldInfo(1, TypeTag.Byte), new DurableFieldInfo(2, TypeTag.ObjectReference, "DB078Alpha"));
    private static readonly DurableSchema MessageSchema = new("DB078Message", 1, new DurableFieldInfo(1, TypeTag.Byte));

    [Fact]
    public void PublicFoundationIsNonGenericAndDoesNotRetainOldSessionOrPendingEventSurface() {
        Assert.Equal("Atelia.DurableGraph", typeof(Repository).Namespace);
        Assert.Equal("Atelia.DurableGraph", typeof(BranchCheckout).Namespace);
        Assert.Equal("Atelia.DurableGraph", typeof(CheckpointAddress).Namespace);
        foreach (Type type in new[] { typeof(Repository), typeof(BranchCheckout) }) {
            Assert.False(type.IsGenericType);
            Assert.DoesNotContain(type.GetMethods(), method => method.IsGenericMethod);
            Assert.DoesNotContain(type.GetMembers(), member => member.Name.Contains("PendingEvent", StringComparison.Ordinal));
        }
        Assert.Empty(typeof(CheckpointAddress).GetConstructors());
        Assert.Null(typeof(Repository).Assembly.GetType("Atelia.DurableGraph.Persistence.EventHistoryRepository"));
        Assert.Null(typeof(Repository).Assembly.GetType("Atelia.DurableGraph.Persistence.EventHistorySession`1"));
        Assert.Null(typeof(Repository).Assembly.GetType("Atelia.DurableGraph.Persistence.GraphFrame"));
    }

    [Fact]
    public void ConsecutiveEventsAndStatesUseExactJournalHeadAndNearestStateGraphParent() {
        CheckpointAddress s0, e1, e2, s1, s2;
        Alpha root = new() { Value = 1 };
        using (Repository repository = Create()) {
            using BranchCheckout checkout = repository.CreateBranch("main", root, NoRebase);
            s0 = checkout.Head;
            e1 = checkout.CommitEvent(new Message { Value = 11 }, NoRebase);
            e2 = checkout.CommitEvent(new Message { Value = 12 }, NoRebase);
            Assert.Same(root, checkout.State);
            Assert.Equal(s0.RevisionAddress, checkout.StateRevisionAddress);
            root.Value = 2;
            s1 = checkout.CommitState(NoRebase);
            root.Value = 3;
            s2 = checkout.CommitState(NoRebase);
            Assert.Null(s0.Parent);
            Assert.Equal(s0.Address, e1.Parent);
            Assert.Equal(e1.Address, e2.Parent);
            Assert.Equal(e2.Address, s1.Parent);
            Assert.Equal(s1.Address, s2.Parent);
            Assert.Equal(s0, repository.GetPreviousState(e2));
            Assert.Equal(5, repository.ReadFrames("main").Count);
            Assert.Equal(2, repository.EnumerateEvents(repository.GetHead("main"), HistoryOrder.OldestFirst).ToArray().Length);
            Assert.Equal((byte)1, Assert.IsType<Alpha>(repository.ReadState(s0)).Value);
        }
        using (SegmentStore segments = OpenState()) {
            using StateRevisionStore states = new(segments);
            Assert.Null(states.Read(s0.RevisionAddress).ParentRevisionAddress);
            foreach (CheckpointAddress address in new[] { e1, e2, s1 }) {
                Assert.Equal(s0.RevisionAddress, states.Read(address.RevisionAddress).ParentRevisionAddress);
            }
            Assert.Equal(s1.RevisionAddress, states.Read(s2.RevisionAddress).ParentRevisionAddress);
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        using BranchCheckout cold = reopened.Checkout("main");
        Assert.Equal((byte)3, Assert.IsType<Alpha>(cold.State).Value);
        Assert.Equal(s2.RevisionAddress, cold.Head.RevisionAddress);
        CheckpointAddress nextEvent = cold.CommitEvent(new Message(), NoRebase);
        Assert.Equal(s2.Address, nextEvent.Parent);
        Assert.Equal(s2.RevisionAddress, cold.StateRevisionAddress);
    }

    [Fact]
    public void EventOnlyColdCheckoutNeedsNoModelsAndFirstStateCanReplaceTypeAndSaveAgain() {
        CheckpointAddress e0, e1, s0, s1, s2;
        using (Repository repository = Create()) {
            using BranchCheckout checkout = repository.CreateBranchFromEvent("main", new Message { Value = 7 }, NoRebase);
            e0 = checkout.Head;
            Assert.Null(checkout.State);
            Assert.Null(checkout.StateId);
            Assert.Null(checkout.StateRevisionAddress);
            e1 = checkout.CommitEvent(new Message { Value = 8 }, NoRebase);
            Assert.Null(repository.GetPreviousState(e1));
        }
        using (Repository repository = Repository.OpenExisting(_root, new StateModelRegistry())) {
            using BranchCheckout empty = repository.Checkout("main");
            Assert.Null(empty.State);
            Assert.Equal(e1.RevisionAddress, empty.Head.RevisionAddress);
            Assert.ThrowsAny<Exception>(() => repository.ReadEvent(empty.Head));
            Assert.False(repository.IsFaulted);
        }
        using (Repository repository = Repository.OpenExisting(_root, Models())) {
            using BranchCheckout checkout = repository.Checkout("main");
            Alpha alpha = new() { Value = 20 };
            s0 = checkout.CommitState(alpha, NoRebase);
            Assert.Same(alpha, checkout.State);
            // A cold Event-only checkout starts a fresh capture cursor; prior Event IDs are not imported.
            Assert.Equal(e0.RootId, s0.RootId);
            Beta beta = new() { Value = 30 };
            s1 = checkout.CommitState(beta, NoRebase);
            Assert.Same(beta, checkout.State);
            Assert.NotEqual(s0.RootId, s1.RootId);
            beta.Value = 31;
            s2 = checkout.CommitState(NoRebase);
            Assert.Equal(s1.RootId, s2.RootId);
            Assert.Equal((byte)7, Assert.IsType<Message>(repository.ReadEvent(repository.EnumerateEvents(repository.GetHead("main"), HistoryOrder.OldestFirst).ToArray().First())).Value);
            Assert.Equal((byte)20, Assert.IsType<Alpha>(repository.ReadState(s0)).Value);
        }
        using (SegmentStore segments = OpenState()) {
            using StateRevisionStore states = new(segments);
            foreach (CheckpointAddress address in new[] { e0, e1, s0 }) {
                Assert.Null(states.Read(address.RevisionAddress).ParentRevisionAddress);
                Assert.All(states.Read(address.RevisionAddress).LocalObjects, row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
            }
            Assert.Equal(s0.RevisionAddress, states.Read(s1.RevisionAddress).ParentRevisionAddress);
            Assert.Equal(s1.RevisionAddress, states.Read(s2.RevisionAddress).ParentRevisionAddress);
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        using BranchCheckout cold = reopened.Checkout("main");
        Assert.Equal((byte)31, Assert.IsType<Beta>(cold.State).Value);
        Assert.Equal(s2.RootId, cold.StateId);
    }

    [Fact]
    public void ChildPromotionAndOldRootDemotionPreserveIdentityAndUnreachableObjectsAreRemoved() {
        Alpha alpha = new() { Value = 1 };
        Beta beta = new() { Value = 2, Child = alpha };
        alpha.Child = beta;
        CheckpointAddress original, promoted, removed;
        using (Repository repository = Create()) {
            using BranchCheckout checkout = repository.CreateBranch("main", alpha, NoRebase);
            original = checkout.Head;
            promoted = checkout.CommitState(beta, NoRebase);
            Assert.Same(beta, checkout.State);
            Assert.Same(alpha, Assert.IsType<Beta>(checkout.State).Child);
            Assert.NotEqual(original.RootId, promoted.RootId);
            Beta read = Assert.IsType<Beta>(repository.ReadState(promoted));
            Assert.Same(read, read.Child!.Child);
            beta.Child = null;
            removed = checkout.CommitState(NoRebase);
            Assert.Equal(promoted.RootId, removed.RootId);
        }
        using (SegmentStore segments = OpenState()) {
            using StateRevisionStore states = new(segments);
            var originalIds = states.ReadLiveObjectHeadMap(original.RevisionAddress).Keys.Order().ToArray();
            Assert.Contains(promoted.RootId.Value, originalIds);
            Assert.Equal(originalIds, states.ReadLiveObjectHeadMap(promoted.RevisionAddress).Keys.Order());
            Assert.Empty(states.Read(promoted.RevisionAddress).LocalObjects);
            Assert.Empty(states.Read(promoted.RevisionAddress).RemovedObjectIds);
            // A smaller full head map may encode removal by absence rather than a Delta Remove row.
            Assert.Equal(new[] { promoted.RootId.Value }, states.ReadLiveObjectHeadMap(removed.RevisionAddress).Keys);
            Assert.DoesNotContain(original.RootId.Value, states.ReadLiveObjectHeadMap(removed.RevisionAddress).Keys);
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        using BranchCheckout cold = reopened.Checkout("main");
        Assert.Null(Assert.IsType<Beta>(cold.State).Child);
        Assert.Equal(promoted.RootId, cold.StateId);
        Assert.Equal(promoted.RootId, cold.CommitState(NoRebase).RootId);
    }

    [Fact]
    public void SameClrTypeMayTakeEitherRoleAndReadPairPreservesArgumentOrder() {
        using Repository repository = Create();
        using BranchCheckout checkout = repository.CreateBranchFromEvent("main", new Message { Value = 1 }, NoRebase);
        CheckpointAddress e = checkout.Head;
        CheckpointAddress s = checkout.CommitState(new Message { Value = 2 }, NoRebase);
        Assert.Equal(GraphFrameKind.Event, e.Kind);
        Assert.Equal(GraphFrameKind.State, s.Kind);
        Assert.Throws<ArgumentException>(() => repository.ReadState(e));
        Assert.Throws<ArgumentException>(() => repository.ReadEvent(s));
        var pair = repository.ReadPair(s, e);
        Assert.Equal((byte)2, Assert.IsType<Message>(pair.First).Value);
        Assert.Equal((byte)1, Assert.IsType<Message>(pair.Second).Value);
    }

    [Fact]
    public void CheckpointPreservesActualRootTypesAcrossStateReplacement() {
        using Repository repository = Create();
        using BranchCheckout checkout = repository.CreateBranch("main", new Alpha { Value = 1 }, NoRebase);
        CheckpointAddress first = checkout.Head;
        CheckpointAddress message = checkout.CommitEvent(new Message { Value = 2 }, NoRebase);
        CheckpointAddress replacement = checkout.CommitState(new Beta { Value = 3 }, NoRebase);
        EventCheckpoint eventView = Assert.IsType<EventCheckpoint>(repository.ReadCheckpoint(message));
        Assert.Equal((byte)2, Assert.IsType<Message>(eventView.Event).Value);
        Assert.Equal((byte)1, Assert.IsType<Alpha>(eventView.PreviousState).Value);
        Assert.Equal(first, eventView.PreviousStateAddress);
        StateCheckpoint stateView = Assert.IsType<StateCheckpoint>(repository.ReadCheckpoint(replacement));
        Assert.Equal((byte)3, Assert.IsType<Beta>(stateView.State).Value);
        Assert.Equal((byte)2, Assert.IsType<Message>(stateView.PreviousEvent).Value);
        Assert.Equal(message, stateView.PreviousEventAddress);
        Assert.NotSame(checkout.State, stateView.State);
        Assert.NotSame(eventView.Event, stateView.PreviousEvent);
    }

    [Fact]
    public void MissingPreviousEventModelFailsCheckpointEvenWhenCurrentStateIsReadable() {
        using (Repository repository = Create()) {
            using BranchCheckout branch = repository.CreateBranchFromEvent("main", new Message { Value = 1 }, NoRebase);
            branch.CommitState(new Alpha { Value = 2 }, NoRebase);
        }
        using Repository stateOnly = Repository.OpenReadOnlyExisting(_root, Models(includeMessage: false));
        CheckpointAddress state = stateOnly.GetHead("main");
        Assert.Equal((byte)2, Assert.IsType<Alpha>(stateOnly.ReadState(state)).Value);
        Checkpoint? delivered = null;
        Assert.ThrowsAny<Exception>(() => delivered = stateOnly.ReadCheckpoint(state));
        Assert.Null(delivered);
        Assert.False(stateOnly.IsFaulted);
        Assert.Equal((byte)2, Assert.IsType<Alpha>(stateOnly.ReadState(state)).Value);
    }

    [Fact]
    public void StateOnlyCatalogRestoresFromConsecutiveEventHeadButMissingStateNeverBecomesNull() {
        CheckpointAddress e2;
        using (Repository repository = Create()) {
            using BranchCheckout checkout = repository.CreateBranch("main", new Alpha { Value = 4 }, NoRebase);
            checkout.CommitEvent(new Message { Value = 5 }, NoRebase);
            e2 = checkout.CommitEvent(new Message { Value = 6 }, NoRebase);
        }
        using (Repository repository = Repository.OpenExisting(_root, Models(includeMessage: false))) {
            using BranchCheckout checkout = repository.Checkout("main");
            Assert.Equal(e2.RevisionAddress, checkout.Head.RevisionAddress);
            Assert.Equal((byte)4, Assert.IsType<Alpha>(checkout.State).Value);
            Assert.ThrowsAny<Exception>(() => repository.ReadEvent(checkout.Head));
            Assert.Equal(GraphFrameKind.State, checkout.CommitState(NoRebase).Kind);
        }
        using Repository missing = Repository.OpenExisting(_root, new StateModelRegistry());
        Assert.ThrowsAny<Exception>(() => missing.Checkout("main"));
        Assert.False(missing.IsFaulted);
        Assert.Equal(GraphFrameKind.State, missing.GetHead("main").Kind);
    }

    [Fact]
    public void PredictableMissingRootAndNoStateFailuresDoNotCaptureOrWrite() {
        int captures = 0;
        using (Repository repository = Create(Models(onCapture: () => captures++))) {
            using BranchCheckout checkout = repository.CreateBranchFromEvent("main", new Message(), NoRebase);
        }
        int captured = captures;
        var before = SnapshotFiles();
        using (Repository repository = Repository.OpenExisting(_root, Models(onCapture: () => captures++))) {
            using BranchCheckout checkout = repository.Checkout("main");
            CheckpointAddress original = checkout.Head;
            Assert.Throws<InvalidOperationException>(() => checkout.CommitState(NoRebase));
            Assert.Throws<ArgumentNullException>(() => checkout.CommitState(null!, NoRebase));
            Assert.Throws<ArgumentNullException>(() => checkout.CommitEvent(null!, NoRebase));
            Assert.ThrowsAny<Exception>(() => checkout.CommitState(new Unregistered(), NoRebase));
            Assert.ThrowsAny<Exception>(() => checkout.CommitEvent(new Unregistered(), NoRebase));
            Assert.Equal(captured, captures);
            Assert.Null(checkout.State);
            Assert.Equal(original, checkout.Head);
            Assert.False(repository.IsFaulted);
        }
        AssertFiles(before);
        using (Repository repository = Repository.OpenExisting(_root, Models())) {
            using BranchCheckout checkout = repository.Checkout("main");
            checkout.CommitState(new Alpha(), NoRebase);
        }
        before = SnapshotFiles();
        using (Repository repository = Repository.OpenExisting(_root, Models(onCapture: () => captures++))) {
            using BranchCheckout checkout = repository.Checkout("main");
            IDurableObject root = checkout.State!;
            CheckpointAddress original = checkout.Head;
            Assert.ThrowsAny<Exception>(() => checkout.CommitState(new Unregistered(), NoRebase));
            Assert.Same(root, checkout.State);
            Assert.Equal(original, checkout.Head);
            Assert.Equal(captured, captures);
        }
        AssertFiles(before);
    }

    [Fact]
    public void AddressesHaveOwnerScopedValueEqualityAndRefOnlyOperationsNeedNoModels() {
        CheckpointAddress old;
        using (Repository repository = Create()) {
            using BranchCheckout checkout = repository.CreateBranchFromEvent("main", new Message(), NoRebase);
            old = checkout.Head;
            Assert.Equal(old, repository.GetHead("main"));
            Assert.True(old == repository.GetHead("main"));
            Assert.False(old != repository.GetHead("main"));
            Assert.Equal(old.GetHashCode(), repository.GetHead("main").GetHashCode());
            Assert.Single(new HashSet<CheckpointAddress> { old, repository.GetHead("main"), repository.ReadFrames("main")[0] });
        }
        using (Repository reopened = Repository.OpenExisting(_root, new StateModelRegistry())) {
            CheckpointAddress current = reopened.GetHead("main");
            Assert.NotEqual(old, current);
            Assert.Equal(current, reopened.CreateBranch("copy", current));
            Assert.Equal(current, reopened.GetHead("copy"));
            reopened.MoveBranch("copy", current, current);
        }
        var before = SnapshotFiles();
        using (Repository reopened = Repository.OpenExisting(_root, new StateModelRegistry())) {
            CheckpointAddress current = reopened.GetHead("main");
            Assert.Throws<ArgumentException>(() => reopened.ReadEvent(old));
            Assert.Throws<ArgumentException>(() => reopened.CreateBranch("old", old));
            Assert.Throws<ArgumentException>(() => reopened.MoveBranch("copy", current, old));
            Assert.Throws<ArgumentException>(() => reopened.MoveBranch("copy", old, current));
            Assert.Throws<ArgumentException>(() => reopened.ReadState(old));
            Assert.Throws<ArgumentException>(() => reopened.ReadPair(current, old));
            Assert.Throws<ArgumentException>(() => reopened.ReadPair(old, current));
            Assert.Throws<ArgumentException>(() => reopened.GetPreviousState(old));
            Assert.Throws<ArgumentNullException>(() => reopened.ReadEvent(null!));
            Assert.Throws<ArgumentNullException>(() => reopened.ReadState(null!));
            Assert.Throws<ArgumentNullException>(() => reopened.GetPreviousState(null!));
            Assert.Throws<ArgumentNullException>(() => reopened.ReadPair(current, null!));
            Assert.Throws<ArgumentNullException>(() => reopened.ReadPair(null!, current));
            Assert.Throws<ArgumentNullException>(() => reopened.CreateBranch("null", (CheckpointAddress)null!));
            Assert.Throws<ArgumentNullException>(() => reopened.MoveBranch("copy", null!, current));
            Assert.Throws<ArgumentNullException>(() => reopened.MoveBranch("copy", current, null!));
            using BranchCheckout checkout2 = reopened.Checkout("copy");
            Assert.Null(checkout2.State);
        }
        AssertFiles(before);
    }

    [Fact]
    public void PhysicallyInterleavedBranchesFollowTheirOwnLogicalStateAncestors() {
        CheckpointAddress root, mainEvent, otherState, mainEvent2, mainState;
        using (Repository repository = Create()) {
            using (BranchCheckout checkout = repository.CreateBranch("main", new Alpha { Value = 1 }, NoRebase)) {
                root = checkout.Head;
                mainEvent = checkout.CommitEvent(new Message { Value = 2 }, NoRebase);
            }
            repository.CreateBranch("other", root);
            using (BranchCheckout other = repository.Checkout("other")) {
                otherState = other.CommitState(new Beta { Value = 8 }, NoRebase);
            }
            using (BranchCheckout main = repository.Checkout("main")) {
                mainEvent2 = main.CommitEvent(new Message { Value = 3 }, NoRebase);
                mainState = main.CommitState(NoRebase);
            }
            Assert.Equal(mainEvent.Address, mainEvent2.Parent);
            Assert.Equal(mainEvent2.Address, mainState.Parent);
            Assert.Equal(root, repository.GetPreviousState(mainEvent2));
            StateCheckpoint mainView = Assert.IsType<StateCheckpoint>(repository.ReadCheckpoint(mainState));
            Assert.Equal(mainEvent2, mainView.PreviousEventAddress);
            Assert.Equal((byte)3, Assert.IsType<Message>(mainView.PreviousEvent).Value);
            StateCheckpoint siblingView = Assert.IsType<StateCheckpoint>(repository.ReadCheckpoint(otherState));
            Assert.Null(siblingView.PreviousEvent);
            Assert.Null(siblingView.PreviousEventAddress);
        }
        using (SegmentStore segments = OpenState()) {
            using StateRevisionStore states = new(segments);
            foreach (CheckpointAddress address in new[] { otherState, mainEvent2, mainState }) {
                Assert.Equal(root.RevisionAddress, states.Read(address.RevisionAddress).ParentRevisionAddress);
            }
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        using (BranchCheckout main = reopened.Checkout("main")) {
            Assert.Equal((byte)1, Assert.IsType<Alpha>(main.State).Value);
        }
        using BranchCheckout otherCold = reopened.Checkout("other");
        Assert.Equal((byte)8, Assert.IsType<Beta>(otherCold.State).Value);
    }

    private sealed class Alpha : IDurableObject { internal byte Value; internal Beta? Child; }
    private sealed class Beta : IDurableObject { internal byte Value; internal Alpha? Child; }
    private sealed class Message : IDurableObject { internal byte Value; }
    private sealed class Unregistered : IDurableObject { }
    private readonly record struct Dto(byte Value, ObjectId Child);

    private static StateModelRegistry Models(bool includeMessage = true, Action? onCapture = null) {
        StateModelRegistry models = new();
        models.Register(Model(AlphaSchema, BetaSchema.SchemaId, () => new Alpha(), x => x.Value, x => x.Child,
            (Alpha x, byte value, IDurableObject? child) => { x.Value = value; x.Child = (Beta?)child; }, onCapture));
        models.Register(Model(BetaSchema, AlphaSchema.SchemaId, () => new Beta(), x => x.Value, x => x.Child,
            (Beta x, byte value, IDurableObject? child) => { x.Value = value; x.Child = (Alpha?)child; }, onCapture));
        if (includeMessage) {
            models.Register(Model(MessageSchema, null, () => new Message(), x => x.Value, _ => null,
                (Message x, byte value, IDurableObject? _) => x.Value = value, onCapture));
        }
        return models;
    }

    private static StateModelBinding Model<T>(DurableSchema schema, string? childSchema, Func<T> allocate,
        Func<T, byte> value, Func<T, IDurableObject?> child, Action<T, byte, IDurableObject?> hydrate, Action? onCapture)
        where T : class, IDurableObject {
        PreparedBaseBody Base(in Dto state) {
            ArrayBufferWriter<byte> bytes = new();
            BinaryPayloadWriter writer = new(bytes);
            writer.WriteByte(state.Value);
            if (childSchema is not null) writer.WriteUInt32(state.Child.Value);
            return new(bytes.WrittenSpan);
        }
        Dto Read(ref BinaryPayloadReader reader) => new(reader.ReadByte(), childSchema is null ? default : new ObjectId(reader.ReadUInt32()));
        void Visit(in Dto state, IStateReferenceVisitor visitor) {
            if (childSchema is not null) visitor.VisitDurable(state.Child, childSchema);
        }
        CapturedStatePreparation<Dto> preparation = new(schema, Base,
            (in Dto prior, in Dto next) => new(prior != next, Base(next).Body));
        return new StateModelBinding<T, Dto>(preparation,
            [new StateReaderBinding<Dto>(schema, Read, (ref BinaryPayloadReader reader, in Dto prior) => Read(ref reader), Visit)],
            row => row.GetState<Dto>(), allocate,
            (T instance, in Dto state, ObjectReadTable objects) => hydrate(instance, state.Value,
                childSchema is null ? null : objects.ResolveDurable<IDurableObject>(state.Child)),
            (instance, context) => {
                onCapture?.Invoke();
                return new(value(instance), childSchema is null ? default : context.CaptureDurable(child(instance), childSchema));
            }, Visit);
    }

    private Repository Create(StateModelRegistry? models = null) => Repository.CreateNew(_root, models ?? Models(),
        new() { NewStoreLayout = RbfSegmentStoreLayout.Flat });
    private SegmentStore OpenState() => SegmentStore.OpenExisting(Path.Combine(_root, "state"));
    private Dictionary<string, byte[]> SnapshotFiles() => Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => path, File.ReadAllBytes);
    private void AssertFiles(Dictionary<string, byte[]> before) {
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order());
        foreach (var pair in before) Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
    }

    public void Dispose() {
        string full = Path.GetFullPath(_root);
        if (!string.Equals(Path.GetDirectoryName(full), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).StartsWith("durable-graph-db078-history-", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Refusing cleanup outside this fixture.");
        }
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }
}
