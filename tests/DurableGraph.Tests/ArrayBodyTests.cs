using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed class ArrayBodyTests {
    public static IEnumerable<object[]> Arrays() {
        yield return [new[] { 1, 2, 3, 4 }];
        yield return [new[,] { { 1, 2 }, { 3, 4 } }];
        yield return [new[,,] { { { 1, 2 }, { 3, 4 } } }];
        yield return [new[,,,] { { { { 1, 2 }, { 3, 4 } } } }];
        yield return [new int[0]];
        yield return [new int[3, 0]];
        yield return [new int[2, 0, 4]];
        yield return [new int[0, 3, 4, 5]];
        yield return [new int[0, 100_000_000, 2, 3]];
        yield return [new int[100_000_000, 0, 2, 3]];
        yield return [new int[100_000_000, 2, 0, 3]];
        yield return [new int[100_000_000, 2, 3, 0]];
    }

    public static IEnumerable<object[]> ScalarArrays() {
        yield return [new[] { true, false }];
        yield return [new[] { byte.MinValue, byte.MaxValue }];
        yield return [new[] { sbyte.MinValue, sbyte.MaxValue }];
        yield return [new[] { short.MinValue, short.MaxValue }];
        yield return [new[] { ushort.MinValue, ushort.MaxValue }];
        yield return [new[] { int.MinValue, int.MaxValue }];
        yield return [new[] { uint.MinValue, uint.MaxValue }];
        yield return [new[] { long.MinValue, long.MaxValue }];
        yield return [new[] { ulong.MinValue, ulong.MaxValue }];
        yield return [new[] { '\0', '\uD800', char.MaxValue }];
        yield return [new[] { Half.MinValue, Half.MaxValue, (Half)(-0f), Half.NaN }];
        yield return [new[] { float.MinValue, float.MaxValue, -0f, float.NaN }];
        yield return [new[] { double.MinValue, double.MaxValue, -0d, double.NaN }];
    }

    [Theory]
    [MemberData(nameof(ScalarArrays))]
    public void AllScalarElementsRoundtripThroughClosedStaticCapabilities(Array domain) {
        Assert.True(BuiltinStateValues.TryBindCurrent(domain.GetType().GetElementType()!, out StateValueBinding element));
        ArrayLayout layout = new(TypeExprKind.VectorArray, element.Slot);
        ArrayObjectBinding binding = ArrayObjectBinding.Create(domain.GetType(), layout, element);
        ObjectStateRecord captured = binding.Capture(new(1), domain, new CaptureSession().BeginCapture());
        ICapturedStatePreparation preparation = (ICapturedStatePreparation)binding;
        PreparedBaseBody body = preparation.PrepareBase(captured);
        StateValueBinding storedElement = new(element.Slot, element.StateType, element.StateOpsType);
        ObjectStateRecord decoded = ArrayStateReader.Create(layout, storedElement).Read(new(1), new Bodies(body.Body.ToArray()));
        Assert.Equal(body.Body.ToArray(), preparation.PrepareBase(decoded).Body.ToArray());
        Array restored = (Array)binding.Allocate(decoded);
        binding.Hydrate(restored, decoded, new ObjectReadTable(new Dictionary<ObjectId, object>()));
        Assert.Equal(domain.Cast<object>(), restored.Cast<object>());
        Assert.False(preparation.PrepareDelta(captured, decoded).HasChanges);
    }

    [Theory]
    [MemberData(nameof(Arrays))]
    public void AllRanksCaptureDecodeHydrateThroughTypedElements(Array domain) {
        ArrayObjectBinding binding = IntBinding(domain.GetType());
        CaptureContext context = new CaptureSession().BeginCapture();
        ObjectStateRecord captured = binding.Capture(new(1), domain, context);
        ICapturedStatePreparation preparation = (ICapturedStatePreparation)binding;
        PreparedBaseBody body = preparation.PrepareBase(captured);
        ArrayStateReader reader = ArrayStateReader.Create(binding.ArrayLayout, binding.ElementBinding);
        ObjectStateRecord decoded = reader.Read(new(1), new Bodies(body.Body.ToArray()));
        Assert.Equal(domain.Cast<int>(), decoded.GetArrayState<int>().Elements.ToArray());
        Array restored = (Array)binding.Allocate(decoded);
        binding.Hydrate(restored, decoded, new ObjectReadTable(new Dictionary<ObjectId, object>()));
        Assert.Equal(domain.GetType(), restored.GetType());
        Assert.Equal(ArrayShape.FromArray(domain), ArrayShape.FromArray(restored));
        Assert.Equal(domain.Cast<int>(), restored.Cast<int>());
        PreparedDeltaBody unchanged = preparation.PrepareDelta(captured, decoded);
        Assert.False(unchanged.HasChanges);
        Assert.Equal(new byte[] { 0 }, unchanged.Body.ToArray());
    }

    [Fact]
    public void CaptureAndSparseDeltaOwnTheirStatesAndUseEachElementOnce() {
        CountingOps.Preparations = 0;
        DurableFieldInfo slot = new(1, TypeTag.Int32);
        ArrayLayout layout = new(TypeExprKind.VectorArray, slot);
        FrozenArrayState<int> prior = new(new(5), new[] { 1, 2, 3, 4, 5 });
        int[] values = [9, 2, 8, 4, 7];
        FrozenArrayState<int> current = new(new(5), values);
        values[0] = 99;
        PreparedDeltaBody delta = ArrayStateBody<int, CountingOps>.PrepareDelta(prior, current, layout);
        Assert.Equal(5, CountingOps.Preparations);
        Assert.True(delta.HasChanges);
        Assert.Equal(new byte[] { 1, 18, 3, 16, 5, 14, 0 }, delta.Body.ToArray());
        BareValueReader reader = new(delta.Body);
        FrozenArrayState<int> applied = ArrayStateBody<int, CountingOps>.ApplyDelta(ref reader, prior, layout);
        reader.EnsureFullyConsumed();
        Assert.Equal(new[] { 9, 2, 8, 4, 7 }, applied.Elements.ToArray());
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, prior.Elements.ToArray());

        int[] domain = [3, 4];
        ArrayObjectBinding binding = IntBinding(typeof(int[]));
        ObjectStateRecord capture = binding.Capture(new(1), domain, new CaptureSession().BeginCapture());
        domain[0] = 88;
        Assert.Equal(new[] { 3, 4 }, capture.GetArrayState<int>().Elements.ToArray());
    }

    [Theory]
    [InlineData(typeof(InvalidDataException), new byte[] { 1, 18, 1, 16, 0 })] // duplicate
    [InlineData(typeof(InvalidDataException), new byte[] { 2, 18, 1, 16, 0 })] // reversed
    [InlineData(typeof(InvalidDataException), new byte[] { 4, 18, 0 })] // out of range
    [InlineData(typeof(InvalidDataException), new byte[] { 1, 2, 0 })] // unchanged element
    [InlineData(typeof(EndOfStreamException), new byte[] { 1, 18 })] // missing terminator
    [InlineData(typeof(EndOfStreamException), new byte[] { 1 })] // truncated element
    [InlineData(typeof(InvalidDataException), new byte[] { 0, 0 })] // trailing byte
    public void MalformedDeltaFailsWithoutChangingPrior(Type exceptionType, byte[] delta) {
        ArrayObjectBinding binding = IntBinding(typeof(int[]));
        ObjectStateRecord prior = binding.Capture(new(1), new[] { 1, 2, 3 }, new CaptureSession().BeginCapture());
        PreparedBaseBody body = ((ICapturedStatePreparation)binding).PrepareBase(prior);
        ArrayStateReader reader = ArrayStateReader.Create(binding.ArrayLayout, binding.ElementBinding);
        Assert.Throws(exceptionType, () => reader.Read(new(1), new Bodies(body.Body.ToArray(), delta)));
        Assert.Throws(exceptionType, () => ApplyIntDelta(delta, prior.GetArrayState<int>(), binding.ArrayLayout));
        Assert.Equal(new[] { 1, 2, 3 }, prior.GetArrayState<int>().Elements.ToArray());
    }

    private static void ApplyIntDelta(byte[] delta, FrozenArrayState<int> prior, ArrayLayout layout) {
        BareValueReader reader = new(delta);
        _ = ArrayStateBody<int, Int32StateOps>.ApplyDelta(ref reader, prior, layout);
        reader.EnsureFullyConsumed();
    }

    [Fact]
    public void ShapeMismatchCovarianceAndUnsupportedShapesFailClosed() {
        ArrayObjectBinding binding = IntBinding(typeof(int[,]));
        ObjectStateRecord first = binding.Capture(new(1), new int[2, 3], new CaptureSession().BeginCapture());
        ObjectStateRecord second = binding.Capture(new(1), new int[3, 2], new CaptureSession().BeginCapture());
        Assert.Throws<InvalidOperationException>(() => ((ICapturedStatePreparation)binding).PrepareDelta(first, second));
        Assert.Throws<InvalidDataException>(() => ArrayShape.FromArray(Array.CreateInstance(typeof(int), [2], [1])));
        Assert.Throws<InvalidDataException>(() => ArrayShape.FromArray(Array.CreateInstance(typeof(int), [2, 2], [0, 1])));
        Assert.Throws<InvalidDataException>(() => ArrayShape.FromArray(new int[1, 1, 1, 1, 1]));
        Assert.Throws<InvalidDataException>(() => binding.Capture(new(1), new int[6], new CaptureSession().BeginCapture()));
        Assert.Throws<InvalidDataException>(() => new ArrayShape(Array.MaxLength, 2));
        ArrayShape empty = new(Array.MaxLength, Array.MaxLength, 0);
        Assert.Equal(0, empty.Count);
        Assert.Equal(3, empty.Rank);
    }

    [Fact]
    public void InvalidLargeShapeIsRejectedBeforeBackingAllocationButZeroByteStructIsValid() {
        ArrayObjectBinding binding = IntBinding(typeof(int[]));
        ArrayStateReader reader = ArrayStateReader.Create(binding.ArrayLayout, binding.ElementBinding);
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        writer.WriteVarUInt32(100_000_000);
        Assert.Throws<InvalidDataException>(() => reader.Read(new(1), new Bodies(buffer.WrittenSpan.ToArray())));
        buffer.Clear();
        writer.WriteVarUInt32(uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => reader.Read(new(1), new Bodies(buffer.WrittenSpan.ToArray())));

        DurableSchema schema = new("array.empty", 1, SchemaKind.InlineValue);
        DurableFieldInfo slot = new(1, TypeTag.InlineValue, inlineSchema: schema);
        ArrayLayout layout = new(TypeExprKind.VectorArray, slot);
        FrozenArrayState<Empty> state = new(new(3), new Empty[3]);
        PreparedBaseBody body = ArrayStateBody<Empty, EmptyOps>.PrepareBase(state, layout);
        Assert.Equal(new byte[] { 3 }, body.Body.ToArray());
        BareValueReader input = new(body.Body);
        FrozenArrayState<Empty> decoded = ArrayStateBody<Empty, EmptyOps>.ReadBase(ref input, layout);
        input.EnsureFullyConsumed();
        Assert.Equal(3, decoded.Shape.Count);
    }

    [Fact]
    public void FloatingElementDeltaPreservesBitSemantics() {
        ArrayLayout layout = new(TypeExprKind.VectorArray, new(1, TypeTag.Single));
        float nan = BitConverter.Int32BitsToSingle(unchecked((int)0x7FC01234));
        FrozenArrayState<float> prior = new(new(2), new[] { 0f, nan });
        FrozenArrayState<float> current = new(new(2), new[] { -0f, nan });
        PreparedDeltaBody delta = ArrayStateBody<float, SingleStateOps>.PrepareDelta(prior, current, layout);
        Assert.True(delta.HasChanges);
        BareValueReader reader = new(delta.Body);
        FrozenArrayState<float> applied = ArrayStateBody<float, SingleStateOps>.ApplyDelta(ref reader, prior, layout);
        Assert.Equal(int.MinValue, BitConverter.SingleToInt32Bits(applied[0]));
        Assert.Equal(BitConverter.SingleToInt32Bits(nan), BitConverter.SingleToInt32Bits(applied[1]));
        reader.EnsureFullyConsumed();
    }

    private static ArrayObjectBinding IntBinding(Type type) {
        Assert.True(BuiltinStateValues.TryBindCurrent(typeof(int), out StateValueBinding element));
        return ArrayObjectBinding.Create(type, new((TypeExprKind)((int)TypeExprKind.VectorArray + type.GetArrayRank() - 1), element.Slot), element);
    }

    private sealed class Bodies(params byte[][] bodies) : IStateBodySource {
        public int Count => bodies.Length;
        public ReadOnlySpan<byte> GetBody(int index) => bodies[index];
    }

    private readonly struct CountingOps : IStateOps<int> {
        public static bool StateEquals(in int left, in int right, DurableFieldInfo slot) => left == right;
        public static int Preparations;
        public static void WriteBase(ref BareValueWriter writer, in int state, DurableFieldInfo slot) => Int32StateOps.WriteBase(ref writer, in state, slot);
        public static int ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => Int32StateOps.ReadBase(ref reader, slot);
        public static PreparedDeltaBody PrepareDelta(in int prior, in int current, DurableFieldInfo slot) {
            Preparations++;
            return Int32StateOps.PrepareDelta(in prior, in current, slot);
        }
        public static int ApplyDelta(ref BareValueReader reader, in int prior, DurableFieldInfo slot) => Int32StateOps.ApplyDelta(ref reader, in prior, slot);
        public static void VisitReferences(in int state, IStateReferenceVisitor visitor, DurableFieldInfo slot) { }
    }

    private readonly struct Empty { }
    private readonly struct EmptyOps : IStateOps<Empty> {
        public static bool StateEquals(in Empty left, in Empty right, DurableFieldInfo slot) => true;
        public static void WriteBase(ref BareValueWriter writer, in Empty state, DurableFieldInfo slot) { }
        public static Empty ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => default;
        public static PreparedDeltaBody PrepareDelta(in Empty prior, in Empty current, DurableFieldInfo slot) => new(false, []);
        public static Empty ApplyDelta(ref BareValueReader reader, in Empty prior, DurableFieldInfo slot) => throw new InvalidDataException();
        public static void VisitReferences(in Empty state, IStateReferenceVisitor visitor, DurableFieldInfo slot) { }
    }
}
