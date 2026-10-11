using Atelia.Binary;
using System.Buffers;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Serialization.Tests;

public sealed class BareValuePrimitiveTests {
    [Fact]
    public void Compound_payload_has_stable_golden_bytes_and_round_trips() {
        Half half = BitConverter.UInt16BitsToHalf(0xfe01);
        float single = BitConverter.Int32BitsToSingle(unchecked((int)0xffc12345));
        double @double = BitConverter.Int64BitsToDouble(unchecked((long)0xfff8123456789abc));
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);

        writer.WriteByte(0xab);
        writer.WriteSByte(-2);
        writer.WriteBoolean(false);
        writer.WriteBoolean(true);
        writer.WriteVarUInt16(300);
        writer.WriteVarUInt32(uint.MaxValue);
        writer.WriteVarUInt64(ulong.MaxValue);
        writer.WriteVarInt16(short.MinValue);
        writer.WriteVarInt32(-1);
        writer.WriteVarInt64(long.MaxValue);
        writer.WriteHalfLE(half);
        writer.WriteSingleLE(single);
        writer.WriteDoubleLE(@double);
        writer.WriteRawBytes([0xaa, 0xbb]);
        writer.WriteVarUInt32(3);
        writer.WriteBytes([0xcc, 0xdd]);

        Assert.Equal(
            [
                0xab,
                0xfe,
                0x00,
                0x01,
                0xac, 0x02,
                0xff, 0xff, 0xff, 0xff, 0x0f,
                0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x01,
                0xff, 0xff, 0x03,
                0x01,
                0xfe, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x01,
                0x01, 0xfe,
                0x45, 0x23, 0xc1, 0xff,
                0xbc, 0x9a, 0x78, 0x56, 0x34, 0x12, 0xf8, 0xff,
                0xaa, 0xbb,
                0x03,
                0x02, 0xcc, 0xdd,
            ],
            buffer.WrittenSpan.ToArray());

