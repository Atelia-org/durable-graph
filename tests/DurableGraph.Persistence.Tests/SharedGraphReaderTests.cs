using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;
using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;
using State = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.State;

namespace Atelia.DurableGraph.Persistence.Tests;

/// <summary>White-box optimization witnesses; public ReadPair does not guarantee cross-graph identity.</summary>
public sealed class SharedGraphReaderTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"durable-shared-reader-{Guid.NewGuid():N}");
    private readonly IRbfFile _schemaFile;
    private readonly SegmentStore _segments;
    private readonly SchemaStore _schemas;
    private readonly StateRevisionStore _store;

    public SharedGraphReaderTests() {
        Directory.CreateDirectory(_root);
        _schemaFile = RbfFile.CreateNew(Path.Combine(_root, "schemas.rbf"));
        _schemas = new(_schemaFile);
        _segments = SegmentStore.CreateNew(Path.Combine(_root, "state"), new() { NewStoreLayout = RbfSegmentStoreLayout.Flat });
        _store = new(_segments);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SingleAndPairReadsDoNotPrepareBodiesEvenWithoutEquality(bool includeEquality) {
        FrameAddress first = Seed((1, new(2, 2, 0, 10)), (2, new(1, 1, 0, 20)));
        FrameAddress second = Reuse(first, 1, 2);
        int bases = 0, deltas = 0;
        StateModelSnapshot models = SharedReadModel.Models(includeEquality: includeEquality,
            prepareBase: (in State _) => { bases++; throw new NotSupportedException("No Base writer for this read."); },
            prepareDelta: (in State _, in State _) => { deltas++; throw new NotSupportedException("No Delta writer for this read."); })
            .Snapshot(_schemas);

        Node single = GraphReader.Read<Node>(_store, _schemas, first, new(1), models).Root;
        var pair = GraphReader.ReadPair<Node, Node>(_store, _schemas, first, new(1), second, new(1), models);

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
        StateModelSnapshot models = SharedReadModel.Models(
            equality: static (in State left, in State right) => left.Value != 20 && left == right).Snapshot(_schemas);

        var pair = GraphReader.ReadPair<Node, Node>(_store, _schemas, address, new(1), address, new(1), models);

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
        StateModelSnapshot models = SharedReadModel.Models(
            normalize: row => { normalizations++; return row.GetState<State>(); },
            equality: (in State _, in State _) => { comparisons++; throw failure; },
            allocate: () => { allocations++; return new(); }, hydrate: _ => hydrations++,
            prepareBase: (in State state) => { preparations++; return SharedReadModel.Base(state); })
            .Snapshot(_schemas);
        (Node First, Node Second)? delivered = null;

        Assert.Same(failure, Assert.Throws<NotSupportedException>(() => delivered = GraphReader.ReadPair<Node, Node>(
            _store, _schemas, address, new(1), address, new(1), models)));

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
    public void StableCycleDecodesAllocatesAndHydratesOnlyOneCopyAcrossDifferentRevisionViews(bool reverse) {
        FrameAddress first = Seed((1, new(2, 2, 0, 10)), (2, new(1, 1, 0, 20)));
        FrameAddress second = Reuse(first, 1, 2);
        int allocations = 0, hydrations = 0;
        StateModelSnapshot models = SharedReadModel.Models(allocate: () => { allocations++; return new(); },
            hydrate: _ => hydrations++).Snapshot(_schemas);
        GraphReadStatistics stats = new();

        var pair = GraphReader.ReadPair<Node, Node>(_store, _schemas,
            reverse ? second : first, new(reverse ? 2U : 1U),
            reverse ? first : second, new(reverse ? 1U : 2U), models, stats);

        Assert.Equal(reverse ? (byte)20 : (byte)10, pair.First.Value);
        Assert.Equal(reverse ? (byte)10 : (byte)20, pair.Second.Value);
        Assert.Same(pair.First.Next, pair.Second);
        Assert.Same(pair.Second.Next, pair.First);
        Assert.Same(pair.First.Next, pair.First.Alias);
        Assert.Equal(2, allocations);
        Assert.Equal(2, hydrations);
        Assert.Equal(2, stats.DecodedObjects);
        Assert.Equal(2, stats.CacheHits);
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
        FrameAddress second = _store.Append(StateRevision.CreateObjectHeadMapBase(first,
            [Write(2, nextChild)], [new(1, first)]));
        Assert.Equal(first, _store.ReadLiveObjectHeadMap(second)[1]);
        GraphReadStatistics stats = new();

        var pair = GraphReader.ReadPair<Node, Node>(_store, _schemas,
            reverse ? second : first, new(1), reverse ? first : second, new(1),
            SharedReadModel.Models().Snapshot(_schemas), stats);

        Assert.NotSame(pair.First, pair.Second);
        Assert.NotSame(pair.First.Next, pair.Second.Next);
        Assert.Same(pair.First, pair.First.Next!.Next);
        Assert.Same(pair.Second, pair.Second.Next!.Next);
        Assert.Equal(reverse && !equalBytes ? (byte)30 : (byte)20, pair.First.Next.Value);
        Assert.Equal(!reverse && !equalBytes ? (byte)30 : (byte)20, pair.Second.Next.Value);
        Assert.Equal(3, stats.DecodedObjects);
        Assert.Equal(1, stats.CacheHits);
        Assert.Equal(4, stats.AllocatedObjects);
        Assert.Equal(4, stats.HydratedObjects);
        Assert.Equal(0, stats.SharedObjects);
    }

    [Fact]
    public void IndependentOwnersCanStillShareAnUnchangedLeaf() {
        FrameAddress first = Seed((1, new(2, 2, 0, 10)), (2, new(0, 0, 0, 20)));
        FrameAddress second = _store.Append(StateRevision.CreateObjectHeadMapBase(first,
            [Write(1, new(2, 2, 0, 11))], [new(2, first)]));
        GraphReadStatistics stats = new();

        var pair = GraphReader.ReadPair<Node, Node>(_store, _schemas, first, new(1), second, new(1),
            SharedReadModel.Models().Snapshot(_schemas), stats);

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
        StateModelSnapshot models = SharedReadModel.Models(normalize: row => {
            normalizations++;
            State state = row.GetState<State>();
            return changeReference ? state with { Next = default } : state with { Value = 21 };
        }).Snapshot(_schemas);
        GraphReadStatistics stats = new();

        var pair = GraphReader.ReadPair<Node, Node>(_store, _schemas, address, new(1), address, new(1), models, stats);

        Assert.Equal(0, normalizations);
        Assert.Same(pair.First, pair.Second);
        Assert.Same(pair.First.Next, pair.Second.Next);
        Assert.Equal((byte)20, pair.First.Next!.Value);
        Assert.Same(pair.First.Next, pair.First.Alias);
        Assert.NotNull(pair.First.Next.Next);
        Assert.Equal((byte)30, pair.First.Next.Next.Value);
        Assert.Same(pair.First.Next.Next, pair.Second.Next!.Next);
        Assert.Equal(3, stats.SharedObjects);
        Assert.Equal(3, stats.DecodedObjects);
        Assert.Equal(3, stats.CacheHits);
    }

    [Fact]
    public void UpgradedNodesRemainIndependentEvenWhenTheirCurrentBytesMatch() {
        FrameAddress address = Seed((1, new(2, 0, 0, 10)), (2, new(1, 0, 0, 20)));
        int normalizations = 0;
        StateModelSnapshot models = SharedReadModel.Models(currentVersion: 2,
            normalize: row => { normalizations++; return row.GetState<State>(); }).Snapshot(_schemas);
        GraphReadStatistics stats = new();

        var pair = GraphReader.ReadPair<Node, Node>(_store, _schemas, address, new(1), address, new(1), models, stats);

        Assert.Equal(4, normalizations);
        Assert.NotSame(pair.First, pair.Second);
        Assert.NotSame(pair.First.Next, pair.Second.Next);
        Assert.Same(pair.First, pair.First.Next!.Next);
        Assert.Same(pair.Second, pair.Second.Next!.Next);
        Assert.Equal(2, stats.DecodedObjects);
        Assert.Equal(4, stats.AllocatedObjects);
        Assert.Equal(0, stats.SharedObjects);
    }

    [Fact]
    public void UpgradeCuttingAnEdgeDoesNotExcuseAnInvalidSourceRowInTheSecondView() {
        FrameAddress first = Seed((1, new(2, 0, 0, 10)), (2, new(3, 0, 0, 20)), (3, new(0, 0, 0, 30)));
        FrameAddress second = Reuse(first, 1, 2);
        StateModelSnapshot models = SharedReadModel.Models(currentVersion: 2,
            normalize: row => row.GetState<State>() with { Next = default, Alias = default }).Snapshot(_schemas);
        long stateTail = Tail(), schemaTail = _schemaFile.TailOffset;
        (Node First, Node Second)? delivered = null;

        Assert.Throws<InvalidDataException>(() => delivered = GraphReader.ReadPair<Node, Node>(
            _store, _schemas, first, new(1), second, new(1), models));

        Assert.Null(delivered);
        Assert.Equal(stateTail, Tail());
        Assert.Equal(schemaTail, _schemaFile.TailOffset);
    }

    [Fact]
    public void SecondHydrationFailureDoesNotDeliverHalfAPairOrWriteEitherStore() {
        FrameAddress first = Seed((1, new(0, 0, 0, 10)));
        FrameAddress second = Seed((1, new(0, 0, 0, 20)));
        List<byte> hydratedValues = [];
        StateModelSnapshot models = SharedReadModel.Models(hydrate: node => {
            hydratedValues.Add(node.Value);
            if (node.Value == 20) throw new InvalidDataException("Second view hydration failed.");
        }).Snapshot(_schemas);
        long stateTail = Tail(), schemaTail = _schemaFile.TailOffset;
        (Node First, Node Second)? delivered = null;

        Assert.Throws<InvalidDataException>(() => delivered = GraphReader.ReadPair<Node, Node>(
            _store, _schemas, first, new(1), second, new(1), models));

        Assert.Null(delivered);
        Assert.Equal(new byte[] { 10, 20 }, hydratedValues);
        Assert.Equal(stateTail, Tail());
        Assert.Equal(schemaTail, _schemaFile.TailOffset);
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
        StateModelSnapshot models = SharedReadModel.Models(allocate: () => singleton,
            hydrate: _ => hydrations++).Snapshot(_schemas);

        Assert.Throws<InvalidDataException>(() => GraphReader.ReadPair<Node, Node>(
            _store, _schemas, first, new(1), second, new(secondId), models));
        Assert.Equal(0, hydrations);
    }

    [Fact]
    public void StringSharingUsesObjectVersionIdentityWithoutInterningEqualDifferentIds() {
        FrameAddress address = _store.Append(StateRevision.CreateObjectHeadMapBase(null,
            [Write(1, new(2, 0, 10, 1)), Write(2, new(0, 0, 11, 2)),
             String(10, new string('s', 12)), String(11, new string('s', 12))], []));
        var pair = GraphReader.ReadPair<Node, Node>(_store, _schemas, address, new(1), address, new(1),
            SharedReadModel.Models().Snapshot(_schemas));

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
        FrameAddress first = _store.Append(StateRevision.CreateObjectHeadMapBase(null,
            [Write(1, new(2, 0, 10, 1)), Write(2, new(0, 0, 11, 2)), String(10, value), String(11, value)], []));
        FrameAddress second = _store.Append(StateRevision.CreateObjectHeadMapBase(first,
            [String(10, value), String(11, value)], [new(1, first), new(2, first)]));

        var pair = GraphReader.ReadPair<Node, Node>(_store, _schemas, first, new(1), second, new(1),
            SharedReadModel.Models().Snapshot(_schemas));

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

    private FrameAddress Seed(params (uint Id, State State)[] rows) => _store.Append(
        StateRevision.CreateObjectHeadMapBase(null, rows.Select(row => Write(row.Id, row.State)), []));

    private FrameAddress Reuse(FrameAddress previous, params uint[] ids) => _store.Append(
        StateRevision.CreateObjectHeadMapBase(previous, [], ids.Select(id => new KeyValuePair<uint, FrameAddress>(id, previous))));

    private ObjectVersionRecord Write(uint id, State state) {
        _schemas.RegisterBatch([SharedReadModel.Schema]);
        RepresentationId representation = _schemas.RegisterRepresentations([ObjectLayout.ForDurable(SharedReadModel.Schema)])[0];
        return ObjectVersionRecord.CreateBase(id, BaseObjectBodyCodec.Encode(representation, SharedReadModel.Base(state)).Body);
    }

    private static ObjectVersionRecord String(uint id, string value) => ObjectVersionRecord.CreateBase(id,
        BaseObjectBodyCodec.EncodeString(StringPayloadCodec.PrepareBase(value)).Body);

    private long Tail() {
        using RbfSegmentWriterLease lease = _segments.OpenActiveWriter();
        return lease.File.TailOffset;
    }

    public void Dispose() {
        _store.Dispose();
        _schemaFile.Dispose();
        _segments.Dispose();
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
            static (ref BinaryPayloadReader input, in State prior) => Read(ref input), Visit);
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

    private static State Read(ref BinaryPayloadReader reader) => new(new(reader.ReadUInt32()), new(reader.ReadUInt32()),
        new(reader.ReadUInt32()), reader.ReadByte(), new(reader.ReadUInt32()));

    internal static PreparedBaseBody Base(in State state) {
        ArrayBufferWriter<byte> bytes = new();
        BinaryPayloadWriter writer = new(bytes);
        writer.WriteUInt32(state.Next.Value);
        writer.WriteUInt32(state.Alias.Value);
        writer.WriteUInt32(state.Text.Value);
        writer.WriteByte(state.Value);
        writer.WriteUInt32(state.Links.Value);
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
