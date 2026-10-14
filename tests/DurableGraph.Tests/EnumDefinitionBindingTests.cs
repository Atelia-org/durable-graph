using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Xunit;

namespace Atelia.DurableGraph.Tests;

public class EnumDefinitionBindingTests {
    private const string DefinitionId = "Enums.Mode";

    [Theory]
    [InlineData(typeof(SByteMode), TypeTag.SByte)]
    [InlineData(typeof(ByteMode), TypeTag.Byte)]
    [InlineData(typeof(Int16Mode), TypeTag.Int16)]
    [InlineData(typeof(UInt16Mode), TypeTag.UInt16)]
    [InlineData(typeof(Int32Mode), TypeTag.Int32)]
    [InlineData(typeof(UInt32Mode), TypeTag.UInt32)]
    [InlineData(typeof(Int64Mode), TypeTag.Int64)]
    [InlineData(typeof(UInt64Mode), TypeTag.UInt64)]
    public void ExplicitEnumDefinitionAcceptsItsUnderlyingInteger(Type enumType, TypeTag tag) {
        StateDefinitionBinding definition = Define(enumType, Template(1, new StateFieldTemplate(1, TypeExpr.Builtin(tag))));

        Assert.Same(enumType, definition.DomainTypeDefinition);
        Assert.Equal(SchemaKind.InlineValue, definition.Kind);
        Assert.Equal(0, definition.Arity);
        Assert.Equal(TypeExpr.Builtin(tag), Assert.Single(Assert.Single(definition.Templates).Fields).ValueType);
    }

