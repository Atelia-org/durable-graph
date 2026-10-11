using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using System.Reflection;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed class NullableStateBodyTests {
    private static readonly DurableFieldInfo IntSlot = DurableFieldInfo.Nullable(7, new(1, TypeTag.Int32));

    [Fact]
    public void OptionalRepresentationIsUnmanagedAndDefaultIsCanonicalAbsent() {
        static int Size<T>() where T : unmanaged => System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
        Assert.True(Size<NullableState<int>>() > 0);
        Assert.True(Size<NullableState<InlineId>>() > 0);
        NullableState<int> absent = default;
        Assert.False(absent.HasValue);
        Assert.Equal(0, absent.Value);
        Assert.True(new NullableState<int>(0).HasValue);
    }

    [Fact]
    public void GoldenBaseAndAllDeltaTransitionsAreIndependentOfChildFraming() {
        NullableState<int> absent = default, three = new(3), minusTwo = new(-2);
        Assert.Equal(new byte[] { 0 }, Encode<int, Int32StateOps>(absent, IntSlot));
        Assert.Equal(new byte[] { 1, 6 }, Encode<int, Int32StateOps>(three, IntSlot));
        Assert.Equal(new byte[] { 1, 3 }, Encode<int, Int32StateOps>(minusTwo, IntSlot));
        CheckDelta(absent, three, [1, 6]);
        CheckDelta(three, absent, [0]);
        CheckDelta(three, minusTwo, [2, 3]);
        Assert.False(NullableStateOps<int, Int32StateOps>.PrepareDelta(absent, absent, IntSlot).HasChanges);
        PreparedDeltaBody unchanged = NullableStateOps<int, Int32StateOps>.PrepareDelta(three, three, IntSlot);
        Assert.False(unchanged.HasChanges);
        Assert.Empty(unchanged.Body.ToArray());
    }

    [Fact]
    public void ReadersConsumeOnlyTheirOwnNestedPayload() {
        BareValueReader baseReader = new(new byte[] { 1, 6, 99 });
        Assert.Equal(3, NullableStateOps<int, Int32StateOps>.ReadBase(ref baseReader, IntSlot).Value);
        Assert.Equal(99, baseReader.ReadByte());
        BareValueReader absentReader = new(new byte[] { 0, 99 });
        Assert.False(NullableStateOps<int, Int32StateOps>.ReadBase(ref absentReader, IntSlot).HasValue);
        Assert.Equal(99, absentReader.ReadByte());
        BareValueReader deltaReader = new(new byte[] { 2, 6, 99 });
        Assert.Equal(3, NullableStateOps<int, Int32StateOps>.ApplyDelta(ref deltaReader, new(1), IntSlot).Value);
        Assert.Equal(99, deltaReader.ReadByte());
    }

    [Theory]
    [InlineData(false, new byte[] { 0 })]
    [InlineData(true, new byte[] { 1, 6 })]
    [InlineData(false, new byte[] { 2, 6 })]
    [InlineData(false, new byte[] { 3 })]
    [InlineData(true, new byte[] { 255 })]
    [InlineData(true, new byte[] { 2, 2 })]
    public void IllegalDeltaCodesPriorStatesAndUnchangedChildAreRejected(bool present, byte[] bytes) {
        Assert.Throws<InvalidDataException>(() => Apply<int, Int32StateOps>(bytes, present ? new(1) : default, IntSlot));
    }

    [Theory]
    [InlineData(new byte[] { 2 })]
    [InlineData(new byte[] { 255 })]
    public void UnknownBaseMarkerIsRejected(byte[] bytes) =>
        Assert.Throws<InvalidDataException>(() => Decode<int, Int32StateOps>(bytes, IntSlot));

    [Fact]
    public void TruncatedBaseAndDeltaAreRejected() {
        Assert.Throws<EndOfStreamException>(() => Decode<int, Int32StateOps>([], IntSlot));
        Assert.Throws<EndOfStreamException>(() => Decode<int, Int32StateOps>([1], IntSlot));
        Assert.Throws<EndOfStreamException>(() => Apply<int, Int32StateOps>([], default, IntSlot));
        Assert.Throws<EndOfStreamException>(() => Apply<int, Int32StateOps>([1], default, IntSlot));
        Assert.Throws<EndOfStreamException>(() => Apply<int, Int32StateOps>([2], new(1), IntSlot));
    }

    [Theory]
    [MemberData(nameof(ArrayBodyTests.ScalarArrays), MemberType = typeof(ArrayBodyTests))]
    public void AllThirteenScalarKindsPreserveBitsAndNullTransitions(Array values) {
        Assert.True(BuiltinStateValues.TryBindCurrent(values.GetType().GetElementType()!, out StateValueBinding binding));
        typeof(NullableStateBodyTests).GetMethod(nameof(CheckScalars), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(binding.StateType, binding.StateOpsType).Invoke(null, [values, binding.Slot]);
    }

    private static void CheckScalars<T, TOps>(Array values, DurableFieldInfo child) where T : unmanaged where TOps : IStateOps<T> {
        DurableFieldInfo slot = DurableFieldInfo.Nullable(1, child);
        NullableState<T> prior = default;
        foreach (T value in values) {
            NullableState<T> current = new(value);
            NullableState<T> decoded = Decode<T, TOps>(Encode<T, TOps>(current, slot), slot);
            Assert.True(NullableStateOps<T, TOps>.StateEquals(current, decoded, slot));
            PreparedDeltaBody delta = NullableStateOps<T, TOps>.PrepareDelta(prior, current, slot);
            if (delta.HasChanges) {
                Assert.True(NullableStateOps<T, TOps>.StateEquals(current, Apply<T, TOps>(delta.Body.ToArray(), prior, slot), slot));
            }
            Assert.False(NullableStateOps<T, TOps>.PrepareDelta(current, decoded, slot).HasChanges);
            prior = current;
        }
        Assert.False(Apply<T, TOps>([0], prior, slot).HasValue);
    }

    [Fact]
    public void FloatingPointEqualityIncludesSignedZeroAndNanPayloadBits() {
        DurableFieldInfo slot = DurableFieldInfo.Nullable(1, new(1, TypeTag.Single));
        NullableState<float> positiveZero = new(0f), negativeZero = new(-0f);
        Assert.False(NullableStateOps<float, SingleStateOps>.StateEquals(positiveZero, negativeZero, slot));
        Assert.Equal(new byte[] { 2, 0, 0, 0, 128 }, NullableStateOps<float, SingleStateOps>.PrepareDelta(positiveZero, negativeZero, slot).Body.ToArray());
        NullableState<float> nan1 = new(BitConverter.Int32BitsToSingle(0x7FC00001));
        NullableState<float> nan2 = new(BitConverter.Int32BitsToSingle(0x7FC00002));
        Assert.False(NullableStateOps<float, SingleStateOps>.StateEquals(nan1, nan2, slot));
        Assert.True(NullableStateOps<float, SingleStateOps>.StateEquals(nan1, nan1, slot));
    }

    [Fact]
    public void AbsentSkipsEveryChildOperationAndPresentUsesCanonicalChildSlot() {
        Sentinel.Reset();
        Sentinel.Throw = true;
        NullableState<int> absent = default;
        int? domain = null;
        Assert.False(NullableValueProjection<int, int, Sentinel>.Capture(domain, null!, IntSlot).HasValue);
        int? target = 12;
        NullableValueProjection<int, int, Sentinel>.Hydrate(ref target, absent, null!, IntSlot);
        Assert.Null(target);
        Assert.True(NullableStateOps<int, Sentinel>.StateEquals(absent, absent, IntSlot));
        Assert.False(NullableStateOps<int, Sentinel>.StateEquals(absent, new(1), IntSlot));
        Assert.Equal(new byte[] { 0 }, Encode<int, Sentinel>(absent, IntSlot));
        Assert.False(Decode<int, Sentinel>([0], IntSlot).HasValue);
        Assert.False(NullableStateOps<int, Sentinel>.PrepareDelta(absent, absent, IntSlot).HasChanges);
        Assert.Equal(new byte[] { 0 }, NullableStateOps<int, Sentinel>.PrepareDelta(new(3), absent, IntSlot).Body.ToArray());
        Assert.False(Apply<int, Sentinel>([0], new(3), IntSlot).HasValue);
        NullableStateOps<int, Sentinel>.VisitReferences(absent, null!, IntSlot);
        Assert.Empty(Sentinel.Calls);

        Sentinel.Throw = false;
        domain = 9;
        NullableState<int> captured = NullableValueProjection<int, int, Sentinel>.Capture(domain, null!, IntSlot);
        NullableValueProjection<int, int, Sentinel>.Hydrate(ref target, captured, null!, IntSlot);
        Assert.Equal(domain, target);
        NullableStateOps<int, Sentinel>.VisitReferences(captured, null!, IntSlot);
        Assert.Equal(new[] { "Capture", "Hydrate", "Visit" }, Sentinel.Calls);
    }

    [Fact]
    public void PreparationIsFusedAndExceptionsRemainVisible() {
        Sentinel.Reset();
        PreparedDeltaBody changed = NullableStateOps<int, Sentinel>.PrepareDelta(new(1), new(2), IntSlot);
        Assert.Equal(new[] { "Prepare" }, Sentinel.Calls);
        Assert.Equal(new byte[] { 2, 4 }, changed.Body.ToArray());
        Sentinel.Calls.Clear();
        Assert.False(NullableStateOps<int, Sentinel>.PrepareDelta(new(1), new(1), IntSlot).HasChanges);
        Assert.Equal(new[] { "Prepare" }, Sentinel.Calls);
        Sentinel.Throw = true;
        Assert.Throws<CodecFailure>(() => NullableStateOps<int, Sentinel>.PrepareDelta(new(1), new(2), IntSlot));
        Assert.Throws<CodecFailure>(() => Encode<int, Sentinel>(new(1), IntSlot));
        Assert.Throws<CodecFailure>(() => Decode<int, Sentinel>([1], IntSlot));
        Assert.Throws<CodecFailure>(() => Apply<int, Sentinel>([2], new(1), IntSlot));
        Sentinel.Reset();
    }

    [Fact]
    public void WrapperRejectsChildApplyThatPretendsToChange() {
        Sentinel.Reset();
        Sentinel.UnchangedApply = true;
        Assert.Throws<InvalidDataException>(() => Apply<int, Sentinel>([2], new(1), IntSlot));
        Assert.Equal(new[] { "Apply", "Equals" }, Sentinel.Calls);
        Sentinel.Reset();
    }

    [Fact]
    public void ZeroByteInlineValueStillHasPresenceAndSetSemantics() {
        DurableSchema schema = new("Empty", 1, SchemaKind.InlineValue);
        DurableFieldInfo slot = DurableFieldInfo.Nullable(1, new(1, TypeTag.InlineValue, inlineSchema: schema));
        NullableState<Empty> present = new(default(Empty));
        Assert.Equal(new byte[] { 1 }, Encode<Empty, EmptyOps>(present, slot));
        Assert.True(Decode<Empty, EmptyOps>([1], slot).HasValue);
        Assert.Equal(new byte[] { 1 }, NullableStateOps<Empty, EmptyOps>.PrepareDelta(default, present, slot).Body.ToArray());
        Assert.True(Apply<Empty, EmptyOps>([1], default, slot).HasValue);
        Assert.False(NullableStateOps<Empty, EmptyOps>.PrepareDelta(present, present, slot).HasChanges);
        Assert.Throws<InvalidDataException>(() => Apply<Empty, EmptyOps>([2], present, slot));
    }

    [Fact]
    public void InlineReferenceEdgesExistOnlyWhileTheOptionalValueIsPresent() {
        DurableSchema schema = new("InlineId", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.String));
        DurableFieldInfo slot = DurableFieldInfo.Nullable(1, new(1, TypeTag.InlineValue, inlineSchema: schema));
        EdgeVisitor visitor = new();
        NullableState<InlineId> present = new(new(new(23)));
        NullableStateOps<InlineId, InlineIdOps>.VisitReferences(present, visitor, slot);
        Assert.Equal(new[] { new ObjectId(23) }, visitor.Ids);
        visitor.Ids.Clear();
        NullableState<InlineId> cleared = Apply<InlineId, InlineIdOps>([0], present, slot);
        NullableStateOps<InlineId, InlineIdOps>.VisitReferences(cleared, visitor, slot);
        Assert.Empty(visitor.Ids);
        Assert.Equal(default, cleared.Value.Id);
    }

    [Fact]
    public void EvenAbsentOperationsRejectNonNullableSlot() {
        DurableFieldInfo slot = new(1, TypeTag.Int32);
        Assert.Throws<ArgumentException>(() => Encode<int, Int32StateOps>(default, slot));
        Assert.Throws<ArgumentException>(() => Decode<int, Int32StateOps>([0], slot));
        Assert.Throws<ArgumentException>(() => Apply<int, Int32StateOps>([0], default, slot));
        Assert.Throws<ArgumentException>(() => NullableStateOps<int, Int32StateOps>.PrepareDelta(default, default, slot));
        Assert.Throws<ArgumentException>(() => NullableStateOps<int, Int32StateOps>.StateEquals(default, default, slot));
        Assert.Throws<ArgumentException>(() => NullableStateOps<int, Int32StateOps>.VisitReferences(default, null!, slot));
        Assert.Throws<ArgumentException>(() => NullableValueProjection<int, int, Sentinel>.Capture(null, null!, slot));
        Assert.Throws<ArgumentException>(() => {
            int? target = null;
            NullableValueProjection<int, int, Sentinel>.Hydrate(ref target, default, null!, slot);
        });
    }

    private static void CheckDelta(NullableState<int> prior, NullableState<int> current, byte[] expected) {
        PreparedDeltaBody delta = NullableStateOps<int, Int32StateOps>.PrepareDelta(prior, current, IntSlot);
        Assert.True(delta.HasChanges);
        Assert.Equal(expected, delta.Body.ToArray());
        Assert.True(NullableStateOps<int, Int32StateOps>.StateEquals(current, Apply<int, Int32StateOps>(expected, prior, IntSlot), IntSlot));
    }

    private static byte[] Encode<T, TOps>(NullableState<T> state, DurableFieldInfo slot) where T : unmanaged where TOps : IStateOps<T> {
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        NullableStateOps<T, TOps>.WriteBase(ref writer, state, slot);
        return buffer.WrittenSpan.ToArray();
    }

    private static NullableState<T> Decode<T, TOps>(byte[] bytes, DurableFieldInfo slot) where T : unmanaged where TOps : IStateOps<T> {
        BareValueReader reader = new(bytes);
        NullableState<T> result = NullableStateOps<T, TOps>.ReadBase(ref reader, slot);
        reader.EnsureFullyConsumed();
        return result;
    }

    private static NullableState<T> Apply<T, TOps>(byte[] bytes, NullableState<T> prior, DurableFieldInfo slot) where T : unmanaged where TOps : IStateOps<T> {
        BareValueReader reader = new(bytes);
        NullableState<T> result = NullableStateOps<T, TOps>.ApplyDelta(ref reader, prior, slot);
        reader.EnsureFullyConsumed();
        return result;
    }

    private readonly record struct InlineId(ObjectId Id);
    private sealed class CodecFailure : Exception { }
    private readonly struct Sentinel : IStateOps<int>, IValueProjection<int, int> {
        public static readonly List<string> Calls = [];
        public static bool Throw, UnchangedApply;
        public static void Reset() { Calls.Clear(); Throw = UnchangedApply = false; }
        private static void Call(string operation, DurableFieldInfo slot) {
            Assert.Equal(new DurableFieldInfo(1, TypeTag.Int32), slot);
            Calls.Add(operation);
            if (Throw) { throw new CodecFailure(); }
        }
        public static int Capture(in int value, CaptureContext context, DurableFieldInfo slot) { Call("Capture", slot); return value; }
        public static void Hydrate(ref int target, in int state, ObjectReadTable objects, DurableFieldInfo slot) { Call("Hydrate", slot); target = state; }
        public static bool StateEquals(in int left, in int right, DurableFieldInfo slot) { Call("Equals", slot); return left == right; }
        public static void WriteBase(ref BareValueWriter writer, in int state, DurableFieldInfo slot) { Call("Write", slot); writer.WriteVarInt32(state); }
        public static int ReadBase(ref BareValueReader reader, DurableFieldInfo slot) { Call("Read", slot); return reader.ReadVarInt32(); }
        public static PreparedDeltaBody PrepareDelta(in int prior, in int current, DurableFieldInfo slot) { Call("Prepare", slot); return Int32StateOps.PrepareDelta(prior, current, slot); }
        public static int ApplyDelta(ref BareValueReader reader, in int prior, DurableFieldInfo slot) { Call("Apply", slot); return UnchangedApply ? prior : reader.ReadVarInt32(); }
        public static void VisitReferences(in int state, IStateReferenceVisitor visitor, DurableFieldInfo slot) => Call("Visit", slot);
    }

    private sealed class EdgeVisitor : IStateReferenceVisitor {
        public List<ObjectId> Ids { get; } = [];
        public void VisitString(ObjectId id) => Ids.Add(id);
        public void VisitDurable(ObjectId id, string nominalSchemaId) => throw new InvalidOperationException();
    }

    private readonly struct InlineIdOps : IStateOps<InlineId> {
        public static bool StateEquals(in InlineId left, in InlineId right, DurableFieldInfo slot) => left == right;
        public static void WriteBase(ref BareValueWriter writer, in InlineId state, DurableFieldInfo slot) => writer.WriteVarUInt32(state.Id.Value);
        public static InlineId ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => new(new(reader.ReadVarUInt32()));
        public static PreparedDeltaBody PrepareDelta(in InlineId prior, in InlineId current, DurableFieldInfo slot) =>
            StringIdStateOps.PrepareDelta(prior.Id, current.Id, slot.InlineSchema!.Fields[0]);
        public static InlineId ApplyDelta(ref BareValueReader reader, in InlineId prior, DurableFieldInfo slot) =>
            new(StringIdStateOps.ApplyDelta(ref reader, prior.Id, slot.InlineSchema!.Fields[0]));
        public static void VisitReferences(in InlineId state, IStateReferenceVisitor visitor, DurableFieldInfo slot) => visitor.VisitString(state.Id);
    }

    private readonly struct Empty { }
    private readonly struct EmptyOps : IStateOps<Empty> {
        public static bool StateEquals(in Empty left, in Empty right, DurableFieldInfo slot) => true;
        public static void WriteBase(ref BareValueWriter writer, in Empty state, DurableFieldInfo slot) { }
        public static Empty ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => default;
        public static PreparedDeltaBody PrepareDelta(in Empty prior, in Empty current, DurableFieldInfo slot) => new(false, []);
        public static Empty ApplyDelta(ref BareValueReader reader, in Empty prior, DurableFieldInfo slot) => default;
        public static void VisitReferences(in Empty state, IStateReferenceVisitor visitor, DurableFieldInfo slot) { }
    }
}
