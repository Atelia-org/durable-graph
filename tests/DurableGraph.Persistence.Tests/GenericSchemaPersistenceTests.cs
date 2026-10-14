
using Atelia.DurableGraph.Schema;
using System.Buffers;
using Atelia.Binary;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class GenericSchemaPersistenceTests {
    [Fact]
    public void NominalReferenceKindObligationDoesNotClassifyItsTypeArgumentsAsObjects() {
        DurableSchema point = new("Point", 1, SchemaKind.InlineValue);
        DurableSchema owner = new("Owner", 1, DurableFieldInfo.Reference(1,
            TypeExpr.Named("Box", TypeExpr.Named("Point"), TypeExpr.Builtin(TypeTag.Int32))));
        SchemaCatalogWorkspace workspace = new(static () => { });
        workspace.Set(owner);
        workspace.Set(point);
        SchemaCatalogView prepared = workspace.Prepare().View;
        SchemaCatalogView projected = new(prepared.RootId, prepared.Rows.ToDictionary());
        Assert.Equal(2, projected.Schemas.Count);
        Assert.Equal(owner, projected.Schemas[new("Owner", 1)]);
        Assert.Equal(point, projected.Schemas[new("Point", 1)]);
        Assert.Equal(SchemaKind.InlineValue, projected.Schemas[new("Point", 1)].Kind);
    }

    [Theory]
    [InlineData("03")]
    [InlineData("00")]
    [InlineData("010F")]
    [InlineData("0200")]
    [InlineData("02034121")]
    public void UnknownOpenAndMalformedTypeExpressionsAreRejected(string hex) {
        Assert.ThrowsAny<Exception>(() => ReadType(Convert.FromHexString(hex)));
    }

    [Fact]
    public void TypeExpressionAcceptsBoundedRedundantArityAndPreservesNominalIdentity() {
        Assert.Equal(TypeExpr.Named("A"), ReadType(Convert.FromHexString("0203418000")));
    }

    [Fact]
    public void TypeExpressionDepthNodeLimitsAndEveryTruncatedPrefixAreEnforcedBeforeSchemaConstruction() {
        var deep = new List<byte>();
        for (int index = 0; index < TypeExpr.MaximumDepth; index++) { deep.AddRange([2, 3, (byte)'N', 1]); }
        deep.AddRange([1, 2]);
        Assert.Throws<InvalidDataException>(() => ReadType(deep.ToArray()));
        var huge = new List<byte> { 2, 3, (byte)'N', 4 };
        for (int parent = 0; parent < 4; parent++) {
            huge.AddRange([2, 3, (byte)'N', 32]);
            for (int child = 0; child < 32; child++) {
                huge.AddRange([2, 3, (byte)'N', 32]);
                for (int leaf = 0; leaf < 32; leaf++) { huge.AddRange([1, 2]); }
            }
        }
        Assert.Throws<InvalidDataException>(() => ReadType(huge.ToArray()));
        // A separate canonical nested type gives every short prefix a definite failure.
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BareValueWriter(buffer);
        TypeExprWireCodec.Write(ref writer, TypeExpr.Named("Box", TypeExpr.Named("Point", TypeExpr.Builtin(TypeTag.Int32))));
        byte[] valid = buffer.WrittenSpan.ToArray();
        for (int length = 0; length < valid.Length; length++) {
            byte[] prefix = valid[..length];
            Assert.ThrowsAny<Exception>(() => ReadType(prefix));
        }
        Assert.Throws<ArgumentException>(() => WriteType(TypeExpr.Named("Box", TypeExpr.Parameter(0))));
    }

    private static TypeExpr ReadType(byte[] bytes) {
        var reader = new BareValueReader(bytes);
        TypeExpr result = TypeExprWireCodec.Read(ref reader);
        reader.EnsureFullyConsumed();
        return result;
    }

    private static void WriteType(TypeExpr type) {
        var writer = new BareValueWriter(new ArrayBufferWriter<byte>());
        TypeExprWireCodec.Write(ref writer, type);
    }
}
