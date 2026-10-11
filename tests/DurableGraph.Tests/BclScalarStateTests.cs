using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed class BclScalarStateTests {
    [Theory]
    [InlineData(TypeTag.Guid, 16)]
    [InlineData(TypeTag.Decimal, 16)]
    [InlineData(TypeTag.TimeSpan, 1)]
    public void ScalarMinimumSizeDoesNotChargeAbsentNullableForItsChild(TypeTag tag, int minimum) {
        DurableFieldInfo child = new(1, tag);
        Assert.Equal(minimum, StateBodySize.MinimumBaseBytes(child));
        Assert.Equal(1, StateBodySize.MinimumBaseBytes(DurableFieldInfo.Nullable(1, child)));
    }

    [Fact]
    public void CompactAbsentNullableListsPassActualAllocationPrecheckForAllThreeLeaves() {
        CheckAbsentList<Guid, GuidStateOps>(TypeTag.Guid);
        CheckAbsentList<decimal, DecimalStateOps>(TypeTag.Decimal);
        CheckAbsentList<TimeSpan, TimeSpanStateOps>(TypeTag.TimeSpan);
    }

    private static void CheckAbsentList<T, TOps>(TypeTag tag) where T : unmanaged where TOps : IStateOps<T> {
        ListLayout layout = new(DurableFieldInfo.Nullable(1, new(1, tag)));
        BareValueReader reader = new(new byte[] { 2, 0, 0 }); // Two absent elements, no scalar child payload.
        FrozenListState<NullableState<T>> state = ListStateBody<NullableState<T>, NullableStateOps<T, TOps>>.ReadBase(ref reader, layout);
        reader.EnsureFullyConsumed();
        Assert.Equal(2, state.Count);
        Assert.False(state[0].HasValue);
        Assert.False(state[1].HasValue);
    }

    [Fact]
    public void GuidStaticOperationsKeepIndependentNetworkOrderGoldenAndRejectFalseChanges() {
        Guid value = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        Check<Guid, GuidStateOps>(Guid.Empty, value, TypeTag.Guid,
            [0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff]);
    }

    [Fact]
    public void DecimalStaticOperationsTreatScaleAndSignedZeroAsState() {
        Check<decimal, DecimalStateOps>(1.0m, 1.00m, TypeTag.Decimal,
            [100, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0]);
        Check<decimal, DecimalStateOps>(0m, new decimal(0, 0, 0, true, 0), TypeTag.Decimal,
            [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 128]);
        DurableFieldInfo slot = DurableFieldInfo.Nullable(1, new(1, TypeTag.Decimal));
        NullableState<decimal> prior = new(1.0m), current = new(1.00m);
        PreparedDeltaBody delta = NullableStateOps<decimal, DecimalStateOps>.PrepareDelta(prior, current, slot);
        Assert.True(delta.HasChanges);
        Assert.Equal(2, delta.Body[0]); // Patch a present child, preserving its complete decimal representation.
        BareValueReader reader = new(delta.Body);
        Assert.Equal(decimal.GetBits(current.Value), decimal.GetBits(NullableStateOps<decimal, DecimalStateOps>.ApplyDelta(ref reader, prior, slot).Value));
        reader.EnsureFullyConsumed();
    }

    [Fact]
    public void TimeSpanStaticOperationsPreserveTickExtremesAndUseSignedVarints() {
        Check<TimeSpan, TimeSpanStateOps>(TimeSpan.Zero, TimeSpan.FromTicks(-1), TypeTag.TimeSpan, [1]);
        Check<TimeSpan, TimeSpanStateOps>(TimeSpan.Zero, TimeSpan.MaxValue, TypeTag.TimeSpan,
            [254, 255, 255, 255, 255, 255, 255, 255, 255, 1]);
        Check<TimeSpan, TimeSpanStateOps>(TimeSpan.MaxValue, TimeSpan.MinValue, TypeTag.TimeSpan,
            [255, 255, 255, 255, 255, 255, 255, 255, 255, 1]);
    }

    [Fact]
    public void DecimalDictionaryRejectsStandardNumericCollisionButApplicationRetainsDistinctRepresentations() {
        DictionaryLayout layout = new(new(1, TypeTag.Decimal), new(2, TypeTag.Int32));
        DictionaryEntryState<decimal, int>[] entries = [new(1.0m, 10), new(1.00m, 20)];
        FrozenDictionaryState<decimal, int> invalid = new(DictionaryComparerKind.ScalarDefault, entries);
        Assert.Throws<InvalidDataException>(() => DictionaryStateBody<decimal, int, DecimalStateOps, Int32StateOps>.PrepareBase(invalid, layout));
        FrozenDictionaryState<decimal, int> application = new(DictionaryComparerKind.Application, entries);
        byte[] bytes = DictionaryStateBody<decimal, int, DecimalStateOps, Int32StateOps>.PrepareBase(application, layout).Body.ToArray();
        BareValueReader reader = new(bytes);
        FrozenDictionaryState<decimal, int> restored = DictionaryStateBody<decimal, int, DecimalStateOps, Int32StateOps>.ReadBase(ref reader, layout);
        reader.EnsureFullyConsumed();
        Assert.Equal(decimal.GetBits(1.0m), decimal.GetBits(restored[0].Key));
        Assert.Equal(decimal.GetBits(1.00m), decimal.GetBits(restored[1].Key));
        // A corrupt standard body must also reject on read, before a current Dictionary is allocated.
        bytes[0] = (byte)DictionaryComparerKind.ScalarDefault;
        Assert.Throws<InvalidDataException>(() => ReadDictionary(bytes, layout));
    }

    private static void ReadDictionary(byte[] bytes, DictionaryLayout layout) {
        BareValueReader reader = new(bytes);
        DictionaryStateBody<decimal, int, DecimalStateOps, Int32StateOps>.ReadBase(ref reader, layout);
    }

    private static void Check<T, TOps>(T prior, T current, TypeTag tag, byte[] golden)
        where T : unmanaged where TOps : IStateOps<T> {
        DurableFieldInfo slot = new(1, tag);
        ArrayBufferWriter<byte> output = new();
        BareValueWriter writer = new(output);
        TOps.WriteBase(ref writer, current, slot);
        Assert.Equal(golden, output.WrittenSpan.ToArray());
        Assert.False(TOps.StateEquals(prior, current, slot));
        PreparedDeltaBody delta = TOps.PrepareDelta(prior, current, slot);
        Assert.True(delta.HasChanges);
        Assert.Equal(golden, delta.Body.ToArray());
        BareValueReader reader = new(golden.Concat(new byte[] { 91 }).ToArray());
        T decoded = TOps.ReadBase(ref reader, slot);
        Assert.True(TOps.StateEquals(current, decoded, slot));
        Assert.Equal(91, reader.ReadByte());
        reader.EnsureFullyConsumed();
        reader = new(delta.Body);
        Assert.True(TOps.StateEquals(current, TOps.ApplyDelta(ref reader, prior, slot), slot));
        reader.EnsureFullyConsumed();
        Assert.False(TOps.PrepareDelta(current, decoded, slot).HasChanges);
        Assert.Empty(TOps.PrepareDelta(current, decoded, slot).Body.ToArray());
        Assert.Throws<InvalidDataException>(() => Apply<T, TOps>(golden, current, slot));
        Assert.True(BuiltinStateValues.TryBindCurrent(typeof(T), out StateValueBinding binding));
        Assert.True(BuiltinStateValues.TryBindStored(slot, out StateValueBinding historical));
        Assert.Equal(typeof(T), binding.StateType);
        Assert.Equal(typeof(TOps), historical.StateOpsType);
    }

    private static void Apply<T, TOps>(byte[] bytes, T prior, DurableFieldInfo slot)
        where T : unmanaged where TOps : IStateOps<T> {
        BareValueReader reader = new(bytes);
        TOps.ApplyDelta(ref reader, prior, slot);
    }
}
