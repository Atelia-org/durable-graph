using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Reflection;
using Atelia.DurableGraph.Build;
using Atelia.DurableGraph.Persistence;
using Microsoft.CodeAnalysis;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedStateModelUpgradesAdjacentDtosOnceAndKeepsCurrentFastPathAndInput(bool forceFamily) {
        using AncestryHistoryDirectory files = new();
        SchemaHistoryTool publisher = new();
        foreach (int version in new[] { 1, 2 }) {
            GeneratorTestRun previous = RunGenerator(StateModelHistorySource(version),
                version == 1 ? [] : files.ReadAdditionalTexts(), null, forceFamily ? "true" : null);
            AssertSchemaOnlyCompiles(previous);
            publisher.Publish(files.WriteManifest(previous), files.History);
        }
        Dictionary<string, string> before = files.ReadContents();
        string source = StateModelHistorySource(3) + """
            public partial class Item {
                private static void UpgradeStateV1ToV2(in __DurableState.V1 prior, out __DurableState.V2 next) {
                    if (prior.Segment0Field1 < 0) throw new System.InvalidOperationException("user upgrade failure");
                    next = new(prior.Segment0Field1 + 1, 9);
                }
                private static void UpgradeStateV2ToV3(in __DurableState.V2 prior, out __DurableState.V3 next) {
                    next = new(prior.Segment0Field1 * 2, prior.Segment0Field2, true);
                }
            }
            public static class Host {
                public static Atelia.DurableGraph.Persistence.StateModelRegistry Models() {
                    var models = new Atelia.DurableGraph.Persistence.StateModelRegistry();
                    models.Register(Item.__DurableState.Model);
                    return models;
                }
                public static object State(int version, int value) => version switch {
                    1 => new Item.__DurableState.V1(value),
                    2 => new Item.__DurableState.V2(value, 9),
                    _ => new Item.__DurableState.V3(value, 4, false),
                };
            }
            """;
        if (forceFamily) {
            source = source.Replace("models.Register(Item.__DurableState.Model);",
                "Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);")
                .Replace("Item.__DurableState", "global::Atelia.DurableGraph.Generated.Family_73746174652E6D6F64656C")
                .Replace("__DurableState", "global::Atelia.DurableGraph.Generated.Family_73746174652E6D6F64656C");
        }
        GeneratorTestRun run = RunGenerator(source, files.ReadAdditionalTexts(), null, forceFamily ? "true" : null);
        AssertSchemaOnlyCompiles(run);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("StateModels.Host")!;
        StateModelRegistry models = host.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>()();
        StateModelBinding model = models.Snapshot().ResolveCurrentModel(host.Assembly.GetType("StateModels.Item")!);
        var state = host.GetMethod("State")!.CreateDelegate<Func<int, int, object>>();
        ObjectStateRecord old = new(new(23), new DurableSchema("state.model", 1, new DurableFieldInfo(1, TypeTag.Int32)), state(1, 5));
        ObjectStateRecord normalized = model.Normalize(old);
        Assert.Equal(new ObjectId(23), normalized.Id);
        Assert.Equal(3, normalized.Schema!.Version);
        Assert.Equal(12, StateModelField(normalized, "Segment0Field1"));
        Assert.Equal((byte)9, StateModelField(normalized, "Segment0Field2"));
        Assert.Equal(true, StateModelField(normalized, "Segment0Field3"));
        Assert.Equal(5, StateModelField(old, "Segment0Field1"));
        Assert.Null(old.Preparation);
        Assert.NotNull(normalized.Preparation);
        ObjectStateRecord current = model.Normalize(new(new(23), model.CurrentSchema, state(3, 42)));
        Assert.Equal(42, StateModelField(current, "Segment0Field1"));
        Assert.Equal(false, StateModelField(current, "Segment0Field3"));
        Assert.Same(normalized.Preparation, current.Preparation);
        Assert.Throws<InvalidOperationException>(() => model.Normalize(new(new(23), model.CurrentSchema, state(1, 42))));
        Assert.Throws<InvalidDataException>(() => model.Normalize(new(new(23), new DurableSchema("state.model", 3, []), state(3, 42))));
        Assert.Throws<InvalidOperationException>(() => model.Normalize(new(new(23), old.Schema!, state(1, -1))));
        Assert.Throws<InvalidDataException>(() => model.Normalize(new(new(23), new DurableSchema("state.model", 4, []), state(3, 1))));
        Assert.Throws<InvalidDataException>(() => model.Normalize(new(new(23), new DurableSchema("state.model", 1, []), state(1, 1))));
        foreach ((string name, string contents) in before) Assert.Equal(contents, File.ReadAllText(Path.Combine(files.History, name)));
    }

    [Fact]
    public void GeneratedStateModelMissingUpgradePreservesReadersAndOnlyRejectsAffectedOldVersions() {
        using AncestryHistoryDirectory files = new();
        SchemaHistoryTool publisher = new();
        foreach (int version in new[] { 1, 2 }) {
            GeneratorTestRun previous = version == 1
                ? RunGenerator(StateModelHistorySource(version))
                : RunGenerator(StateModelHistorySource(version), files.ReadAdditionalTexts());
            AssertSchemaOnlyCompiles(previous);
            publisher.Publish(files.WriteManifest(previous), files.History);
        }
        GeneratorTestRun run = RunGenerator(StateModelHistorySource(3) + """
            public partial class Item {
                private static void UpgradeStateV2ToV3(in __DurableState.V2 prior, out __DurableState.V3 next) {
                    next = new(prior.Segment0Field1, prior.Segment0Field2, true);
                }
            }
            public static class Host {
                public static StateModelBinding Model() => Item.__DurableState.Model;
                public static object State(int version) => version == 1
                    ? (object)new Item.__DurableState.V1(8) : new Item.__DurableState.V2(8, 2);
                public static int ReadOld() {
                    var reader = new Atelia.DurableGraph.Serialization.BinaryPayloadReader(new byte[] { 16 });
                    return Item.__DurableState.ReadBaseBodyV1(ref reader).Segment0Field1;
                }
            }
            """, files.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(run);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("StateModels.Host")!;
        StateModelBinding model = host.GetMethod("Model")!.CreateDelegate<Func<StateModelBinding>>()();
        var state = host.GetMethod("State")!.CreateDelegate<Func<int, object>>();
        Assert.Equal(8, host.GetMethod("ReadOld")!.CreateDelegate<Func<int>>()());
        Assert.Contains("UpgradeStateV1ToV2", Assert.Throws<InvalidDataException>(() =>
            model.Normalize(new(new(1), model.Readers[0].Schema, state(1)))).Message);
        Assert.Equal(8, StateModelField(model.Normalize(new(new(1), model.Readers[1].Schema, state(2))), "Segment0Field1"));
    }

    [Fact]
    public void GeneratedStateModelLeafUpgradeOwnsExactAncestorLayoutWithoutRunningAncestorUpgrade() {
        using AncestryHistoryDirectory files = new();
        SchemaHistoryTool publisher = new();
        GeneratorTestRun initial = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            namespace StateModels;
            [DurableType("state.base", 1)]
            public abstract partial class OldBase : IDurableObject { [DurableField(4)] private int _old; }
            [DurableType("state.leaf", 1)]
            public sealed partial class Leaf : OldBase { [DurableField(1)] private string? _name; }
            """);
        AssertSchemaOnlyCompiles(initial);
        publisher.Publish(files.WriteManifest(initial), files.History);
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            namespace StateModels;
            [DurableType("state.base", 2)]
            public abstract partial class NewBase : IDurableObject {
                [DurableField(9)] private readonly byte _small;
                private static void UpgradeStateV1ToV2(in __DurableState.V1 prior, out __DurableState.V2 next)
                    => throw new System.Exception("Must not independently upgrade ancestor");
            }
            [DurableType("state.leaf", 2)]
            public sealed partial class Leaf : NewBase {
                [DurableField(1)] private readonly string? _name;
                private static void UpgradeStateV1ToV2(in __DurableState.V1 prior, out __DurableState.V2 next)
                    => next = new((byte)(prior.Segment0Field4 + 1), prior.Segment1Field1);
            }
            public static class Host {
                public static StateModelBinding Model() => Leaf.__DurableState.Model;
                public static object Old() => new Leaf.__DurableState.V1(7, new(99));
            }
            """, files.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(run);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        Assert.Null(assembly.GetType("StateModels.OldBase"));
        Type host = assembly.GetType("StateModels.Host")!;
        StateModelBinding model = host.GetMethod("Model")!.CreateDelegate<Func<StateModelBinding>>()();
        object old = host.GetMethod("Old")!.CreateDelegate<Func<object>>()();
        ObjectStateRecord current = model.Normalize(new(new(1), model.Readers[0].Schema, old));
        Assert.Equal((byte)8, StateModelField(current, "Segment0Field9"));
        Assert.Equal(new ObjectId(99), StateModelField(current, "Segment1Field1"));
        IDurableObject domain = model.Allocate();
        string name = new(new[] { 'N' });
        model.Hydrate(domain, current, new ObjectReadTable(StringReadTable.FromDecoded([(new ObjectId(99), name)]), new Dictionary<ObjectId, IDurableObject>()));
        Assert.Equal((byte)8, domain.GetType().BaseType!.GetField("_small", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(domain));
        Assert.Same(name, domain.GetType().GetField("_name", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(domain));
    }

    [Fact]
    public void GeneratedReadonlyHydrateSkipsConstructorsAndInitializersPreservesIdentityAndCapturesAgain() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            namespace StateModels;
            [DurableType("readonly.base", 1)]
            public abstract partial class Base : IDurableObject {
                [DurableField(1)] private readonly int _number = 123;
                [DurableField(2)] private readonly string _name;
                [Transient] public int Cache = 456;
                public static int Calls;
                protected Base(string name) { Calls++; _name = name; }
            }
            [DurableType("readonly.leaf", 1)]
            public sealed partial class Leaf : Base {
                [DurableField(1)] private readonly string? _shared;
                [DurableField(2)] private readonly string? _distinct;
                [DurableField(3)] private readonly string? _empty;
                [DurableField(4)] private readonly string? _emptyAlias;
                [DurableField(5)] private readonly string? _null;
                [DurableField(6)] private int _mutable = 789;
                public Leaf(string name) : base(name) { Calls++; _shared = name; }
            }
            public static class Host {
                public static StateModelBinding Model() => Leaf.__DurableState.Model;
                public static object State() => new Leaf.__DurableState.V1(17, new(7), new(7), new(8), new(9), new(10), default, 91);
            }
            """);
        AssertSchemaOnlyCompiles(run);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        Type host = assembly.GetType("StateModels.Host")!;
        StateModelBinding model = host.GetMethod("Model")!.CreateDelegate<Func<StateModelBinding>>()();
        ObjectStateRecord current = model.Normalize(new(new(1), model.CurrentSchema, host.GetMethod("State")!.CreateDelegate<Func<object>>()()));
        IDurableObject domain = model.Allocate();
        Type leaf = domain.GetType(), parent = leaf.BaseType!;
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        Assert.Equal(0, parent.GetField("_number", fields)!.GetValue(domain));
        Assert.Equal(0, parent.GetField("Cache")!.GetValue(domain));
        Assert.Equal(0, leaf.GetField("_mutable", fields)!.GetValue(domain));
        // Loading validates the complete DTO view before allocating or hydrating any domain instance.
        Assert.Throws<InvalidDataException>(() => model.VisitReferences(current,
            new StateReferenceValidator(new Dictionary<ObjectId, ObjectStateRecord>())));
        Assert.Equal(0, parent.GetField("_number", fields)!.GetValue(domain));
        string first = new(new[] { 'S' }), second = new(new[] { 'S' });
        model.Hydrate(domain, current, new ObjectReadTable(StringReadTable.FromDecoded([(new ObjectId(7), first), (new ObjectId(8), second), (new ObjectId(9), ""), (new ObjectId(10), "")]), new Dictionary<ObjectId, IDurableObject>()));
        Assert.Equal(0, parent.GetField("Calls")!.GetValue(null));
        Assert.Equal(17, parent.GetField("_number", fields)!.GetValue(domain));
        Assert.Equal(91, leaf.GetField("_mutable", fields)!.GetValue(domain));
        Assert.Same(first, parent.GetField("_name", fields)!.GetValue(domain));
        Assert.Same(first, leaf.GetField("_shared", fields)!.GetValue(domain));
        Assert.Same(second, leaf.GetField("_distinct", fields)!.GetValue(domain));
        Assert.NotSame(first, second);
        Assert.Same(string.Empty, leaf.GetField("_empty", fields)!.GetValue(domain));
        Assert.Same(string.Empty, leaf.GetField("_emptyAlias", fields)!.GetValue(domain));
        Assert.Null(leaf.GetField("_null", fields)!.GetValue(domain));
        CaptureSession session = new();
        using CaptureContext capture = session.BeginCapture();
        model.AddRoot(capture, domain);
        CapturedGraph graph = capture.Seal();
        PreparedCapturedGraph prepared = session.Prepare(graph);
        Assert.Equal(4, prepared.Objects.Count);
        Assert.Equal(17, StateModelField(graph.Objects[0], "Segment0Field1"));
        Assert.Same(current.Preparation, graph.Objects[0].Preparation);
        session.Accept(graph);
        leaf.GetField("_mutable", fields)!.SetValue(domain, 92);
        using CaptureContext nextCapture = session.BeginCapture();
        model.AddRoot(nextCapture, domain);
        CapturedGraph nextGraph = nextCapture.Seal();
        PreparedCapturedGraph changed = session.Prepare(nextGraph);
        Assert.True(changed.Objects[0].DeltaBody!.HasChanges);
        ObjectStateRecord replayed = model.Readers[0].Read(new(1), new StateModelBodySource(
            prepared.Objects[0].BaseBody.Body.ToArray(), changed.Objects[0].DeltaBody!.Body.ToArray()));
        Assert.Equal(92, StateModelField(replayed, "Segment1Field6"));
        Assert.Equal(StateModelField(nextGraph.Objects[0], "Segment0Field2"), StateModelField(replayed, "Segment0Field2"));
        session.Discard(nextGraph);
    }

    [Theory]
    [InlineData("private void UpgradeStateV1ToV2(in __DurableState.V1 old, out __DurableState.V2 next) => next = default;")]
    [InlineData("private static void UpgradeStateV1ToV2(__DurableState.V1 old, out __DurableState.V2 next) => next = default;")]
    [InlineData("private static int UpgradeStateV1ToV2(in __DurableState.V1 old, out __DurableState.V2 next) { next = default; return 0; }")]
    public void GeneratedStateModelRejectsMalformedDeclaredUpgrade(string method) {
        using AncestryHistoryDirectory files = new();
        GeneratorTestRun initial = RunGenerator(StateModelHistorySource(1));
        new SchemaHistoryTool().Publish(files.WriteManifest(initial), files.History);
        GeneratorTestRun run = RunGenerator(StateModelHistorySource(2) + "public partial class Item { " + method + " }", files.ReadAdditionalTexts());
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0020");
    }

    [Fact]
    public void GeneratedStateModelCompilerChecksExactDtoTypesAndOutAssignment() {
        using AncestryHistoryDirectory files = new();
        GeneratorTestRun initial = RunGenerator(StateModelHistorySource(1));
        new SchemaHistoryTool().Publish(files.WriteManifest(initial), files.History);
        foreach (string method in new[] {
            "private static void UpgradeStateV1ToV2(in __DurableState.V2 old, out __DurableState.V2 next) => next = old;",
            "private static void UpgradeStateV1ToV2(in __DurableState.V1 old, out __DurableState.V2 next) { }",
        }) {
            GeneratorTestRun run = RunGenerator(StateModelHistorySource(2) + "public partial class Item { " + method + " }", files.ReadAdditionalTexts());
            Assert.Contains(run.OutputCompilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedStateModelChecksEveryDeclaredUpgradeEvenWhenLaterEdgeIsMissing(bool wrongPriorType) {
        using AncestryHistoryDirectory files = new();
        SchemaHistoryTool publisher = new();
        GeneratorTestRun first = RunGenerator(StateModelHistorySource(1));
        AssertSchemaOnlyCompiles(first);
        publisher.Publish(files.WriteManifest(first), files.History);
        GeneratorTestRun second = RunGenerator(StateModelHistorySource(2), files.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(second);
        publisher.Publish(files.WriteManifest(second), files.History);
        string prior = wrongPriorType ? "V2" : "V1";
        GeneratorTestRun run = RunGenerator(StateModelHistorySource(3) + $$"""
            public partial class Item {
                private static void UpgradeStateV1ToV2(in __DurableState.{{prior}} prior, out __DurableState.V2 next)
                    => next = new(prior.Segment0Field1, 9);
            }
            public static class Host {
                public static StateModelBinding Model() => Item.__DurableState.Model;
                public static object Old() {
                    var reader = new Atelia.DurableGraph.Serialization.BinaryPayloadReader(new byte[] { 16 });
                    return Item.__DurableState.ReadBaseBodyV1(ref reader);
                }
            }
            """, files.ReadAdditionalTexts());
        if (wrongPriorType) {
            Assert.Contains(run.OutputCompilation.GetDiagnostics(), diagnostic => diagnostic.Id == "CS1503");
            return;
        }
        AssertSchemaOnlyCompiles(run);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("StateModels.Host")!;
        StateModelBinding model = host.GetMethod("Model")!.CreateDelegate<Func<StateModelBinding>>()();
        ObjectStateRecord old = new(new(1), model.Readers[0].Schema, host.GetMethod("Old")!.CreateDelegate<Func<object>>()());
        Assert.Equal(8, StateModelField(old, "Segment0Field1"));
        Assert.Contains("UpgradeStateV2ToV3", Assert.Throws<InvalidDataException>(() => model.Normalize(old)).Message);
    }

    private static string StateModelHistorySource(int version) => """
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        namespace StateModels;
        """ + $"\n[DurableType(\"state.model\", {version})]\n" +
        "public sealed partial class Item : IDurableObject { [DurableField(1)] private int _number; " +
        (version >= 2 ? "[DurableField(2)] private byte _small; " : "") +
        (version >= 3 ? "[DurableField(3)] private bool _flag; " : "") + "}\n";

    private static object? StateModelField(ObjectStateRecord row, string name) {
        object value = typeof(ObjectStateRecord).GetField("_content", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(row)!;
        return value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(value);
    }

    private sealed class StateModelBodySource(params byte[][] bodies) : IStateBodySource {
        public int Count => bodies.Length;
        public ReadOnlySpan<byte> GetBody(int index) => bodies[index];
    }
}
