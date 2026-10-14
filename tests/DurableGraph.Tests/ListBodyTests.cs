using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed class ListBodyTests {
    public static IEnumerable<object[]> Edits() {
        yield return [new int[] { 1, 2, 3 }, new int[] { 1, 2, 3 }];
        yield return [new int[] { 1, 2, 3 }, new int[] { 1, 2, 3, 0, 4 }];
        yield return [new int[] { 1, 2, 3 }, new int[] { 1 }];
        yield return [new int[] { 1, 2, 3 }, Array.Empty<int>()];
        yield return [Array.Empty<int>(), new int[] { 0, 0 }];
        yield return [new int[] { 1, 2, 3 }, new int[] { 9, 1, 2, 3 }];
        yield return [new int[] { 1, 2, 3 }, new int[] { 1, 8, 2, 3 }];
        yield return [new int[] { 1, 2, 3 }, new int[] { 1, 3 }];
        yield return [new int[] { 1, 2, 3 }, new int[] { 3, 2, 1 }];
    }

    [Theory]
    [MemberData(nameof(Edits))]
    public void PositionalDeltaRoundtripsEditsWithoutMutatingPrior(int[] before, int[] after) {
        ListLayout layout = new(new(1, TypeTag.Int32));
        FrozenListState<int> prior = new(before), current = new(after);
        CountingOps.Preparations = 0;
        PreparedDeltaBody delta = ListStateBody<int, CountingOps>.PrepareDelta(prior, current, layout, ListDeltaAlgorithm.Position);
        Assert.Equal(before.Zip(after).Count(pair => pair.First != pair.Second), CountingOps.Preparations);
        Assert.Equal(!before.SequenceEqual(after), delta.HasChanges);
        BareValueReader reader = new(delta.Body);
        FrozenListState<int> applied = ListStateBody<int, CountingOps>.ApplyDelta(ref reader, prior, layout);
        reader.EnsureFullyConsumed();
        Assert.Equal(after, applied.Elements.ToArray());
        Assert.Equal(before, prior.Elements.ToArray());
        Assert.NotSame(prior, applied);
    }

    [Fact]
    public void GoldenBodyDistinguishesSparseSourcePatchesAndNewRange() {
        ListLayout layout = new(new(1, TypeTag.Int32));
        PreparedBaseBody body = ListStateBody<int, Int32StateOps>.PrepareBase(new(new[] { 1, 2, 3 }), layout);
        Assert.Equal(new byte[] { 3, 2, 4, 6 }, body.Body.ToArray());
        PreparedDeltaBody delta = ListStateBody<int, Int32StateOps>.PrepareDelta(new(new[] { 1, 2, 3 }), new(new[] { 9, 2, 3, 0 }), layout, ListDeltaAlgorithm.Position);
        Assert.Equal(new byte[] { 4, 3, 0, 3, 1, 18, 0, 2, 1, 0 }, delta.Body.ToArray());
        Assert.Equal(new byte[] { 0 }, ListStateBody<int, Int32StateOps>.PrepareDelta(new(new[] { 1 }), new([]), layout).Body.ToArray());
    }

    [Theory]
    [MemberData(nameof(ArrayBodyTests.ScalarArrays), MemberType = typeof(ArrayBodyTests))]
    public void EveryScalarListCapturesAndHydratesWithHistoricalStaticOps(Array values) {
        Type domainElement = values.GetType().GetElementType()!;
        Assert.True(BuiltinStateValues.TryBindCurrent(domainElement, out StateValueBinding element));
        Type listType = typeof(List<>).MakeGenericType(domainElement);
        System.Collections.IList domain = (System.Collections.IList)Activator.CreateInstance(listType)!;
        foreach (object value in values) { domain.Add(value); }
        ListLayout layout = new(element.Slot);
        ListObjectBinding binding = ListObjectBinding.Create(listType, layout, element);
        ObjectStateRecord captured = binding.Capture(new(1), domain, new CaptureSession().BeginCapture());
        IStatePreparationBinding preparation = (IStatePreparationBinding)binding;
        PreparedBaseBody body = preparation.PrepareBase(captured);
        domain.Clear(); // Frozen content does not retain the mutable list.
        ObjectStateRecord decoded = ListStateReader.Create(layout, new(element.Slot, element.StateType, element.StateOpsType))
            .Read(new(1), new Bodies(body.Body.ToArray()));
        System.Collections.IList restored = (System.Collections.IList)binding.Allocate(decoded);
        binding.Hydrate(restored, decoded, new ObjectReadTable(new Dictionary<ObjectId, object>()));
        Assert.Equal(values.Cast<object>(), restored.Cast<object>());
        Assert.False(preparation.PrepareDelta(captured, decoded).HasChanges);
        Assert.Equal(body.Body.ToArray(), preparation.PrepareBase(decoded).Body.ToArray());
    }

    [Theory]
    [InlineData(typeof(InvalidDataException), new byte[] { 3, 3, 0, 3, 1, 18, 1, 16, 0 })] // Duplicate patch.
    [InlineData(typeof(InvalidDataException), new byte[] { 3, 3, 0, 3, 2, 18, 1, 16, 0 })] // Descending patch.
    [InlineData(typeof(InvalidDataException), new byte[] { 3, 3, 0, 3, 4, 18, 0 })] // Patch outside the selected range.
    [InlineData(typeof(InvalidDataException), new byte[] { 2, 1, 2, 2 })] // Source end outside prior.
    [InlineData(typeof(InvalidDataException), new byte[] { 1, 1, 4, 1 })] // Source start outside prior.
    [InlineData(typeof(InvalidDataException), new byte[] { 3, 3, 0, 3, 1, 2, 0 })] // Unchanged child.
    [InlineData(typeof(InvalidDataException), new byte[] { 3, 3, 0, 3, 0 })] // Empty CopyAndPatch.
    [InlineData(typeof(InvalidDataException), new byte[] { 3, 1, 0, 0 })] // Zero source count.
    [InlineData(typeof(InvalidDataException), new byte[] { 1, 2, 0 })] // Zero literal count.
    [InlineData(typeof(InvalidDataException), new byte[] { 1, 1, 0, 2 })] // Output overrun.
    [InlineData(typeof(EndOfStreamException), new byte[] { 3, 3, 0, 3, 1 })]
    [InlineData(typeof(EndOfStreamException), new byte[] { 3, 3, 0, 3, 1, 18 })] // Last Patch still needs its terminator.
    [InlineData(typeof(EndOfStreamException), new byte[] { 3, 1 })]
    [InlineData(typeof(EndOfStreamException), new byte[] { 3, 1, 0, 1 })] // Output underfill.
    [InlineData(typeof(InvalidDataException), new byte[] { 3, 0 })] // No outer zero opcode.
    [InlineData(typeof(InvalidDataException), new byte[] { 3, 4 })]
    [InlineData(typeof(InvalidDataException), new byte[] { 3, 1, 0, 3, 0 })] // Trailing data.
    [InlineData(typeof(InvalidDataException), new byte[] { 1, 2, 1 })] // Missing literal value.
    [InlineData(typeof(InvalidDataException), new byte[] { 255, 255, 255, 255, 15 })]
    public void MalformedDeltaRejectsWithoutChangingPrior(Type exception, byte[] bytes) {
        ListLayout layout = new(new(1, TypeTag.Int32));
        FrozenListState<int> prior = new(new[] { 1, 2, 3 });
        Assert.Throws(exception, () => Apply(bytes, prior, layout));
        Assert.Equal(new[] { 1, 2, 3 }, prior.Elements.ToArray());
        Assert.True(BuiltinStateValues.TryBindCurrent(typeof(int), out StateValueBinding element));
        byte[] body = ListStateBody<int, Int32StateOps>.PrepareBase(prior, layout).Body.ToArray();
        Assert.Throws(exception, () => ListStateReader.Create(layout, element).Read(new(1), new Bodies(body, bytes)));
    }

    [Fact]
    public void Redundant_zero_count_completes_empty_delta_without_operations_or_prior_mutation() {
        ListLayout layout = new(new(1, TypeTag.Int32));
        foreach (int[] values in new[] { Array.Empty<int>(), new[] { 1, 2, 3 } }) {
            FrozenListState<int> prior = new(values);
            BareValueReader reader = new([0x80, 0]);
            FrozenListState<int> result = ListStateBody<int, Int32StateOps>.ApplyDelta(ref reader, prior, layout);
            Assert.Empty(result.Elements.ToArray());
            Assert.Equal(values, prior.Elements.ToArray());
            Assert.True(reader.End);
            Assert.Equal(2, reader.ConsumedCount);
            reader.EnsureFullyConsumed();
        }
    }

    [Fact]
    public void AllTruncationsAndOversizedCountsRejectButZeroByteTailRemainsValid() {
        ListLayout layout = new(new(1, TypeTag.Int32));
        Assert.True(BuiltinStateValues.TryBindCurrent(typeof(int), out StateValueBinding element));
        ListStateReader reader = ListStateReader.Create(layout, element);
        byte[] valid = [3, 2, 4, 6];
        for (int length = 0; length < valid.Length; length++) {
            byte[] truncated = valid[..length];
            Exception? error = Record.Exception(() => reader.Read(new(1), new Bodies(truncated)));
            Assert.True(error is InvalidDataException or EndOfStreamException);
        }
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        writer.WriteVarUInt32(100_000_000);
        Assert.Throws<InvalidDataException>(() => reader.Read(new(1), new Bodies(buffer.WrittenSpan.ToArray())));
        DurableSchema empty = new("list.empty", 1, SchemaKind.InlineValue);
        ListLayout emptyLayout = new(new(1, TypeTag.InlineValue, inlineSchema: empty));
        PreparedDeltaBody delta = ListStateBody<Empty, EmptyOps>.PrepareDelta(new(new Empty[1]), new(new Empty[4]), emptyLayout);
        Assert.True(delta.HasChanges);
        Assert.Equal(new byte[] { 4, 1, 0, 1, 2, 3 }, delta.Body.ToArray());
        BareValueReader input = new(delta.Body);
        Assert.Equal(4, ListStateBody<Empty, EmptyOps>.ApplyDelta(ref input, new(new Empty[1]), emptyLayout).Count);
        input.EnsureFullyConsumed();
        PreparedBaseBody body = ListStateBody<Empty, EmptyOps>.PrepareBase(new(new Empty[4]), emptyLayout);
        Assert.Equal(new byte[] { 4 }, body.Body.ToArray());
        input = new(body.Body);
        Assert.Equal(4, ListStateBody<Empty, EmptyOps>.ReadBase(ref input, emptyLayout).Count);
    }

    [Fact]
    public void FloatingDeltaUsesBitwiseEqualityAndFrozenConstructorOwnsBuffer() {
        ListLayout layout = new(new(1, TypeTag.Single));
        float nan = BitConverter.Int32BitsToSingle(unchecked((int)0x7FC01234));
        float[] values = [-0f, nan];
        FrozenListState<float> current = new(values);
        values[0] = 42;
        PreparedDeltaBody delta = ListStateBody<float, SingleStateOps>.PrepareDelta(new(new[] { 0f, nan }), current, layout);
        Assert.True(delta.HasChanges);
        BareValueReader reader = new(delta.Body);
        FrozenListState<float> applied = ListStateBody<float, SingleStateOps>.ApplyDelta(ref reader, new(new[] { 0f, nan }), layout);
        Assert.Equal(int.MinValue, BitConverter.SingleToInt32Bits(applied[0]));
        Assert.Equal(BitConverter.SingleToInt32Bits(nan), BitConverter.SingleToInt32Bits(applied[1]));
    }

    private static void Apply(byte[] bytes, FrozenListState<int> prior, ListLayout layout) {
        BareValueReader reader = new(bytes);
        ListStateBody<int, Int32StateOps>.ApplyDelta(ref reader, prior, layout);
        reader.EnsureFullyConsumed();
    }
    private sealed class Bodies(params byte[][] bodies) : IStateBodySource {
        public int Count => bodies.Length;
        public ReadOnlySpan<byte> GetBody(int index) => bodies[index];
    }
    private readonly struct CountingOps : IStateOps<int> {
        public static int Preparations;
        public static bool StateEquals(in int left, in int right, DurableFieldInfo slot) => left == right;
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
