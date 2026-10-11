using Atelia.Binary;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Serialization.Tests;

/// <summary>Shared primitive bodies for slot and array binding tests.</summary>
internal static class PrimitiveSlotCodecs {
    private static readonly Dictionary<Type, ValueSlotCodec> Codecs = new() {
        [typeof(bool)] = new ValueSlotCodec<bool>(
            static (ref BareValueReader reader, ref bool value) => value = reader.ReadBoolean(),
            static (ref BareValueWriter writer, ref bool value) => writer.WriteBoolean(value)),
        [typeof(byte)] = new ValueSlotCodec<byte>(
            static (ref BareValueReader reader, ref byte value) => value = reader.ReadByte(),
            static (ref BareValueWriter writer, ref byte value) => writer.WriteByte(value)),
        [typeof(sbyte)] = new ValueSlotCodec<sbyte>(
            static (ref BareValueReader reader, ref sbyte value) => value = reader.ReadSByte(),
            static (ref BareValueWriter writer, ref sbyte value) => writer.WriteSByte(value)),
        [typeof(short)] = new ValueSlotCodec<short>(
            static (ref BareValueReader reader, ref short value) => value = reader.ReadVarInt16(),
            static (ref BareValueWriter writer, ref short value) => writer.WriteVarInt16(value)),
        [typeof(ushort)] = new ValueSlotCodec<ushort>(
            static (ref BareValueReader reader, ref ushort value) => value = reader.ReadVarUInt16(),
            static (ref BareValueWriter writer, ref ushort value) => writer.WriteVarUInt16(value)),
        [typeof(int)] = new ValueSlotCodec<int>(
            static (ref BareValueReader reader, ref int value) => value = reader.ReadVarInt32(),
            static (ref BareValueWriter writer, ref int value) => writer.WriteVarInt32(value)),
        [typeof(uint)] = new ValueSlotCodec<uint>(
            static (ref BareValueReader reader, ref uint value) => value = reader.ReadVarUInt32(),
            static (ref BareValueWriter writer, ref uint value) => writer.WriteVarUInt32(value)),
        [typeof(long)] = new ValueSlotCodec<long>(
            static (ref BareValueReader reader, ref long value) => value = reader.ReadVarInt64(),
            static (ref BareValueWriter writer, ref long value) => writer.WriteVarInt64(value)),
        [typeof(ulong)] = new ValueSlotCodec<ulong>(
            static (ref BareValueReader reader, ref ulong value) => value = reader.ReadVarUInt64(),
            static (ref BareValueWriter writer, ref ulong value) => writer.WriteVarUInt64(value)),
        [typeof(char)] = new ValueSlotCodec<char>(
            static (ref BareValueReader reader, ref char value) => value = (char)reader.ReadVarUInt16(),
            static (ref BareValueWriter writer, ref char value) => writer.WriteVarUInt16(value)),
        [typeof(Half)] = new ValueSlotCodec<Half>(
            static (ref BareValueReader reader, ref Half value) => value = reader.ReadHalfLE(),
            static (ref BareValueWriter writer, ref Half value) => writer.WriteHalfLE(value)),
        [typeof(float)] = new ValueSlotCodec<float>(
            static (ref BareValueReader reader, ref float value) => value = reader.ReadSingleLE(),
            static (ref BareValueWriter writer, ref float value) => writer.WriteSingleLE(value)),
        [typeof(double)] = new ValueSlotCodec<double>(
            static (ref BareValueReader reader, ref double value) => value = reader.ReadDoubleLE(),
            static (ref BareValueWriter writer, ref double value) => writer.WriteDoubleLE(value)),
    };

    internal static ValueSlotCodec Get(Type type) {
        ArgumentNullException.ThrowIfNull(type);
        return Codecs.TryGetValue(type, out ValueSlotCodec? codec)
            ? codec
            : throw new NotSupportedException($"No primitive slot codec is defined for '{type}'.");
    }

    internal static ValueSlotCodec<T> Get<T>() where T : struct => (ValueSlotCodec<T>)Get(typeof(T));
}
