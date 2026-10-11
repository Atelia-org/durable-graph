using Atelia.DurableGraph.Schema;
namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class SchemaCatalogWireCodecTests {
    [Fact]
    public void IndependentGoldenUsesEarlierBaseIdAndOrderedFields() {
        DurableSchema ancestor = new("Z", 2, new DurableFieldInfo(3, TypeTag.String));
        DurableSchema derived = new("A", 1, [new(8, TypeTag.UInt32), new(1, TypeTag.Boolean)], ancestor);
        byte[] golden = Convert.FromHexString("0202020102035A00020001030403010203410001020201010809");
        Assert.Equal(0x31424353U, SchemaCatalogWireCodec.RbfTag);
        Assert.Equal(golden, SchemaCatalogTestData.Write([ancestor, derived]));
        var result = SchemaCatalogTestData.Read(golden);
        Assert.Equal(derived, result[new("A", 1)]);
        Assert.Same(result[new("Z", 2)], result[new("A", 1)].BaseSchema);
    }

    [Fact]
    public void AllFourteenCodesHaveFixedIndependentGoldenBytes() {
        TypeTag[] tags = [TypeTag.Boolean, TypeTag.Int32, TypeTag.Int64, TypeTag.String,
            TypeTag.Byte, TypeTag.SByte, TypeTag.Int16, TypeTag.UInt16, TypeTag.UInt32,
            TypeTag.UInt64, TypeTag.Char, TypeTag.Half, TypeTag.Single, TypeTag.Double];
        DurableSchema schema = new("T", 1, tags.Select((tag, i) => new DurableFieldInfo(i + 1, tag)).ToArray());
        byte[] golden = Convert.FromHexString("020102010203540001000E0101020203030404050506060707080809090A0A0B0B0C0C0D0D0E0E");
        Assert.Equal(golden, SchemaCatalogTestData.Write([schema]));
        Assert.Equal(schema, SchemaCatalogTestData.Read(golden)[new("T", 1)]);
    }

    [Theory]
    [InlineData("0301020102034100010000")] // Unknown batch version.
    [InlineData("0200")] // Empty physical batch.
    [InlineData("02FFFFFFFF0F")] // Impossible row count.
    [InlineData("02010201020341000100FFFFFFFF0F")] // Impossible field count.
    [InlineData("02010201020341000100010111")] // Unknown slot.
    [InlineData("02010201020341000100010100")] // Invalid slot.
    [InlineData("020102010203410001000202020101")] // Descending fields.
    [InlineData("020102010203410001000201010102")] // Duplicate fields.
    [InlineData("02010201020341000100010001")] // Field zero.
    [InlineData("0201020102034100010001808080800801")] // Field exceeds Int32.
    [InlineData("0201020102034100000000")] // Schema version zero.
    [InlineData("020102010203410080808080080000")] // Schema version exceeds Int32.
    [InlineData("020102010203410001000000")] // Trailing bytes.
    [InlineData("0201020102034100010300")] // Future base.
    [InlineData("0201020102034100010200")] // Self base.
    [InlineData("0201020102034100010100")] // String cannot be a base.
    [InlineData("02010201020000010000")] // Empty identity.
    [InlineData("020102010102010000")] // Schema nominal type cannot be builtin.
    [InlineData("0201020002034100010000")] // Unknown row kind.
    [InlineData("0201030102034100010000")] // Initial ID gap.
    [InlineData("0201020102034100010000030102034200010000")] // Uncounted row.
    public void MalformedDefinitionsAreRejected(string hex) {
        Assert.Throws<InvalidDataException>(() => SchemaCatalogWireCodec.Read(Convert.FromHexString(hex)));
    }

    [Fact]
    public void BoundedRedundantBaseIdIsAcceptedAndWriterUsesShortestEncoding() {
        byte[] redundant = Convert.FromHexString("020102010203410001800000");
        DurableSchema schema = Assert.Single(SchemaCatalogWireCodec.Read(redundant)).Schema!;
        Assert.Equal(new DurableSchema("A", 1), schema);
        Assert.Equal(Convert.FromHexString("0201020102034100010000"), SchemaCatalogTestData.Write([schema]));
    }

    [Fact]
    public void EveryTruncatedPrefixIsRejected() {
        byte[] valid = Convert.FromHexString("02010201020341000100010102");
        Assert.Single(SchemaCatalogWireCodec.Read(valid));
        for (int length = 0; length < valid.Length; length++) {
            byte[] prefix = valid[..length];
            Exception? error = Record.Exception(() => SchemaCatalogWireCodec.Read(prefix));
            Assert.True(error is InvalidDataException or EndOfStreamException, $"Prefix {length}: {error}");
        }
    }

    [Fact]
    public void ExactAncestorResolvesAndRepeatedKeyCannotAliasAnotherId() {
        DurableSchema ancestor = new("Base", 3, new DurableFieldInfo(1, TypeTag.Int64));
        DurableSchema derived = new("Leaf", 1, [], ancestor);
        var result = SchemaCatalogTestData.Read(SchemaCatalogTestData.Write([ancestor, derived]));
        Assert.Same(result[new("Base", 3)], result[new("Leaf", 1)].BaseSchema);
        foreach (DurableSchema alias in new[] { ancestor, new DurableSchema("Base", 3) }) {
            byte[] bytes = CatalogTestData.Encode(CatalogTestData.Schemas([ancestor, alias]));
            Assert.Throws<InvalidDataException>(() => SchemaCatalogWireCodec.Read(bytes));
        }
        DurableSchema mismatched = new("Base", 3, new DurableFieldInfo(1, TypeTag.String));
        Assert.Throws<ArgumentException>(() => SchemaCatalogTestData.Write([mismatched, derived]));
    }

    [Fact]
    public void MaximumDepthIncludesAllEarlierDependenciesInSnapshot() {
        DurableSchema[] valid = Chain(256);
        Assert.Equal(256, SchemaCatalogWireCodec.Read(SchemaCatalogTestData.Write(valid)).Length);
        DurableSchema[] tooDeep = Chain(257);
        Assert.Throws<ArgumentException>(() => SchemaCatalogTestData.Write(tooDeep));
        Assert.Throws<InvalidDataException>(() => SchemaCatalogWireCodec.Read(CatalogTestData.Encode(CatalogTestData.Schemas(tooDeep))));
        // Two versions of one family in an exact ancestor lineage remain invalid.
        byte[] repeatedIdentity = Convert.FromHexString("0202020102034100010000030102034100020200");
        Assert.Throws<InvalidDataException>(() => SchemaCatalogWireCodec.Read(repeatedIdentity));
    }

    [Fact]
    public void DefinitionVersionIsCanonicalAndSchemaKeyRemainsAnInMemoryConstraint() {
        DurableSchema schema = new("A", 128);
        byte[] golden = Convert.FromHexString("020102010203410080010000");
        Assert.Equal(golden, SchemaCatalogTestData.Write([schema]));
        Assert.Equal(schema, SchemaCatalogTestData.Read(golden)[new("A", 128)]);
        Assert.Throws<ArgumentException>(() => new SchemaKey(" ", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SchemaKey("A", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SchemaKey("A", -1));
    }

    internal static DurableSchema[] Chain(int depth) {
        var result = new DurableSchema[depth];
        DurableSchema? ancestor = null;
        for (int index = 0; index < depth; index++) {
            result[index] = ancestor = new DurableSchema($"Type{index:D3}", 1, [], ancestor);
        }
        return result;
    }
}
