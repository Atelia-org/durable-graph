using Atelia.Binary;
using Atelia.DurableGraph.Serialization;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Atelia.Text;

/// <summary>Text bodies carry a UTF-16 length followed by nonempty Bare string segments (Base)
/// or Copy/New ranges with raw UTF-16LE literals (Delta).</summary>
internal static class TextBodyCodec {
    private const byte Copy = 1;
    private const byte New = 2;
    // A cold replay implementation limit, unrelated to the wire or the live editor policy.
    private const int MaximumReplayPieces = 4096;

    // Cheap planning input, not an exact encoded size or a capacity limit. Each segment
    // contributes an estimated one-byte header; selecting its encoding would scan text.
    internal static long EstimateBaseSize(FrozenText state) =>
        BareValueEncoding.MeasureVarUInt32((uint)state.Content.Length) +
        (long)state.Content.View.PieceCount + 2L * state.Content.Length;

    internal static PreparedBaseBody PrepareBase(FrozenText state) {
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        writer.WriteVarUInt32((uint)state.Content.Length);
        // The frozen view contains only nonempty pieces. Each slice is an independent
        // value: a surrogate outside its boundary does not participate in encoding it.
        foreach (TextPiece piece in state.Content.View.Pieces) { writer.WriteString(piece.Memory.Span); }
        return new(buffer.WrittenSpan);
    }

    internal static PreparedDeltaBody PrepareDelta(FrozenText previous, FrozenText current) {
        bool unchanged = ReferenceEquals(previous.ContentToken, current.ContentToken) ||
            previous.Content.ContentEquals(current.Content);
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        writer.WriteVarUInt32((uint)current.Content.Length);
        if (unchanged) {
            if (current.Content.Length != 0) { WriteCopy(ref writer, 0, current.Content.Length); }
            return new(false, buffer.WrittenSpan);
        }
        if (!ReferenceEquals(current.SourceToken, previous.ContentToken)) {
            // A shared CLR builder may have been rebound by another graph. Never interpret
            // its offsets against this graph's different persistent predecessor.
            if (current.Content.Length != 0) {
                writer.WriteByte(New);
                writer.WriteVarUInt32((uint)current.Content.Length);
                foreach (ReadOnlyMemory<char> chunk in current.Content.GetChunks()) { WriteUnits(ref writer, chunk.Span); }
            }
            return new(true, buffer.WrittenSpan);
        }
        WriteSourceRanges(ref writer, current.Content.View.PieceSpan, previous.Content.Length, current.Content.Length);
        return new(true, buffer.WrittenSpan);
    }

    private static void WriteSourceRanges(ref BareValueWriter writer, ReadOnlySpan<TextPiece> pieces,
        int sourceLength, int outputLength) {
        int output = 0;
        for (int index = 0; index < pieces.Length;) {
            TextPiece first = pieces[index];
            bool isCopy = first.BaseOffset >= 0;
            int length = first.Length;
            int end = index + 1;
            // New pieces form one literal even across strings. Copies can merge only
            // when the next piece continues the range in this exact predecessor.
            while (end < pieces.Length) {
                TextPiece next = pieces[end];
                if (isCopy ? next.BaseOffset != (long)first.BaseOffset + length : next.BaseOffset >= 0) { break; }
                length = checked(length + next.Length);
                end++;
            }
            if (length <= 0 || length > outputLength - output ||
                (isCopy && (first.BaseOffset > sourceLength || length > sourceLength - first.BaseOffset))) {
                throw new InvalidOperationException("The frozen Text pieces contain an invalid source range.");
            }
            if (isCopy) {
                WriteCopy(ref writer, first.BaseOffset, length);
            } else {
                writer.WriteByte(New);
                writer.WriteVarUInt32((uint)length);
                foreach (TextPiece literal in pieces[index..end]) { WriteUnits(ref writer, literal.Memory.Span); }
            }
            output += length;
            index = end;
        }
        if (output != outputLength) { throw new InvalidOperationException("The frozen Text pieces do not cover the output."); }
    }

    private static void WriteCopy(ref BareValueWriter writer, int offset, int length) {
        writer.WriteByte(Copy);
        writer.WriteVarUInt32((uint)offset);
        writer.WriteVarUInt32((uint)length);
    }

