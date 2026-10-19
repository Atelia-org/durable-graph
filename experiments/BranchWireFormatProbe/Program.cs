using System.Buffers.Binary;
using System.Text;
using Atelia.FrameStore;
using Atelia.HeadStore;

// Candidate bytes only. Numeric IDs/addresses below do NOT prove stored object existence.
var probe = new Probe();
probe.Run();
Console.WriteLine($"PASS: {probe.Assertions} assertions; candidate codecs only, not a DG format or performance qualification.");

sealed class Probe {
    public int Assertions { get; private set; }
    private void Check(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
        Assertions++;
    }
    private void Reject(Action action, string message) {
        try { action(); } catch (InvalidDataException) { Assertions++; return; }
        throw new InvalidOperationException(message);
    }
    private void Malformed(byte[] valid, Action<byte[]> decode, string label) {
        for (int length = 0; length < valid.Length; length++) {
            int cut = length;
            Reject(() => decode(valid[..cut]), $"{label}: truncation {cut}");
        }
        Reject(() => decode([.. valid, 0]), $"{label}: trailing byte");
        byte[] old = (byte[])valid.Clone();
        old[3]--;
        Reject(() => decode(old), $"{label}: old version");
    }
    private static BranchId Branch(uint value) {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return BranchId.Read(bytes);
    }
    private static CommitId Commit(BranchId branch) {
        byte[] bytes = new byte[12];
        branch.Write().CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(4), (16UL << 26) | 16UL);
        return CommitId.Read(bytes);
    }
    private static byte[] SetU32(byte[] source, int offset, uint value) {
        byte[] result = (byte[])source.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset), value);
        return result;
    }
    public void Run() {
        BranchId a = Branch(1), b = Branch(256);
        CommitId ca = Commit(a), cb = Commit(b);
        Check(a.Write().Length == 4 && BranchId.Read(a.Write()) == a, "4B BranchId round-trip");
        Check(ca.Write().Length == 12 && CommitId.Read(ca.Write()) == ca, "12B CommitId round-trip");
        Check(CommitId.Read(ca.Write()).Value.HeadId == a.Value, "Commit owner preserved");
        Reject(() => BranchId.Read(new byte[4]), "default Branch rejected");
        Reject(() => CommitId.Read(new byte[12]), "default Commit rejected");
        Reject(() => BranchId.Read([.. a.Write(), 0]), "ID trailing rejected");
        Reject(() => CommitId.Read(ca.Write()[..11]), "Commit truncated rejected");
        Reject(() => CommitId.Read([.. ca.Write(), 0]), "Commit trailing rejected");
        Reject(() => CommitId.Read([.. a.Write(), 0, 0, 0, 0, 0, 0, 0, 0]), "zero ticket length rejected");
        Reject(() => default(BranchId).Write(), "default Branch write rejected");
        Reject(() => default(CommitId).Write(), "default Commit write rejected");
        byte[] shortBranch = [9, 9, 9], shortCommit = Enumerable.Repeat((byte)9, 11).ToArray();
        Check(!a.Value.TryWrite(shortBranch) && shortBranch.All(x => x == 9), "Branch short write leaves target unchanged");
        Check(!ca.Value.TryWrite(shortCommit) && shortCommit.All(x => x == 9), "Commit short write leaves target unchanged");
        byte[] defaultTarget = Enumerable.Repeat((byte)9, 12).ToArray();
        Check(!default(HeadId).TryWrite(defaultTarget) && defaultTarget.All(x => x == 9), "default Branch write leaves target unchanged");
        Check(!default(HeadEntryId).TryWrite(defaultTarget) && defaultTarget.All(x => x == 9), "default Commit write leaves target unchanged");

        const string label = "仙侠/捡到小绿瓶";
        byte[] directory = Wire.Directory([(a, label), (b, label)], 256);
        var members = Wire.ReadDirectory(directory, 256);
        Check(members.Length == 2 && members[0] == (b, label) && members[1] == (a, label), "duplicate Chinese labels; byte-lex ordering differs from numeric ordering");
        Check(directory.SequenceEqual(Wire.Directory([(b, label), (a, label)], 256)), "canonical directory independent of input order");
        Check(Wire.ReadDirectory(Wire.Directory([], 256), 256).Length == 0, "empty directory");
        Check(Wire.ReadDirectory(Wire.Directory([(a, " e\u0301 ")], 256), 256)[0].Label == " e\u0301 ", "label is neither trimmed nor Unicode-normalized");
        Reject(() => Wire.Directory([(a, label), (a, label)], 256), "duplicate BranchId");
        Reject(() => Wire.Directory([(a, "\uD800")], 256), "unpaired UTF16 high surrogate");
        Reject(() => Wire.Directory([(a, "\uDC00")], 256), "unpaired UTF16 low surrogate");
        Reject(() => Wire.Directory([(a, "")], 256), "empty label");
        Reject(() => Wire.Directory([(a, new string('中', 257))], 256), "label unit limit");
        string emoji = string.Concat(Enumerable.Repeat("😀", 128));
        Check(Wire.ReadDirectory(Wire.Directory([(a, emoji)], 256), 256)[0].Label == emoji, "supplementary Unicode uses two code units");
        byte[] smallD = Wire.Directory([(a, "a"), (b, "b")], 256);
        byte[] duplicateD = (byte[])smallD.Clone();
        duplicateD.AsSpan(8, 4).CopyTo(duplicateD.AsSpan(17, 4));
        Reject(() => Wire.ReadDirectory(duplicateD, 256), "duplicate persisted ID");
        byte[] reversedD = [.. smallD[..8], .. smallD[17..], .. smallD[8..17]];
        Reject(() => Wire.ReadDirectory(reversedD, 256), "noncanonical order");
        Reject(() => Wire.ReadDirectory(SetU32(smallD, 4, uint.MaxValue), 256), "unbounded count");
        Reject(() => Wire.ReadDirectory(SetU32(smallD, 12, uint.MaxValue), 256), "unbounded string bytes");
        byte[] invalidUtf8 = (byte[])smallD.Clone();
        invalidUtf8[16] = 0xFF;
        Reject(() => Wire.ReadDirectory(invalidUtf8, 256), "invalid UTF8");
        byte[] encodedSurrogate = [.. "P0R3"u8.ToArray(), 1, 0, 0, 0, .. a.Write(), 3, 0, 0, 0, 0xED, 0xA0, 0x80];
        Reject(() => Wire.ReadDirectory(encodedSurrogate, 256), "UTF8 surrogate sequence");
        byte[] overlongUtf8 = [.. "P0R3"u8.ToArray(), 1, 0, 0, 0, .. a.Write(), 2, 0, 0, 0, 0xC0, 0xAF];
        Reject(() => Wire.ReadDirectory(overlongUtf8, 256), "overlong UTF8");
        Reject(() => Wire.ReadDirectory(Wire.Directory([(a, new string('中', 257))], 1024), 256), "decoded code-unit bound");
        Malformed(smallD, bytes => Wire.ReadDirectory(bytes, 256), "D");

        byte[] tags = Wire.Tags([("b", cb), ("a", ca)]);
        Check(Wire.ReadTags(tags).SequenceEqual(new[] { ("a", ca), ("b", cb) }), "T stores exact Commit IDs in Ordinal-name order");
        Check(Wire.ReadTags(Wire.Tags([])).Length == 0, "empty tags");
        Reject(() => Wire.Tags([("a", ca), ("a", cb)]), "duplicate tag input");
        Reject(() => Wire.Tags([("\uD800", ca)]), "invalid tag Unicode");
        Reject(() => Wire.Tags([("", ca)]), "empty tag name");
        Reject(() => Wire.Tags([(new string('a', 1025), ca)]), "tag unit bound");
        byte[] duplicateT = (byte[])tags.Clone();
        duplicateT[29] = (byte)'a';
        Reject(() => Wire.ReadTags(duplicateT), "duplicate persisted tag");
        Reject(() => Wire.ReadTags([.. tags[..8], .. tags[25..], .. tags[8..25]]), "noncanonical tag order");
        Reject(() => Wire.ReadTags(SetU32(tags, 4, uint.MaxValue)), "tag count bound");
        byte[] badT = (byte[])tags.Clone();
        badT[12] = 0xFF;
        Reject(() => Wire.ReadTags(badT), "invalid tag UTF8");
        Reject(() => Wire.Tags(Enumerable.Range(0, 400).Select(i => (new string('中', 1020) + i.ToString("D4"), ca))), "T total length bound");
        Malformed(tags, bytes => Wire.ReadTags(bytes), "T");

        // FrameAddress validates numeric shape only; this probe never opens a store.
        Check(FrameAddress.TryRead(ca.Write(), out FrameAddress graph), "synthetic FrameAddress numeric shape accepted");
        byte[] input = [7, 8];
        var entry = new Entry(graph, input);
        input[0] = 0;
        Check(entry.CopySummary()[0] == 7, "entry captures caller bytes");
        byte[] returned = entry.CopySummary();
        returned[0] = 0;
        Check(entry.CopySummary()[0] == 7, "entry does not expose retained array");
        byte[] encoded = Wire.Entry(entry);
        Entry decoded = Wire.ReadEntry(encoded);
        encoded[^1] = 0;
        Check(decoded.Graph == graph && decoded.CopySummary().SequenceEqual(new byte[] { 7, 8 }), "decoder owns summary independently from encoded bytes");
        Check(Wire.ReadEntry(Wire.Entry(new Entry(graph, []))).CopySummary().Length == 0, "empty summary valid");
        Reject(() => Wire.Entry(new Entry(default, [])), "invalid graph address rejected");
        Malformed(Wire.Entry(entry), bytes => Wire.ReadEntry(bytes), "Entry");
        Reject(() => Wire.ReadEntry(SetU32(Wire.Entry(entry), 16, uint.MaxValue)), "summary length overflow rejected");
        int maxSummary = HeadStore.MaxHeadEntryPayloadLength - Wire.EntryOverhead;
        byte[] maximum = Wire.Entry(new Entry(graph, new byte[maxSummary]));
        Check(maximum.Length == HeadStore.MaxHeadEntryPayloadLength && Wire.ReadEntry(maximum).CopySummary().Length == maxSummary, "exact HS payload maximum");
        Reject(() => Wire.Entry(new Entry(graph, new byte[maxSummary + 1])), "one byte beyond HS payload maximum");
        Reject(() => Wire.ReadEntry([.. maximum, 0]), "oversized encoded entry");
        Console.WriteLine($"HeadStore payload limit = {HeadStore.MaxHeadEntryPayloadLength}; Entry overhead = {Wire.EntryOverhead}; max summary = {maxSummary} bytes.");

        foreach (int units in new[] { 32, 64, 256, 1024 }) {
            foreach (char character in new[] { 'a', '中' }) {
                string text = new(character, units);
                int textBytes = Encoding.UTF8.GetByteCount(text);
                int length = checked(8 + 1000 * (8 + textBytes));
                var rows = Enumerable.Range(1, 1000).Select(i => (Branch((uint)i), text));
                if (length <= HeadStore.MaxHeadEntryPayloadLength) {
                    byte[] bytes = Wire.Directory(rows, units);
                    Check(bytes.Length == length && Wire.ReadDirectory(bytes, units).Length == 1000, "1000-branch size and round-trip");
                } else {
                    Reject(() => Wire.Directory(rows, units), "1000-branch total bound");
                }
                Console.WriteLine($"1000 branches: {units} units of {character}, {textBytes} bytes/label, D={length}, fits={length <= HeadStore.MaxHeadEntryPayloadLength}, equal-label capacity={(HeadStore.MaxHeadEntryPayloadLength - 8) / (8 + textBytes)}.");
            }
        }
        // 256 is an optional product policy, not a count limit: smaller labels allow >1000 members.
        Check(Wire.ReadDirectory(Wire.Directory(Enumerable.Range(1, 1001).Select(i => (Branch((uint)i), "中")), 256), 256).Length == 1001, "no artificial 1000-member cap");
    }
}

