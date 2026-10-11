using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.FrameStore;
using SegmentStore = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class GraphReaderTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"durable-graph-reader-{Guid.NewGuid():N}");
    private readonly GraphResources _file;
    private readonly SegmentStore _segments;
    private readonly SchemaStore _schemas;
    private readonly StateRevisionStore _store;
    private static readonly DurableSchema ParentSchema = new("ReaderNode", 1);
    private static readonly DurableSchema FirstSchema = Schema("ReaderFirst");
    private static readonly DurableSchema SecondSchema = Schema("ReaderSecond");

    public GraphReaderTests() {
        Directory.CreateDirectory(_root);
        _file = GraphResources.CreateNew(Path.Combine(_root, "schemas.rbf"));
        _schemas = _file.Schemas;
        _segments = _file.Data;
        _store = new(_segments);
    }

    [Fact]
    public void PairRestoresInputOrderActualTypesAndEachGraphsAliasesAndCycles() {
        FrameAddress first = Seed((1, FirstSchema, new(2, 2, 10)), (2, SecondSchema, new(1, 1, 11)));
        FrameAddress second = Seed((1, FirstSchema, new(2, 2, 20)), (2, SecondSchema, new(1, 1, 21)));
        StateModelRegistry registry = Registry(Model<FirstNode>(FirstSchema, () => new()), Model<SecondNode>(SecondSchema, () => new()));
        StateModelSnapshot models = registry.Snapshot(_schemas);
        long stateTail = Tail();
        long schemaTail = DataBytes(_file.Data);

        // Requested roots can be a durable base; restoration still uses the actual model.
        (Node First, Node Second) pair = GraphReader.ReadPair<Node, Node>(
            _store, _schemas, second, new(1), first, new(2), models);

        Assert.IsType<FirstNode>(pair.First);
        Assert.IsType<SecondNode>(pair.Second);
        Assert.Equal(20, pair.First.Value);
        Assert.Equal(11, pair.Second.Value);
        Assert.Same(pair.First.Next, pair.First.Alias);
        Assert.Same(pair.First, pair.First.Next!.Next);
        Assert.Same(pair.Second.Next, pair.Second.Alias);
        Assert.Same(pair.Second, pair.Second.Next!.Next);
        // No assertion about identity between the two graphs: it is deliberately not promised.
        Assert.Equal(stateTail, Tail());
        Assert.Equal(schemaTail, DataBytes(_file.Data));
    }

    [Fact]
    public void SameSelectionTwiceIsLegalWithoutCrossGraphIdentityContract() {
        FrameAddress address = Seed((1, FirstSchema, new(1, 1, 4)));
        StateModelSnapshot models = Registry(Model<FirstNode>(FirstSchema, () => new())).Snapshot(_schemas);
        var pair = GraphReader.ReadPair<FirstNode, FirstNode>(_store, _schemas, address, new(1), address, new(1), models);
        Assert.Equal(4, pair.First.Value);
        Assert.Equal(4, pair.Second.Value);
        Assert.Same(pair.First, pair.First.Next);
        Assert.Same(pair.Second, pair.Second.Next);
    }

    [Fact]
    public void SecondHydrationFailureDeliversNoPairAndDoesNotWrite() {
        FrameAddress first = Seed((1, FirstSchema, new(0, 0, 1)));
        FrameAddress second = Seed((1, SecondSchema, new(0, 0, 2)));
        List<string> callbacks = [];
        StateModelSnapshot models = Registry(
            Model<FirstNode>(FirstSchema, () => new(), () => callbacks.Add("first")),
            Model<SecondNode>(SecondSchema, () => new(), () => {
                callbacks.Add("second");
                throw new InvalidDataException("second hydration failed");
            })).Snapshot(_schemas);
        (Node First, Node Second)? delivered = null;
        long stateTail = Tail();
        long schemaTail = DataBytes(_file.Data);

        Assert.Throws<InvalidDataException>(() => delivered = GraphReader.ReadPair<Node, Node>(
            _store, _schemas, first, new(1), second, new(1), models));

        Assert.Null(delivered);
        Assert.Equal(["first", "second"], callbacks);
        Assert.Equal(stateTail, Tail());
        Assert.Equal(schemaTail, DataBytes(_file.Data));
    }

    [Fact]
    public void MissingWrongOrNonExactRootFailsBeforeAllocation() {
        FrameAddress address = Seed((1, FirstSchema, new(0, 0, 1)));
        int allocations = 0;
        StateModelSnapshot models = Registry(Model<FirstNode>(FirstSchema, () => { allocations++; return new(); })).Snapshot(_schemas);
        Assert.Throws<InvalidDataException>(() => GraphReader.Read<Node>(_store, _schemas, address, new(99), models));
        Assert.Throws<InvalidDataException>(() => GraphReader.Read<SecondNode>(_store, _schemas, address, new(1), models));
        Assert.Throws<InvalidDataException>(() => GraphReader.Read<Node>(_store, _schemas, address, new(1), models, requireExactRootType: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphReader.Read<Node>(_store, _schemas, address, default, models));
        Assert.Equal(0, allocations);
    }

    [Fact]
    public void IndependentMembershipDoesNotRunUnrelatedParentReadersOrModels() {
        FrameAddress parent = Seed((1, FirstSchema, new(0, 0, 1)), (9, SecondSchema, new(0, 0, 9)));
        FrameAddress child = _store.Append(StateRevision.CreateObjectHeadMapBase(parent, [], [new(1, parent)]));
        // No SecondNode reader/model is registered at all. Reading the whole parent fails,
        // while this independent selection uses its own membership and only first's old head.
        int hydrations = 0;
        StateModelSnapshot models = Registry(Model<FirstNode>(FirstSchema, () => new(), () => hydrations++)).Snapshot(_schemas);
        FirstNode root = GraphReader.Read<FirstNode>(_store, _schemas, child, new(1), models).Root;
        Assert.Equal(1, root.Value);
        Assert.Equal(1, hydrations);
        Assert.Throws<InvalidDataException>(() => GraphReader.Read<FirstNode>(_store, _schemas, parent, new(1), models));
        Assert.Equal(1, hydrations);
    }

    private abstract class Node : IDurableObject {
        internal Node? Next;
        internal Node? Alias;
        internal byte Value;
    }
    private sealed class FirstNode : Node { }
    private sealed class SecondNode : Node { }
    private readonly record struct State(ObjectId Next, ObjectId Alias, byte Value) {
        internal State(uint next, uint alias, byte value) : this(new ObjectId(next), new ObjectId(alias), value) { }
    }

    private static DurableSchema Schema(string id) => new(id, 1,
        [new(1, TypeTag.ObjectReference, ParentSchema.SchemaId), new(2, TypeTag.ObjectReference, ParentSchema.SchemaId), new(3, TypeTag.Byte)], ParentSchema);

    private static StateModelBinding Model<T>(DurableSchema schema, Func<T> allocate, Action? onHydrate = null) where T : Node {
        static void Visit(in State state, IStateReferenceVisitor visitor) {
            visitor.VisitDurable(state.Next, ParentSchema.SchemaId);
            visitor.VisitDurable(state.Alias, ParentSchema.SchemaId);
        }
        CapturedStatePreparation<State> preparation = new(schema,
            static (in State state) => Base(state),
            static (in State prior, in State next) => new(prior != next, Base(next).Body));
        StateReaderBinding reader = new StateReaderBinding<State>(schema,
            static (ref BareValueReader reader) => new(reader.ReadVarUInt32(), reader.ReadVarUInt32(), reader.ReadByte()),
            static (ref BareValueReader reader, in State prior) => new(reader.ReadVarUInt32(), reader.ReadVarUInt32(), reader.ReadByte()), Visit);
        return new StateModelBinding<T, State>(preparation, [reader], row => row.GetState<State>(), allocate,
            (T domain, in State state, ObjectReadTable objects) => {
                onHydrate?.Invoke();
                domain.Next = objects.ResolveDurable<Node>(state.Next);
                domain.Alias = objects.ResolveDurable<Node>(state.Alias);
                domain.Value = state.Value;
            },
            (domain, context) => new(context.CaptureDurable(domain.Next, ParentSchema.SchemaId),
                context.CaptureDurable(domain.Alias, ParentSchema.SchemaId), domain.Value), Visit);
    }

    private static StateModelRegistry Registry(params StateModelBinding[] models) {
        StateModelRegistry registry = new();
        foreach (StateModelBinding model in models) registry.Register(model);
        return registry;
    }

    private FrameAddress Seed(params (uint Id, DurableSchema Schema, State State)[] rows) {
        _schemas.RegisterBatch(rows.Select(static row => row.Schema));
        return _store.Append(StateRevision.CreateObjectHeadMapBase(null, rows.Select(row => ObjectVersionRecord.CreateBase(row.Id,
            BaseObjectBodyCodec.Encode(_schemas.RegisterRepresentations([ObjectLayout.ForDurable(row.Schema)])[0], Base(row.State)).Body)), []));
    }

    private static PreparedBaseBody Base(State state) {
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        writer.WriteVarUInt32(state.Next.Value);
        writer.WriteVarUInt32(state.Alias.Value);
        writer.WriteByte(state.Value);
        return new(bytes.WrittenSpan);
    }

    private long Tail() => DataBytes(_segments);

    public void Dispose() {
        _store.Dispose();
        _file.Dispose();
        _segments.Dispose();
        string resolved = Path.GetFullPath(_root);
        if (!string.Equals(Path.GetDirectoryName(resolved), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("durable-graph-reader-", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Refusing to delete outside this test fixture.");
        }
        Directory.Delete(resolved, recursive: true);
    }
}