    private static void WriteUnits(ref BareValueWriter writer, ReadOnlySpan<char> units) {
        if (BitConverter.IsLittleEndian) {
            writer.WriteRawBytes(MemoryMarshal.AsBytes(units));
            return;
        }
        // Bounded scratch avoids one allocation per piece and is independent of host endianness.
        Span<byte> bytes = stackalloc byte[512];
        while (!units.IsEmpty) {
            int count = Math.Min(units.Length, bytes.Length / 2);
            for (int index = 0; index < count; index++) {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(index * 2, 2), units[index]);
            }
            writer.WriteRawBytes(bytes[..(count * 2)]);
            units = units[count..];
        }
    }

    internal static FrozenText ReadBase(ReadOnlySpan<byte> body) {
        BareValueReader reader = new(body);
        int length = ReadLength(ref reader);
        // Both supported payload encodings use at least one byte per UTF-16 unit.
        // Reject an impossible declared result before allocating its final string.
        if (length > reader.RemainingCount) { throw new InvalidDataException("Text Base units cannot fit in the remaining body."); }
        if (length == 0) {
            // string.Create skips the callback for zero length, including its tail check.
            reader.EnsureFullyConsumed();
            return new(TextSnapshot.FromString(string.Empty), sourceToken: null);
        }
        string text = string.Create(length, reader, static (destination, cursor) => {
            // Consume the complete bounded body even after the destination is full:
            // an extra empty segment is invalid rather than an ignorable suffix.
            while (!cursor.End) {
                int written;
                try { written = cursor.ReadString(destination); }
                catch (ArgumentException error) when (error.ParamName == "destination") {
                    throw new InvalidDataException("A Text Base segment exceeds the declared UTF-16 length.", error);
                }
                if (written == 0) { throw new InvalidDataException("A Text Base segment must be nonempty."); }
                destination = destination[written..];
            }
            if (!destination.IsEmpty) { throw new InvalidDataException("Text Base segments do not fill the declared UTF-16 length."); }
        });
        return new(TextSnapshot.FromString(text), sourceToken: null);
    }

    internal static FrozenText ApplyDelta(ref BareValueReader reader, FrozenText previous) {
        // Check the entire body before allocating the declared result. A malformed short
        // body must not turn an attacker-controlled length into a large allocation.
        BareValueReader validation = reader;
        int length = ReadLength(ref validation);
        ValidateDelta(ref validation, previous.Content.Length, length);
        validation.EnsureFullyConsumed();
        BareValueReader original = reader;
        _ = ReadLength(ref reader);
        List<TextPiece> pieces = [];
        int output = 0;
        while (output < length) {
            byte operation = reader.ReadByte();
            if (operation == Copy) {
                int source = checked((int)reader.ReadVarUInt32());
                int count = checked((int)reader.ReadVarUInt32());
                if (!previous.Content.View.TryAppendRangeTo(pieces, source, count, MaximumReplayPieces)) {
                    reader = original;
                    return ApplyFlatValidated(ref reader, previous);
                }
                output += count;
            } else {
                int count = checked((int)reader.ReadVarUInt32());
                if (pieces.Count == MaximumReplayPieces) {
                    reader = original;
                    return ApplyFlatValidated(ref reader, previous);
                }
                string literal = ReadString(reader.ReadRawBytes(count * 2));
                pieces.Add(new(literal, 0, count, -1));
                output += count;
            }
        }
        reader.EnsureFullyConsumed();
        return new(new TextSnapshot(TextPieceView.FromOwnedPieces(pieces, length)), sourceToken: null);
    }

    private static FrozenText ApplyFlatValidated(ref BareValueReader reader, FrozenText previous) {
        int length = ReadLength(ref reader);
        char[] units = new char[length];
        int output = 0;
        while (output < length) {
            byte operation = reader.ReadByte();
            if (operation == Copy) {
                int source = (int)reader.ReadVarUInt32();
                int count = (int)reader.ReadVarUInt32();
                previous.Content.CopyTo(source, units.AsSpan(output, count));
                output += count;
            } else {
                int count = (int)reader.ReadVarUInt32();
                ReadUnits(reader.ReadRawBytes(count * 2), units.AsSpan(output, count));
                output += count;
            }
        }
        reader.EnsureFullyConsumed();
        return new(TextSnapshot.FromString(new string(units)), sourceToken: null);
    }

    internal static FrozenText FinishReplay(FrozenText state) {
        // Prune a disproportionately retained source once at the end of the chain.
        // Intermediate versions can still share that source while replaying later Copies.
        TextPieceView view = state.Content.View;
        return view.LargestBacking > 4L * Math.Max(view.Length, 1) &&
            (long)view.LargestBacking - view.Length >= 4096
            ? new(TextSnapshot.FromString(view.ToText()), sourceToken: null) : state;
    }

    private static void ValidateDelta(ref BareValueReader reader, int sourceLength, int length) {
        int output = 0;
        byte priorOperation = 0;
        int priorCopyEnd = 0;
        while (output < length) {
            byte operation = reader.ReadByte();
            if (operation is not (Copy or New)) { throw new InvalidDataException("Unknown Text Delta operation."); }
            uint source = operation == Copy ? reader.ReadVarUInt32() : 0;
            uint count = reader.ReadVarUInt32();
            if (count == 0 || count > length - output) { throw new InvalidDataException("A Text Delta range must be positive and fit its result."); }
            if (operation == Copy) {
                if (source > sourceLength || count > sourceLength - source) { throw new InvalidDataException("A Text Copy exceeds its exact predecessor."); }
                if (priorOperation == Copy && source == priorCopyEnd) { throw new InvalidDataException("Adjacent continuous Text Copies must be merged."); }
                priorCopyEnd = (int)(source + count);
            } else {
                if (priorOperation == New) { throw new InvalidDataException("Adjacent Text New ranges must be merged."); }
                if (count > reader.RemainingCount / 2) { throw new InvalidDataException("Truncated Text New units."); }
                reader.ReadRawBytes((int)count * 2);
            }
            output += (int)count;
            priorOperation = operation;
        }
    }

    private static int ReadLength(ref BareValueReader reader) {
        uint length = reader.ReadVarUInt32();
        if (length > Array.MaxLength) { throw new InvalidDataException("Text length exceeds the supported CLR buffer length."); }
        return (int)length;
    }

    private static void ReadUnits(ReadOnlySpan<byte> bytes, Span<char> target) {
        if (BitConverter.IsLittleEndian) {
            MemoryMarshal.Cast<byte, char>(bytes).CopyTo(target);
            return;
        }
        for (int index = 0; index < target.Length; index++) {
            target[index] = (char)BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(index * 2, 2));
        }
    }

    private static string ReadString(ReadOnlySpan<byte> bytes) {
        if (BitConverter.IsLittleEndian) { return new string(MemoryMarshal.Cast<byte, char>(bytes)); }
        char[] units = new char[bytes.Length / 2];
        ReadUnits(bytes, units);
        return new string(units);
    }
}
