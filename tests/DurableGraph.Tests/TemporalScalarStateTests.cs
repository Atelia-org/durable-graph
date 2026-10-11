using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed class TemporalScalarStateTests {
    [Theory]
    [InlineData(TypeTag.DateOnly, 1)]
    [InlineData(TypeTag.DateTimeOffset, 2)]
    [InlineData(TypeTag.TimeOnly, 1)]
    public void ScalarMinimumSizeDoesNotChargeAbsentNullableForItsChild(TypeTag tag, int minimum) {
        DurableFieldInfo child = new(1, tag);
        Assert.Equal(minimum, StateBodySize.MinimumBaseBytes(child));
        Assert.Equal(1, StateBodySize.MinimumBaseBytes(DurableFieldInfo.Nullable(1, child)));
    }

    [Fact]
    public void CompactAbsentNullableListsPassActualAllocationPrecheckForAllThreeLeaves() {
        CheckAbsentList<DateOnly, DateOnlyStateOps>(TypeTag.DateOnly);
        CheckAbsentList<DateTimeOffset, DateTimeOffsetStateOps>(TypeTag.DateTimeOffset);
        CheckAbsentList<TimeOnly, TimeOnlyStateOps>(TypeTag.TimeOnly);
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
    public void DateOnlyStaticOperationsPreserveDayNumber() {
        Check<DateOnly, DateOnlyStateOps>(DateOnly.MinValue, DateOnly.FromDayNumber(128), TypeTag.DateOnly, [128, 1]);
    }

    [Fact]
    public void TimeOnlyStaticOperationsPreserveTicks() {
        Check<TimeOnly, TimeOnlyStateOps>(TimeOnly.MinValue, new TimeOnly(128), TypeTag.TimeOnly, [128, 1]);
    }

    [Fact]
    public void OffsetOnlyChangesAreVisibleInScalarAndNullableOperations() {
        DateTimeOffset before = new(TimeSpan.TicksPerHour, TimeSpan.Zero);
        DateTimeOffset after = new(0, TimeSpan.FromHours(-1));
        Assert.Equal(before, after); // CLR equality is instant equality, deliberately weaker than stored state.
        Check<DateTimeOffset, DateTimeOffsetStateOps>(before, after, TypeTag.DateTimeOffset, [0, 119]);
        DurableFieldInfo slot = DurableFieldInfo.Nullable(1, new(1, TypeTag.DateTimeOffset));
        NullableState<DateTimeOffset> prior = new(before), current = new(after);
        PreparedDeltaBody delta = NullableStateOps<DateTimeOffset, DateTimeOffsetStateOps>.PrepareDelta(prior, current, slot);
        Assert.True(delta.HasChanges);
        Assert.Equal(2, delta.Body[0]);
        BareValueReader reader = new(delta.Body);
        Assert.True(current.Value.EqualsExact(NullableStateOps<DateTimeOffset, DateTimeOffsetStateOps>.ApplyDelta(ref reader, prior, slot).Value));
        reader.EnsureFullyConsumed();
    }

    [Fact]
    public void TemporalDictionaryRejectsStandardInstantCollisionButApplicationRetainsDistinctOffsets() {
        DictionaryLayout layout = new(new(1, TypeTag.DateTimeOffset), new(2, TypeTag.Int32));
        DictionaryEntryState<DateTimeOffset, int>[] entries = [new(new DateTimeOffset(TimeSpan.TicksPerHour, TimeSpan.Zero), 10), new(new DateTimeOffset(0, TimeSpan.FromHours(-1)), 20)];
        FrozenDictionaryState<DateTimeOffset, int> invalid = new(DictionaryComparerKind.ScalarDefault, entries);
        Assert.Throws<InvalidDataException>(() => DictionaryStateBody<DateTimeOffset, int, DateTimeOffsetStateOps, Int32StateOps>.PrepareBase(invalid, layout));
        FrozenDictionaryState<DateTimeOffset, int> application = new(DictionaryComparerKind.Application, entries);
        byte[] bytes = DictionaryStateBody<DateTimeOffset, int, DateTimeOffsetStateOps, Int32StateOps>.PrepareBase(application, layout).Body.ToArray();
        BareValueReader reader = new(bytes);
        FrozenDictionaryState<DateTimeOffset, int> restored = DictionaryStateBody<DateTimeOffset, int, DateTimeOffsetStateOps, Int32StateOps>.ReadBase(ref reader, layout);
        reader.EnsureFullyConsumed();
        Assert.True(entries[0].Key.EqualsExact(restored[0].Key));
        Assert.True(entries[1].Key.EqualsExact(restored[1].Key));
        // A corrupt standard body must also reject on read, before a current Dictionary is allocated.
        bytes[0] = (byte)DictionaryComparerKind.ScalarDefault;
        Assert.Throws<InvalidDataException>(() => ReadDictionary(bytes, layout));
    }

    private static void ReadDictionary(byte[] bytes, DictionaryLayout layout) {
        BareValueReader reader = new(bytes);
        DictionaryStateBody<DateTimeOffset, int, DateTimeOffsetStateOps, Int32StateOps>.ReadBase(ref reader, layout);
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
