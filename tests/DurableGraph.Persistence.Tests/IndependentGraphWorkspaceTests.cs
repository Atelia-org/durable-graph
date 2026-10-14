
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class IndependentGraphWorkspaceTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"durable-graph-independent-workspace-{Guid.NewGuid():N}");
    private RepositoryStorageFacade _repository;
    private GraphRecordStore Store => _repository.Store;
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1000000, 1);
    private const ulong InitialSequence = 0xFEDC_BA98_7654_3210UL;
    private static readonly DurableSchema DomainSchema = new("IndependentDomain", 1);
    public IndependentGraphWorkspaceTests() => _repository = RepositoryStorageFacade.CreateNew(_root);

    [Fact]
    public void SnapshotAndNextStateAreContentSiblingsAndColdSnapshotReadsOnlyItsClosure() {
        string text = new('s', 8);
        Node stable = new() { Value = 4, Text = text };
        Node changed = new() { Value = 2, Right = stable, Text = text };
        changed.Left = changed;
        Bob bob = new() { Value = 3 };
        World world = new() { Value = 1, Left = changed, Right = bob, Text = text };
        StateModelRegistry models = Models();
        BranchSaveSession workspace = new(_repository, "main", models);
        FrameAddress s0 = Commit(workspace, world);
        ObjectId worldId = workspace.RootId;
        ObjectId changedId = FindId(s0, 2, models);
        ObjectId stableId = FindId(s0, 4, models);
        ObjectId bobId = FindId(s0, 3, models);
        ObjectId stringId = Assert.Single(Decode(s0, models).Objects, row => row.Kind == ObjectStateKind.String).Id;
        StoredGraphRevision source = Store.ReadGraph(s0);
        changed.Value = 22;
        Event root = new() { Value = 100, Left = changed, Right = stable, Text = text };
        FrameAddress e1;
        ObjectId eventId;
        using (PreparedBranchSave save = workspace.Prepare(root, StoredGraphKind.Event, NoRebase)) {
            eventId = save.Business.Write.RootId;
            Assert.Equal(s0, save.Business.Write.SourceGraph);
            ObjectVersionRecord delta = Assert.Single(save.Business.Write.Records, row => row.ObjectId == changedId.Value);
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
            Assert.Equal(source.ObjectHeads[changedId.Value], delta.PriorAddress);
            Assert.DoesNotContain(save.Business.Write.Records, row => row.ObjectId == stableId.Value || row.ObjectId == stringId.Value);
            e1 = save.Commit().Business.Address;
        }
        Assert.Equal(s0, workspace.StateSource!.Address);
        Assert.Equal(worldId, workspace.RootId);
        Assert.Same(world, workspace.State);
        Assert.Same(bob, world.Right);
        StoredGraphRevision eventGraph = Store.ReadGraph(e1);
        Assert.Equal(s0, eventGraph.SourceGraph);
        Assert.Equal(source.ObjectHeads[stableId.Value], eventGraph.ObjectHeads[stableId.Value]);
        Assert.Equal(source.ObjectHeads[stringId.Value], eventGraph.ObjectHeads[stringId.Value]);
        Assert.DoesNotContain(worldId.Value, eventGraph.ObjectHeads.Keys);
        Assert.DoesNotContain(bobId.Value, eventGraph.ObjectHeads.Keys);

        changed.Value = 23;
        FrameAddress s1 = Commit(workspace);
        StoredGraphRevision state = Store.ReadGraph(s1);
        Assert.Equal(e1, state.Parent); // History order retains the Event.
        Assert.Equal(s0, state.SourceGraph); // Both contents compare with the last accepted State.
        ObjectVersionRecord stateDelta = Assert.Single(Records(s1));
        Assert.Equal(changedId.Value, stateDelta.ObjectId);
        Assert.Equal(ObjectVersionKind.Delta, stateDelta.Kind);
        Assert.Equal(source.ObjectHeads[changedId.Value], stateDelta.PriorAddress);
        Assert.Contains(bobId.Value, state.ObjectHeads.Keys);
        Assert.DoesNotContain(eventId.Value, state.ObjectHeads.Keys);

        ReopenReadOnly();
        Dictionary<string, int> callbacks = [];
        StateModelRegistry coldModels = Models(onRead: name => callbacks[name] = callbacks.GetValueOrDefault(name) + 1);
        Dictionary<string, byte[]> before = Files();
        Event restored = Assert.IsType<Event>(BusinessPersistenceAdapter.ReadRoot(Store, e1, coldModels));
        Assert.Equal((byte)22, restored.Left!.Value);
        Assert.Same(restored.Left, restored.Left.Left);
        Assert.Same(restored.Right, restored.Left.Right);
        Assert.Same(restored.Text, restored.Left.Text);
        Assert.Same(restored.Text, restored.Right!.Text);
        Assert.DoesNotContain(nameof(World), callbacks.Keys);
        Assert.DoesNotContain(nameof(Bob), callbacks.Keys);
        Assert.True(callbacks[nameof(Event)] > 0);
        Assert.True(callbacks[nameof(Node)] > 0);
        AssertFilesEqual(before);
        World restoredState = Assert.IsType<World>(BusinessPersistenceAdapter.ReadRoot(Store, s1, coldModels));
        Assert.Equal((byte)23, restoredState.Left!.Value);
        Assert.IsType<Bob>(restoredState.Right);
        Assert.Same(restoredState.Left, restoredState.Left.Left);
        AssertFilesEqual(before);
    }

    [Fact]
    public void ReplacementRootInstallsOnlyAfterPublicationAndUsesTheFrozenCandidate() {
        Node child = new() { Value = 2 };
        World original = new() { Value = 1, Left = child };
        StateModelRegistry models = Models();
        BranchSaveSession workspace = new(_repository, "main", models);
        FrameAddress first = Commit(workspace, original);
        ObjectId oldRootId = workspace.RootId;
        ObjectId childId = FindId(first, 2, models);
        World replacement = new() { Value = 10, Left = child };
        using (PreparedBranchSave abandoned = workspace.Prepare(replacement, parameters: NoRebase)) {
            Assert.NotEqual(oldRootId, abandoned.Business.Write.RootId);
            Assert.Same(original, workspace.State);
            Assert.Equal(first, _repository.GetHead("main"));
        }
        Assert.Same(original, workspace.State);
        Assert.Equal(oldRootId, workspace.RootId);
        _repository.SaveStageObserver = point => {
            if (point != RepositorySaveStage.BeforePublication) { return; }
            Assert.Same(original, workspace.State);
            Assert.Equal(first, workspace.StateSource!.Address);
            Assert.Equal(first, _repository.GetHead("main"));
        };
        FrameAddress published;
        using (PreparedBranchSave save = workspace.Prepare(replacement, parameters: NoRebase)) {
            Assert.Equal(first, save.Business.Write.SourceGraph);
            Assert.Contains(oldRootId.Value, save.Business.Write.RemovedIds);
            Assert.DoesNotContain(save.Business.Write.Records, row => row.ObjectId == childId.Value);
            replacement.Value = 11;
            published = save.Commit().Business.Address;
            Assert.Equal(save.Business.Write.RootId, workspace.RootId);
            Assert.Throws<InvalidOperationException>(() => save.Commit());
        }
        _repository.SaveStageObserver = null;
        Assert.Same(replacement, workspace.State);
        Assert.Same(child, ((World)workspace.State!).Left);
        Assert.Equal((byte)10, Decode(published, models).GetRequired(workspace.RootId).GetState<State>().Value);
        using PreparedBranchSave later = workspace.Prepare(workspace.State!, parameters: NoRebase);
        ObjectVersionRecord delta = Assert.Single(later.Business.Write.Records);
        Assert.Equal(workspace.RootId.Value, delta.ObjectId);
        Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
        Assert.Equal(Store.ReadGraph(published).ObjectHeads[workspace.RootId.Value], delta.PriorAddress);
    }

    [Fact]
    public void PendingCandidateAndCaptureErrorsNeverReplaceTheStateBaseline() {
        bool fail = false;
        Action? reenter = null;
        World world = new() { Value = 1, Left = new Node { Value = 2 } };
        StateModelRegistry models = Models(onCapture: _ => {
            reenter?.Invoke();
            if (fail) throw new InvalidDataException("test capture failure");
        });
        BranchSaveSession workspace = new(_repository, "main", models);
        using (PreparedBranchSave firstEvent = workspace.Prepare(new Event { Left = world.Left }, StoredGraphKind.Event, NoRebase)) {
            Assert.Null(firstEvent.Business.Write.SourceGraph);
            Assert.NotEmpty(firstEvent.Business.Write.Records);
            Assert.All(firstEvent.Business.Write.Records, row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
            Assert.Throws<InvalidOperationException>(() => workspace.Prepare(world));
            Assert.Throws<InvalidOperationException>(() => workspace.Prepare(new Event(), StoredGraphKind.Event));
        }
        Assert.Null(workspace.State);
        Assert.Null(workspace.StateSource);
        Assert.True(workspace.RootId.IsNull);
        FrameAddress s0 = Commit(workspace, world);
        ObjectId oldRootId = workspace.RootId;
        Event root = new() { Left = world.Left };
        using (PreparedBranchSave pending = workspace.Prepare(root, StoredGraphKind.Event, NoRebase)) {
            Assert.Throws<InvalidOperationException>(() => workspace.Prepare(world));
            Assert.Throws<InvalidOperationException>(() => workspace.Prepare(new World()));
            Assert.Throws<InvalidOperationException>(() => workspace.Prepare(root, StoredGraphKind.Event));
        }
        Assert.ThrowsAny<ArgumentException>(() => workspace.Prepare(new Unknown(), StoredGraphKind.Event));
        Assert.ThrowsAny<ArgumentException>(() => workspace.Prepare(new DerivedWorld()));
        long frames = Store.AppendedFrames;
        fail = true;
        Assert.Throws<InvalidDataException>(() => workspace.Prepare(root, StoredGraphKind.Event));
        Assert.Throws<InvalidDataException>(() => workspace.Prepare(world));
        Assert.Throws<InvalidDataException>(() => workspace.Prepare(new World()));
        fail = false;
        reenter = () => Assert.Throws<InvalidOperationException>(() => workspace.Prepare(root, StoredGraphKind.Event));
        using (PreparedBranchSave save = workspace.Prepare(root, StoredGraphKind.Event)) {
            Assert.Equal(s0, save.Business.Write.SourceGraph);
        }
        reenter = null;
        Assert.Equal(frames, Store.AppendedFrames);
        Assert.Same(world, workspace.State);
        Assert.Equal(s0, workspace.StateSource!.Address);
        Assert.Equal(oldRootId, workspace.RootId);
        using PreparedBranchSave unchanged = workspace.Prepare(world);
        Assert.Empty(unchanged.Business.Write.Records);
        Assert.Empty(unchanged.Business.Write.RemovedIds);
    }

    [Fact]
    public void SnapshotOnlyInstancesDoNotEnterStateBindingsAndDiscardCanConsumeIds() {
        BranchSaveSession workspace = new(_repository, "main", Models());
        World world = new() { Value = 1 };
        Commit(workspace, world);
        Node eventOnly = new() { Value = 2 };
        Event root = new() { Left = eventOnly };
        uint lastSnapshotId;
        using (PreparedBranchSave save = workspace.Prepare(root, StoredGraphKind.Event, NoRebase)) {
            lastSnapshotId = save.Business.Write.Records.Max(row => row.ObjectId);
            save.Commit();
        }
        using (PreparedBranchSave abandoned = workspace.Prepare(root, StoredGraphKind.Event, NoRebase)) {
            Assert.True(abandoned.Business.Write.RootId.Value > lastSnapshotId);
            lastSnapshotId = abandoned.Business.Write.Records.Max(row => row.ObjectId);
        }
        world.Left = eventOnly;
        using PreparedBranchSave state = workspace.Prepare(world, parameters: NoRebase);
        ObjectVersionRecord inserted = Assert.Single(state.Business.Write.Records, row => row.Kind == ObjectVersionKind.Base);
        Assert.True(inserted.ObjectId > lastSnapshotId);
    }

    [Fact]
    public void SnapshotRebaseKeepsSelectedLocalBaseAtItsActualBatchHead() {
        Node child = new() { Value = 2 };
        World world = new() { Value = 1, Left = child };
        StateModelRegistry models = Models();
        BranchSaveSession workspace = new(_repository, "main", models);
        FrameAddress s0 = Commit(workspace, world);
        ObjectId childId = FindId(s0, 2, models);
        child.Value = 3;
        FrameAddress s1 = Commit(workspace);
        Assert.Equal(ObjectVersionKind.Delta, Assert.Single(Records(s1)).Kind);
        using PreparedBranchSave snapshot = workspace.Prepare(new Event { Left = child }, StoredGraphKind.Event, new(1, 100));
        ObjectVersionRecord rebased = Assert.Single(snapshot.Business.Write.Records, row => row.ObjectId == childId.Value);
        Assert.Equal(ObjectVersionKind.Base, rebased.Kind);
        Assert.Equal(s1, snapshot.Business.Write.SourceGraph);
        StoredGraphRevision graph = snapshot.Commit().Business;
        Assert.Equal(graph.OwnedBatch, graph.ObjectHeads[childId.Value]);
        Assert.Equal(ObjectVersionKind.Base, Store.ReadObjectChain(graph.Address, childId.Value).Entries[0].Record.Kind);
        Assert.Equal(s1, workspace.StateSource!.Address);
    }

    [Fact]
    public void SavingUpgradedSnapshotDoesNotClearTheStateRewriteObligation() {
        World initial = new() { Value = 1, Left = new Node { Value = 2 } };
        StateModelRegistry oldModels = Models();
        BranchSaveSession old = new(_repository, "main", oldModels);
        FrameAddress s0 = Commit(old, initial);
        ObjectId childId = FindId(s0, 2, oldModels);
        StateModelRegistry models = Models(upgradeNode: true);
        BranchSaveSession workspace = new(_repository, "main", models);
        Assert.Equal((byte)12, ((World)workspace.State!).Left!.Value);
        using (PreparedBranchSave snapshot = workspace.Prepare(new Event { Left = ((World)workspace.State!).Left }, StoredGraphKind.Event, NoRebase)) {
            Assert.Equal(ObjectVersionKind.Base, Assert.Single(snapshot.Business.Write.Records, row => row.ObjectId == childId.Value).Kind);
            snapshot.Commit();
        }
        Assert.Equal(s0, workspace.StateSource!.Address);
        FrameAddress s1;
        using (PreparedBranchSave state = workspace.Prepare(workspace.State!, parameters: NoRebase)) {
            Assert.Equal(s0, state.Business.Write.SourceGraph);
            ObjectVersionRecord rewritten = Assert.Single(state.Business.Write.Records);
            Assert.Equal(ObjectVersionKind.Base, rewritten.Kind);
            Assert.Equal(childId.Value, rewritten.ObjectId);
            s1 = state.Commit().Business.Address;
        }
        using PreparedBranchSave unchanged = workspace.Prepare(workspace.State!, parameters: NoRebase);
        Assert.Equal(s1, unchanged.Business.Write.SourceGraph);
        Assert.Empty(unchanged.Business.Write.Records);
    }

    private abstract class Domain : IDurableObject {
        internal Domain? Left;
        internal Domain? Right;
        internal string? Text;
        internal byte Value;
        internal ulong Sequence = InitialSequence;
    }
    private class World : Domain { }
    private sealed class DerivedWorld : World { }
    private sealed class Node : Domain { }
    private sealed class Bob : Domain { }
    private sealed class Event : Domain { }
    private sealed class Unknown : Domain { }
    private readonly record struct State(ObjectId Left, ObjectId Right, ObjectId Text, byte Value, ulong Sequence);

    private static DurableSchema Schema(string name, int version = 1) => new($"Independent{name}", version,
        [new(1, TypeTag.ObjectReference, DomainSchema.SchemaId), new(2, TypeTag.ObjectReference, DomainSchema.SchemaId),
         new(3, TypeTag.String), new(4, TypeTag.Byte), new(5, TypeTag.UInt64)], DomainSchema);

    private static StateModelRegistry Models(Action<Domain>? onCapture = null, Action<string>? onRead = null, bool upgradeNode = false) {
        StateModelRegistry registry = new();
        registry.Register(Model<World>(nameof(World), static () => new World(), onCapture, onRead));
        registry.Register(Model<Node>(nameof(Node), static () => new Node(), onCapture, onRead, upgradeNode));
        registry.Register(Model<Bob>(nameof(Bob), static () => new Bob(), onCapture, onRead));
        registry.Register(Model<Event>(nameof(Event), static () => new Event(), onCapture, onRead));
        return registry;
    }

    private static StateModelBinding Model<T>(string name, Func<T> allocate, Action<Domain>? onCapture,
        Action<string>? onRead, bool upgrade = false) where T : Domain {
        DurableSchema original = Schema(name);
        DurableSchema current = upgrade ? Schema(name, 2) : original;
        StatePreparationBinding<State> preparation = new(current,
            static (in State state) => Base(state), static (in State prior, in State next) => Delta(prior, next));
        StateReaderBinding Reader(DurableSchema schema) => new StateReaderBinding<State>(schema,
            (ref BareValueReader input) => { onRead?.Invoke(name); return Read(ref input); },
            (ref BareValueReader input, in State prior) => { onRead?.Invoke(name); return Apply(ref input, prior); }, Visit);
        StateReaderBinding[] readers = upgrade ? [Reader(original), Reader(current)] : [Reader(current)];
        return new StateModelBinding<T, State>(preparation, readers,
            row => {
                onRead?.Invoke(name);
                State state = row.GetState<State>();
                return upgrade && row.Schema!.Version == 1 ? state with { Value = (byte)(state.Value + 10) } : state;
            },
            () => { onRead?.Invoke(name); return allocate(); },
            (T domain, in State state, ObjectReadTable objects) => {
                onRead?.Invoke(name);
                domain.Left = objects.ResolveDurable<Domain>(state.Left);
                domain.Right = objects.ResolveDurable<Domain>(state.Right);
                domain.Text = objects.ResolveString(state.Text);
                domain.Value = state.Value;
                domain.Sequence = state.Sequence;
            },
            (domain, context) => {
                onCapture?.Invoke(domain);
                return new(context.CaptureDurable(domain.Left, DomainSchema.SchemaId), context.CaptureDurable(domain.Right, DomainSchema.SchemaId),
                    context.CaptureString(domain.Text), domain.Value, domain.Sequence);
            }, Visit);
    }

    private static void Visit(in State state, IStateReferenceVisitor visitor) {
        visitor.VisitDurable(state.Left, DomainSchema.SchemaId);
        visitor.VisitDurable(state.Right, DomainSchema.SchemaId);
        visitor.VisitString(state.Text);
    }

    private static PreparedBaseBody Base(State state) {
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        writer.WriteVarUInt32(state.Left.Value);
        writer.WriteVarUInt32(state.Right.Value);
        writer.WriteVarUInt32(state.Text.Value);
        writer.WriteByte(state.Value);
        writer.WriteVarUInt64(state.Sequence);
        return new(bytes.WrittenSpan);
    }

    private static State Read(ref BareValueReader input) => new(new(input.ReadVarUInt32()), new(input.ReadVarUInt32()),
        new(input.ReadVarUInt32()), input.ReadByte(), input.ReadVarUInt64());

    private static PreparedDeltaBody Delta(State prior, State next) {
        byte mask = (byte)((prior.Left != next.Left ? 1 : 0) | (prior.Right != next.Right ? 2 : 0) |
            (prior.Text != next.Text ? 4 : 0) | (prior.Value != next.Value ? 8 : 0) | (prior.Sequence != next.Sequence ? 16 : 0));
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        writer.WriteByte(mask);
        if ((mask & 1) != 0) writer.WriteVarUInt32(next.Left.Value);
        if ((mask & 2) != 0) writer.WriteVarUInt32(next.Right.Value);
        if ((mask & 4) != 0) writer.WriteVarUInt32(next.Text.Value);
        if ((mask & 8) != 0) writer.WriteByte(next.Value);
        if ((mask & 16) != 0) writer.WriteVarUInt64(next.Sequence);
        return new(mask != 0, bytes.WrittenSpan);
    }

    private static State Apply(ref BareValueReader input, State prior) {
        byte mask = input.ReadByte();
        if (mask == 0 || mask > 31) throw new InvalidDataException("Invalid test bitmap.");
        return new((mask & 1) != 0 ? new(input.ReadVarUInt32()) : prior.Left,
            (mask & 2) != 0 ? new(input.ReadVarUInt32()) : prior.Right,
            (mask & 4) != 0 ? new(input.ReadVarUInt32()) : prior.Text,
            (mask & 8) != 0 ? input.ReadByte() : prior.Value,
            (mask & 16) != 0 ? input.ReadVarUInt64() : prior.Sequence);
    }

    private FrameAddress Commit(BranchSaveSession workspace, IDurableObject? root = null) {
        using PreparedBranchSave save = workspace.Prepare(root ?? workspace.State!, parameters: NoRebase);
        return save.Commit().Business.Address;
    }
    private DecodedRevision Decode(FrameAddress address, StateModelRegistry models) => DecodedRevision.ReadStoredGraph(Store, address, models);
    private IReadOnlyList<ObjectVersionRecord> Records(FrameAddress address) =>
        Store.ReadGraph(address).OwnedBatch is { } batch ? Store.ReadBatch(batch).Records : [];
    private ObjectId FindId(FrameAddress address, byte value, StateModelRegistry models) =>
        Assert.Single(Decode(address, models).Objects, row => row.Kind == ObjectStateKind.Durable && row.GetState<State>().Value == value).Id;
    private void ReopenReadOnly() {
        _repository.Dispose();
        _repository = RepositoryStorageFacade.OpenReadOnlyExisting(_root);
    }
    private Dictionary<string, byte[]> Files() => Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
        .ToDictionary(static path => path, static path => File.ReadAllBytes(path));
    private void AssertFilesEqual(Dictionary<string, byte[]> before) {
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order());
        foreach ((string path, byte[] bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
    }
    public void Dispose() {
        _repository.Dispose();
        SharedReadModel.DeleteFixture(_root, "durable-graph-independent-workspace-");
    }
}
