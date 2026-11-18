using System.Diagnostics;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;

namespace Atelia.DurableGraph.Persistence.Tests;

/// <summary>Opt-in, bounded product-path evidence. No timing assertions or research-project dependency.</summary>
public sealed class DurableTextProductProbeTests {
    [Fact]
    public void RecordProductSaveAndConsumerCosts() {
        string? directory = Environment.GetEnvironmentVariable("DG_TEXT_PRODUCT_PROBE");
        if (string.IsNullOrEmpty(directory)) { return; }
        directory = Path.GetFullPath(directory);
        int repetition = int.Parse(Environment.GetEnvironmentVariable("DG_TEXT_PRODUCT_PROBE_REP") ?? "0");
        Assert.InRange(repetition, 0, 2);
        Assert.False(Directory.Exists(directory), "Use a new output directory for each repetition.");
        Directory.CreateDirectory(directory);
        Stopwatch budget = Stopwatch.StartNew();
        List<SaveResult> saves = [];
        List<ConsumerResult> consumers = [];
        foreach (int length in new[] { 65_536, 1_048_576 }) {
            foreach (bool eager in repetition % 2 == 0 ? new[] { false, true } : new[] { true, false }) {
                Assert.True(budget.Elapsed < TimeSpan.FromMinutes(3), "Bounded probe exceeded its three-minute budget.");
                saves.Add(SaveChain(Path.Combine(directory, $"{length}-{(eager ? "eager" : "selected")}"), length, eager));
            }
            consumers.Add(Consumers(length, false));
            consumers.Add(Consumers(length, true));
        }
        // This fixture is deliberately sparse (32 one-unit edits at 64Ki/1Mi).
        // Eager uses exact Base bytes while deferred uses a soft estimate;
        // arbitrary workloads can therefore choose different checkpoint positions.
        // Here the actual records below must prove both stayed on the same Delta route.
        foreach (var pair in saves.GroupBy(item => item.Length)) {
            SaveResult[] variants = pair.ToArray();
            Assert.Equal(variants[0].TextRecords, variants[1].TextRecords);
        }
        File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new {
            Repetition = repetition,
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Stopwatch.Frequency,
            Assembly = typeof(Repository).Assembly.Location,
            ElapsedSeconds = budget.Elapsed.TotalSeconds,
            Boundaries = "Save metrics include real capture/prepare/append/publication/install; edits and initial Save separate. Cold is fresh Repository/OpenGraph plus full text consumption, not OS-cache-cold. Three new test processes; no steady-state/JIT claim. AllocatedBytes is cumulative calling-thread allocation, not live heap.",
            Saves = saves, Consumers = consumers, EncodingSamples = EncodingSamples(),
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void RecordSegmentedBaseCosts() {
        string? directory = Environment.GetEnvironmentVariable("DG_TEXT_SEGMENT_PROBE");
        if (string.IsNullOrEmpty(directory)) { return; }
        directory = Path.GetFullPath(directory);
        Assert.False(Directory.Exists(directory), "Use a fresh output directory.");
        Directory.CreateDirectory(directory);
        List<SegmentResult> results = [];
        foreach (int length in new[] { 65_536, 1_048_576 }) {
            foreach (bool mixed in new[] { false, true }) {
                string expected = mixed ? string.Concat(Enumerable.Repeat("汉a", length / 2)) : new string('a', length);
                foreach (int chunkLength in new[] { length, 16 }) {
                    DurableText text = new(expected);
                    if (chunkLength != length) {
                        // Public batch construction, outside all measured regions. Each replacement
                        // owns a separate string; adjacent literals are not one physical backing.
                        TextEdit[] edits = new TextEdit[length / chunkLength];
                        for (int i = 0; i < edits.Length; i++) {
                            edits[i] = new(i * chunkLength, chunkLength, new string(expected.AsSpan(i * chunkLength, chunkLength)));
                        }
                        text.ApplyEdits(edits);
                    }
                    DurableTextState frozen = text.CaptureState();
                    int pieces = frozen.Content.View.PieceCount;
                    Assert.Equal(length / chunkLength, pieces);
                    byte[] body = TextBodyCodec.PrepareBase(frozen).Body.ToArray();
                    // Warm only this micro-observation. The real SaveGraph below has no warmup claim.
                    _ = TextBodyCodec.ReadBase(body);
                    _ = new string(expected.AsSpan());
                    _ = new DurableTextState(TextSnapshot.FromString(expected));
                    Cost[] decode = new Cost[5], finalStrings = new Cost[5], wrappers = new Cost[5], consume = new Cost[5];
                    for (int i = 0; i < decode.Length; i++) {
                        DurableTextState? decoded = null, wrapped = null;
                        string? copied = null, rendered = null;
                        decode[i] = Measure(() => decoded = TextBodyCodec.ReadBase(body));
                        finalStrings[i] = Measure(() => copied = new string(expected.AsSpan()));
                        wrappers[i] = Measure(() => wrapped = new DurableTextState(TextSnapshot.FromString(expected)));
                        consume[i] = Measure(() => rendered = decoded!.Content.ToString());
                        Assert.Equal(expected, rendered);
                        Assert.Equal(expected, copied);
                        Assert.Single(decoded!.Content.View.Pieces);
                        Assert.Same(decoded.Content.View.Pieces[0].Text, rendered);
                        // Allocation is evidence, not a functional pass threshold. Preserve all
                        // samples, including runtime/tiering outliers; compare segment counts offline.
                        GC.KeepAlive(wrapped);
                    }
                    string path = Path.Combine(directory, $"{length}-{(mixed ? "mixed" : "ascii")}-{pieces}");
                    long payloadBytes;
                    Cost save;
                    using (Repository repository = Repository.CreateNew(path, Models())) {
                        using GraphBuilder builder = repository.CreateGraph(new TextProductProbe { Body = text });
                        GraphRef reference = default;
                        save = Measure(() => reference = repository.SaveGraph(builder));
                        var descriptor = Resources(repository).ReadHeapDescriptor(reference.CommitId.Value);
                        StoredGraphRevision graph = repository.GraphStore.ReadBusinessGraph(descriptor.Address);
                        var row = builder.Business.Baseline!.CurrentDtos.Values.Single(item => item.Kind == ObjectStateKind.Text);
                        ObjectVersionRecord record = repository.GraphStore.ReadBatch(graph.OwnedBatch!.Value).Records.Single(item => item.ObjectId == row.Id.Value);
                        Assert.Equal("Base", record.Kind.ToString());
                        Assert.Equal(body, TypedBaseBodyCodec.Decode(record.Body, null).Body.ToArray());
                        payloadBytes = record.EncodedPayloadBytes!.Value;
                    }
                    results.Add(new(length, mixed ? "han-ascii-alternating" : "ascii", pieces,
                        body.Length, TextBodyCodec.EstimateBaseSize(frozen), payloadBytes,
                        Convert.ToHexString(SHA256.HashData(body)), save, decode, finalStrings, wrappers, consume));
                }
            }
        }
        File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new {
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            Stopwatch.Frequency,
            Boundaries = "Same body, 1 versus N/16 physical pieces. Setup/encoding excluded from decode timer. Each codec case warms once then records five allocation/time samples. Decode consumes no repository cache and produces one final string plus constant state/view/list wrappers; string and wrapper controls measured separately. Real first SaveGraph is separate, includes acceptance compaction. Save payload includes object envelope; BaseBodyBytes excludes it. No OS-cold or throughput claim.",
            Results = results,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record SegmentResult(int Units, string Content, int PhysicalPieces, int BaseBodyBytes,
        long BaseEstimateBytes, long ObjectPayloadBytes, string BodySha256, Cost InitialSave,
        Cost[] Decode, Cost[] FinalString, Cost[] Wrapper, Cost[] ConsumeDecoded);

    private static SaveResult SaveChain(string path, int length, bool eager) {
        TextProductProbe root = new() { Body = new DurableText(new string('a', length)), Gist = "one-line gist" };
        char[] expected = new string('a', length).ToCharArray();
        List<GraphRef> references = [];
        List<Cost> saveCosts = [], editCosts = [];
        List<RecordResult> records = [];
        int baseEncodes = 0;
        Cost initial;
        int initialEncodes;
        GraphRef final = default;
        BusinessPersistenceAdapter.EagerTextBaseForTesting = eager;
        TextBodyCodec.BaseEncodingObserver = () => baseEncodes++;
        try {
            using Repository repository = Repository.CreateNew(path, Models());
            using GraphBuilder builder = repository.CreateGraph(root);
            initial = Measure(() => references.Add(repository.SaveGraph(builder)));
            initialEncodes = baseEncodes;
            for (int index = 0; index < 32; index++) {
                int position = 1 + (index * 4051 % (length - 2));
                expected[position] = 'b';
                editCosts.Add(Measure(() => root.Body.Replace(position, 1, "b")));
                saveCosts.Add(Measure(() => references.Add(repository.SaveGraph(builder))));
            }
            final = references[^1];
            // Decode actual appended records only after all measured saves.
            foreach (GraphRef reference in references) {
                var descriptor = Resources(repository).ReadHeapDescriptor(reference.CommitId.Value);
                StoredGraphRevision graph = repository.GraphStore.ReadBusinessGraph(descriptor.Address);
                var row = builder.Business.Baseline!.CurrentDtos.Values.Single(item => item.Kind == ObjectStateKind.Text);
                ObjectVersionRecord? record = graph.OwnedBatch is { } batch
                    ? repository.GraphStore.ReadBatch(batch).Records.SingleOrDefault(item => item.ObjectId == row.Id.Value) : null;
                records.Add(record is null ? new("Reuse", 0, "") : new(record.Kind.ToString(),
                    record.EncodedPayloadBytes!.Value, Convert.ToHexString(SHA256.HashData(record.Body))));
            }
        } finally {
            TextBodyCodec.BaseEncodingObserver = null;
            BusinessPersistenceAdapter.EagerTextBaseForTesting = false;
        }
        string? recovered = null;
        Cost cold = Measure(() => {
            using Repository reader = Repository.OpenExisting(path, Models());
            using GraphBuilder loaded = reader.OpenGraph(final);
            recovered = ((TextProductProbe)loaded.Root).Body.ToString();
        });
        Assert.Equal(new string(expected), recovered);
        Assert.Equal("Base", records[0].Kind);
        Assert.Equal(33, records.Count);
        Assert.All(records.Skip(1), record => Assert.Equal("Delta", record.Kind));
        Assert.Equal(1, initialEncodes);
        Assert.Equal(eager ? 32 : 0, baseEncodes - initialEncodes);
        return new(length, eager, initial, initialEncodes, saveCosts.ToArray(), editCosts.ToArray(),
            baseEncodes - initialEncodes, records.ToArray(), cold,
            Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length));
    }

    private static object[] EncodingSamples() {
        (string Name, string Value)[] samples = [
            ("ascii", "the quick brown fox jumps over the lazy dog"),
            ("chinese", "长寿命智能体持续编辑正文并保存独立快照"),
            ("mixed", "memo 正文: alpha 中文 beta\r\n"),
            ("surrogates", "a\uD83D\uDE00b\uD800x\uDC00\0\r\n"),
        ];
        List<object> results = [];
        foreach ((string name, string value) in samples) {
            DurableText text = new();
            var prior = text.CaptureState();
            // Adjacent literal edits intentionally split physical surrogate pairs.
            foreach (char unit in value) { text.Replace(text.Length, 0, unit.ToString()); }
            var current = text.CaptureState();
            byte[]? body = null, delta = null;
            Cost baseCost = Measure(() => body = TextBodyCodec.PrepareBase(current).Body.ToArray());
            Cost deltaCost = Measure(() => delta = TextBodyCodec.PrepareDelta(prior, current).Body.ToArray());
            ArrayBufferWriter<byte> stringBytes = new();
            BareValueWriter writer = new(stringBytes);
            writer.WriteString(value);
            Assert.Equal(value, text.ToString());
            results.Add(new { Name = name, Units = value.Length, BaseBytes = body!.Length,
                AllNewDeltaBytes = delta!.Length, ExistingStringBytes = stringBytes.WrittenCount,
                BaseCost = baseCost, DeltaCost = deltaCost,
                BaseHex = Convert.ToHexString(body), DeltaHex = Convert.ToHexString(delta) });
        }
        return results.ToArray();
    }

    private static ConsumerResult Consumers(int length, bool repetitive) {
        string original = repetitive ? new string('a', length - 1) + "b"
            : string.Concat(Enumerable.Repeat("alpha beta gamma delta\n", length / 23 + 1))[..length];
        DurableText text = new(original);
        // Physical fragmentation is intentional; all compared paths consume this same snapshot.
        for (int index = 0; index < 32; index++) { text.Replace(1 + index * (length / 33), 1, "x"); }
        TextSnapshot snapshot = text.Snapshot();
        string expected = snapshot.ToString();
        string needle = repetitive ? "aaaaab" : "gamma";
        int expectedIndex = expected.IndexOf(needle, StringComparison.Ordinal);
        Dictionary<string, Cost> costs = [];
        foreach (string route in new[] { "AppendTo", "ToStringThenAppend", "SnapshotAppendTo" }) {
            string? rendered = null;
            void Render() {
                StringBuilder builder = new();
                for (int count = 0; count < 4; count++) {
                    if (route == "AppendTo") { text.AppendTo(builder); }
                    else if (route == "ToStringThenAppend") { builder.Append(text.ToString()); }
                    else { snapshot.AppendTo(builder); }
                }
                rendered = builder.ToString();
            }
            Render(); // Consumer-only warmup, identical per route; never warms Save measurements.
            costs[route] = Measure(Render);
            Assert.Equal(string.Concat(Enumerable.Repeat(expected, 4)), rendered);
        }
        int actualIndex = -2;
        _ = snapshot.IndexOf(needle);
        costs["IndexOf"] = Measure(() => actualIndex = snapshot.IndexOf(needle));
        Assert.Equal(expectedIndex, actualIndex);
        costs["MaterializeThenIndexOf"] = Measure(() => actualIndex = snapshot.ToString().IndexOf(needle, StringComparison.Ordinal));
        Assert.Equal(expectedIndex, actualIndex);
        return new(length, repetitive, needle, expectedIndex, costs);
    }

    private static StateModelRegistry Models() {
        StateModelRegistry models = new();
        models.Register(global::Atelia.DurableGraph.Generated.Family_5465787450726F6475637450726F6265.Definition);
        return models;
    }
    private static Cost Measure(Action action) {
        long allocated = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
        action();
        long end = Stopwatch.GetTimestamp();
        long totalAllocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        return new(Stopwatch.GetElapsedTime(start, end).TotalMilliseconds, totalAllocated);
    }
    private readonly record struct Cost(double Milliseconds, long AllocatedBytes);
    private sealed record RecordResult(string Kind, long PayloadBytes, string BodySha256);
    private sealed record SaveResult(int Length, bool Eager, Cost InitialSave, int InitialBaseEncodes,
        Cost[] Saves, Cost[] Edits, int ChangedSaveBaseEncodes, RecordResult[] TextRecords, Cost ColdOpenAndConsume, long RepositoryFileBytes);
    private sealed record ConsumerResult(int Length, bool RepeatedPrefix, string Needle, int Match, Dictionary<string, Cost> Costs);
}

[DurableType("TextProductProbe", 1)]
public sealed partial class TextProductProbe : IDurableObject {
    [DurableField(1)] public DurableText Body = new();
    [DurableField(2)] public string Gist = "";
}
