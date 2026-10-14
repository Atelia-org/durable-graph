
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.FrameStore;
using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;
using State = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.State;

namespace Atelia.DurableGraph.Persistence.Tests;

/// <summary>White-box optimization witnesses; public ReadPair does not guarantee cross-graph identity.</summary>
public sealed class SharedGraphReaderTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"durable-shared-reader-{Guid.NewGuid():N}");
    private readonly RepositoryStorageFacade _repository;
    private readonly PreparedSchemaCatalogWrite _initialCatalog;
    private readonly Dictionary<FrameAddress, string> _branches = [];
    private GraphRecordStore Store => _repository.Store;
    private int _nextBranch;
    public SharedGraphReaderTests() {
        _repository = RepositoryStorageFacade.CreateNew(_root);
        SchemaCatalogWorkspace catalog = new(_repository.EnsureEditingAllowed);
        catalog.Set(SharedReadModel.Schema);
        _initialCatalog = catalog.Prepare();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SingleAndPairReadsDoNotPrepareBodiesEvenWithoutEquality(bool includeEquality) {
        FrameAddress first = Seed((1, new(2, 2, 0, 10)), (2, new(1, 1, 0, 20)));
        FrameAddress second = Reuse(first, 1, 2);
        int bases = 0, deltas = 0;
        StateModelRegistry models = SharedReadModel.Models(includeEquality: includeEquality,
            prepareBase: (in State _) => { bases++; throw new NotSupportedException("No Base writer for this read."); },
            prepareDelta: (in State _, in State _) => { deltas++; throw new NotSupportedException("No Delta writer for this read."); });


        Node single = Read(first, new(1), models);
        var pair = ReadPair(first, new(1), second, new(1), models);

        Assert.Equal((byte)10, single.Value);
        Assert.Same(single, single.Next!.Next);
        foreach (Node root in new[] { pair.First, pair.Second }) {
            Assert.Equal((byte)10, root.Value);
            Assert.Equal((byte)20, root.Next!.Value);
            Assert.Same(root, root.Next.Next);
            Assert.Same(root.Next, root.Alias);
        }
        if (includeEquality) {
            Assert.Same(pair.First, pair.Second);
        } else {
            Assert.NotSame(pair.First, pair.Second);
            Assert.NotSame(pair.First.Next, pair.Second.Next);
        }
        Assert.Equal(0, bases);
        Assert.Equal(0, deltas);
    }

    [Fact]
    public void UnprovenCycleExcludesItsOwnersButNotAnIndependentProvenLeaf() {
        FrameAddress address = Seed((1, new(2, 3, 0, 10)), (2, new(1, 0, 0, 20)), (3, new(0, 0, 0, 30)));
        StateModelRegistry models = SharedReadModel.Models(
            equality: static (in State left, in State right) => left.Value != 20 && left == right);

        var pair = ReadPair(address, new(1), address, new(1), models);

        Assert.NotSame(pair.First, pair.Second);
        Assert.NotSame(pair.First.Next, pair.Second.Next);
        Assert.Same(pair.First, pair.First.Next!.Next);
        Assert.Same(pair.Second, pair.Second.Next!.Next);
        Assert.Same(pair.First.Alias, pair.Second.Alias);
    }

    [Fact]
    public void ComparisonFailurePropagatesWithoutEncodingAllocationOrRetry() {
        FrameAddress address = Seed((1, new(2, 0, 0, 10)), (2, new(1, 0, 0, 20)));
        int normalizations = 0, comparisons = 0, allocations = 0, hydrations = 0, preparations = 0;
        NotSupportedException failure = new("A registered equality failed; this is not a missing capability.");
        StateModelRegistry models = SharedReadModel.Models(
            normalize: row => { normalizations++; return row.GetState<State>(); },
            equality: (in State _, in State _) => { comparisons++; throw failure; },
            allocate: () => { allocations++; return new(); }, hydrate: _ => hydrations++,
            prepareBase: (in State state) => { preparations++; return SharedReadModel.Base(state); });

        (Node First, Node Second)? delivered = null;

        Assert.Same(failure, Assert.Throws<NotSupportedException>(() => delivered = ReadPair(address, new(1), address, new(1), models)));

        Assert.Null(delivered);
        Assert.Equal(0, normalizations);
        Assert.Equal(1, comparisons);
        Assert.Equal(0, preparations);
        Assert.Equal(0, allocations);
        Assert.Equal(0, hydrations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StableCycleAllocatesAndHydratesOnlyOneCopyAcrossDifferentRevisionViews(bool reverse) {
        FrameAddress first = Seed((1, new(2, 2, 0, 10)), (2, new(1, 1, 0, 20)));
        FrameAddress second = Reuse(first, 1, 2);
        int allocations = 0, hydrations = 0;
        StateModelRegistry models = SharedReadModel.Models(allocate: () => { allocations++; return new(); },
            hydrate: _ => hydrations++);
        GraphReadStatistics stats = new();

        var pair = ReadPair(
            reverse ? second : first, new(reverse ? 2U : 1U),
            reverse ? first : second, new(reverse ? 1U : 2U), models, stats);

        Assert.Equal(reverse ? (byte)20 : (byte)10, pair.First.Value);
        Assert.Equal(reverse ? (byte)10 : (byte)20, pair.Second.Value);
        Assert.Same(pair.First.Next, pair.Second);
        Assert.Same(pair.Second.Next, pair.First);
        Assert.Same(pair.First.Next, pair.First.Alias);
        Assert.Equal(2, allocations);
        Assert.Equal(2, hydrations);
        Assert.Equal(2, stats.PreparedGraphs);
        Assert.Equal(4, stats.NormalizedObjects);
        Assert.Equal(2, stats.AllocatedObjects);
        Assert.Equal(2, stats.HydratedObjects);
        Assert.Equal(2, stats.SharedObjects);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RewrittenChildSplitsItsEntireCycleEvenWhenItsNewBytesAreEqual(bool equalBytes, bool reverse) {
        State originalChild = new(1, 1, 0, 20);
        FrameAddress first = Seed((1, new(2, 2, 0, 10)), (2, originalChild));
        State nextChild = equalBytes ? originalChild : originalChild with { Value = 30 };
        FrameAddress second = Append(first, [Write(2, nextChild)]);
        Assert.Equal(Store.ReadGraph(first).ObjectHeads[1], Store.ReadGraph(second).ObjectHeads[1]);
        GraphReadStatistics stats = new();

        var pair = ReadPair(
            reverse ? second : first, new(1), reverse ? first : second, new(1),
            SharedReadModel.Models(), stats);

        Assert.NotSame(pair.First, pair.Second);
        Assert.NotSame(pair.First.Next, pair.Second.Next);
        Assert.Same(pair.First, pair.First.Next!.Next);
        Assert.Same(pair.Second, pair.Second.Next!.Next);
        Assert.Equal(reverse && !equalBytes ? (byte)30 : (byte)20, pair.First.Next.Value);
        Assert.Equal(!reverse && !equalBytes ? (byte)30 : (byte)20, pair.Second.Next.Value);
        Assert.Equal(2, stats.PreparedGraphs);
        Assert.Equal(4, stats.NormalizedObjects);
        Assert.Equal(4, stats.AllocatedObjects);
        Assert.Equal(4, stats.HydratedObjects);
        Assert.Equal(0, stats.SharedObjects);
    }

    [Fact]
    public void IndependentOwnersCanStillShareAnUnchangedLeaf() {
        FrameAddress first = Seed((1, new(2, 2, 0, 10)), (2, new(0, 0, 0, 20)));
        FrameAddress second = Append(first, [Write(1, new(2, 2, 0, 11))]);
        GraphReadStatistics stats = new();

        var pair = ReadPair(first, new(1), second, new(1),
            SharedReadModel.Models(), stats);

        Assert.NotSame(pair.First, pair.Second);
        Assert.Same(pair.First.Next, pair.Second.Next);
        Assert.Equal(3, stats.AllocatedObjects);
        Assert.Equal(3, stats.HydratedObjects);
        Assert.Equal(1, stats.SharedObjects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentNormalizationPreservesCompleteValuesAndReferencesWithoutHistoricalCallback(bool changeReference) {
        FrameAddress address = Seed((1, new(2, 2, 0, 10)), (2, new(3, 0, 0, 20)), (3, new(0, 0, 0, 30)));
        int normalizations = 0;
        StateModelRegistry models = SharedReadModel.Models(normalize: row => {
            normalizations++;
            State state = row.GetState<State>();
            return changeReference ? state with { Next = default } : state with { Value = 21 };
        });
        GraphReadStatistics stats = new();

        var pair = ReadPair(address, new(1), address, new(1), models, stats);

        Assert.Equal(0, normalizations);
        Assert.Same(pair.First, pair.Second);
        Assert.Same(pair.First.Next, pair.Second.Next);
        Assert.Equal((byte)20, pair.First.Next!.Value);
        Assert.Same(pair.First.Next, pair.First.Alias);
        Assert.NotNull(pair.First.Next.Next);
        Assert.Equal((byte)30, pair.First.Next.Next.Value);
        Assert.Same(pair.First.Next.Next, pair.Second.Next!.Next);
        Assert.Equal(3, stats.SharedObjects);
        Assert.Equal(2, stats.PreparedGraphs);
        Assert.Equal(6, stats.NormalizedObjects);
    }

    [Fact]
    public void UpgradedNodesRemainIndependentEvenWhenTheirCurrentBytesMatch() {
        FrameAddress address = Seed((1, new(2, 0, 0, 10)), (2, new(1, 0, 0, 20)));
        int normalizations = 0;
        StateModelRegistry models = SharedReadModel.Models(currentVersion: 2,
            normalize: row => { normalizations++; return row.GetState<State>(); });
        GraphReadStatistics stats = new();

        var pair = ReadPair(address, new(1), address, new(1), models, stats);

        Assert.Equal(4, normalizations);
        Assert.Equal(2, stats.PreparedGraphs);
        Assert.Equal(4, stats.NormalizedObjects);
        Assert.NotSame(pair.First, pair.Second);
        Assert.NotSame(pair.First.Next, pair.Second.Next);
        Assert.Same(pair.First, pair.First.Next!.Next);
        Assert.Same(pair.Second, pair.Second.Next!.Next);
        Assert.Equal(4, stats.AllocatedObjects);
        Assert.Equal(0, stats.SharedObjects);
    }

    [Fact]
    public void UpgradeCuttingAnEdgeDoesNotExcuseAnInvalidSourceRowInTheSecondView() {
        FrameAddress first = Seed((1, new(2, 0, 0, 10)), (2, new(3, 0, 0, 20)), (3, new(0, 0, 0, 30)));
        FrameAddress second = Reuse(first, 1, 2);
        StateModelRegistry models = SharedReadModel.Models(currentVersion: 2,
            normalize: row => row.GetState<State>() with { Next = default, Alias = default });
        long stateTail = Tail(), schemaTail = DataBytes(_repository.Data);
        (Node First, Node Second)? delivered = null;

        Assert.Throws<InvalidDataException>(() => delivered = ReadPair(first, new(1), second, new(1), models));

        Assert.Null(delivered);
        Assert.Equal(stateTail, Tail());
        Assert.Equal(schemaTail, DataBytes(_repository.Data));
    }

    [Fact]
    public void SecondHydrationFailureDoesNotDeliverHalfAPairOrWriteEitherStore() {
        FrameAddress first = Seed((1, new(0, 0, 0, 10)));
        FrameAddress second = Seed((1, new(0, 0, 0, 20)));
        List<byte> hydratedValues = [];
        StateModelRegistry models = SharedReadModel.Models(hydrate: node => {
            hydratedValues.Add(node.Value);
            if (node.Value == 20) throw new InvalidDataException("Second view hydration failed.");
        });
        long stateTail = Tail(), schemaTail = DataBytes(_repository.Data);
        (Node First, Node Second)? delivered = null;

        Assert.Throws<InvalidDataException>(() => delivered = ReadPair(first, new(1), second, new(1), models));

        Assert.Null(delivered);
        Assert.Equal(new byte[] { 10, 20 }, hydratedValues);
        Assert.Equal(stateTail, Tail());
        Assert.Equal(schemaTail, DataBytes(_repository.Data));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AllocatorCannotMergeDifferentObjectIdsOrDifferentHeads(bool distinctIds) {
        FrameAddress first = Seed((1, new(0, 0, 0, 10)));
        uint secondId = distinctIds ? 2U : 1U;
        FrameAddress second = Seed((secondId, new(0, 0, 0, 20)));
        Node singleton = new();
        int hydrations = 0;
        StateModelRegistry models = SharedReadModel.Models(allocate: () => singleton,
            hydrate: _ => hydrations++);

        Assert.Throws<InvalidDataException>(() => ReadPair(first, new(1), second, new(secondId), models));
        Assert.Equal(0, hydrations);
    }

    [Fact]
    public void StringSharingUsesObjectVersionIdentityWithoutInterningEqualDifferentIds() {
        FrameAddress address = Append(null,
            [Write(1, new(2, 0, 10, 1)), Write(2, new(0, 0, 11, 2)),
             String(10, new string('s', 12)), String(11, new string('s', 12))]);
        var pair = ReadPair(address, new(1), address, new(1),
            SharedReadModel.Models());

        Assert.Same(pair.First.Text, pair.Second.Text);
        Assert.Same(pair.First.Next!.Text, pair.Second.Next!.Text);
        Assert.Equal(pair.First.Text, pair.First.Next.Text);
        Assert.NotSame(pair.First.Text, pair.First.Next.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RewrittenEqualStringsKeepVersionIdentityExceptCanonicalEmpty(bool empty) {
        string value = empty ? string.Empty : new string('v', 12);
        FrameAddress first = Append(null,
            [Write(1, new(2, 0, 10, 1)), Write(2, new(0, 0, 11, 2)), String(10, value), String(11, value)]);
        FrameAddress second = Append(first, [String(10, value), String(11, value)]);

        var pair = ReadPair(first, new(1), second, new(1),
            SharedReadModel.Models());

        Assert.Equal(value, pair.First.Text);
        Assert.Equal(value, pair.Second.Text);
        if (empty) {
            // Empty's global canonical identity is the explicit exception across both ID and head.
            Assert.Same(string.Empty, pair.First.Text);
            Assert.Same(string.Empty, pair.First.Next!.Text);
            Assert.Same(string.Empty, pair.Second.Text);
            Assert.Same(string.Empty, pair.Second.Next!.Text);
        } else {
            Assert.NotSame(pair.First.Text, pair.Second.Text);
            Assert.NotSame(pair.First.Next!.Text, pair.Second.Next!.Text);
            Assert.NotSame(pair.First.Text, pair.First.Next.Text);
            Assert.NotSame(pair.Second.Text, pair.Second.Next.Text);
        }
    }

    private FrameAddress Seed(params (uint Id, State State)[] rows) => Append(null, rows.Select(row => Write(row.Id, row.State)));
    private FrameAddress Reuse(FrameAddress previous, params uint[] ids) => Append(previous, [],
        Store.ReadGraph(previous).ObjectHeads.Keys.Except(ids));
    private FrameAddress Append(FrameAddress? previous, IEnumerable<ObjectVersionRecord> records, IEnumerable<uint>? removed = null) {
        ObjectVersionRecord[] rows = records.ToArray();
        string branch = previous is { } source ? _branches[source] : $"source-{_nextBranch++}";
        ObjectId root = previous is { } prior ? Store.ReadGraph(prior).RootId : new(rows.Min(row => row.ObjectId));
        bool initializeMeta = _repository.SharedSchemaRoot is null;
        using PendingRepositorySave pending = _repository.Prepare(branch, StoredGraphKind.State, new(previous, root, rows, removed),
            initializeMeta ? _initialCatalog.Write : null);
        StoredGraphRevision graph = _repository.Commit(pending,
            initializeMeta ? _initialCatalog.Install : null,
            prepareMeta: initializeMeta ? _initialCatalog.PrepareInstall : null).Business;
        _branches.Add(graph.Address, branch);
        return graph.Address;
    }
    private ObjectVersionRecord Write(uint id, State state) => ObjectVersionRecord.CreateBase(id,
        TypedBaseBodyCodec.Encode(ObjectLayout.ForDurable(SharedReadModel.Schema), SharedReadModel.Base(state), _initialCatalog.View));
    private static ObjectVersionRecord String(uint id, string value) => ObjectVersionRecord.CreateBase(id,
        TypedBaseBodyCodec.Encode(ObjectLayout.String, StringPayloadCodec.PrepareBase(value)));
    private Node Read(FrameAddress address, ObjectId root, StateModelRegistry models) {
        DecodedRevision decoded = DecodedRevision.ReadStoredGraph(Store, address, models);
        return GraphReader.ReadRoot<Node>(decoded, root, new(decoded.SourceModels!));
    }
    // Same-S reuse must come from the actual first reader. This diagnostic does not
    // memoize decoded DTOs; the common materializer still owns both allocation tables.
    private (Node First, Node Second) ReadPair(FrameAddress first, ObjectId firstRoot,
        FrameAddress second, ObjectId secondRoot, StateModelRegistry models, GraphReadStatistics? statistics = null) {
        StateModelRegistry.FrozenConfiguration configuration = models.FreezeConfiguration();
        DecodedRevision a = DecodedRevision.ReadStoredGraph(Store, first, configuration);
        bool sameSchema = Store.ReadMetadata(first).SchemaRoot == Store.ReadMetadata(second).SchemaRoot;
        DecodedRevision b = DecodedRevision.ReadStoredGraph(Store, second, configuration, sameSchema ? a.BindingEnvironment : null);
        if (sameSchema) { Assert.Same(a.BindingEnvironment, b.BindingEnvironment); }
        else { Assert.NotSame(a.BindingEnvironment, b.BindingEnvironment); }
        var pair = GraphReader.ReadPair(a, new(a.SourceModels!, statistics), firstRoot,
            b, new(b.SourceModels!, statistics), secondRoot);
        return ((Node)pair.First, (Node)pair.Second);
    }
    private long Tail() => DataBytes(_repository.Data);
    public void Dispose() {
        _repository.Dispose();
        SharedReadModel.DeleteFixture(_root, "durable-shared-reader-");
    }
}

internal static class SharedReadModel {
    internal static readonly TypeExpr LinksType = TypeExpr.List(TypeExpr.Named("SharedReadNode"));
    internal static readonly DurableSchema Schema = new("SharedReadNode", 1,
        new DurableFieldInfo(1, TypeTag.ObjectReference, "SharedReadNode"),
        new DurableFieldInfo(2, TypeTag.ObjectReference, "SharedReadNode"),
        new DurableFieldInfo(3, TypeTag.String), new DurableFieldInfo(4, TypeTag.Byte),
        DurableFieldInfo.Reference(5, LinksType));

    internal sealed class Node : IDurableObject {
        internal Node? Next;
        internal Node? Alias;
        internal string? Text;
        internal byte Value;
        internal List<Node>? Links;
    }

    internal readonly record struct State(ObjectId Next, ObjectId Alias, ObjectId Text, byte Value, ObjectId Links = default) {
        internal State(uint next, uint alias, uint text, byte value)
            : this(new ObjectId(next), new ObjectId(alias), new ObjectId(text), value) { }
    }

    internal static StateModelRegistry Models(int currentVersion = 1, Func<ObjectStateRecord, State>? normalize = null,
        Func<Node>? allocate = null, Action<Node>? hydrate = null,
        bool includeEquality = true, StateEquality<State>? equality = null,
        StateBasePreparer<State>? prepareBase = null, StateDeltaPreparer<State>? prepareDelta = null) {
        DurableSchema current = currentVersion == 1 ? Schema : new(Schema.SchemaId, currentVersion, Schema.Fields.ToArray());
        CapturedStatePreparation<State> preparation = new(current, prepareBase ?? Base,
            prepareDelta ?? (static (in State prior, in State next) => new(prior != next, Base(next).Body)),
            includeEquality ? equality ?? (static (in State left, in State right) => left == right) : null);
        StateReaderBinding Reader(DurableSchema schema) => new StateReaderBinding<State>(schema, Read,
            static (ref BareValueReader input, in State prior) => Read(ref input), Visit);
        StateModelBinding<Node, State> model = new(preparation,
            currentVersion == 1 ? [Reader(Schema)] : [Reader(Schema), Reader(current)],
            normalize ?? (static row => row.GetState<State>()), allocate ?? (static () => new()),
            (Node domain, in State state, ObjectReadTable objects) => {
                domain.Next = objects.ResolveDurable<Node>(state.Next);
                domain.Alias = objects.ResolveDurable<Node>(state.Alias);
                domain.Text = objects.ResolveString(state.Text);
                domain.Value = state.Value;
                domain.Links = objects.ResolveObject<List<Node>>(state.Links);
                hydrate?.Invoke(domain);
            }, static (domain, context) => new(context.CaptureDurable(domain.Next, Schema.SchemaId),
                context.CaptureDurable(domain.Alias, Schema.SchemaId), context.CaptureString(domain.Text), domain.Value,
                context.CaptureObject(domain.Links, LinksType)), Visit);
        StateModelRegistry registry = new();
        registry.Register(model);
        return registry;
    }

    private static void Visit(in State state, IStateReferenceVisitor visitor) {
        visitor.VisitDurable(state.Next, Schema.SchemaId);
        visitor.VisitDurable(state.Alias, Schema.SchemaId);
        visitor.VisitString(state.Text);
        visitor.VisitObject(state.Links, LinksType);
    }

    private static State Read(ref BareValueReader reader) => new(new(reader.ReadVarUInt32()), new(reader.ReadVarUInt32()),
        new(reader.ReadVarUInt32()), reader.ReadByte(), new(reader.ReadVarUInt32()));

    internal static PreparedBaseBody Base(in State state) {
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        writer.WriteVarUInt32(state.Next.Value);
        writer.WriteVarUInt32(state.Alias.Value);
        writer.WriteVarUInt32(state.Text.Value);
        writer.WriteByte(state.Value);
        writer.WriteVarUInt32(state.Links.Value);
        return new(bytes.WrittenSpan);
    }

    internal static void DeleteFixture(string path, string prefix) {
        string resolved = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(resolved), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith(prefix, StringComparison.Ordinal)) {
            throw new InvalidOperationException("Refusing to delete outside this test fixture.");
        }
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}
