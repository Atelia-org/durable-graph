using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.FrameStore;
using SegmentStore = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class TypedObjectVersionReaderTests : IDisposable {
    private readonly string _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"durable-graph-typed-reader-{Guid.NewGuid():N}"));
    private int _nextFile;

    public TypedObjectVersionReaderTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ExactHistoricalReaderReconstructsRawDeltasAfterColdSchemaReopen() {
        DurableSchema ancestor = new("Ancestor", 1);
        DurableSchema old = new("Leaf", 1, [new(1, TypeTag.Byte)], ancestor);
        DurableSchema current = new("Leaf", 2, [new(1, TypeTag.UInt16)], ancestor);
        string path = NextPath();
        RepresentationId oldId;
        using (GraphResources file = GraphResources.CreateNew(path)) {
            SchemaStore schemas = file.Schemas;
            oldId = schemas.RegisterRepresentations([ObjectLayout.ForDurable(old), ObjectLayout.ForDurable(current)])[0];
        }
        ObjectVersionChain chain = Chain(BaseObjectBodyCodec.Encode(oldId, new([21])), [1, 25], [1, 26]);
        Assert.Equal(new byte[] { 1, 25 }, chain.Records[1].Record.Body.ToArray());
        Assert.Equal(new byte[] { 1, 26 }, chain.Records[2].Record.Body.ToArray());
        using GraphResources reopened = GraphResources.OpenReadOnlyExisting(path);
        SchemaStore restored = reopened.Schemas;
        int calls = 0;
        byte Read(ref BareValueReader reader) { calls++; return reader.ReadByte(); }
        byte Apply(ref BareValueReader reader, in byte prior) {
            calls++;
            Assert.Equal(1, reader.ReadByte()); // One changed slot, followed by its replacement value.
            return reader.ReadByte();
        }
        Assert.Equal((byte)26, TypedObjectVersionReader.ReadDurable<byte>(chain, restored, old, Read, Apply));
        Assert.Equal(3, calls);
        calls = 0;
        Assert.Throws<InvalidDataException>(() => TypedObjectVersionReader.ReadDurable<byte>(chain, restored, current, Read, Apply));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FullDefinitionMismatchRejectsBeforeAnyBodyCallback(int mismatch) {
        DurableSchema ancestor = new("Ancestor", 1, new DurableFieldInfo(1, TypeTag.Byte));
        DurableSchema stored = new("Leaf", 1, [new(1, TypeTag.Int32)], ancestor);
        DurableSchema wrong = mismatch switch {
            0 => new("Leaf", 1, [new(1, TypeTag.Int64)], ancestor),
            1 => new("Leaf", 1, [new(2, TypeTag.Int32)], ancestor),
            2 => new("Leaf", 1, [new(1, TypeTag.Int32)], new("Ancestor", 1, new DurableFieldInfo(1, TypeTag.SByte))),
            _ => new("Leaf", 1, [new(1, TypeTag.Int32)], new("Ancestor", 2, new DurableFieldInfo(1, TypeTag.Byte))),
        };
        using GraphResources file = GraphResources.CreateNew(NextPath());
        SchemaStore schemas = file.Schemas;
        RepresentationId id = schemas.RegisterRepresentations([ObjectLayout.ForDurable(stored)])[0];
        ObjectVersionChain chain = Chain(BaseObjectBodyCodec.Encode(id, new([])), Array.Empty<byte>());
        int calls = 0;
        int Read(ref BareValueReader reader) { calls++; return 0; }
        int Apply(ref BareValueReader reader, in int prior) { calls++; return prior; }
        Assert.Throws<InvalidDataException>(() => TypedObjectVersionReader.ReadDurable<int>(chain, schemas, wrong, Read, Apply));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void MissingRepresentationWrongKindAndOpaqueRawBaseNeverInvokeCallbacks() {
        DurableSchema schema = new("A", 1);
        using GraphResources file = GraphResources.CreateNew(NextPath());
        SchemaStore schemas = file.Schemas;
        ObjectVersionChain missing = Chain(BaseObjectBodyCodec.Encode(new(2), new([7])));
        ObjectVersionChain text = Chain(BaseObjectBodyCodec.EncodeString(new([0])));
        ObjectVersionChain opaque = Chain(new([7]));
        int calls = 0;
        int Read(ref BareValueReader reader) { calls++; return 0; }
        int Apply(ref BareValueReader reader, in int prior) { calls++; return prior; }
        Assert.Throws<InvalidDataException>(() => TypedObjectVersionReader.ReadDurable<int>(missing, schemas, schema, Read, Apply));
        Assert.Throws<InvalidDataException>(() => TypedObjectVersionReader.ReadDurable<int>(text, schemas, schema, Read, Apply));
        Assert.Throws<InvalidDataException>(() => TypedObjectVersionReader.ReadDurable<int>(opaque, schemas, schema, Read, Apply));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("0400", typeof(InvalidDataException))]
    [InlineData("0402", typeof(InvalidDataException))]
    [InlineData("048100", typeof(InvalidDataException))]
    [InlineData("04FFFFFFFF1F", typeof(InvalidDataException))]
    [InlineData("04", typeof(EndOfStreamException))]
    [InlineData("0480", typeof(EndOfStreamException))]
    public void MalformedOrUnknownRepresentationFailsBeforeBodyCallbacks(string hex, Type errorType) {
        using GraphResources file = GraphResources.CreateNew(NextPath());
        SchemaStore schemas = file.Schemas;
        ObjectVersionChain chain = Chain(new(Convert.FromHexString(hex)));
        int calls = 0;
        int Read(ref BareValueReader reader) { calls++; return 0; }
        int Apply(ref BareValueReader reader, in int prior) { calls++; return prior; }
        Assert.Throws(errorType, () => TypedObjectVersionReader.ReadDurable<int>(chain, schemas, new("A", 1), Read, Apply));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void InlineSchemaNodeCannotBeUsedAsAnObjectBaseBeforeCallbacks() {
        using GraphResources file = GraphResources.CreateNew(NextPath());
        SchemaStore schemas = file.Schemas;
        schemas.Register(new DurableSchema("Point", 1, SchemaKind.InlineValue,
            new DurableFieldInfo(1, TypeTag.Int32)));
        ObjectVersionChain chain = Chain(new(Convert.FromHexString("040200")));
        int calls = 0;
        int Read(ref BareValueReader reader) { calls++; return 0; }
        int Apply(ref BareValueReader reader, in int prior) { calls++; return prior; }
        Assert.Throws<InvalidDataException>(() => schemas.GetRepresentation(new(2)));
        Assert.Throws<InvalidDataException>(() => TypedObjectVersionReader.ReadDurable<int>(
            chain, schemas, new("World", 1), Read, Apply));
        Assert.Equal(0, calls);
        Assert.Equal(1, schemas.Count);
    }

    [Theory]
    [InlineData("010203410121")]
    [InlineData("0202020341000121")]
    [InlineData("0302020341000121")]
    public void RetiredBaseHeadersFailBeforeBodyCallbacks(string hex) {
        using GraphResources file = GraphResources.CreateNew(NextPath());
        SchemaStore schemas = file.Schemas;
        DurableSchema schema = new("A", 1, new DurableFieldInfo(1, TypeTag.Byte));
        schemas.Register(schema);
        long tail = DataBytes(file.Data);
        ObjectVersionChain chain = Chain(new(Convert.FromHexString(hex)), [1, 42]);
        int calls = 0;
        byte Read(ref BareValueReader reader) { calls++; return reader.ReadByte(); }
        byte Apply(ref BareValueReader reader, in byte prior) {
            calls++;
            Assert.Equal(1, reader.ReadByte());
            return reader.ReadByte();
        }
        Assert.Throws<InvalidDataException>(() => TypedObjectVersionReader.ReadDurable<byte>(chain, schemas, schema, Read, Apply));
        Assert.Equal(0, calls);
        Assert.Equal(tail, DataBytes(file.Data));
        // Rejected data must not silently allocate the next persistent ID.
        Assert.Equal(new RepresentationId(2), schemas.RegisterRepresentations([ObjectLayout.ForDurable(schema)])[0]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EveryBodyMustBeFullyConsumed(bool trailingBase) {
        DurableSchema schema = new("A", 1, new DurableFieldInfo(1, TypeTag.Byte));
        using GraphResources file = GraphResources.CreateNew(NextPath());
        SchemaStore schemas = file.Schemas;
        RepresentationId id = schemas.RegisterRepresentations([ObjectLayout.ForDurable(schema)])[0];
        ObjectVersionChain chain = Chain(
            BaseObjectBodyCodec.Encode(id, new(trailingBase ? new byte[] { 1, 2 } : new byte[] { 1 })),
            trailingBase ? new byte[] { 1, 3 } : new byte[] { 1, 3, 4 });
        int deltaCalls = 0;
        byte Read(ref BareValueReader reader) => reader.ReadByte();
        byte Apply(ref BareValueReader reader, in byte prior) {
            deltaCalls++;
            Assert.Equal(1, reader.ReadByte());
            return reader.ReadByte();
        }
        Assert.Throws<InvalidDataException>(() => TypedObjectVersionReader.ReadDurable<byte>(chain, schemas, schema, Read, Apply));
        Assert.Equal(trailingBase ? 0 : 1, deltaCalls);
    }

    [Fact]
    public void BuiltInStringNeedsNoSchemaStoreAndEmptyIsCanonical() {
        ObjectVersionChain text = Chain(BaseObjectBodyCodec.EncodeString(StringPayloadCodec.PrepareBase("hello")));
        string first = TypedObjectVersionReader.ReadString(text);
        string second = TypedObjectVersionReader.ReadString(text);
        Assert.Equal("hello", first);
        Assert.Equal(first, second);
        Assert.NotSame(first, second);
        Assert.Same(string.Empty, TypedObjectVersionReader.ReadString(Chain(BaseObjectBodyCodec.EncodeString(new([0])))));
        Assert.Throws<InvalidDataException>(() => TypedObjectVersionReader.ReadString(Chain(BaseObjectBodyCodec.EncodeString(new([0])), Array.Empty<byte>())));
        Assert.Throws<InvalidDataException>(() => TypedObjectVersionReader.ReadString(Chain(BaseObjectBodyCodec.EncodeString(new([0, 1])))));
        Assert.Throws<InvalidDataException>(() => TypedObjectVersionReader.ReadString(Chain(BaseObjectBodyCodec.Encode(new(2), new([0])))));
    }

    private ObjectVersionChain Chain(EncodedBaseObjectBody encodedBaseBody, params byte[][] deltas) {
        using SegmentStore segments = SegmentStore.Create(NextPath());
        using StateRevisionStore store = new(segments);
        FrameAddress address = store.Append(StateRevision.CreateObjectHeadMapBase(null, [ObjectVersionRecord.CreateBase(1, encodedBaseBody.Body)], []));
        foreach (byte[] delta in deltas) {
            address = store.Append(StateRevision.CreateObjectHeadMapDelta(address, [ObjectVersionRecord.CreateDelta(1, address, delta)], []));
        }
        return store.ReadObjectVersionChain(address, 1);
    }

    private string NextPath() => Path.Combine(_root, (++_nextFile).ToString(System.Globalization.CultureInfo.InvariantCulture));

    public void Dispose() {
        string resolved = Path.GetFullPath(_root);
        if (!string.Equals(Path.GetDirectoryName(resolved), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("durable-graph-typed-reader-", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Refusing to delete outside the fixture's temporary directory.");
        }
        Directory.Delete(resolved, recursive: true);
    }
}
