using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Collections;
using System.Reflection;
using Atelia.Binary;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class RepresentationStoreTests : IDisposable {
    private readonly List<string> _paths = [];

    [Fact]
    public void BuiltinStringNeedsNoFrameAndZeroOrUnknownIdsAreInvalid() {
        string path = NewPath();
        using (GraphResources file = GraphResources.CreateLegacyNew(path)) {
            var store = file.Schemas;
            Assert.Equal(1U, RepresentationId.String.Value);
            Assert.Same(ObjectLayout.String, store.GetRepresentation(RepresentationId.String));
            Assert.Equal(new[] { RepresentationId.String, RepresentationId.String },
                store.RegisterRepresentations([ObjectLayout.String, ObjectLayout.String]));
            Assert.Empty(store.RegisterRepresentations([]));
            Assert.Equal(0, store.Count);
            Assert.Equal(0, CountFrames(file));
            Assert.Throws<InvalidDataException>(() => store.GetRepresentation(default));
            Assert.Throws<InvalidDataException>(() => store.GetRepresentation(new(2)));
            Assert.Equal(typeof(string), store.ResolveReader(RepresentationId.String, new StateReaderRegistry().Snapshot(store)).StateType);
        }
        using (GraphResources file = GraphResources.OpenLegacyReadOnlyExisting(path)) {
            var store = file.Schemas;
            Assert.Same(ObjectLayout.String, store.GetRepresentation(RepresentationId.String));
            Assert.Throws<InvalidOperationException>(() => store.RegisterRepresentations([ObjectLayout.String]));
            Assert.Equal(0, CountFrames(file));
        }
    }

    [Fact]
    public void FullLayoutsRegisterOnceInInputOrderAndReopenWithoutRenumbering() {
        string path = NewPath();
        DurableSchema point = Point(1, TypeTag.Int32);
        DurableSchema ancestor = new("Ancestor", 2, new DurableFieldInfo(1, TypeTag.Int64));
        DurableSchema owner = new(TypeExpr.Named("Pair", TypeExpr.Builtin(TypeTag.Int32), point.Type), 1,
            [new(1, TypeTag.Int32), new(2, TypeTag.InlineValue, inlineSchema: point)], ancestor);
        ObjectLayout durable = ObjectLayout.ForDurable(owner);
        ObjectLayout array = InlineArray(point);
        RepresentationId durableId;
        RepresentationId arrayId;
        using (GraphResources file = GraphResources.CreateLegacyNew(path)) {
            var store = file.Schemas;
            RepresentationId[] ids = store.RegisterRepresentations([durable, ObjectLayout.String, array, durable]);
            (durableId, arrayId) = (ids[0], ids[2]);
            Assert.Equal(new RepresentationId[] { durableId, RepresentationId.String, arrayId, durableId }, ids);
            Assert.Equal(4U, durableId.Value); // Ancestor and inline Point precede the owner.
            Assert.Equal(5U, arrayId.Value);
            Assert.Equal(1, CountFrames(file));
            Assert.Equal(3, store.Count);
            long tail = DataBytes(file.Data);
            ObjectLayout equivalent = ObjectLayout.ForDurable(new(owner.Type, 1,
                [new(1, TypeTag.Int32), new(2, TypeTag.InlineValue, inlineSchema: Point(1, TypeTag.Int32))],
                new("Ancestor", 2, new DurableFieldInfo(1, TypeTag.Int64))));
            Assert.Equal(new RepresentationId[] { arrayId, durableId }, store.RegisterRepresentations([array, equivalent]));
            Assert.Equal(tail, DataBytes(file.Data));
        }
        using (GraphResources file = GraphResources.OpenLegacyExisting(path)) {
            var store = file.Schemas;
            Assert.Equal(1, CountFrames(file));
            Assert.Equal(durable, store.GetRepresentation(durableId));
            Assert.Equal(array, store.GetRepresentation(arrayId));
            Assert.Same(store.GetRequired("Point", 1), store.GetRepresentation(arrayId).Array!.ElementSlot.InlineSchema);
            Assert.Equal(new RepresentationId[] { arrayId, durableId }, store.RegisterRepresentations([array, durable]));
            Assert.Equal(new RepresentationId(6), store.RegisterRepresentations([PrimitiveArray(TypeTag.Int32)])[0]);
        }
    }

    [Fact]
    public void IdentityIncludesNominalArgumentsExactInlineVersionAndRankButNotArrayShape() {
        using GraphResources file = GraphResources.CreateLegacyNew(NewPath());
        var store = file.Schemas;
        ObjectLayout first = ObjectLayout.ForDurable(new("First", 1));
        ObjectLayout second = ObjectLayout.ForDurable(new("Second", 1));
        ObjectLayout phantomInt = ObjectLayout.ForDurable(new(TypeExpr.Named("Phantom", TypeExpr.Builtin(TypeTag.Int32)), 1));
        ObjectLayout phantomString = ObjectLayout.ForDurable(new(TypeExpr.Named("Phantom", TypeExpr.Builtin(TypeTag.String)), 1));
        ObjectLayout point1 = InlineArray(Point(1, TypeTag.Int32));
        ObjectLayout point2 = InlineArray(Point(2, TypeTag.Int32));
        ObjectLayout vector = PrimitiveArray(TypeTag.Int32);
        ObjectLayout matrix = ObjectLayout.ForArray(new(TypeExprKind.Rank2Array, new(1, TypeTag.Int32)));
        RepresentationId[] ids = store.RegisterRepresentations([first, second, phantomInt, phantomString, point1, point2, vector, matrix]);
        Assert.Equal(8, ids.Distinct().Count());
        // Length lives in each object's frozen state; the descriptor is identical for all lengths.
        Assert.Equal(ids[6], store.RegisterRepresentations([PrimitiveArray(TypeTag.Int32)])[0]);
    }

    [Fact]
    public void ReferenceTargetVersionDoesNotChangeOwnerOrOuterArrayRepresentation() {
        using GraphResources file = GraphResources.CreateLegacyNew(NewPath());
        var store = file.Schemas;
        ObjectLayout owner = ObjectLayout.ForDurable(new("Owner", 1, DurableFieldInfo.Reference(1, TypeExpr.Named("Target"))));
        ObjectLayout outer = ObjectLayout.ForArray(new(TypeExprKind.VectorArray,
            DurableFieldInfo.Reference(1, TypeExpr.VectorArray(TypeExpr.Named("Point")))));
        RepresentationId[] original = store.RegisterRepresentations([owner, outer]);
        store.RegisterRepresentations([ObjectLayout.ForDurable(new("Target", 1)), InlineArray(Point(1, TypeTag.Int32))]);
        store.RegisterRepresentations([ObjectLayout.ForDurable(new("Target", 2)), InlineArray(Point(2, TypeTag.Int64))]);
        Assert.Equal(original, store.RegisterRepresentations([owner, outer]));
    }

    [Fact]
    public void ExistingIdCannotHideConflictingCompleteSchemaOrNestedDependency() {
        using GraphResources file = GraphResources.CreateLegacyNew(NewPath());
        var store = file.Schemas;
        DurableSchema point = Point(1, TypeTag.Int32);
        DurableSchema owner = new("Owner", 1, new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: point));
        store.RegisterRepresentations([ObjectLayout.ForDurable(owner), InlineArray(point)]);
        long tail = DataBytes(file.Data);
        Assert.Throws<SchemaConflictException>(() => store.RegisterRepresentations([
            PrimitiveArray(TypeTag.Byte), InlineArray(Point(1, TypeTag.Int64))]));
        Assert.Throws<SchemaConflictException>(() => store.RegisterRepresentations([
            ObjectLayout.ForDurable(new("Owner", 1, new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: Point(1, TypeTag.Int64))))]));
        Assert.Throws<SchemaConflictException>(() => store.RegisterRepresentations([
            ObjectLayout.ForDurable(new("Owner", 1))]));
        Assert.Equal(tail, DataBytes(file.Data));
        Assert.False(store.IsFaulted);
        Assert.Throws<InvalidDataException>(() => store.GetRepresentation(new(5)));
        Assert.Equal(new RepresentationId(5), store.RegisterRepresentations([PrimitiveArray(TypeTag.Byte)])[0]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ArrayReferenceDeclarationConflictsAreCheckedInBothRegistrationOrders(bool referenceFirst) {
        using GraphResources file = GraphResources.CreateLegacyNew(NewPath());
        var store = file.Schemas;
        ObjectLayout reference = ObjectLayout.ForArray(new(TypeExprKind.VectorArray, DurableFieldInfo.Reference(1, TypeExpr.Named("Point"))));
        if (referenceFirst) {
            store.RegisterRepresentations([reference]);
            Assert.Throws<ArgumentException>(() => store.Register(Point(1, TypeTag.Int32)));
        }
        else {
            store.Register(Point(1, TypeTag.Int32));
            Assert.Throws<ArgumentException>(() => store.RegisterRepresentations([reference]));
        }
        Assert.False(store.IsFaulted);
    }

    [Fact]
    public void ArrayNominalArgumentsRemainNonOwningAndValidateArity() {
        using GraphResources file = GraphResources.CreateLegacyNew(NewPath());
        var store = file.Schemas;
        TypeExpr point = TypeExpr.Named("Point");
        ObjectLayout[] layouts = [
            ObjectLayout.ForArray(new(TypeExprKind.VectorArray, DurableFieldInfo.Reference(1, TypeExpr.VectorArray(point)))),
            ObjectLayout.ForArray(new(TypeExprKind.VectorArray, DurableFieldInfo.Reference(1, TypeExpr.Named("Box", point))))];
        store.RegisterRepresentations(layouts);
        Assert.Equal(0, store.Count); // Nominal operands need no exact Schema or current CLR registration.
        store.Register(Point(1, TypeTag.Int32));
        Assert.Throws<ArgumentException>(() => store.Register(new DurableSchema("Box", 1)));
        store.Register(new DurableSchema(TypeExpr.Named("Box", point), 1));
    }

    [Fact]
    public void FullInputFreezeRejectsLateGetterFailureNullAndReentryBeforeWriting() {
        using GraphResources file = GraphResources.CreateLegacyNew(NewPath());
        var store = file.Schemas;
        ObjectLayout layout = ObjectLayout.ForDurable(new("Fresh", 1));
        long tail = DataBytes(file.Data);
        Assert.Throws<IOException>(() => store.RegisterRepresentations(new CallbackList(2, index =>
            index == 0 ? layout : throw new IOException("late input"))));
        Assert.Throws<ArgumentNullException>(() => store.RegisterRepresentations([layout, null!]));
        Assert.Throws<InvalidOperationException>(() => store.RegisterRepresentations(new CallbackList(1, _ => {
            store.GetRepresentation(RepresentationId.String);
            return layout;
        })));
        Assert.Equal(0, store.Count);
        Assert.False(store.IsFaulted);
        Assert.Equal(tail, DataBytes(file.Data));
        Assert.Equal(new RepresentationId(2), store.RegisterRepresentations([layout])[0]);
    }

    [Fact]
    public void ContainerOnlySnapshotReopensWithoutUserSchemasAndRetainsStableIds() {
        string path = NewPath();
        ObjectLayout layout = PrimitiveArray(TypeTag.Int32);
        using (GraphResources file = GraphResources.CreateLegacyNew(path)) {
            Assert.Equal(new RepresentationId(2), file.Schemas.RegisterRepresentations([layout])[0]);
            Assert.Equal(0, file.Schemas.Count);
            Assert.Equal(1, CountFrames(file));
        }
        using (GraphResources file = GraphResources.OpenLegacyReadOnlyExisting(path)) {
            Assert.Equal(0, file.Schemas.Count);
            Assert.Equal(layout, file.Schemas.GetRepresentation(new(2)));
            Assert.Equal(1, CountFrames(file));
        }
        using (GraphResources file = GraphResources.OpenLegacyExisting(path)) {
            Assert.Equal(new RepresentationId(2), file.Schemas.RegisterRepresentations([layout])[0]);
            Assert.Equal(1, CountFrames(file));
            Assert.Equal(new RepresentationId(3), file.Schemas.RegisterRepresentations([PrimitiveArray(TypeTag.Byte)])[0]);
            Assert.Equal(0, file.Schemas.Count);
        }
        using GraphResources reopened = GraphResources.OpenLegacyReadOnlyExisting(path);
        Assert.Equal(layout, reopened.Schemas.GetRepresentation(new(2)));
        Assert.Equal(PrimitiveArray(TypeTag.Byte), reopened.Schemas.GetRepresentation(new(3)));
    }

    [Fact]
    public void ReaderBindingUsesEachSuppliedCatalogAndRetainedCapabilityRatherThanAStoredClrType() {
        string path = NewPath();
        DurableSchema schema = new("Retired", 1, new DurableFieldInfo(1, TypeTag.Int32));
        using (GraphResources file = GraphResources.CreateLegacyNew(path)) {
            file.Schemas.RegisterRepresentations([ObjectLayout.ForDurable(schema), InlineArray(Point(1, TypeTag.Int32))]);
        }
        using (GraphResources file = GraphResources.OpenLegacyReadOnlyExisting(path)) {
            var store = file.Schemas;
            StateReaderRegistry first = new();
            var intReader = new StateReaderBinding<int>(schema,
                static (ref BareValueReader reader) => reader.ReadVarInt32(),
                static (ref BareValueReader reader, in int prior) => reader.ReadVarInt32(),
                static (in int state, IStateReferenceVisitor visitor) => { });
            first.Register(intReader);
            StateReaderRegistry second = new();
            var longReader = new StateReaderBinding<long>(schema,
                static (ref BareValueReader reader) => reader.ReadVarInt32(),
                static (ref BareValueReader reader, in long prior) => reader.ReadVarInt32(),
                static (in long state, IStateReferenceVisitor visitor) => { });
            second.Register(longReader);
            Assert.Same(intReader, store.ResolveReader(new(2), first.Snapshot(store)));
            Assert.Same(longReader, store.ResolveReader(new(2), second.Snapshot(store)));
            Assert.Throws<InvalidDataException>(() => store.ResolveReader(new(2), new StateReaderRegistry().Snapshot(store)));
            second.Register(new StateDefinitionBinding("Point", SchemaKind.InlineValue, 0, null,
                [new("Point", 1, SchemaKind.InlineValue, 0, [new(1, TypeExpr.Builtin(TypeTag.Int32))])],
                historicalValueFactory: static (exact, _) => new(
                    new(1, TypeTag.InlineValue, inlineSchema: exact), typeof(int), typeof(Int32StateOps))));
            StateModelSnapshot snapshot = second.Snapshot(store);
            Assert.Equal(InlineArray(Point(1, TypeTag.Int32)), store.ResolveReader(new(4), snapshot).Layout);
            Assert.Throws<InvalidDataException>(() => snapshot.GetDomainType(TypeExpr.Named("Point")));
        }
    }

    [Fact]
    public void IdExhaustionRejectsWholeBatchWithoutWritingAndExistingIdsRemainAvailable() {
        using GraphResources file = GraphResources.CreateLegacyNew(NewPath());
        var store = file.Schemas;
        RepresentationId old = store.RegisterRepresentations([PrimitiveArray(TypeTag.Int32)])[0];
        DurableSchema existing = new("Existing", 1);
        store.Register(existing);
        RepresentationId existingId = store.RegisterRepresentations([ObjectLayout.ForDurable(existing)])[0];
        // Exhaustion cannot be reached economically in a test. Change only the allocator
        // boundary; assertions concern the public all-or-nothing registration contract.
        typeof(SchemaStore).GetField("_nextRepresentationId", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(store, (ulong)uint.MaxValue + 1);
        long tail = DataBytes(file.Data);
        Assert.Throws<InvalidOperationException>(() => store.RegisterRepresentations([
            ObjectLayout.ForDurable(new("A", 1)), PrimitiveArray(TypeTag.Byte)]));
        Assert.Equal(tail, DataBytes(file.Data));
        Assert.Equal(1, store.Count);
        Assert.False(store.IsFaulted);
        Assert.Same(existing, store.Register(existing));
        Assert.Equal(new[] { existingId, old }, store.RegisterRepresentations([ObjectLayout.ForDurable(existing), PrimitiveArray(TypeTag.Int32)]));
        Assert.Throws<InvalidOperationException>(() => store.Register(new("New", 1)));
        Assert.Throws<InvalidOperationException>(() => store.RegisterRepresentations([PrimitiveArray(TypeTag.Int64)]));
        Assert.Equal(PrimitiveArray(TypeTag.Int32), store.GetRepresentation(old));
        Assert.Equal(tail, DataBytes(file.Data));
        Assert.False(store.IsFaulted);
    }

    private static DurableSchema Point(int version, TypeTag tag) => new("Point", version, SchemaKind.InlineValue, new DurableFieldInfo(1, tag));
    private static ObjectLayout InlineArray(DurableSchema element) => ObjectLayout.ForArray(new(TypeExprKind.VectorArray,
        new(1, TypeTag.InlineValue, inlineSchema: element)));
    private static ObjectLayout PrimitiveArray(TypeTag tag) => ObjectLayout.ForArray(new(TypeExprKind.VectorArray, new(1, tag)));
    private string NewPath() {
        string path = Path.Combine(Path.GetTempPath(), $"durable-representations-{Guid.NewGuid():N}");
        _paths.Add(path);
        return path;
    }
    public void Dispose() { foreach (string path in _paths) { if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); } } }

    private sealed class CallbackList(int count, Func<int, ObjectLayout> get) : IReadOnlyList<ObjectLayout> {
        public int Count => count;
        public ObjectLayout this[int index] => get(index);
        public IEnumerator<ObjectLayout> GetEnumerator() => throw new InvalidOperationException("Inputs must be frozen by index.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static long CountFrames(GraphResources resources) => resources.Data.Inventory(static _ => { }).Unwrap();
}