    [Theory]
    [InlineData(typeof(SByteMode), TypeTag.Byte)]
    [InlineData(typeof(ByteMode), TypeTag.SByte)]
    [InlineData(typeof(Int16Mode), TypeTag.UInt16)]
    [InlineData(typeof(UInt16Mode), TypeTag.Int16)]
    [InlineData(typeof(Int32Mode), TypeTag.UInt32)]
    [InlineData(typeof(UInt32Mode), TypeTag.Int32)]
    [InlineData(typeof(Int64Mode), TypeTag.UInt64)]
    [InlineData(typeof(UInt64Mode), TypeTag.Int64)]
    public void EnumDefinitionRejectsWrongSignednessEvenAtSameWidth(Type enumType, TypeTag wrongTag) {
        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            Define(enumType, Template(1, new StateFieldTemplate(1, TypeExpr.Builtin(wrongTag)))));
        Assert.Equal("templates", error.ParamName);
    }

    [Theory]
    [InlineData(TypeTag.Boolean)]
    [InlineData(TypeTag.Char)]
    [InlineData(TypeTag.Single)]
    [InlineData(TypeTag.Double)]
    [InlineData(TypeTag.String)]
    public void NonIntegerCurrentEnumSlotIsRejected(TypeTag tag) {
        Assert.Throws<ArgumentException>(() => Define(typeof(Int32Mode), Template(1, new StateFieldTemplate(1, TypeExpr.Builtin(tag)))));
    }

    [Fact]
    public void EnumDefinitionRejectsWrongFieldIdMissingOrExtraFieldsAndNullable() {
        TypeExpr value = TypeExpr.Builtin(TypeTag.Byte);
        Assert.Throws<ArgumentException>(() => Define(typeof(ByteMode), Template(1, new StateFieldTemplate(2, value))));
        Assert.Throws<ArgumentException>(() => Define(typeof(ByteMode), Template(1)));
        Assert.Throws<ArgumentException>(() => Define(typeof(ByteMode), Template(1, new StateFieldTemplate(1, value), new StateFieldTemplate(2, value))));
        Assert.Throws<ArgumentException>(() => Define(typeof(ByteMode), Template(1, new StateFieldTemplate(1, TypeExpr.Nullable(value)))));
        Assert.Throws<ArgumentException>(() => Define(typeof(ByteMode), Template(1, new StateFieldTemplate(1, TypeExpr.Named("Other.Inline"), 1))));
    }

    [Fact]
    public void EnumDefinitionRejectsReferenceKindAndGenericArity() {
        Assert.Throws<ArgumentException>(() => new StateDefinitionBinding(DefinitionId,
            SchemaKind.ReferenceObject, 0, typeof(ByteMode),
            [new(DefinitionId, 1, SchemaKind.ReferenceObject, 0, [new StateFieldTemplate(1, TypeExpr.Builtin(TypeTag.Byte))])]));
        Assert.Throws<ArgumentException>(() => new StateDefinitionBinding(DefinitionId,
            SchemaKind.InlineValue, 1, typeof(ByteMode),
            [new StateSchemaTemplate(DefinitionId, 1, SchemaKind.InlineValue, 1, [new StateFieldTemplate(1, TypeExpr.Parameter(0))])]));
        Assert.Throws<ArgumentException>(() => Define(typeof(ByteMode),
            new StateSchemaTemplate(DefinitionId, 1, SchemaKind.InlineValue, 1, [new StateFieldTemplate(1, TypeExpr.Parameter(0))])));
    }

    [Fact]
    public void CurrentEnumTemplateCannotHaveRepresentationParameters() {
        StateSchemaTemplate template = new(DefinitionId, 1, SchemaKind.InlineValue, 0,
            [new StateFieldTemplate(1, TypeExpr.Builtin(TypeTag.Byte))], stateParameters: [new(TypeExpr.Builtin(TypeTag.Byte))]);
        Assert.Throws<ArgumentException>(() => Define(typeof(ByteMode), template));
    }

    [Fact]
    public void OnlyHighestVersionIsConstrainedByCurrentEnumWidth() {
        StateSchemaTemplate historical = Template(1, new StateFieldTemplate(1, TypeExpr.Builtin(TypeTag.Byte)));
        StateSchemaTemplate current = Template(2, new StateFieldTemplate(1, TypeExpr.Builtin(TypeTag.UInt64)));
        // Input order must not choose which template receives current CLR validation.
        StateDefinitionBinding definition = Define(typeof(UInt64Mode), current, historical);
        TestContext context = new(definition);
        DurableSchema stored = new(DefinitionId, 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Byte));
        DurableSchema latest = new(DefinitionId, 2, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.UInt64));

        Assert.Equal(2, definition.CurrentVersion);
        Assert.Same(stored, context.BindSchema(stored).Schema);
        Assert.Same(latest, context.BindSchema(latest).Schema);
        Assert.Throws<InvalidDataException>(() => new TestContext(definition).BindSchema(
            new(DefinitionId, 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.UInt64))));
        Assert.Throws<ArgumentException>(() => Define(typeof(ByteMode), historical, current));
    }

    [Fact]
    public void RetainedInlineHistoryHasNoEnumShapeConstraint() {
        // No persistent enum/struct discriminator exists: a previous struct may
        // have an arbitrary inline shape, with conversion selected by its owner.
        StateSchemaTemplate historical = Template(1,
            new StateFieldTemplate(7, TypeExpr.Builtin(TypeTag.Int32)), new StateFieldTemplate(9, TypeExpr.Builtin(TypeTag.Boolean)));
        StateSchemaTemplate current = Template(2, new StateFieldTemplate(1, TypeExpr.Builtin(TypeTag.Byte)));
        StateDefinitionBinding definition = Define(typeof(ByteMode), historical, current);
        DurableSchema stored = new(DefinitionId, 1, SchemaKind.InlineValue,
            new DurableFieldInfo(7, TypeTag.Int32), new DurableFieldInfo(9, TypeTag.Boolean));
        Assert.Same(stored, new TestContext(definition).BindSchema(stored).Schema);

        StateDefinitionBinding historyOnly = new(DefinitionId, SchemaKind.InlineValue, 0, null, [historical]);
        Assert.Null(historyOnly.DomainTypeDefinition);
        Assert.Same(stored, new TestContext(historyOnly).BindSchema(stored).Schema);
    }

    [Fact]
    public void EquivalentCurrentStructDoesNotAcquireEnumRestrictions() {
        StateDefinitionBinding definition = Define(typeof(InlineValue), Template(1, new StateFieldTemplate(1, TypeExpr.Builtin(TypeTag.Byte))));
        Assert.Same(typeof(InlineValue), definition.DomainTypeDefinition);
        Assert.Throws<ArgumentException>(() => Define(typeof(byte), Template(1, new StateFieldTemplate(1, TypeExpr.Builtin(TypeTag.Byte)))));
    }

    private static StateSchemaTemplate Template(int version, params StateFieldTemplate[] fields) =>
        new(DefinitionId, version, SchemaKind.InlineValue, 0, fields);

    private static StateDefinitionBinding Define(Type domain, params StateSchemaTemplate[] templates) =>
        new(DefinitionId, SchemaKind.InlineValue, 0, domain, templates);

    private enum SByteMode : sbyte { }
    private enum ByteMode : byte { }
    private enum Int16Mode : short { }
    private enum UInt16Mode : ushort { }
    private enum Int32Mode : int { }
    private enum UInt32Mode : uint { }
    private enum Int64Mode : long { }
    private enum UInt64Mode : ulong { }
    private readonly struct InlineValue { }

    private sealed class TestContext(StateDefinitionBinding definition) : StateBindingContext {
        public override bool TryGetCurrentModel(Type type, out StateModelBinding? model) { model = null; return false; }
        public override StateValueBinding ResolveCurrentValue(Type type) => throw new NotSupportedException();
        public override StateValueBinding ResolveStoredValue(DurableFieldInfo slot) => throw new NotSupportedException();
        public override StateReaderBinding ResolveReader(DurableSchema schema) => throw new NotSupportedException();
        public override TypeExpr GetTypeExpr(Type type) => throw new NotSupportedException();
        public override Type GetDomainType(TypeExpr type) => throw new NotSupportedException();
        public override StateDefinitionBinding GetDefinition(string id) => id == definition.DefinitionId ? definition : throw new InvalidDataException();
        protected override bool TryGetSchema(TypeExpr type, int version, out DurableSchema? schema) { schema = null; return false; }
    }
}
