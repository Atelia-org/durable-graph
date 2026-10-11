using Atelia.Binary;
using System.Buffers;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Serialization.Tests;

public sealed class ArrayElementCodecTests {
    [Fact]
    public void Vector_binding_writes_only_elements_and_reads_into_existing_slots() {
        ArrayElementCodec codec = PrimitiveSlotCodecs.Get<int>().BindVector();
        int[] source = [-1, 0, 1, 64];
        Assert.Equal([0x01, 0x00, 0x02, 0x80, 0x01], Encode(codec, source));
        int[] target = [9, 9, 9, 9];
        BareValueReader reader = new([0x01, 0x00, 0x02, 0x80, 0x01]);
        codec.ReadElements(ref reader, target);
        Assert.Equal(source, target);
        reader.EnsureFullyConsumed();
    }

    [Fact]
    public void Array2_binding_uses_last_dimension_fastest_and_honors_each_arrays_bounds() {
        ArrayElementCodec codec = PrimitiveSlotCodecs.Get<int>().BindArray2();
        int[,] source = (int[,])Array.CreateInstance(typeof(int), [2, 3], [-2, 7]);
        source[-2, 7] = 1;
        source[-2, 8] = 2;
        source[-2, 9] = 3;
        source[-1, 7] = 4;
        source[-1, 8] = 5;
        source[-1, 9] = 6;
        Assert.Equal([0x02, 0x04, 0x06, 0x08, 0x0a, 0x0c], Encode(codec, source));

        int[,] target = (int[,])Array.CreateInstance(typeof(int), [2, 3], [10, -4]);
        BareValueReader reader = new([0x02, 0x04, 0x06, 0x08, 0x0a, 0x0c]);
        codec.ReadElements(ref reader, target);
        Assert.Equal(1, target[10, -4]);
        Assert.Equal(2, target[10, -3]);
        Assert.Equal(3, target[10, -2]);
        Assert.Equal(4, target[11, -4]);
        Assert.Equal(5, target[11, -3]);
        Assert.Equal(6, target[11, -2]);
        reader.EnsureFullyConsumed();
    }

    [Fact]
    public void Array2_binding_handles_extreme_bounds_without_upper_bound_increment_overflow() {
        ArrayElementCodec codec = PrimitiveSlotCodecs.Get<int>().BindArray2();
        int[,] source = (int[,])Array.CreateInstance(
            typeof(int), [1, 2], [int.MinValue, int.MaxValue - 1]);
        source[int.MinValue, int.MaxValue - 1] = -1;
        source[int.MinValue, int.MaxValue] = 64;
        Assert.Equal([0x01, 0x80, 0x01], Encode(codec, source));

        int[,] target = (int[,])Array.CreateInstance(
            typeof(int), [1, 2], [int.MinValue, int.MaxValue - 1]);
        BareValueReader reader = new([0x01, 0x80, 0x01]);
        codec.ReadElements(ref reader, target);
        Assert.Equal(-1, target[int.MinValue, int.MaxValue - 1]);
        Assert.Equal(64, target[int.MinValue, int.MaxValue]);
        reader.EnsureFullyConsumed();
    }

    [Fact]
    public void Empty_vectors_and_either_empty_dimension_do_not_invoke_element_bodies() {
        ValueSlotCodec<int> slot = new(
            static (ref BareValueReader reader, ref int value) => throw new InvalidOperationException(),
            static (ref BareValueWriter writer, ref int value) => throw new InvalidOperationException());
        AssertEmpty(slot.BindVector(), Array.Empty<int>());
        AssertEmpty(slot.BindArray2(), Array.CreateInstance(typeof(int), [0, 3], [-2, 7]));
        AssertEmpty(slot.BindArray2(), Array.CreateInstance(typeof(int), [3, 0], [-2, 7]));
    }

    [Fact]
    public void Bindings_with_the_same_value_type_retain_their_own_bodies() {
        ValueSlotCodec<int> ordinary = PrimitiveSlotCodecs.Get<int>();
        ValueSlotCodec<int> biased = new(
            static (ref BareValueReader reader, ref int value) => value = reader.ReadVarInt32() - 10,
            static (ref BareValueWriter writer, ref int value) => writer.WriteVarInt32(value + 10));
        ArrayElementCodec ordinaryVector = ordinary.BindVector();
        ArrayElementCodec biasedVector = biased.BindVector();
        ArrayElementCodec ordinaryMatrix = ordinary.BindArray2();
        ArrayElementCodec biasedMatrix = biased.BindArray2();
        Assert.Equal([0x02, 0x04], Encode(ordinaryVector, new[] { 1, 2 }));
        Assert.Equal([0x16, 0x18], Encode(biasedVector, new[] { 1, 2 }));
        Assert.Equal([0x02, 0x04], Encode(ordinaryMatrix, new[,] { { 1, 2 } }));
        Assert.Equal([0x16, 0x18], Encode(biasedMatrix, new[,] { { 1, 2 } }));
        Assert.Equal([0x02, 0x04], Encode(ordinaryVector, new[] { 1, 2 }));

        int[] vector = new int[2];
        int[,] matrix = new int[1, 2];
        BareValueReader reader = new([0x16, 0x18, 0x16, 0x18]);
        biasedVector.ReadElements(ref reader, vector);
        biasedMatrix.ReadElements(ref reader, matrix);
        Assert.Equal([1, 2], vector);
        Assert.Equal(1, matrix[0, 0]);
        Assert.Equal(2, matrix[0, 1]);
        reader.EnsureFullyConsumed();
    }

