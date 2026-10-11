using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.FrameStore;
using SegmentStore = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class RevisionDecoderTests : IDisposable {
    private readonly string _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"durable-graph-revision-decoder-{Guid.NewGuid():N}"));
    private static readonly DurableSchema NodeSchema = new("Node", 1,
        new DurableFieldInfo(1, TypeTag.Byte), new DurableFieldInfo(2, TypeTag.String));
    private static readonly DurableSchema ByteSchema = new("Number", 1, new DurableFieldInfo(1, TypeTag.Byte));
    private int _nextFile;

    public RevisionDecoderTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void DecodesFullMembershipTypedDeltaAndOneStringInstancePerIdWithoutWriting() {
        DecodedRevision decoded;
        FrameAddress latest;
        using (GraphResources file = GraphResources.CreateNew(NextPath())) {
            var segments = file.Data;
            SchemaStore schemas = file.Schemas;
            schemas.Register(NodeSchema);
            using StateRevisionStore store = new(segments);
            FrameAddress original = store.Append(StateRevision.CreateObjectHeadMapBase(null,
                [Node(schemas, 1, 4, 3), Node(schemas, 2, 5, 3), Text(3, "same"), Text(4, "same"), Text(5, ""), Text(6, "")], []));
            latest = store.Append(StateRevision.CreateObjectHeadMapDelta(original,
                [ObjectVersionRecord.CreateDelta(1, original, new byte[] { 1, 9 })], []));
            StateReaderRegistry readers = NodeReaders();
            long schemaTail = DataBytes(file.Data);
            long stateTail = Tail(segments);
            decoded = RevisionDecoder.Read(store, schemas, latest, readers);
            Assert.Equal(schemaTail, DataBytes(file.Data));
            Assert.Equal(stateTail, Tail(segments));
            Assert.Equal(new NodeState(4, 3), RevisionDecoder.Read(store, schemas, original, readers).GetRequired(new ObjectId(1)).GetState<NodeState>());
        }
        Assert.Equal(latest, decoded.RevisionAddress);
        Assert.Equal(new uint[] { 1, 2, 3, 4, 5, 6 }, decoded.Objects.Select(static row => row.Id.Value));
        Assert.Equal(new NodeState(9, 3), decoded.GetRequired(new ObjectId(1)).GetState<NodeState>());
        Assert.Equal(new NodeState(5, 3), decoded.GetRequired(new ObjectId(2)).GetState<NodeState>());
        Assert.Same(decoded.GetRequired(new ObjectId(3)).StringContent, decoded.Strings.ResolveString(new ObjectId(3)));
        Assert.Same(decoded.Strings.ResolveString(new ObjectId(3)), decoded.Strings.ResolveString(decoded.GetRequired(new ObjectId(2)).GetState<NodeState>().TextId));
        Assert.Equal(decoded.Strings.ResolveString(new ObjectId(3)), decoded.Strings.ResolveString(new ObjectId(4)));
        Assert.NotSame(decoded.Strings.ResolveString(new ObjectId(3)), decoded.Strings.ResolveString(new ObjectId(4)));
        Assert.Same(string.Empty, decoded.Strings.ResolveString(new ObjectId(5)));
        Assert.Same(string.Empty, decoded.GetRequired(new ObjectId(6)).StringContent);
        Assert.Null(decoded.Strings.ResolveString(new ObjectId(0)));
        Assert.Throws<InvalidDataException>(() => decoded.GetRequired(new ObjectId(0)));
        Assert.Throws<InvalidDataException>(() => decoded.GetRequired(new ObjectId(99)));
        Assert.False(decoded.Objects is System.Collections.ICollection);
        Assert.False(decoded.Objects is IList<ObjectStateRecord>);
        NodeState copy = decoded.GetRequired(new ObjectId(1)).GetState<NodeState>();
        copy = copy with { Value = 99 };
        Assert.Equal((byte)9, decoded.GetRequired(new ObjectId(1)).GetState<NodeState>().Value);
    }

    [Fact]
    public void EmptyMembershipIsARealRevisionAndInvalidAddressDoesNotMeanEmpty() {
        using GraphResources file = GraphResources.CreateNew(NextPath());
        var segments = file.Data;
        SchemaStore schemas = file.Schemas;
        using StateRevisionStore store = new(segments);
        FrameAddress address = store.Append(StateRevision.CreateObjectHeadMapBase(null, [], []));
        DecodedRevision result = RevisionDecoder.Read(store, schemas, address, new());
        Assert.Empty(result.Objects);
        Assert.Null(result.Strings.ResolveString(new ObjectId(0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RevisionDecoder.Read(store, schemas, default, new()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TargetRevisionReferenceValidationRejectsRemovedOrWrongKindString(bool wrongKind) {
        using GraphResources file = GraphResources.CreateNew(NextPath());
        var segments = file.Data;
        SchemaStore schemas = file.Schemas;
        schemas.RegisterBatch([NodeSchema, ByteSchema]);
        using StateRevisionStore store = new(segments);
        FrameAddress original = store.Append(StateRevision.CreateObjectHeadMapBase(null, [Node(schemas, 1, 4, 2), Text(2, "value")], []));
        StateReaderRegistry readers = NodeReaders();
        readers.Register(ByteBinding(ByteSchema));
        DecodedRevision previous = RevisionDecoder.Read(store, schemas, original, readers);
        FrameAddress invalid = store.Append(StateRevision.CreateObjectHeadMapDelta(original,
            wrongKind ? [Durable(schemas, 2, ByteSchema, [7])] : [], wrongKind ? [] : [2]));
        long stateTail = Tail(segments);
        long schemaTail = DataBytes(file.Data);
        DecodedRevision? result = null;
        Assert.Throws<InvalidDataException>(() => result = RevisionDecoder.Read(store, schemas, invalid, readers));
        Assert.Null(result);
        Assert.Equal(stateTail, Tail(segments));
        Assert.Equal(schemaTail, DataBytes(file.Data));
        Assert.Equal("value", previous.Strings.ResolveString(new ObjectId(2)));
        Assert.Equal("value", RevisionDecoder.Read(store, schemas, original, readers).Strings.ResolveString(new ObjectId(2)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void UnknownDefinitionReaderOrFutureVersionDoesNotFallBack(int missing) {
        using GraphResources file = GraphResources.CreateNew(NextPath());
        var segments = file.Data;
        SchemaStore schemas = file.Schemas;
        DurableSchema stored = missing == 2 ? new("Number", 2, new DurableFieldInfo(1, TypeTag.Byte)) : ByteSchema;
        if (missing != 0) { schemas.Register(stored); }
        using StateRevisionStore store = new(segments);
        // A legacy v1 literal intentionally names a missing Schema without registering it.
        ObjectVersionRecord record = missing == 0
            ? ObjectVersionRecord.CreateBase(1, Convert.FromHexString("01020D4E756D6265720107"))
            : Durable(schemas, 1, stored, [7]);
        FrameAddress address = store.Append(StateRevision.CreateObjectHeadMapBase(null, [record], []));
        StateReaderRegistry readers = new();
        int calls = 0;
        if (missing != 1) { readers.Register(ByteBinding(ByteSchema, () => calls++)); }
        Assert.Throws<InvalidDataException>(() => RevisionDecoder.Read(store, schemas, address, readers));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullSchemaMismatchIncludesAncestorAndPrecedesThisObjectsBody(bool ancestorMismatch) {
        using GraphResources file = GraphResources.CreateNew(NextPath());
        var segments = file.Data;
        DurableSchema stored = new("Leaf", 1, [new(1, TypeTag.Byte)], new("Ancestor", 1));
        DurableSchema wrong = ancestorMismatch
            ? new("Leaf", 1, [new(1, TypeTag.Byte)], new("Ancestor", 2))
            : new("Leaf", 1, [new(1, TypeTag.SByte)], new("Ancestor", 1));
        SchemaStore schemas = file.Schemas;
        schemas.Register(stored);
        using StateRevisionStore store = new(segments);
        FrameAddress address = store.Append(StateRevision.CreateObjectHeadMapBase(null, [Durable(schemas, 1, stored, [7])], []));
        StateReaderRegistry readers = new();
        int calls = 0;
        readers.Register(ByteBinding(wrong, () => calls++));
        Assert.Throws<InvalidDataException>(() => RevisionDecoder.Read(store, schemas, address, readers));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void LateUnknownReaderDoesNotPublishPartialRowsOrUseCallbackRegistration() {
        using GraphResources file = GraphResources.CreateNew(NextPath());
        var segments = file.Data;
        DurableSchema second = new("Other", 1, new DurableFieldInfo(1, TypeTag.Byte));
        SchemaStore schemas = file.Schemas;
        schemas.RegisterBatch([ByteSchema, second]);
        using StateRevisionStore store = new(segments);
        FrameAddress address = store.Append(StateRevision.CreateObjectHeadMapBase(null,
            [Durable(schemas, 1, ByteSchema, [7]), Durable(schemas, 2, second, [8])], []));
        StateReaderRegistry readers = new();
        StateReaderBinding<byte> late = ByteBinding(second);
        int calls = 0;
        readers.Register(ByteBinding(ByteSchema, () => { calls++; readers.Register(late); }));
        DecodedRevision? result = null;
        Assert.Throws<InvalidDataException>(() => result = RevisionDecoder.Read(store, schemas, address, readers));
        Assert.Null(result);
        Assert.Equal(1, calls); // Object-first processing permits earlier pure callbacks.
        DecodedRevision retried = RevisionDecoder.Read(store, schemas, address, readers);
        Assert.Equal((byte)8, retried.GetRequired(new ObjectId(2)).GetState<byte>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RejectsLateMalformedBodiesAndStringDelta(int malformed) {
        using GraphResources file = GraphResources.CreateNew(NextPath());
        var segments = file.Data;
        SchemaStore schemas = file.Schemas;
        schemas.Register(NodeSchema);
        using StateRevisionStore store = new(segments);
        FrameAddress original = store.Append(StateRevision.CreateObjectHeadMapBase(null,
            [Node(schemas, 1, 4, 0), malformed == 3 ? Text(2, "text") : Node(schemas, 2, 5, 0)], []));
        ObjectVersionRecord bad = malformed switch {
            0 => Durable(schemas, 2, NodeSchema, [5, 0, 0]), // Trailing Base byte.
            1 => ObjectVersionRecord.CreateDelta(2, original, new byte[] { 1 }), // Missing changed value.
            2 => ObjectVersionRecord.CreateDelta(2, original, new byte[] { 1, 9, 0 }), // Trailing Delta byte.
            _ => ObjectVersionRecord.CreateDelta(2, original, Array.Empty<byte>()),
        };
        FrameAddress address = store.Append(StateRevision.CreateObjectHeadMapDelta(original, [bad], []));
        DecodedRevision? result = null;
        if (malformed == 1) {
            Assert.Throws<EndOfStreamException>(() => result = RevisionDecoder.Read(store, schemas, address, NodeReaders()));
        } else {
            Assert.Throws<InvalidDataException>(() => result = RevisionDecoder.Read(store, schemas, address, NodeReaders()));
        }
        Assert.Null(result);
    }

    [Fact]
    public void DeclaredHeadMustContainLocalObjectAndCannotFallbackToParent() {
        using GraphResources file = GraphResources.CreateNew(NextPath());
        var segments = file.Data;
        SchemaStore schemas = file.Schemas;
        using StateRevisionStore store = new(segments);
        FrameAddress original = store.Append(StateRevision.CreateObjectHeadMapBase(null, [Text(1, "text")], []));
        FrameAddress unchanged = store.Append(StateRevision.CreateObjectHeadMapDelta(original, [], []));
        FrameAddress corrupt = store.Append(StateRevision.CreateObjectHeadMapBase(unchanged, [], [new(1, unchanged)]));
        Assert.Throws<InvalidDataException>(() => RevisionDecoder.Read(store, schemas, corrupt, new()));
    }

    private readonly record struct NodeState(byte Value, ObjectId TextId) {
        internal NodeState(byte value, uint textId) : this(value, new ObjectId(textId)) { }
    }

    private static StateReaderRegistry NodeReaders() {
        StateReaderRegistry readers = new();
        readers.Register(new StateReaderBinding<NodeState>(NodeSchema,
            static (ref BareValueReader reader) => new(reader.ReadByte(), reader.ReadVarUInt32()),
            static (ref BareValueReader reader, in NodeState prior) => {
                byte bitmap = reader.ReadByte();
                if (bitmap == 0 || bitmap > 3) { throw new InvalidDataException("Invalid test DTO Delta bitmap."); }
                byte value = (bitmap & 1) != 0 ? reader.ReadByte() : prior.Value;
                ObjectId textId = (bitmap & 2) != 0 ? new ObjectId(reader.ReadVarUInt32()) : prior.TextId;
                return new(value, textId);
            },
            static (in NodeState state, IStateReferenceVisitor visitor) => { visitor.VisitString(state.TextId); }));
        return readers;
    }

    private static StateReaderBinding<byte> ByteBinding(DurableSchema schema, Action? onRead = null) => new(schema,
        (ref BareValueReader reader) => { onRead?.Invoke(); return reader.ReadByte(); },
        static (ref BareValueReader reader, in byte prior) => reader.ReadByte(),
        static (in byte state, IStateReferenceVisitor visitor) => { });

    private static ObjectVersionRecord Node(SchemaStore schemas, uint id, byte value, byte textId) => Durable(schemas, id, NodeSchema, [value, textId]);
    private static ObjectVersionRecord Durable(SchemaStore schemas, uint id, DurableSchema schema, ReadOnlySpan<byte> body) =>
        ObjectVersionRecord.CreateBase(id, BaseObjectBodyCodec.Encode(schemas.RegisterRepresentations([ObjectLayout.ForDurable(schema)])[0], new(body)).Body);
    private static ObjectVersionRecord Text(uint id, string value) =>
        ObjectVersionRecord.CreateBase(id, BaseObjectBodyCodec.EncodeString(StringPayloadCodec.PrepareBase(value)).Body);

    private string NextPath() => Path.Combine(_root, (++_nextFile).ToString(System.Globalization.CultureInfo.InvariantCulture));
    private static long Tail(SegmentStore segments) => DataBytes(segments);

    public void Dispose() {
        string resolved = Path.GetFullPath(_root);
        if (!string.Equals(Path.GetDirectoryName(resolved), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("durable-graph-revision-decoder-", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Refusing to delete outside the fixture's temporary directory.");
        }
        Directory.Delete(resolved, recursive: true);
    }
}
