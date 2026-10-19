using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;

using System.Buffers;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using SegmentStore = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

/// <summary>DB-078-A's independent histories and root-type transitions.</summary>
public sealed partial class DB078HistoryTests : IDisposable {
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
        Assert.Equal("Atelia.DurableGraph", typeof(WorkingCopy).Namespace);
        Assert.Equal("Atelia.DurableGraph", typeof(CommitId).Namespace);
        foreach (Type type in new[] { typeof(Repository), typeof(WorkingCopy) }) {
            Assert.False(type.IsGenericType);
            Assert.DoesNotContain(type.GetMethods(), method => method.IsGenericMethod);
            Assert.DoesNotContain(type.GetMembers(), member => member.Name.Contains("PendingEvent", StringComparison.Ordinal));
        }
        Assert.Empty(typeof(CommitId).GetConstructors());
        Assert.Null(typeof(Repository).Assembly.GetType("Atelia.DurableGraph.Persistence.EventHistoryRepository"));
        Assert.Null(typeof(Repository).Assembly.GetType("Atelia.DurableGraph.Persistence.EventHistorySession`1"));
        Assert.Null(typeof(Repository).Assembly.GetType("Atelia.DurableGraph.Persistence.GraphFrame"));
    }

    [Fact]
    public void ConsecutiveEventsAndStatesUseExactHeadHistoryAndNearestStateSource() {
        CommitId s0, e1, e2, s1, s2;
        Alpha root = new() { Value = 1 };
        using (Repository repository = Create()) {
            using WorkingCopy checkout = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", root, parameters: NoRebase).Id);
            s0 = checkout.BaseCommitId;
            e1 = checkout.CommitEvent(new Message { Value = 11 }, parameters: NoRebase).Id;
            e2 = checkout.CommitEvent(new Message { Value = 12 }, parameters: NoRebase).Id;
            root = Assert.IsType<Alpha>(checkout.State);
            Assert.Equal(s0, checkout.StateSourceCommitId);
            root.Value = 2;
            s1 = checkout.CommitState(parameters: NoRebase).Id;
            root.Value = 3;
            s2 = checkout.CommitState(parameters: NoRebase).Id;
            Assert.Null(Previous(repository, s0));
            Assert.Equal(s0, Previous(repository, e1));
            Assert.Equal(e1, Previous(repository, e2));
            Assert.Equal(e2, Previous(repository, s1));
            Assert.Equal(s1, Previous(repository, s2));
            Assert.Equal(s0, repository.GetPreviousState(e2));
            Assert.Equal(5, repository.ReadFrames(BranchNamed(repository, "main")).Count);
            Assert.Equal(2, repository.EnumerateEvents(repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id, HistoryOrder.OldestFirst).ToArray().Length);
            Assert.Equal((byte)1, Assert.IsType<Alpha>(repository.LoadState(s0)).Value);
        }
        using (RepositoryResources resources = RepositoryResources.OpenReadOnlyExisting(_root)) {
            GraphRecordStore states = resources.GraphStore;
            Assert.Null(Snapshot(resources, s0).SourceGraph);
            foreach (CommitId address in new[] { e1, e2, s1 }) {
                Assert.Equal(Describe(resources, s0).Address, Snapshot(resources, address).SourceGraph);
            }
            Assert.Equal(Describe(resources, s1).Address, Snapshot(resources, s2).SourceGraph);
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        using WorkingCopy cold = reopened.OpenWorkingCopy(BranchNamed(reopened, "main"));
        Assert.Equal((byte)3, Assert.IsType<Alpha>(cold.State).Value);
        Assert.Equal(s2, cold.BaseCommitId);
        CommitId nextEvent = cold.CommitEvent(new Message(), parameters: NoRebase).Id;
        Assert.Equal(s2, Previous(reopened, nextEvent));
        Assert.Equal(s2, cold.StateSourceCommitId);
    }

    [Fact]
    public void EventOnlyColdCheckoutNeedsNoModelsAndFirstStateCanReplaceTypeAndSaveAgain() {
        CommitId e0, e1, s0, s1, s2;
        using (Repository repository = Create()) {
            using WorkingCopy checkout = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("main", new Message { Value = 7 }, parameters: NoRebase).Id);
            e0 = checkout.BaseCommitId;
            Assert.Null(checkout.State);
            Assert.Null(checkout.StateId);
            Assert.Null(checkout.StateRevisionAddress);
            e1 = checkout.CommitEvent(new Message { Value = 8 }, parameters: NoRebase).Id;
            Assert.Null(repository.GetPreviousState(e1));
        }
        using (Repository repository = Repository.OpenExisting(_root, new StateModelRegistry())) {
            using WorkingCopy empty = repository.OpenWorkingCopy(BranchNamed(repository, "main"));
            Assert.Null(empty.State);
            Assert.Equal(e1, empty.BaseCommitId);
            Assert.ThrowsAny<Exception>(() => repository.LoadEvent(empty.BaseCommitId));
            Assert.False(repository.IsFaulted);
        }
        using (Repository repository = Repository.OpenExisting(_root, Models())) {
            using WorkingCopy checkout = repository.OpenWorkingCopy(BranchNamed(repository, "main"));
            Alpha alpha = new() { Value = 20 };
            s0 = checkout.CommitState(alpha, parameters: NoRebase).Id;
            Assert.Same(alpha, checkout.State);
            // A cold Event-only checkout starts a fresh capture cursor; prior Event IDs are not imported.
            Assert.Equal(Describe(repository, e0).RootId, Describe(repository, s0).RootId);
            Beta beta = new() { Value = 30 };
            s1 = checkout.CommitState(beta, parameters: NoRebase).Id;
            Assert.Same(beta, checkout.State);
            Assert.NotEqual(Describe(repository, s0).RootId, Describe(repository, s1).RootId);
            beta.Value = 31;
            s2 = checkout.CommitState(parameters: NoRebase).Id;
            Assert.Equal(Describe(repository, s1).RootId, Describe(repository, s2).RootId);
            Assert.Equal((byte)7, Assert.IsType<Message>(repository.LoadEvent(repository.EnumerateEvents(repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id, HistoryOrder.OldestFirst).ToArray().First().Id)).Value);
            Assert.Equal((byte)20, Assert.IsType<Alpha>(repository.LoadState(s0)).Value);
        }
        using (RepositoryResources resources = RepositoryResources.OpenReadOnlyExisting(_root)) {
            GraphRecordStore states = resources.GraphStore;
            foreach (CommitId address in new[] { e0, e1, s0 }) {
                Assert.Null(Snapshot(resources, address).SourceGraph);
                Assert.All(StorageTestData.LocalRecords(states, Describe(resources, address).Address), row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
            }
            Assert.Equal(Describe(resources, s0).Address, Snapshot(resources, s1).SourceGraph);
            Assert.Equal(Describe(resources, s1).Address, Snapshot(resources, s2).SourceGraph);
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        using WorkingCopy cold = reopened.OpenWorkingCopy(BranchNamed(reopened, "main"));
        Assert.Equal((byte)31, Assert.IsType<Beta>(cold.State).Value);
        Assert.Equal(Describe(reopened, s2).RootId, cold.StateId);
    }

    [Fact]
    public void ChildPromotionAndOldRootDemotionPreserveIdentityAndUnreachableObjectsAreRemoved() {
        Alpha alpha = new() { Value = 1 };
        Beta beta = new() { Value = 2, Child = alpha };
        alpha.Child = beta;
        CommitId original, promoted, removed;
        using (Repository repository = Create()) {
            using WorkingCopy checkout = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", alpha, parameters: NoRebase).Id);
            original = checkout.BaseCommitId;
            alpha = Assert.IsType<Alpha>(checkout.State);
            beta = alpha.Child!;
            promoted = checkout.CommitState(beta, parameters: NoRebase).Id;
            Assert.Same(beta, checkout.State);
            Assert.Same(alpha, Assert.IsType<Beta>(checkout.State).Child);
            Assert.NotEqual(Describe(repository, original).RootId, Describe(repository, promoted).RootId);
            Beta read = Assert.IsType<Beta>(repository.LoadState(promoted));
            Assert.Same(read, read.Child!.Child);
            beta.Child = null;
            removed = checkout.CommitState(parameters: NoRebase).Id;
            Assert.Equal(Describe(repository, promoted).RootId, Describe(repository, removed).RootId);
        }
        using (RepositoryResources resources = RepositoryResources.OpenReadOnlyExisting(_root)) {
            GraphRecordStore states = resources.GraphStore;
            var originalIds = Snapshot(resources, original).ObjectHeads.Keys.Order().ToArray();
            Assert.Contains(Describe(resources, promoted).RootId.Value, originalIds);
            Assert.Equal(originalIds, Snapshot(resources, promoted).ObjectHeads.Keys.Order());
            Assert.Empty(StorageTestData.LocalRecords(states, Describe(resources, promoted).Address));
            // A smaller full head map may encode removal by absence rather than a Delta Remove row.
            Assert.Equal(new[] { Describe(resources, promoted).RootId.Value }, Snapshot(resources, removed).ObjectHeads.Keys);
            Assert.DoesNotContain(Describe(resources, original).RootId.Value, Snapshot(resources, removed).ObjectHeads.Keys);
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        using WorkingCopy cold = reopened.OpenWorkingCopy(BranchNamed(reopened, "main"));
        Assert.Null(Assert.IsType<Beta>(cold.State).Child);
        Assert.Equal(Describe(reopened, promoted).RootId, cold.StateId);
        Assert.Equal(Describe(reopened, promoted).RootId, Describe(reopened, cold.CommitState(parameters: NoRebase).Id).RootId);
    }

    [Fact]
    public void SameClrTypeMayTakeEitherRoleAndReadPairPreservesArgumentOrder() {
        using Repository repository = Create();
        using WorkingCopy checkout = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("main", new Message { Value = 1 }, parameters: NoRebase).Id);
        CommitId e = checkout.BaseCommitId;
        CommitId s = checkout.CommitState(new Message { Value = 2 }, parameters: NoRebase).Id;
        Assert.Equal(CommitKind.Event, repository.ReadCommitInfo(e).Kind);
        Assert.Equal(CommitKind.State, repository.ReadCommitInfo(s).Kind);
        Assert.Throws<ArgumentException>(() => repository.LoadState(e));
        Assert.Throws<ArgumentException>(() => repository.LoadEvent(s));
        var pair = repository.ReadPair(s, e);
        Assert.Equal((byte)2, Assert.IsType<Message>(pair.First).Value);
        Assert.Equal((byte)1, Assert.IsType<Message>(pair.Second).Value);
    }

    [Fact]
    public void CheckpointPreservesActualRootTypesAcrossStateReplacement() {
        using Repository repository = Create();
        using WorkingCopy checkout = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", new Alpha { Value = 1 }, parameters: NoRebase).Id);
        CommitId first = checkout.BaseCommitId;
        CommitId message = checkout.CommitEvent(new Message { Value = 2 }, parameters: NoRebase).Id;
        CommitId replacement = checkout.CommitState(new Beta { Value = 3 }, parameters: NoRebase).Id;
        EventCheckpoint eventView = Assert.IsType<EventCheckpoint>(repository.ReadCheckpoint(message));
        Assert.Equal((byte)2, Assert.IsType<Message>(eventView.Event).Value);
        Assert.Equal((byte)1, Assert.IsType<Alpha>(eventView.PreviousState).Value);
        Assert.Equal(first, eventView.PreviousStateCommit?.Id);
        StateCheckpoint stateView = Assert.IsType<StateCheckpoint>(repository.ReadCheckpoint(replacement));
        Assert.Equal((byte)3, Assert.IsType<Beta>(stateView.State).Value);
        Assert.Equal((byte)2, Assert.IsType<Message>(stateView.PreviousEvent).Value);
        Assert.Equal(message, stateView.PreviousEventCommit?.Id);
        Assert.NotSame(checkout.State, stateView.State);
        Assert.NotSame(eventView.Event, stateView.PreviousEvent);
    }

    [Fact]
    public void MissingPreviousEventModelFailsCheckpointEvenWhenCurrentStateIsReadable() {
        using (Repository repository = Create()) {
            using WorkingCopy branch = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("main", new Message { Value = 1 }, parameters: NoRebase).Id);
            branch.CommitState(new Alpha { Value = 2 }, parameters: NoRebase);
        }
        using Repository stateOnly = Repository.OpenReadOnlyExisting(_root, Models(includeMessage: false));
        CommitId state = stateOnly.ReadCurrentCommit(BranchNamed(stateOnly, "main")).Commit.Id;
        Assert.Equal((byte)2, Assert.IsType<Alpha>(stateOnly.LoadState(state)).Value);
        Checkpoint? delivered = null;
        Assert.ThrowsAny<Exception>(() => delivered = stateOnly.ReadCheckpoint(state));
        Assert.Null(delivered);
        Assert.False(stateOnly.IsFaulted);
        Assert.Equal((byte)2, Assert.IsType<Alpha>(stateOnly.LoadState(state)).Value);
    }

    [Fact]
    public void StateOnlyCatalogRestoresFromConsecutiveEventHeadButMissingStateNeverBecomesNull() {
        CommitId e2;
        using (Repository repository = Create()) {
            using WorkingCopy checkout = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", new Alpha { Value = 4 }, parameters: NoRebase).Id);
            checkout.CommitEvent(new Message { Value = 5 }, parameters: NoRebase);
            e2 = checkout.CommitEvent(new Message { Value = 6 }, parameters: NoRebase).Id;
        }
        using (Repository repository = Repository.OpenExisting(_root, Models(includeMessage: false))) {
            using WorkingCopy checkout = repository.OpenWorkingCopy(BranchNamed(repository, "main"));
            Assert.Equal(e2, checkout.BaseCommitId);
            Assert.Equal((byte)4, Assert.IsType<Alpha>(checkout.State).Value);
            Assert.ThrowsAny<Exception>(() => repository.LoadEvent(checkout.BaseCommitId));
            Assert.Equal(CommitKind.State, checkout.CommitState(parameters: NoRebase).Kind);
        }
        using Repository missing = Repository.OpenExisting(_root, new StateModelRegistry());
        Assert.ThrowsAny<Exception>(() => missing.OpenWorkingCopy(BranchNamed(missing, "main")));
        Assert.False(missing.IsFaulted);
        Assert.Equal(CommitKind.State, missing.ReadCurrentCommit(BranchNamed(missing, "main")).Commit.Kind);
    }

    [Fact]
    public void PredictableMissingRootAndNoStateFailuresDoNotCaptureOrWrite() {
        int captures = 0;
        using (Repository repository = Create(Models(onCapture: () => captures++))) {
            using WorkingCopy checkout = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("main", new Message(), parameters: NoRebase).Id);
        }
        int captured = captures;
        var before = SnapshotFiles();
        using (Repository repository = Repository.OpenExisting(_root, Models(onCapture: () => captures++))) {
            using WorkingCopy checkout = repository.OpenWorkingCopy(BranchNamed(repository, "main"));
            CommitId original = checkout.BaseCommitId;
            Assert.Throws<InvalidOperationException>(() => checkout.CommitState(parameters: NoRebase).Id);
            Assert.Throws<ArgumentNullException>(() => checkout.CommitState(nextState: null!, parameters: NoRebase).Id);
            Assert.Throws<ArgumentNullException>(() => checkout.CommitEvent(null!, parameters: NoRebase).Id);
            Assert.ThrowsAny<Exception>(() => checkout.CommitState(new Unregistered(), parameters: NoRebase).Id);
            Assert.ThrowsAny<Exception>(() => checkout.CommitEvent(new Unregistered(), parameters: NoRebase).Id);
            Assert.Equal(captured, captures);
            Assert.Null(checkout.State);
            Assert.Equal(original, checkout.BaseCommitId);
            Assert.False(repository.IsFaulted);
        }
        AssertFiles(before);
        using (Repository repository = Repository.OpenExisting(_root, Models())) {
            using WorkingCopy checkout = repository.OpenWorkingCopy(BranchNamed(repository, "main"));
            checkout.CommitState(new Alpha(), parameters: NoRebase);
        }
        before = SnapshotFiles();
        using (Repository repository = Repository.OpenExisting(_root, Models(onCapture: () => captures++))) {
            using WorkingCopy checkout = repository.OpenWorkingCopy(BranchNamed(repository, "main"));
            IDurableObject root = checkout.State!;
            CommitId original = checkout.BaseCommitId;
            Assert.ThrowsAny<Exception>(() => checkout.CommitState(new Unregistered(), parameters: NoRebase).Id);
            root = Assert.IsType<Alpha>(checkout.State);
            Assert.Equal(original, checkout.BaseCommitId);
            Assert.Equal(captured, captures);
        }
        AssertFiles(before);
    }

    [Fact]
    public void CommitIdsPersistAcrossOwnersAndForkDescriptionsNeedNoModels() {
        CommitId saved;
        BranchId branch;
        using (Repository repository = Create()) {
            branch = repository.CreateRootBranchFromEvent("main", new Message(), parameters: NoRebase).Id;
            saved = repository.ReadCurrentCommit(branch).Commit.Id;
            Assert.Equal(saved, repository.ReadCommitInfo(saved).Id);
            Assert.Single(new HashSet<CommitId> { saved, repository.ReadFrames(branch)[0].Id });
        }
        using Repository reopened = Repository.OpenExisting(_root, new StateModelRegistry());
        Assert.Equal(saved, reopened.ReadCurrentCommit(branch).Commit.Id);
        Assert.Equal(saved.GetHashCode(), reopened.ReadCommitInfo(saved).Id.GetHashCode());
        BranchInfo copy = reopened.ForkBranch(saved, "copy");
        Assert.Equal(saved, reopened.ReadCurrentCommit(copy.Id).Commit.Id);
        using WorkingCopy checkout = reopened.OpenWorkingCopy(copy.Id);
        Assert.Null(checkout.State);
        Assert.ThrowsAny<ArgumentException>(() => reopened.LoadEvent(default));
        Assert.ThrowsAny<ArgumentException>(() => reopened.LoadState(default));
        Assert.ThrowsAny<ArgumentException>(() => reopened.GetPreviousState(default));
        Assert.ThrowsAny<ArgumentException>(() => reopened.ReadPair(saved, default));
        Assert.ThrowsAny<ArgumentException>(() => reopened.ReadPair(default, saved));
        Assert.ThrowsAny<ArgumentException>(() => reopened.ForkBranch(default, "invalid"));
    }

    [Fact]
    public void PhysicallyInterleavedBranchesFollowTheirOwnLogicalStateAncestors() {
        CommitId root, mainEvent, otherState, mainEvent2, mainState;
        using (Repository repository = Create()) {
            using (WorkingCopy checkout = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", new Alpha { Value = 1 }, parameters: NoRebase).Id)) {
                root = checkout.BaseCommitId;
                mainEvent = checkout.CommitEvent(new Message { Value = 2 }, parameters: NoRebase).Id;
            }
            repository.ForkBranch(root, "other");
            using (WorkingCopy other = repository.OpenWorkingCopy(BranchNamed(repository, "other"))) {
                otherState = other.CommitState(new Beta { Value = 8 }, parameters: NoRebase).Id;
            }
            using (WorkingCopy main = repository.OpenWorkingCopy(BranchNamed(repository, "main"))) {
                mainEvent2 = main.CommitEvent(new Message { Value = 3 }, parameters: NoRebase).Id;
                mainState = main.CommitState(parameters: NoRebase).Id;
            }
            Assert.Equal(mainEvent, Previous(repository, mainEvent2));
            Assert.Equal(mainEvent2, Previous(repository, mainState));
            Assert.Equal(root, repository.GetPreviousState(mainEvent2));
            StateCheckpoint mainView = Assert.IsType<StateCheckpoint>(repository.ReadCheckpoint(mainState));
            Assert.Equal(mainEvent2, mainView.PreviousEventCommit?.Id);
            Assert.Equal((byte)3, Assert.IsType<Message>(mainView.PreviousEvent).Value);
            StateCheckpoint siblingView = Assert.IsType<StateCheckpoint>(repository.ReadCheckpoint(otherState));
            Assert.Null(siblingView.PreviousEvent);
            Assert.Null(siblingView.PreviousEventCommit?.Id);
        }
        using (RepositoryResources resources = RepositoryResources.OpenReadOnlyExisting(_root)) {
            GraphRecordStore states = resources.GraphStore;
            foreach (CommitId address in new[] { otherState, mainEvent2, mainState }) {
                Assert.Equal(Describe(resources, root).Address, Snapshot(resources, address).SourceGraph);
            }
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        using (WorkingCopy main = reopened.OpenWorkingCopy(BranchNamed(reopened, "main"))) {
            Assert.Equal((byte)1, Assert.IsType<Alpha>(main.State).Value);
        }
        using WorkingCopy otherCold = reopened.OpenWorkingCopy(BranchNamed(reopened, "other"));
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
            BareValueWriter writer = new(bytes);
            writer.WriteByte(state.Value);
            if (childSchema is not null) writer.WriteVarUInt32(state.Child.Value);
            return new(bytes.WrittenSpan);
        }
        Dto Read(ref BareValueReader reader) => new(reader.ReadByte(), childSchema is null ? default : new ObjectId(reader.ReadVarUInt32()));
        void Visit(in Dto state, IStateReferenceVisitor visitor) {
            if (childSchema is not null) visitor.VisitDurable(state.Child, childSchema);
        }
        StatePreparationBinding<Dto> preparation = new(schema, Base,
            (in Dto prior, in Dto next) => new(prior != next, Base(next).Body));
        return new StateModelBinding<T, Dto>(preparation,
            [new StateReaderBinding<Dto>(schema, Read, (ref BareValueReader reader, in Dto prior) => Read(ref reader), Visit)],
            row => row.GetState<Dto>(), allocate,
            (T instance, in Dto state, ObjectReadTable objects) => hydrate(instance, state.Value,
                childSchema is null ? null : objects.ResolveDurable<IDurableObject>(state.Child)),
            (instance, context) => {
                onCapture?.Invoke();
                return new(value(instance), childSchema is null ? default : context.CaptureDurable(child(instance), childSchema));
            }, Visit);
    }

    private Repository Create(StateModelRegistry? models = null) => Repository.CreateNew(_root, models ?? Models());
    private SegmentStore OpenState() => SegmentStore.Open(Path.Combine(_root, "data"));
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