    [Fact]
    public void Writer_bodies_receive_actual_vector_and_matrix_slots_by_ref() {
        ValueSlotCodec<int> slot = new(
            PrimitiveSlotCodecs.Get<int>().Read,
            static (ref BareValueWriter writer, ref int value) => {
                writer.WriteVarInt32(value);
                value++;
            });
        int[] vector = [7];
        int[,] matrix = { { 9 } };
        Assert.Equal([0x0e], Encode(slot.BindVector(), vector));
        Assert.Equal([0x12], Encode(slot.BindArray2(), matrix));
        Assert.Equal(8, vector[0]);
        Assert.Equal(10, matrix[0, 0]);
    }

    [Fact]
    public void Exact_type_validation_precedes_any_read_or_write() {
        ArrayElementCodec vector = PrimitiveSlotCodecs.Get<int>().BindVector();
        ArrayElementCodec matrix = PrimitiveSlotCodecs.Get<int>().BindArray2();
        Array[] invalidVectors = [
            new uint[1], new long[1], new object[1], new int[1, 1], new int[1, 1, 1],
            Array.CreateInstance(typeof(int), [1], [1]),
        ];
        Array[] invalidMatrices = [new int[1], new uint[1, 1], new long[1, 1], new int[1, 1, 1]];
        foreach (Array invalid in invalidVectors) {
            AssertRejectedBeforeIo<ArgumentException>(vector, invalid);
        }

        foreach (Array invalid in invalidMatrices) {
            AssertRejectedBeforeIo<ArgumentException>(matrix, invalid);
        }

        AssertRejectedBeforeIo<ArgumentNullException>(vector, null!);
        AssertRejectedBeforeIo<ArgumentNullException>(matrix, null!);
    }

    [Fact]
    public void Truncated_vector_read_keeps_preceding_values_and_leaves_failing_slot_unchanged() {
        int[] target = [99, 98, 97];
        BareValueReader reader = new([0x02, 0x80]);
        Exception? exception = null;
        try {
            PrimitiveSlotCodecs.Get<int>().BindVector().ReadElements(ref reader, target);
        } catch (Exception current) {
            exception = current;
        }

        Assert.IsType<EndOfStreamException>(exception);
        Assert.Equal([1, 98, 97], target);
        Assert.Equal(1, reader.ConsumedCount);
        Assert.Equal(1, reader.RemainingCount);
    }

    [Fact]
    public void Invalid_boolean_matrix_read_keeps_already_filled_slots() {
        bool[,] target = { { true, false }, { true, true } };
        BareValueReader reader = new([0x00, 0x01, 0x02, 0x00]);
        Exception? exception = null;
        try {
            PrimitiveSlotCodecs.Get<bool>().BindArray2().ReadElements(ref reader, target);
        } catch (Exception current) {
            exception = current;
        }

        Assert.IsType<InvalidDataException>(exception);
        Assert.False(target[0, 0]);
        Assert.True(target[0, 1]);
        Assert.True(target[1, 0]);
        Assert.True(target[1, 1]);
        Assert.Equal(2, reader.ConsumedCount);
    }

    [Fact]
    public void Read_stops_at_target_element_count_and_outer_caller_checks_trailing_bytes() {
        int[] target = new int[1];
        BareValueReader reader = new([0x02, 0x04]);
        PrimitiveSlotCodecs.Get<int>().BindVector().ReadElements(ref reader, target);
        Assert.Equal([1], target);
        Assert.Equal(1, reader.ConsumedCount);
        Exception? exception = null;
        try {
            reader.EnsureFullyConsumed();
        } catch (Exception current) {
            exception = current;
        }

        Assert.IsType<InvalidDataException>(exception);
        Assert.Equal(2, reader.ReadVarInt32());
        reader.EnsureFullyConsumed();
    }

    [Fact]
    public void Throwing_writer_body_does_not_roll_back_previous_output() {
        ValueSlotCodec<int> slot = new(
            PrimitiveSlotCodecs.Get<int>().Read,
            static (ref BareValueWriter writer, ref int value) => {
                if (value == 2) {
                    throw new InvalidOperationException("Element body failed.");
                }

                writer.WriteVarInt32(value);
            });
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        Exception? exception = null;
        try {
            slot.BindVector().WriteElements(ref writer, new[] { 1, 2, 3 });
        } catch (Exception current) {
            exception = current;
        }

        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal([0x02], buffer.WrittenSpan.ToArray());
    }

    private static byte[] Encode(ArrayElementCodec codec, Array values) {
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        codec.WriteElements(ref writer, values);
        return buffer.WrittenSpan.ToArray();
    }

    private static void AssertEmpty(ArrayElementCodec codec, Array values) {
        Assert.Empty(Encode(codec, values));
        BareValueReader reader = new([0xab]);
        codec.ReadElements(ref reader, values);
        Assert.Equal(0, reader.ConsumedCount);
    }

    private static void AssertRejectedBeforeIo<TException>(ArrayElementCodec codec, Array values)
        where TException : Exception {
        BareValueReader reader = new([0xab, 0x01]);
        Assert.Equal(0xab, reader.ReadByte());
        Exception? readException = null;
        try {
            codec.ReadElements(ref reader, values);
        } catch (Exception current) {
            readException = current;
        }

        Assert.IsType<TException>(readException);
        Assert.Equal(1, reader.ConsumedCount);
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        writer.WriteByte(0xab);
        Exception? writeException = null;
        try {
            codec.WriteElements(ref writer, values);
        } catch (Exception current) {
            writeException = current;
        }

        Assert.IsType<TException>(writeException);
        Assert.Equal([0xab], buffer.WrittenSpan.ToArray());
    }
}
