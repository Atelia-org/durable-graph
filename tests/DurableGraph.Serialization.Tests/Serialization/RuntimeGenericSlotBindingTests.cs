using Atelia.Binary;
using System.Buffers;
using System.Reflection;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Serialization.Tests;

/// <summary>
/// Handwritten equivalents of future generated open generic bodies. These tests exercise runtime
/// binding with the real byte substrate; they do not claim generic Schema or SG support.
/// </summary>
public sealed class RuntimeGenericSlotBindingTests {
    [Fact]
    public void Runtime_closed_nested_body_uses_the_same_ref_codec_for_local_field_vector_and_array2() {
        ValueSlotCodec inner = BindCell(PrimitiveSlotCodecs.Get(typeof(int)));
        ValueSlotCodec outer = BindCell(inner);
        Assert.Equal(typeof(Cell<Cell<int>>), outer.ValueType);
        ValueSlotCodec<Cell<Cell<int>>> codec = Assert.IsType<ValueSlotCodec<Cell<Cell<int>>>>(outer);

        Cell<Cell<int>> source = new() {
            Tag = 1, Sentinel = 10,
            Item = new Cell<int> { Tag = 2, Item = 17, Sentinel = 20 },
        };
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        codec.Write(ref writer, ref source);
        Assert.Equal(new byte[] { 1, 2, 34 }, buffer.WrittenSpan.ToArray());
        Assert.Equal(17, source.Item.Item);
        Assert.Equal(10, source.Sentinel);
        Assert.Equal(20, source.Item.Sentinel);

        Cell<Cell<int>> local = EmptyCell(31, 32);
        Holder holder = new() { Value = EmptyCell(41, 42) };
        BareValueReader localReader = new(buffer.WrittenSpan);
        codec.Read(ref localReader, ref local);
        localReader.EnsureFullyConsumed();
        BareValueReader fieldReader = new(buffer.WrittenSpan);
        codec.Read(ref fieldReader, ref holder.Value);
        fieldReader.EnsureFullyConsumed();
        AssertCell(local, 17, 31, 32);
        AssertCell(holder.Value, 17, 41, 42);

        // The array type is constructed only after runtime binding. Test setup inspects it by a
        // known typed cast; the product entry accepts Array and never reflects over its elements.
        Array vector = Array.CreateInstance(outer.ValueType, 2);
        Cell<Cell<int>>[] typedVector = (Cell<Cell<int>>[])vector;
        typedVector[0] = EmptyCell(51, 52);
        typedVector[1] = EmptyCell(61, 62);
        byte[] twoBodies = [1, 2, 34, 1, 2, 57]; // 17, -29, ZigZag encoded.
        ArrayElementCodec vectorBody = outer.BindVector();
        BareValueReader vectorReader = new(twoBodies);
        vectorBody.ReadElements(ref vectorReader, vector);
        vectorReader.EnsureFullyConsumed();
        AssertCell(typedVector[0], 17, 51, 52);
        AssertCell(typedVector[1], -29, 61, 62);
        Assert.Equal(twoBodies, WriteElements(vectorBody, vector));

        Array matrix = Array.CreateInstance(outer.ValueType, [1, 2], [-3, 7]);
        Cell<Cell<int>>[,] typedMatrix = (Cell<Cell<int>>[,])matrix;
        typedMatrix[-3, 7] = EmptyCell(71, 72);
        typedMatrix[-3, 8] = EmptyCell(81, 82);
        ArrayElementCodec matrixBody = outer.BindArray2();
        BareValueReader matrixReader = new(twoBodies);
        matrixBody.ReadElements(ref matrixReader, matrix);
        matrixReader.EnsureFullyConsumed();
        AssertCell(typedMatrix[-3, 7], 17, 71, 72);
        AssertCell(typedMatrix[-3, 8], -29, 81, 82);
        Assert.Equal(twoBodies, WriteElements(matrixBody, matrix));
    }

