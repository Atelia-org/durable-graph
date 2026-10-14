using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using Atelia.Binary;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class TemporalScalarCatalogTests {
    [Theory]
    [InlineData(typeof(DateOnly), TypeTag.DateOnly, 22, typeof(DateOnlyStateOps))]
    [InlineData(typeof(TimeOnly), TypeTag.TimeOnly, 23, typeof(TimeOnlyStateOps))]
    [InlineData(typeof(DateTimeOffset), TypeTag.DateTimeOffset, 24, typeof(DateTimeOffsetStateOps))]
    public void TemporalLeavesBindCurrentAndStoredWithoutUserDefinitions(Type domain, TypeTag tag, byte code, Type ops) {
        ModelExecutionEnvironment snapshot = new StateModelRegistry().CreateEnvironment();
        TypeExpr nominal = TypeExpr.Builtin(tag);
        Assert.Equal(code, (byte)tag);
        Assert.Equal(nominal, snapshot.GetTypeExpr(domain));
        Assert.Equal(domain, snapshot.GetDomainType(nominal));
        StateValueBinding current = snapshot.ResolveCurrentValue(domain);
        StateValueBinding stored = snapshot.ResolveStoredValue(new(1, tag));
        Assert.Equal(domain, current.StateType);
        Assert.Equal(domain, stored.StateType);
        Assert.Equal(ops, current.StateOpsType);
        Assert.Equal(ops, stored.StateOpsType);
        Assert.Equal(typeof(IdentityValueProjection<>).MakeGenericType(domain), current.ProjectionType);

        Type nullable = typeof(Nullable<>).MakeGenericType(domain);
        Assert.Equal(typeof(NullableState<>).MakeGenericType(domain), snapshot.ResolveCurrentValue(nullable).StateType);
        foreach (Type composed in new[] { nullable, domain.MakeArrayType(), domain.MakeArrayType(2), domain.MakeArrayType(3),
            domain.MakeArrayType(4), typeof(List<>).MakeGenericType(nullable), typeof(Dictionary<,>).MakeGenericType(domain, nullable) }) {
            Assert.Equal(composed, snapshot.GetDomainType(snapshot.GetTypeExpr(composed)));
        }
        AssertNominal(nominal, [1, code]);
        AssertNominal(TypeExpr.Nullable(nominal), [9, 1, code]);
        AssertNominal(TypeExpr.List(nominal), [8, 1, code]);
        AssertNominal(TypeExpr.Dictionary(nominal, nominal), [10, 1, code, 1, code]);
    }

    [Theory]
    [InlineData(TypeTag.DateOnly, 22)]
    [InlineData(TypeTag.TimeOnly, 23)]
    [InlineData(TypeTag.DateTimeOffset, 24)]
    public void ContainerCatalogUsesLeafTagsWithoutAdditionalMetadataNodes(TypeTag tag, byte code) {
        Assert.Equal(code, (byte)tag);
        SchemaCatalogWorkspace workspace = new(static () => { });
        List<ObjectLayout> layouts = [];
        for (byte rank = 4; rank <= 7; rank++) {
            layouts.Add(ObjectLayout.ForArray(new((TypeExprKind)rank, new(1, tag))));
        }
        layouts.Add(ObjectLayout.ForList(new(new(1, tag))));
        layouts.Add(ObjectLayout.ForDictionary(new(new(1, tag), new(2, tag))));
        foreach (ObjectLayout layout in layouts) { workspace.Set(layout); }
        SchemaCatalogView view = workspace.Prepare().View;
        Assert.Empty(view.Schemas);
        Assert.Empty(view.DescriptorIds);
        foreach (ObjectLayout layout in layouts) { view.Require(layout); }
    }

    [Fact]
    public void TemporalFieldsShareOneOwnerSchemaRecord() {
        DurableSchema schema = new("C", 1, new DurableFieldInfo(1, TypeTag.DateOnly),
            new DurableFieldInfo(2, TypeTag.TimeOnly), new DurableFieldInfo(3, TypeTag.DateTimeOffset));
        SchemaCatalogWorkspace workspace = new(static () => { });
        workspace.Set(schema);
        SchemaCatalogView view = workspace.Prepare().View;
        Assert.Equal(schema, Assert.Single(view.Schemas).Value);
        Assert.Single(view.DescriptorIds);
        view.Require(ObjectLayout.ForDurable(schema));
    }

    [Fact]
    public void EarlierBuiltinNominalBytesKeepTheirMeaning() {
        foreach (byte code in Enumerable.Range(1, 14).Concat(Enumerable.Range(19, 3)).Select(value => (byte)value)) {
            AssertNominal(TypeExpr.Builtin((TypeTag)code), [1, code]);
        }
    }

    private static void AssertNominal(TypeExpr type, byte[] golden) {
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        TypeExprWireCodec.Write(ref writer, type);
        Assert.Equal(golden, buffer.WrittenSpan.ToArray());
        BareValueReader reader = new(golden);
        Assert.Equal(type, TypeExprWireCodec.Read(ref reader));
        reader.EnsureFullyConsumed();
    }
}
