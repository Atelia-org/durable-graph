using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class LoadedReferenceWorldTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"durable-graph-reference-world-{Guid.NewGuid():N}");
    private readonly RepositoryStorageFacade _repository;
    private GraphRecordStore Store => _repository.Store;
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1000000, 1);
    public LoadedReferenceWorldTests() => _repository = RepositoryStorageFacade.CreateNew(_root);

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
        Assert.Throws<InvalidDataException>(() => Load(source, unchangedOwner));
        Assert.Equal(1, upgrades); // Stored B ancestry was accepted, then current C ancestry rejected.
        Assert.Equal(0, allocations);

        int targetAllocations = 0;
        StateModelRegistry clearedOwner = Registry(
            Model<World>(ownerV2, () => new World(), ownerV1, upgrade: state => state with { NextId = new ObjectId(0) }),
            Model<Node>(currentTarget, () => { targetAllocations++; return new Node(); }, oldTarget));
        BusinessPersistenceAdapter loaded = Load(source, clearedOwner);
        Assert.Null(((World)loaded.State!).Next);
        Assert.Equal(0, targetAllocations);
        BranchSaveSession session = new(_repository, "main", loaded, _repository.EnsureEditingAllowed);
        using PreparedBranchSave prepared = session.Prepare(loaded.State!, parameters: NoRebase);
        Assert.Equal(ObjectVersionKind.Base, Assert.Single(prepared.Business.Write.Records).Kind);
        Assert.Equal(new uint[] { 1 }, prepared.Commit().Business.ObjectHeads.Keys);
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
        Assert.Throws<InvalidDataException>(() => Load(source, models));
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
            Assert.Throws<InvalidDataException>(() => Load(source, models));
        } else {
            BusinessPersistenceAdapter loaded = Load(source, models);
            BranchSaveSession session = new(_repository, "main", loaded, _repository.EnsureEditingAllowed);
            using PreparedBranchSave prepared = session.Prepare(loaded.State!, parameters: NoRebase);
            Assert.Equal(new uint[] { 9 }, prepared.Business.Write.RemovedIds);
        }
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(99u)]
    public void WrongKindFamilyOrMissingTargetIsRejectedBeforeAllocation(uint reference) {
        DurableSchema worldSchema = Schema("World", 1, "Expected");
        DurableSchema otherSchema = Schema("Other", 1, "Expected");
        PreparedSchemaCatalogWrite catalog = PrepareCatalog([worldSchema, otherSchema]);
        FrameAddress source = Publish(catalog,
            [Durable(catalog.View, 1, worldSchema, new(reference, 1)),
             ObjectVersionRecord.CreateBase(2, TypedBaseBodyCodec.Encode(ObjectLayout.String, StringPayloadCodec.PrepareBase("text"), catalog.View)),
             Durable(catalog.View, 3, otherSchema, new(0, 3))]);
        int allocated = 0;
        StateModelRegistry models = Registry(Model<World>(worldSchema, () => { allocated++; return new World(); }),
            Model<Node>(otherSchema, () => { allocated++; return new Node(); }));
        Assert.Throws<InvalidDataException>(() => Load(source, models));
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
        BusinessPersistenceAdapter? delivered = null;
        Assert.Throws<InvalidDataException>(() => delivered = Load(source, models));
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
        Assert.Throws<InvalidDataException>(() => Load(source, models));
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
        long schemaTail = DataBytes(_repository.Data);
        BusinessPersistenceAdapter? delivered = null;
        Assert.Throws<InvalidDataException>(() => delivered = Load(source, models));
        Assert.Null(delivered);
        Assert.Equal(2, hydrations);
        Assert.Equal(stateTail, Tail());
        Assert.Equal(schemaTail, DataBytes(_repository.Data));
    }

    [Fact]
    public void ExactTypeCollisionLeavesAllCatalogIndexesUnchanged() {
        StateModelRegistry registry = Registry(Model<World>(Schema("First", 1, "First"), () => new World()));
        Assert.Throws<InvalidOperationException>(() => registry.Register(Model<World>(Schema("Second", 1, "Second"), () => new World())));
        ModelExecutionEnvironment snapshot = registry.CreateEnvironment();
        Assert.Single(snapshot.Models);
        Assert.Single(snapshot.Types);
        Assert.Single(snapshot.Readers);
        Assert.False(snapshot.Models.ContainsKey(TypeExpr.Named("Second")));
        StateModelBinding second = Model<Node>(Schema("Second", 1, "Second"), () => new Node());
        registry.Register(second);
        Assert.Same(second, registry.CreateEnvironment().Types[typeof(Node)]);
    }

    [Fact]
    public void UnchangedOwnerReferencesAreValidatedAgainstQueriedRevisionMembership() {
        DurableSchema schema = Schema("World", 1, "World");
        FrameAddress original = Seed((1, schema, new State(2, 1)), (2, schema, new State(0, 2)));
        FrameAddress removed = Store.Append(StoredGraphKind.State, original, Store.ReadGraph(original).SchemaRoot,
            new(original, new(1), [], [2])).Address;
        int allocations = 0;
        StateModelRegistry models = Registry(Model<World>(schema, () => { allocations++; return new World(); }));
        Assert.Throws<InvalidDataException>(() => Load(removed, models));
        Assert.Equal(0, allocations);
        BusinessPersistenceAdapter oldView = Load(original, models);
        Assert.Equal((byte)2, Assert.IsType<World>(((World)oldView.State!).Next).Value);
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
        long schemaTail = DataBytes(_repository.Data);
        long stateTail = Tail();
        BranchSaveSession session = new(_repository, "main", models);
        Assert.Throws<InvalidDataException>(() => session.Prepare(world, parameters: NoRebase));
        Assert.Equal(3, captures);
        Assert.Equal(schemaTail, DataBytes(_repository.Data));
        Assert.Equal(stateTail, Tail());

        lateChild.Value = 3;
        using PreparedBranchSave repaired = session.Prepare(world, parameters: NoRebase);
        Assert.Equal(6, captures);
        Assert.Null(repaired.Business.Write.SourceGraph);
        Assert.Equal(3, repaired.Business.Write.Records.Count);
        Assert.All(repaired.Business.Write.Records, static row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
        Assert.Equal(stateTail, Tail()); // Joint preparation does not separately append Schema.
        FrameAddress address = repaired.Commit().Business.Address;
        Assert.True(Tail() > stateTail);
        BusinessPersistenceAdapter loaded = Load(address, models);
        Assert.Equal((byte)3, Assert.IsType<Node>(((World)loaded.State!).Next!.Next).Value);
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
        BusinessPersistenceAdapter loaded = Load(address, models);
        Domain? cursor = ((World)loaded.State!);
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
            static (ref BareValueReader reader) => new(reader.ReadVarUInt32(), reader.ReadByte()),
            static (ref BareValueReader reader, in State prior) => new(reader.ReadVarUInt32(), reader.ReadByte()),
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
    private BusinessPersistenceAdapter Load(FrameAddress source, StateModelRegistry models) =>
        BusinessPersistenceAdapter.LoadState(Store, source, models);
    private FrameAddress Seed(params (uint Id, DurableSchema Schema, State State)[] rows) {
        PreparedSchemaCatalogWrite catalog = PrepareCatalog(rows.Select(static row => row.Schema));
        return Publish(catalog, rows.Select(row => Durable(catalog.View, row.Id, row.Schema, row.State)));
    }
    private PreparedSchemaCatalogWrite PrepareCatalog(IEnumerable<DurableSchema> schemas) {
        SchemaCatalogWorkspace catalog = new(_repository.EnsureEditingAllowed);
        foreach (DurableSchema schema in schemas) { catalog.Set(schema); }
        return catalog.Prepare();
    }
    private FrameAddress Publish(PreparedSchemaCatalogWrite catalog, IEnumerable<ObjectVersionRecord> records) {
        using PendingRepositorySave pending = _repository.Prepare("main", StoredGraphKind.State, new(null, new(1), records), catalog.Write);
        return _repository.Commit(pending, catalog.Install, prepareMeta: catalog.PrepareInstall).Business.Address;
    }
    private static ObjectVersionRecord Durable(SchemaCatalogView catalog, uint id, DurableSchema schema, State state) =>
        ObjectVersionRecord.CreateBase(id, TypedBaseBodyCodec.Encode(ObjectLayout.ForDurable(schema), Base(state), catalog));
    private static PreparedBaseBody Base(State state) {
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        writer.WriteVarUInt32(state.NextId.Value);
        writer.WriteByte(state.Value);
        return new(bytes.WrittenSpan);
    }
    private long Tail() => DataBytes(_repository.Data);
    public void Dispose() {
        _repository.Dispose();
        string resolved = Path.GetFullPath(_root);
        if (!string.Equals(Path.GetDirectoryName(resolved), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("durable-graph-reference-world-", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Refusing to delete outside this test fixture.");
        }
        Directory.Delete(resolved, recursive: true);
    }
}
