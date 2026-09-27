using System.Diagnostics;
using System.Text.Json;
using Atelia.RbfSegmentStore;
using Xunit.Abstractions;
using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;

namespace Atelia.DurableGraph.Persistence.Tests;

/// <summary>Small real-product samples, with the default cache; no before/after attribution or time thresholds.</summary>
public sealed class ReadCacheProductMeasurementTests(ITestOutputHelper output) {
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1000000, 1);

    [Fact]
    public void Measure_open_read_pair_resume_and_commit() {
        for (int sample = -1; sample < 3; sample++) {
            string path = Path.Combine(Path.GetTempPath(), $"durable-cache-product-measure-{Guid.NewGuid():N}");
            try {
                StateModelRegistry models = SharedReadModel.Models();
                Node world = new() { Value = 1, Text = new string('x', 32), Links = [] };
                for (byte i = 0; i < 16; i++) world.Links.Add(new() { Value = i, Text = world.Text });
                world.Next = world.Alias = world.Links[0];
                using (Repository repository = Repository.CreateNew(path, models, new() { NewStoreLayout = RbfSegmentStoreLayout.Flat })) {
                    using BranchCheckout session = repository.CreateBranch("main", world, NoRebase);
                    for (int step = 0; step < 3; step++) {
                        session.CommitEvent(new Node { Value = 7 }, NoRebase);
                        world.Links[step].Value++;
                        session.CommitState(NoRebase);
                    }
                    session.CommitEvent(new Node { Value = 9, Links = world.Links }, NoRebase);
                }
                ulong expected = Checksum(world);
                var open = Measure(() => Repository.OpenReadOnlyExisting(path, models));
                using (Repository repository = open.Value) {
                    CheckpointAddress pending = repository.GetHead("main");
                    CheckpointAddress state = Assert.IsType<CheckpointAddress>(repository.GetPreviousState(pending));
                    var first = Measure(() => ((Node)repository.ReadState(state)));
                    var warm = Measure(() => ((Node)repository.ReadState(state)));
                    var pair = Measure(() => (((Node First, Node Second))repository.ReadPair(state, pending)));
                    var pairWarm = Measure(() => (((Node First, Node Second))repository.ReadPair(state, pending)));
                    Assert.Equal(expected, Checksum(first.Value));
                    Assert.Equal(expected, Checksum(warm.Value));
                    Assert.Equal(expected, Checksum(pair.Value.First));
                    Assert.Equal(expected, Checksum(pairWarm.Value.First));
                    Assert.Equal((byte)9, pair.Value.Second.Value);
                    Assert.Equal(16, pair.Value.Second.Links!.Count);
                    if (sample >= 0) Write(new { Kind = "product-read", Sample = sample, DurableNodes = 17,
                        CompletedStateChanges = 3, HeadIsEvent = true, OpenReadOnly = open.Cost,
                        FirstReadAfterOpen = first.Cost, RepeatedRead = warm.Cost,
                        ReadPairAfterReads = pair.Cost, RepeatedReadPair = pairWarm.Cost, Checksum = expected });
                }

                var writableOpen = Measure(() => Repository.OpenExisting(path, models));
                using (Repository repository = writableOpen.Value) {
                    var resume = Measure(() => repository.Checkout("main"));
                    using BranchCheckout session = resume.Value;
                    Node editableState = Assert.IsType<Node>(session.State);
                    Assert.NotNull(editableState.Links);
                    Assert.Equal(expected, Checksum(editableState));
                    Assert.NotSame(editableState.Links, ((Node)repository.ReadEvent(session.Head)).Links);
                    Assert.NotSame(editableState.Links![0], ((Node)repository.ReadEvent(session.Head)).Links![0]);
                    editableState.Links[0].Value++;
                    ulong nextExpected = Checksum(editableState);

                    Stamp afterPrepare = default, beforeAppend = default, afterAppend = default;
                    repository.Checkpoint = checkpoint => {
                        if (checkpoint == CommitCheckpoint.AfterPrepare) afterPrepare = Stamp.Now();
                        else if (checkpoint == CommitCheckpoint.BeforeStateAppend) beforeAppend = Stamp.Now();
                        else if (checkpoint == CommitCheckpoint.AfterStateDurable) afterAppend = Stamp.Now();
                    };
                    Stamp start = Stamp.Now();
                    CheckpointAddress committed = session.CommitState(NoRebase);
                    Stamp end = Stamp.Now();
                    repository.Checkpoint = null;
                    Assert.True(afterPrepare.Timestamp > 0 && beforeAppend.Timestamp > 0 && afterAppend.Timestamp > 0);
                    Assert.Equal(nextExpected, Checksum(((Node)repository.ReadState(committed))));
                    if (sample >= 0) Write(new { Kind = "product-write", Sample = sample,
                        OpenWritable = writableOpen.Cost, ResumeAfterOpen = resume.Cost,
                        PrepareIncludingEntryPreflight = Between(start, afterPrepare),
                        StateAppendDurably = Between(beforeAppend, afterAppend),
                        CompleteStateCommit = Between(start, end), Checksum = nextExpected });
                }
            } finally {
                SharedReadModel.DeleteFixture(path, "durable-cache-product-measure-");
            }
        }
    }

    private static ulong Checksum(Node node) {
        ulong sum = node.Value;
        if (node.Text is { } text) foreach (char value in text) sum = unchecked(sum * 31 + value);
        if (node.Links is { } links) {
            foreach (Node child in links) {
                sum = unchecked(sum * 31 + child.Value);
                if (child.Text is { } childText) foreach (char value in childText) sum = unchecked(sum * 31 + value);
            }
        }
        return sum;
    }
    private static (T Value, Cost Cost) Measure<T>(Func<T> action) {
        Stamp start = Stamp.Now();
        T value = action();
        return (value, Between(start, Stamp.Now()));
    }
    private static Cost Between(Stamp start, Stamp end) => new(
        Stopwatch.GetElapsedTime(start.Timestamp, end.Timestamp).TotalMilliseconds, end.AllocatedBytes - start.AllocatedBytes);
    private void Write(object value) => output.WriteLine("DB067_MEASUREMENT " + JsonSerializer.Serialize(value));
    private readonly record struct Cost(double Milliseconds, long AllocatedBytes);
    private readonly record struct Stamp(long Timestamp, long AllocatedBytes) {
        internal static Stamp Now() => new(Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread());
    }
}
