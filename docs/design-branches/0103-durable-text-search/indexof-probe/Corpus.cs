using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Atelia.DurableGraph;

internal static class Corpus {
    private const int Documents = 1_000;
    private const int Units = 4_000;
    private const string Needle = "北门仓库";
    private const string Background = "清点仓库后回到南门。有人在北门等人，另有人往西街找钥匙。清晨的风吹过屋檐。";
    private enum Route { HeldSnapshot, FreshSnapshot, VisitOnce, VisitPerFind }
    private enum Task { RareFirst, EarlyFirst, SpreadEight }
    private readonly record struct Query(string Value, int Limit);
    private sealed record Document(DurableText Text, TextSnapshot Snapshot);
    private sealed record Fixture(Document[] Docs, Query Query, long Expected);

    public static void Run(string outputFile, int rep) {
        using var output = new StreamWriter(new FileStream(outputFile, FileMode.CreateNew, FileAccess.Write));
        var dll = typeof(DurableText).Assembly.Location;
        string sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll)));
        output.WriteLine("rep,fragmented_percent,task,route,batch,scans,ms_per_scan,bytes_per_scan,checksum,runtime,dll_sha256");
        var cases = (from percent in new[] { 20, 100 } from task in Enum.GetValues<Task>() select (percent, task)).ToArray();
        foreach (var (percent, task) in cases.Skip(rep % cases.Length).Concat(cases.Take(rep % cases.Length))) {
            Fixture fixture = MakeFixture(percent, task);
            var routes = Enum.GetValues<Route>();
            foreach (var route in routes) { Check(Scan(fixture, route), fixture.Expected); }
            // Compare complete 1,000-document queries. Each route occupies each order slot in one process.
            for (int batch = 0; batch < routes.Length; batch++) {
                for (int slot = 0; slot < routes.Length; slot++) {
                    var route = routes[(slot + rep + batch) % routes.Length];
                    const int scans = 3;
                    long sum = 0;
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    long start = Stopwatch.GetTimestamp();
                    for (int i = 0; i < scans; i++) { sum += Scan(fixture, route); }
                    var elapsed = Stopwatch.GetElapsedTime(start);
                    long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                    Check(sum, fixture.Expected * scans);
                    output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"{rep},{percent},{task},{route},{batch},{scans},{elapsed.TotalMilliseconds / scans:F6},{(double)bytes / scans:F2},{fixture.Expected},{Environment.Version},{sha}"));
                }
            }
        }
    }

    private static Fixture MakeFixture(int percent, Task task) {
        var docs = new Document[Documents];
        var query = new Query(Needle, task == Task.SpreadEight ? 8 : 1);
        long expected = 0;
        for (int i = 0; i < Documents; i++) {
            char[] data = new char[Units];
            for (int j = 0; j < Units; j++) { data[j] = Background[(i + j) % Background.Length]; }
            int written = task switch { Task.RareFirst => i % 10 == 0 ? 1 : 0, Task.EarlyFirst => 1, _ => 16 };
            for (int h = 0; h < written; h++) {
                int pos = task switch {
                    Task.RareFirst => 10 + i * 37 % 3950,
                    Task.EarlyFirst => 4 + i % 25,
                    _ => 17 + h * 242 + i % 31
                };
                Needle.CopyTo(0, data, pos, Needle.Length);
            }
            string source = new(data);
            DurableText text = new(source);
            // Four isolated, equal-length replacements leave four one-unit pieces and five source slices.
            if (percent == 100 || i / 10 % 5 == 0) {
                foreach (var pos in new[] { 19, 701, 1_902, 3_104 }) { text.Replace(pos, 1, new string(data[pos], 1)); }
            }
            docs[i] = new(text, text.Snapshot());

            int found = 0;
            int score = 0;
            int start = 0;
            while (found < query.Limit) {
                int at = source.IndexOf(query.Value, start, StringComparison.Ordinal);
                if (at < 0) { break; }
                score += at + 1;
                start = at + query.Value.Length;
                found++;
            }
            Check(found, Math.Min(written, query.Limit));
            expected += (i + 1L) * score;
        }
        return new(docs, query, expected);
    }

    private static long Scan(Fixture fixture, Route route) {
        long sum = 0;
        for (int i = 0; i < fixture.Docs.Length; i++) {
            Document doc = fixture.Docs[i];
            int value = route switch {
                Route.HeldSnapshot => Find(doc.Snapshot, fixture.Query),
                Route.FreshSnapshot => Find(doc.Text.Snapshot(), fixture.Query),
                Route.VisitOnce => doc.Text.VisitSpan(fixture.Query, Find),
                Route.VisitPerFind => SeparateVisits(doc.Text, fixture.Query),
                _ => throw new ArgumentOutOfRangeException(nameof(route))
            };
            sum += (i + 1L) * value;
        }
        return sum;
    }

    private static int Find(TextSnapshot snapshot, Query query) {
        int score = 0;
        int start = 0;
        for (int n = 0; n < query.Limit; n++) {
            int at = snapshot.IndexOf(query.Value, start);
            if (at < 0) { break; }
            score += at + 1;
            start = at + query.Value.Length;
        }
        return score;
    }

    private static int Find(ReadOnlySpan<char> span, Query query) {
        int score = 0;
        int start = 0;
        for (int n = 0; n < query.Limit; n++) {
            int index = span[start..].IndexOf(query.Value, StringComparison.Ordinal);
            if (index < 0) { break; }
            int at = start + index;
            score += at + 1;
            start = at + query.Value.Length;
        }
        return score;
    }

    private static int SeparateVisits(DurableText text, Query query) {
        int score = 0;
        int start = 0;
        for (int n = 0; n < query.Limit; n++) {
            int index = text.VisitSpan((query.Value, start), static (s, q) => s[q.start..].IndexOf(q.Value, StringComparison.Ordinal));
            if (index < 0) { break; }
            int at = start + index;
            score += at + 1;
            start = at + query.Value.Length;
        }
        return score;
    }

    private static void Check(long actual, long expected) {
        if (actual != expected) { throw new InvalidOperationException($"Expected {expected}, got {actual}."); }
    }
}
