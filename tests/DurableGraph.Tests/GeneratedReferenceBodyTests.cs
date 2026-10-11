using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using System.Reflection;
using Atelia.DurableGraph.Build;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Theory]
    [InlineData("new Holder.__DurableState.V1(7u, default)")]
    [InlineData("new Holder.__DurableState.V1(default, new ObjectId(7))")]
    public void GeneratedReferenceSlotsCannotBeInterchangedWithNumericUInt32(string invalidConstruction) {
        const string source = """
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("object-id.holder", 1)]
            public partial class Holder : IDurableObject {
                [DurableField(1)] private string? _text;
                [DurableField(2)] private uint _number;
            }
            """;
        GeneratorTestRun valid = RunGenerator(source);
        AssertSchemaOnlyCompiles(valid);
        Type dto = EmitAndLoad(valid.OutputCompilation).GetType("Holder")!
            .GetNestedType("__DurableState", BindingFlags.NonPublic)!
            .GetNestedType("V1", BindingFlags.NonPublic)!;
        Assert.Equal(typeof(ObjectId), dto.GetField("Segment0Field1", BindingFlags.Instance | BindingFlags.NonPublic)!.FieldType);
        Assert.Equal(typeof(uint), dto.GetField("Segment0Field2", BindingFlags.Instance | BindingFlags.NonPublic)!.FieldType);
        GeneratorTestRun invalid = RunGenerator(source + "\npublic static class Host { public static object Create() => " + invalidConstruction + "; }");
        Assert.Contains(invalid.OutputCompilation.GetDiagnostics(), diagnostic => diagnostic.Id == "CS1503");
    }

    [Fact]
    public void GeneratedReferenceBodiesUseUInt32IdentityAndVisitEveryConstraint() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using Atelia.DurableGraph.Serialization;
            namespace ReferenceBodies;
            [DurableType("node", 1)]
            public partial class Node : IDurableObject {
                [DurableField(1)] private Node? _next;
                [DurableField(2)] private string? _name;
                [DurableField(3)] private Node? _other;
            }
            public static class Host {
                public static PreparedBaseBody Base() {
                    var state = new Node.__DurableState.V1(new(128), new(4), default);
                    return Node.__DurableState.PrepareBaseBody(in state);
                }
                public static PreparedDeltaBody Delta() {
                    var prior = new Node.__DurableState.V1(new(128), new(4), default);
                    var current = new Node.__DurableState.V1(new(128), new(4), new(129));
                    return Node.__DurableState.PrepareDeltaBody(in prior, in current);
                }
                public static byte[] Replay(byte[] delta) {
                    var prior = new Node.__DurableState.V1(new(128), new(4), default);
                    var reader = new BareValueReader(delta);
                    var current = Node.__DurableState.ApplyDeltaBodyV1(ref reader, in prior);
                    return Node.__DurableState.PrepareBaseBody(in current).Body.ToArray();
                }
                public static void Visit(IStateReferenceVisitor visitor) {
                    var state = new Node.__DurableState.V1(new(128), new(4), default);
                    Node.__DurableState.VisitReferences(in state, visitor);
                }
            }
            """);
        AssertSchemaOnlyCompiles(run);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("ReferenceBodies.Host")!;
        Assert.Equal<byte>([0x80, 1, 4, 0], host.GetMethod("Base")!.CreateDelegate<Func<PreparedBaseBody>>()().Body.ToArray());
        PreparedDeltaBody delta = host.GetMethod("Delta")!.CreateDelegate<Func<PreparedDeltaBody>>()();
        Assert.True(delta.HasChanges);
        Assert.Equal<byte>([4, 0x81, 1], delta.Body.ToArray());
        Assert.Equal<byte>([0x80, 1, 4, 0x81, 1],
            host.GetMethod("Replay")!.CreateDelegate<Func<byte[], byte[]>>()(delta.Body.ToArray()));
        ReferenceBodyVisitor visitor = new();
        host.GetMethod("Visit")!.CreateDelegate<Action<IStateReferenceVisitor>>()(visitor);
        Assert.Equal(["durable:128:node", "string:4", "durable:0:node"], visitor.Entries);
        string generated = GeneratedSource(run, "DurableStates.g.cs");
        AssertGeneratedBodiesRemainStaticallyBound(generated);
        Assert.Contains("writer.WriteVarUInt32(current.Segment0Field3.Value)", generated);
        Assert.DoesNotContain("Node.__DurableState.Model", generated);
    }

    [Fact]
    public void GeneratedReadonlyInheritedReferencesCaptureAndRestoreSharedMutualGraph() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            namespace ReferenceBodies;
            [DurableType("base", 1)]
            public abstract partial class Base : IDurableObject {
                [DurableField(7)] private readonly Base? _parent;
                public Base? Parent => _parent;
                protected Base(Base? parent) { _parent = parent; }
            }
            [DurableType("node", 1)]
            public sealed partial class Node : Base {
                [DurableField(1)] private readonly Node? _self;
                [DurableField(2)] private Node? _child;
                [DurableField(3)] private string? _name;
                [Transient] public int Cache = 99;
                public static int Calls;
                public Node() : base(null) {
                    Calls++; _self = this; _name = "shared";
                    _child = new Node(this);
                }
                private Node(Node parent) : base(parent) { Calls++; _self = this; _name = parent._name; }
                public Node? Self => _self;
                public Node? Child => _child;
            }
            public static class Host {
                public static StateModelBinding[] Models() => [Node.__DurableState.Model, Base.__DurableState.Model];
                public static IDurableObject New() => new Node();
                public static ObjectId AddRoot(CaptureContext context, IDurableObject root) => Node.__DurableState.AddRoot(context, (Node)root);
            }
            """);
        AssertSchemaOnlyCompiles(run);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        Type host = assembly.GetType("ReferenceBodies.Host")!;
        StateModelBinding[] models = host.GetMethod("Models")!.CreateDelegate<Func<StateModelBinding[]>>()();
        IDurableObject root = host.GetMethod("New")!.CreateDelegate<Func<IDurableObject>>()();
        var addRoot = host.GetMethod("AddRoot")!.CreateDelegate<Func<CaptureContext, IDurableObject, ObjectId>>();
        CaptureSession session = new();
        using CaptureContext capture = session.BeginCapture(models);
        ObjectId rootId = addRoot(capture, root);
        CapturedGraph graph = capture.Seal();
        Assert.Equal(3, graph.Objects.Count);
        Assert.Equal(rootId, Assert.Single(graph.RootIds));
        ObjectStateRecord[] durable = graph.Objects.Where(item => item.Schema is not null).ToArray();
        Assert.Equal(2, durable.Length);
        StateModelBinding node = models[0];
        Dictionary<ObjectId, IDurableObject> instances = durable.ToDictionary(item => item.Id, _ => node.Allocate());
        ObjectReadTable objects = new(StringReadTable.FromDecoded(graph.Objects
            .Where(item => item.Schema is null).Select(item => (item.Id, item.StringContent))), instances);
        foreach (ObjectStateRecord item in durable) node.Hydrate(instances[item.Id], item, objects);
        IDurableObject restored = instances[rootId];
        Type type = restored.GetType();
        object child = type.GetProperty("Child")!.GetValue(restored)!;
        Assert.Same(restored, type.GetProperty("Self")!.GetValue(restored));
        Assert.Same(child, type.GetProperty("Self")!.GetValue(child));
        Assert.Same(restored, type.GetProperty("Parent")!.GetValue(child));
        Assert.Null(type.GetProperty("Parent")!.GetValue(restored));
        Assert.Equal(0, type.GetField("Cache")!.GetValue(restored));
        Assert.Equal(2, type.GetField("Calls")!.GetValue(null));
        Assert.NotSame(root, restored);
        AssertGeneratedBodiesRemainStaticallyBound(GeneratedSource(run, "DurableStates.g.cs"));
    }

    [Fact]
    public void GeneratedHistoricalReferencesRetainNominalConstraintsAfterOldClrTypesAreRemoved() {
        using AncestryHistoryDirectory files = new();
        SchemaHistoryTool publisher = new();
        GeneratorTestRun initial = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            namespace ReferenceBodies;
            [DurableType("retired", 1)]
            public partial class Retired : IDurableObject { }
            [DurableType("owner", 1)]
            public partial class Owner : IDurableObject { [DurableField(8)] private Retired? _reference; }
            """);
        AssertSchemaOnlyCompiles(initial);
        publisher.Publish(files.WriteManifest(initial), files.History);
        GeneratorTestRun current = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            namespace ReferenceBodies;
            [DurableType("owner", 2)]
            public partial class Owner : IDurableObject {
                private static void UpgradeStateV1ToV2(in __DurableState.V1 prior, out __DurableState.V2 next) => next = default;
            }
            public static class Host {
                public static StateModelBinding Model() => Owner.__DurableState.Model;
                public static object Old() => new Owner.__DurableState.V1(new(91));
            }
            """, files.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(current);
        Type host = EmitAndLoad(current.OutputCompilation).GetType("ReferenceBodies.Host")!;
        StateModelBinding model = host.GetMethod("Model")!.CreateDelegate<Func<StateModelBinding>>()();
        ObjectStateRecord old = new(new(1), model.Readers[0].Schema, host.GetMethod("Old")!.CreateDelegate<Func<object>>()());
        ReferenceBodyVisitor visitor = new();
        model.Readers[0].VisitReferences(old, visitor);
        Assert.Equal(["durable:91:retired"], visitor.Entries);
        visitor.Entries.Clear();
        ObjectStateRecord normalized = model.Normalize(old);
        model.VisitReferences(normalized, visitor);
        Assert.Empty(visitor.Entries);
        Assert.Equal(2, normalized.Schema!.Version);
        Assert.Equal("retired", model.Readers[0].Schema.Fields[0].TargetSchemaId);
        AssertGeneratedBodiesRemainStaticallyBound(GeneratedSource(current, "DurableStates.g.cs"));
    }

    private sealed class ReferenceBodyVisitor : IStateReferenceVisitor {
        public List<string> Entries { get; } = [];
        public void VisitString(ObjectId objectId) => Entries.Add($"string:{objectId.Value}");
        public void VisitDurable(ObjectId objectId, string nominalSchemaId) => Entries.Add($"durable:{objectId.Value}:{nominalSchemaId}");
    }
}
