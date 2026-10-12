using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Collections;
using System.Reflection;
using Atelia.Data;
using Atelia.DurableGraph.Serialization;
using Atelia.Rbf;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class RepresentationStoreTests : IDisposable {
    private readonly List<string> _paths = [];

    [Fact]
    public void BuiltinStringNeedsNoFrameAndZeroOrUnknownIdsAreInvalid() {
        string path = NewPath();
        using (IRbfFile file = RbfFile.CreateNew(path)) {
            var recording = new RecordingFile(file);
            var store = new SchemaStore(recording);
            Assert.Equal(1U, RepresentationId.String.Value);
            Assert.Same(ObjectLayout.String, store.GetRepresentation(RepresentationId.String));
            Assert.Equal(new[] { RepresentationId.String, RepresentationId.String },
                store.RegisterRepresentations([ObjectLayout.String, ObjectLayout.String]));
            Assert.Empty(store.RegisterRepresentations([]));
            Assert.Equal(0, store.Count);
            Assert.Empty(recording.Events);
            Assert.Throws<InvalidDataException>(() => store.GetRepresentation(default));
            Assert.Throws<InvalidDataException>(() => store.GetRepresentation(new(2)));
            Assert.Equal(typeof(string), store.ResolveReader(RepresentationId.String, new StateReaderRegistry().Snapshot(store)).StateType);
        }
        using (IRbfFile file = RbfFile.OpenReadOnlyExisting(path)) {
            var recording = new RecordingFile(file);
            var store = new SchemaStore(recording, readOnly: true);
            Assert.Same(ObjectLayout.String, store.GetRepresentation(RepresentationId.String));
            Assert.Throws<InvalidOperationException>(() => store.RegisterRepresentations([ObjectLayout.String]));
            Assert.Empty(recording.Events);
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
        using (IRbfFile file = RbfFile.CreateNew(path)) {
            var recording = new RecordingFile(file);
            var store = new SchemaStore(recording);
            RepresentationId[] ids = store.RegisterRepresentations([durable, ObjectLayout.String, array, durable]);
            (durableId, arrayId) = (ids[0], ids[2]);
            Assert.Equal(new RepresentationId[] { durableId, RepresentationId.String, arrayId, durableId }, ids);
            Assert.Equal(4U, durableId.Value); // Ancestor and inline Point precede the owner.
            Assert.Equal(5U, arrayId.Value);
            Assert.Equal(new[] { "append-catalog", "flush" }, recording.Events);
            Assert.Equal(3, store.Count);
            long tail = file.TailOffset;
            ObjectLayout equivalent = ObjectLayout.ForDurable(new(owner.Type, 1,
                [new(1, TypeTag.Int32), new(2, TypeTag.InlineValue, inlineSchema: Point(1, TypeTag.Int32))],
                new("Ancestor", 2, new DurableFieldInfo(1, TypeTag.Int64))));
            Assert.Equal(new RepresentationId[] { arrayId, durableId }, store.RegisterRepresentations([array, equivalent]));
            Assert.Equal(tail, file.TailOffset);
        }
        using (IRbfFile file = RbfFile.OpenExisting(path)) {
            var recording = new RecordingFile(file);
            var store = new SchemaStore(recording);
            Assert.Equal(new[] { "flush" }, recording.Events);
            Assert.Equal(durable, store.GetRepresentation(durableId));
            Assert.Equal(array, store.GetRepresentation(arrayId));
            Assert.Same(store.GetRequired("Point", 1), store.GetRepresentation(arrayId).Array!.ElementSlot.InlineSchema);
            Assert.Equal(new RepresentationId[] { arrayId, durableId }, store.RegisterRepresentations([array, durable]));
            Assert.Equal(new RepresentationId(6), store.RegisterRepresentations([PrimitiveArray(TypeTag.Int32)])[0]);
        }
    }

    [Fact]
    public void IdentityIncludesNominalArgumentsExactInlineVersionAndRankButNotArrayShape() {
        using IRbfFile file = RbfFile.CreateNew(NewPath());
        var store = new SchemaStore(file);
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
        using IRbfFile file = RbfFile.CreateNew(NewPath());
        var store = new SchemaStore(file);
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
        using IRbfFile file = RbfFile.CreateNew(NewPath());
        var store = new SchemaStore(file);
        DurableSchema point = Point(1, TypeTag.Int32);
        DurableSchema owner = new("Owner", 1, new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: point));
        store.RegisterRepresentations([ObjectLayout.ForDurable(owner), InlineArray(point)]);
        long tail = file.TailOffset;
        Assert.Throws<SchemaConflictException>(() => store.RegisterRepresentations([
            PrimitiveArray(TypeTag.Byte), InlineArray(Point(1, TypeTag.Int64))]));
        Assert.Throws<SchemaConflictException>(() => store.RegisterRepresentations([
            ObjectLayout.ForDurable(new("Owner", 1, new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: Point(1, TypeTag.Int64))))]));
        Assert.Throws<SchemaConflictException>(() => store.RegisterRepresentations([
            ObjectLayout.ForDurable(new("Owner", 1))]));
        Assert.Equal(tail, file.TailOffset);
        Assert.False(store.IsFaulted);
        Assert.Throws<InvalidDataException>(() => store.GetRepresentation(new(5)));
        Assert.Equal(new RepresentationId(5), store.RegisterRepresentations([PrimitiveArray(TypeTag.Byte)])[0]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ArrayReferenceDeclarationConflictsAreCheckedInBothRegistrationOrders(bool referenceFirst) {
        using IRbfFile file = RbfFile.CreateNew(NewPath());
        var store = new SchemaStore(file);
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
        using IRbfFile file = RbfFile.CreateNew(NewPath());
        var store = new SchemaStore(file);
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
        using IRbfFile file = RbfFile.CreateNew(NewPath());
        var store = new SchemaStore(file);
        ObjectLayout layout = ObjectLayout.ForDurable(new("Fresh", 1));
        long tail = file.TailOffset;
        Assert.Throws<IOException>(() => store.RegisterRepresentations(new CallbackList(2, index =>
            index == 0 ? layout : throw new IOException("late input"))));
        Assert.Throws<ArgumentNullException>(() => store.RegisterRepresentations([layout, null!]));
        Assert.Throws<InvalidOperationException>(() => store.RegisterRepresentations(new CallbackList(1, _ => {
            store.GetRepresentation(RepresentationId.String);
            return layout;
        })));
        Assert.Equal(0, store.Count);
        Assert.False(store.IsFaulted);
        Assert.Equal(tail, file.TailOffset);
        Assert.Equal(new RepresentationId(2), store.RegisterRepresentations([layout])[0]);
    }

    [Fact]
    public void RawWriteDuringInputFreezeFaultsBeforeAnyOwnedAppend() {
        using IRbfFile file = RbfFile.CreateNew(NewPath());
        var recording = new RecordingFile(file);
        var store = new SchemaStore(recording);
        Assert.Throws<InvalidOperationException>(() => store.RegisterRepresentations(new CallbackList(1, _ => {
            file.Append(SchemaCatalogWireCodec.RbfTag, CatalogTestData.Encode(CatalogTestData.Schemas([new DurableSchema("External", 1)]))).Unwrap();
            return PrimitiveArray(TypeTag.Int32);
        })));
        Assert.True(store.IsFaulted);
        Assert.Empty(recording.Events);
        var reopened = new SchemaStore(file);
        Assert.Equal(new RepresentationId(3), reopened.RegisterRepresentations([PrimitiveArray(TypeTag.Int32)])[0]);
    }

    [Theory]
    [InlineData(FailurePoint.BeforeAppend, false)]
    [InlineData(FailurePoint.AfterAppend, true)]
    [InlineData(FailurePoint.BeforeFlush, true)]
    [InlineData(FailurePoint.AfterFlush, true)]
    public void UncertainUnifiedCatalogWriteFaultsUntilColdOpen(FailurePoint point, bool batchExists) {
        string path = NewPath();
        ObjectLayout layout = ObjectLayout.ForDurable(new("A", 1));
        using (IRbfFile file = RbfFile.CreateNew(path)) {
            var recording = new RecordingFile(file) { Failure = point };
            var store = new SchemaStore(recording);
            Assert.Throws<IOException>(() => store.RegisterRepresentations([layout]));
            Assert.True(store.IsFaulted);
            Assert.Throws<InvalidOperationException>(() => store.GetRepresentation(RepresentationId.String));
            Assert.Throws<InvalidOperationException>(() => store.RegisterRepresentations([]));
            Assert.Throws<InvalidOperationException>(() => store.GetRequired("A", 1));
        }
        // This observes complete bytes after a process-local reopen; it is not a power-loss simulation.
        using (IRbfFile file = RbfFile.OpenExisting(path)) {
            var store = new SchemaStore(file);
            Assert.Equal(batchExists ? 1 : 0, store.Count);
            if (batchExists) { Assert.Equal(layout, store.GetRepresentation(new(2))); }
            else { Assert.Throws<InvalidDataException>(() => store.GetRepresentation(new(2))); }
            Assert.Equal(new RepresentationId(2), store.RegisterRepresentations([layout])[0]);
        }
    }

    [Fact]
    public void ArrayOnlyLogReopensWithDurableBarrierEvenWithoutUserSchemas() {
        string path = NewPath();
        ObjectLayout layout = PrimitiveArray(TypeTag.Int32);
        using (IRbfFile file = RbfFile.CreateNew(path)) {
            var recording = new RecordingFile(file) { Failure = FailurePoint.AfterAppend };
            var store = new SchemaStore(recording);
            Assert.Throws<IOException>(() => store.RegisterRepresentations([layout]));
            Assert.Equal(new[] { "append-catalog" }, recording.Events);
        }
        using (IRbfFile file = RbfFile.OpenReadOnlyExisting(path)) {
            var recording = new RecordingFile(file);
            var store = new SchemaStore(recording, readOnly: true);
            Assert.Equal(0, store.Count);
            Assert.Equal(layout, store.GetRepresentation(new(2)));
            Assert.Empty(recording.Events);
        }
        using (IRbfFile file = RbfFile.OpenExisting(path)) {
            var recording = new RecordingFile(file);
            var store = new SchemaStore(recording);
            Assert.Equal(0, store.Count);
            Assert.Equal(new[] { "flush" }, recording.Events);
            store.RegisterRepresentations([layout]);
            Assert.Equal(new[] { "flush" }, recording.Events);
        }
        using (IRbfFile file = RbfFile.OpenExisting(path)) {
            var recording = new RecordingFile(file) { Failure = FailurePoint.BeforeFlush, FailureAppendNumber = 0 };
            Assert.Throws<IOException>(() => new SchemaStore(recording));
        }
    }

    [Fact]
    public void ReaderBindingUsesEachSuppliedCatalogAndRetainedCapabilityRatherThanAStoredClrType() {
        string path = NewPath();
        DurableSchema schema = new("Retired", 1, new DurableFieldInfo(1, TypeTag.Int32));
        using (IRbfFile file = RbfFile.CreateNew(path)) {
            new SchemaStore(file).RegisterRepresentations([ObjectLayout.ForDurable(schema), InlineArray(Point(1, TypeTag.Int32))]);
        }
        using (IRbfFile file = RbfFile.OpenReadOnlyExisting(path)) {
            var store = new SchemaStore(file, readOnly: true);
            StateReaderRegistry first = new();
            var intReader = new StateReaderBinding<int>(schema,
                static (ref BinaryPayloadReader reader) => reader.ReadInt32(),
                static (ref BinaryPayloadReader reader, in int prior) => reader.ReadInt32(),
                static (in int state, IStateReferenceVisitor visitor) => { });
            first.Register(intReader);
            StateReaderRegistry second = new();
            var longReader = new StateReaderBinding<long>(schema,
                static (ref BinaryPayloadReader reader) => reader.ReadInt32(),
                static (ref BinaryPayloadReader reader, in long prior) => reader.ReadInt32(),
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
        using IRbfFile file = RbfFile.CreateNew(NewPath());
        var store = new SchemaStore(file);
        RepresentationId old = store.RegisterRepresentations([PrimitiveArray(TypeTag.Int32)])[0];
        DurableSchema existing = new("Existing", 1);
        store.Register(existing);
        RepresentationId existingId = store.RegisterRepresentations([ObjectLayout.ForDurable(existing)])[0];
        // Exhaustion cannot be reached economically in a test. Change only the allocator
        // boundary; assertions concern the public all-or-nothing registration contract.
        typeof(SchemaStore).GetField("_nextRepresentationId", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(store, (ulong)uint.MaxValue + 1);
        long tail = file.TailOffset;
        Assert.Throws<InvalidOperationException>(() => store.RegisterRepresentations([
            ObjectLayout.ForDurable(new("A", 1)), PrimitiveArray(TypeTag.Byte)]));
        Assert.Equal(tail, file.TailOffset);
        Assert.Equal(1, store.Count);
        Assert.False(store.IsFaulted);
        Assert.Same(existing, store.Register(existing));
        Assert.Equal(new[] { existingId, old }, store.RegisterRepresentations([ObjectLayout.ForDurable(existing), PrimitiveArray(TypeTag.Int32)]));
        Assert.Throws<InvalidOperationException>(() => store.Register(new("New", 1)));
        Assert.Throws<InvalidOperationException>(() => store.RegisterRepresentations([PrimitiveArray(TypeTag.Int64)]));
        Assert.Equal(PrimitiveArray(TypeTag.Int32), store.GetRepresentation(old));
        Assert.Equal(tail, file.TailOffset);
        Assert.False(store.IsFaulted);
    }

    private static DurableSchema Point(int version, TypeTag tag) => new("Point", version, SchemaKind.InlineValue, new DurableFieldInfo(1, tag));
    private static ObjectLayout InlineArray(DurableSchema element) => ObjectLayout.ForArray(new(TypeExprKind.VectorArray,
        new(1, TypeTag.InlineValue, inlineSchema: element)));
    private static ObjectLayout PrimitiveArray(TypeTag tag) => ObjectLayout.ForArray(new(TypeExprKind.VectorArray, new(1, tag)));
    private string NewPath() {
        string path = Path.Combine(Path.GetTempPath(), $"durable-representations-{Guid.NewGuid():N}.rbf");
        _paths.Add(path);
        return path;
    }
    public void Dispose() { foreach (string path in _paths) { File.Delete(path); } }

    private sealed class CallbackList(int count, Func<int, ObjectLayout> get) : IReadOnlyList<ObjectLayout> {
        public int Count => count;
        public ObjectLayout this[int index] => get(index);
        public IEnumerator<ObjectLayout> GetEnumerator() => throw new InvalidOperationException("Inputs must be frozen by index.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public enum FailurePoint { None, BeforeAppend, AfterAppend, BeforeFlush, AfterFlush }
    private sealed class RecordingFile(IRbfFile inner) : IRbfFile {
        public List<string> Events { get; } = [];
        public FailurePoint Failure { get; init; }
        public int FailureAppendNumber { get; init; } = 1;
        private int _appends;
        public long TailOffset => inner.TailOffset;
        private void Fail(FailurePoint point) {
            if (Failure == point && _appends == FailureAppendNumber) { throw new IOException($"Injected {point} on append {_appends}."); }
        }
        public AteliaResult<SizedPtr> Append(uint tag, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> tailMeta = default) {
            _appends++;
            Assert.Equal(SchemaCatalogWireCodec.RbfTag, tag);
            Events.Add("append-catalog");
            Fail(FailurePoint.BeforeAppend);
            var result = inner.Append(tag, payload, tailMeta);
            Fail(FailurePoint.AfterAppend);
            return result;
        }
        public void DurableFlush() {
            Events.Add("flush");
            Fail(FailurePoint.BeforeFlush);
            inner.DurableFlush();
            Fail(FailurePoint.AfterFlush);
        }
        public RbfFrameBuilder BeginAppend() => inner.BeginAppend();
        public AteliaResult<RbfPooledFrame> ReadPooledFrame(SizedPtr ptr) => inner.ReadPooledFrame(ptr);
        public AteliaResult<RbfFrame> ReadFrame(SizedPtr ptr, Span<byte> buffer) => inner.ReadFrame(ptr, buffer);
        public RbfReverseSequence ScanReverse(bool showTombstone = false) => inner.ScanReverse(showTombstone);
        public RbfForwardSequence ScanForward(bool showTombstone = false) => inner.ScanForward(showTombstone);
        public AteliaResult<RbfScanBoundary> GetScanBoundaryAfter(SizedPtr ticket) => inner.GetScanBoundaryAfter(ticket);
        public AteliaResult<RbfForwardSequence> ScanForward(RbfScanBoundary boundary, bool showTombstone = false) => inner.ScanForward(boundary, showTombstone);
        public long GetPhysicalOffsetImmediatelyAfter(SizedPtr ticket) => inner.GetPhysicalOffsetImmediatelyAfter(ticket);
        public AteliaResult<OptionalRbfFrameInfo> ReadFrameInfoImmediatelyAfter(SizedPtr ticket) => inner.ReadFrameInfoImmediatelyAfter(ticket);
        public AteliaResult<RbfFrameInfo> ReadFrameInfo(SizedPtr ticket) => inner.ReadFrameInfo(ticket);
        public AteliaResult<RbfTailMeta> ReadTailMeta(SizedPtr ticket, Span<byte> buffer) => inner.ReadTailMeta(ticket, buffer);
        public AteliaResult<RbfPooledTailMeta> ReadPooledTailMeta(SizedPtr ticket) => inner.ReadPooledTailMeta(ticket);
        public void Truncate(long newLengthBytes) => inner.Truncate(newLengthBytes);
        public void SetupReadLog(string? logPath) => inner.SetupReadLog(logPath);
        public void Dispose() => inner.Dispose();
    }
}
