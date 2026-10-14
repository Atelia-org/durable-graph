using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed class StateBaseProjectionTests {
    [Fact]
    public void ProjectionUsesExistingDerivedInstanceWithoutAllocatingOrRegisteringIt() {
        DurableSchema schema = SimpleSchema();
        StateModelBinding<Base, int> model = Model(schema, supportsBaseProjection: true);
        TestContext bindings = new(model);
        StateBaseProjection<Base, int> projection = bindings.BindBaseProjection<Base, int>(schema);
        Derived domain = new() { Value = 17, Extra = 91 };
        using CaptureContext capture = new CaptureSession().BeginCapture();

        Assert.Equal(17, projection.Capture(domain, capture));
        Assert.Empty(capture.Seal().Objects);
        int next = 29;
        projection.Hydrate(domain, in next, new ObjectReadTable(new Dictionary<ObjectId, object>()));
        Assert.Equal(29, domain.Value);
        Assert.Equal(91, domain.Extra);
    }

    [Fact]
    public void WholeObjectEntryPointsStillRejectADerivedInstance() {
        DurableSchema schema = SimpleSchema();
        StateModelBinding<Base, int> model = Model(schema, supportsBaseProjection: true);
        Derived domain = new();
        ObjectStateRecord state = new(new ObjectId(1), schema, 7);
        using CaptureContext capture = new CaptureSession().BeginCapture();
        Assert.Throws<InvalidDataException>(() => model.AddRoot(capture, domain));
        Assert.Throws<InvalidDataException>(() => model.Capture(new ObjectId(1), domain, capture));
        Assert.Throws<InvalidDataException>(() => model.Hydrate(domain, state,
            new ObjectReadTable(new Dictionary<ObjectId, object>())));
    }

    [Fact]
    public void ManualBindingsMustExplicitlyGrantProjectionCapability() {
        DurableSchema schema = SimpleSchema();
        TestContext context = new(Model(schema));
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => context.BindBaseProjection<Base, int>(schema));
        Assert.Contains("explicitly support", error.Message);
    }

    [Fact]
    public void BindingRejectsWrongDtoDomainOrCompleteSchema() {
        DurableSchema schema = SimpleSchema();
        TestContext context = new(Model(schema, supportsBaseProjection: true));
        Assert.Throws<InvalidDataException>(() => context.BindBaseProjection<Base, long>(schema));
        // Deliberately return the same base model even for this wrong CLR request.
        Assert.Throws<InvalidDataException>(() => context.BindBaseProjection<Derived, int>(schema));
        Assert.Throws<InvalidDataException>(() => context.BindBaseProjection<Base, int>(
            new DurableSchema(schema.SchemaId, schema.Version, new DurableFieldInfo(1, TypeTag.UInt32))));
        Assert.Throws<InvalidDataException>(() => context.BindBaseProjection<Base, int>(
            new DurableSchema(schema.SchemaId, schema.Version + 1, schema.Fields.ToArray())));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CachedSchemaBindingRechecksLateRegisteredInlineAndBaseConflicts(bool ancestorConflict) {
        DurableSchema inline = new("projection-value", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int32));
        DurableSchema ancestor = new("projection-ancestor", 1);
        DurableSchema schema = new("projection-base", 1,
            [new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: inline)], ancestor);
        TestContext context = new(Model(schema, supportsBaseProjection: true));
        context.BindBaseProjection<Base, int>(schema);
        DurableSchema conflict = ancestorConflict
            ? new(ancestor.SchemaId, 1, new DurableFieldInfo(1, TypeTag.Byte))
            : new(inline.SchemaId, 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int64));
        context.Registered.Add((conflict.Type, conflict.Version), conflict);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => context.BindBaseProjection<Base, int>(schema));
        Assert.IsType<SchemaConflictException>(error.InnerException);
        Assert.Contains(ancestorConflict ? ".base" : ".field[1].inline", error.Message);
    }

    [Fact]
    public void BindingRejectsRetainedTemplateMismatchBeforeProjectionIsReturned() {
        DurableSchema schema = SimpleSchema();
        TestContext context = new(Model(schema, supportsBaseProjection: true));
        context.Definitions[schema.SchemaId] = new(schema.SchemaId, SchemaKind.ReferenceObject, 0, null,
            [new(schema.SchemaId, 1, SchemaKind.ReferenceObject, 0, [new(1, TypeExpr.Builtin(TypeTag.UInt32))])]);
        Assert.Throws<InvalidDataException>(() => context.BindBaseProjection<Base, int>(schema));
    }

    [Fact]
    public void NullArgumentsFailBeforeCallingTheGeneratedDelegates() {
        DurableSchema schema = SimpleSchema();
        TestContext bindings = new(Model(schema, supportsBaseProjection: true));
        Assert.Throws<ArgumentNullException>(() => bindings.BindBaseProjection<Base, int>(null!));
        StateBaseProjection<Base, int> projection = bindings.BindBaseProjection<Base, int>(schema);
        using CaptureContext capture = new CaptureSession().BeginCapture();
        ObjectReadTable objects = new(new Dictionary<ObjectId, object>());
        int state = 7;
        Assert.Throws<ArgumentNullException>(() => projection.Capture(null!, capture));
        Assert.Throws<ArgumentNullException>(() => projection.Capture(new Derived(), null!));
        Assert.Throws<ArgumentNullException>(() => projection.Hydrate(null!, in state, objects));
        Assert.Throws<ArgumentNullException>(() => projection.Hydrate(new Derived(), in state, null!));
    }

    private static DurableSchema SimpleSchema() => new("projection-base", 1, new DurableFieldInfo(1, TypeTag.Int32));

    private static StateModelBinding<Base, int> Model(DurableSchema schema, bool supportsBaseProjection = false) {
        StatePreparationBinding<int> preparation = new(schema,
            static (in int state) => throw new NotSupportedException(),
            static (in int prior, in int next) => throw new NotSupportedException());
        StateReaderBinding<int> reader = new(schema,
            static (ref BareValueReader body) => throw new NotSupportedException(),
            static (ref BareValueReader body, in int prior) => throw new NotSupportedException(),
            static (in int state, IStateReferenceVisitor visitor) => { });
        return new(preparation, [reader],
            static _ => throw new InvalidOperationException("Projection must not normalize."),
            static () => throw new InvalidOperationException("Projection must not allocate."),
            static (Base domain, in int state, ObjectReadTable objects) => domain.Value = state,
            static (domain, _) => domain.Value,
            static (in int state, IStateReferenceVisitor visitor) => { },
            supportsBaseProjection: supportsBaseProjection);
    }

    private class Base : IDurableObject { internal int Value; }
    private sealed class Derived : Base { internal int Extra; }

    private sealed class TestContext : StateBindingContext {
        private readonly StateModelBinding _model;
        internal Dictionary<string, StateDefinitionBinding> Definitions { get; } = [];
        internal Dictionary<(TypeExpr Type, int Version), DurableSchema> Registered { get; } = [];

        internal TestContext(StateModelBinding model) {
            _model = model;
            AddDefinition(model.CurrentSchema);
        }

        private void AddDefinition(DurableSchema schema) {
            if (schema.BaseSchema is { } ancestor) { AddDefinition(ancestor); }
            foreach (DurableFieldInfo field in schema.Fields) {
                if (field.ValueSchema is { } inline) { AddDefinition(inline); }
            }
            StateSchemaTemplate template = new(schema.SchemaId, schema.Version, schema.Kind, 0,
                schema.Fields.Select(field => new StateFieldTemplate(field.FieldId, NominalType(field), field.ValueSchema?.Version)),
                schema.BaseSchema is { } parent ? new(parent.Type, parent.Version) : null);
            Definitions[schema.SchemaId] = new(schema.SchemaId, schema.Kind, 0, null, [template]);
        }

        public override bool TryGetCurrentModel(Type domainType, out StateModelBinding? model) { model = _model; return true; }
        public override StateValueBinding ResolveCurrentValue(Type domainType) => throw new NotSupportedException();
        public override StateValueBinding ResolveStoredValue(DurableFieldInfo slot) => throw new NotSupportedException();
        public override StateReaderBinding ResolveReader(DurableSchema schema) => throw new NotSupportedException();
        public override TypeExpr GetTypeExpr(Type domainType) => throw new NotSupportedException();
        public override Type GetDomainType(TypeExpr type) => throw new NotSupportedException();
        public override StateDefinitionBinding GetDefinition(string definitionId) => Definitions[definitionId];
        protected override bool TryGetSchema(TypeExpr type, int version, out DurableSchema? schema) => Registered.TryGetValue((type, version), out schema);
    }
}
