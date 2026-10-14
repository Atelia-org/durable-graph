using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Xunit;

namespace Atelia.DurableGraph.Tests;

public class NullableSchemaTests {
    private static readonly TypeExpr Int = TypeExpr.Builtin(TypeTag.Int32);
    private static readonly TypeExpr Point = TypeExpr.Named("Nullable.Point");

    [Fact]
    public void NullableLayoutCanonicalizesChildAndIncludesItsExactVersion() {
        DurableSchema point1 = PointSchema(1);
        DurableSchema point2 = PointSchema(2);
        DurableFieldInfo first = DurableFieldInfo.Nullable(7, new(45, TypeTag.InlineValue, inlineSchema: point1));
        DurableFieldInfo same = DurableFieldInfo.Nullable(7, new(2, TypeTag.InlineValue, inlineSchema: PointSchema(1)));
        DurableFieldInfo next = DurableFieldInfo.Nullable(7, new(1, TypeTag.InlineValue, inlineSchema: point2));
        Assert.Equal(1, first.NullableLayout!.ElementSlot.FieldId);
        Assert.Null(first.InlineSchema);
        Assert.Same(point1, first.ValueSchema);
        Assert.Equal(first, same);
        Assert.Equal(first.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(first, next);
        Assert.Equal(TypeExpr.Nullable(Point), StateBindingContext.NominalType(first));
        Assert.Equal(first.NullableLayout, StateBindingContext.WithFieldId(first, 8).NullableLayout);
        Assert.Equal(new DurableSchema("Owner", 1, first), new DurableSchema("Owner", 1, same));
        Assert.NotEqual(new DurableSchema("Owner", 1, first), new DurableSchema("Owner", 1, next));
    }

    [Fact]
    public void InvalidChildrenAndBareNullableTagAreRejected() {
        Assert.Throws<ArgumentNullException>(() => new DurableFieldInfo(1, TypeTag.Nullable));
        Assert.Throws<ArgumentException>(() => DurableFieldInfo.Nullable(1, default));
        Assert.Throws<ArgumentException>(() => DurableFieldInfo.Nullable(1, new(1, TypeTag.String)));
        Assert.Throws<ArgumentException>(() => DurableFieldInfo.Nullable(1, DurableFieldInfo.Reference(1, Point)));
        Assert.Throws<ArgumentException>(() => DurableFieldInfo.Nullable(1, DurableFieldInfo.Nullable(1, new(1, TypeTag.Int32))));
        Assert.Throws<ArgumentException>(() => TypeExpr.Nullable(TypeExpr.Builtin(TypeTag.String)));
        Assert.Throws<ArgumentException>(() => TypeExpr.Nullable(TypeExpr.VectorArray(Int)));
        Assert.Throws<ArgumentException>(() => TypeExpr.Nullable(TypeExpr.List(Int)));
        Assert.Throws<ArgumentException>(() => TypeExpr.Nullable(TypeExpr.Nullable(Int)));
    }

    [Fact]
    public void NullableNominalSubstitutionPreservesWrapperAndLimits() {
        TypeExpr pattern = TypeExpr.Nullable(TypeExpr.Parameter(0));
        Assert.False(pattern.IsClosed);
        TypeExpr closed = StateBindingContext.Substitute(pattern, [Point]);
        Assert.True(closed.IsNullable);
        Assert.True(closed.IsClosed);
        Assert.Equal("Nullable<Nullable.Point>", closed.ToString());
        Assert.Throws<ArgumentException>(() => StateBindingContext.Substitute(pattern, [TypeExpr.Nullable(Int)]));
        TypeExpr deep = Int;
        for (int index = 1; index < TypeExpr.MaximumDepth; index++) { deep = TypeExpr.Named("Wrap", deep); }
        Assert.Throws<ArgumentException>(() => TypeExpr.Nullable(deep));
    }

    [Fact]
    public void NullableParameterBindsBothWrapperAndChildOperands() {
        TypeExpr parameter = TypeExpr.Parameter(0);
        TypeExpr wrapper = TypeExpr.Nullable(parameter);
        TestContext context = Context(new("Nullable.Owner", 1, SchemaKind.ReferenceObject, 1, [new(1, wrapper)]));
        DurableFieldInfo slot = NullablePoint(1);
        DurableSchema owner = new(TypeExpr.Named("Nullable.Owner", Point), 1, slot);
        StateSchemaBinding binding = context.BindSchema(owner);
        Assert.True(binding.TryGetSlot(wrapper, null, out DurableFieldInfo actualWrapper));
        Assert.Equal(slot, actualWrapper);
        Assert.True(binding.TryGetSlot(parameter, null, out DurableFieldInfo actualChild));
        Assert.Equal(slot.NullableLayout!.ElementSlot, actualChild);
        Assert.Single(binding.FieldSlots);
    }

    [Fact]
    public void ParameterClosedAsNullableBindsWholeSlotRatherThanInnerOperand() {
        TypeExpr parameter = TypeExpr.Parameter(0);
        TestContext context = Context(new("Nullable.Owner", 1, SchemaKind.ReferenceObject, 1, [new(1, parameter)]));
        DurableFieldInfo slot = NullablePoint(1);
        DurableSchema owner = new(TypeExpr.Named("Nullable.Owner", TypeExpr.Nullable(Point)), 1, slot);
        StateSchemaBinding binding = context.BindSchema(owner);
        Assert.True(binding.TryGetSlot(parameter, null, out DurableFieldInfo actual));
        Assert.Equal(slot, actual);
        Assert.Single(binding.ValueSlots);
    }

    [Fact]
    public void BaseParameterSubstitutionDoesNotInventAFixedInlineVersionRequirement() {
        TypeExpr wrappedPoint = TypeExpr.Nullable(Point);
        StateSchemaTemplate owner = new("Nullable.Owner", 1, SchemaKind.ReferenceObject, 0, [],
            new(TypeExpr.Named("Nullable.Base", wrappedPoint), 1));
        TestContext context = new(
            new("Nullable.Owner", SchemaKind.ReferenceObject, 0, null, [owner]),
            new("Nullable.Base", SchemaKind.ReferenceObject, 1, null,
                [new("Nullable.Base", 1, SchemaKind.ReferenceObject, 1, [new(1, TypeExpr.Parameter(0))])]),
            new("Nullable.Point", SchemaKind.InlineValue, 0, null,
                [new("Nullable.Point", 1, SchemaKind.InlineValue, 0, [new(1, Int)])]));
        DurableSchema ancestor = new(TypeExpr.Named("Nullable.Base", wrappedPoint), 1, NullablePoint(1));
        DurableSchema schema = new("Nullable.Owner", 1, [], ancestor);
        StateSchemaBinding binding = context.BindSchema(schema);
        Assert.True(binding.TryGetSlot(wrappedPoint, null, out DurableFieldInfo wrapper));
        Assert.Equal(NullablePoint(1), wrapper);
        Assert.True(binding.TryGetSlot(Point, null, out DurableFieldInfo child));
        Assert.Equal(PointSchema(1), child.InlineSchema);
    }

    [Fact]
    public void FixedNullableInlineVersionIsRequiredAndChecked() {
        TypeExpr wrapper = TypeExpr.Nullable(Point);
        TestContext context = Context(new("Nullable.Owner", 1, SchemaKind.ReferenceObject, 0, [new(1, wrapper, 1)]));
        DurableSchema owner = new("Nullable.Owner", 1, NullablePoint(1));
        StateSchemaBinding binding = context.BindSchema(owner);
        Assert.True(binding.TryGetSlot(wrapper, 1, out _));
        Assert.True(binding.TryGetSlot(Point, 1, out _));
        Assert.Throws<InvalidDataException>(() => context.BindSchema(new("Nullable.Owner", 1, NullablePoint(2))));
        TestContext missing = Context(new("Nullable.Owner", 1, SchemaKind.ReferenceObject, 0, [new(1, wrapper)]));
        Assert.Throws<InvalidDataException>(() => missing.BindSchema(owner));
        Assert.Throws<ArgumentException>(() => new StateSchemaTemplate("Invalid", 1, SchemaKind.ReferenceObject, 1,
            [new(1, TypeExpr.Nullable(TypeExpr.Parameter(0)), 1)]));
    }

    [Fact]
    public void CachedBindingChecksLateNullableChildRegistrationConflict() {
        TestContext context = Context(new("Nullable.Owner", 1, SchemaKind.ReferenceObject, 0,
            [new(1, TypeExpr.Nullable(Point), 1)]));
        DurableSchema owner = new("Nullable.Owner", 1, NullablePoint(1));
        context.BindSchema(owner);
        context.Registered.Add((Point, 1), new(Point, 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int64)));
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => context.BindSchema(owner));
        Assert.IsType<SchemaConflictException>(error.InnerException);
        Assert.Contains("field[1].inline", error.Message);
    }

    [Fact]
    public void NullableDependenciesRetainSharedDagEquality() {
        DurableSchema first = PointSchema(1), same = PointSchema(1), different = PointSchema(2);
        for (int index = 0; index < 80; index++) {
            first = Wrap(first, index);
            same = Wrap(same, index);
            different = Wrap(different, index);
        }
        Assert.Equal(first, same);
        Assert.Equal(first.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(first, different);

        static DurableSchema Wrap(DurableSchema child, int index) => new($"Layer{index}", 1, SchemaKind.InlineValue,
            DurableFieldInfo.Nullable(1, new(1, TypeTag.InlineValue, inlineSchema: child)),
            DurableFieldInfo.Nullable(2, new(1, TypeTag.InlineValue, inlineSchema: child)));
    }

    private static DurableSchema PointSchema(int version) => new(Point, version, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int32));
    private static DurableFieldInfo NullablePoint(int version) => DurableFieldInfo.Nullable(1, new(1, TypeTag.InlineValue, inlineSchema: PointSchema(version)));

    private static TestContext Context(StateSchemaTemplate owner) => new(
        new("Nullable.Owner", SchemaKind.ReferenceObject, owner.Arity, null, [owner]),
        new("Nullable.Point", SchemaKind.InlineValue, 0, null,
            [new("Nullable.Point", 1, SchemaKind.InlineValue, 0, [new(1, Int)]),
             new("Nullable.Point", 2, SchemaKind.InlineValue, 0, [new(1, Int)])]));

    private sealed class TestContext(params StateDefinitionBinding[] definitions) : StateBindingContext {
        internal readonly Dictionary<(TypeExpr Type, int Version), DurableSchema> Registered = [];
        public override bool TryGetCurrentModel(Type type, out StateModelBinding? model) { model = null; return false; }
        public override StateValueBinding ResolveCurrentValue(Type type) => throw new NotSupportedException();
        public override StateValueBinding ResolveStoredValue(DurableFieldInfo slot) => throw new NotSupportedException();
        public override StateReaderBinding ResolveReader(DurableSchema schema) => throw new NotSupportedException();
        public override TypeExpr GetTypeExpr(Type type) => throw new NotSupportedException();
        public override Type GetDomainType(TypeExpr type) => throw new NotSupportedException();
        public override StateDefinitionBinding GetDefinition(string id) => definitions.Single(definition => definition.DefinitionId == id);
        protected override bool TryGetSchema(TypeExpr type, int version, out DurableSchema? schema) => Registered.TryGetValue((type, version), out schema);
    }
}
