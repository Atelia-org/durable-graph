using Atelia.DurableGraph.Schema;
using System.Text;
using Atelia.DurableGraph.Build;

namespace Atelia.DurableGraph.Tests;

public sealed class ArrayTemplateHistoryTests {
    [Fact]
    public void VersionFourGoldenPreservesArrayCompositionAndOpenOperands() {
        using Fixture fixture = new();
        string text = History("// field:1|15|a1(p0)\n// field:2|15|a2(a3(a4(b2)))\n" +
            "// field:3|15|nQm94(a1(b4))\n// field:4|16|nUGFpcg==(a1(p0))|1\n// field:5|2\n");
        SchemaHistoryRecord record = fixture.Parse(text);
        Assert.Equal(text, SchemaHistoryTextCodec.RenderHistory(record, 4));
        Assert.Equal("a1(p0)", record.Fields[0].ValuePattern.ToString());
        Assert.Equal("a1(b2)", record.Fields[0].ValuePattern.Substitute([record.Fields[4].ValuePattern]).ToString());
        Assert.Equal("a2(a3(a4(b2)))", record.Fields[1].ValuePattern.ToString());
        Assert.Equal("nQm94(a1(b4))", record.Fields[2].ValuePattern.ToString());
    }

    [Theory]
    [InlineData("a1(b2)")]
    [InlineData("nQm94(a1(b2))")]
    public void VersionThreeRejectsArrayConstructorsAtAnyDepth(string pattern) {
        using Fixture fixture = new();
        Assert.Throws<SchemaHistoryException>(() => fixture.Parse(
            History("// field:1|15|" + pattern + "\n").Replace("history:4", "history:3")));
    }

    [Theory]
    [InlineData("1|15|a0(b2)")]
    [InlineData("1|15|a5(b2)")]
    [InlineData("1|15|a01(b2)")]
    [InlineData("1|15|a1()")]
    [InlineData("1|15|a1(b2,b4)")]
    [InlineData("1|15|a1(p1)")]
    [InlineData("1|15|b4")]
    [InlineData("1|16|a1(b2)|1")]
    [InlineData("1|17|a1(p0)")]
    public void VersionFourRejectsNoncanonicalAndMisclassifiedArraySlots(string field) {
        using Fixture fixture = new();
        Assert.Throws<SchemaHistoryException>(() => fixture.Parse(History("// field:" + field + "\n")));
    }

    [Fact]
    public void VersionFourPublicationDoesNotTreatArrayElementAsAReferenceObjectFamily() {
        using Fixture fixture = new();
        string point = GenericTemplateHistoryTests.History("Point", kind: 2, body: "// field:1|2\n").Replace("history:3", "history:4");
        string owner = History("// field:1|15|a1(nUG9pbnQ=())\n");
        fixture.Publish(point, owner);
        fixture.Publish(point, owner);
    }

    [Fact]
    public void ArrayConstructorsShareThePatternDepthLimit() {
        using Fixture fixture = new();
        string pattern = "b2";
        for (int index = 0; index < 63; index++) pattern = "a1(" + pattern + ")";
        Assert.Equal(pattern, fixture.Parse(History("// field:1|15|" + pattern + "\n")).Fields[0].ValuePattern.ToString());
        Assert.Throws<SchemaHistoryException>(() => fixture.Parse(History("// field:1|15|a1(" + pattern + ")\n")));
    }

    internal static string History(string body) => GenericTemplateHistoryTests.History("Arrays", arity: 1, body: body)
        .Replace("history:3", "history:4");

    private sealed class Fixture : IDisposable {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "durable-array-history-" + Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(_root);
        public SchemaHistoryRecord Parse(string text) {
            string path = Path.Combine(_root, "input.dgschema");
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return SchemaHistoryTextCodec.ParseHistory(path);
        }
        public void Publish(params string[] records) {
            string path = Path.Combine(_root, "manifest.g.cs");
            File.WriteAllText(path, "// durable-graph-schema-history-manifest:4\n" +
                string.Concat(records.Select(text => text.Replace("// durable-graph-schema-history:4\n", ""))), new UTF8Encoding(false));
            new SchemaHistoryTool().Publish(path, Path.Combine(_root, "accepted"));
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}

public sealed partial class DurableSchemaGeneratorTests {
    [Theory]
    [InlineData("a1(b2)")]
    [InlineData("nQm94(a2(p0))")]
    public void GeneratorVersionThreeRejectsNestedArrayGrammar(string pattern) {
        GeneratorTestRun run = RunGenerator("class Plain {}", new InMemoryAdditionalText("arrays.dgschema",
            ArrayTemplateHistoryTests.History("// field:1|15|" + pattern + "\n").Replace("history:4", "history:3")));
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0012");
    }

    [Fact]
    public void GeneratedArrayFieldsShareReferenceBodyAndPreserveOpenHistoryPatterns() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("Pair",1)] public partial struct Pair<T,U> {
                [DurableField(1)] public T First;
                [DurableField(2)] public U Second;
                [DurableField(3)] public T[] Nested;
            }
            [DurableType("Box",1)] public partial class Box<T>:IDurableObject {
                [DurableField(1)] public T Value;
                [DurableField(2)] public T[] Items;
            }
            [DurableType("World",1)] public partial class World:IDurableObject {
                [DurableField(1)] public Pair<int,string>[] Pairs;
                [DurableField(2)] public int[][] Jagged;
                [DurableField(3)] public Pair<int,string>[][,] Mixed;
                [DurableField(4)] public Box<int[]> Box;
                [DurableField(5)] public int[,,,] RankFour;
            }
            """);
        AssertSchemaOnlyCompiles(run);
        string generated = GeneratedSource(run, "DurableGenericStates.g.cs");
        Assert.Contains("context.CaptureObject(field", generated);
        Assert.Contains("objects.ResolveObject<", generated);
        Assert.Contains("visitor.VisitObject(state.", generated);
        Assert.Contains("TypeExpr.MultiDimArray(", generated);
        Assert.Contains("TypeExpr.VectorArray(", generated);
        Assert.DoesNotContain("Array.GetValue(", generated);
        string history = GeneratedSource(run, "DurableGraphSchemaHistoryCandidates.g.cs");
        Assert.Contains("manifest:9", history);
        Assert.Contains("// field:2|15|a1(p0)", history);
        Assert.Contains("// field:2|15|a1(a1(b2))", history);
        Assert.Contains("// field:4|15|nQm94(a1(b2))", history);
        Assert.Contains("// field:5|15|a4(b2)", history);
    }

    [Theory]
    [InlineData("int[,,,,]")]
    [InlineData("object[]")]
    public void UnsupportedArrayElementsAndRanksRemainPreciseGeneratorErrors(string type) {
        GeneratorTestRun run = RunGenerator("using Atelia.DurableGraph; [DurableType(\"Bad\",1)] public partial class Bad:IDurableObject { [DurableField(1)] public " + type + " Value; }");
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0007");
    }
}
