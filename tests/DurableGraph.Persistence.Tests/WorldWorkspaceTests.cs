using System.Buffers;
using System.Reflection;
using System.Runtime.CompilerServices;

using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed partial class SplitBusinessWorkspaceTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"durable-graph-workspace-{Guid.NewGuid():N}");
    private readonly RepositoryStorageFacade _repository;
    private GraphRecordStore Store => _repository.Store;
    private const ulong InitialSequence = 0xFEDC_BA98_7654_3210UL;
    private static readonly DurableSchema Old = Schema("World", 1);
    private static readonly DurableSchema Current = Schema("World", 2);
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1000000, 1);

    public SplitBusinessWorkspaceTests() => _repository = RepositoryStorageFacade.CreateNew(_root);

    [Fact]
    public void ThreeJointInstallsRetainWorldAndUseFrozenCandidateRatherThanLaterMutation() {
        World world = new() { Value = 1, Text = new string('x', 1) };
        StateModelRegistry models = Registry(Model());
        BranchSaveSessionFixture session = new(_repository, "main", models);
        Assert.Null(session.StateSource);
        Assert.True(session.RootId.IsNull);
        FrameAddress first;
        using (PreparedFixtureSave pending = session.Prepare(world, parameters: NoRebase)) {
            Assert.Throws<InvalidOperationException>(() => session.Prepare(world));
            Assert.Throws<InvalidOperationException>(() => Candidate(pending).Install());
            world.Value = 2;
            first = pending.Commit().Business.Address;
            Assert.Throws<InvalidOperationException>(() => pending.Commit());
        }
        Assert.Same(world, session.State);
        Assert.Equal(first, session.StateSource!.Address);
        Assert.Equal((byte)1, Read(first, session.RootId, models).Value);
        Assert.Equal(InitialSequence, Read(first, session.RootId, models).Sequence);
        using (PreparedFixtureSave second = session.Prepare(world, parameters: NoRebase)) {
            Assert.Equal(first, second.Business.Write.SourceGraph);
            Assert.Equal(ObjectVersionKind.Delta, Assert.Single(second.Business.Write.Records).Kind);
            FrameAddress address = second.Commit().Business.Address;
            Assert.Equal((byte)2, Read(address, session.RootId, models).Value);
        }
        world.Value = 3;
        world.Sequence--;
        using (PreparedFixtureSave third = session.Prepare(world, parameters: NoRebase)) {
            FrameAddress address = third.Commit().Business.Address;
            Assert.Equal((byte)3, Read(address, session.RootId, models).Value);
            Assert.Equal(InitialSequence - 1, Read(address, session.RootId, models).Sequence);
        }
        Assert.Same(world, session.State);
        using PreparedFixtureSave unchanged = session.Prepare(world, parameters: NoRebase);
        Assert.Empty(unchanged.Business.Write.Records);
        Assert.Empty(unchanged.Business.Write.RemovedIds);
    }

    [Fact]
    public void RemovedInstanceReentryUsesFreshIdAndDiscardedCandidateBurnsIds() {
        string original = new('x', 1);
        World world = new() { Text = original };
        BranchSaveSessionFixture session = new(_repository, "main", Registry(Model()));
        using (PreparedFixtureSave first = session.Prepare(world, parameters: NoRebase)) { first.Commit(); }
        uint oldId = MechanismFixture.Snapshot(_repository, session.StateSource!.Address).ObjectHeads.Keys.Max();
        world.Text = null;
        using (PreparedFixtureSave remove = session.Prepare(world, parameters: NoRebase)) {
            Assert.Contains(oldId, remove.Business.Write.RemovedIds);
            Assert.DoesNotContain(oldId, remove.Commit().Business.ObjectHeads.Keys);
        }
        world.Text = original;
        uint burned;
        using (PreparedFixtureSave abandoned = session.Prepare(world, parameters: NoRebase)) {
            burned = abandoned.Business.Write.Records.Max(row => row.ObjectId);
            Assert.True(burned > oldId);
        }
        using PreparedFixtureSave readded = session.Prepare(world, parameters: NoRebase);
        uint newId = readded.Business.Write.Records.Max(row => row.ObjectId);
        Assert.True(newId > burned);
        Assert.Equal(ObjectVersionKind.Base, readded.Business.Write.Records.Single(row => row.ObjectId == newId).Kind);
        readded.Commit();
        Assert.Same(original, world.Text);
        Assert.Equal(newId, Read(session.StateSource!.Address, session.RootId, session.Models).TextId.Value);
    }

    [Fact]
    public void FailedBodyPreparationBurnsIdsButPreservesAcceptedSourceAndAllowsRetry() {
        bool fail = false;
        World world = new() { Value = 1 };
        StateModelRegistry models = Registry(Model(beforePrepare: () => { if (fail) throw new InvalidOperationException("body failed"); }));
        BranchSaveSessionFixture session = new(_repository, "main", models);
        using (PreparedFixtureSave seed = session.Prepare(world, parameters: NoRebase)) { seed.Commit(); }
        StoredGraphRevision first = session.StateSource!;
        NormalizedGraphState baseline = session.Baseline!;
        world.Text = new string('y', 1);
        long frames = Store.AppendedFrames;
        fail = true;
        Assert.Throws<InvalidOperationException>(() => session.Prepare(world, parameters: NoRebase));
        Assert.Same(first, session.StateSource);
        Assert.Same(baseline, session.Baseline);
        Assert.Equal(frames, Store.AppendedFrames);
        Assert.False(_repository.HasPending);
        Assert.False(_repository.IsFaulted);
        fail = false;
        using PreparedFixtureSave retry = session.Prepare(world, parameters: NoRebase);
        Assert.Equal(first.Address, retry.Business.Write.SourceGraph);
        Assert.Contains(retry.Business.Write.Records, row => row.ObjectId == 3); // ID 2 belonged only to the failed candidate.
        retry.Commit();
    }

    [Fact]
    public void UpgradeRewriteAndCompleteSourceMembershipSurviveDiscardAndEventThenClearOnState() {
        FrameAddress old = Seed(Old, new(5, 100, 0), Text(100, "orphan after upgrade"));
        StateModelRegistry models = Registry(Model(upgrade: state => state with { TextId = new ObjectId(0) }));
        BranchSaveSessionFixture session = new(_repository, "main", models);
        World instance = Assert.IsType<World>(session.State);
        NormalizedGraphState source = session.Baseline!;
        Assert.Contains(new ObjectId(100), source.Objects.Keys);
        Assert.True(source.Objects[session.RootId].RequiresRewrite);
        long frames = Store.AppendedFrames;
        using (PreparedFixtureSave abandoned = session.Prepare(instance, parameters: NoRebase)) {
            Assert.Equal(ObjectVersionKind.Base, Assert.Single(abandoned.Business.Write.Records).Kind);
            Assert.Equal(new uint[] { 100 }, abandoned.Business.Write.RemovedIds);
        }
        Assert.Equal(frames, Store.AppendedFrames);
        Assert.Same(source, session.Baseline);
        using (PreparedFixtureSave snapshot = session.Prepare(instance, StoredGraphKind.Event, NoRebase)) {
            Assert.Equal(ObjectVersionKind.Base, Assert.Single(snapshot.Business.Write.Records).Kind);
            StoredGraphRevision eventGraph = snapshot.Commit().Business;
            Assert.Null(eventGraph.SourceGraph);
            Assert.Equal(new uint[] { 1 }, eventGraph.ObjectHeads.Keys);
        }
        Assert.Same(source, session.Baseline);
        Assert.True(session.Baseline!.Objects[session.RootId].RequiresRewrite);
        using (PreparedFixtureSave retry = session.Prepare(instance, parameters: NoRebase)) {
            Assert.Equal(ObjectVersionKind.Base, Assert.Single(retry.Business.Write.Records).Kind);
            StoredGraphRevision installed = retry.Commit().Business;
            Assert.Equal(new uint[] { 1 }, installed.ObjectHeads.Keys);
        }
        Assert.False(session.Baseline!.Objects[session.RootId].RequiresRewrite);
        Assert.Same(instance, session.State);
        using (PreparedFixtureSave unchanged = session.Prepare(instance, parameters: NoRebase)) {
            Assert.Empty(unchanged.Business.Write.Records);
            Assert.Empty(unchanged.Business.Write.RemovedIds);
        }
        instance.Value++;
        using PreparedFixtureSave changed = session.Prepare(instance, parameters: NoRebase);
        ObjectVersionRecord delta = Assert.Single(changed.Business.Write.Records);
        Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
        Assert.Equal(session.StateSource!.ObjectHeads[session.RootId.Value], delta.PriorAddress);
        changed.Commit();
        Assert.Equal((byte)5, Read(old, new(1), models).Value);
    }

    [Fact]
    public void EmptyAliasesCollapseOnlyOnConfirmedStateAndThenRemainStable() {
        FrameAddress old = Seed(Current, new(5, 9, 0), Text(3, ""), Text(9, ""));
        BranchSaveSessionFixture session = new(_repository, "main", Registry(Model()));
        NormalizedGraphState source = session.Baseline!;
        using (PreparedFixtureSave abandoned = session.Prepare(session.State!, parameters: NoRebase)) {
            Assert.Equal(new uint[] { 9 }, abandoned.Business.Write.RemovedIds);
        }
        Assert.Same(source, session.Baseline);
        Assert.Equal(new uint[] { 1, 3, 9 }, MechanismFixture.Snapshot(_repository, old).ObjectHeads.Keys);
        using (PreparedFixtureSave first = session.Prepare(session.State!, parameters: NoRebase)) {
            Assert.Equal(new uint[] { 9 }, first.Business.Write.RemovedIds);
            Assert.Equal(new uint[] { 1, 3 }, first.Commit().Business.ObjectHeads.Keys);
        }
        using PreparedFixtureSave second = session.Prepare(session.State!, parameters: NoRebase);
        Assert.Empty(second.Business.Write.Records);
        Assert.Empty(second.Business.Write.RemovedIds);
        Assert.Same(string.Empty, Assert.IsType<World>(session.State).Text);
    }

    [Fact]
    public void RecursivePreparationLeavesNeitherWorkspaceNorOwnerOccupied() {
        bool recurse = true;
        BranchSaveSessionFixture? session = null;
        World world = new();
        StateModelRegistry models = Registry(Model(beforePrepare: () => { if (recurse) session!.Prepare(world); }));
        session = new(_repository, "main", models);
        Assert.Throws<InvalidOperationException>(() => session.Prepare(world));
        Assert.False(_repository.HasPending);
        Assert.False(_repository.IsFaulted);
        recurse = false;
        using PreparedFixtureSave retry = session.Prepare(world);
        Assert.True(retry.Business.Write.RootId.Value > 1);
        retry.Commit();
    }

    [Fact]
    public void DurableCycleChildOnlyUpdatesPreserveInstancesAndRetiredChildGetsNewId() {
        DurableSchema schema = new("Node", 1, new DurableFieldInfo(1, TypeTag.ObjectReference, "Node"), new DurableFieldInfo(2, TypeTag.Byte));
        int restores = 0;
        StatePreparationBinding<NodeState> preparation = new(schema,
            static (in NodeState state) => NodeBase(state),
            static (in NodeState prior, in NodeState next) => new(prior != next, NodeBase(next).Body));
        StateReaderBinding<NodeState> reader = new(schema,
            static (ref BareValueReader bytes) => new(bytes.ReadVarUInt32(), bytes.ReadByte()),
            static (ref BareValueReader bytes, in NodeState prior) => new(bytes.ReadVarUInt32(), bytes.ReadByte()), VisitNode);
        StateModelBinding<Node, NodeState> model = new(preparation, [reader], static row => row.GetState<NodeState>(),
            () => { restores++; return new Node(); },
            static (Node node, in NodeState state, ObjectReadTable objects) => {
                node.Next = objects.ResolveDurable<Node>(state.NextId); node.Value = state.Value;
            }, static (node, context) => new(context.CaptureDurable(node.Next, "Node"), node.Value), VisitNode);
        Node child = new() { Value = 7 }; child.Next = child;
        Node root = new() { Next = child };
        StateModelRegistry models = Registry(model);
        BranchSaveSessionFixture session = new(_repository, "main", models);
        FrameAddress first;
        using (PreparedFixtureSave seed = session.Prepare(root, parameters: NoRebase)) { first = seed.Commit().Business.Address; }
        child.Value = 8;
        FrameAddress changed;
        using (PreparedFixtureSave pending = session.Prepare(root, parameters: NoRebase)) {
            ObjectVersionRecord write = Assert.Single(pending.Business.Write.Records);
            Assert.Equal(2u, write.ObjectId);
            Assert.Equal(ObjectVersionKind.Base, write.Kind); // The complete two-slot body is cheaper than a prior pointer.
            changed = pending.Commit().Business.Address;
        }
        Assert.Same(root, session.State);
        Assert.Same(child, root.Next);
        Assert.Same(child, child.Next);
        Assert.Equal(0, restores);
        root.Next = null;
        using (PreparedFixtureSave pending = session.Prepare(root, parameters: NoRebase)) {
            Assert.Equal(new uint[] { 2 }, pending.Business.Write.RemovedIds);
            Assert.Equal(new uint[] { 1 }, pending.Commit().Business.ObjectHeads.Keys);
        }
        root.Next = child;
        using (PreparedFixtureSave pending = session.Prepare(root, parameters: NoRebase)) {
            Assert.Equal(ObjectVersionKind.Base, pending.Business.Write.Records.Single(row => row.ObjectId == 3).Kind);
            pending.Commit();
        }
        Assert.Same(child, root.Next);
        Assert.Equal((byte)7, DecodedRevision.ReadStoredGraph(Store, MechanismFixture.Snapshot(_repository, first), (models).FreezeConfiguration()).GetRequired(new(2)).GetState<NodeState>().Value);
        NodeState updated = DecodedRevision.ReadStoredGraph(Store, MechanismFixture.Snapshot(_repository, changed), (models).FreezeConfiguration()).GetRequired(new(2)).GetState<NodeState>();
        Assert.Equal((byte)8, updated.Value);
        Assert.Equal(new ObjectId(2), updated.NextId);
    }

    private static BusinessSaveCandidate Candidate(PreparedFixtureSave save) => (BusinessSaveCandidate)typeof(PreparedBusinessWrite)
        .GetField("_candidate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(save.Business)!;

    private State Read(FrameAddress address, ObjectId id, StateModelRegistry models) =>
        DecodedRevision.ReadStoredGraph(Store, MechanismFixture.Snapshot(_repository, address), (models).FreezeConfiguration()).GetRequired(id).GetState<State>();
    private State Read(FrameAddress address, ObjectId id, ModelExecutionEnvironment models) {
        StoredGraphRevision graph = MechanismFixture.Snapshot(_repository, address);
        SchemaCatalogView catalog = SchemaCatalogView.Load(Store, graph.SchemaRoot!.Value);
        return StoredGraphDecoder.Read(Store, graph, models, catalog).Rows[id].GetState<State>();
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
        BareValueWriter writer = new(bytes);
        writer.WriteVarUInt32(state.NextId.Value);
        writer.WriteByte(state.Value);
        return new(bytes.WrittenSpan);
    }

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
        StatePreparationBinding<State> preparation = new(currentSchema,
            (in State state) => { beforePrepare?.Invoke(); return Base(state); },
            static (in State prior, in State next) => Delta(prior, next));
        StateReaderBinding<State> Reader(DurableSchema schema) => new(schema,
            (ref BareValueReader reader) => { onRead?.Invoke(); return Read(ref reader); },
            static (ref BareValueReader reader, in State prior) => Apply(ref reader, prior), Validate);
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
    // This fixture writes real employee metadata and typed records to seed otherwise
    // legal historical IDs (including distinct empty-string aliases). No legacy wire is used.
    private FrameAddress Seed(DurableSchema schema, State state, params ObjectVersionRecord[] strings) {
        SchemaCatalogWorkspace catalog = new(_repository.EnsureEditingAllowed);
        catalog.Set(schema);
        PreparedSchemaCatalogWrite prepared = catalog.Prepare();
        ObjectVersionRecord root = ObjectVersionRecord.CreateBase(1,
            TypedBaseBodyCodec.Encode(ObjectLayout.ForDurable(schema), Base(state), prepared.View));
        using PendingRepositorySave pending = MechanismFixture.Prepare(_repository, "main", StoredGraphKind.State, new(null, new(1), new[] { root }.Concat(strings)), prepared.Write);
        return _repository.Commit(pending).Business.Address;
    }
    private static ObjectVersionRecord Text(uint id, string value) => ObjectVersionRecord.CreateBase(id,
        TypedBaseBodyCodec.Encode(ObjectLayout.String, StringBodyPreparer.PrepareBase(value)));
    private static PreparedBaseBody Base(State state) {
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        writer.WriteByte(state.Value);
        writer.WriteVarUInt32(state.TextId.Value);
        writer.WriteVarUInt32(state.AliasId.Value);
        writer.WriteVarUInt64(state.Sequence);
        return new(bytes.WrittenSpan);
    }
    private static State Read(ref BareValueReader reader) => new(reader.ReadByte(), reader.ReadVarUInt32(), reader.ReadVarUInt32(), reader.ReadVarUInt64());
    private static PreparedDeltaBody Delta(State prior, State next) {
        byte mask = (byte)((prior.Value != next.Value ? 1 : 0) | (prior.TextId != next.TextId ? 2 : 0) | (prior.AliasId != next.AliasId ? 4 : 0) | (prior.Sequence != next.Sequence ? 8 : 0));
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        writer.WriteByte(mask);
        if ((mask & 1) != 0) writer.WriteByte(next.Value);
        if ((mask & 2) != 0) writer.WriteVarUInt32(next.TextId.Value);
        if ((mask & 4) != 0) writer.WriteVarUInt32(next.AliasId.Value);
        if ((mask & 8) != 0) writer.WriteVarUInt64(next.Sequence);
        return new(mask != 0, bytes.WrittenSpan);
    }
    private static State Apply(ref BareValueReader reader, State prior) {
        byte mask = reader.ReadByte();
        if (mask == 0 || mask > 15) throw new InvalidDataException("Invalid test bitmap.");
        return new((mask & 1) != 0 ? reader.ReadByte() : prior.Value,
            (mask & 2) != 0 ? new ObjectId(reader.ReadVarUInt32()) : prior.TextId,
            (mask & 4) != 0 ? new ObjectId(reader.ReadVarUInt32()) : prior.AliasId,
            (mask & 8) != 0 ? reader.ReadVarUInt64() : prior.Sequence);
    }
    public void Dispose() {
        _repository.Dispose();
        SharedReadModel.DeleteFixture(_root, "durable-graph-workspace-");
    }
}
