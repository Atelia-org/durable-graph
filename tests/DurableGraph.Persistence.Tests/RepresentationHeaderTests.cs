using Atelia.DurableGraph.Schema;
using Atelia.Binary;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class RepresentationHeaderTests : IDisposable {
    private readonly List<string> _paths = [];

    [Theory]
    [InlineData(1U, "0401AB")]
    [InlineData(2U, "0402AB")]
    [InlineData(127U, "047FAB")]
    [InlineData(128U, "048001AB")]
    [InlineData(16383U, "04FF7FAB")]
    [InlineData(16384U, "04808001AB")]
    [InlineData(uint.MaxValue, "04FFFFFFFF0FAB")]
    public void NewBaseHeaderUsesOnlyVersionAndCanonicalRepresentationId(uint id, string hex) {
        byte[] golden = Convert.FromHexString(hex);
        Assert.Equal(golden, BaseObjectBodyCodec.Encode(new(id), new([0xAB])).Body.ToArray());
        // Independently specified bytes keep integer-width boundaries visible.
        BareValueReader reader = new(golden);
        Assert.Equal(4, reader.ReadByte());
        Assert.Equal(id, reader.ReadVarUInt32());
        Assert.Equal(0xAB, reader.ReadByte());
        reader.EnsureFullyConsumed();
    }

    [Fact]
    public void HeaderIdWidthBoundariesResolveAfterColdReopen() {
        string path = NewPath();
        ObjectLayout[] layouts = Enumerable.Range(2, 16383)
            .Select(value => ObjectLayout.ForDurable(new DurableSchema($"Type{value}", 1))).ToArray();
        using (GraphResources file = GraphResources.CreateNew(path)) {
            SchemaStore schemas = file.Schemas;
            RepresentationId[] ids = schemas.RegisterRepresentations(layouts);
            Assert.Equal(new RepresentationId(2), ids[0]);
            Assert.Equal(new RepresentationId(16384), ids[^1]);
        }
        using GraphResources reopened = GraphResources.OpenReadOnlyExisting(path);
        SchemaStore restored = reopened.Schemas;
        foreach ((uint id, string hex) in new (uint, string)[] {
            (127, "047FAB"), (128, "048001AB"), (16383, "04FF7FAB"), (16384, "04808001AB")
        }) {
            var decoded = BaseObjectBodyCodec.Decode(Convert.FromHexString(hex), restored);
            Assert.Equal(new RepresentationId(id), decoded.RepresentationId);
            Assert.Equal(layouts[(int)id - 2], decoded.Layout);
            Assert.Equal(new byte[] { 0xAB }, decoded.Body.ToArray());
        }
    }

    [Fact]
    public void ClassAndArrayShareTheSameHeaderWhileExactLayoutComesFromDirectory() {
        DurableSchema point = new("Point", 2, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int32));
        DurableSchema box = new(TypeExpr.Named("Box", TypeExpr.Builtin(TypeTag.Int32)), 1,
            new DurableFieldInfo(1, TypeTag.Int32));
        ObjectLayout classLayout = ObjectLayout.ForDurable(box);
        ObjectLayout arrayLayout = ObjectLayout.ForArray(new(TypeExprKind.Rank2Array,
            new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: point)));
        string path = NewPath();
        using (GraphResources file = GraphResources.CreateNew(path)) {
            SchemaStore schemas = file.Schemas;
            RepresentationId[] ids = schemas.RegisterRepresentations([classLayout, arrayLayout]);
            // Point's inline Schema occupies node 3 before its array owner receives node 4.
            Assert.Equal(new[] { new RepresentationId(2), new RepresentationId(4) }, ids);
            Assert.Equal(Convert.FromHexString("0402AB"), BaseObjectBodyCodec.Encode(ids[0], new([0xAB])).Body.ToArray());
            Assert.Equal(Convert.FromHexString("0404AB"), BaseObjectBodyCodec.Encode(ids[1], new([0xAB])).Body.ToArray());
        }
        using GraphResources reopened = GraphResources.OpenReadOnlyExisting(path);
        SchemaStore restored = reopened.Schemas;
        long tail = DataBytes(reopened.Data);
        var durable = BaseObjectBodyCodec.Decode(Convert.FromHexString("0402AB"), restored);
        var array = BaseObjectBodyCodec.Decode(Convert.FromHexString("0404AB"), restored);
        Assert.Equal(classLayout, durable.Layout);
        Assert.Equal(arrayLayout, array.Layout);
        Assert.Equal(new RepresentationId(2), durable.RepresentationId);
        Assert.Equal(new RepresentationId(4), array.RepresentationId);
        Assert.Throws<InvalidDataException>(() => restored.GetRepresentation(new(3)));
        Assert.Throws<InvalidDataException>(() => BaseObjectBodyCodec.Decode(Convert.FromHexString("0403AB"), restored));
        Assert.Same(restored.GetRequired("Point", 2), array.Layout.Array!.ElementSlot.InlineSchema);
        Assert.Equal(tail, DataBytes(reopened.Data));
        Assert.Throws<InvalidDataException>(() => BaseObjectBodyCodec.Decode(Convert.FromHexString("0402AB")));
        Assert.Throws<InvalidDataException>(() => BaseObjectBodyCodec.Decode(Convert.FromHexString("0404AB")));
    }

    [Fact]
    public void StringHeaderResolvesWithoutSchemaStore() {
        var decoded = BaseObjectBodyCodec.Decode(Convert.FromHexString("0401AB"));
        Assert.Same(ObjectLayout.String, decoded.Layout);
        Assert.Equal(ObjectStateKind.String, decoded.Kind);
        Assert.Equal(RepresentationId.String, decoded.RepresentationId);
        Assert.Equal(new byte[] { 0xAB }, decoded.Body.ToArray());
        Assert.Equal(Convert.FromHexString("0401AB"), BaseObjectBodyCodec.EncodeString(new([0xAB])).Body.ToArray());
    }

    [Theory]
    [InlineData("0101AB")]
    [InlineData("0201AB")]
    [InlineData("0301AB")]
    [InlineData("0102034101AB")]
    [InlineData("02020203410001AB")]
    [InlineData("03020203410001AB")]
    [InlineData("0303010402AB")]
    public void RetiredBaseFormatsFailClosedEvenWhenSchemasAreAvailable(string hex) {
        using GraphResources file = GraphResources.CreateNew(NewPath());
        SchemaStore schemas = file.Schemas;
        schemas.RegisterRepresentations([ObjectLayout.ForDurable(new DurableSchema("A", 1))]);
        Assert.Throws<InvalidDataException>(() => BaseObjectBodyCodec.Decode(Convert.FromHexString(hex), schemas));
        Assert.Throws<InvalidDataException>(() => BaseObjectBodyCodec.Decode(Convert.FromHexString(hex)));
    }

    [Theory]
    [InlineData("0400")]
    [InlineData("0402")]
    [InlineData("047F")]
    [InlineData("04FFFFFFFF1F")]
    [InlineData("04808080808000")]
    [InlineData("00")]
    [InlineData("05")]
    public void InvalidOrUnknownIdAndVersionFailClosed(string hex) {
        using GraphResources file = GraphResources.CreateNew(NewPath());
        SchemaStore schemas = file.Schemas;
        Assert.Throws<InvalidDataException>(() => BaseObjectBodyCodec.Decode(Convert.FromHexString(hex), schemas));
        Assert.Throws<InvalidDataException>(() => BaseObjectBodyCodec.Decode(Convert.FromHexString(hex)));
    }

    [Fact]
    public void HeaderAcceptsBoundedRedundantRepresentationId() {
        var decoded = BaseObjectBodyCodec.Decode(Convert.FromHexString("048100AB"));
        Assert.Equal(RepresentationId.String, decoded.RepresentationId);
        Assert.Equal(new byte[] { 0xAB }, decoded.Body.ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("04")]
    [InlineData("0480")]
    [InlineData("048080")]
    [InlineData("04808080")]
    [InlineData("0480808080")]
    public void EveryIncompleteV4HeaderFails(string hex) {
        Assert.Throws<EndOfStreamException>(() => BaseObjectBodyCodec.Decode(Convert.FromHexString(hex)));
    }

    [Fact]
    public void ZeroCannotBeEncodedAndDecodedBodyOwnsItsBytes() {
        Assert.Throws<ArgumentOutOfRangeException>(() => BaseObjectBodyCodec.Encode(default, new([])));
        byte[] encoded = [4, 1, 7, 8];
        var decoded = BaseObjectBodyCodec.Decode(encoded);
        encoded.AsSpan().Clear();
        Assert.Equal(new byte[] { 7, 8 }, decoded.Body.ToArray());
    }

    private string NewPath() {
        string path = Path.Combine(Path.GetTempPath(), $"durable-representation-header-{Guid.NewGuid():N}.rbf");
        _paths.Add(path);
        return path;
    }

    public void Dispose() {
        foreach (string path in _paths) { DeleteTestPath(path); }
    }
}
