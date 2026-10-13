using Atelia.DurableGraph.Schema;
using System.Buffers;
using Atelia.Binary;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class ListCatalogTests {
    [Fact]
    public void ListTypeHasIndependentGoldenBytes() {
        Assert.Equal(Convert.FromHexString("080102"), WriteType(TypeExpr.List(TypeExpr.Builtin(TypeTag.Int32))));
        Assert.Equal(TypeExpr.List(TypeExpr.Builtin(TypeTag.Int32)), ReadType(Convert.FromHexString("080102")));
    }

    [Fact]
    public void NestedConstructorsHaveIndependentNominalGoldenBytes() {
        TypeExpr nested = TypeExpr.List(TypeExpr.VectorArray(TypeExpr.List(TypeExpr.Builtin(TypeTag.Int32))));
        byte[] typeGolden = Convert.FromHexString("0804080102");
        Assert.Equal(typeGolden, WriteType(nested));
        Assert.Equal(nested, ReadType(typeGolden));
    }

    [Fact]
    public void ListTypeUsesTheSameDepthAndClosureLimits() {
        TypeExpr type = TypeExpr.Builtin(TypeTag.Int32);
        for (int i = 1; i < TypeExpr.MaximumDepth; i++) { type = TypeExpr.List(type); }
        Assert.Equal(type, ReadType(WriteType(type)));
        byte[] tooDeep = [.. Enumerable.Repeat((byte)8, TypeExpr.MaximumDepth), 1, 2];
        Assert.Throws<InvalidDataException>(() => ReadType(tooDeep));
        Assert.Throws<InvalidDataException>(() => ReadType([8, 3, 0]));
        Assert.Throws<InvalidDataException>(() => ReadType([11, 1, 2]));
        Assert.Throws<ArgumentException>(() => WriteType(TypeExpr.List(TypeExpr.Parameter(0))));
    }

    private static byte[] WriteType(TypeExpr type) {
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        TypeExprWireCodec.Write(ref writer, type);
        return bytes.WrittenSpan.ToArray();
    }
    private static TypeExpr ReadType(byte[] bytes) {
        BareValueReader reader = new(bytes);
        TypeExpr result = TypeExprWireCodec.Read(ref reader);
        reader.EnsureFullyConsumed();
        return result;
    }
}
