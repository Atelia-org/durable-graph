using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class IndependentGraphWorkspaceTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"durable-graph-independent-workspace-{Guid.NewGuid():N}");
    private IRbfFile _file;
    private SegmentStore _segments;
    private SchemaStore _schemas;
    private StateRevisionStore _store;
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1000000, 1);
    private const ulong InitialSequence = 0xFEDC_BA98_7654_3210UL;
    private static readonly DurableSchema DomainSchema = new("IndependentDomain", 1);

    public IndependentGraphWorkspaceTests() {
        Directory.CreateDirectory(_root);
        _file = RbfFile.CreateNew(Path.Combine(_root, "schemas.rbf"));
        _schemas = new(_file);
        _segments = SegmentStore.CreateNew(Path.Combine(_root, "state"), new() { NewStoreLayout = RbfSegmentStoreLayout.Flat });
        _store = new(_segments);
    }

    [Fact]
    public void SnapshotAndNextStateAreSiblingsAndColdSnapshotReadsOnlyItsClosure() {
        string text = new('s', 8);
        Node stable = new() { Value = 4, Text = text };
        Node changed = new() { Value = 2, Right = stable, Text = text };
        changed.Left = changed;
        Bob bob = new() { Value = 3 };
        World world = new() { Value = 1, Left = changed, Right = bob, Text = text };
        StateModelRegistry models = Models();
        WorldWorkspace workspace = WorldWorkspace.Create(_store, _schemas, world, models);
        FrameAddress s0 = Commit(workspace);
        ObjectId worldId = workspace.WorldId;
        ObjectId changedId = FindId(s0, 2, models);
        ObjectId stableId = FindId(s0, 4, models);
        ObjectId bobId = FindId(s0, 3, models);
        ObjectId stringId = Assert.Single(Decode(s0, models).Objects, row => row.Kind == ObjectStateKind.String).Id;
        changed.Value = 22;
        Event root = new() { Value = 100, Left = changed, Right = stable, Text = text };
        FrameAddress e1;
        ObjectId eventId;
        using (PreparedWorldSave save = workspace.StageSnapshot(root, NoRebase)) {
            Assert.True(save.IsIndependentSnapshot);
            eventId = save.RootId;
            Assert.Equal(s0, save.Revision.ParentRevisionAddress);
            Assert.Equal(ObjectHeadMapKind.Base, save.Revision.ObjectHeadMapKind);
            Assert.Empty(save.Revision.RemovedObjectIds);
            Assert.Equal(s0, save.Revision.ExternalObjectHeads[stableId.Value]);
            Assert.Equal(s0, save.Revision.ExternalObjectHeads[stringId.Value]);
            ObjectVersionRecord delta = Assert.Single(save.Revision.LocalObjects, row => row.ObjectId == changedId.Value);
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
            Assert.Equal(s0, delta.PriorAddress);
            Assert.Throws<InvalidOperationException>(() => save.Install());
            e1 = _store.AppendDurably(save.Revision);
            Assert.Throws<InvalidOperationException>(() => save.PrepareInstall(e1));
        }
        Assert.Equal(s0, workspace.ParentRevisionAddress);
        Assert.Equal(worldId, workspace.WorldId);
        Assert.Same(world, workspace.World);
        Assert.Same(bob, world.Right);
        Assert.DoesNotContain(worldId.Value, _store.ReadLiveObjectHeadMap(e1).Keys);
        Assert.DoesNotContain(bobId.Value, _store.ReadLiveObjectHeadMap(e1).Keys);

        changed.Value = 23;
        FrameAddress s1 = Commit(workspace);
        StateRevision revision = _store.Read(s1);
        Assert.Equal(s0, revision.ParentRevisionAddress);
        ObjectVersionRecord stateDelta = Assert.Single(revision.LocalObjects);
        Assert.Equal(changedId.Value, stateDelta.ObjectId);
        Assert.Equal(ObjectVersionKind.Delta, stateDelta.Kind);
        Assert.Equal(s0, stateDelta.PriorAddress);
        Assert.Contains(bobId.Value, _store.ReadLiveObjectHeadMap(s1).Keys);
        Assert.DoesNotContain(eventId.Value, _store.ReadLiveObjectHeadMap(s1).Keys);

        ReopenReadOnly();
        Dictionary<string, int> callbacks = [];
        StateModelRegistry coldModels = Models(onRead: name => callbacks[name] = callbacks.GetValueOrDefault(name) + 1);
        Dictionary<string, byte[]> before = Files();
        Event restored = GraphReader.Read<Event>(_store, _schemas, e1, eventId, coldModels.Snapshot(_schemas)).Root;
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
        World restoredState = GraphReader.Read<World>(_store, _schemas, s1, worldId, coldModels.Snapshot(_schemas)).Root;
        Assert.Equal((byte)23, restoredState.Left!.Value);
        Assert.IsType<Bob>(restoredState.Right);
        Assert.Same(restoredState.Left, restoredState.Left.Left);
    }

    [Fact]
    public void ReplacementRootInstallsOnlyAfterPublicationAndUsesTheFrozenCandidate() {
        Node child = new() { Value = 2 };
        World original = new() { Value = 1, Left = child };
        StateModelRegistry models = Models();
        WorldWorkspace workspace = WorldWorkspace.Create(_store, _schemas, original, models);
        FrameAddress first = Commit(workspace);
        ObjectId oldRootId = workspace.WorldId;
        ObjectId childId = FindId(first, 2, models);
        World replacement = new() { Value = 10, Left = child };
        using (PreparedWorldSave abandoned = workspace.Stage(replacement, NoRebase)) {
            Assert.NotEqual(oldRootId, abandoned.RootId);
            Assert.Same(original, workspace.World);
            FrameAddress unpublished = _store.AppendDurably(abandoned.Revision);
            abandoned.PrepareInstall(unpublished);
            // The outside publisher did not publish this appended revision.
            Assert.Same(original, workspace.World);
            Assert.Equal(first, workspace.ParentRevisionAddress);
        }
        Assert.Same(original, workspace.World);
        Assert.Equal(oldRootId, workspace.WorldId);
        FrameAddress published;
        using (PreparedWorldSave save = workspace.Stage(replacement, NoRebase)) {
            Assert.False(save.IsIndependentSnapshot);
            Assert.Equal(first, save.Revision.ParentRevisionAddress);
            Assert.Contains(oldRootId.Value, save.Revision.RemovedObjectIds);
            Assert.DoesNotContain(childId.Value, save.Revision.LocalObjectIds);
            replacement.Value = 11;
            published = _store.AppendDurably(save.Revision);
            save.PrepareInstall(published);
            Assert.Same(original, workspace.World);
            save.Install();
            Assert.Equal(save.RootId, workspace.WorldId);
            Assert.Throws<InvalidOperationException>(() => save.Install());
        }
        Assert.Same(replacement, workspace.World);
        Assert.Same(child, ((World)workspace.World!).Left);
        Assert.Equal((byte)10, Decode(published, models).GetRequired(workspace.WorldId).GetState<State>().Value);
        using PreparedWorldSave later = workspace.Stage(NoRebase);
        ObjectVersionRecord delta = Assert.Single(later.Revision.LocalObjects);
        Assert.Equal(workspace.WorldId.Value, delta.ObjectId);
        Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
        Assert.Equal(published, delta.PriorAddress);
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
        WorldWorkspace workspace = WorldWorkspace.Create(_store, _schemas, world, models);
        using (PreparedWorldSave firstEvent = workspace.StageSnapshot(new Event { Left = world.Left }, NoRebase)) {
            Assert.True(firstEvent.IsIndependentSnapshot);
            Assert.Null(firstEvent.Revision.ParentRevisionAddress);
            Assert.Equal(ObjectHeadMapKind.Base, firstEvent.Revision.ObjectHeadMapKind);
            Assert.NotEmpty(firstEvent.Revision.LocalObjects);
            Assert.All(firstEvent.Revision.LocalObjects, row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
            Assert.Empty(firstEvent.Revision.ExternalObjectHeads);
            Assert.Throws<InvalidOperationException>(firstEvent.Install);
            Assert.Throws<InvalidOperationException>(() => workspace.Stage(NoRebase));
            Assert.Throws<InvalidOperationException>(() => workspace.StageSnapshot(new Event(), NoRebase));
        }
        Assert.Same(world, workspace.World);
        Assert.Null(workspace.ParentRevisionAddress);
        Assert.Equal(new ObjectId(0), workspace.WorldId);
        FrameAddress s0 = Commit(workspace);
        ObjectId oldRootId = workspace.WorldId;
        Event root = new() { Left = world.Left };
        using (PreparedWorldSave pending = workspace.StageSnapshot(root, NoRebase)) {
            Assert.Throws<InvalidOperationException>(() => workspace.Stage(NoRebase));
            Assert.Throws<InvalidOperationException>(() => workspace.Stage(new World(), NoRebase));
            Assert.Throws<InvalidOperationException>(() => workspace.StageSnapshot(root, NoRebase));
        }
        Assert.ThrowsAny<ArgumentException>(() => workspace.StageSnapshot(new Unknown(), NoRebase));
        Assert.ThrowsAny<ArgumentException>(() => workspace.Stage(new DerivedWorld(), NoRebase));
        fail = true;
        Assert.Throws<InvalidDataException>(() => workspace.StageSnapshot(root, NoRebase));
        Assert.Throws<InvalidDataException>(() => workspace.Stage(NoRebase));
        Assert.Throws<InvalidDataException>(() => workspace.Stage(new World(), NoRebase));
        fail = false;
        reenter = () => Assert.Throws<InvalidOperationException>(() => workspace.StageSnapshot(root, NoRebase));
        using (PreparedWorldSave save = workspace.StageSnapshot(root, NoRebase)) {
            Assert.Equal(s0, save.Revision.ParentRevisionAddress);
        }
        reenter = null;
        Assert.Same(world, workspace.World);
        Assert.Equal(s0, workspace.ParentRevisionAddress);
        Assert.Equal(oldRootId, workspace.WorldId);
        using PreparedWorldSave unchanged = workspace.Stage(NoRebase);
        Assert.Empty(unchanged.Revision.LocalObjects);
        Assert.Empty(unchanged.Revision.RemovedObjectIds);
    }

    [Fact]
    public void SnapshotOnlyInstancesDoNotEnterStateBindingsAndFailureCanConsumeIds() {
        StateModelRegistry models = Models();
        World world = new() { Value = 1 };
        WorldWorkspace workspace = WorldWorkspace.Create(_store, _schemas, world, models);
        Commit(workspace);
        Node eventOnly = new() { Value = 2 };
        Event root = new() { Left = eventOnly };
        uint lastSnapshotId;
        using (PreparedWorldSave save = workspace.StageSnapshot(root, NoRebase)) {
            lastSnapshotId = save.Revision.LocalObjectIds.Max();
            _store.AppendDurably(save.Revision);
        }
        using (PreparedWorldSave abandoned = workspace.StageSnapshot(root, NoRebase)) {
            Assert.True(abandoned.RootId.Value > lastSnapshotId);
            lastSnapshotId = abandoned.Revision.LocalObjectIds.Max();
        }
        world.Left = eventOnly;
        using PreparedWorldSave state = workspace.Stage(NoRebase);
        ObjectVersionRecord inserted = Assert.Single(state.Revision.LocalObjects, row => row.Kind == ObjectVersionKind.Base);
        Assert.True(inserted.ObjectId > lastSnapshotId);
    }

    [Fact]
    public void SnapshotRebaseKeepsSelectedLocalBaseAndDoesNotAlsoAddAnExternalHead() {
        Node child = new() { Value = 2 };
        World world = new() { Value = 1, Left = child };
        StateModelRegistry models = Models();
        WorldWorkspace workspace = WorldWorkspace.Create(_store, _schemas, world, models);
        FrameAddress s0 = Commit(workspace);
        ObjectId childId = FindId(s0, 2, models);
        child.Value = 3;
        FrameAddress s1 = Commit(workspace);
        Assert.Equal(ObjectVersionKind.Delta, Assert.Single(_store.Read(s1).LocalObjects).Kind);
        using PreparedWorldSave snapshot = workspace.StageSnapshot(new Event { Left = child }, new(1, 100));
        ObjectVersionRecord rebased = Assert.Single(snapshot.Revision.LocalObjects, row => row.ObjectId == childId.Value);
        Assert.Equal(ObjectVersionKind.Base, rebased.Kind);
        Assert.DoesNotContain(childId.Value, snapshot.Revision.ExternalObjectHeads.Keys);
        Assert.Equal(s1, snapshot.Revision.ParentRevisionAddress);
        FrameAddress address = _store.AppendDurably(snapshot.Revision);
        Assert.Equal(address, _store.ReadLiveObjectHeadMap(address)[childId.Value]);
    }

    [Fact]
    public void SavingUpgradedSnapshotDoesNotClearTheStateRewriteObligation() {
        World initial = new() { Value = 1, Left = new Node { Value = 2 } };
        StateModelRegistry oldModels = Models();
        WorldWorkspace old = WorldWorkspace.Create(_store, _schemas, initial, oldModels);
        FrameAddress s0 = Commit(old);
        ObjectId childId = FindId(s0, 2, oldModels);
        StateModelRegistry models = Models(upgradeNode: true);
        WorldWorkspace workspace = WorldWorkspace.Load(_store, _schemas, s0, old.WorldId, models);
        Assert.Equal((byte)12, ((World)workspace.World!).Left!.Value);
        using (PreparedWorldSave snapshot = workspace.StageSnapshot(new Event { Left = ((World)workspace.World!).Left }, NoRebase)) {
            Assert.Equal(ObjectVersionKind.Base, Assert.Single(snapshot.Revision.LocalObjects, row => row.ObjectId == childId.Value).Kind);
            _store.AppendDurably(snapshot.Revision);
        }
        Assert.Equal(s0, workspace.ParentRevisionAddress);
        FrameAddress s1;
        using (PreparedWorldSave state = workspace.Stage(NoRebase)) {
            Assert.Equal(s0, state.Revision.ParentRevisionAddress);
            Assert.Equal(ObjectVersionKind.Base, Assert.Single(state.Revision.LocalObjects).Kind);
            Assert.Equal(childId.Value, Assert.Single(state.Revision.LocalObjects).ObjectId);
            s1 = _store.AppendDurably(state.Revision);
            state.PrepareInstall(s1);
            state.Install();
        }
        using PreparedWorldSave unchanged = workspace.Stage(NoRebase);
        Assert.Equal(s1, unchanged.Revision.ParentRevisionAddress);
        Assert.Empty(unchanged.Revision.LocalObjects);
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
        CapturedStatePreparation<State> preparation = new(current,
            static (in State state) => Base(state), static (in State prior, in State next) => Delta(prior, next));
        StateReaderBinding Reader(DurableSchema schema) => new StateReaderBinding<State>(schema,
            (ref BinaryPayloadReader input) => { onRead?.Invoke(name); return Read(ref input); },
            (ref BinaryPayloadReader input, in State prior) => { onRead?.Invoke(name); return Apply(ref input, prior); }, Visit);
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
        BinaryPayloadWriter writer = new(bytes);
        writer.WriteUInt32(state.Left.Value);
        writer.WriteUInt32(state.Right.Value);
        writer.WriteUInt32(state.Text.Value);
        writer.WriteByte(state.Value);
        writer.WriteUInt64(state.Sequence);
        return new(bytes.WrittenSpan);
    }

    private static State Read(ref BinaryPayloadReader input) => new(new(input.ReadUInt32()), new(input.ReadUInt32()),
        new(input.ReadUInt32()), input.ReadByte(), input.ReadUInt64());

    private static PreparedDeltaBody Delta(State prior, State next) {
        byte mask = (byte)((prior.Left != next.Left ? 1 : 0) | (prior.Right != next.Right ? 2 : 0) |
            (prior.Text != next.Text ? 4 : 0) | (prior.Value != next.Value ? 8 : 0) | (prior.Sequence != next.Sequence ? 16 : 0));
        ArrayBufferWriter<byte> bytes = new();
        BinaryPayloadWriter writer = new(bytes);
        writer.WriteByte(mask);
        if ((mask & 1) != 0) writer.WriteUInt32(next.Left.Value);
        if ((mask & 2) != 0) writer.WriteUInt32(next.Right.Value);
        if ((mask & 4) != 0) writer.WriteUInt32(next.Text.Value);
        if ((mask & 8) != 0) writer.WriteByte(next.Value);
        if ((mask & 16) != 0) writer.WriteUInt64(next.Sequence);
        return new(mask != 0, bytes.WrittenSpan);
    }

    private static State Apply(ref BinaryPayloadReader input, State prior) {
        byte mask = input.ReadByte();
        if (mask == 0 || mask > 31) throw new InvalidDataException("Invalid test bitmap.");
        return new((mask & 1) != 0 ? new(input.ReadUInt32()) : prior.Left,
            (mask & 2) != 0 ? new(input.ReadUInt32()) : prior.Right,
            (mask & 4) != 0 ? new(input.ReadUInt32()) : prior.Text,
            (mask & 8) != 0 ? input.ReadByte() : prior.Value,
            (mask & 16) != 0 ? input.ReadUInt64() : prior.Sequence);
    }

    private FrameAddress Commit(WorldWorkspace workspace) {
        using PreparedWorldSave save = workspace.Stage(NoRebase);
        FrameAddress address = _store.AppendDurably(save.Revision);
        save.PrepareInstall(address);
        // This fixture explicitly simulates the outside publisher confirming publication.
        save.Install();
        return address;
    }

    private DecodedRevision Decode(FrameAddress address, StateModelRegistry models) =>
        RevisionDecoder.ReadSnapshot(_store, _schemas, address, models.Snapshot(_schemas));

    private ObjectId FindId(FrameAddress address, byte value, StateModelRegistry models) =>
        Assert.Single(Decode(address, models).Objects, row => row.Kind == ObjectStateKind.Durable && row.GetState<State>().Value == value).Id;

    private void ReopenReadOnly() {
        _store.Dispose();
        _file.Dispose();
        _segments.Dispose();
        _file = RbfFile.OpenReadOnlyExisting(Path.Combine(_root, "schemas.rbf"));
        _schemas = new(_file, readOnly: true);
        _segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(_root, "state"));
        _store = new(_segments);
    }

    private Dictionary<string, byte[]> Files() => Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
        .ToDictionary(static path => path, static path => File.ReadAllBytes(path));

    private void AssertFilesEqual(Dictionary<string, byte[]> before) {
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order());
        foreach ((string path, byte[] bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    public void Dispose() {
        _store.Dispose();
        _file.Dispose();
        _segments.Dispose();
        string resolved = Path.GetFullPath(_root);
        if (!string.Equals(Path.GetDirectoryName(resolved), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("durable-graph-independent-workspace-", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Refusing to delete outside this test fixture.");
        }
        Directory.Delete(resolved, recursive: true);
    }
}
