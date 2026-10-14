using System.Text;
using Atelia.DurableGraph.Build;

namespace Atelia.DurableGraph.Tests;

public sealed class ListTemplateHistoryTests {
    [Fact]
    public void VersionFiveGoldenPreservesRecursiveListPatternsAndSubstitution() {
        using Fixture fixture = new();
        string text = History("// field:1|15|l(p0)\n// field:2|15|a2(l(a1(b2)))\n" +
            "// field:3|16|nUGFpcg==(l(p0))|1\n// field:4|15|l(l(b4))\n// field:5|2\n");
        SchemaHistoryRecord record = fixture.Parse(text);
        Assert.Equal(text, SchemaHistoryTextCodec.RenderHistory(record, 5));
        Assert.Equal("l(b2)", record.Fields[0].ValuePattern.Substitute([record.Fields[4].ValuePattern]).ToString());
        Assert.Equal("nUGFpcg==(l(b2))", record.Fields[2].ValuePattern.Substitute([record.Fields[4].ValuePattern]).ToString());
        Assert.Equal("a2(l(a1(b2)))", record.Fields[1].ValuePattern.ToString());
    }

    [Theory]
    [InlineData(3, "l(b2)")]
    [InlineData(3, "nQm94(l(b2))")]
    [InlineData(4, "l(b2)")]
    [InlineData(4, "nQm94(a1(l(b2)))")]
    public void EarlierVersionsRejectListConstructorsAtEveryDepth(int version, string pattern) {
        using Fixture fixture = new();
        Assert.Throws<SchemaHistoryException>(() => fixture.Parse(
            History("// field:1|15|" + pattern + "\n").Replace("history:5", "history:" + version)));
    }

    [Theory]
    [InlineData("1|15|l()")]
    [InlineData("1|15|l(b2,b4)")]
    [InlineData("1|15|l (b2)")]
    [InlineData("1|15|l(p1)")]
    [InlineData("1|15|l(b02)")]
    [InlineData("1|16|l(b2)|1")]
    [InlineData("1|17|l(p0)")]
    public void ListGrammarAndReferenceSlotClassificationAreStrict(string field) {
        using Fixture fixture = new();
        Assert.Throws<SchemaHistoryException>(() => fixture.Parse(History("// field:" + field + "\n")));
    }

    [Fact]
    public void ListsSharePatternDepthLimitWithArrayAndNamedConstructors() {
        using Fixture fixture = new();
        string pattern = "b2";
        for (int index = 0; index < 63; index++) pattern = index % 2 == 0 ? "l(" + pattern + ")" : "a2(" + pattern + ")";
        Assert.Equal(pattern, fixture.Parse(History("// field:1|15|" + pattern + "\n")).Fields[0].ValuePattern.ToString());
        Assert.Throws<SchemaHistoryException>(() => fixture.Parse(History("// field:1|15|l(" + pattern + ")\n")));
    }

    [Fact]
    public void PublishingVersionSixPreservesEarlierFilesAndTheirHashes() {
        using Fixture fixture = new();
        string[] old = [
            "// durable-graph-schema-history:1\n// schema-begin\n// schema-id-base64:T25l\n// version:1\n// field:1|2\n// schema-end\n",
            "// durable-graph-schema-history:2\n// schema-begin\n// schema-id-base64:VHdv\n// version:1\n// kind:2\n// field:1|2\n// schema-end\n",
            GenericTemplateHistoryTests.History("Three", arity: 1, body: "// field:1|17|p0\n"),
            GenericTemplateHistoryTests.History("Four", body: "// field:1|15|a2(b2)\n").Replace("history:3", "history:4")
        ];
        foreach (string text in old) fixture.Accept(text);
        string[] prior = fixture.Accepted();
        fixture.Publish(History("// field:1|15|l(nVHdv())\n"));
        foreach (string record in prior) Assert.Contains(record, fixture.Accepted());
        Assert.Equal(prior.Length + 1, fixture.Accepted().Length);
        string[] published = fixture.Accepted();
        fixture.Publish(History("// field:1|15|l(nVHdv())\n"));
        Assert.Equal(published, fixture.Accepted());
    }

    internal static string History(string body) => GenericTemplateHistoryTests.History("Lists", arity: 1, body: body)
        .Replace("history:3", "history:5");

    private sealed class Fixture : IDisposable {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "durable-list-history-" + Guid.NewGuid().ToString("N"));
        private string AcceptedPath => Path.Combine(_root, "accepted");
        public Fixture() => Directory.CreateDirectory(_root);
        public SchemaHistoryRecord Parse(string text) {
            string path = Path.Combine(_root, "input.dgschema");
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return SchemaHistoryTextCodec.ParseHistory(path);
        }
        public void Accept(string text) {
            SchemaHistoryRecord record = Parse(text);
            Directory.CreateDirectory(AcceptedPath);
            File.WriteAllText(Path.Combine(AcceptedPath, SchemaHistoryTextCodec.GetHistoryFileName(record, text)), text, new UTF8Encoding(false));
        }
        public void Publish(string text) {
            string manifest = Path.Combine(_root, "manifest.g.cs");
            File.WriteAllText(manifest, text.Replace("schema-history:5", "schema-history-manifest:5"), new UTF8Encoding(false));
            new SchemaHistoryTool().Publish(manifest, AcceptedPath);
            new SchemaHistoryTool().Verify(manifest, AcceptedPath);
        }
        public string[] Accepted() => Directory.GetFiles(AcceptedPath).Order(StringComparer.Ordinal)
            .Select(path => Path.GetFileName(path) + File.ReadAllText(path)).ToArray();
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}

public sealed partial class DurableSchemaGeneratorTests {
    [Theory]
    [InlineData(3, "l(b2)")]
    [InlineData(3, "nQm94(l(p0))")]
    [InlineData(4, "l(b2)")]
    [InlineData(4, "a2(l(p0))")]
    public void GeneratorEarlierHistoryVersionsRejectListGrammar(int version, string pattern) {
        GeneratorTestRun run = RunGenerator("class Plain {}", new InMemoryAdditionalText("lists.dgschema",
            ListTemplateHistoryTests.History("// field:1|15|" + pattern + "\n").Replace("history:5", "history:" + version)));
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0012");
    }

    [Theory]
    [InlineData("1|15|l()")]
    [InlineData("1|15|l(b2,b4)")]
    [InlineData("1|16|l(b2)|1")]
    [InlineData("1|17|l(p0)")]
    public void GeneratorVersionFiveSharesMalformedListRejections(string field) {
        GeneratorTestRun run = RunGenerator("class Plain {}", new InMemoryAdditionalText("lists.dgschema",
            ListTemplateHistoryTests.History("// field:" + field + "\n")));
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0012");
    }
}
