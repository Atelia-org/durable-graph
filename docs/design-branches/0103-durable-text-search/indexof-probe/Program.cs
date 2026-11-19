using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Security.Cryptography;
using Atelia.DurableGraph;

internal static class Program {
    private readonly record struct Query(string Needle, int Start);
    private readonly record struct Sample(long Ticks, long Bytes);

    private static int Visit(DurableText text, string needle, int start) {
        ArgumentNullException.ThrowIfNull(needle);
        if ((uint)start > (uint)text.Length) { throw new ArgumentOutOfRangeException(nameof(start)); }
        if (needle.Length == 0) { return start; }
        if (needle.Length > text.Length - start) { return -1; }
        return text.VisitSpan(new Query(needle, start), static (span, q) => {
            int relative = span[q.Start..].IndexOf(q.Needle, StringComparison.Ordinal);
            return relative < 0 ? -1 : q.Start + relative;
        });
    }

    private static Sample Batch(Func<int> search, int expected, int repetitions) {
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < repetitions; i++) {
            if (search() != expected) { throw new InvalidOperationException("Ordinal result differs from string oracle."); }
        }
        return new(Stopwatch.GetTimestamp() - start, GC.GetAllocatedBytesForCurrentThread() - bytes);
    }

    private static int Iterations(Func<int> search, int expected) {
        const int maximum = 131072;
        int count = 1;
        while (true) {
            long ticks = Batch(search, expected, count).Ticks;
            if (ticks >= Stopwatch.Frequency / 1000 || count == maximum) {
                double target = 0.008 * Stopwatch.Frequency * count / Math.Max(1, ticks);
                return (int)Math.Clamp(target, 2, maximum);
            }
            count = Math.Min(maximum, count * 4);
        }
    }

    private static void CheckArguments() {
        var text = new DurableText("ab");
        text.Replace(1, 0, "\ud83d\ude00");
        var snapshot = text.Snapshot();
        foreach (var needle in new[] { "", "too-long", "\ude00b", "a" }) {
            for (int start = 0; start <= text.Length; start++) {
                int expected = "a\ud83d\ude00b".IndexOf(needle, start, StringComparison.Ordinal);
                if (snapshot.IndexOf(needle, start) != expected || Visit(text, needle, start) != expected ||
                    WindowControl.IndexOf(snapshot, needle, start) != expected) {
                    throw new Exception("Guard, offset or UTF-16 mismatch");
                }
            }
        }
        foreach (var search in new Func<string, int, int>[] {
            snapshot.IndexOf, (s, i) => Visit(text, s, i), (s, i) => WindowControl.IndexOf(snapshot, s, i)
        }) {
            try { search(null!, 0); throw new Exception("Expected null rejection"); } catch (ArgumentNullException) { }
            foreach (int start in new[] { -1, text.Length + 1 }) {
                try { search("", start); throw new Exception("Expected range rejection"); } catch (ArgumentOutOfRangeException) { }
            }
        }
    }

    public static void Main(string[] args) {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        if (args is ["cold-smoke"]) { Corpus.RunCold(null, 0); return; }
        bool smoke = args.Length == 1 && args[0] == "smoke";
        int rep = 0;
        if (!smoke && (args.Length != 3 || !int.TryParse(args[2], out rep) || rep < 0)) {
            throw new ArgumentException("Use smoke, cold-smoke, or matrix / short / corpus / cold followed by <new.csv> <rep>.");
        }
        if (args[0] == "corpus") { Corpus.Run(args[1], rep); return; }
        if (args[0] == "cold") { Corpus.RunCold(args[1], rep); return; }
        if (!smoke && args[0] is not ("matrix" or "short")) { throw new ArgumentException("Unknown mode."); }

        CheckArguments();
        using var output = smoke ? null : new StreamWriter(new FileStream(args[1], FileMode.CreateNew, FileAccess.Write));
        output?.WriteLine("rep,length,shape,pieces,case,needle_units,start,expected,route,block,slot,iterations,ticks,frequency,allocated_bytes,ns_per_call,bytes_per_call,runtime,dll_sha256");
        string dll = typeof(DurableText).Assembly.Location;
        string sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll)));
        var cases = (args[0] == "short" ? SearchCase.ShortPatterns() : SearchCase.All()).ToArray();
        Console.WriteLine($"Cases={cases.Length} runtime={RuntimeInformation.FrameworkDescription} arch={RuntimeInformation.ProcessArchitecture} vec256={Vector256.IsHardwareAccelerated} vec512={Vector512.IsHardwareAccelerated} product={sha}");
        new Random(1030 + rep).Shuffle(cases);
        for (int index = 0; index < cases.Length; index++) {
            var c = cases[index];
            var (text, snapshot, source, pieces) = c.Create();
            int expected = source.IndexOf(c.Needle, c.Start, StringComparison.Ordinal);
            Func<int>[] routes = [
                () => snapshot.IndexOf(c.Needle, c.Start),
                () => Visit(text, c.Needle, c.Start),
                () => WindowControl.IndexOf(snapshot, c.Needle, c.Start)
            ];
            foreach (var route in routes) { Batch(route, expected, 1); }
            if (smoke) { continue; }

            int[] counts = routes.Select(route => Iterations(route, expected)).ToArray();
            for (int block = 0; block < routes.Length; block++) {
                for (int slot = 0; slot < routes.Length; slot++) {
                    int id = (index + rep + block + (rep % 2 == 0 ? slot : routes.Length - slot)) % routes.Length;
                    int n = counts[id];
                    var sample = Batch(routes[id], expected, n);
                    double ns = 1e9 * sample.Ticks / Stopwatch.Frequency / n;
                    string route = id switch { 0 => "snapshot", 1 => "visit", _ => "window" };
                    output!.WriteLine($"{rep},{c.Length},{c.Shape},{pieces},{c.Name},{c.Needle.Length},{c.Start},{expected},{route},{block},{slot},{n},{sample.Ticks},{Stopwatch.Frequency},{sample.Bytes},{ns:F4},{(double)sample.Bytes / n:F4},{Environment.Version},{sha}");
                }
            }
        }
        Console.WriteLine(smoke ? "All fixtures, results and semantic guards passed." : $"Finished rep {rep}.");
    }
}
