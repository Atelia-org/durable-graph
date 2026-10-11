using Atelia.DurableGraph.Schema;
using System.Buffers;
using Atelia.Binary;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class GenericSchemaPersistenceTests {
    [Fact]
    public void GenericCatalogNodeAndEnvelopeHaveIndependentGoldenBytes() {
        TypeExpr type = TypeExpr.Named("B", TypeExpr.Builtin(TypeTag.Int32), TypeExpr.Named("P"));
        DurableSchema schema = new(type, 128);
        // ID 2 reference Schema; Named B with Int32/P arguments; version 128; no base/fields.
        byte[] golden = Convert.FromHexString("020102010203420201020203500080010000");
        Assert.Equal(golden, SchemaCatalogTestData.Write([schema]));
        Assert.Equal(schema, SchemaCatalogTestData.Read(golden)[new(type, 128)]);
        string path = Path.Combine(Path.GetTempPath(), $"generic-envelope-{Guid.NewGuid():N}.rbf");
        try {
            using GraphResources file = GraphResources.CreateNew(path);
            SchemaStore schemas = file.Schemas;
            RepresentationId id = schemas.RegisterRepresentations([ObjectLayout.ForDurable(schema)])[0];
            var encoded = BaseObjectBodyCodec.Encode(id, new([0xAB]));
            Assert.Equal(Convert.FromHexString("0402AB"), encoded.Body.ToArray());
            var decoded = BaseObjectBodyCodec.Decode(encoded.Body, schemas);
            Assert.Equal(schema, decoded.Layout.Schema);
            Assert.Equal(id, decoded.RepresentationId);
            Assert.Equal(new byte[] { 0xAB }, decoded.Body.ToArray());
            Assert.Throws<InvalidDataException>(() => BaseObjectBodyCodec.Decode(
                Convert.FromHexString("0302020342020102020350008001AB"), schemas));
        }
        finally { DeleteTestPath(path); }
    }

    [Fact]
    public void CatalogGoldenSeparatesClosuresAndKeepsNominalReferenceArguments() {
        TypeExpr intType = TypeExpr.Named("B", TypeExpr.Builtin(TypeTag.Int32));
        TypeExpr stringType = TypeExpr.Named("B", TypeExpr.Builtin(TypeTag.String));
        DurableSchema intBox = new(intType, 1, DurableFieldInfo.Reference(1, stringType));
        DurableSchema stringBox = new(stringType, 1);
        byte[] golden = Convert.FromHexString("02020201020342010102010001010F0203420101040301020342010104010000");
        Assert.Equal(golden, SchemaCatalogTestData.Write([intBox, stringBox]));
        var decoded = SchemaCatalogTestData.Read(golden);
        Assert.Equal(intBox, decoded[new(intType, 1)]);
        Assert.Equal(stringBox, decoded[new(stringType, 1)]);
        Assert.Equal(stringType, decoded[new(intType, 1)].Fields[0].TargetType);
    }

    [Theory]
    [InlineData("010103410100010102")]
    [InlineData("02010341010100010102")]
    [InlineData("030102034100010100010102")]
    public void OldSchemaBatchPayloadsCannotMasqueradeAsCatalogBatches(string hex) {
        Assert.Throws<InvalidDataException>(() => SchemaCatalogWireCodec.Read(Convert.FromHexString(hex)));
    }

    [Fact]
    public void ExactClosuresRegisterAndColdReopenWithRepositoryLocalConflictProtection() {
        string path = Path.Combine(Path.GetTempPath(), $"durable-generic-schema-{Guid.NewGuid():N}.rbf");
        try {
            DurableSchema point1 = new("Point", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int32));
            DurableSchema point2 = new("Point", 2, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int64));
            TypeExpr pointBoxType = TypeExpr.Named("Box", TypeExpr.Named("Point"));
            DurableSchema pointBox1 = new(pointBoxType, 1, new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: point1));
            DurableSchema pointBoxMissedBump = new(pointBoxType, 1, new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: point2));
            DurableSchema intBox = new(TypeExpr.Named("Box", TypeExpr.Builtin(TypeTag.Int32)), 1, new DurableFieldInfo(1, TypeTag.Int32));
            using (GraphResources file = GraphResources.CreateNew(path)) {
                var store = file.Schemas;
                store.RegisterBatch([pointBox1, intBox]);
                long tail = DataBytes(file.Data);
                Assert.Throws<SchemaConflictException>(() => store.Register(pointBoxMissedBump));
                Assert.Equal(tail, DataBytes(file.Data));
                Assert.Equal(3, store.Count);
                Assert.False(store.TryGet(new("Point", 2), out _));
                Assert.True(store.TryGet(new(pointBoxType, 1), out var found));
                Assert.Same(pointBox1, found);
            }
            using (GraphResources file = GraphResources.OpenExisting(path)) {
                var store = file.Schemas;
                Assert.Equal(pointBox1, store.GetRequired(pointBoxType, 1));
                Assert.Equal(intBox, store.GetRequired(intBox.Type, 1));
                Assert.Same(store.GetRequired("Point", 1), store.GetRequired(pointBoxType, 1).Fields[0].InlineSchema);
                store.Register(new DurableSchema(pointBoxType, 2, new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: point2)));
                Assert.Equal(5, store.Count);
            }
        }
        finally { DeleteTestPath(path); }
    }

    [Fact]
    public void DeclarationArityAndKindCannotChangeAcrossClosedFamiliesOrNominalOperands() {
        string path = Path.Combine(Path.GetTempPath(), $"durable-generic-arity-{Guid.NewGuid():N}.rbf");
        try {
            using GraphResources file = GraphResources.CreateNew(path);
            var store = file.Schemas;
            store.Register(new DurableSchema(TypeExpr.Named("Box", TypeExpr.Builtin(TypeTag.Int32)), 1));
            long tail = DataBytes(file.Data);
            Assert.Throws<ArgumentException>(() => store.Register(new DurableSchema("Box", 2)));
            Assert.Throws<ArgumentException>(() => store.Register(new DurableSchema(
                TypeExpr.Named("Box", TypeExpr.Builtin(TypeTag.String)), 2, SchemaKind.InlineValue)));
            Assert.Throws<ArgumentException>(() => store.Register(new DurableSchema("Owner", 1,
                DurableFieldInfo.Reference(1, TypeExpr.Named("Box", TypeExpr.Builtin(TypeTag.Int32), TypeExpr.Builtin(TypeTag.Int64))))));
            Assert.Equal(tail, DataBytes(file.Data));
            Assert.Equal(1, store.Count);
        }
        finally { DeleteTestPath(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NominalReferenceKindObligationSurvivesFrameOrderAndColdReopen(bool registerReferenceFirst) {
        DurableSchema owner = new("Owner", 1, DurableFieldInfo.Reference(1, TypeExpr.Named("Point")));
        DurableSchema point = new("Point", 1, SchemaKind.InlineValue);
        DurableSchema first = registerReferenceFirst ? owner : point;
        DurableSchema second = registerReferenceFirst ? point : owner;
        string path = Path.Combine(Path.GetTempPath(), $"durable-generic-reference-kind-{Guid.NewGuid():N}.rbf");
        try {
            using (GraphResources file = GraphResources.CreateNew(path)) {
                var store = file.Schemas;
                long tail = DataBytes(file.Data);
                Assert.Throws<ArgumentException>(() => store.RegisterBatch([first, second]));
                Assert.Equal(tail, DataBytes(file.Data));
                store.Register(first);
                Assert.Equal(1, store.Count);
            }
            using (GraphResources file = GraphResources.OpenExisting(path)) {
                var store = file.Schemas;
                long tail = DataBytes(file.Data);
                Assert.Throws<ArgumentException>(() => store.Register(second));
                Assert.Equal(tail, DataBytes(file.Data));
                Assert.Equal(1, store.Count);
            }
            byte[] invalid = CatalogTestData.Encode(CatalogTestData.Schemas([first, second]));
            Assert.Throws<InvalidDataException>(() => SchemaCatalogWireCodec.Read(invalid));
        }
        finally { DeleteTestPath(path); }
    }

    [Fact]
    public void NominalReferenceKindObligationDoesNotClassifyItsTypeArgumentsAsObjects() {
        DurableSchema point = new("Point", 1, SchemaKind.InlineValue);
        DurableSchema owner = new("Owner", 1, DurableFieldInfo.Reference(1,
            TypeExpr.Named("Box", TypeExpr.Named("Point"), TypeExpr.Builtin(TypeTag.Int32))));
        var decoded = SchemaCatalogTestData.Read(SchemaCatalogTestData.Write([owner, point]));
        Assert.Equal(2, decoded.Count);
        Assert.Equal(owner, decoded[new("Owner", 1)]);
        Assert.Equal(point, decoded[new("Point", 1)]);
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
