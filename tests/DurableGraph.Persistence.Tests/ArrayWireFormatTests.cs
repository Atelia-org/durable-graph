using Atelia.DurableGraph.Schema;
using System.Buffers;
using Atelia.Binary;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class ArrayWireFormatTests {
    [Fact]
    public void RecursiveArrayConstructorsHaveIndependentGoldenBytes() {
        TypeExpr type = TypeExpr.Named("B",
            TypeExpr.VectorArray(TypeExpr.MultiDimArray(TypeExpr.Builtin(TypeTag.Int32), 2)),
            TypeExpr.MultiDimArray(TypeExpr.MultiDimArray(TypeExpr.Named("P"), 4), 3));
        byte[] golden = Convert.FromHexString("0203420204050102060702035000");
        Assert.Equal(golden, WriteType(type));
        Assert.Equal(type, ReadType(golden));
        for (int length = 0; length < golden.Length; length++) {
            byte[] prefix = golden[..length];
            Exception? error = Record.Exception(() => ReadType(prefix));
            Assert.True(error is InvalidDataException or EndOfStreamException, $"Prefix {length}: {error}");
        }
    }

    [Fact]
    public void ArrayNestingParticipatesInWireDepthLimitAndRejectsOpenOperands() {
        TypeExpr type = TypeExpr.Builtin(TypeTag.Int32);
        for (int depth = 1; depth < TypeExpr.MaximumDepth; depth++) { type = TypeExpr.VectorArray(type); }
        Assert.Equal(type, ReadType(WriteType(type)));
        byte[] tooDeep = new byte[TypeExpr.MaximumDepth + 2];
        tooDeep.AsSpan(0, TypeExpr.MaximumDepth).Fill(4);
        tooDeep[^2] = 1;
        tooDeep[^1] = 2;
        Assert.Throws<InvalidDataException>(() => ReadType(tooDeep));
        Assert.Throws<InvalidDataException>(() => ReadType([4, 3, 0]));
        Assert.Throws<InvalidDataException>(() => ReadType([11, 1, 2]));
        Assert.Throws<ArgumentException>(() => WriteType(TypeExpr.VectorArray(TypeExpr.Parameter(0))));
    }

    [Fact]
    public void CatalogPersistsArrayReferencesAndNestedGenericArguments() {
        TypeExpr slot = TypeExpr.VectorArray(TypeExpr.MultiDimArray(TypeExpr.Builtin(TypeTag.Int32), 2));
        DurableSchema schema = new("A", 1, DurableFieldInfo.Reference(1, slot));
        byte[] golden = Convert.FromHexString("0201020102034100010001010F04050102");
        Assert.Equal(golden, SchemaCatalogTestData.Write([schema]));
        Assert.Equal(schema, SchemaCatalogTestData.Read(golden)[new("A", 1)]);
        byte[] old = (byte[])golden.Clone();
        old[0] = 3;
        Assert.Throws<InvalidDataException>(() => SchemaCatalogWireCodec.Read(old));

        DurableSchema generic = new(TypeExpr.Named("B", slot), 1);
        byte[] nested = SchemaCatalogTestData.Write([generic]);
        Assert.Equal(generic, SchemaCatalogTestData.Read(nested)[new(generic.Type, 1)]);
        nested[0] = 3;
        Assert.Throws<InvalidDataException>(() => SchemaCatalogWireCodec.Read(nested));
    }

    [Fact]
    public void ArrayReferenceChecksNestedAritiesWithoutAssumingElementFamilyKind() {
        DurableSchema point = new("P", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int32));
        DurableSchema owner = new("A", 1, DurableFieldInfo.Reference(1, TypeExpr.VectorArray(point.Type)));
        var read = SchemaCatalogWireCodec.Read(SchemaCatalogTestData.Write([owner, point]))
            .ToDictionary(entry => entry.Id);
        Assert.Equal(point, read[new(3)].Schema);
        DurableSchema invalid = new("B", 1, DurableFieldInfo.Reference(1,
            TypeExpr.MultiDimArray(TypeExpr.Named("P", TypeExpr.Builtin(TypeTag.Int32)), 4)));
        byte[] invalidSnapshot = CatalogTestData.Encode(CatalogTestData.Schemas([owner, point, invalid]));
        Assert.Throws<InvalidDataException>(() => SchemaCatalogWireCodec.Read(invalidSnapshot));
    }

    [Theory]
    [InlineData(TypeExprKind.VectorArray, "02010203010402")]
    [InlineData(TypeExprKind.Rank2Array, "02010203010502")]
    [InlineData(TypeExprKind.Rank3Array, "02010203010602")]
    [InlineData(TypeExprKind.Rank4Array, "02010203010702")]
    public void CatalogNodePreservesExactShapeConstructor(TypeExprKind constructor, string hex) {
        ArrayLayout layout = new(constructor, new DurableFieldInfo(1, TypeTag.Int32));
        byte[] golden = Convert.FromHexString(hex);
        Assert.Equal(golden, WriteArray(layout));
        Assert.Equal(layout, ReadArray(golden));
    }

    [Fact]
    public void ReferenceElementNodeSupportsJaggedGenericComposition() {
        ArrayLayout layout = new(TypeExprKind.VectorArray, DurableFieldInfo.Reference(1,
            TypeExpr.VectorArray(TypeExpr.Named("B", TypeExpr.MultiDimArray(TypeExpr.Builtin(TypeTag.String), 4)))));
        byte[] golden = Convert.FromHexString("0201020301040F0402034201070104");
        Assert.Equal(golden, WriteArray(layout));
        Assert.Equal(layout, ReadArray(golden));
        ArrayLayout strings = new(TypeExprKind.VectorArray, new DurableFieldInfo(1, TypeTag.String));
        Assert.Equal(Convert.FromHexString("02010203010404"), WriteArray(strings));
        // A second representation of the same string slot is deliberately noncanonical.
        Assert.Throws<InvalidDataException>(() => ReadArray(Convert.FromHexString("0201020301040F0104")));
    }

    [Fact]
    public void InlineElementNodeUsesEarlierExactSchemaIdAndColdReopens() {
        string path = Path.Combine(Path.GetTempPath(), $"durable-array-wire-{Guid.NewGuid():N}.rbf");
        try {
            DurableSchema point1 = new("P", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int32));
            DurableSchema point2 = new("P", 2, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int64));
            ArrayLayout layout = new(TypeExprKind.VectorArray, new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: point1));
            SchemaCatalogEntry[] rows = [SchemaCatalogEntry.ForSchema(new(2), point1), SchemaCatalogEntry.ForArray(new(3), layout)];
            byte[] snapshot = SchemaCatalogWireCodec.Write(rows);
            Assert.Equal(Convert.FromHexString("030301041002"), snapshot[^6..]);
            SchemaCatalogEntry[] decodedRows = SchemaCatalogWireCodec.Read(snapshot);
            Assert.Same(decodedRows[0].Schema, decodedRows[1].Array!.ElementSlot.InlineSchema);
            // A complete catalog's known class ID cannot be used as an inline element dependency.
            byte[] classPrefix = SchemaCatalogTestData.Write([new DurableSchema("R", 1)]);
            byte[] invalid = [2, 2, .. classPrefix.AsSpan(2), .. Convert.FromHexString("030301041002")];
            Assert.Throws<InvalidDataException>(() => ReadArray(invalid));
            RepresentationId id;
            using (GraphResources file = GraphResources.CreateLegacyNew(path)) {
                var schemas = file.Schemas;
                schemas.Register(point2);
                schemas.Register(point1);
                id = schemas.RegisterRepresentations([ObjectLayout.ForArray(layout)])[0];
                Assert.Equal(4U, id.Value);
            }
            using (GraphResources file = GraphResources.OpenLegacyExisting(path)) {
                var schemas = file.Schemas;
                var decoded = schemas.GetRepresentation(id);
                Assert.Equal(layout, decoded.Array);
                Assert.Same(schemas.GetRequired("P", 1), decoded.Array!.ElementSlot.InlineSchema);
            }
        }
        finally { DeleteTestPath(path); }
    }

    [Theory]
    [InlineData("02010203000402")] // Unknown codec zero.
    [InlineData("02010203020402")] // Future codec.
    [InlineData("02010203010302")] // Open type is not an array constructor.
    [InlineData("02010203010802")] // Unknown rank.
    [InlineData("02010203010400")] // Invalid element slot.
    [InlineData("02010203010411")] // Template parameter is not a closed element slot.
    [InlineData("0201020301040F040300")] // Open reference element operand.
    [InlineData("0201020301041000")] // Zero cannot identify an inline Schema.
    [InlineData("0201020301041001")] // String cannot identify an inline Schema.
    [InlineData("0201020301041002")] // Self dependency.
    [InlineData("0201020301041003")] // Future dependency.
    public void MalformedArrayNodesFailClosed(string hex) {
        Assert.Throws<InvalidDataException>(() => ReadArray(Convert.FromHexString(hex)));
    }

    [Fact]
    public void ArrayWrapperDoesNotConsumeAnExtraExactSchemaDepthLevel() {
        var chain = new DurableSchema[256];
        for (int index = 0; index < chain.Length; index++) {
            DurableFieldInfo field = index == 0 ? new(1, TypeTag.Int32)
                : new(1, TypeTag.InlineValue, inlineSchema: chain[index - 1]);
            chain[index] = new($"I{index:D3}", 1, SchemaKind.InlineValue, field);
        }
        ArrayLayout layout = new(TypeExprKind.VectorArray, new(1, TypeTag.InlineValue, inlineSchema: chain[^1]));
        SchemaCatalogEntry array = SchemaCatalogEntry.ForArray(new(258), layout);
        byte[] snapshot = SchemaCatalogWireCodec.Write([.. CatalogTestData.Schemas(chain), array]);
        Assert.Equal(Convert.FromHexString("8202030104108102"), snapshot[^8..]);
        SchemaCatalogEntry[] decoded = SchemaCatalogWireCodec.Read(snapshot);
        Assert.Equal(layout, decoded[^1].Array);
        Assert.Same(decoded[^2].Schema, decoded[^1].Array!.ElementSlot.InlineSchema);
    }

    [Fact]
    public void EveryTruncatedReferenceArrayNodeFails() {
        byte[] golden = Convert.FromHexString("0201020301040F0402034201070104");
        for (int length = 0; length < golden.Length; length++) {
            byte[] prefix = golden[..length];
            Exception? error = Record.Exception(() => ReadArray(prefix));
            Assert.True(error is InvalidDataException or EndOfStreamException, $"Prefix {length}: {error}");
        }
        Assert.Equal(TypeExprKind.VectorArray, ReadArray(golden).Constructor);
    }

    private static ArrayLayout ReadArray(byte[] bytes) =>
        Assert.Single(SchemaCatalogWireCodec.Read(bytes), static row => row.Array is not null).Array!;

    private static byte[] WriteArray(ArrayLayout layout) =>
        SchemaCatalogWireCodec.Write([SchemaCatalogEntry.ForArray(new(2), layout)]);

    private static byte[] WriteType(TypeExpr type) {
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        TypeExprWireCodec.Write(ref writer, type);
        return buffer.WrittenSpan.ToArray();
    }

    private static TypeExpr ReadType(byte[] bytes) {
        BareValueReader reader = new(bytes);
        TypeExpr result = TypeExprWireCodec.Read(ref reader);
        reader.EnsureFullyConsumed();
        return result;
    }
}
