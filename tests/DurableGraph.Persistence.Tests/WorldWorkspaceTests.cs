using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using System.Runtime.CompilerServices;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed partial class WorldWorkspaceTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"durable-graph-workspace-{Guid.NewGuid():N}");
    private readonly IRbfFile _file;
    private readonly SegmentStore _segments;
    private readonly SchemaStore _schemas;
    private readonly StateRevisionStore _store;
    // A real stable scalar makes sparse field updates smaller than a complete Base.
    private const ulong InitialSequence = 0xFEDC_BA98_7654_3210UL;
    private static readonly DurableSchema Old = Schema("World", 1);
    private static readonly DurableSchema Current = Schema("World", 2);
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1000000, 1);

    public WorldWorkspaceTests() {
        Directory.CreateDirectory(_root);
        _file = RbfFile.CreateNew(Path.Combine(_root, "schemas.rbf"));
        _schemas = new(_file);
        _segments = SegmentStore.CreateNew(Path.Combine(_root, "state"), new() { NewStoreLayout = RbfSegmentStoreLayout.Flat });
        _store = new(_segments);
    }

    [Fact]
    public void ThreeInstallsRetainWorldAndInstallFrozenCandidateRatherThanLaterMutation() {
        World world = new() { Value = 1, Text = new string('x', 1) };
        StateModelRegistry models = Registry(Model());
        WorldWorkspace workspace = WorldWorkspace.Create(_store, _schemas, world, models);
        Assert.Null(workspace.ParentRevisionAddress);
        Assert.Equal(new ObjectId(0), workspace.WorldId);
        FrameAddress first;
        using (PreparedWorldSave pending = workspace.Stage(NoRebase)) {
            Assert.Throws<InvalidOperationException>(() => workspace.Stage(NoRebase));
            Assert.Throws<InvalidOperationException>(pending.Install);
            world.Value = 2;
            first = Install(pending);
            Assert.Throws<InvalidOperationException>(pending.Install);
        }
        Assert.Same(world, workspace.World);
        Assert.Equal(first, workspace.ParentRevisionAddress);
        Assert.Equal((byte)1, Read(first, workspace.WorldId, models).Value);
        Assert.Equal(InitialSequence, Read(first, workspace.WorldId, models).Sequence);
        using (PreparedWorldSave second = workspace.Stage(NoRebase)) {
            Assert.Equal(first, second.Revision.ParentRevisionAddress);
            Assert.Equal(ObjectVersionKind.Delta, Assert.Single(second.Revision.LocalObjects).Kind);
            FrameAddress address = Install(second);
            Assert.Equal((byte)2, Read(address, workspace.WorldId, models).Value);
        }
        world.Value = 3;
        world.Sequence = InitialSequence - 1;
        using (PreparedWorldSave third = workspace.Stage(NoRebase)) {
            FrameAddress address = Install(third);
            Assert.Equal((byte)3, Read(address, workspace.WorldId, models).Value);
            Assert.Equal(InitialSequence - 1, Read(address, workspace.WorldId, models).Sequence);
        }
        Assert.Same(world, workspace.World);
        using PreparedWorldSave unchanged = workspace.Stage(NoRebase);
        Assert.Empty(unchanged.Revision.LocalObjects);
    }

    [Fact]
    public void RemovedInstanceReentryUsesFreshIdAndUnpublishedCandidateBurnsIds() {
        string original = new('x', 1);
        World world = new() { Text = original };
        WorldWorkspace workspace = WorldWorkspace.Create(_store, _schemas, world, Registry(Model()));
        using (PreparedWorldSave first = workspace.Stage(NoRebase)) Install(first);
        uint oldId = _store.ReadLiveObjectHeadMap(workspace.ParentRevisionAddress!.Value).Keys.Max();
        world.Text = null;
        using (PreparedWorldSave remove = workspace.Stage(NoRebase)) {
            FrameAddress saved = Install(remove);
            Assert.DoesNotContain(oldId, _store.ReadLiveObjectHeadMap(saved).Keys);
        }
        world.Text = original;
        uint burned;
        using (PreparedWorldSave abandoned = workspace.Stage(NoRebase)) {
            burned = abandoned.Revision.LocalObjectIds.Max();
            Assert.True(burned > oldId);
        }
        using PreparedWorldSave readded = workspace.Stage(NoRebase);
        uint newId = readded.Revision.LocalObjectIds.Max();
        Assert.True(newId > burned);
        Assert.Equal(ObjectVersionKind.Base, readded.Revision.LocalObjects.Single(row => row.ObjectId == newId).Kind);
        Install(readded);
        Assert.Same(original, world.Text);
    }

    [Fact]
    public void FailedCaptureBurnsIdsButPreservesAcceptedParentAndBindings() {
        bool fail = false;
        World world = new() { Value = 1 };
        StateModelRegistry models = Registry(Model(beforePrepare: () => { if (fail) throw new InvalidOperationException("body failed"); }));
        WorldWorkspace workspace = WorldWorkspace.Create(_store, _schemas, world, models);
        FrameAddress first;
        using (PreparedWorldSave pending = workspace.Stage(NoRebase)) first = Install(pending);
        world.Text = new string('y', 1);
        fail = true;
        Assert.Throws<InvalidOperationException>(() => workspace.Stage(NoRebase));
        Assert.Equal(first, workspace.ParentRevisionAddress);
        fail = false;
        using PreparedWorldSave retry = workspace.Stage(NoRebase);
        Assert.Equal(first, retry.Revision.ParentRevisionAddress);
        Assert.Contains(3u, retry.Revision.LocalObjectIds); // ID 2 was consumed by the failed candidate.
        Install(retry);
    }

    [Fact]
    public void UpgradeRewriteAndCompleteSourceMembershipSurviveDiscardThenClearOnInstall() {
        FrameAddress old = Seed(Old, new(5, 100, 0), Text(100, "orphan after upgrade"));
        StateModelRegistry models = Registry(Model(upgrade: state => state with { TextId = new ObjectId(0) }));
        WorldWorkspace workspace = WorldWorkspace.Load(_store, _schemas, old, new ObjectId(1), models);
        World instance = (World)workspace.World!;
        using (PreparedWorldSave abandoned = workspace.Stage(NoRebase)) {
            Assert.Equal(ObjectVersionKind.Base, Assert.Single(abandoned.Revision.LocalObjects).Kind);
            FrameAddress orphanAppend = _store.Append(abandoned.Revision);
            Assert.Equal(new uint[] { 1 }, _store.ReadLiveObjectHeadMap(orphanAppend).Keys);
            abandoned.PrepareInstall(orphanAppend);
        }
        Assert.Equal(old, workspace.ParentRevisionAddress);
        using (PreparedWorldSave retry = workspace.Stage(NoRebase)) {
            Assert.Equal(ObjectVersionKind.Base, Assert.Single(retry.Revision.LocalObjects).Kind);
            FrameAddress installed = Install(retry);
            Assert.Equal(new uint[] { 1 }, _store.ReadLiveObjectHeadMap(installed).Keys);
        }
        using (PreparedWorldSave unchanged = workspace.Stage(NoRebase)) {
            Assert.Empty(unchanged.Revision.LocalObjects);
            Assert.Empty(unchanged.Revision.RemovedObjectIds);
        }
        instance.Value = 6;
        instance.Text = new string('z', 1);
        using PreparedWorldSave changed = workspace.Stage(NoRebase);
        Assert.Equal(ObjectVersionKind.Delta, changed.Revision.LocalObjects.Single(row => row.ObjectId == 1).Kind);
        Assert.Contains(101u, changed.Revision.LocalObjectIds);
        Assert.Same(instance, workspace.World);
        Install(changed);
    }

    [Fact]
    public void EmptyAliasesCollapseOnlyAtSuccessfulInstallAndThenRemainStable() {
        FrameAddress old = Seed(Current, new(5, 9, 0), Text(3, ""), Text(9, ""));
        WorldWorkspace workspace = WorldWorkspace.Load(_store, _schemas, old, new ObjectId(1), Registry(Model()));
        using (PreparedWorldSave first = workspace.Stage(NoRebase)) {
            Assert.Equal(new uint[] { 9 }, first.Revision.RemovedObjectIds);
            Install(first);
        }
        using PreparedWorldSave second = workspace.Stage(NoRebase);
        Assert.Empty(second.Revision.LocalObjects);
        Assert.Empty(second.Revision.RemovedObjectIds);
        Assert.Same(string.Empty, ((World)workspace.World!).Text);
    }

    [Fact]
    public void RecursivePreparationFailsWithoutLeavingWorkspaceOccupied() {
        bool recurse = true;
        WorldWorkspace? workspace = null;
        StateModelRegistry models = Registry(Model(beforePrepare: () => { if (recurse) workspace!.Stage(NoRebase); }));
        workspace = WorldWorkspace.Create(_store, _schemas, new World(), models);
        Assert.Throws<InvalidOperationException>(() => workspace.Stage(NoRebase));
        recurse = false;
        using PreparedWorldSave retry = workspace.Stage(NoRebase);
        Assert.True(retry.RootId.Value > 1);
        Install(retry);
    }

    [Fact]
    public void DurableCycleChildOnlyChangesPreserveInstancesAndRetiredChildGetsNewId() {
        DurableSchema schema = new("Node", 1, new DurableFieldInfo(1, TypeTag.ObjectReference, "Node"),
            new DurableFieldInfo(2, TypeTag.Byte));
        int restores = 0;
        CapturedStatePreparation<NodeState> preparation = new(schema,
            static (in NodeState state) => NodeBase(state),
            static (in NodeState prior, in NodeState next) => new(prior != next, NodeBase(next).Body));
        StateReaderBinding<NodeState> reader = new(schema,
            static (ref BinaryPayloadReader bytes) => new(bytes.ReadUInt32(), bytes.ReadByte()),
            static (ref BinaryPayloadReader bytes, in NodeState prior) => new(bytes.ReadUInt32(), bytes.ReadByte()),
            VisitNode);
        StateModelBinding<Node, NodeState> model = new(preparation, [reader],
            static row => row.GetState<NodeState>(),
            () => { restores++; return new Node(); },
            static (Node node, in NodeState state, ObjectReadTable objects) => {
                node.Next = objects.ResolveDurable<Node>(state.NextId);
                node.Value = state.Value;
            },
            static (node, context) => new(context.CaptureDurable(node.Next, "Node"), node.Value), VisitNode);
        Node child = new() { Value = 7 };
        child.Next = child;
        Node root = new() { Next = child };
        StateModelRegistry models = Registry(model);
        WorldWorkspace workspace = WorldWorkspace.Create(_store, _schemas, root, models);
        FrameAddress first;
        using (PreparedWorldSave pending = workspace.Stage(NoRebase)) {
            first = _store.Append(pending.Revision);
            pending.PrepareInstall(first);
            pending.Install();
        }
        child.Value = 8;
        FrameAddress changed;
        using (PreparedWorldSave pending = workspace.Stage(NoRebase)) {
            ObjectVersionRecord write = Assert.Single(pending.Revision.LocalObjects);
            Assert.Equal(2u, write.ObjectId);
            // This fixture's Delta repeats the complete two-field body; the compact v4 Base
            // is cheaper than adding a prior pointer. Identity must survive that choice too.
            Assert.Equal(ObjectVersionKind.Base, write.Kind);
            changed = _store.Append(pending.Revision);
            pending.PrepareInstall(changed);
            pending.Install();
        }
        Assert.Same(root, workspace.World);
        Assert.Same(child, root.Next);
        Assert.Same(child, child.Next);
        Assert.Equal(0, restores); // Neither initial nor subsequent installation rematerializes any object.
        root.Next = null;
        using (PreparedWorldSave pending = workspace.Stage(NoRebase)) {
            FrameAddress removed = _store.Append(pending.Revision);
            Assert.Equal(new uint[] { 1 }, _store.ReadLiveObjectHeadMap(removed).Keys);
            pending.PrepareInstall(removed);
            pending.Install();
        }
        root.Next = child;
        using (PreparedWorldSave pending = workspace.Stage(NoRebase)) {
            Assert.Equal(ObjectVersionKind.Base, pending.Revision.LocalObjects.Single(row => row.ObjectId == 3).Kind);
            pending.PrepareInstall(_store.Append(pending.Revision));
            pending.Install();
        }
        Assert.Same(child, root.Next);
        Assert.Equal((byte)7, RevisionDecoder.ReadSnapshot(_store, _schemas, first, models.Snapshot().Readers)
            .GetRequired(new ObjectId(2)).GetState<NodeState>().Value);
        NodeState changedState = RevisionDecoder.ReadSnapshot(_store, _schemas, changed, models.Snapshot().Readers)
            .GetRequired(new ObjectId(2)).GetState<NodeState>();
        Assert.Equal((byte)8, changedState.Value);
        Assert.Equal(new ObjectId(2), changedState.NextId);
    }

    private sealed class Node : IDurableObject {
        internal Node? Next;
        internal byte Value;
    }
    private readonly record struct NodeState(ObjectId NextId, byte Value) {
        internal NodeState(uint nextId, byte value) : this(new ObjectId(nextId), value) { }
    }
    private static void VisitNode(in NodeState state, IStateReferenceVisitor visitor) => visitor.VisitDurable(state.NextId, "Node");
    private static PreparedBaseBody NodeBase(NodeState state) {
        ArrayBufferWriter<byte> bytes = new();
        BinaryPayloadWriter writer = new(bytes);
        writer.WriteUInt32(state.NextId.Value);
        writer.WriteByte(state.Value);
        return new(bytes.WrittenSpan);
    }

    private FrameAddress Install(PreparedWorldSave pending) {
        FrameAddress address = _store.Append(pending.Revision);
        pending.PrepareInstall(address);
        pending.Install(); // Internal mechanism witness; publication is the outer Repository's responsibility.
        return address;
    }

    private State Read(FrameAddress address, ObjectId id, StateModelRegistry models) =>
        RevisionDecoder.ReadSnapshot(_store, _schemas, address, models.Snapshot().Readers).GetRequired(id).GetState<State>();

    private class World : IDurableObject {
        internal byte Value;
        internal string? Text;
        internal string? Alias;
        internal ulong Sequence = InitialSequence;
        internal int TransientMarker = 73;
        internal World(int unused = 0) { }
    }
    private sealed class OtherWorld() : World(0) { }
    private readonly record struct State(byte Value, ObjectId TextId, ObjectId AliasId, ulong Sequence = InitialSequence) {
        internal State(byte value, uint textId, uint aliasId, ulong sequence = InitialSequence) : this(value, new ObjectId(textId), new ObjectId(aliasId), sequence) { }
    }

    private static StateModelBinding Model(Func<State, State>? upgrade = null, Action? beforePrepare = null,
        Action? onRead = null, DurableSchema? current = null, DurableSchema? old = null) =>
        ModelCore<World>(upgrade, beforePrepare, onRead, current, old);

    private static StateModelBinding ModelCore<TWorld>(Func<State, State>? upgrade = null, Action? beforePrepare = null,
        Action? onRead = null, DurableSchema? current = null, DurableSchema? old = null) where TWorld : World {
        DurableSchema currentSchema = current ?? Current;
        DurableSchema oldSchema = old ?? Old;
        CapturedStatePreparation<State> preparation = new(currentSchema,
            (in State state) => { beforePrepare?.Invoke(); return Base(state); },
            static (in State prior, in State next) => Delta(prior, next));
        StateReaderBinding<State> Reader(DurableSchema schema) => new(schema,
            (ref BinaryPayloadReader reader) => { onRead?.Invoke(); return Read(ref reader); },
            static (ref BinaryPayloadReader reader, in State prior) => Apply(ref reader, prior), Validate);
        StateReaderBinding[] readers = currentSchema.Equals(oldSchema)
            ? [Reader(currentSchema)] : [Reader(oldSchema), Reader(currentSchema)];
        return new StateModelBinding<TWorld, State>(preparation, readers,
            row => row.Schema!.Equals(currentSchema) ? row.GetState<State>() : (upgrade ?? (static state => state))(row.GetState<State>()),
            static () => (TWorld)RuntimeHelpers.GetUninitializedObject(typeof(TWorld)),
            static (TWorld world, in State state, ObjectReadTable strings) => {
                world.Value = state.Value;
                world.Text = strings.ResolveString(state.TextId);
                world.Alias = strings.ResolveString(state.AliasId);
                world.Sequence = state.Sequence;
            },
            static (world, context) => new(world.Value, context.CaptureString(world.Text), context.CaptureString(world.Alias), world.Sequence),
            Validate);
    }

    private static void Validate(in State state, IStateReferenceVisitor visitor) {
        visitor.VisitString(state.TextId);
        visitor.VisitString(state.AliasId);
    }
    private static StateModelRegistry Registry(StateModelBinding model) {
        StateModelRegistry result = new();
        result.Register(model);
        return result;
    }
    private static DurableSchema Schema(string id, int version) => new(id, version,
        new DurableFieldInfo(1, TypeTag.Byte), new DurableFieldInfo(2, TypeTag.String), new DurableFieldInfo(3, TypeTag.String),
        new DurableFieldInfo(4, TypeTag.UInt64));
    private FrameAddress Seed(DurableSchema schema, State state, params ObjectVersionRecord[] strings) {
        _schemas.Register(schema);
        return _store.Append(StateRevision.CreateObjectHeadMapBase(null, new[] { Durable(1, schema, state) }.Concat(strings), []));
    }
    private ObjectVersionRecord Durable(uint id, DurableSchema schema, State state) =>
        ObjectVersionRecord.CreateBase(id, BaseObjectBodyCodec.Encode(
            _schemas.RegisterRepresentations([ObjectLayout.ForDurable(schema)])[0], Base(state)).Body);
    private static ObjectVersionRecord Text(uint id, string value) =>
        ObjectVersionRecord.CreateBase(id, BaseObjectBodyCodec.EncodeString(StringPayloadCodec.PrepareBase(value)).Body);
    private static PreparedBaseBody Base(State state) {
        ArrayBufferWriter<byte> bytes = new();
        BinaryPayloadWriter writer = new(bytes);
        writer.WriteByte(state.Value);
        writer.WriteUInt32(state.TextId.Value);
        writer.WriteUInt32(state.AliasId.Value);
        writer.WriteUInt64(state.Sequence);
        return new(bytes.WrittenSpan);
    }
    private static State Read(ref BinaryPayloadReader reader) => new(reader.ReadByte(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt64());
    private static PreparedDeltaBody Delta(State prior, State next) {
        byte mask = (byte)((prior.Value != next.Value ? 1 : 0) | (prior.TextId != next.TextId ? 2 : 0) | (prior.AliasId != next.AliasId ? 4 : 0) | (prior.Sequence != next.Sequence ? 8 : 0));
        ArrayBufferWriter<byte> bytes = new();
        BinaryPayloadWriter writer = new(bytes);
        writer.WriteByte(mask);
        if ((mask & 1) != 0) writer.WriteByte(next.Value);
        if ((mask & 2) != 0) writer.WriteUInt32(next.TextId.Value);
        if ((mask & 4) != 0) writer.WriteUInt32(next.AliasId.Value);
        if ((mask & 8) != 0) writer.WriteUInt64(next.Sequence);
        return new(mask != 0, bytes.WrittenSpan);
    }
    private static State Apply(ref BinaryPayloadReader reader, State prior) {
        byte mask = reader.ReadByte();
        if (mask == 0 || mask > 15) throw new InvalidDataException("Invalid test bitmap.");
        return new((mask & 1) != 0 ? reader.ReadByte() : prior.Value,
            (mask & 2) != 0 ? new ObjectId(reader.ReadUInt32()) : prior.TextId,
            (mask & 4) != 0 ? new ObjectId(reader.ReadUInt32()) : prior.AliasId,
            (mask & 8) != 0 ? reader.ReadUInt64() : prior.Sequence);
    }
    private long Tail() {
        using RbfSegmentWriterLease writer = _segments.OpenActiveWriter();
        return writer.File.TailOffset;
    }
    public void Dispose() {
        _store.Dispose();
        _file.Dispose();
        _segments.Dispose();
        string resolved = Path.GetFullPath(_root);
        if (!string.Equals(Path.GetDirectoryName(resolved), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("durable-graph-workspace-", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Refusing to delete outside this test fixture.");
        }
        Directory.Delete(resolved, recursive: true);
    }
}
