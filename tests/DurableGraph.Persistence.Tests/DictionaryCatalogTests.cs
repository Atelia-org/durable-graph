
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using Atelia.Binary;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class DictionaryCatalogTests {
    [Fact]
    public void DictionaryTypeHasIndependentGoldenBytesAndOrderedOperands() {
        TypeExpr type = TypeExpr.Dictionary(TypeExpr.Builtin(TypeTag.String), TypeExpr.Builtin(TypeTag.Int32));
        byte[] typeGolden = Convert.FromHexString("0A01040102");
        Assert.Equal(typeGolden, WriteType(type));
        Assert.Equal(type, ReadType(typeGolden));
        Assert.NotEqual(type, ReadType(Convert.FromHexString("0A01020104")));
    }

    [Fact]
    public void NestedNominalConstructorsHaveIndependentGoldenBytes() {
        TypeExpr nested = TypeExpr.Dictionary(TypeExpr.Builtin(TypeTag.String),
            TypeExpr.List(TypeExpr.VectorArray(TypeExpr.Dictionary(TypeExpr.Builtin(TypeTag.Int32), TypeExpr.Builtin(TypeTag.UInt64)))));
        byte[] typeGolden = Convert.FromHexString("0A010408040A0102010A");
        Assert.Equal(typeGolden, WriteType(nested));
        Assert.Equal(nested, ReadType(typeGolden));
    }

    [Fact]
    public void DictionaryTypesKeepDepthNodeAndClosureLimitsForBothOperands() {
        TypeExpr scalar = TypeExpr.Builtin(TypeTag.Int32);
        TypeExpr type = scalar;
        for (int depth = 1; depth < TypeExpr.MaximumDepth; depth++) { type = TypeExpr.Dictionary(scalar, type); }
        Assert.Equal(type, ReadType(WriteType(type)));
        byte[] deep = [.. Enumerable.Range(0, TypeExpr.MaximumDepth).SelectMany(_ => new byte[] { 10, 1, 2 }), 1, 2];
        Assert.Throws<InvalidDataException>(() => ReadType(deep));
        Assert.Throws<InvalidDataException>(() => ReadType([10, 3, 0, 1, 2]));
        Assert.Throws<InvalidDataException>(() => ReadType([10, 1, 2, 3, 0]));
        Assert.Throws<InvalidDataException>(() => ReadType([11, 1, 2, 1, 2]));
        Assert.Throws<ArgumentException>(() => WriteType(TypeExpr.Dictionary(TypeExpr.Parameter(0), scalar)));
        Assert.Throws<ArgumentException>(() => WriteType(TypeExpr.Dictionary(scalar, TypeExpr.Parameter(0))));
        Assert.Throws<InvalidDataException>(() => ReadType(ExpandedDictionaryType(12)));
    }

    [Theory]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public void BothDictionarySlotsShareTheExactInlineSchemaDepthBudget(int depth, bool valid) {
        DurableSchema inline = new("Value0", 1, SchemaKind.InlineValue);
        for (int index = 1; index < depth; index++) {
            inline = new DurableSchema($"Value{index}", 1, SchemaKind.InlineValue,
                new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: inline));
        }
        ObjectLayout layout = ObjectLayout.ForDictionary(new(
            new(1, TypeTag.InlineValue, inlineSchema: inline), new(2, TypeTag.InlineValue, inlineSchema: inline)));
        SchemaCatalogWorkspace workspace = new(static () => { });
        if (!valid) {
            Assert.Throws<InvalidDataException>(() => { workspace.Set(layout); _ = workspace.Prepare(); });
            Assert.Null(workspace.Accepted);
            return;
        }
        workspace.Set(layout);
        SchemaCatalogView prepared = workspace.Prepare().View;
        Dictionary<ObjectId, ObjectStateRecord> rows = prepared.Rows.ToDictionary();
        ObjectStateRecord declarations = rows[prepared.DescriptorListId];
        rows[declarations.Id] = new(declarations.Id, declarations.Layout.List!,
            new FrozenListState<ObjectId>(prepared.DescriptorIds.Reverse().ToArray()));
        // This owned employee projection validates forward dependencies and memo identity;
        // it does not issue a persistence or execution provenance certificate.
        SchemaCatalogView projected = new(prepared.RootId, rows);
        DictionaryLayout dictionary = projected.ResolveLayout(projected.DescriptorId(layout)).Dictionary!;
        Assert.Equal(depth, projected.Schemas.Count);
        Assert.Equal(inline, dictionary.KeySlot.InlineSchema);
        Assert.Same(projected.Schemas[new(inline.Type, 1)], dictionary.KeySlot.InlineSchema);
        Assert.Same(dictionary.KeySlot.InlineSchema, dictionary.ValueSlot.InlineSchema);
    }

    private static byte[] ExpandedDictionaryType(int depth) => depth == 0 ? [1, 2]
        : [10, .. ExpandedDictionaryType(depth - 1), .. ExpandedDictionaryType(depth - 1)];
    private static byte[] WriteType(TypeExpr type) {
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        TypeExprWireCodec.Write(ref writer, type);
        return bytes.WrittenSpan.ToArray();
    }
    private static TypeExpr ReadType(byte[] bytes) {
        BareValueReader reader = new(bytes);
        TypeExpr type = TypeExprWireCodec.Read(ref reader);
        reader.EnsureFullyConsumed();
        return type;
    }
}
