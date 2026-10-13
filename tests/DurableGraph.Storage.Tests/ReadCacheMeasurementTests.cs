using System.Diagnostics;
using System.Text.Json;
using Atelia.FrameStore;
using static Atelia.DurableGraph.Storage.Tests.StorageTestFrames;
using Xunit.Abstractions;

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
                    Repeated100QueryAndTraversal = repeated.Cost, Checksum = first.Value });
            }
        }
    }

    private static ulong Query(IReadOnlyDictionary<uint, FrameAddress> items, int count) {
        ulong sum = 0;
        for (uint key = 0; key <= count * 3 + 1; key++) {
            if (items.TryGetValue(key, out FrameAddress value)) sum += key + unchecked((uint)value.GetHashCode());
        }
        foreach ((uint key, FrameAddress value) in items) sum += key + unchecked((uint)value.GetHashCode());
        return sum;
    }

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
