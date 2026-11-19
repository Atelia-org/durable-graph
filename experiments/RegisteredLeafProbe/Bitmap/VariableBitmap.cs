using System.Buffers;
using Atelia.Binary;
using Atelia.DurableGraph.Serialization;

namespace Probe.Bitmap;

// A second ordinary mutable leaf. Only managed bytes are reachable from this type.
[DurableLeaf("Probe.VariableBitmapV1", 1)]
public sealed class VariableBitmap {
    private byte[] _bytes;
    public int Length { get; private set; }

    public VariableBitmap(int bits) {
        ArgumentOutOfRangeException.ThrowIfNegative(bits);
        Length = bits;
        _bytes = new byte[ByteCount(bits)];
    }

    internal VariableBitmap(int bits, ReadOnlySpan<byte> bytes) {
        Length = bits;
        _bytes = bytes.ToArray();
    }

    public bool this[int bit] {
        get {
            Range(bit);
            return (_bytes[bit / 8] & 1 << (bit % 8)) != 0;
        }
        set {
            Range(bit);
            byte mask = (byte)(1 << (bit % 8));
            if (value) _bytes[bit / 8] |= mask;
            else _bytes[bit / 8] &= (byte)~mask;
        }
    }

    public void Resize(int bits) {
        ArgumentOutOfRangeException.ThrowIfNegative(bits);
        Array.Resize(ref _bytes, ByteCount(bits));
        Length = bits;
        if ((bits & 7) != 0) _bytes[^1] &= (byte)((1 << (bits & 7)) - 1);
    }

    internal static int ByteCount(int bits) => (int)(((long)bits + 7) / 8);
    internal byte[] Freeze() => (byte[])_bytes.Clone();
    private void Range(int bit) => ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)bit, (uint)Length, nameof(bit));
}

public static class BitmapPersistence {
    private static readonly Codec SharedCodec = new();

    public static void Register(IDurableLeafRegistration registry) => registry.RegisterLeaf(SharedCodec);

    private sealed record Frozen(int Bits, byte[] Data);

    private sealed class Codec : DurableLeafCodec<VariableBitmap, Frozen> {
        public override Frozen Capture(VariableBitmap value, LeafAcceptance? acceptance) => new(value.Length, value.Freeze());
        public override VariableBitmap Restore(Frozen state) => new(state.Bits, state.Data);
        public override long? EstimateBaseSize(Frozen state) => BareValueEncoding.MeasureVarUInt32((uint)state.Bits) + state.Data.Length;

        public override PreparedBaseBody PrepareBase(Frozen state) {
            var buffer = new ArrayBufferWriter<byte>();
            var writer = new BareValueWriter(buffer);
            WriteFull(ref writer, state);
            return new(buffer.WrittenSpan);
        }

        public override PreparedDeltaBody PrepareDelta(Frozen previous, Frozen current) {
            var buffer = new ArrayBufferWriter<byte>();
            var writer = new BareValueWriter(buffer);
            if (current.Bits != previous.Bits) {
                writer.WriteByte(1);
                WriteFull(ref writer, current);
                return new(true, buffer.WrittenSpan);
            }
            int changed = 0;
            for (int i = 0; i < current.Data.Length; i++) if (current.Data[i] != previous.Data[i]) changed++;
            writer.WriteByte(0);
            writer.WriteVarUInt32((uint)changed);
            for (int i = 0; i < current.Data.Length; i++) {
                if (current.Data[i] == previous.Data[i]) continue;
                writer.WriteVarUInt32((uint)i);
                writer.WriteByte(current.Data[i]);
            }
            return new(changed != 0, buffer.WrittenSpan);
        }

        public override Frozen ReadBase(int version, ref BareValueReader reader) {
            int bits = checked((int)reader.ReadVarUInt32());
            return Checked(bits, reader.ReadRawBytes(VariableBitmap.ByteCount(bits)).ToArray());
        }

        public override Frozen ApplyDelta(int version, ref BareValueReader reader, Frozen previous) {
            byte tag = reader.ReadByte();
            if (tag == 1) return ReadBase(version, ref reader);
            if (tag != 0) throw new InvalidDataException("Unknown bitmap delta.");
            uint count = reader.ReadVarUInt32();
            if (count > previous.Data.Length) throw new InvalidDataException("Too many bitmap changes.");
            byte[] next = (byte[])previous.Data.Clone();
            int last = -1;
            for (uint i = 0; i < count; i++) {
                uint at = reader.ReadVarUInt32();
                if (at >= next.Length || (int)at <= last) throw new InvalidDataException("Invalid bitmap offset.");
                last = (int)at;
                next[last] = reader.ReadByte();
            }
            return Checked(previous.Bits, next);
        }

        private static void WriteFull(ref BareValueWriter writer, Frozen state) {
            writer.WriteVarUInt32((uint)state.Bits);
            writer.WriteRawBytes(state.Data);
        }

        private static Frozen Checked(int bits, byte[] bytes) {
            if ((bits & 7) != 0 && (bytes[^1] >> (bits & 7)) != 0)
                throw new InvalidDataException("Bitmap has nonzero padding.");
            return new(bits, bytes);
        }
    }
}
