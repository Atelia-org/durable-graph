using System.Buffers.Binary;
using Atelia.Binary;

namespace Atelia.DurableGraph.Serialization;

/// <summary>DG's composed BCL values; primitive encoding and cursor ownership belong to Atelia.Binary.</summary>
public static class BclValueCodec {
    public static void WriteGuid(ref BareValueWriter writer, Guid value) {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes, bigEndian: true, out _);
        writer.WriteRawBytes(bytes);
    }

    public static Guid ReadGuid(ref BareValueReader reader) => new(reader.ReadRawBytes(16), bigEndian: true);

    public static void WriteDecimal(ref BareValueWriter writer, decimal value) {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        Span<byte> bytes = stackalloc byte[16];
        for (int index = 0; index < bits.Length; index++) {
            BinaryPrimitives.WriteInt32LittleEndian(bytes[(index * sizeof(int))..], bits[index]);
        }
        writer.WriteRawBytes(bytes);
    }

    /// <summary>Preserves all coefficient words, scale, and signed zero; failure leaves the cursor unchanged.</summary>
    public static decimal ReadDecimal(ref BareValueReader reader) {
        BareValueReader candidate = reader;
        ReadOnlySpan<byte> bytes = candidate.ReadRawBytes(16);
        int lo = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        int mid = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]);
        int hi = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        uint scale = (flags >> 16) & 0xff;
        if ((flags & 0x7f00ffffu) != 0 || scale > 28) {
            throw new InvalidDataException("Invalid decimal flags: reserved bits must be zero and scale must be 0 through 28.");
        }
        decimal value = new(lo, mid, hi, isNegative: (flags & 0x80000000u) != 0, scale: (byte)scale);
        reader = candidate;
        return value;
    }

    public static void WriteTimeSpan(ref BareValueWriter writer, TimeSpan value) => writer.WriteVarInt64(value.Ticks);
    public static TimeSpan ReadTimeSpan(ref BareValueReader reader) => new(reader.ReadVarInt64());
    public static void WriteDateOnly(ref BareValueWriter writer, DateOnly value) => writer.WriteVarUInt32((uint)value.DayNumber);
    public static DateOnly ReadDateOnly(ref BareValueReader reader) {
        BareValueReader candidate = reader;
        uint dayNumber = candidate.ReadVarUInt32();
        if (dayNumber > (uint)DateOnly.MaxValue.DayNumber) {
            throw new InvalidDataException("DateOnly day number exceeds the supported calendar range.");
        }
        DateOnly value = DateOnly.FromDayNumber((int)dayNumber);
        reader = candidate;
        return value;
    }

    public static void WriteTimeOnly(ref BareValueWriter writer, TimeOnly value) => writer.WriteVarUInt64((ulong)value.Ticks);
    public static TimeOnly ReadTimeOnly(ref BareValueReader reader) {
        BareValueReader candidate = reader;
        ulong ticks = candidate.ReadVarUInt64();
        if (ticks >= (ulong)TimeSpan.TicksPerDay) {
            throw new InvalidDataException("TimeOnly ticks must be within a single day.");
        }
        TimeOnly value = new((long)ticks);
        reader = candidate;
        return value;
    }

    public static void WriteDateTimeOffset(ref BareValueWriter writer, DateTimeOffset value) {
        writer.WriteVarUInt64((ulong)value.Ticks);
        writer.WriteVarInt32((int)(value.Offset.Ticks / TimeSpan.TicksPerMinute));
    }

    /// <summary>Preserves clock ticks and the complete offset; failure leaves the cursor unchanged.</summary>
    public static DateTimeOffset ReadDateTimeOffset(ref BareValueReader reader) {
        BareValueReader candidate = reader;
        ulong clockTicks = candidate.ReadVarUInt64();
        int offsetMinutes = candidate.ReadVarInt32();
        if (clockTicks > (ulong)DateTime.MaxValue.Ticks || offsetMinutes < -840 || offsetMinutes > 840) {
            throw new InvalidDataException("DateTimeOffset clock ticks or offset are outside the supported range.");
        }
        long offsetTicks = offsetMinutes * TimeSpan.TicksPerMinute;
        long utcTicks = (long)clockTicks - offsetTicks;
        if (utcTicks < 0 || utcTicks > DateTime.MaxValue.Ticks) {
            throw new InvalidDataException("DateTimeOffset UTC ticks are outside the supported calendar range.");
        }
        DateTimeOffset value = new((long)clockTicks, new TimeSpan(offsetTicks));
        reader = candidate;
        return value;
    }
}
