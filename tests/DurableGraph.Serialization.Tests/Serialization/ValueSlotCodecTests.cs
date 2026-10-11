using Atelia.Binary;
using System.Buffers;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Serialization.Tests;

public sealed class ValueSlotCodecTests {
    [Fact]
    public void Integral_slots_preserve_the_existing_primitive_wire_contract() {
        AssertGolden(false, [0x00]);
        AssertGolden(true, [0x01]);
        AssertGolden(byte.MinValue, [0x00]);
        AssertGolden(byte.MaxValue, [0xff]);
        AssertGolden(sbyte.MinValue, [0x80]);
        AssertGolden(sbyte.MaxValue, [0x7f]);
        AssertGolden(short.MinValue, [0xff, 0xff, 0x03]);
        AssertGolden(short.MaxValue, [0xfe, 0xff, 0x03]);
        AssertGolden(ushort.MinValue, [0x00]);
        AssertGolden(ushort.MaxValue, [0xff, 0xff, 0x03]);
        AssertGolden(int.MinValue, [0xff, 0xff, 0xff, 0xff, 0x0f]);
        AssertGolden(int.MaxValue, [0xfe, 0xff, 0xff, 0xff, 0x0f]);
        AssertGolden(uint.MinValue, [0x00]);
        AssertGolden(uint.MaxValue, [0xff, 0xff, 0xff, 0xff, 0x0f]);
        AssertGolden(long.MinValue, [0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x01]);
        AssertGolden(long.MaxValue, [0xfe, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x01]);
        AssertGolden(ulong.MinValue, [0x00]);
        AssertGolden(ulong.MaxValue, [0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x01]);
    }

    [Fact]
    public void Char_slot_preserves_code_units_including_unpaired_surrogates() {
        AssertGolden('\0', [0x00]);
        AssertGolden('\uffff', [0xff, 0xff, 0x03]);
        AssertGolden('\ud800', [0x80, 0xb0, 0x03]);
        AssertGolden('\udfff', [0xff, 0xbf, 0x03]);
    }

    [Fact]
    public void Floating_slots_preserve_negative_zero_infinity_and_nan_payload_bits() {
        foreach (ushort bits in new ushort[] { 0x8000, 0x7c00, 0xfe01 }) {
            Half value = BitConverter.UInt16BitsToHalf(bits);
            Half restored = AssertGolden(value, [(byte)bits, (byte)(bits >> 8)]);
            Assert.Equal(bits, BitConverter.HalfToUInt16Bits(restored));
        }

        foreach (uint bits in new uint[] { 0x80000000, 0x7f800000, 0xffc12345 }) {
            float value = BitConverter.UInt32BitsToSingle(bits);
            float restored = AssertGolden(value, [
                (byte)bits, (byte)(bits >> 8), (byte)(bits >> 16), (byte)(bits >> 24),
            ]);
            Assert.Equal(bits, BitConverter.SingleToUInt32Bits(restored));
        }

        foreach (ulong bits in new ulong[] { 0x8000000000000000, 0x7ff0000000000000, 0xfff8123456789abc }) {
            double value = BitConverter.UInt64BitsToDouble(bits);
            double restored = AssertGolden(value, [
                (byte)bits, (byte)(bits >> 8), (byte)(bits >> 16), (byte)(bits >> 24),
                (byte)(bits >> 32), (byte)(bits >> 40), (byte)(bits >> 48), (byte)(bits >> 56),
            ]);
            Assert.Equal(bits, BitConverter.DoubleToUInt64Bits(restored));
        }
    }

    [Theory]
    [InlineData(typeof(string))]
    [InlineData(typeof(object))]
    [InlineData(typeof(decimal))]
    [InlineData(typeof(nint))]
    [InlineData(typeof(nuint))]
    [InlineData(typeof(DayOfWeek))]
    [InlineData(typeof(int?))]
    [InlineData(typeof(int[]))]
    [InlineData(typeof(DateTime))]
    [InlineData(typeof(ValueTuple<int>))]
    public void Runtime_lookup_rejects_types_outside_the_explicit_primitive_list(Type type) {
        Assert.Throws<NotSupportedException>(() => PrimitiveSlotCodecs.Get(type));
    }

    [Fact]
    public void Typed_lookup_also_rejects_unsupported_value_types() {
        Assert.Throws<NotSupportedException>(() => PrimitiveSlotCodecs.Get<decimal>());
        Assert.Throws<NotSupportedException>(() => PrimitiveSlotCodecs.Get<DayOfWeek>());
        Assert.Throws<NotSupportedException>(() => PrimitiveSlotCodecs.Get<ValueTuple<int>>());
    }

    [Fact]
    public void Null_type_and_delegates_are_rejected() {
        Assert.Throws<ArgumentNullException>(() => PrimitiveSlotCodecs.Get(null!));
        ValueSlotCodec<int> primitive = PrimitiveSlotCodecs.Get<int>();
        Assert.Throws<ArgumentNullException>(() => new ValueSlotCodec<int>(null!, primitive.Write));
        Assert.Throws<ArgumentNullException>(() => new ValueSlotCodec<int>(primitive.Read, null!));
    }

    private static T AssertGolden<T>(T value, byte[] golden) where T : struct {
        ValueSlotCodec<T> codec = PrimitiveSlotCodecs.Get<T>();
        Assert.Equal(typeof(T), codec.ValueType);
        ValueSlotCodec<T> runtimeCodec = Assert.IsType<ValueSlotCodec<T>>(PrimitiveSlotCodecs.Get(typeof(T)));
        Assert.Equal(typeof(T[]), runtimeCodec.BindVector().ArrayType);
        Assert.Equal(typeof(T[,]), runtimeCodec.BindArray2().ArrayType);
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        codec.Write(ref writer, ref value);
        Assert.Equal(golden, buffer.WrittenSpan.ToArray());

        // Decode independent golden bytes through the runtime lookup path.
        BareValueReader reader = new(golden);
        T restored = default;
        runtimeCodec.Read(ref reader, ref restored);
        reader.EnsureFullyConsumed();
        Assert.Equal(value, restored);
        return restored;
    }
}
