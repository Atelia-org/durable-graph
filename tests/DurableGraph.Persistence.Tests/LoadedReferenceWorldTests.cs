using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class LoadedReferenceWorldTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"durable-graph-reference-world-{Guid.NewGuid():N}");
    private readonly IRbfFile _file;
    private readonly SegmentStore _segments;
    private readonly SchemaStore _schemas;
    private readonly StateRevisionStore _store;
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1000000, 1);

    public LoadedReferenceWorldTests() {
        Directory.CreateDirectory(_root);
        _file = RbfFile.CreateNew(Path.Combine(_root, "schemas.rbf"));
        _schemas = new(_file);
        _segments = SegmentStore.CreateNew(Path.Combine(_root, "state"), new() { NewStoreLayout = RbfSegmentStoreLayout.Flat });
        _store = new(_segments);
    }

    [Fact]
    public void StoredAndCurrentReferencesUseTheirOwnExactAncestry() {
        DurableSchema baseB = new("B", 1);
        DurableSchema baseC = new("C", 1);
        DurableSchema ownerV1 = Schema("Owner", 1, "B");
        DurableSchema ownerV2 = Schema("Owner", 2, "B");
        DurableSchema oldTarget = Schema("Target", 1, "B", baseB);
        DurableSchema currentTarget = Schema("Target", 2, "B", baseC);
        FrameAddress source = Seed((1, ownerV1, new State(2, 1)), (2, oldTarget, new State(0, 2)));
        int upgrades = 0;
        int allocations = 0;
        StateModelBinding target = Model<Node>(currentTarget, () => { allocations++; return new Node(); }, oldTarget,
            upgrade: state => { upgrades++; return state; });
        StateModelRegistry unchangedOwner = Registry(Model<World>(ownerV1, () => { allocations++; return new World(); }), target);
        Assert.Throws<InvalidDataException>(() => LoadedWorld.Load<World>(_store, _schemas, source, new ObjectId(1), unchangedOwner));
        Assert.Equal(1, upgrades); // Stored B ancestry was accepted, then current C ancestry rejected.
        Assert.Equal(0, allocations);

        int targetAllocations = 0;
        StateModelRegistry clearedOwner = Registry(
            Model<World>(ownerV2, () => new World(), ownerV1, upgrade: state => state with { NextId = new ObjectId(0) }),
            Model<Node>(currentTarget, () => { targetAllocations++; return new Node(); }, oldTarget));
        LoadedWorld<World> loaded = LoadedWorld.Load<World>(_store, _schemas, source, new ObjectId(1), clearedOwner);
        Assert.Null(loaded.World.Next);
        Assert.Equal(0, targetAllocations);
        PreparedWorldRevision prepared = loaded.Prepare(NoRebase);
        Assert.Equal(ObjectVersionKind.Base, Assert.Single(prepared.Revision.LocalObjects).Kind);
        Assert.Equal(new uint[] { 1 }, _store.ReadLiveObjectHeadMap(_store.Append(prepared.Revision)).Keys);
    }

    [Fact]
    public void InvalidStoredReferenceFailsBeforeAnyUpgradeEvenIfCurrentAncestryWouldAccept() {
        DurableSchema baseB = new("B", 1);
        DurableSchema baseC = new("C", 1);
        DurableSchema owner = Schema("Owner", 1, "B");
        DurableSchema oldTarget = Schema("Target", 1, "B", baseC);
        DurableSchema currentTarget = Schema("Target", 2, "B", baseB);
        FrameAddress source = Seed((1, owner, new State(2, 1)), (2, oldTarget, new State(0, 2)));
        int upgrades = 0;
        StateModelRegistry models = Registry(Model<World>(owner, () => new World()),
            Model<Node>(currentTarget, () => new Node(), oldTarget, upgrade: state => { upgrades++; return state; }));
        Assert.Throws<InvalidDataException>(() => LoadedWorld.Load<World>(_store, _schemas, source, new ObjectId(1), models));
        Assert.Equal(0, upgrades);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(99u)]
    public void AbstractCurrentOrphanIsValidatedButNeverAllocated(uint orphanReference) {
        DurableSchema worldSchema = Schema("World", 1, "World");
        DurableSchema oldOrphan = Schema("Orphan", 1, "World");
        DurableSchema currentOrphan = Schema("Orphan", 2, "World");
        FrameAddress source = Seed((1, worldSchema, new State(0, 1)), (9, oldOrphan, new State(0, 2)));
        StateModelRegistry models = Registry(Model<World>(worldSchema, () => new World()),
            Model<AbstractNode>(currentOrphan, () => throw new Exception("Must not allocate an unreachable abstract row"), oldOrphan,
                upgrade: state => state with { NextId = new ObjectId(orphanReference) }));
        if (orphanReference != 0) {
            Assert.Throws<InvalidDataException>(() => LoadedWorld.Load<World>(_store, _schemas, source, new ObjectId(1), models));
        } else {
            LoadedWorld<World> loaded = LoadedWorld.Load<World>(_store, _schemas, source, new ObjectId(1), models);
            Assert.Equal(new uint[] { 9 }, loaded.Prepare(NoRebase).Revision.RemovedObjectIds);
        }
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(99u)]
    public void WrongKindFamilyOrMissingTargetIsRejectedBeforeAllocation(uint reference) {
        DurableSchema worldSchema = Schema("World", 1, "Expected");
        DurableSchema otherSchema = Schema("Other", 1, "Expected");
        _schemas.RegisterBatch([worldSchema, otherSchema]);
        FrameAddress source = _store.Append(StateRevision.CreateObjectHeadMapBase(null,
            [Durable(1, worldSchema, new(reference, 1)),
             ObjectVersionRecord.CreateBase(2, BaseObjectBodyCodec.EncodeString(StringPayloadCodec.PrepareBase("text")).Body),
             Durable(3, otherSchema, new(0, 3))], []));
        int allocated = 0;
        StateModelRegistry models = Registry(Model<World>(worldSchema, () => { allocated++; return new World(); }),
            Model<Node>(otherSchema, () => { allocated++; return new Node(); }));
        Assert.Throws<InvalidDataException>(() => LoadedWorld.Load<World>(_store, _schemas, source, new ObjectId(1), models));
        Assert.Equal(0, allocated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicateOrNullAllocationFailsBeforeAnyHydrate(bool nullAllocation) {
        DurableSchema schema = Schema("World", 1, "World");
        FrameAddress source = Seed((1, schema, new State(2, 1)), (2, schema, new State(1, 2)));
        World singleton = new();
        int hydrations = 0;
        int allocations = 0;
        StateModelRegistry models = Registry(Model<World>(schema,
            () => ++allocations == 2 && nullAllocation ? null! : singleton,
            onHydrate: () => hydrations++));
        LoadedWorld<World>? delivered = null;
        Assert.Throws<InvalidDataException>(() => delivered = LoadedWorld.Load<World>(_store, _schemas, source, new ObjectId(1), models));
        Assert.Null(delivered);
        Assert.Equal(2, allocations);
        Assert.Equal(0, hydrations);
    }

    [Fact]
    public void WrongExactAllocationTypeIsRejectedBeforeHydration() {
        DurableSchema schema = Schema("World", 1, "World");
        FrameAddress source = Seed((1, schema, new State(0, 1)));
        int hydrations = 0;
        StateModelRegistry models = Registry(Model<World>(schema, () => new DerivedWorld(), onHydrate: () => hydrations++));
        Assert.Throws<InvalidDataException>(() => LoadedWorld.Load<World>(_store, _schemas, source, new ObjectId(1), models));
        Assert.Equal(0, hydrations);
    }

    [Fact]
    public void LateHydrateFailureDeliversNothingAndDoesNotWrite() {
        DurableSchema schema = Schema("World", 1, "World");
        FrameAddress source = Seed((1, schema, new State(2, 1)), (2, schema, new State(1, 2)));
        int allocations = 0;
        int hydrations = 0;
        StateModelRegistry models = Registry(Model<World>(schema, () => { allocations++; return new World(); }, onHydrate: () => {
            Assert.Equal(2, allocations);
            if (++hydrations == 2) throw new InvalidDataException("Late hydrate failure");
        }));
        long stateTail = Tail();
        long schemaTail = _file.TailOffset;
        LoadedWorld<World>? delivered = null;
        Assert.Throws<InvalidDataException>(() => delivered = LoadedWorld.Load<World>(_store, _schemas, source, new ObjectId(1), models));
        Assert.Null(delivered);
        Assert.Equal(2, hydrations);
        Assert.Equal(stateTail, Tail());
        Assert.Equal(schemaTail, _file.TailOffset);
    }

    [Fact]
    public void ExactTypeCollisionLeavesAllCatalogIndexesUnchanged() {
        StateModelRegistry registry = Registry(Model<World>(Schema("First", 1, "First"), () => new World()));
        Assert.Throws<InvalidOperationException>(() => registry.Register(Model<World>(Schema("Second", 1, "Second"), () => new World())));
        StateModelSnapshot snapshot = registry.Snapshot();
        Assert.Single(snapshot.Models);
        Assert.Single(snapshot.Types);
        Assert.Single(snapshot.Readers);
        Assert.False(snapshot.Models.ContainsKey(TypeExpr.Named("Second")));
        StateModelBinding second = Model<Node>(Schema("Second", 1, "Second"), () => new Node());
        registry.Register(second);
        Assert.Same(second, registry.Snapshot().Types[typeof(Node)]);
    }

    [Fact]
    public void UnchangedOwnerReferencesAreValidatedAgainstQueriedRevisionMembership() {
        DurableSchema schema = Schema("World", 1, "World");
        FrameAddress original = Seed((1, schema, new State(2, 1)), (2, schema, new State(0, 2)));
        FrameAddress removed = _store.Append(StateRevision.CreateObjectHeadMapDelta(original, [], [2]));
        int allocations = 0;
        StateModelRegistry models = Registry(Model<World>(schema, () => { allocations++; return new World(); }));
        Assert.Throws<InvalidDataException>(() => LoadedWorld.Load<World>(_store, _schemas, removed, new ObjectId(1), models));
        Assert.Equal(0, allocations);
        LoadedWorld<World> oldView = LoadedWorld.Load<World>(_store, _schemas, original, new ObjectId(1), models);
        Assert.Equal((byte)2, Assert.IsType<World>(oldView.World.Next).Value);
        Assert.Equal(2, allocations);
    }

    [Fact]
    public void LateChildCaptureFailureWritesNothingAndFreshPrepareCanBeRetried() {
        DurableSchema rootSchema = Schema("World", 1, "Node");
        DurableSchema childSchema = Schema("Node", 1, "Node");
        Node lateChild = new() { Value = 2 };
        World world = new() { Next = new Node { Value = 1, Next = lateChild } };
        int captures = 0;
        StateModelRegistry models = Registry(
            Model<World>(rootSchema, () => new World(), onCapture: _ => captures++),
            Model<Node>(childSchema, () => new Node(), onCapture: child => {
                captures++;
                if (child.Value == 2) throw new InvalidDataException("Late child Capture failure");
            }));
        long schemaTail = _file.TailOffset;
        long stateTail = Tail();
        Assert.Throws<InvalidDataException>(() => LoadedWorld.PrepareNew(_store, _schemas, world, models, NoRebase));
        Assert.Equal(3, captures);
        Assert.Equal(schemaTail, _file.TailOffset);
        Assert.Equal(stateTail, Tail());

        lateChild.Value = 3;
        PreparedWorldRevision repaired = LoadedWorld.PrepareNew(_store, _schemas, world, models, NoRebase);
        Assert.Equal(6, captures);
        Assert.Null(repaired.Revision.ParentRevisionAddress);
        Assert.Equal(3, repaired.Revision.LocalObjects.Count);
        Assert.All(repaired.Revision.LocalObjects, static row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
        Assert.Equal(stateTail, Tail());
        FrameAddress address = _store.Append(repaired.Revision);
        LoadedWorld<World> loaded = LoadedWorld.Load<World>(_store, _schemas, address, repaired.WorldId, models);
        Assert.Equal((byte)3, Assert.IsType<Node>(loaded.World.Next!.Next).Value);
    }

    [Fact]
    public void LongStoredChainUsesIterativeReachabilityAndAllocatesBeforeHydration() {
        const int count = 3000;
        DurableSchema schema = Schema("World", 1, "World");
        (uint Id, DurableSchema Schema, State State)[] rows = Enumerable.Range(1, count)
            .Select(id => ((uint)id, schema, new State(id < count ? (uint)(id + 1) : 0, (byte)(id % 251))))
            .ToArray();
        FrameAddress address = Seed(rows);
        int allocations = 0;
        int hydrations = 0;
        StateModelRegistry models = Registry(Model<World>(schema, () => { allocations++; return new World(); }, onHydrate: () => {
            Assert.Equal(count, allocations);
            hydrations++;
        }));
        LoadedWorld<World> loaded = LoadedWorld.Load<World>(_store, _schemas, address, new ObjectId(1), models);
        Domain? cursor = loaded.World;
        for (int id = 1; id <= count; id++) {
            Assert.NotNull(cursor);
            Assert.Equal((byte)(id % 251), cursor.Value);
            cursor = cursor.Next;
        }
        Assert.Null(cursor);
        Assert.Equal(count, hydrations);
    }

    private abstract class Domain : IDurableObject {
        internal Domain? Next;
        internal byte Value;
    }
    private class World : Domain { }
    private sealed class DerivedWorld : World { }
    private sealed class Node : Domain { }
    private abstract class AbstractNode : Domain { }
    private readonly record struct State(ObjectId NextId, byte Value) {
        internal State(uint nextId, byte value) : this(new ObjectId(nextId), value) { }
    }

    private static DurableSchema Schema(string id, int version, string nominal, DurableSchema? parent = null) =>
        new(id, version, [new(1, TypeTag.ObjectReference, nominal), new(2, TypeTag.Byte)], parent);

    private static StateModelBinding Model<T>(DurableSchema current, Func<T> allocate, DurableSchema? old = null,
        Func<State, State>? upgrade = null, Action? onHydrate = null, Action<T>? onCapture = null) where T : Domain {
        CapturedStatePreparation<State> preparation = new(current,
            static (in State state) => Base(state),
            static (in State prior, in State next) => new(prior != next, Base(next).Body));
        static void Visit(DurableSchema schema, in State state, IStateReferenceVisitor visitor) =>
            visitor.VisitDurable(state.NextId, schema.Fields[0].TargetSchemaId!);
        StateReaderBinding Reader(DurableSchema schema) => new StateReaderBinding<State>(schema,
            static (ref BinaryPayloadReader reader) => new(reader.ReadUInt32(), reader.ReadByte()),
            static (ref BinaryPayloadReader reader, in State prior) => new(reader.ReadUInt32(), reader.ReadByte()),
            (in State state, IStateReferenceVisitor visitor) => Visit(schema, in state, visitor));
        StateReaderBinding[] readers = old is null || old.Equals(current) ? [Reader(current)] : [Reader(old), Reader(current)];
        return new StateModelBinding<T, State>(preparation, readers,
            row => row.Schema!.Equals(current) ? row.GetState<State>() : (upgrade ?? (static value => value))(row.GetState<State>()),
            allocate,
            (T domain, in State state, ObjectReadTable objects) => {
                onHydrate?.Invoke();
                domain.Next = objects.ResolveDurable<Domain>(state.NextId);
                domain.Value = state.Value;
            },
            (domain, context) => {
                onCapture?.Invoke(domain);
                return new(context.CaptureDurable(domain.Next, current.Fields[0].TargetSchemaId!), domain.Value);
            },
            (in State state, IStateReferenceVisitor visitor) => Visit(current, in state, visitor));
    }

    private static StateModelRegistry Registry(params StateModelBinding[] models) {
        StateModelRegistry registry = new();
        foreach (StateModelBinding model in models) registry.Register(model);
        return registry;
    }
    private FrameAddress Seed(params (uint Id, DurableSchema Schema, State State)[] rows) {
        _schemas.RegisterBatch(rows.Select(static row => row.Schema));
        return _store.Append(StateRevision.CreateObjectHeadMapBase(null, rows.Select(row => Durable(row.Id, row.Schema, row.State)), []));
    }
    private ObjectVersionRecord Durable(uint id, DurableSchema schema, State state) =>
        ObjectVersionRecord.CreateBase(id, BaseObjectBodyCodec.Encode(
            _schemas.RegisterRepresentations([ObjectLayout.ForDurable(schema)])[0], Base(state)).Body);
    private static PreparedBaseBody Base(State state) {
        ArrayBufferWriter<byte> bytes = new();
        BinaryPayloadWriter writer = new(bytes);
        writer.WriteUInt32(state.NextId.Value);
        writer.WriteByte(state.Value);
        return new(bytes.WrittenSpan);
    }
    private long Tail() {
        using RbfSegmentWriterLease lease = _segments.OpenActiveWriter();
        return lease.File.TailOffset;
    }
    public void Dispose() {
        _store.Dispose();
        _file.Dispose();
        _segments.Dispose();
        string resolved = Path.GetFullPath(_root);
        if (!string.Equals(Path.GetDirectoryName(resolved), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("durable-graph-reference-world-", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Refusing to delete outside this test fixture.");
        }
        Directory.Delete(resolved, recursive: true);
    }
}