    [Fact]
    public void Runtime_factory_closes_different_primitive_arguments_without_a_closed_type_registry() {
        ValueSlotCodec slot = BindCell(PrimitiveSlotCodecs.Get(typeof(double)));
        Assert.Equal(typeof(Cell<double>), slot.ValueType);
        ArrayElementCodec body = slot.BindVector();
        Array target = Array.CreateInstance(slot.ValueType, 1);
        byte[] bytes = [9, 0xbc, 0x9a, 0x78, 0x56, 0x34, 0x12, 0xf8, 0xff];
        BareValueReader reader = new(bytes);
        body.ReadElements(ref reader, target);
        reader.EnsureFullyConsumed();
        Cell<double>[] values = (Cell<double>[])target;
        Assert.Equal(9, values[0].Tag);
        Assert.Equal(unchecked((long)0xfff8123456789abc), BitConverter.DoubleToInt64Bits(values[0].Item));
        Assert.Equal(bytes, WriteElements(body, target));
    }

    [Fact]
    public void Failed_nested_read_leaves_only_the_visited_slots_changed() {
        ValueSlotCodec outer = BindCell(BindCell(PrimitiveSlotCodecs.Get(typeof(int))));
        Cell<Cell<int>>[] target = [EmptyCell(1, 2), EmptyCell(3, 4)];
        target[1].Item.Item = 99;
        BareValueReader reader = new([1, 2, 34, 5, 6, 0x80]);
        try {
            outer.BindVector().ReadElements(ref reader, target);
            Assert.Fail("The second element has a truncated Int32.");
        } catch (EndOfStreamException) {
            Assert.Equal(5, reader.ConsumedCount);
            AssertCell(target[0], 17, 1, 2);
            Assert.Equal(5, target[1].Tag);
            Assert.Equal(6, target[1].Item.Tag);
            Assert.Equal(99, target[1].Item.Item);
            Assert.Equal(3, target[1].Sentinel);
            Assert.Equal(4, target[1].Item.Sentinel);
        }
    }

    private static ValueSlotCodec BindCell(ValueSlotCodec member) {
        // Reflection is used once to close a precompiled body factory, never during element access.
        MethodInfo factory = typeof(RuntimeGenericSlotBindingTests).GetMethod(
            nameof(CreateCell), BindingFlags.NonPublic | BindingFlags.Static)!;
        return factory.MakeGenericMethod(member.ValueType)
            .CreateDelegate<Func<ValueSlotCodec, ValueSlotCodec>>()(member);
    }

    private static ValueSlotCodec CreateCell<T>(ValueSlotCodec member) where T : struct {
        ValueSlotCodec<T> item = (ValueSlotCodec<T>)member;
        return new ValueSlotCodec<Cell<T>>(
            (ref BareValueReader reader, ref Cell<T> value) => {
                value.Tag = reader.ReadByte();
                item.Read(ref reader, ref value.Item);
            },
            (ref BareValueWriter writer, ref Cell<T> value) => {
                writer.WriteByte(value.Tag);
                item.Write(ref writer, ref value.Item);
            });
    }

    private static byte[] WriteElements(ArrayElementCodec body, Array values) {
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        body.WriteElements(ref writer, values);
        return buffer.WrittenSpan.ToArray();
    }

    private static Cell<Cell<int>> EmptyCell(int outerSentinel, int innerSentinel) => new() {
        Sentinel = outerSentinel,
        Item = new Cell<int> { Sentinel = innerSentinel },
    };

    private static void AssertCell(Cell<Cell<int>> value, int number, int outerSentinel, int innerSentinel) {
        Assert.Equal(1, value.Tag);
        Assert.Equal(2, value.Item.Tag);
        Assert.Equal(number, value.Item.Item);
        Assert.Equal(outerSentinel, value.Sentinel);
        Assert.Equal(innerSentinel, value.Item.Sentinel);
    }

    private struct Cell<T> where T : struct {
        internal byte Tag;
        internal T Item;
        // Deliberately omitted by the supplied body; proves reads modify the actual nested slot.
        internal int Sentinel;
    }

    private sealed class Holder {
        internal Cell<Cell<int>> Value;
    }
}
