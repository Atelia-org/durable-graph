using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.FrameStore;
using SegmentStore = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class RevisionReadSessionTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"durable-graph-read-session-{Guid.NewGuid():N}");
    private static readonly DurableSchema NodeSchema = new("CacheNode", 1,
        new DurableFieldInfo(1, TypeTag.Byte), new DurableFieldInfo(2, TypeTag.String));
    private static readonly DurableSchema TargetSchema = new("CacheTarget", 1, new DurableFieldInfo(1, TypeTag.Byte));
    private static readonly DurableSchema OtherSchema = new("CacheOther", 1, new DurableFieldInfo(1, TypeTag.Byte));
    private static readonly DurableSchema OwnerSchema = new("CacheOwner", 1, new DurableFieldInfo(1, TypeTag.ObjectReference, "CacheTarget"));
    private int _nextPath;

    public RevisionReadSessionTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void CacheUsesIdAndActualHeadRetainsOwnedStateAndDoesNotWrite() {
        DecodedRevision first, second, third;
        int reads = 0, applies = 0;
        GraphReadStatistics statistics = new();
        FrameAddress original, changed, rewritten;
        using (GraphResources file = GraphResources.CreateLegacyNew(NextPath())) {
            var segments = file.Data;
            SchemaStore schemas = file.Schemas;
            using StateRevisionStore store = new(segments);
            original = store.Append(StateRevision.CreateObjectHeadMapBase(null,
                [Node(schemas, 1, 4, 3), Node(schemas, 2, 5, 3), Text(3, "equal"), Text(4, "equal")], []));
            changed = store.Append(StateRevision.CreateObjectHeadMapDelta(original,
                [ObjectVersionRecord.CreateDelta(2, original, new byte[] { 9 })], []));
            rewritten = store.Append(StateRevision.CreateObjectHeadMapDelta(changed, [Node(schemas, 2, 9, 3)], []));
            StateReaderRegistry readers = Nodes(() => reads++, () => applies++);
            RevisionReadSession session = new(store, schemas, readers.Snapshot(schemas), statistics);
            Assert.Same(store, session.Store);
            Assert.Same(schemas, session.Schemas);
            Assert.Same(statistics, session.Statistics);
            long stateTail = Tail(segments), schemaTail = DataBytes(file.Data);
            first = session.Read(original);
            second = session.Read(changed);
            third = session.Read(rewritten);
            Assert.Equal(stateTail, Tail(segments));
            Assert.Equal(schemaTail, DataBytes(file.Data));
        }
        Assert.Equal(6, statistics.DecodedObjects);
        Assert.Equal(6, statistics.CacheHits);
        Assert.Equal(4, reads); // Two initial nodes, one Delta chain's Base, one rewritten Base.
        Assert.Equal(1, applies);
        Assert.Equal(0, statistics.AllocatedObjects);
        Assert.Equal(0, statistics.HydratedObjects);
        Assert.Equal(0, statistics.SharedObjects);
        Assert.Equal(original, first.ObjectHeads[new(1)]);
        Assert.Equal(original, second.ObjectHeads[new(1)]);
        Assert.Equal(changed, second.ObjectHeads[new(2)]);
        Assert.Equal(rewritten, third.ObjectHeads[new(2)]);
        Assert.Same(first.GetRequired(new(1)), second.GetRequired(new(1)));
        Assert.NotSame(first.GetRequired(new(1)), first.GetRequired(new(2))); // Shared Frame is not shared ID.
        Assert.NotSame(first.GetRequired(new(2)), second.GetRequired(new(2)));
        Assert.NotSame(second.GetRequired(new(2)), third.GetRequired(new(2))); // Equal content, new head.
        Assert.Equal(new NodeState(5, new(3)), first.GetRequired(new(2)).GetState<NodeState>());
        Assert.Equal(new NodeState(9, new(3)), second.GetRequired(new(2)).GetState<NodeState>());
        Assert.Equal(second.GetRequired(new(2)).GetState<NodeState>(), third.GetRequired(new(2)).GetState<NodeState>());
        Assert.Same(first.Strings.ResolveString(new(3)), second.Strings.ResolveString(new(3)));
        Assert.Same(second.GetRequired(new(3)).StringContent, second.Strings.ResolveString(new(3)));
        Assert.Equal(first.Strings.ResolveString(new(3)), first.Strings.ResolveString(new(4)));
        Assert.NotSame(first.Strings.ResolveString(new(3)), first.Strings.ResolveString(new(4)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CachedOwnerStillValidatesRemovedOrWrongKindStringInEveryView(bool wrongKind) {
        using GraphResources file = GraphResources.CreateLegacyNew(NextPath());
        var segments = file.Data;
        SchemaStore schemas = file.Schemas;
        using StateRevisionStore store = new(segments);
        FrameAddress original = store.Append(StateRevision.CreateObjectHeadMapBase(null,
            [Node(schemas, 1, 4, 2), Text(2, "value")], []));
        FrameAddress invalid = store.Append(StateRevision.CreateObjectHeadMapDelta(original,
            wrongKind ? [Durable(schemas, 2, TargetSchema, [7])] : [], wrongKind ? [] : [2]));
        StateReaderRegistry readers = Nodes();
        readers.Register(Bytes(TargetSchema));
        RevisionReadSession session = new(store, schemas, readers.Snapshot(schemas));
        DecodedRevision previous = session.Read(original);
        DecodedRevision? result = null;
        Assert.Throws<InvalidDataException>(() => result = session.Read(invalid));
        Assert.Null(result);
        Assert.Equal(1, session.Statistics.CacheHits);
        Assert.Equal("value", previous.Strings.ResolveString(new(2)));
        Assert.Same(previous.GetRequired(new(1)), session.Read(original).GetRequired(new(1)));
    }

    [Fact]
    public void CachedOwnerStillValidatesNominalAncestryAgainstTheSecondView() {
        using GraphResources file = GraphResources.CreateLegacyNew(NextPath());
        var segments = file.Data;
        SchemaStore schemas = file.Schemas;
        using StateRevisionStore store = new(segments);
        FrameAddress original = store.Append(StateRevision.CreateObjectHeadMapBase(null,
            [Durable(schemas, 1, OwnerSchema, [2]), Durable(schemas, 2, TargetSchema, [7])], []));
        FrameAddress invalid = store.Append(StateRevision.CreateObjectHeadMapDelta(original,
            [Durable(schemas, 2, OtherSchema, [7])], []));
        StateReaderRegistry readers = new();
        readers.Register(new StateReaderBinding<ObjectId>(OwnerSchema,
            static (ref BareValueReader reader) => new(reader.ReadVarUInt32()),
            static (ref BareValueReader reader, in ObjectId prior) => new(reader.ReadVarUInt32()),
            static (in ObjectId state, IStateReferenceVisitor visitor) => visitor.VisitDurable(state, "CacheTarget")));
        readers.Register(Bytes(TargetSchema));
        readers.Register(Bytes(OtherSchema));
        RevisionReadSession session = new(store, schemas, readers.Snapshot(schemas));
        session.Read(original);
        Assert.Throws<InvalidDataException>(() => session.Read(invalid));
        Assert.Equal(1, session.Statistics.CacheHits);
    }

    [Theory]
    [InlineData(DictionaryComparerKind.StringOrdinal, "left", "left")]
    [InlineData(DictionaryComparerKind.StringOrdinalIgnoreCase, "LEFT", "left")]
    [InlineData(DictionaryComparerKind.ReferenceIdentity, "", "")]
    public void CachedDictionaryRepeatsLookupValidationAfterReferencedStringsChange(
        DictionaryComparerKind kind, string firstKey, string replacement) {
        using GraphResources file = GraphResources.CreateLegacyNew(NextPath());
        var segments = file.Data;
        SchemaStore schemas = file.Schemas;
        using StateRevisionStore store = new(segments);
        DictionaryLayout layout = new(new(1, TypeTag.String), new(2, TypeTag.Int32));
        RepresentationId representation = schemas.RegisterRepresentations([ObjectLayout.ForDictionary(layout)])[0];
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        writer.WriteByte((byte)kind);
        writer.WriteVarUInt32(2);
        writer.WriteVarUInt32(2); writer.WriteVarInt32(10);
        writer.WriteVarUInt32(3); writer.WriteVarInt32(20);
        FrameAddress original = store.Append(StateRevision.CreateObjectHeadMapBase(null,
            [ObjectVersionRecord.CreateBase(1, BaseObjectBodyCodec.Encode(representation, new(bytes.WrittenSpan)).Body),
             Text(2, firstKey), Text(3, "right")], []));
        FrameAddress invalid = store.Append(StateRevision.CreateObjectHeadMapDelta(original, [Text(3, replacement)], []));
        RevisionReadSession session = new(store, schemas, new StateModelRegistry().Snapshot(schemas));
        session.Read(original);
        Assert.Throws<InvalidDataException>(() => session.Read(invalid));
        Assert.Equal(2, session.Statistics.CacheHits); // Map and first string, before per-view lookup rejection.
        Assert.Equal(4, session.Statistics.DecodedObjects);
    }

    [Fact]
    public void CacheCannotMakeAnInheritedObjectIntoALocalRecordAtAnotherHead() {
        using GraphResources file = GraphResources.CreateLegacyNew(NextPath());
        var segments = file.Data;
        SchemaStore schemas = file.Schemas;
        using StateRevisionStore store = new(segments);
        FrameAddress original = store.Append(StateRevision.CreateObjectHeadMapBase(null, [Text(1, "text")], []));
        FrameAddress inherited = store.Append(StateRevision.CreateObjectHeadMapDelta(original, [], []));
        FrameAddress corrupt = store.Append(StateRevision.CreateObjectHeadMapBase(inherited, [], [new(1, inherited)]));
        RevisionReadSession session = new(store, schemas, new StateModelRegistry().Snapshot(schemas));
        session.Read(inherited);
        Assert.Throws<InvalidDataException>(() => session.Read(corrupt));
        Assert.Equal(1, session.Statistics.DecodedObjects);
        Assert.Equal(0, session.Statistics.CacheHits);
    }

    [Fact]
    public void PartialBodyConsumptionNeverEntersTheCache() {
        using GraphResources file = GraphResources.CreateLegacyNew(NextPath());
        var segments = file.Data;
        SchemaStore schemas = file.Schemas;
        using StateRevisionStore store = new(segments);
        FrameAddress corrupt = store.Append(StateRevision.CreateObjectHeadMapBase(null,
            [Durable(schemas, 1, NodeSchema, [4, 0, 99])], []));
        int reads = 0;
        RevisionReadSession session = new(store, schemas, Nodes(() => reads++).Snapshot(schemas));
        Assert.Throws<InvalidDataException>(() => session.Read(corrupt));
        Assert.Throws<InvalidDataException>(() => session.Read(corrupt));
        Assert.Equal(2, reads);
        Assert.Equal(0, session.Statistics.DecodedObjects);
        Assert.Equal(0, session.Statistics.CacheHits);
    }

    [Fact]
    public void SessionsNeverShareCachedResultsEvenWithTheSameModelSnapshot() {
        using GraphResources file = GraphResources.CreateLegacyNew(NextPath());
        var segments = file.Data;
        SchemaStore schemas = file.Schemas;
        using StateRevisionStore store = new(segments);
        FrameAddress address = store.Append(StateRevision.CreateObjectHeadMapBase(null, [Text(1, "text")], []));
        StateModelSnapshot models = new StateModelRegistry().Snapshot(schemas);
        RevisionReadSession first = new(store, schemas, models), second = new(store, schemas, models);
        Assert.NotSame(first.Read(address).GetRequired(new(1)).StringContent, second.Read(address).GetRequired(new(1)).StringContent);
        Assert.Equal(1, first.Statistics.DecodedObjects);
        Assert.Equal(1, second.Statistics.DecodedObjects);
        Assert.Equal(0, first.Statistics.CacheHits);
        Assert.Equal(0, second.Statistics.CacheHits);
    }

    private readonly record struct NodeState(byte Value, ObjectId TextId);

    private static StateReaderRegistry Nodes(Action? onRead = null, Action? onApply = null) {
        StateReaderRegistry readers = new();
        readers.Register(new StateReaderBinding<NodeState>(NodeSchema,
            (ref BareValueReader reader) => { onRead?.Invoke(); return new(reader.ReadByte(), new(reader.ReadVarUInt32())); },
            (ref BareValueReader reader, in NodeState prior) => { onApply?.Invoke(); return new(reader.ReadByte(), prior.TextId); },
            static (in NodeState state, IStateReferenceVisitor visitor) => visitor.VisitString(state.TextId)));
        return readers;
    }

    private static StateReaderBinding<byte> Bytes(DurableSchema schema) => new(schema,
        static (ref BareValueReader reader) => reader.ReadByte(),
        static (ref BareValueReader reader, in byte prior) => reader.ReadByte(),
        static (in byte state, IStateReferenceVisitor visitor) => { });
    private static ObjectVersionRecord Node(SchemaStore schemas, uint id, byte value, byte textId) => Durable(schemas, id, NodeSchema, [value, textId]);
    private static ObjectVersionRecord Durable(SchemaStore schemas, uint id, DurableSchema schema, ReadOnlySpan<byte> body) =>
        ObjectVersionRecord.CreateBase(id, BaseObjectBodyCodec.Encode(schemas.RegisterRepresentations([ObjectLayout.ForDurable(schema)])[0], new(body)).Body);
    private static ObjectVersionRecord Text(uint id, string value) =>
        ObjectVersionRecord.CreateBase(id, BaseObjectBodyCodec.EncodeString(StringPayloadCodec.PrepareBase(value)).Body);
    private string NextPath() => Path.Combine(_root, (++_nextPath).ToString(System.Globalization.CultureInfo.InvariantCulture));
    private static long Tail(SegmentStore segments) => DataBytes(segments);

    public void Dispose() {
        string resolved = Path.GetFullPath(_root);
        if (!string.Equals(Path.GetDirectoryName(resolved), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("durable-graph-read-session-", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Refusing to delete outside the fixture's temporary directory.");
        }
        Directory.Delete(resolved, recursive: true);
    }
}
