#:package Atelia.Binary@0.1.3-dev.20261101.g9ad39fc
using System.Buffers;
using System.Buffers.Binary;
using System.Text.Unicode;
using Atelia.Binary;

// Standalone .NET 10 mechanism probe. The extension below is the proposed reader.
static void Require(bool condition) {
    if (!condition) throw new InvalidOperationException("Probe mismatch.");
}

static byte[] Encode(string[] pieces) {
    ArrayBufferWriter<byte> output = new();
    BareValueWriter writer = new(output);
    writer.WriteVarUInt32((uint)pieces.Sum(piece => piece.Length));
    foreach (string piece in pieces) writer.WriteString(piece);
    return output.WrittenSpan.ToArray();
}

static string Decode(ReadOnlySpan<byte> body, bool direct) {
    BareValueReader reader = new(body);
    uint length = reader.ReadVarUInt32();
    if (length > Array.MaxLength || length > reader.RemainingCount) throw new InvalidDataException();
    if (length == 0) {
        reader.EnsureFullyConsumed(); // string.Create(0, ...) skips the callback.
        return string.Empty;
    }
    return direct
        ? string.Create((int)length, reader, static (chars, cursor) => Fill(chars, cursor, true))
        : string.Create((int)length, reader, static (chars, cursor) => Fill(chars, cursor, false));
}

static void Fill(Span<char> remaining, BareValueReader reader, bool direct) {
    while (!reader.End) {
        int count;
        if (direct) {
            try { count = reader.ReadString(remaining); }
            catch (ArgumentException ex) when (ex.ParamName == "destination") {
                throw new InvalidDataException("Segment exceeds the declared Text length.", ex);
            }
        } else {
            string piece = reader.ReadString();
            if (!piece.AsSpan().TryCopyTo(remaining)) throw new InvalidDataException();
            count = piece.Length;
        }
        if (count == 0) throw new InvalidDataException("Empty segment.");
        remaining = remaining[count..];
    }
    if (!remaining.IsEmpty) throw new InvalidDataException("Short Text.");
}

string[][] cases = [[], ["A"], ["A", "中", "\0\r\n"], ["abc\U0001F600def", "é"],
    ["\uD83D", "\uDE00"], ["\uDC00x\uD800", "\uFEFF"], [new string('界', 16384)]];
foreach (var pieces in cases) Require(Decode(Encode(pieces), true) == string.Concat(pieces));
Require(Encode(["AB"]).AsSpan().SequenceEqual(new byte[] { 2, 5, 65, 66 }));
Require(Encode(["A", "B"]).AsSpan().SequenceEqual(new byte[] { 2, 3, 65, 3, 66 }));
// Legal alternate UTF-8 and UTF-16 representations with nonminimal headers.
Require(Decode([2, 0x87, 0, 0xE4, 0xB8, 0xAD, 0x82, 0, 65, 0], true) == "中A");
byte[][] invalid = [[], [0, 0], [1, 0], [1, 1], [1, 3], [2, 3, 65],
    [1, 3, 65, 3, 66], [1, 3, 0xFF], [1, 5, 0xC0, 0x80], [1, 7, 0xED, 0xA0, 0x80],
    [1, 5, 0xE2, 0x82], [1, 0x80], [0xFF, 0xFF, 0xFF, 0xFF, 0x0F]];
foreach (byte[] bytes in invalid) {
    try { Decode(bytes, true); }
    catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException) { continue; }
    throw new InvalidOperationException("Accepted malformed body.");
}
// Two sequential calls, nonzero reader offset, and an oversized destination with guard chars.
var cursor = new BareValueReader([0xAA, 3, 65, 2, 0x00, 0xD8]);
cursor.ReadByte();
char[] buffer = ['!', '!', '!', '!'];
Require(cursor.ReadString(buffer.AsSpan(1, 2)) == 1 && cursor.ConsumedCount == 3);
Require(cursor.ReadString(buffer.AsSpan(2, 1)) == 1 && cursor.End);
Require(buffer.AsSpan().SequenceEqual("!A\uD800!"));
foreach ((byte[] bytes, int capacity, int limit, Type error) in new (byte[], int, int, Type)[] {
    ([5,65,66], 1, 2, typeof(ArgumentException)), ([4,65,0,66,0], 1, 4, typeof(ArgumentException)),
    ([5,65,0xFF], 3, 2, typeof(InvalidDataException)), ([5,65], 2, 2, typeof(EndOfStreamException)),
    ([9,0xF0,0x9F,0x98,0x80], 1, 4, typeof(ArgumentException)),
    ([3,65], 1, 0, typeof(InvalidDataException)), ([3,65], 1, -1, typeof(ArgumentOutOfRangeException))
}) {
    var test = new BareValueReader(new byte[] { 0xAA }.Concat(bytes).ToArray());
    test.ReadByte();
    try { test.ReadString(new char[capacity], limit); }
    catch (Exception ex) when (ex.GetType() == error && test.ConsumedCount == 1) { continue; }
    throw new InvalidOperationException("Wrong error or reader position.");
}
var empty = new BareValueReader([0]);
Require(empty.ReadString(Span<char>.Empty) == 0 && empty.End);

static long Allocated(Func<string> action, string expected) {
    for (int i = 0; i < 8; i++) Require(action() == expected);
    long start = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < 32; i++) Require(action() == expected);
    return (GC.GetAllocatedBytesForCurrentThread() - start) / 32;
}
string unit = "a中\U0001F600";
string expected = string.Concat(Enumerable.Repeat(unit, 1024));
foreach (int width in new int[] { 1, 4, 64 }) {
    string[] pieces = Enumerable.Range(0, expected.Length / width)
        .Select(i => expected.Substring(i * width, width)).ToArray();
    byte[] body = Encode(pieces);
    long directBytes = Allocated(() => Decode(body, true), expected);
    long allocatingReaderBytes = Allocated(() => Decode(body, false), expected);
    long finalStringBytes = Allocated(() => new string(expected.AsSpan()), expected);
    Require(directBytes == finalStringBytes && directBytes < allocatingReaderBytes);
    Console.WriteLine($"N={expected.Length} pieces={pieces.Length} direct={directBytes}" +
        $" allocatingReader={allocatingReaderBytes} finalStringOnly={finalStringBytes} B/read");
}
Console.WriteLine($"PASS; runtime {Environment.Version}");

internal static class SpanReaderProbe {
    public static int ReadString(this ref BareValueReader reader, scoped Span<char> destination,
        int maxPayloadByteCount = int.MaxValue) {
        ArgumentOutOfRangeException.ThrowIfNegative(maxPayloadByteCount);
        var trial = reader;
        uint header = trial.ReadVarUInt32();
        bool utf8 = (header & 1) != 0;
        uint bytes = utf8 ? header >> 1 : header;
        if (bytes > (uint)maxPayloadByteCount) throw new InvalidDataException();
        var payload = trial.ReadRawBytes((int)bytes);
        int count;
        if (utf8) {
            var status = Utf8.ToUtf16(payload, destination, out int consumed, out count,
                replaceInvalidSequences: false, isFinalBlock: true);
            if (status == OperationStatus.DestinationTooSmall) throw new ArgumentException("Short span.", nameof(destination));
            if (status != OperationStatus.Done || consumed != payload.Length) throw new InvalidDataException("Invalid UTF-8.");
        } else {
            count = payload.Length / 2;
            if (count > destination.Length) throw new ArgumentException("Short span.", nameof(destination));
            for (int i = 0; i < count; i++) {
                destination[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(i * 2, 2));
            }
        }
        reader = trial;
        return count;
    }
}
