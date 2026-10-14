using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
namespace Atelia.DurableGraph.Tests;

public sealed class ListTypeExprTests {
    [Fact]
    public void ListIsAnIndependentRecursiveNominalConstructor() {
        TypeExpr integer = TypeExpr.Builtin(TypeTag.Int32);
        TypeExpr list = TypeExpr.List(integer);
        Assert.Equal(8, (int)list.Kind);
        Assert.True(list.IsList);
        Assert.False(list.IsArray);
        Assert.Equal(0, list.ArrayRank);
        Assert.Same(integer, list.ElementType);
        Assert.True(list.IsClosed);
        Assert.Equal("List<Int32>", list.ToString());
        Assert.Equal(list, TypeExpr.List(integer));
        Assert.Equal(list.GetHashCode(), TypeExpr.List(integer).GetHashCode());
        Assert.Equal(4, new HashSet<TypeExpr> { list, TypeExpr.Named("List", integer), TypeExpr.VectorArray(integer), TypeExpr.List(list) }.Count);
        Assert.Equal(list, DurableFieldInfo.NominalType(DurableFieldInfo.Reference(7, list)));
        Assert.Throws<ArgumentNullException>(() => TypeExpr.List(null!));
        Assert.Throws<ArgumentException>(() => DurableFieldInfo.Reference(1, TypeExpr.List(TypeExpr.Parameter(0))));
    }

    [Fact]
    public void SubstitutionTraversesListsArraysAndNamedArguments() {
        TypeExpr open = TypeExpr.Named("Box", TypeExpr.List(TypeExpr.MultiDimArray(TypeExpr.List(TypeExpr.Parameter(0)), 2)));
        TypeExpr element = TypeExpr.Named("Point");
        TypeExpr expected = TypeExpr.Named("Box", TypeExpr.List(TypeExpr.MultiDimArray(TypeExpr.List(element), 2)));
        Assert.False(open.IsClosed);
        Assert.Equal(expected, StateBindingContext.Substitute(open, [element]));
        Assert.Throws<InvalidDataException>(() => StateBindingContext.Substitute(open, []));
        Assert.Throws<ArgumentException>(() => new StateSchemaTemplate("Owner", 1, SchemaKind.ReferenceObject, 0, [new(1, open)]));
        TypeExpr nested = element;
        for (int index = 1; index < TypeExpr.MaximumDepth; index++) { nested = TypeExpr.List(nested); }
        Assert.Throws<ArgumentException>(() => TypeExpr.List(nested));
    }

    [Fact]
    public void ListLayoutOwnsExactElementButReferenceConstraintIsNominal() {
        DurableSchema v1 = new("Point", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int32));
        DurableSchema v2 = new("Point", 2, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int64));
        ListLayout first = new(new(3, TypeTag.InlineValue, inlineSchema: v1));
        ListLayout next = new(new(1, TypeTag.InlineValue, inlineSchema: v2));
        Assert.Equal(first.Type, next.Type);
        Assert.NotEqual(first, next);
        Assert.Equal(1, first.ElementSlot.FieldId);
        Assert.Equal(ObjectStateKind.List, ObjectLayout.ForList(first).Kind);
        Assert.Null(ObjectLayout.ForList(first).Schema);
        Assert.False(ObjectLayout.ForList(first).Equals(ObjectLayout.ForList(next)));
        Assert.True(StateReferenceValidator.Accepts(ObjectLayout.ForList(next), first.Type));
        Assert.False(StateReferenceValidator.Accepts(ObjectLayout.ForList(first), TypeExpr.VectorArray(v1.Type)));
        Assert.Equal(2U, first.CodecVersion);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ListLayout(first.ElementSlot, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ListLayout(first.ElementSlot, 3));
        Assert.Throws<ArgumentException>(() => new ListLayout(default));
    }

    [Fact]
    public void ObjectReadTableRejectsListSubclassForBuiltinListSlot() {
        ObjectReadTable objects = new(new Dictionary<ObjectId, object> { [new(1)] = new DerivedList() });
        Assert.Throws<InvalidDataException>(() => objects.ResolveObject<List<int>>(new(1)));
        Assert.Null(objects.ResolveObject<List<int>>(default));
        Assert.True(BuiltinStateValues.TryBindCurrent(typeof(int), out StateValueBinding element));
        Assert.Throws<ArgumentException>(() => ListObjectBinding.Create(typeof(DerivedList), new(element.Slot), element));
        Assert.Throws<ArgumentException>(() => ListObjectBinding.Create(typeof(IList<int>), new(element.Slot), element));
        Assert.Throws<ArgumentException>(() => ListObjectBinding.Create(typeof(List<long>), new(element.Slot), element));
    }

    private sealed class DerivedList : List<int> { }
}
