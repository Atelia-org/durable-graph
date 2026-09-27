using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.RbfSegmentStore;
using Xunit.Abstractions;

namespace Atelia.DurableGraph.Persistence.Tests;

/// <summary>Opt-in paired full-Fork measurements. No elapsed-time assertions or physical-I/O claims.</summary>
public sealed class DB080PreparedStateMeasurements(ITestOutputHelper output) {
    private const int Nodes = 64;
    private const int Forks = 12;
    private const int RecordedSamples = 3;
    private const string FixturePrefix = "durable-db080-measure-";
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1000000, 1);
    private static readonly TypeExpr NodeType = TypeExpr.Named("DB080MeasureNode");
    private static readonly TypeExpr LinksType = TypeExpr.List(NodeType);
    private static readonly DurableSchema Schema = new("DB080MeasureNode", 1,
        DurableFieldInfo.Reference(1, NodeType), DurableFieldInfo.Reference(2, LinksType),
        new DurableFieldInfo(3, TypeTag.Int32));

    [Fact]
    public void Measure_complete_fork_with_and_without_prepared_state() {
        if (Environment.GetEnvironmentVariable("DURABLEGRAPH_DB080_MEASURE") != "1") { return; }
        string? destination = Environment.GetEnvironmentVariable("DURABLEGRAPH_DB080_MEASURE_OUTPUT");
        if (destination is not null) {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        }
        using StreamWriter? file = destination is null ? null : new(destination, append: false);
        Write(new {
            Kind = "configuration", Nodes, Forks, RecordedSamples, DiscardedWarmupSamples = 1,
            Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            OperatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            Timer = "Fork entry through returned checkout, including durable named-ref publication; excludes Dispose and assertions",
            Allocation = "GC.GetAllocatedBytesForCurrentThread; excludes other threads and native memory",
            Pairing = "Fresh repository per cell/sample; same seeded model/history; enabled-first order alternates by sample",
            Cache = "Store and OS caches not disabled; prepared-slot toggle is the only product-path difference",
            Family = "Hand-written non-generic StateDefinitionBinding, same runtime Family closure route; not generator/package evidence"
        }, file);
        foreach (bool family in new[] { false, true }) {
            foreach (bool containers in new[] { false, true }) {
                foreach (string scenario in new[] { "same-state", "events-share-state", "alternating-state", "event-only" }) {
                    for (int sample = -1; sample < RecordedSamples; sample++) {
                        foreach (bool enabled in sample % 2 == 0 ? new[] { true, false } : new[] { false, true }) {
                            Run(family, containers, scenario, sample, enabled, file);
                        }
                    }
                }
            }
        }
    }

    private void Run(bool family, bool containers, string scenario, int sample, bool enabled, StreamWriter? file) {
        string path = Path.Combine(Path.GetTempPath(), FixturePrefix + Guid.NewGuid().ToString("N"));
        try {
            using Repository repository = Repository.CreateNew(path, Models(family),
                new() { NewStoreLayout = RbfSegmentStoreLayout.Flat });
            repository.PreparedStateReuseEnabled = enabled;
            CheckpointAddress[] requests = Seed(repository, containers, scenario);
            GraphReadStatistics statistics = new();
            repository.RestorationStatistics = statistics;
            List<object> operations = [];
            double totalMilliseconds = 0;
            long totalAllocatedBytes = 0;
            Node? previous = null;
            for (int index = 0; index < Forks; index++) {
                string name = $"fork-{index}";
                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                BranchCheckout fork = repository.Fork(name, requests[index]);
                long end = Stopwatch.GetTimestamp();
                long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                double milliseconds = Stopwatch.GetElapsedTime(start, end).TotalMilliseconds;
                using (fork) {
                    Assert.Equal(requests[index], fork.Head);
                    Assert.Equal(requests[index], repository.GetHead(name));
                    if (scenario == "event-only") {
                        Assert.Null(fork.State);
                    } else {
                        Node root = Assert.IsType<Node>(fork.State);
                        Assert.NotSame(previous, root);
                        AssertGraph(root, containers, scenario == "alternating-state" && index % 2 != 0 ? 1000 : 0);
                        previous = root;
                    }
                }
                totalMilliseconds += milliseconds;
                totalAllocatedBytes += allocated;
                operations.Add(new { Index = index, CompleteForkMilliseconds = milliseconds, AllocatedBytes = allocated });
            }
            if (sample >= 0) Write(new {
                Kind = "sample", Family = family, Containers = containers, Scenario = scenario,
                Sample = sample, Enabled = enabled, Nodes, Forks,
                CompleteForkMilliseconds = totalMilliseconds, AllocatedBytes = totalAllocatedBytes,
                statistics.PreparedStateHits, statistics.PreparedStateMisses,
                statistics.DecodedObjects, statistics.CacheHits, statistics.NormalizedObjects,
                statistics.PreparedGraphs, statistics.ReachabilityVisits,
                statistics.CurrentReferenceValidationVisits, statistics.DictionaryValidationCalls,
                statistics.AllocatedObjects, statistics.HydratedObjects,
                SlotRows = repository.PreparedStateEntry?.Selection.Normalized.Objects.Count ?? 0,
                SlotReachable = repository.PreparedStateEntry?.Selection.Reachable.Count ?? 0,
                SlotExactRequirements = repository.PreparedStateEntry?.SchemaRequirementCount ?? 0,
                SlotLogicalPayloadBytes = repository.PreparedStateEntry is null ? 0 :
                    Nodes * 12L + (containers ? Nodes * 4L : 0) + (Nodes + (containers ? 1 : 0)) * 4L,
                ResidencyEstimate = "Logical DTO fields plus reachable ObjectIds only; not measured heap bytes. Excludes object headers, dictionaries, layouts, provenance, certificate paths and shared model/Schema references; one slot has no byte cap.",
                Operations = operations
            }, file);
        } finally {
            SharedReadModel.DeleteFixture(path, FixturePrefix);
        }
    }

    private static CheckpointAddress[] Seed(Repository repository, bool containers, string scenario) {
        Node root = Graph(containers);
        using BranchCheckout branch = scenario == "event-only"
            ? repository.CreateBranchFromEvent("main", root, NoRebase)
            : repository.CreateBranch("main", root, NoRebase);
        CheckpointAddress first = branch.Head;
        CheckpointAddress[] requests = new CheckpointAddress[Forks];
        if (scenario == "alternating-state") {
            root.Value = 1000;
            CheckpointAddress second = branch.CommitState(NoRebase);
            for (int index = 0; index < Forks; index++) { requests[index] = index % 2 == 0 ? first : second; }
        } else if (scenario is "events-share-state" or "event-only") {
            requests[0] = first;
            for (int index = 1; index < Forks; index++) {
                requests[index] = branch.CommitEvent(new Node { Value = index }, NoRebase);
            }
        } else {
            Array.Fill(requests, first);
        }
        return requests;
    }

    private static Node Graph(bool containers) {
        Node[] nodes = Enumerable.Range(0, Nodes).Select(index => new Node { Value = index }).ToArray();
        for (int index = 0; index < Nodes; index++) { nodes[index].Next = nodes[(index + 1) % Nodes]; }
        if (containers) { nodes[0].Links = nodes.ToList(); }
        return nodes[0];
    }

    private static void AssertGraph(Node root, bool containers, int rootValue) {
        Node cursor = root;
        for (int index = 0; index < Nodes; index++) {
            Assert.Equal(index == 0 ? rootValue : index, cursor.Value);
            if (containers) { Assert.Same(cursor, root.Links![index]); }
            cursor = Assert.IsType<Node>(cursor.Next);
        }
        Assert.Same(root, cursor);
        if (containers) { Assert.Equal(Nodes, root.Links!.Count); }
        else { Assert.Null(root.Links); }
    }

    private void Write(object row, StreamWriter? file) {
        string json = JsonSerializer.Serialize(row);
        output.WriteLine("DB080_MEASUREMENT " + json);
        file?.WriteLine(json);
        file?.Flush();
    }

    private static StateModelRegistry Models(bool family) {
        StateModelRegistry models = new();
        if (family) {
            models.Register(new StateDefinitionBinding(Schema.SchemaId, SchemaKind.ReferenceObject, 0, typeof(Node),
                [new(Schema.SchemaId, 1, SchemaKind.ReferenceObject, 0,
                    [new(1, NodeType), new(2, LinksType), new(3, TypeExpr.Builtin(TypeTag.Int32))])],
                currentModelFactory: (_, context) => Model(context.ResolveReader(Schema)),
                historicalReaderFactory: static (schema, _) => Reader(schema)));
        } else {
            models.Register(Model(Reader(Schema)));
        }
        return models;
    }

    private static StateModelBinding<Node, State> Model(StateReaderBinding reader) => new(
        new CapturedStatePreparation<State>(Schema, Base,
            static (in State prior, in State next) => new(prior != next, Base(next).Body),
            static (in State left, in State right) => left == right),
        [reader], static row => row.GetState<State>(), static () => new Node(),
        static (Node target, in State state, ObjectReadTable objects) => {
            target.Next = objects.ResolveDurable<Node>(state.Next);
            target.Links = objects.ResolveObject<List<Node>>(state.Links);
            target.Value = state.Value;
        }, static (source, context) => new(context.CaptureDurable(source.Next, Schema.SchemaId),
            context.CaptureObject(source.Links, LinksType), source.Value), Visit);

    private static StateReaderBinding<State> Reader(DurableSchema schema) => new(schema, Read,
        static (ref BinaryPayloadReader reader, in State _) => Read(ref reader), Visit);
    private static State Read(ref BinaryPayloadReader reader) => new(new(reader.ReadUInt32()), new(reader.ReadUInt32()), reader.ReadInt32());
    private static void Visit(in State state, IStateReferenceVisitor visitor) {
        visitor.VisitDurable(state.Next, Schema.SchemaId);
        visitor.VisitObject(state.Links, LinksType);
    }
    private static PreparedBaseBody Base(in State state) {
        ArrayBufferWriter<byte> bytes = new();
        BinaryPayloadWriter writer = new(bytes);
        writer.WriteUInt32(state.Next.Value);
        writer.WriteUInt32(state.Links.Value);
        writer.WriteInt32(state.Value);
        return new(bytes.WrittenSpan);
    }
    private sealed class Node : IDurableObject {
        internal Node? Next;
        internal List<Node>? Links;
        internal int Value;
    }
    private readonly record struct State(ObjectId Next, ObjectId Links, int Value);
}
