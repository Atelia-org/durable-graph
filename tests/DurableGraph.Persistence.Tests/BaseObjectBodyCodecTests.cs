using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.Rbf;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class BaseObjectBodyCodecTests : IDisposable {
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"base-object-codec-{Guid.NewGuid():N}.rbf");

    [Fact]
    public void TypedBaseEnvelopeHelpersRemainInternalImplementationDetails() {
        Type[] helperTypes = [
            typeof(BaseObjectBodyCodec),
            typeof(EncodedBaseObjectBody),
            typeof(DecodedBaseObjectBody),
        ];
        Assert.All(helperTypes, static type => Assert.False(type.IsPublic));
        Assert.Null(typeof(TypedObjectVersionReader).Assembly.GetType("Atelia.DurableGraph.Persistence.BaseObjectPayloadCodec"));
        Assert.Null(typeof(TypedObjectVersionReader).Assembly.GetType("Atelia.DurableGraph.Persistence.BaseObjectPayload"));
    }

    [Fact]
    public void HeadersHaveIndependentGoldenBytesAndPreserveOpaqueBodies() {
        EncodedBaseObjectBody text = BaseObjectBodyCodec.EncodeString(new([0xFF, 0x80]));
        Assert.Equal(new byte[] { 4, 1, 0xFF, 0x80 }, text.Body.ToArray());
        DecodedBaseObjectBody decodedText = BaseObjectBodyCodec.Decode(text.Body);
        Assert.Equal(ObjectStateKind.String, decodedText.Kind);
        Assert.Equal(ObjectLayout.String, decodedText.Layout);
        Assert.Equal(RepresentationId.String, decodedText.RepresentationId);
        Assert.Equal(new byte[] { 0xFF, 0x80 }, decodedText.Body.ToArray());

        DurableSchema schema = new("A", 128, new DurableFieldInfo(1, TypeTag.Int32));
        using GraphResources file = GraphResources.CreateLegacyNew(_path);
        SchemaStore schemas = file.Schemas;
        RepresentationId id = schemas.RegisterRepresentations([ObjectLayout.ForDurable(schema)])[0];
        Assert.Equal(new RepresentationId(2), id);
        EncodedBaseObjectBody durable = BaseObjectBodyCodec.Encode(id, new([0xFF, 0]));
        // The schema name and version live in the directory; the Base holds only ID 2.
        Assert.Equal(new byte[] { 4, 2, 0xFF, 0 }, durable.Body.ToArray());
        DecodedBaseObjectBody decoded = BaseObjectBodyCodec.Decode(durable.Body, schemas);
        Assert.Equal(ObjectStateKind.Durable, decoded.Kind);
        Assert.Equal(schema, decoded.Layout.Schema);
        Assert.Equal(id, decoded.RepresentationId);
        Assert.Equal(new byte[] { 0xFF, 0 }, decoded.Body.ToArray());
    }

    [Fact]
    public void EmptyBodyIsValidAtTheHeaderBoundaryAndDecodeOwnsItsBody() {
        Assert.Empty(BaseObjectBodyCodec.Decode(new byte[] { 4, 1 }).Body.ToArray());
        using GraphResources file = GraphResources.CreateLegacyNew(_path);
        SchemaStore schemas = file.Schemas;
        DurableSchema schema = new("A", 1);
        RepresentationId id = schemas.RegisterRepresentations([ObjectLayout.ForDurable(schema)])[0];
        byte[] wire = [4, 2, 7, 8];
        DecodedBaseObjectBody decoded = BaseObjectBodyCodec.Decode(wire, schemas);
        wire.AsSpan().Fill(0);
        Assert.Equal(schema, decoded.Layout.Schema);
        Assert.Equal(id, decoded.RepresentationId);
        Assert.Equal(new byte[] { 7, 8 }, decoded.Body.ToArray());
    }

    [Theory]
    [InlineData(new byte[] { 0, 1 })]
    [InlineData(new byte[] { 5, 1 })]
    [InlineData(new byte[] { 4, 0 })]
    public void UnknownHeadersAreRejected(byte[] wire) {
        Assert.Throws<InvalidDataException>(() => BaseObjectBodyCodec.Decode(wire));
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 4 })]
    [InlineData(new byte[] { 4, 0x80 })]
    public void TruncatedHeadersAreRejected(byte[] wire) {
        Assert.Throws<EndOfStreamException>(() => BaseObjectBodyCodec.Decode(wire));
    }

    [Fact]
    public void BoundedRedundantRepresentationIdRemainsReadable() {
        Assert.Equal(RepresentationId.String, BaseObjectBodyCodec.Decode(new byte[] { 4, 0x81, 0 }).RepresentationId);
    }

    [Fact]
    public void NullInputsAreRejected() {
        Assert.Throws<ArgumentNullException>(() => BaseObjectBodyCodec.EncodeString(null!));
        Assert.Throws<ArgumentNullException>(() => BaseObjectBodyCodec.Encode(RepresentationId.String, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => BaseObjectBodyCodec.Encode(default, new([])));
    }

    public void Dispose() => DeleteTestPath(_path);
}
