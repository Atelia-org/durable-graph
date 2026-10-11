using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed class StateEqualityTests {
    [Fact]
    public void BuiltinComparisonMatchesPersistentBytesDeltaAndRoundTrip() {
        Check<bool, BooleanStateOps>(TypeTag.Boolean, [false, true]);
        Check<byte, ByteStateOps>(TypeTag.Byte, [0, 1, byte.MaxValue]);
        Check<sbyte, SByteStateOps>(TypeTag.SByte, [sbyte.MinValue, 0, sbyte.MaxValue]);
        Check<short, Int16StateOps>(TypeTag.Int16, [short.MinValue, 0, short.MaxValue]);
        Check<ushort, UInt16StateOps>(TypeTag.UInt16, [0, 1, ushort.MaxValue]);
        Check<int, Int32StateOps>(TypeTag.Int32, [int.MinValue, 0, int.MaxValue]);
        Check<uint, UInt32StateOps>(TypeTag.UInt32, [0, 1, uint.MaxValue]);
        Check<long, Int64StateOps>(TypeTag.Int64, [long.MinValue, 0, long.MaxValue]);
        Check<ulong, UInt64StateOps>(TypeTag.UInt64, [0, 1, ulong.MaxValue]);
        Check<char, CharStateOps>(TypeTag.Char, ['\0', 'x', '\uD800', '\uFFFF']);
        Check<Half, HalfStateOps>(TypeTag.Half, [
            (Half)0, BitConverter.UInt16BitsToHalf(0x8000),
            BitConverter.UInt16BitsToHalf(0x7E01), BitConverter.UInt16BitsToHalf(0x7E02), Half.PositiveInfinity]);
        Check<float, SingleStateOps>(TypeTag.Single, [
            0f, BitConverter.UInt32BitsToSingle(0x80000000),
            BitConverter.UInt32BitsToSingle(0x7FC00001), BitConverter.UInt32BitsToSingle(0x7FC00002), float.PositiveInfinity]);
        Check<double, DoubleStateOps>(TypeTag.Double, [
            0d, BitConverter.UInt64BitsToDouble(0x8000000000000000),
            BitConverter.UInt64BitsToDouble(0x7FF8000000000001), BitConverter.UInt64BitsToDouble(0x7FF8000000000002), double.PositiveInfinity]);
        Check<ObjectId, StringIdStateOps>(TypeTag.String, [new(0), new(1), new(uint.MaxValue)]);
        Check<ObjectId, ObjectIdStateOps>(TypeTag.ObjectReference, [new(0), new(1), new(uint.MaxValue)]);
        Check<ObjectId, DurableIdStateOps>(TypeTag.ObjectReference, [new(0), new(1), new(uint.MaxValue)]);
    }

    private static void Check<T, TOps>(TypeTag tag, T[] values) where T : unmanaged where TOps : IStateOps<T> {
        DurableFieldInfo slot = tag == TypeTag.ObjectReference ? DurableFieldInfo.Reference(1, TypeExpr.Named("Node")) : new(1, tag);
        for (int leftIndex = 0; leftIndex < values.Length; leftIndex++) {
            for (int rightIndex = 0; rightIndex < values.Length; rightIndex++) {
                T left = values[leftIndex], right = values[rightIndex];
                byte[] leftBytes = Encode<T, TOps>(in left, slot), rightBytes = Encode<T, TOps>(in right, slot);
                bool equal = TOps.StateEquals(in left, in right, slot);
                Assert.Equal(leftBytes.SequenceEqual(rightBytes), equal);
                Assert.Equal(!TOps.PrepareDelta(in left, in right, slot).HasChanges, equal);
                BareValueReader reader = new(rightBytes);
                T restored = TOps.ReadBase(ref reader, slot);
                reader.EnsureFullyConsumed();
                Assert.True(TOps.StateEquals(in right, in restored, slot));
            }
        }
        // Warm up generic dispatch before measuring only the comparison loop.
        int expected = 0;
        for (int iteration = 0; iteration < 1000; iteration++) {
            if (TOps.StateEquals(in values[0], in values[iteration % values.Length], slot)) expected++;
        }
        long start = GC.GetAllocatedBytesForCurrentThread();
        int observed = 0;
        for (int iteration = 0; iteration < 1000; iteration++) {
            if (TOps.StateEquals(in values[0], in values[iteration % values.Length], slot)) observed++;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.Equal(expected, observed);
        Assert.Equal(0, allocated);
    }

    private static byte[] Encode<T, TOps>(in T value, DurableFieldInfo slot) where T : unmanaged where TOps : IStateOps<T> {
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        TOps.WriteBase(ref writer, in value, slot);
        return buffer.WrittenSpan.ToArray();
    }
}
