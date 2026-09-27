using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Atelia.DurableGraph.Persistence;
using Atelia.RbfSegmentStore;
using Microsoft.CodeAnalysis;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    private const int LeafMeasureOwners = 256;
    private const int LeafMeasureFields = 16;
    private const int LeafMeasureMutableNodes = 1024;
    private const int LeafMeasureForks = 8;
    private const int LeafMeasureSamples = 5;
    private static readonly ReadAmplificationBaseBudgetParameters LeafMeasurePolicy = new(int.MaxValue, 1);

    /// <summary>Opt-in DB-082 full-Fork A/B, with DB-080 enabled on both sides; no time assertions.</summary>
    [Fact]
    public void DB082_Measure_generated_immutable_leaf_forks() {
        if (Environment.GetEnvironmentVariable("DURABLEGRAPH_DB082_MEASURE") != "1") { return; }
        string destination = Environment.GetEnvironmentVariable("DURABLEGRAPH_DB082_MEASURE_OUTPUT")
            ?? Path.Combine(Path.GetTempPath(), "durable-db082-measurements.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        using StreamWriter output = new(destination, append: false);
        WriteLeafMeasurement(output, new {
            Kind = "configuration", Owners = LeafMeasureOwners, FieldsPerOwner = LeafMeasureFields,
            MutableNodes = LeafMeasureMutableNodes, Forks = LeafMeasureForks, RecordedSamples = LeafMeasureSamples,
            WarmupSamples = 1, Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            ModelOptimization = "Release, actual generated binary and forced-Family models",
            Timing = "Complete named Fork includes durable ref publication, leaf-table construction and identity import; Dispose/checks/heap sampling excluded",
            Materialization = "GraphReadStatistics.MaterializationElapsedTicks: shared-core registration, Allocate, ObjectReadTable and Hydrate; subtotal, not full Fork",
            Allocation = "Current-thread managed allocation around Fork only, excludes other threads and native memory",
            Peak = "Sampled GC.GetTotalMemory(false) after each Fork is an occupied-managed-heap proxy, not exact live/peak or cache bytes. Process.PeakWorkingSet64 is process-lifetime cumulative, includes JIT/generator/earlier cells, not attributable to this cell.",
            Residency = "Forced-GC endpoint deltas are approximate process-wide managed live-heap observations, not isolated cache size. Logical leaf payload omits headers, table, DTO, certificate and model overhead.",
            Pairing = "Fresh equally seeded repository per sample; A/B order alternates; 8 checkouts remain alive until batch end; DB080 enabled both sides; no DB081 hot admission"
        });
        foreach (bool family in new[] { false, true }) {
            LeafMeasurementModel model = BuildLeafMeasurementModel(family);
            foreach (string scenario in new[] { "leaf-heavy", "all-mutable", "cycle", "pure-leaf", "deep-low-reuse" }) {
                for (int sample = -1; sample < LeafMeasureSamples; sample++) {
                    foreach (bool enabled in sample % 2 == 0 ? new[] { true, false } : new[] { false, true }) {
                        RunLeafMeasurement(model, family, scenario, sample, enabled, output);
                    }
                }
            }
        }
    }

    private static LeafMeasurementModel BuildLeafMeasurementModel(bool family) {
        GeneratorTestRun run = RunGenerator(LeafMeasurementSource(family), [], null, family ? "true" : null);
        Assert.DoesNotContain(run.GeneratorDiagnostics, IsError);
        Assert.Equal(family, run.GeneratedSources.Any(source => source.HintName == "DurableGenericStates.g.cs"));
        Assembly assembly = EmitAndLoad(run.OutputCompilation.WithOptions(
            run.OutputCompilation.Options.WithOptimizationLevel(OptimizationLevel.Release)));
        Type host = assembly.GetType("LeafMeasurement.Host")!;
        var model = new LeafMeasurementModel(
            host.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>(),
            host.GetMethod("Create")!.CreateDelegate<Func<string, IDurableObject>>(),
            host.GetMethod("Check")!.CreateDelegate<Func<IDurableObject, string, long>>(),
            host.GetMethod("Mutate")!.CreateDelegate<Action<IDurableObject>>());
        var snapshot = model.Models().Snapshot();
        Assert.True(snapshot.ResolveCurrentModel(assembly.GetType("LeafMeasurement.Leaf")!).IsImmutableLeaf);
        Assert.False(snapshot.ResolveCurrentModel(assembly.GetType("LeafMeasurement.Owner")!).IsImmutableLeaf);
        return model;
    }

    private static void RunLeafMeasurement(LeafMeasurementModel model, bool family, string scenario,
        int sample, bool enabled, StreamWriter output) {
        const string prefix = "durable-db082-measure-";
        string path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        try {
            using Repository repository = Repository.CreateNew(path, model.Models(),
                new() { NewStoreLayout = RbfSegmentStoreLayout.Flat });
            repository.PreparedStateReuseEnabled = true;
            repository.ImmutableLeafReuseEnabled = enabled;
            var seed = SeedLeafMeasurement(repository, model, scenario);
            GraphReadStatistics statistics = new();
            repository.RestorationStatistics = statistics;
            long baselineHeap = GC.GetTotalMemory(forceFullCollection: true);
            LeafMeasurementBatch batch = ForkLeafMeasurementBatch(repository, model, scenario, seed, baselineHeap);
            long slotOnlyHeap = GC.GetTotalMemory(forceFullCollection: true);
            int residentLeaves = repository.PreparedStateEntry?.ImmutableLeafCount ?? 0;
            if (sample >= 0) WriteLeafMeasurement(output, new {
                Kind = "sample", Family = family, Scenario = scenario,
                ContainerList = family && scenario == "cycle", Sample = sample, Enabled = enabled,
                Forks = LeafMeasureForks, batch.CompleteForkMilliseconds, batch.AllocatedBytes,
                MaterializationMilliseconds = statistics.MaterializationElapsedTicks * 1000.0 / Stopwatch.Frequency,
                statistics.AllocatedObjects, statistics.HydratedObjects, statistics.ReusedImmutableLeaves,
                statistics.PreparedStateHits, statistics.PreparedStateMisses, statistics.PreparedGraphs,
                statistics.DecodedObjects, statistics.NormalizedObjects, statistics.ReachabilityVisits,
                SlotRows = repository.PreparedStateEntry?.Selection.Normalized.Objects.Count ?? 0,
                SlotExactRequirements = repository.PreparedStateEntry?.Requirements.Count ?? 0,
                ResidentLeaves = residentLeaves, LeafLogicalPayloadBytes = residentLeaves * 4L,
                BaselineManagedHeapBytes = baselineHeap, batch.SampledPeakOccupiedManagedBytes,
                batch.LiveManagedBytesWithCheckouts, LiveWithCheckoutsDeltaBytes = batch.LiveManagedBytesWithCheckouts - baselineHeap,
                ManagedHeapAfterCheckoutReleaseBytes = slotOnlyHeap, AfterCheckoutReleaseDeltaBytes = slotOnlyHeap - baselineHeap,
                ProcessLifetimePeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
                batch.Operations
            });
            GC.KeepAlive(repository);
        } finally {
            string resolved = Path.GetFullPath(path);
            if (!string.Equals(Path.GetDirectoryName(resolved), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith(prefix, StringComparison.Ordinal)) {
                throw new InvalidOperationException("Refusing to delete outside the DB082 fixture.");
            }
            if (Directory.Exists(resolved)) { Directory.Delete(resolved, recursive: true); }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (CheckpointAddress[] Requests, long[] Checksums) SeedLeafMeasurement(
        Repository repository, LeafMeasurementModel model, string scenario) {
        IDurableObject root = model.Create(scenario);
        using BranchCheckout source = repository.CreateBranch("main", root, LeafMeasurePolicy);
        CheckpointAddress first = source.Head;
        long firstChecksum = model.Check(root, scenario);
        CheckpointAddress second = first;
        long secondChecksum = firstChecksum;
        if (scenario == "deep-low-reuse") {
            model.Mutate(root);
            second = source.CommitState(LeafMeasurePolicy);
            secondChecksum = model.Check(root, scenario);
        }
        return (Enumerable.Range(0, LeafMeasureForks).Select(index => index % 2 == 0 ? first : second).ToArray(),
            Enumerable.Range(0, LeafMeasureForks).Select(index => index % 2 == 0 ? firstChecksum : secondChecksum).ToArray());
    }

    // Keep all domain references in this non-inlined scope so the subsequent heap sample retains only repo-owned material.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static LeafMeasurementBatch ForkLeafMeasurementBatch(Repository repository, LeafMeasurementModel model,
        string scenario, (CheckpointAddress[] Requests, long[] Checksums) seed, long baselineHeap) {
        List<BranchCheckout> checkouts = [];
        List<object> operations = [];
        double totalMilliseconds = 0;
        long totalAllocatedBytes = 0;
        long peak = baselineHeap;
        try {
            for (int index = 0; index < LeafMeasureForks; index++) {
                string name = $"fork-{index}";
                long allocationStart = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                BranchCheckout checkout = repository.Fork(name, seed.Requests[index]);
                long stop = Stopwatch.GetTimestamp();
                long allocated = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
                double milliseconds = Stopwatch.GetElapsedTime(start, stop).TotalMilliseconds;
                checkouts.Add(checkout);
                Assert.Equal(seed.Requests[index], checkout.Head);
                Assert.Equal(seed.Requests[index], repository.GetHead(name));
                Assert.Equal(seed.Checksums[index], model.Check(checkout.State!, scenario));
                if (index > 0 && scenario != "pure-leaf") { Assert.NotSame(checkouts[0].State, checkout.State); }
                peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: false));
                totalMilliseconds += milliseconds;
                totalAllocatedBytes += allocated;
                operations.Add(new { Index = index, CompleteForkMilliseconds = milliseconds, AllocatedBytes = allocated });
            }
            long live = GC.GetTotalMemory(forceFullCollection: true);
            GC.KeepAlive(checkouts);
            return new(totalMilliseconds, totalAllocatedBytes, peak, live, operations);
        } finally {
            foreach (BranchCheckout checkout in checkouts) { checkout.Dispose(); }
        }
    }

    private static void WriteLeafMeasurement(StreamWriter output, object value) {
        output.WriteLine(JsonSerializer.Serialize(value));
        output.Flush();
    }

    private sealed record LeafMeasurementModel(Func<StateModelRegistry> Models, Func<string, IDurableObject> Create,
        Func<IDurableObject, string, long> Check, Action<IDurableObject> Mutate);
    private sealed record LeafMeasurementBatch(double CompleteForkMilliseconds, long AllocatedBytes,
        long SampledPeakOccupiedManagedBytes, long LiveManagedBytesWithCheckouts, List<object> Operations);

    private static string LeafMeasurementSource(bool family) {
        string fields = string.Join("\n", Enumerable.Range(0, LeafMeasureFields)
            .Select(index => $"[DurableField({index + 3})] public Leaf? L{index};"));
        string fill = string.Join("\n", Enumerable.Range(0, LeafMeasureFields)
            .Select(index => $"owner.L{index} = new Leaf(index * {LeafMeasureFields} + {index});"));
        string sum = string.Join("\n", Enumerable.Range(0, LeafMeasureFields)
            .Select(index => $"sum += cursor.L{index}?.Value ?? 0;"));
        return $$"""
            #nullable enable
            using System;
            using System.Collections.Generic;
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Runtime;
            using Atelia.DurableGraph.Persistence;
            namespace LeafMeasurement;
            [DurableType("db082.measure.leaf", 1)]
            public partial class Leaf : IDurableObject {
                [DurableField(1)] public readonly int Value;
                public Leaf(int value) { Value = value; }
            }
            [DurableType("db082.measure.owner", 1)]
            public partial class Owner : IDurableObject {
                [DurableField(1)] public Owner? Next;
                [DurableField(2)] public int Counter;
                {{fields}}
                {{(family ? "[DurableField(100)] public List<Owner>? Links;" : "")}}
            }
            public static class Host {
                public static StateModelRegistry Models() {
                    var models = new StateModelRegistry();
                    {{(family ? "Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);" : "models.Register(Leaf.__DurableState.Model); models.Register(Owner.__DurableState.Model);")}}
                    return models;
                }
                public static IDurableObject Create(string scenario) {
                    if (scenario == "pure-leaf") return new Leaf(42);
                    bool manyLeaves = scenario == "leaf-heavy" || scenario == "cycle";
                    int count = manyLeaves ? {{LeafMeasureOwners}} : {{LeafMeasureMutableNodes}};
                    var nodes = new Owner[count];
                    for (int index = 0; index < count; index++) {
                        var owner = new Owner { Counter = index };
                        if (manyLeaves) { {{fill}} }
                        nodes[index] = owner;
                        if (index > 0) nodes[index - 1].Next = owner;
                    }
                    if (scenario == "cycle") {
                        nodes[count - 1].Next = nodes[0];
                        {{(family ? "nodes[0].Links = new List<Owner>(nodes);" : "")}}
                    }
                    if (scenario == "deep-low-reuse") nodes[count - 1].L0 = new Leaf(42);
                    return nodes[0];
                }
                public static long Check(IDurableObject value, string scenario) {
                    if (scenario == "pure-leaf") return ((Leaf)value).Value;
                    var root = (Owner)value;
                    var cursor = root;
                    int count = scenario == "leaf-heavy" || scenario == "cycle" ? {{LeafMeasureOwners}} : {{LeafMeasureMutableNodes}};
                    long sum = 0;
                    for (int index = 0; index < count; index++) {
                        if (cursor == null) throw new InvalidOperationException("Broken chain.");
                        sum += cursor.Counter;
                        {{sum}}
                        {{(family ? "if (scenario == \"cycle\" && !ReferenceEquals(root.Links![index], cursor)) throw new InvalidOperationException(\"Broken List alias.\");" : "")}}
                        cursor = cursor.Next;
                    }
                    if (scenario == "cycle" ? !ReferenceEquals(root, cursor) : cursor != null) throw new InvalidOperationException("Broken chain end.");
                    return sum;
                }
                public static void Mutate(IDurableObject root) { ((Owner)root).Counter++; }
            }
            """;
    }
}
