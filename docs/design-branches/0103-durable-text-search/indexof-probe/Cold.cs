using System.Diagnostics;
using System.Security.Cryptography;
using Atelia.Text;

internal static partial class Corpus {
    private readonly record struct ColdCase(Task Task, int Percent, bool Large = false, int Start = 0, bool OneEdit = false) {
        public int Count => Large ? 16 : Documents;
        public int Length => Large ? 1 << 20 : Units;
        public string Name => Large ? (Start == 0 ? "LargeEarly" : "LargeTail") : Task.ToString();
        public int EditCount => OneEdit ? 1 : Count * Percent / 100;
        public bool Edited(int index) => OneEdit ? index == 0 : Percent == 100 || index / 10 % 5 == 0;
    }

    private readonly record struct ColdSample(long Ticks, long Bytes, int Gen0, int Gen1, int Gen2, long Checksum);

    public static void RunCold(string? outputFile, int rep) {
        var cases = (from percent in new[] { 20, 100 }
                     from task in Enum.GetValues<Task>()
                     select new ColdCase(task, percent)).ToList();
        cases.Add(new(Task.EarlyFirst, 100, Large: true));
        cases.Add(new(Task.EarlyFirst, 100, Large: true, Start: (1 << 20) - 64));
        cases.Add(new(Task.SpreadEight, 100, OneEdit: true));
        using var output = outputFile is null ? null :
            new StreamWriter(new FileStream(outputFile, FileMode.CreateNew, FileAccess.Write));
        output?.WriteLine("rep,length,documents,fragmented_percent,edit_target_documents,edited_documents,task,start,route,block,slot,stage,ticks,frequency,allocated_bytes,gen0,gen1,gen2,ms_per_scan,bytes_per_scan,checksum,runtime,dll_sha256");
        string sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(TextEditor).Assembly.Location)));
        Route[] routes = [Route.HeldSnapshot, Route.VisitOnce];
        if (output is not null) {
            // Compile the timer, both route implementations and post-edit validation on disposable fixtures.
            foreach (var sample in new[] { cases[0], cases.First(c => c.Large) }) {
                foreach (var route in routes) { Trial(sample, route, collect: false); }
            }
        }

        var ordered = cases.ToArray();
        new Random(8910 + rep).Shuffle(ordered);
        for (int index = 0; index < ordered.Length; index++) {
            var c = ordered[index];
            for (int block = 0; block < (output is null ? 1 : 2); block++) {
                for (int slot = 0; slot < routes.Length; slot++) {
                    var route = routes[(index + rep + block + slot) % routes.Length];
                    var samples = Trial(c, route, collect: output is not null);
                    for (int stage = 0; output is not null && stage < samples.Length; stage++) {
                        var s = samples[stage];
                        string phase = stage switch { 0 => "first", 1 => "second", _ => "after-edit" };
                        int edited = stage == 2 ? c.EditCount : 0;
                        double ms = s.Ticks * 1000.0 / Stopwatch.Frequency;
                        output.WriteLine($"{rep},{c.Length},{c.Count},{c.Percent},{c.EditCount},{edited},{c.Name},{c.Start},{route},{block},{slot},{phase},{s.Ticks},{Stopwatch.Frequency},{s.Bytes},{s.Gen0},{s.Gen1},{s.Gen2},{ms:F6},{s.Bytes},{s.Checksum},{Environment.Version},{sha}");
                    }
                }
            }
        }
        Console.WriteLine($"Cold timelines verified for {cases.Count} fixtures and two routes. Product {sha}");
    }

    private static ColdSample[] Trial(ColdCase c, Route route, bool collect) {
        string[] sources = new string[c.Count];
        Fixture fixture = c.Large ? LargeFixture(c, sources) : MakeFixture(c.Percent, c.Task, sources);
        if (collect) { CollectSetupGarbage(); }
        var first = Measure(fixture, c, route, fixture.Expected);
        var second = Measure(fixture, c, route, fixture.Expected);
        long expected = Edit(fixture, c, sources);
        Check(expected, fixture.Expected + 1);
        if (collect) { CollectSetupGarbage(); }
        var edited = Measure(fixture, c, route, expected);
        return [first, second, edited];
    }

    private static void CollectSetupGarbage() =>
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);

    private static ColdSample Measure(Fixture fixture, ColdCase c, Route route, long expected) {
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        long sum = c.Large ? ScanLarge(fixture, c.Start, route) : Scan(fixture, route);
        long ticks = Stopwatch.GetTimestamp() - start;
        bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        Check(sum, expected);
        return new(ticks, bytes, GC.CollectionCount(0) - g0, GC.CollectionCount(1) - g1, GC.CollectionCount(2) - g2, sum);
    }

    private static Fixture LargeFixture(ColdCase c, string[] sources) {
        var docs = new Document[c.Count];
        var query = new Query(Needle, 1);
        long expected = 0;
        for (int i = 0; i < docs.Length; i++) {
            int at = c.Start == 0 ? 8 + i % 8 : c.Length - 32 + i % 8;
            string source = string.Create(c.Length, at, static (span, hit) => {
                span.Fill('中');
                Needle.AsSpan().CopyTo(span[hit..]);
            });
            var text = new TextEditor(source);
            for (int k = 1; k <= 4; k++) { text.Replace(k * c.Length / 5, 1, "中"); }
            docs[i] = new(text, text.Snapshot());
            sources[i] = source;
            Check(source.IndexOf(Needle, c.Start, StringComparison.Ordinal), at);
            expected += (i + 1L) * (at + 1);
        }
        return new(docs, query, expected);
    }

    private static int LargeVisit(ReadOnlySpan<char> content, int start) {
        int at = content[start..].IndexOf(Needle, StringComparison.Ordinal);
        return at < 0 ? -1 : at + start;
    }

    private static long ScanLarge(Fixture fixture, int start, Route route) {
        long score = 0;
        for (int i = 0; i < fixture.Docs.Length; i++) {
            var doc = fixture.Docs[i];
            int at = route == Route.VisitOnce ? doc.Text.VisitSpan(start, LargeVisit) : doc.Snapshot.IndexOf(Needle, start);
            score += (i + 1L) * (at + 1);
        }
        return score;
    }

    private static long Edit(Fixture fixture, ColdCase c, string[] sources) {
        long expected = 0;
        for (int i = 0; i < fixture.Docs.Length; i++) {
            string source = sources[i];
            if (c.Edited(i)) {
                // Move the first document's first match one unit right. Preserve hit counts and
                // use existing one-unit pieces for all other edits, skipping a matching range.
                int position;
                string replacement;
                if (i == 0) {
                    position = source.IndexOf(Needle, c.Start, StringComparison.Ordinal);
                    replacement = "◇" + Needle;
                } else {
                    position = c.Large ? c.Length / 5 : 701;
                    if (source.AsSpan(position - Needle.Length + 1, 2 * Needle.Length - 1)
                        .Contains(Needle, StringComparison.Ordinal)) { position = 1902; }
                    replacement = "◇";
                }
                var doc = fixture.Docs[i];
                doc.Text.Replace(position, replacement.Length, replacement);
                fixture.Docs[i] = new(doc.Text, doc.Text.Snapshot());
                source = string.Concat(source.AsSpan(0, position), replacement, source.AsSpan(position + replacement.Length));
            }
            int start = c.Start;
            for (int h = 0; h < fixture.Query.Limit; h++) {
                int at = source.IndexOf(Needle, start, StringComparison.Ordinal);
                if (at < 0) { break; }
                expected += (i + 1L) * (at + 1);
                start = at + Needle.Length;
            }
        }
        return expected;
    }
}
