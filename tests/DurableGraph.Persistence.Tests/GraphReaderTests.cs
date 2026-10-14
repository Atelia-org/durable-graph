
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class GraphReaderTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"durable-graph-reader-{Guid.NewGuid():N}");
    private readonly RepositoryStorageFacade _writer;
    private int _seedIndex;
    private static readonly DurableSchema ParentSchema = new("ReaderNode", 1);
    private static readonly DurableSchema FirstSchema = Schema("ReaderFirst");
    private static readonly DurableSchema SecondSchema = Schema("ReaderSecond");

    public GraphReaderTests() => _writer = RepositoryStorageFacade.CreateNew(_root);

    [Fact]
    public void PairRestoresInputOrderActualTypesAndEachGraphsAliasesAndCycles() {
        FrameAddress first = Seed((1, FirstSchema, new(2, 2, 10)), (2, SecondSchema, new(1, 1, 11)));
        FrameAddress second = Seed((1, FirstSchema, new(2, 2, 20)), (2, SecondSchema, new(1, 1, 21)));
        StateModelRegistry registry = Registry(Model<FirstNode>(FirstSchema, () => new()), Model<SecondNode>(SecondSchema, () => new()));
        using RepositoryStorageFacade reader = OpenReader();
        long tail = DataBytes(reader.Data);
        StateModelRegistry.FrozenConfiguration configuration = registry.FreezeConfiguration();
        DecodedRevision secondSource = DecodedRevision.ReadStoredGraph(reader.Store, second, configuration);
        DecodedRevision firstSource = DecodedRevision.ReadStoredGraph(reader.Store, first, configuration, secondSource.BindingEnvironment);
        Assert.Same(secondSource.BindingEnvironment, firstSource.BindingEnvironment);

        // Input roots can select durable subclasses; restoration uses their actual models.
        var pair = GraphReader.ReadPair(secondSource, new(secondSource.SourceModels!), new(1),
            firstSource, new(firstSource.SourceModels!), new(2));
        Node firstRoot = Assert.IsType<FirstNode>(pair.First);
        Node secondRoot = Assert.IsType<SecondNode>(pair.Second);
        Assert.Equal(20, firstRoot.Value);
        Assert.Equal(11, secondRoot.Value);
        Assert.Same(firstRoot.Next, firstRoot.Alias);
        Assert.Same(firstRoot, firstRoot.Next!.Next);
        Assert.Same(secondRoot.Next, secondRoot.Alias);
        Assert.Same(secondRoot, secondRoot.Next!.Next);
        // No assertion about identity between graphs: it is deliberately not promised.
        Assert.Equal(tail, DataBytes(reader.Data));
    }

    [Fact]
    public void SameSelectionTwiceIsLegalWithoutCrossGraphIdentityContract() {
        FrameAddress address = Seed((1, FirstSchema, new(1, 1, 4)));
        StateModelRegistry registry = Registry(Model<FirstNode>(FirstSchema, () => new()));
        using RepositoryStorageFacade reader = OpenReader();
        long tail = DataBytes(reader.Data);
        StateModelRegistry.FrozenConfiguration configuration = registry.FreezeConfiguration();
        DecodedRevision first = DecodedRevision.ReadStoredGraph(reader.Store, address, configuration);
        DecodedRevision second = DecodedRevision.ReadStoredGraph(reader.Store, address, configuration, first.BindingEnvironment);
        Assert.Same(first.BindingEnvironment, second.BindingEnvironment);
        var pair = GraphReader.ReadPair(first, new(first.SourceModels!), new(1), second, new(second.SourceModels!), new(1));
        FirstNode firstRoot = Assert.IsType<FirstNode>(pair.First);
        FirstNode secondRoot = Assert.IsType<FirstNode>(pair.Second);
        Assert.Equal(4, firstRoot.Value);
        Assert.Equal(4, secondRoot.Value);
        Assert.Same(firstRoot, firstRoot.Next);
        Assert.Same(secondRoot, secondRoot.Next);
        Assert.Equal(tail, DataBytes(reader.Data));
    }

    [Fact]
    public void SecondHydrationFailureDeliversNoPairAndDoesNotWrite() {
        FrameAddress first = Seed((1, FirstSchema, new(0, 0, 1)));
        FrameAddress second = Seed((1, SecondSchema, new(0, 0, 2)));
        List<string> callbacks = [];
        StateModelRegistry registry = Registry(
            Model<FirstNode>(FirstSchema, () => new(), () => callbacks.Add("first")),
            Model<SecondNode>(SecondSchema, () => new(), () => {
                callbacks.Add("second");
                throw new InvalidDataException("second hydration failed");
            }));
        using RepositoryStorageFacade reader = OpenReader();
        long tail = DataBytes(reader.Data);
        StateModelRegistry.FrozenConfiguration configuration = registry.FreezeConfiguration();
        DecodedRevision firstSource = DecodedRevision.ReadStoredGraph(reader.Store, first, configuration);
        DecodedRevision secondSource = DecodedRevision.ReadStoredGraph(reader.Store, second, configuration, firstSource.BindingEnvironment);
        (IDurableObject First, IDurableObject Second)? delivered = null;

        Assert.Throws<InvalidDataException>(() => delivered = GraphReader.ReadPair(
            firstSource, new(firstSource.SourceModels!), new(1), secondSource, new(secondSource.SourceModels!), new(1)));

        Assert.Null(delivered);
        Assert.Equal(["first", "second"], callbacks);
        Assert.Equal(tail, DataBytes(reader.Data));
    }

    [Fact]
    public void MissingWrongOrNonExactRootFailsBeforeAllocation() {
        FrameAddress address = Seed((1, FirstSchema, new(0, 0, 1)));
        int allocations = 0;
        StateModelRegistry registry = Registry(Model<FirstNode>(FirstSchema, () => { allocations++; return new(); }));
        using RepositoryStorageFacade reader = OpenReader();
        long tail = DataBytes(reader.Data);
        DecodedRevision source = DecodedRevision.ReadStoredGraph(reader.Store, address, registry);
        Assert.Throws<InvalidDataException>(() => GraphReader.Read<Node>(source, new(99), new(source.SourceModels!)));
        Assert.Throws<InvalidDataException>(() => GraphReader.Read<SecondNode>(source, new(1), new(source.SourceModels!)));
        Assert.Throws<InvalidDataException>(() => GraphReader.Read<Node>(source, new(1), new(source.SourceModels!), requireExactRootType: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphReader.Read<Node>(source, default, new(source.SourceModels!)));
        Assert.Equal(0, allocations);
        Assert.Equal(tail, DataBytes(reader.Data));
    }

    [Fact]
    public void IndependentMembershipDoesNotRunUnrelatedParentReadersOrModels() {
        FrameAddress parent = Seed((1, FirstSchema, new(0, 0, 1)), (9, SecondSchema, new(0, 0, 9)));
        StoredGraphRevision parentGraph = _writer.Store.ReadGraph(parent);
        FrameAddress child;
        // The child has its own actual complete membership, retaining only first's
        // existing head. Directory reconstruction never invokes unrelated body readers.
        using (PendingRepositorySave pending = _writer.Prepare("seed-0", StoredGraphKind.State,
            new(parent, new(1), [], [9]))) {
            child = _writer.Commit(pending).Business.Address;
        }
        Assert.Equal(new uint[] { 1 }, _writer.Store.ReadGraph(child).ObjectHeads.Keys);
        Assert.Equal(parentGraph.ObjectHeads[1], _writer.Store.ReadGraph(child).ObjectHeads[1]);
        // No SecondNode reader/model is registered. Reading the complete parent fails;
        // the selected child only restores its own membership and retained old first head.
        int hydrations = 0;
        StateModelRegistry registry = Registry(Model<FirstNode>(FirstSchema, () => new(), () => hydrations++));
        using RepositoryStorageFacade reader = OpenReader();
        long tail = DataBytes(reader.Data);
        StateModelRegistry.FrozenConfiguration configuration = registry.FreezeConfiguration();
        DecodedRevision source = DecodedRevision.ReadStoredGraph(reader.Store, child, configuration);
        FirstNode root = GraphReader.Read<FirstNode>(source, new(1), new(source.SourceModels!)).Root;
        Assert.Equal(1, root.Value);
        Assert.Equal(1, hydrations);
        Assert.Throws<InvalidDataException>(() => DecodedRevision.ReadStoredGraph(reader.Store, parent, configuration, source.BindingEnvironment));
        Assert.Equal(1, hydrations);
        Assert.Equal(tail, DataBytes(reader.Data));
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
        StatePreparationBinding<State> preparation = new(schema,
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
        SchemaCatalogView? accepted = _writer.SharedSchemaRoot is { } root ? SchemaCatalogView.Load(_writer.Store, root) : null;
        SchemaCatalogWorkspace catalog = new(_writer.EnsureEditingAllowed, accepted);
        // Both seeds use one real immutable catalog, including the nominal base closure.
        catalog.Set(FirstSchema);
        catalog.Set(SecondSchema);
        PreparedSchemaCatalogWrite meta = catalog.Prepare();
        ObjectVersionRecord[] records = rows.Select(row => ObjectVersionRecord.CreateBase(row.Id,
            TypedBaseBodyCodec.Encode(ObjectLayout.ForDurable(row.Schema), Base(row.State), meta.View))).ToArray();
        string branch = $"seed-{_seedIndex++}";
        using PendingRepositorySave pending = _writer.Prepare(branch, StoredGraphKind.State, new(null, new(1), records), meta.Write);
        RepositorySaveResult saved = _writer.Commit(pending, meta.Install, prepareMeta: meta.PrepareInstall);
        Assert.Equal(saved.Business.Address, _writer.GetHead(branch));
        Assert.Equal(saved.Business.SchemaRoot, _writer.SharedSchemaRoot);
        return saved.Business.Address;
    }

    private RepositoryStorageFacade OpenReader() {
        _writer.Dispose();
        return RepositoryStorageFacade.OpenReadOnlyExisting(_root);
    }

    private static PreparedBaseBody Base(State state) {
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        writer.WriteVarUInt32(state.Next.Value);
        writer.WriteVarUInt32(state.Alias.Value);
        writer.WriteByte(state.Value);
        return new(bytes.WrittenSpan);
    }

    public void Dispose() {
        _writer.Dispose();
        string resolved = Path.GetFullPath(_root);
        if (!string.Equals(Path.GetDirectoryName(resolved), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("durable-graph-reader-", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Refusing to delete outside this test fixture.");
        }
        Directory.Delete(resolved, recursive: true);
    }
}
