using Atelia.DurableGraph.Schema;
using System.Buffers;
using Atelia.Binary;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class BclScalarCatalogTests {
    [Theory]
    [InlineData(TypeTag.Guid, 19)]
    [InlineData(TypeTag.Decimal, 20)]
    [InlineData(TypeTag.TimeSpan, 21)]
    public void NewBuiltinLeavesHaveIndependentNominalAndCatalogBytes(TypeTag tag, byte code) {
        Assert.Equal(code, (byte)tag);
        Assert.Equal(2, SchemaCatalogWireCodec.Version);
        TypeExpr type = TypeExpr.Builtin(tag);
        AssertNominal(type, [1, code]);
        AssertNominal(TypeExpr.List(type), [8, 1, code]);
        AssertNominal(TypeExpr.Nullable(type), [9, 1, code]);
        AssertNominal(TypeExpr.Dictionary(type, type), [10, 1, code, 1, code]);
        for (byte rank = 4; rank <= 7; rank++) {
            SchemaCatalogEntry row = SchemaCatalogEntry.ForArray(new(2), new((TypeExprKind)rank, new(1, tag)));
            byte[] golden = [2, 1, 2, 3, 1, rank, code];
            Assert.Equal(golden, SchemaCatalogWireCodec.Write([row]));
            Assert.Equal(row.Layout, SchemaCatalogWireCodec.Read(golden)[0].Layout);
        }
    }

    [Fact]
    public void BuiltinFieldsNeedNoAdditionalSchemaNodes() {
        DurableSchema schema = new("C", 1, new DurableFieldInfo(1, TypeTag.Guid),
            new DurableFieldInfo(2, TypeTag.Decimal), new DurableFieldInfo(3, TypeTag.TimeSpan));
        SchemaCatalogEntry row = SchemaCatalogEntry.ForSchema(new(2), schema);
        byte[] golden = [2, 1, 2, 1, 2, 3, 67, 0, 1, 0, 3, 1, 19, 2, 20, 3, 21];
        Assert.Equal(golden, SchemaCatalogWireCodec.Write([row]));
        SchemaCatalogEntry decoded = Assert.Single(SchemaCatalogWireCodec.Read(golden));
        Assert.Equal(schema, decoded.Schema);
        Assert.Equal(new RepresentationId(2), decoded.Id);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(25)]
    [InlineData(255)]
    public void SparseBuiltinCodesDoNotAdmitCompoundOrUnknownTags(byte code) {
        Assert.Throws<ArgumentOutOfRangeException>(() => TypeExpr.Builtin((TypeTag)code));
        Assert.Throws<InvalidDataException>(() => ReadNominal([1, code]));
    }

    [Fact]
    public void OldBuiltinNominalBytesRemainUnchanged() {
        for (byte code = 1; code <= 14; code++) {
            AssertNominal(TypeExpr.Builtin((TypeTag)code), [1, code]);
        }
    }

    private static void AssertNominal(TypeExpr type, byte[] golden) {
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        TypeExprWireCodec.Write(ref writer, type);
        Assert.Equal(golden, bytes.WrittenSpan.ToArray());
        Assert.Equal(type, ReadNominal(golden));
    }

    private static TypeExpr ReadNominal(byte[] bytes) {
        BareValueReader reader = new(bytes);
        TypeExpr result = TypeExprWireCodec.Read(ref reader);
        reader.EnsureFullyConsumed();
        return result;
    }
}
