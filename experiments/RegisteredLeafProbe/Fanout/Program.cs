using System.Diagnostics;
using System.Text.Json;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Generated;
using Atelia.DurableGraph.Persistence;
#if REGISTERED_LEAF
using Body = Atelia.Text.TextEditor;
#else
using Body = Atelia.DurableGraph.DurableText;
#endif

if (args.Length != 1 || Directory.Exists(args[0])) {
    throw new ArgumentException("Pass an unused directory for the new store.");
}
const int leafCount = 128, units = 4096, warm = 6, measured = 24;
var models = new StateModelRegistry();
DurableDefinitions.Register(models);
#if REGISTERED_LEAF
Atelia.Text.TextEditorPersistence.Register(models);
#endif
var expected = Enumerable.Range(0, leafCount).Select(i => new string((char)('A' + i % 26), units)).ToArray();
var world = new MemoCollection { Entries = expected.Select(s => new Body(s)).ToList() };
var cycles = new List<Sample>();
GraphRef last;
using (var repo = Repository.CreateNew(args[0], models)) {
    using var graph = repo.CreateGraph(world);
    last = repo.SaveGraph(graph);
    for (var step = 0; step < warm + measured; step++) {
        int target = step % 6;
        int offset = 1 + step;
        const string one = "中", two = "记";
        expected[target] = expected[target].Remove(offset, 1).Insert(offset, one)
            .Remove(units - offset, 1).Insert(units - offset, two);
        long allocated = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
        var body = world.Entries[target];
        body.Replace(offset, 1, one);
        body.Replace(units - offset, 1, two);
        last = repo.SaveGraph(graph);
        var cycle = Sample.End(start, allocated);
        if (step >= warm) cycles.Add(cycle);
    }
}

var reads = new List<Sample>();
for (int read = 0; read < 12; read++) {
    long allocated = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
    using var repo = Repository.OpenReadOnlyExisting(args[0], models);
    using var graph = repo.OpenGraph(last);
    var bodies = ((MemoCollection)graph.Root).Entries;
    var sample = Sample.End(start, allocated);
    if (bodies.Count != expected.Length) throw new InvalidDataException("Incorrect list length.");
    for (int i = 0; i < bodies.Count; i++) {
        if (bodies[i].ToString() != expected[i]) throw new InvalidDataException($"Wrong text at {i}.");
    }
    if (read >= 2) reads.Add(sample);
}
Console.WriteLine(JsonSerializer.Serialize(new {
    domain = typeof(Body).FullName,
    graph = typeof(Repository).Assembly.ManifestModule.ModuleVersionId,
    hot = Sample.Median(cycles), cold = Sample.Median(reads)
}));

readonly record struct Sample(double Microseconds, long Bytes) {
    internal static Sample End(long timestamp, long bytes) =>
        new(Stopwatch.GetElapsedTime(timestamp).TotalMicroseconds, GC.GetAllocatedBytesForCurrentThread() - bytes);
    internal static Sample Median(List<Sample> values) =>
        new(values.Select(x => x.Microseconds).Order().ElementAt(values.Count / 2),
            values.Select(x => x.Bytes).Order().ElementAt(values.Count / 2));
}

[DurableType("DB104.MemoCollection", 1)]
public sealed partial class MemoCollection : IDurableObject {
    [DurableField(1)] public List<Body> Entries = [];
}
