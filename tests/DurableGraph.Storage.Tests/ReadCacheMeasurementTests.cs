using System.Diagnostics;
using System.Text.Json;
using Atelia.FrameStore;
using static Atelia.DurableGraph.Storage.Tests.StorageTestFrames;
using Xunit.Abstractions;
using Frames = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Storage.Tests;

/// <summary>Reproducible mechanism measurements; elapsed time is evidence, never a pass threshold.</summary>
public sealed class ReadCacheMeasurementTests(ITestOutputHelper output) {
    [Fact]
    public void Measure_sorted_array_construction_freeze_and_reads() {
        foreach (int count in new[] { 1, 64, 256 }) {
            // Discard one pass to separate representation costs from first generic/JIT initialization.
            for (int sample = -1; sample < 3; sample++) {
                var build = Measure(() => {
                    SortedDictionary<uint, FrameAddress> items = new();
                    for (uint i = 1; i <= count; i++) items.Add(i * 3, Address(1, i * 64));
                    return items;
                });
                var freeze = Measure(() => new FrozenSortedDictionary<uint, FrameAddress>(build.Value));
                var first = Measure(() => Query(freeze.Value, count));
                var repeated = Measure(() => {
                    ulong sum = 0;
                    for (int repeat = 0; repeat < 100; repeat++) sum = unchecked(sum + Query(freeze.Value, count));
                    return sum;
                });
                Assert.Equal(unchecked(first.Value * 100), repeated.Value);
                if (sample >= 0) Write(new { Kind = "arrays", Count = count, Sample = sample,
                    Build = build.Cost, Freeze = freeze.Cost, FirstQueryAndTraversal = first.Cost,
                    Repeated100QueryAndTraversal = repeated.Cost, Checksum = first.Value,
                    EstimatedMapChargeBytes = StateRevisionReadCache.GetMapCharge(count) });
            }
        }
    }

    [Theory]
    [InlineData("fitting", 32, 64, 2)]
    [InlineData("oversize", 16, 4096, 0)]
    [InlineData("thrashing", 16, 128, 6)]
    public void Measure_actual_store_budgets(string fixture, int objectCount, int bodyBytes, int depth) {
        string path = Path.Combine(Path.GetTempPath(), $"durable-read-cache-measure-{Guid.NewGuid():N}");
        try {
            FrameAddress head;
            long maximumFrameCharge;
            using (Frames segments = Frames.Create(path)) {
                using StateRevisionStore writer = new(segments, 0);
                StateRevision revision = StateRevision.CreateObjectHeadMapBase(null, Records(objectCount, bodyBytes, 0, null), []);
                head = writer.Append(revision);
                maximumFrameCharge = StateRevisionReadCache.GetRevisionCharge(revision);
                for (int step = 1; step <= depth; step++) {
                    revision = StateRevision.CreateObjectHeadMapDelta(head, Records(objectCount, bodyBytes, step, head), []);
                    head = writer.Append(revision);
                    maximumFrameCharge = Math.Max(maximumFrameCharge, StateRevisionReadCache.GetRevisionCharge(revision));
                }
                segments.ConfirmDurable();
            }

            long smallBudget = fixture == "oversize"
                ? 2 * (StateRevisionReadCache.EntryOverheadBytes + StateRevisionReadCache.GetMapCharge(objectCount))
                : StateRevisionReadCache.EntryOverheadBytes + maximumFrameCharge;
            ulong? expected = null;
            foreach (long budget in new[] { 0L, StateRevisionStore.DefaultReadCacheBudgetBytes, smallBudget }) {
                for (int sample = -1; sample < 3; sample++) {
                    using Frames segments = Frames.OpenReadOnly(path);
                    using StateRevisionStore store = new(segments, budget);
                    var first = Measure(() => ReadAll(store, head));
                    var firstStats = store.ReadCacheStatistics;
                    var warm = Measure(() => ReadAll(store, head));
                    var warmStats = store.ReadCacheStatistics;
                    expected ??= first.Value;
                    Assert.Equal(expected.Value, first.Value);
                    Assert.Equal(first.Value, warm.Value);
                    Assert.InRange(warmStats.ResidentChargeBytes, 0, budget);
                    Assert.InRange(warmStats.PeakResidentChargeBytes, 0, budget);
                    if (sample >= 0) Write(new { Kind = "store", Fixture = fixture, ObjectCount = objectCount,
                        BodyBytes = bodyBytes, Depth = depth, BudgetBytes = budget, Sample = sample,
                        First = first.Cost, Warm = warm.Cost, Checksum = first.Value,
                        FirstStatistics = firstStats, AfterWarmStatistics = warmStats });
                }
            }
        } finally {
            string resolved = Path.GetFullPath(path);
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), Path.GetDirectoryName(resolved), ignoreCase: true);
            Assert.StartsWith("durable-read-cache-measure-", Path.GetFileName(resolved));
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }

    private static ObjectVersionRecord[] Records(int count, int bodyBytes, int step, FrameAddress? prior) {
        ObjectVersionRecord[] records = new ObjectVersionRecord[count];
        for (uint id = 1; id <= count; id++) {
            byte[] body = new byte[bodyBytes];
            for (int i = 0; i < body.Length; i++) body[i] = (byte)(id + step + i);
            records[id - 1] = prior is { } address
                ? ObjectVersionRecord.CreateDelta(id, address, body) : ObjectVersionRecord.CreateBase(id, body);
        }
        return records;
    }

    private static ulong ReadAll(StateRevisionStore store, FrameAddress head) {
        ulong hash = 14695981039346656037UL;
        foreach ((uint id, FrameAddress address) in store.ReadLiveObjectHeadMap(head)) {
            hash = Mix(hash, id);
            hash = Mix(hash, unchecked((uint)address.GetHashCode()));
            ObjectVersionChain chain = store.ReadObjectVersionChain(head, id);
            hash = Mix(hash, (ulong)chain.ReconstructionPayloadBytes);
            foreach (ObjectVersionChainEntry entry in chain.Records) {
                hash = Mix(hash, (ulong)entry.ObjectVersionPayloadBytes);
                foreach (byte value in entry.Record.Body) hash = Mix(hash, value);
            }
        }
        return hash;
    }

    private static ulong Query(IReadOnlyDictionary<uint, FrameAddress> items, int count) {
        ulong sum = 0;
        for (uint key = 0; key <= count * 3 + 1; key++) {
            if (items.TryGetValue(key, out FrameAddress value)) sum += key + unchecked((uint)value.GetHashCode());
        }
        foreach ((uint key, FrameAddress value) in items) sum += key + unchecked((uint)value.GetHashCode());
        return sum;
    }

    private static ulong Mix(ulong hash, ulong value) => unchecked((hash ^ value) * 1099511628211UL);
    private static (T Value, Cost Cost) Measure<T>(Func<T> action) {
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        T value = action();
        long end = Stopwatch.GetTimestamp();
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        return (value, new(Stopwatch.GetElapsedTime(start, end).TotalMilliseconds, bytes));
    }
    private void Write(object value) => output.WriteLine("DB067_MEASUREMENT " + JsonSerializer.Serialize(value,
        new JsonSerializerOptions { IncludeFields = true }));
    private readonly record struct Cost(double Milliseconds, long AllocatedBytes);
}
