using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using Xunit.Abstractions;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;
using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;

namespace Atelia.DurableGraph.Persistence.Tests;

/// <summary>
/// Same workload before/after DB-069. Measures the workspace save kernel, excluding
/// durable flush and Journal publication. Fresh Store validation is outside timed rounds;
/// it is not an operating-system cold-cache measurement. No timing assertions.
/// </summary>
public sealed class HotSaveMeasurementTests(ITestOutputHelper output) {
    private const int HotCommits = 4;
    private const int ArrayLength = 4096;
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1000000, 5);
    private static readonly TypeExpr ValuesType = TypeExpr.VectorArray(TypeExpr.Builtin(TypeTag.Int32));
    private static readonly DurableSchema Schema = new("HotSaveMeasurementRoot", 1,
        DurableFieldInfo.Reference(1, ValuesType),
        new DurableFieldInfo(2, TypeTag.ObjectReference, "SharedReadNode"));

    [Theory]
    [InlineData(8, StateRevisionStore.DefaultReadCacheBudgetBytes)]
    [InlineData(64, StateRevisionStore.DefaultReadCacheBudgetBytes)]
    [InlineData(8, 0L)]
    [InlineData(64, 0L)]
    public void Measure_hot_save_kernel(int history, long cacheBudget) {
        // One discarded round per cell, then three recorded rounds.
        for (int sample = -1; sample < 3; sample++) {
            string path = Path.Combine(Path.GetTempPath(), $"durable-hot-save-measure-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            try {
                using IRbfFile schemaFile = RbfFile.CreateNew(Path.Combine(path, "schemas.rbf"));
                SchemaStore schemas = new(schemaFile);
                using SegmentStore segments = SegmentStore.CreateNew(Path.Combine(path, "state"),
                    new() { NewStoreLayout = RbfSegmentStoreLayout.Flat });
                StateModelRegistry models = Models();
                string shared = new('x', 64);
                Root world = new() {
                    Values = Enumerable.Range(0, ArrayLength).ToArray(),
                    Stable = new() { Value = 7, Text = shared, Next = new() { Value = 9, Text = shared } }
                };
                List<object> samples = [];
                FrameAddress final;
                ObjectId rootId;
                uint arrayId = 0;
                using (StateRevisionStore store = new(segments, cacheBudget)) {
                    WorldWorkspace workspace = WorldWorkspace.Create(store, schemas, world, models);
                    using (PreparedWorldSave initial = workspace.Stage(NoRebase)) {
                        Assert.All(initial.Revision.LocalObjects, row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
                        Install(store, initial);
                    }
                    for (int step = 0; step < history; step++) {
                        Mutate(world, step);
                        using PreparedWorldSave pending = workspace.Stage(NoRebase);
                        ObjectVersionRecord delta = Assert.Single(pending.Revision.LocalObjects);
                        Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
                        arrayId = delta.ObjectId;
                        Install(store, pending);
                    }
                    for (int step = 0; step < HotCommits; step++) {
                        Mutate(world, history + step);
                        Dictionary<string, long> traversalBefore = Statistics(store, "TraversalStatistics");
                        Dictionary<string, long> cacheBefore = Statistics(store, "ReadCacheStatistics");
                        Stamp start = Stamp.Now();
                        using PreparedWorldSave pending = workspace.Stage(NoRebase);
                        Stamp staged = Stamp.Now();
                        FrameAddress address = store.Append(pending.Revision);
                        Stamp appended = Stamp.Now();
                        pending.PrepareInstall(address);
                        Stamp prepared = Stamp.Now();
                        pending.Install();
                        Stamp installed = Stamp.Now();
                        Dictionary<string, long> traversalAfter = Statistics(store, "TraversalStatistics");
                        Dictionary<string, long> cacheAfter = Statistics(store, "ReadCacheStatistics");
                        ObjectVersionRecord delta = Assert.Single(pending.Revision.LocalObjects);
                        Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
                        Assert.Equal(arrayId, delta.ObjectId);
                        samples.Add(new {
                            Step = step, Stage = Between(start, staged), Append = Between(staged, appended),
                            PrepareInstall = Between(appended, prepared), Install = Between(prepared, installed),
                            Total = Between(start, installed),
                            TraversalDelta = Difference(traversalBefore, traversalAfter),
                            CacheDelta = Difference(cacheBefore, cacheAfter), CacheAfter = cacheAfter,
                            LocalBases = 0, LocalDeltas = 1, DeltaBodyBytes = delta.Body.Length
                        });
                    }
                    // Do not cold-read between commits: that would prewarm maps and old chains.
                    using (PreparedWorldSave unchanged = workspace.Stage(NoRebase)) {
                        Assert.Empty(unchanged.Revision.LocalObjects);
                    }
                    final = workspace.ParentRevisionAddress!.Value;
                    rootId = workspace.WorldId;
                }
                using StateRevisionStore cold = new(segments, cacheBudget);
                Root restored = GraphReader.Read<Root>(cold, schemas, final, rootId, models.Snapshot(schemas)).Root;
                Assert.Equal(world.Values, restored.Values);
                Assert.Equal(world.Stable!.Value, restored.Stable!.Value);
                Assert.Equal(world.Stable.Next!.Value, restored.Stable.Next!.Value);
                Assert.Same(restored.Stable.Text, restored.Stable.Next.Text);
                ulong checksum = Checksum(world);
                Assert.Equal(checksum, Checksum(restored));
                ObjectVersionChain chain = cold.ReadObjectVersionChain(final, arrayId);
                Assert.Equal(history + HotCommits + 1, chain.Records.Count);
                Assert.Equal(ObjectVersionKind.Base, chain.Records[0].Record.Kind);
                Assert.All(chain.Records.Skip(1), row => Assert.Equal(ObjectVersionKind.Delta, row.Record.Kind));
                Assert.Equal(chain.Records.Sum(row => (long)row.ObjectVersionPayloadBytes), chain.ReconstructionPayloadBytes);
                if (sample >= 0) output.WriteLine("DB069_MEASUREMENT " + JsonSerializer.Serialize(new {
                    Sample = sample, History = history, CacheBudgetBytes = cacheBudget,
                    ArrayElements = ArrayLength, HotCommits, SavePolicy = "1000000x,5%",
                    Barrier = "Append only; no durable flush or Journal publication", Samples = samples,
                    Checksum = checksum, ChainEntries = chain.Records.Count,
                    ActualReconstructionPayloadBytes = chain.ReconstructionPayloadBytes,
                    ActualBasePayloadBytes = chain.Records[0].ObjectVersionPayloadBytes,
                    ActualDeltaPayloadBytes = chain.Records.Skip(1).Sum(row => (long)row.ObjectVersionPayloadBytes),
                    LastDeltaPayloadBytes = chain.Records[^1].ObjectVersionPayloadBytes,
                    LastDeltaBodyBytes = chain.Records[^1].Record.Body.Length
                }));
            } finally {
                SharedReadModel.DeleteFixture(path, "durable-hot-save-measure-");
            }
        }
    }

    private static void Mutate(Root world, int step) => world.Values[(step * 31) % ArrayLength] += step + 1;

    private static void Install(StateRevisionStore store, PreparedWorldSave pending) {
        FrameAddress address = store.Append(pending.Revision);
        pending.PrepareInstall(address);
        pending.Install();
    }

    private static Dictionary<string, long> Statistics(StateRevisionStore store, string name) {
        object value = typeof(StateRevisionStore).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(store)!;
        return value.GetType().GetProperties().ToDictionary(property => property.Name,
            property => Convert.ToInt64(property.GetValue(value)));
    }

    private static Dictionary<string, long> Difference(Dictionary<string, long> before, Dictionary<string, long> after) =>
        after.ToDictionary(pair => pair.Key, pair => pair.Value - before[pair.Key]);

    private static ulong Checksum(Root root) {
        ulong result = root.Stable!.Value;
        foreach (int value in root.Values) result = unchecked(result * 31 + (uint)value);
        foreach (char value in root.Stable.Text!) result = unchecked(result * 31 + value);
        return unchecked(result * 31 + root.Stable.Next!.Value);
    }

    private static Cost Between(Stamp start, Stamp end) => new(
        Stopwatch.GetElapsedTime(start.Timestamp, end.Timestamp).TotalMilliseconds, end.AllocatedBytes - start.AllocatedBytes);
    private readonly record struct Cost(double Milliseconds, long AllocatedBytes);
    private readonly record struct Stamp(long Timestamp, long AllocatedBytes) {
        internal static Stamp Now() => new(Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread());
    }

    private sealed class Root : IDurableObject {
        internal int[] Values = [];
        internal Node? Stable;
    }
    private readonly record struct State(ObjectId Values, ObjectId Stable);

    private static StateModelRegistry Models() {
        StateModelRegistry models = SharedReadModel.Models();
        CapturedStatePreparation<State> preparation = new(Schema, Base,
            static (in State prior, in State next) => new(prior != next, Base(next).Body),
            static (in State left, in State right) => left == right);
        models.Register(new StateModelBinding<Root, State>(preparation,
            [new StateReaderBinding<State>(Schema, Read,
                static (ref BinaryPayloadReader reader, in State prior) => Read(ref reader), Visit)],
            static row => row.GetState<State>(), static () => new(),
            static (Root root, in State state, ObjectReadTable objects) => {
                root.Values = objects.ResolveObject<int[]>(state.Values)!;
                root.Stable = objects.ResolveDurable<Node>(state.Stable);
            }, static (root, context) => new(context.CaptureObject(root.Values, ValuesType),
                context.CaptureDurable(root.Stable, SharedReadModel.Schema.SchemaId)), Visit));
        return models;
    }

    private static void Visit(in State state, IStateReferenceVisitor visitor) {
        visitor.VisitObject(state.Values, ValuesType);
        visitor.VisitDurable(state.Stable, SharedReadModel.Schema.SchemaId);
    }
    private static State Read(ref BinaryPayloadReader reader) => new(new(reader.ReadUInt32()), new(reader.ReadUInt32()));
    private static PreparedBaseBody Base(in State state) {
        ArrayBufferWriter<byte> bytes = new();
        BinaryPayloadWriter writer = new(bytes);
        writer.WriteUInt32(state.Values.Value);
        writer.WriteUInt32(state.Stable.Value);
        return new(bytes.WrittenSpan);
    }
}