readonly record struct BranchId(HeadId Value) {
    public byte[] Write() { byte[] b = new byte[HeadId.EncodedSize]; if (!Value.TryWrite(b)) { throw new InvalidDataException(); } return b; }
    public static BranchId Read(ReadOnlySpan<byte> b) => HeadId.TryRead(b, out var id) ? new(id) : throw new InvalidDataException();
}
readonly record struct CommitId(HeadEntryId Value) {
    public byte[] Write() { byte[] b = new byte[HeadEntryId.EncodedSize]; if (!Value.TryWrite(b)) { throw new InvalidDataException(); } return b; }
    public static CommitId Read(ReadOnlySpan<byte> b) => HeadEntryId.TryRead(b, out var id) ? new(id) : throw new InvalidDataException();
}
sealed class Entry(FrameAddress graph, ReadOnlySpan<byte> summary) {
    private readonly byte[] _summary = summary.ToArray();
    public FrameAddress Graph { get; } = graph;
    public byte[] CopySummary() => (byte[])_summary.Clone();
}

// Simple candidate codec, intentionally separate from product types and history policy.
static class Wire {
    public const int EntryOverhead = 4 + FrameAddress.EncodedSize + 4;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static byte[] Text(string text, int maxUnits) {
        if (text.Length is < 1 || text.Length > maxUnits) { throw new InvalidDataException("Text code-unit limit."); }
        try { return Utf8.GetBytes(text); } catch (EncoderFallbackException e) { throw new InvalidDataException("Invalid UTF16.", e); }
    }
    private static byte[] Finish(MemoryStream stream) {
        if (stream.Length > HeadStore.MaxHeadEntryPayloadLength) { throw new InvalidDataException("HS payload limit."); }
        return stream.ToArray();
    }
    private static void U32(Stream s, int value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, checked((uint)value)); s.Write(b); }
    public static byte[] Directory(IEnumerable<(BranchId Id, string Label)> input, int maxUnits) {
        var rows = input.Select(x => (Id: x.Id.Write(), Text: Text(x.Label, maxUnits))).ToArray();
        Array.Sort(rows, (x, y) => x.Id.AsSpan().SequenceCompareTo(y.Id));
        if (8L + rows.Sum(x => 8L + x.Text.Length) > HeadStore.MaxHeadEntryPayloadLength) { throw new InvalidDataException("HS payload limit."); }
        using var s = new MemoryStream();
        s.Write("P0R3"u8); U32(s, rows.Length);
        for (int i = 0; i < rows.Length; i++) {
            if (i > 0 && rows[i - 1].Id.AsSpan().SequenceEqual(rows[i].Id)) { throw new InvalidDataException("Duplicate branch."); }
            s.Write(rows[i].Id); U32(s, rows[i].Text.Length); s.Write(rows[i].Text);
        }
        return Finish(s);
    }
    public static (BranchId Id, string Label)[] ReadDirectory(byte[] bytes, int maxUnits) {
        var r = new Reader(bytes, "P0R3"u8);
        int count = r.Count(9);
        var rows = new (BranchId, string)[count];
        ReadOnlySpan<byte> previous = [];
        for (int i = 0; i < count; i++) {
            ReadOnlySpan<byte> id = r.Take(4);
            if (i > 0 && previous.SequenceCompareTo(id) >= 0) { throw new InvalidDataException("Unsorted or duplicate branch."); }
            rows[i] = (BranchId.Read(id), r.Text(maxUnits)); previous = id;
        }
        r.End(); return rows;
    }
    public static byte[] Tags(IEnumerable<(string Name, CommitId Id)> input) {
        var rows = input.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
        long total = 8;
        foreach (var row in rows) { total += 16 + Text(row.Name, 1024).Length; }
        if (total > HeadStore.MaxHeadEntryPayloadLength) { throw new InvalidDataException("HS payload limit."); }
        using var s = new MemoryStream();
        s.Write("P0T2"u8); U32(s, rows.Length);
        for (int i = 0; i < rows.Length; i++) {
            if (i > 0 && rows[i - 1].Name == rows[i].Name) { throw new InvalidDataException("Duplicate tag."); }
            byte[] name = Text(rows[i].Name, 1024);
            U32(s, name.Length); s.Write(name); s.Write(rows[i].Id.Write());
        }
        return Finish(s);
    }
    public static (string Name, CommitId Id)[] ReadTags(byte[] bytes) {
        var r = new Reader(bytes, "P0T2"u8);
        int count = r.Count(17);
        var rows = new (string Name, CommitId Id)[count];
        for (int i = 0; i < count; i++) {
            string name = r.Text(1024);
            if (i > 0 && StringComparer.Ordinal.Compare(rows[i - 1].Name, name) >= 0) { throw new InvalidDataException("Unsorted or duplicate tag."); }
            rows[i] = (name, CommitId.Read(r.Take(12)));
        }
        r.End(); return rows;
    }
    public static byte[] Entry(Entry entry) {
        byte[] summary = entry.CopySummary();
        if (summary.Length > HeadStore.MaxHeadEntryPayloadLength - EntryOverhead) { throw new InvalidDataException("HS payload limit."); }
        byte[] bytes = new byte[EntryOverhead + summary.Length];
        "P0E1"u8.CopyTo(bytes);
        if (!entry.Graph.TryWrite(bytes.AsSpan(4, 12))) { throw new InvalidDataException("Invalid graph address."); }
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), checked((uint)summary.Length));
        summary.CopyTo(bytes, EntryOverhead); return bytes;
    }
    public static Entry ReadEntry(byte[] bytes) {
        var r = new Reader(bytes, "P0E1"u8);
        if (!FrameAddress.TryRead(r.Take(12), out var graph)) { throw new InvalidDataException("Invalid graph address."); }
        var entry = new Entry(graph, r.Take(r.Length())); r.End(); return entry;
    }
    private ref struct Reader {
        private ReadOnlySpan<byte> _remaining;
        public Reader(ReadOnlySpan<byte> input, ReadOnlySpan<byte> magic) {
            if (input.Length > HeadStore.MaxHeadEntryPayloadLength || !input.StartsWith(magic)) { throw new InvalidDataException("Length or version."); }
            _remaining = input[magic.Length..];
        }
        public ReadOnlySpan<byte> Take(int count) {
            if (count < 0 || count > _remaining.Length) { throw new InvalidDataException("Truncated."); }
            var value = _remaining[..count]; _remaining = _remaining[count..]; return value;
        }
        public int Length() {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
            if (value > int.MaxValue) { throw new InvalidDataException("Length overflow."); }
            return (int)value;
        }
        public int Count(int minItemBytes) {
            int count = Length();
            if (count > _remaining.Length / minItemBytes) { throw new InvalidDataException("Count exceeds payload."); }
            return count;
        }
        public string Text(int maxUnits) {
            int bytes = Length();
            if (bytes > 3 * maxUnits) { throw new InvalidDataException("Text byte bound."); }
            string value;
            try { value = Utf8.GetString(Take(bytes)); } catch (DecoderFallbackException e) { throw new InvalidDataException("Invalid UTF8.", e); }
            if (value.Length is < 1 || value.Length > maxUnits) { throw new InvalidDataException("Text unit bound."); }
            return value;
        }
        public void End() { if (!_remaining.IsEmpty) { throw new InvalidDataException("Trailing bytes."); } }
    }
}
