using Atelia.Binary;
using Atelia.DurableGraph.Schema;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void StandaloneAttributedStructEmitsSchemaAndStaticValueBridgeWithoutObjectRegistration() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("position", 1)]
            public readonly partial struct Position {
                [DurableField(7)] private readonly int _x;
            }
            """);
        Assert.DoesNotContain(run.GeneratorDiagnostics, IsError);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        var schema = (DurableSchema)assembly.GetType("Position")!.GetProperty("Schema")!.GetValue(null)!;
        Assert.Equal(SchemaKind.InlineValue, schema.Kind);
        string bodies = GeneratedSource(run, "DurableStates.g.cs");
        Assert.Contains("Capture(in global::Position value)", bodies);
        Assert.Contains("Hydrate(ref global::Position target", bodies);
        Assert.DoesNotContain("RegisterModel", bodies);
        Assert.DoesNotContain("RegisterReaders", bodies);
        Assert.DoesNotContain("AddRoot", bodies);
        Assert.Contains("// kind:2", GeneratedSource(run, "DurableGraphSchemaHistoryCandidates.g.cs"));
    }

    [Fact]
    public void MutableStructFieldCaptureFreezesValueBeforeInPlaceDomainMutation() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using Atelia.DurableGraph.Serialization;
            [DurableType("mutable.value", 1)]
            public partial struct Value {
                [DurableField(1)] public int X;
            }
            [DurableType("mutable.world", 1)]
            public partial class World : IDurableObject {
                [DurableField(1)] public Value Value;
            }
            public static class Host {
                public static bool Probe() {
                    var world = new World();
                    world.Value.X = 10;
                    var prior = World.__DurableState.Capture(world);
                    world.Value.X = 27;
                    var current = World.__DurableState.Capture(world);
                    var delta = World.__DurableState.PrepareDeltaBody(in prior, in current);
                    var reader = new BareValueReader(delta.Body);
                    var restored = World.__DurableState.ApplyDeltaBodyV1(ref reader, in prior);
                    reader.EnsureFullyConsumed();
                    var unchanged = World.__DurableState.PrepareDeltaBody(in current, in restored);
                    return prior.Segment0Field1.Segment0Field1 == 10 &&
                        current.Segment0Field1.Segment0Field1 == 27 &&
                        restored.Segment0Field1.Segment0Field1 == 27 &&
                        world.Value.X == 27 && delta.HasChanges && !unchanged.HasChanges;
                }
            }
            """);
        Assert.DoesNotContain(run.GeneratorDiagnostics, IsError);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        Assert.True(assembly.GetType("Host")!.GetMethod("Probe")!.CreateDelegate<Func<bool>>()());
    }

    [Theory]
    [InlineData("ref partial struct Value")]
    public void UnsupportedStructShapesFailClosed(string declaration) {
        GeneratorTestRun run = RunGenerator("using Atelia.DurableGraph; [DurableType(\"value\",1)] public " + declaration + " {}");
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0001");
    }

    [Fact]
    public void UnmarkedStructFieldIsNotAutomaticallyEnrolled() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            public struct Value { public int X; }
            [DurableType("world",1)] public partial class World : IDurableObject {
                [DurableField(1)] public Value Value;
            }
            """);
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0007");
    }

    [Fact]
    public void InlineVersionChangeRequiresOwnerVersionChange() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("value",2)] public partial struct Value { [DurableField(1)] public long X; }
            [DurableType("world",1)] public partial class World : IDurableObject { [DurableField(1)] public Value Value; }
            """, InlineHistory("value",1,2,"1|2"), InlineHistory("world",1,1,"1|16|dmFsdWU=|1"));
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0015");
    }

    [Fact]
    public void AcceptedInlineDependencyCannotBeRepairedFromCurrentCandidate() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("value",1)] public partial struct Value { [DurableField(1)] public int X; }
            [DurableType("world",2)] public partial class World : IDurableObject { }
            """, InlineHistory("world",1,1,"1|16|dmFsdWU=|1"));
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0019");
    }

    [Fact]
    public void HistoricalStructDeclarationCanDisappearWhileOwnerUpgradeChainStillCompiles() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using States=Atelia.DurableGraph.Generated.Family_776F726C64;
            [DurableType("world",3)] public partial class World : IDurableObject {
                [DurableField(1)] public int X;
                static void UpgradeStateV1ToV2(in States.V1 prior, out States.V2 next) => next = new(new(prior.Segment0Field1.Segment0Field1 + 1));
                static void UpgradeStateV2ToV3(in States.V2 prior, out States.V3 next) => next = new(prior.Segment0Field1.Segment0Field1 + 1);
                public static int Probe() {
                    var first = new States.V1(new(10));
                    UpgradeStateV1ToV2(in first, out var second);
                    UpgradeStateV2ToV3(in second, out var third);
                    return third.Segment0Field1;
                }
            }
            """, InlineHistory("value",1,2,"1|2"), InlineHistory("value",2,2,"1|2"),
                InlineHistory("world",1,1,"1|16|dmFsdWU=|1"), InlineHistory("world",2,1,"1|16|dmFsdWU=|2"));
        Assert.DoesNotContain(run.GeneratorDiagnostics, IsError);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        Assert.Equal(12, assembly.GetType("World")!.GetMethod("Probe")!.Invoke(null,null));
        Assert.Null(assembly.GetType("Value"));
    }

    [Fact]
    public void SchemaFamilyCannotChangeFromClassToInlineValue() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("value",2)] public partial struct Value { }
            """, SchemaHistory("old.dgschema","value",1));
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0015");
    }

    [Theory]
    [InlineData("1|16|dmFsdWU=|1", 1)]
    [InlineData("1|16|dmFsdWU=|1", 2)]
    public void WrongKindAndCyclicInlineHistoryFailClosed(string field, int valueKind) {
        GeneratorTestRun run = RunGenerator("class Plain {}", InlineHistory("value",1,valueKind,field));
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0019");
    }

    [Theory]
    [InlineData(256, false)]
    [InlineData(257, true)]
    public void ExactHistoryDepthIncludesTheLongestPathEvenWhenDependenciesAreShared(int count, bool fails) {
        List<AdditionalText> history = new();
        for (int index = count - 1; index >= 0; index--) {
            string id = "node" + index.ToString("D3");
            string child = Convert.ToBase64String(Encoding.UTF8.GetBytes("node" + (index + 1).ToString("D3")));
            history.Add(index == count - 1 ? InlineHistory(id,1,2) : InlineHistory(id,1,2,"1|16|" + child + "|1", "2|16|" + child + "|1"));
        }
        GeneratorTestRun run = RunGenerator("class Plain {}", history.ToArray());
        Assert.Equal(fails, run.GeneratorDiagnostics.Any(diagnostic => diagnostic.Id == "DG0019"));
        Assert.DoesNotContain(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "CS8785");
    }

    private static AdditionalText InlineHistory(string id,int version,int kind, params string[] fields) =>
        new InMemoryAdditionalText(id + version + ".dgschema", "// durable-graph-schema-history:2\n// schema-begin\n// schema-id-base64:" +
            Convert.ToBase64String(Encoding.UTF8.GetBytes(id)) + "\n// version:" + version + "\n// kind:" + kind + "\n" +
            string.Concat(fields.Select(field => "// field:" + field + "\n")) + "// schema-end\n");
}
