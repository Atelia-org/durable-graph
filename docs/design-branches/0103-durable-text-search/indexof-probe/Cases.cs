using System.Reflection;
using System.Runtime.InteropServices;
using Atelia.DurableGraph;

internal sealed record SearchCase(int Length, string Shape, string Name, string Needle, int Start, int Expected, char Fill = '中') {
    public int EditCount => Shape switch { "edit9" => 4, "edit129" => 64, _ => 0 };

    public static IEnumerable<SearchCase> All() {
        const string word = "北门仓库";
        foreach (int length in new[] { 4096, 65536, 1048576 }) {
            int late = length - 32;
            foreach (string shape in new[] { "single", "slice", "edit9", "edit129" }) {
                yield return new(length, shape, "early", word, 0, 8);
                yield return new(length, shape, "late", word, 0, late);
                yield return new(length, shape, "miss", word, 0, -1);
                yield return new(length, shape, "tail-start", word, length - 64, late);
            }
            foreach (string shape in new[] { "edit9", "edit129" }) {
                foreach (int divisor in new[] { 256, 128, 64, 32, 16, 8, 4, 2 }) {
                    yield return new(length, shape, $"at-1-of-{divisor}", word, 0, length / divisor);
                }
                int firstBoundary = length / (shape == "edit9" ? 5 : 65);
                yield return new(length, shape, "cross-first-edit", word, 0, firstBoundary - 2);
                yield return new(length, shape, "cross-window-offset", word, 17, 17 + 1024 - 2);
                yield return new(length, shape, "tail-miss", word, length - 64, -1);
            }
            foreach (string shape in new[] { "single", "edit9", "edit129" }) {
                yield return new(length, shape, "char-early", "北", 0, 8);
                yield return new(length, shape, "char-miss", "北", 0, -1);
                yield return new(length, shape, "prefix-late", "aaaaab", 0, length - 6, 'a');
            }

            string longRare = "北" + new string('中', 254) + "南";
            yield return new(length, "edit9", "rare-256-late", longRare, 0, length - 256);
            // Many positions satisfy both endpoints; only the interior character disproves the match.
            foreach (int width in new[] { 32, 256, 1024 }) {
                char[] almost = Enumerable.Range(0, width).Select(i => i % 2 == 0 ? 'a' : 'b').ToArray();
                almost[^3] = 'X';
                foreach (string shape in new[] { "single", "edit9" }) {
                    yield return new(length, shape, $"periodic-{width}-miss", new string(almost), 0, -1, 'p');
                }
            }
        }
    }

    public static IEnumerable<SearchCase> ShortPatterns() {
        foreach (int length in new[] { 256, 4096, 65536 }) {
            foreach (string shape in new[] { "single", "edit9" }) {
                foreach (int width in new[] { 4, 8, 16 }) {
                    char[] period = Enumerable.Range(0, width).Select(i => i % 2 == 0 ? 'a' : 'b').ToArray();
                    foreach (int mismatch in new[] { 0, width - 3 }) {
                        char[] needle = (char[])period.Clone();
                        needle[mismatch] = 'X';
                        yield return new(length, shape, $"periodic-{width}-mismatch-at-{mismatch}",
                            new string(needle), 0, -1, 'p');
                    }
                }
                if (length == 256) {
                    yield return new(length, shape, "early", "北门仓库", 0, 8);
                    yield return new(length, shape, "miss", "北门仓库", 0, -1);
                }
            }
        }
    }

    public (DurableText Text, TextSnapshot Snapshot, string Source, int Pieces) Create() {
        const string filler = "记事山林河流。月色入窗，人们踏上归途。\r\n";
        char[] chars = new char[Length];
        for (int i = 0; i < chars.Length; i++) {
            chars[i] = Fill switch { 'p' => "ab"[i % 2], 'a' => 'a', _ => filler[i % filler.Length] };
        }
        if (Expected >= 0) { Needle.AsSpan().CopyTo(chars.AsSpan(Expected)); }
        string source = new(chars);
        if (source.IndexOf(Needle, Start, StringComparison.Ordinal) != Expected) { throw new Exception($"Fixture hit: {this}"); }

        int edits = EditCount;
        DurableText text;
        List<int> lengths = [];
        if (Shape == "slice") {
            text = new DurableText("!" + source + "?");
            text.ApplyEdits([new(0, 1, ""), new(Length + 1, 1, "")]);
            lengths.Add(Length);
        } else {
            text = new DurableText(source);
            var replacements = new TextEdit[edits];
            int cursor = 0;
            for (int i = 0; i < edits; i++) {
                int offset = (i + 1) * Length / (edits + 1);
                replacements[i] = new(offset, 1, source[offset].ToString());
                lengths.Add(offset - cursor);
                lengths.Add(1);
                cursor = offset + 1;
            }
            lengths.Add(Length - cursor);
            if (edits > 0) { text.ApplyEdits(replacements); }
        }
        TextSnapshot snapshot = text.Snapshot();
        // Shape inspection is outside all timed regions. Both measured calls use public methods.
        var chunks = (IEnumerable<ReadOnlyMemory<char>>)typeof(TextSnapshot)
            .GetMethod("GetChunks", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(snapshot, null)!;
        var actual = chunks.ToArray();
        if (!lengths.SequenceEqual(actual.Select(c => c.Length)) || snapshot.ToString() != source) {
            throw new Exception($"Unexpected chunks: {this}");
        }
        if (Shape == "slice" && !(MemoryMarshal.TryGetString(actual[0], out _, out int sliceOffset, out _) && sliceOffset == 1)) {
            throw new Exception("Nonzero slice was lost");
        }
        return (text, snapshot, source, actual.Length);
    }
}
