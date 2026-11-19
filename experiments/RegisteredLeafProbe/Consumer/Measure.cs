using System.Diagnostics;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using Atelia.Text;

namespace Probe.Consumer;

internal static class Measure {
    internal static object Run(string directory, int rotation) {
        Directory.CreateDirectory(directory);
        // Warm both implementations before recording either lane; each graph has its own repository.
        foreach (bool external in new[] { false, true }) RunOne(Path.Combine(directory, "warm-" + external), external, 4096, 3, 2);
        var rows = new List<object>();
        foreach (int units in new[] { 4096, 65536 }) {
            bool first = (rotation % 2 == 0) != (units == 4096);
            foreach (bool external in new[] { first, !first })
                rows.Add(RunOne(Path.Combine(directory, $"s{units}-{external}"), external, units, 16, 8));
        }
        return rows;
    }

    internal static object RunOne(string path, bool external, int units, int saves, int changes, Diagnostics.Counters? counts=null) {
        var seed = new string('a', units);
        var expected = seed.ToCharArray();
        var refs = new List<GraphRef>();
        Timed firstSave;
        var editCosts = new List<Timed>();
        var saveCosts = new List<Timed>();
        long fileBytes, firstFileBytes=0;
        int firstEncoded=0;
        var readCosts=new List<Timed>();
        using (var repository = Repository.CreateNew(path, counts?.Models() ?? Program.Models())) {
            Action<int, int, string> replace;
            Func<int> find;
            IDurableObject root;
            if (external) {
                var e = new TextEditor(seed);
                replace = e.Replace;
                find = () => e.VisitSpan(0,static (span,_)=>span.IndexOf('Z'));
                root = new RegisteredEntry { Body = e };
            } else {
                var e = new DurableText(seed);
                replace = e.Replace;
                find = () => e.VisitSpan(0,static (span,_)=>span.IndexOf('Z'));
                root = new BuiltinEntry { Body = e };
            }
            using var graph = repository.CreateGraph(root);
            GraphRef id = default;
            firstSave = Timed.Capture(() => id = repository.SaveGraph(graph));
            refs.Add(id);
            if(counts is not null) { firstEncoded=counts.Base; firstFileBytes=StoreBytes(path); }
            int sequence = 0;
            for (int s = 0; s < saves; s++) {
                if (counts is not null) {
                    int first=-2,second=-2;
                    readCosts.Add(Timed.Capture(()=>{first=find();second=find();}));
                    Program.Need(first==second && (first>=0)==(s>0),"read before rewrite");
                }
                var positions = new int[changes];
                for (int j = 0; j < changes; j++) {
                    int at = (++sequence * 997) % units;
                    positions[j] = at;
                    expected[at] = 'Z';
                }
                editCosts.Add(Timed.Capture(() => {
                    foreach (int at in positions) replace(at, 1, "Z");
                }));
                saveCosts.Add(Timed.Capture(() => id = repository.SaveGraph(graph)));
                refs.Add(id);
            }
        }
        fileBytes = StoreBytes(path);
        string actual = "";
        var latest = refs[^1];
        Timed cold = Timed.Capture(() => {
            using var repository = Repository.OpenReadOnlyExisting(path, Program.Models());
            using var graph = repository.OpenGraph(latest);
            actual = external ? ((RegisteredEntry)graph.Root).Body.ToString() : ((BuiltinEntry)graph.Root).Body.ToString();
        });
        Program.Need(actual == new string(expected), "cold benchmark text differs from oracle");
        using var inspector = Repository.OpenReadOnlyExisting(path, Program.Models());
        var chain = Records.Inspect(inspector, refs);
        return new { external, units, saves, changes, firstSave, editCosts, saveCosts, readCosts, cold, fileBytes, chain,
            encoding=counts is null ? null : new { firstEncoded, hotBaseEncodes=counts.Base-firstEncoded, registeredDeltaPrepares=external?(int?)counts.Delta:null,
                firstFileBytes, hotFileGrowth=fileBytes-firstFileBytes }};
    }

    private static long StoreBytes(string path) => Directory.EnumerateFiles(path,"*",SearchOption.AllDirectories).Sum(f=>new FileInfo(f).Length);

    internal readonly record struct Timed(double Microseconds, long AllocatedBytes) {
        public static Timed Capture(Action action) {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            action();
            long end = Stopwatch.GetTimestamp();
            var result = GC.GetAllocatedBytesForCurrentThread() - allocated;
            return new(Stopwatch.GetElapsedTime(start, end).TotalMicroseconds, result);
        }
    }
}