        BareValueReader reader = new(buffer.WrittenSpan);
        Assert.Equal(0, reader.ConsumedCount);
        Assert.Equal(buffer.WrittenCount, reader.RemainingCount);
        Assert.Equal(0xab, reader.ReadByte());
        Assert.Equal(-2, reader.ReadSByte());
        Assert.False(reader.ReadBoolean());
        Assert.True(reader.ReadBoolean());
        Assert.Equal(300, reader.ReadVarUInt16());
        Assert.Equal(uint.MaxValue, reader.ReadVarUInt32());
        Assert.Equal(ulong.MaxValue, reader.ReadVarUInt64());
        Assert.Equal(short.MinValue, reader.ReadVarInt16());
        Assert.Equal(-1, reader.ReadVarInt32());
        Assert.Equal(long.MaxValue, reader.ReadVarInt64());
        Assert.Equal(0xfe01, BitConverter.HalfToUInt16Bits(reader.ReadHalfLE()));
        Assert.Equal(
            unchecked((int)0xffc12345),
            BitConverter.SingleToInt32Bits(reader.ReadSingleLE()));
        Assert.Equal(
            unchecked((long)0xfff8123456789abc),
            BitConverter.DoubleToInt64Bits(reader.ReadDoubleLE()));
        Assert.Equal([0xaa, 0xbb], reader.ReadRawBytes(2).ToArray());
        Assert.Equal(3u, reader.ReadVarUInt32());
        Assert.Equal([0xcc, 0xdd], reader.ReadBytes().ToArray());
        Assert.True(reader.End);
        Assert.Equal(buffer.WrittenCount, reader.ConsumedCount);
        Assert.Equal(0, reader.RemainingCount);
        reader.EnsureFullyConsumed();
    }

    [Fact]
    public void Reader_accepts_independent_compound_golden() {
        BareValueReader reader = new([
            0x01,
            0xac, 0x02,
            0x01,
            0x00, 0x00, 0x00, 0x80,
            0xaa, 0xbb,
            0x03,
            0x02, 0xcc, 0xdd,
        ]);

        Assert.True(reader.ReadBoolean());
        Assert.Equal(300, reader.ReadVarUInt16());
        Assert.Equal(-1, reader.ReadVarInt32());
        Assert.Equal(
            unchecked((int)0x80000000),
            BitConverter.SingleToInt32Bits(reader.ReadSingleLE()));
        Assert.Equal([0xaa, 0xbb], reader.ReadRawBytes(2).ToArray());
        Assert.Equal(3u, reader.ReadVarUInt32());
        Assert.Equal([0xcc, 0xdd], reader.ReadBytes().ToArray());
        Assert.True(reader.End);
        reader.EnsureFullyConsumed();
    }

    [Fact]
    public void Integral_boundaries_round_trip() {
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);

        writer.WriteVarUInt16(0);
        writer.WriteVarUInt16(ushort.MaxValue);
        writer.WriteVarUInt32(0);
        writer.WriteVarUInt32(uint.MaxValue);
        writer.WriteVarUInt64(0);
        writer.WriteVarUInt64(ulong.MaxValue);
        writer.WriteVarInt16(short.MinValue);
        writer.WriteVarInt16(short.MaxValue);
        writer.WriteVarInt32(int.MinValue);
        writer.WriteVarInt32(int.MaxValue);
        writer.WriteVarInt64(long.MinValue);
        writer.WriteVarInt64(long.MaxValue);

        BareValueReader reader = new(buffer.WrittenSpan);
        Assert.Equal(0, reader.ReadVarUInt16());
        Assert.Equal(ushort.MaxValue, reader.ReadVarUInt16());
        Assert.Equal(0u, reader.ReadVarUInt32());
        Assert.Equal(uint.MaxValue, reader.ReadVarUInt32());
        Assert.Equal(0ul, reader.ReadVarUInt64());
        Assert.Equal(ulong.MaxValue, reader.ReadVarUInt64());
        Assert.Equal(short.MinValue, reader.ReadVarInt16());
        Assert.Equal(short.MaxValue, reader.ReadVarInt16());
        Assert.Equal(int.MinValue, reader.ReadVarInt32());
        Assert.Equal(int.MaxValue, reader.ReadVarInt32());
        Assert.Equal(long.MinValue, reader.ReadVarInt64());
        Assert.Equal(long.MaxValue, reader.ReadVarInt64());
        Assert.True(reader.End);
    }

    [Theory]
    [InlineData(0u, 1)]
    [InlineData(127u, 1)]
    [InlineData(128u, 2)]
    [InlineData(65_536u, 3)]
    [InlineData(uint.MaxValue, 5)]
    public void UInt32_uses_the_shortest_codeword_at_width_boundaries(
        uint value,
        int expectedWidth) {
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);

        writer.WriteVarUInt32(value);

        Assert.Equal(expectedWidth, buffer.WrittenCount);
        BareValueReader reader = new(buffer.WrittenSpan);
        Assert.Equal(value, reader.ReadVarUInt32());
        Assert.True(reader.End);
    }

    [Fact]
    public void Integer_writers_request_only_their_maximum_codeword_width() {
        BoundedBufferWriter uint16Buffer = new(3);
        BoundedBufferWriter uint32Buffer = new(5);
        BareValueWriter uint16Writer = new(uint16Buffer);
        BareValueWriter uint32Writer = new(uint32Buffer);

        uint16Writer.WriteVarUInt16(ushort.MaxValue);
        uint32Writer.WriteVarUInt32(uint.MaxValue);

        Assert.Equal([0xff, 0xff, 0x03], uint16Buffer.WrittenSpan.ToArray());
        Assert.Equal(
            [0xff, 0xff, 0xff, 0xff, 0x0f],
            uint32Buffer.WrittenSpan.ToArray());
    }

    [Theory]
    [InlineData(new byte[] { 0xff, 0xff, 0xff, 0xff, 0x10 })]
    [InlineData(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80 })]
    public void UInt32_rejects_overflow_input_without_advancing(
        byte[] source) {
        Exception exception = CaptureUInt32FailureAndAssertCursor(source);

        Assert.IsType<InvalidDataException>(exception);
    }

    [Fact]
    public void UInt32_rejects_truncated_input_without_advancing() {
        Exception exception = CaptureUInt32FailureAndAssertCursor([0x80]);

        Assert.IsType<EndOfStreamException>(exception);
    }

    [Fact]
    public void UInt64_rejects_overflow_without_advancing() {
        byte[] overlong = [
            0x80, 0x80, 0x80, 0x80, 0x80,
            0x80, 0x80, 0x80, 0x80, 0x00,
        ];
        byte[] overflow = [
            0xff, 0xff, 0xff, 0xff, 0xff,
            0xff, 0xff, 0xff, 0xff, 0x02,
        ];

        BareValueReader overlongReader = new(overlong);
        Assert.Equal(0ul, overlongReader.ReadVarUInt64());
        overlongReader.EnsureFullyConsumed();
        Assert.IsType<InvalidDataException>(
            CaptureUInt64FailureAndAssertCursor(overflow));
    }

    [Fact]
    public void UInt16_overflow_does_not_advance() {
        BareValueReader reader = new([0x80, 0x80, 0x04]);
        Exception? exception = null;

        try {
            reader.ReadVarUInt16();
        } catch (Exception current) {
            exception = current;
        }

        Assert.IsType<InvalidDataException>(exception);
        Assert.Equal(0, reader.ConsumedCount);
        Assert.Equal(3, reader.RemainingCount);
    }

    [Fact]
    public void Integers_accept_bounded_redundant_zero_codewords() {
        BareValueReader uint16 = new([0x80, 0x80, 0]);
        BareValueReader uint32 = new([0x80, 0]);
        Assert.Equal(0, uint16.ReadVarUInt16());
        Assert.Equal(0u, uint32.ReadVarUInt32());
        uint16.EnsureFullyConsumed();
        uint32.EnsureFullyConsumed();
    }

    [Fact]
    public void Signed_ZigZag_has_stable_golden_bytes() {
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);

        writer.WriteVarInt16(0);
        writer.WriteVarInt16(-1);
        writer.WriteVarInt16(1);
        writer.WriteVarInt16(-2);

        Assert.Equal([0x00, 0x01, 0x02, 0x03], buffer.WrittenSpan.ToArray());

        BareValueReader reader = new([0x00, 0x01, 0x02, 0x03]);
        Assert.Equal(0, reader.ReadVarInt16());
        Assert.Equal(-1, reader.ReadVarInt16());
        Assert.Equal(1, reader.ReadVarInt16());
        Assert.Equal(-2, reader.ReadVarInt16());
        Assert.True(reader.End);
    }

    [Fact]
    public void Invalid_boolean_does_not_advance() {
        BareValueReader reader = new([0x02, 0x01]);
        Exception? exception = null;

        try {
            reader.ReadBoolean();
        } catch (Exception current) {
            exception = current;
        }

        Assert.IsType<InvalidDataException>(exception);
        Assert.Equal(0, reader.ConsumedCount);
        Assert.Equal(2, reader.RemainingCount);
    }

    [Fact]
    public void Fixed_width_truncation_does_not_advance() {
        BareValueReader reader = new([0x00, 0x01, 0x02]);
        Exception? exception = null;

        try {
            reader.ReadSingleLE();
        } catch (Exception current) {
            exception = current;
        }

        Assert.IsType<EndOfStreamException>(exception);
        Assert.Equal(0, reader.ConsumedCount);
        Assert.Equal(3, reader.RemainingCount);
    }

    [Fact]
    public void Truncated_bytes_roll_back_completely() {
        BareValueReader reader = new([0x03, 0xaa, 0xbb]);
        Exception? failure = null;
        try { reader.ReadBytes(); } catch (Exception exception) { failure = exception; }
        Assert.IsType<EndOfStreamException>(failure);
        Assert.Equal(0, reader.ConsumedCount);
        Assert.Equal(3, reader.RemainingCount);
    }

    [Fact]
    public void Span_argument_failure_and_truncation_do_not_advance() {
        BareValueReader reader = new([0x01]);
        Exception? negativeException = null;
        Exception? truncatedException = null;

        try {
            reader.ReadRawBytes(-1);
        } catch (Exception current) {
            negativeException = current;
        }

        try {
            reader.ReadRawBytes(2);
        } catch (Exception current) {
            truncatedException = current;
        }

        Assert.IsType<ArgumentOutOfRangeException>(negativeException);
        Assert.IsType<EndOfStreamException>(truncatedException);
        Assert.Equal(0, reader.ConsumedCount);
        Assert.Equal(1, reader.RemainingCount);
    }

    [Fact]
    public void Ensure_fully_consumed_rejects_trailing_bytes_without_advancing() {
        BareValueReader reader = new([0x01]);
        Exception? exception = null;

        try {
            reader.EnsureFullyConsumed();
        } catch (Exception current) {
            exception = current;
        }

        Assert.IsType<InvalidDataException>(exception);
        Assert.Equal(0, reader.ConsumedCount);
    }

    [Fact]
    public void Writer_rejects_null_downstream() {
        Assert.Throws<ArgumentNullException>(() => new BareValueWriter(null!));
    }

    private static Exception CaptureUInt32FailureAndAssertCursor(byte[] source) {
        BareValueReader reader = new(source);
        try {
            reader.ReadVarUInt32();
        } catch (Exception exception) {
            Assert.Equal(0, reader.ConsumedCount);
            Assert.Equal(source.Length, reader.RemainingCount);
            return exception;
        }

        throw new InvalidOperationException("Expected UInt32 decoding to fail.");
    }

    private static Exception CaptureUInt64FailureAndAssertCursor(byte[] source) {
        BareValueReader reader = new(source);
        try {
            reader.ReadVarUInt64();
        } catch (Exception exception) {
            Assert.Equal(0, reader.ConsumedCount);
            Assert.Equal(source.Length, reader.RemainingCount);
            return exception;
        }

        throw new InvalidOperationException("Expected UInt64 decoding to fail.");
    }

    private sealed class BoundedBufferWriter : IBufferWriter<byte> {
        private readonly byte[] _buffer;
        private int _writtenCount;

        internal BoundedBufferWriter(int capacity) {
            _buffer = new byte[capacity];
        }

        internal ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _writtenCount);

        public void Advance(int count) {
            if (count < 0 || count > _buffer.Length - _writtenCount) {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            _writtenCount += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0) {
            ValidateSizeHint(sizeHint);
            return _buffer.AsMemory(_writtenCount);
        }

        public Span<byte> GetSpan(int sizeHint = 0) {
            ValidateSizeHint(sizeHint);
            return _buffer.AsSpan(_writtenCount);
        }

        private void ValidateSizeHint(int sizeHint) {
            if (sizeHint < 0 || sizeHint > _buffer.Length - _writtenCount) {
                throw new ArgumentOutOfRangeException(nameof(sizeHint));
            }
        }
    }
}
