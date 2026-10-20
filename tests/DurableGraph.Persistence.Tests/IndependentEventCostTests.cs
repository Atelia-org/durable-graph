using System.Buffers;
using System.Diagnostics;
using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Xunit.Abstractions;

namespace Atelia.DurableGraph.Persistence.Tests;

// DB-097 observational cost witness. No timing/allocation threshold encodes a performance promise.
public sealed class IndependentEventCostTests(ITestOutputHelper output) : IDisposable {
    private const string Prefix = "dg-independent-event-cost-";
    private readonly List<string> _paths = [];

    [Theory]
    [InlineData(0, "small")]
    [InlineData(1000, "small")]
    [InlineData(10000, "small")]
    [InlineData(1000, "shared8")]
    [InlineData(10000, "shared8")]
    [InlineData(1000, "whole")]
    [InlineData(10000, "whole")]
    public void ReportsEventCostsAndPreservesValuesAndState(int stateCount, string shape) {
        Harness harness = new();
        string path = NewPath();
        Atelia.FrameStore.FrameAddress eventAddress = default;
        Atelia.FrameStore.FrameAddress? eventSource = null;
        int expectedCount = shape == "whole" ? stateCount : shape == "shared8" ? 8 : 1;
        int[] expected;
        using (RepositoryStorageFacade repository = RepositoryStorageFacade.CreateNew(path)) {
            BranchSaveSessionFixture session = new(repository, "main", harness.Models);
            Node? state = stateCount == 0 ? null : Chain(stateCount);
            if (state is not null) {
                using PreparedFixtureSave seed = session.Prepare(state);
                seed.Commit();
            }
            Node domainEvent = shape == "whole" ? state! : shape == "shared8" ? At(state!, stateCount - 8) : new() { Value = 42 };
            if (shape != "small") domainEvent.Value++;
            expected = Values(domainEvent);
            var baseline = session.Baseline;
            var source = session.StateSource;
            for (int sample = 0; sample < 2; sample++) {
                string label = $"N={stateCount} M={expectedCount} shape={shape} sample={sample}";
                BusinessSaveCandidate capture = Measure(label, "capture", () => session.Session.CaptureOwned(domainEvent, StoredGraphKind.Event));
                var catalog = repository.CatalogForPreparation.PrepareOwned(capture.Graph.Objects.Select(static row => row.Layout));
                harness.BodyCalls = 0;
                long qualifications = repository.Store.BusinessGraphQualificationCount;
                using (PreparedBusinessWrite prepared = Measure(label, "prepare-owned", () => session.Session.PrepareOwned(capture, catalog.View, null))) {
                    output.WriteLine($"{label} qualifications={repository.Store.BusinessGraphQualificationCount - qualifications} bodyCalls={harness.BodyCalls} records={prepared.Write.Records.Count} removed={prepared.Write.RemovedIds.Count}");
                }
                long bytes = FileBytes(path);
                long frames = repository.Store.AppendedFrames;
                long totalAllocation = GC.GetAllocatedBytesForCurrentThread();
                long totalStarted = Stopwatch.GetTimestamp();
                using PreparedFixtureSave save = session.Prepare(domainEvent, StoredGraphKind.Event);
                long prepareTicks = Stopwatch.GetTimestamp() - totalStarted;
                long prepareAllocation = GC.GetAllocatedBytesForCurrentThread() - totalAllocation;
                long commitStarted = Stopwatch.GetTimestamp();
                long commitAllocation = GC.GetAllocatedBytesForCurrentThread();
                RepositorySaveResult result = save.Commit();
                long commitTicks = Stopwatch.GetTimestamp() - commitStarted;
                commitAllocation = GC.GetAllocatedBytesForCurrentThread() - commitAllocation;
                long elapsed = Stopwatch.GetTimestamp() - totalStarted;
                long allocated = GC.GetAllocatedBytesForCurrentThread() - totalAllocation;
                output.WriteLine($"{label} stage=prepare-full allocated={prepareAllocation} ms={prepareTicks * 1000.0 / Stopwatch.Frequency:F3}");
                output.WriteLine($"{label} stage=commit-including-flush allocated={commitAllocation} ms={commitTicks * 1000.0 / Stopwatch.Frequency:F3}");
                output.WriteLine($"{label} totalAllocated={allocated} totalMs={elapsed * 1000.0 / Stopwatch.Frequency:F3} repositoryFileGrowth={FileBytes(path) - bytes} appendedFrames={repository.Store.AppendedFrames - frames}");
                eventAddress = result.Business.Address;
                eventSource = result.Business.SourceGraph;
                Assert.Same(baseline, session.Baseline);
                Assert.Same(source, session.StateSource);
                Node read = Assert.IsType<Node>(BusinessPersistenceAdapter.ReadRoot(repository.Store, result.Business, harness.Models));
                Assert.Equal(expected, Values(read));
                Assert.NotSame(domainEvent, read);
                read.Value = -777;
                Assert.Equal(expected, Values(domainEvent));
            }
        }
        using RepositoryStorageFacade cold = RepositoryStorageFacade.OpenReadOnlyExisting(path);
        Node restored = Measure($"N={stateCount} M={expectedCount} shape={shape}", "cold-read", () => {
            var graph = cold.Store.ReadBusinessGraph(eventAddress, eventSource);
            return Assert.IsType<Node>(BusinessPersistenceAdapter.ReadRoot(cold.Store, graph, harness.Models));
        });
        Assert.Equal(expected, Values(restored));
    }

