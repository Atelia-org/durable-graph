using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using Atelia.Binary;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class BclScalarCatalogTests {
    [Theory]
    [InlineData(TypeTag.Guid, 19)]
    [InlineData(TypeTag.Decimal, 20)]
    [InlineData(TypeTag.TimeSpan, 21)]
    public void NewBuiltinLeavesHaveIndependentNominalBytesAndBuiltinCatalogRoute(TypeTag tag, byte code) {
        Assert.Equal(code, (byte)tag);
        TypeExpr type = TypeExpr.Builtin(tag);
        AssertNominal(type, [1, code]);
        AssertNominal(TypeExpr.List(type), [8, 1, code]);
        AssertNominal(TypeExpr.Nullable(type), [9, 1, code]);
        AssertNominal(TypeExpr.Dictionary(type, type), [10, 1, code, 1, code]);
        P0CatalogWorkspace workspace = new(static () => { });
        List<ObjectLayout> layouts = [];
        for (byte rank = 4; rank <= 7; rank++) {
            AssertNominal(rank == 4 ? TypeExpr.VectorArray(type) : TypeExpr.MultiDimArray(type, rank - 3), [rank, 1, code]);
            ObjectLayout layout = ObjectLayout.ForArray(new((TypeExprKind)rank, new(1, tag)));
            layouts.Add(layout);
            workspace.Set(layout);
        }
        P0CatalogView view = workspace.Prepare().View;
        Assert.Empty(view.Schemas);
        Assert.Empty(view.DescriptorIds);
        foreach (ObjectLayout layout in layouts) { view.Require(layout); }
    }

    [Fact]
    public void BuiltinFieldsNeedNoAdditionalSchemaNodes() {
        DurableSchema schema = new("C", 1, new DurableFieldInfo(1, TypeTag.Guid),
            new DurableFieldInfo(2, TypeTag.Decimal), new DurableFieldInfo(3, TypeTag.TimeSpan));
        P0CatalogWorkspace workspace = new(static () => { });
        workspace.Set(schema);
        P0CatalogView view = workspace.Prepare().View;
        Assert.Equal(schema, Assert.Single(view.Schemas).Value);
        Assert.Single(view.DescriptorIds);
        view.Require(ObjectLayout.ForDurable(schema));
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
