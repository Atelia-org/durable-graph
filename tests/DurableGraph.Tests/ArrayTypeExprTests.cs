using Atelia.DurableGraph;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Runtime;

namespace Atelia.DurableGraph.Tests;

public sealed class ArrayTypeExprTests {
    [Theory]
    [InlineData(1, TypeExprKind.VectorArray, 4, "Int32[]")]
    [InlineData(2, TypeExprKind.Rank2Array, 5, "Int32[,]")]
    [InlineData(3, TypeExprKind.Rank3Array, 6, "Int32[,,]")]
    [InlineData(4, TypeExprKind.Rank4Array, 7, "Int32[,,,]")]
    public void ArrayConstructorsHaveStableStructure(int rank, TypeExprKind kind, int code, string display) {
        TypeExpr element = TypeExpr.Builtin(TypeTag.Int32);
        TypeExpr array = ArrayOf(element, rank);

        Assert.Equal(kind, array.Kind);
        Assert.Equal(code, (int)array.Kind);
        Assert.True(array.IsArray);
        Assert.True(array.IsClosed);
        Assert.Equal(rank, array.ArrayRank);
        Assert.Same(element, array.ElementType);
        Assert.Same(element, Assert.Single(array.Arguments));
        Assert.Null(array.DefinitionId);
        Assert.Equal(display, array.ToString());
        Assert.Equal(array, ArrayOf(TypeExpr.Builtin(TypeTag.Int32), rank));
        Assert.Equal(array.GetHashCode(), ArrayOf(TypeExpr.Builtin(TypeTag.Int32), rank).GetHashCode());
    }

    [Fact]
    public void OpenMixedCompositionSubstitutesAcrossEveryConstructor() {
        TypeExpr pattern = TypeExpr.Named("Box", TypeExpr.VectorArray(
            TypeExpr.MultiDimArray(TypeExpr.Named("Pair", TypeExpr.Parameter(1), TypeExpr.Parameter(0)), 4)));
        TypeExpr[] arguments = [TypeExpr.VectorArray(TypeExpr.Builtin(TypeTag.Int32)), TypeExpr.Builtin(TypeTag.String)];
        TypeExpr expected = TypeExpr.Named("Box", TypeExpr.VectorArray(
            TypeExpr.MultiDimArray(TypeExpr.Named("Pair", arguments[1], arguments[0]), 4)));

        Assert.False(pattern.IsClosed);
        Assert.True(expected.IsClosed);
        Assert.Equal(expected, StateBindingContext.Substitute(pattern, arguments));
        Assert.Throws<InvalidDataException>(() => StateBindingContext.Substitute(pattern, [arguments[0]]));
    }

    [Fact]
    public void ArrayShapeAndRecursiveElementIdentityRemainDistinct() {
        TypeExpr integer = TypeExpr.Builtin(TypeTag.Int32);
        TypeExpr[] values = [
            integer,
            TypeExpr.Named("Int32"),
            TypeExpr.VectorArray(integer),
            TypeExpr.VectorArray(TypeExpr.Builtin(TypeTag.String)),
            TypeExpr.VectorArray(TypeExpr.VectorArray(integer)),
            TypeExpr.MultiDimArray(integer, 2),
            TypeExpr.MultiDimArray(integer, 3),
            TypeExpr.MultiDimArray(integer, 4),
        ];

        Assert.Equal(values.Length, values.ToHashSet().Count);
        Assert.Equal(values.Length, new SortedSet<TypeExpr>(values).Count);
        Assert.True(TypeExpr.VectorArray(integer).CompareTo(TypeExpr.MultiDimArray(integer, 2)) < 0);
        Assert.False(integer.IsArray);
        Assert.Equal(0, integer.ArrayRank);
        Assert.Null(integer.ElementType);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void RectangularArrayRejectsUnsupportedRanks(int rank) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TypeExpr.MultiDimArray(TypeExpr.Builtin(TypeTag.Int32), rank));

    [Fact]
    public void NullElementIsRejected() {
        Assert.Throws<ArgumentNullException>(() => TypeExpr.VectorArray(null!));
        Assert.Throws<ArgumentNullException>(() => TypeExpr.MultiDimArray(null!, 2));
    }

    [Fact]
    public void ArrayConstructorsShareDepthAndExpandedNodeLimits() {
        TypeExpr nested = TypeExpr.Builtin(TypeTag.Int32);
        for (int depth = 1; depth < TypeExpr.MaximumDepth; depth++) {
            nested = depth % 2 == 0 ? TypeExpr.VectorArray(nested) : TypeExpr.MultiDimArray(nested, 3);
        }
        Assert.Throws<ArgumentException>(() => TypeExpr.VectorArray(nested));
        Assert.Throws<ArgumentException>(() => TypeExpr.Named("Outer", nested));

        // Repeated shared nodes count by expansion; an array is not a limit bypass.
        TypeExpr leaf = TypeExpr.VectorArray(TypeExpr.Builtin(TypeTag.Int32));
        TypeExpr branch = TypeExpr.Named("Branch", Enumerable.Repeat(leaf, 32).ToArray());
        TypeExpr large = TypeExpr.Named("Large", Enumerable.Repeat(branch, 32).ToArray());
        Assert.Throws<ArgumentException>(() => TypeExpr.Named("TooLarge", large, large));
    }

    [Fact]
    public void ClosedArrayReferenceIsCanonicalAndOpenOrStringAliasIsRejected() {
        TypeExpr array = TypeExpr.VectorArray(TypeExpr.Named("Point", TypeExpr.Builtin(TypeTag.Int32)));
        DurableFieldInfo field = DurableFieldInfo.Reference(7, array);
        Assert.Equal(TypeTag.ObjectReference, field.TypeTag);
        Assert.Equal(15, (int)field.TypeTag);
        Assert.Same(array, field.TargetType);
        Assert.Null(field.TargetSchemaId);
        Assert.Equal(array, DurableFieldInfo.NominalType(field));
        Assert.Equal(DurableFieldInfo.Reference(1, array), DurableFieldInfo.WithFieldId(field, 1));

        Assert.Throws<ArgumentException>(() => DurableFieldInfo.Reference(1, TypeExpr.VectorArray(TypeExpr.Parameter(0))));
        Assert.Throws<ArgumentException>(() => DurableFieldInfo.Reference(1, TypeExpr.Builtin(TypeTag.String)));
        Assert.Throws<ArgumentException>(() => DurableFieldInfo.Reference(1, TypeExpr.Builtin(TypeTag.Int32)));
    }

    [Fact]
    public void ArrayParameterScopeIsCheckedInsideRetainedTemplates() {
        TypeExpr pattern = TypeExpr.VectorArray(TypeExpr.MultiDimArray(TypeExpr.Parameter(0), 2));
        StateSchemaTemplate accepted = new("Holder", 1, SchemaKind.ReferenceObject, 1, [new(1, pattern)]);
        Assert.Same(pattern, accepted.Fields[0].ValueType);
        Assert.Throws<ArgumentException>(() => new StateSchemaTemplate(
            "Holder", 1, SchemaKind.ReferenceObject, 0, [new(1, pattern)]));
    }

    private static TypeExpr ArrayOf(TypeExpr element, int rank) =>
        rank == 1 ? TypeExpr.VectorArray(element) : TypeExpr.MultiDimArray(element, rank);
}