    private T Measure<T>(string label, string stage, Func<T> action) {
        long allocation = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        T result = action();
        long elapsed = Stopwatch.GetTimestamp() - started;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocation;
        output.WriteLine($"{label} stage={stage} allocated={allocated} ms={elapsed * 1000.0 / Stopwatch.Frequency:F3}");
        return result;
    }

    // Logical lengths after durable commit, including control/history files; not physical disk blocks or syscall bytes.
    private static long FileBytes(string path) => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(static file => new FileInfo(file).Length);
    private static Node At(Node root, int offset) { while (offset-- > 0) root = root.Next!; return root; }
    private static int[] Values(Node root) { List<int> values = []; for (Node? node = root; node is not null; node = node.Next) values.Add(node.Value); return values.ToArray(); }
    private static Node Chain(int count) {
        Node? next = null;
        for (int index = count - 1; index >= 0; index--) next = new Node { Value = index, Next = next };
        return next!;
    }

    private sealed class Node : IDurableObject {
        internal int Value;
        internal Node? Next;
    }

    private readonly record struct NodeState(int Value, ObjectId Next);

    private sealed class Harness {
        private const string SchemaId = "independent-event-cost.node";
        private static readonly DurableSchema Schema = new(SchemaId, 1,
            new DurableFieldInfo(1, TypeTag.Int32), new DurableFieldInfo(2, TypeTag.ObjectReference, SchemaId));
        internal StateModelRegistry Models { get; } = new();
        internal int BodyCalls;


        internal Harness() {
            StatePreparationBinding<NodeState> preparation = new(Schema, PrepareBase, PrepareDelta,
                static (in NodeState left, in NodeState right) => left == right);
            StateReaderBinding<NodeState> reader = new(Schema, Read,
                static (ref BareValueReader input, in NodeState prior) => {
                    NodeState current = Read(ref input);
                    if (current == prior) throw new InvalidDataException("A child patch must change its prior.");
                    return current;
                }, Visit);
            Models.Register(new StateModelBinding<Node, NodeState>(preparation, [reader],
                static source => source.GetState<NodeState>(), static () => new Node(),
                static (Node target, in NodeState state, ObjectReadTable objects) => {
                    target.Value = state.Value;
                    target.Next = objects.ResolveDurable<Node>(state.Next);
                }, static (value, capture) => new(value.Value, capture.CaptureDurable(value.Next, SchemaId)), Visit));
        }

        private PreparedBaseBody PrepareBase(in NodeState value) {
            BodyCalls++;

            return new(Encode(in value));
        }

        private PreparedDeltaBody PrepareDelta(in NodeState prior, in NodeState current) {
            BodyCalls++;

            return prior == current ? new(false, []) : new(true, Encode(in current));
        }

        private static byte[] Encode(in NodeState state) {
            ArrayBufferWriter<byte> buffer = new();
            BareValueWriter writer = new(buffer);
            writer.WriteVarInt32(state.Value);
            writer.WriteVarUInt32(state.Next.Value);
            return buffer.WrittenSpan.ToArray();
        }

        private static NodeState Read(ref BareValueReader reader) => new(reader.ReadVarInt32(), new(reader.ReadVarUInt32()));
        private static void Visit(in NodeState state, IStateReferenceVisitor visitor) => visitor.VisitDurable(state.Next, SchemaId);
    }

    private string NewPath() {
        string path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
        _paths.Add(path);
        return path;
    }

    public void Dispose() {
        foreach (string path in _paths) SharedReadModel.DeleteFixture(path, Prefix);
    }
}


