using System.Text;
using Atelia.DurableGraph.Build;

namespace Atelia.DurableGraph.Tests;

public sealed class DictionaryHistoryTests {
    [Fact]
    public void VersionSevenGoldenKeepsBothOperandsAndSubstitutesRecursively() {
        using Fixture fixture = new();
        string text = History("Maps", "// field:1|15|d(p0,l(q(p1)))\n" +
            "// field:2|15|a2(d(b4,d(b2,nUG9pbnQ=())))\n" +
            "// field:3|16|nUGFpcg==(d(p1,p0))|1\n// field:4|2\n// field:5|4\n", arity: 2);
        SchemaHistoryRecord record = fixture.Parse(text);
        Assert.Equal(text, SchemaHistoryTextCodec.RenderHistory(record, 7));
        Assert.Equal("d(b4,l(q(b2)))", record.Fields[0].ValuePattern.Substitute(
            [record.Fields[4].ValuePattern, record.Fields[3].ValuePattern]).ToString());
        Assert.Equal("nUGFpcg==(d(b2,b4))", record.Fields[2].ValuePattern.Substitute(
            [record.Fields[4].ValuePattern, record.Fields[3].ValuePattern]).ToString());
        Assert.Equal("a2(d(b4,d(b2,nUG9pbnQ=())))", record.Fields[1].ValuePattern.ToString());
        Assert.Throws<SchemaHistoryException>(() => SchemaHistoryTextCodec.RenderHistory(record, 6));
    }

    [Theory]
    [InlineData("d(b2,b4)")]
    [InlineData("l(d(b2,b4))")]
    [InlineData("nQm94(a2(d(b2,b4)))")]
    public void EarlierVersionsRejectDictionaryAtEveryDepth(string pattern) {
        using Fixture fixture = new();
        for (int version = 3; version <= 6; version++) {
            Assert.Throws<SchemaHistoryException>(() => fixture.Parse(
                History("Old", "// field:1|15|" + pattern + "\n").Replace("history:7", "history:" + version)));
        }
    }

    [Theory]
    [InlineData("1|15|d()")]
    [InlineData("1|15|d(b2)")]
    [InlineData("1|15|d(b2,b4,b2)")]
    [InlineData("1|15|d(b2,)")]
    [InlineData("1|15|d(,b2)")]
    [InlineData("1|15|d (b2,b4)")]
    [InlineData("1|15|d(b02,b4)")]
    [InlineData("1|15|d(b2,p2)")]
    [InlineData("1|16|d(b2,b4)|1")]
    [InlineData("1|17|d(p0,p1)")]
    [InlineData("1|18|q(d(b2,b4))")]
    public void DictionaryGrammarAndReferenceClassificationAreStrict(string field) {
        using Fixture fixture = new();
        Assert.Throws<SchemaHistoryException>(() => fixture.Parse(History("Bad", "// field:" + field + "\n", arity: 2)));
    }

    [Fact]
    public void DictionarySharesPatternDepthAndExpandedNodeLimits() {
        using Fixture fixture = new();
        string pattern = "b2";
        for (int index = 0; index < 63; index++) pattern = "d(b2," + pattern + ")";
        Assert.Equal(pattern, fixture.Parse(History("Depth", "// field:1|15|" + pattern + "\n")).Fields[0].ValuePattern.ToString());
        Assert.Throws<SchemaHistoryException>(() => fixture.Parse(History("Depth", "// field:1|15|d(b2," + pattern + ")\n")));
        pattern = "b2";
        for (int index = 0; index < 11; index++) pattern = "d(" + pattern + "," + pattern + ")";
        Assert.Equal(pattern, fixture.Parse(History("Nodes", "// field:1|15|" + pattern + "\n")).Fields[0].ValuePattern.ToString());
        Assert.Throws<SchemaHistoryException>(() => fixture.Parse(History("Nodes", "// field:1|15|d(b2," + pattern + ")\n")));
    }

    [Fact]
    public void PublishingVersionSevenKeepsEveryOlderAcceptedFileAndHash() {
        using Fixture fixture = new();
        string[] previous = [
            "// durable-graph-schema-history:1\n// schema-begin\n// schema-id-base64:T25l\n// version:1\n// field:1|2\n// schema-end\n",
            "// durable-graph-schema-history:2\n// schema-begin\n// schema-id-base64:VHdv\n// version:1\n// kind:2\n// field:1|2\n// schema-end\n",
            GenericTemplateHistoryTests.History("Three", arity: 1, body: "// field:1|17|p0\n"),
            History("Four", "// field:1|15|a2(b2)\n").Replace("history:7", "history:4"),
            History("Five", "// field:1|15|l(b2)\n").Replace("history:7", "history:5"),
            History("Six", "// field:1|18|q(b2)\n").Replace("history:7", "history:6")
        ];
        foreach (string text in previous) fixture.Accept(text);
        string[] before = fixture.Accepted();
        fixture.Publish(History("New", "// field:1|15|d(b4,l(q(b2)))\n"));
        foreach (string accepted in before) Assert.Contains(accepted, fixture.Accepted());
        Assert.Equal(7, fixture.Accepted().Length);
        string[] published = fixture.Accepted();
        fixture.Publish(History("New", "// field:1|15|d(b4,l(q(b2)))\n"));
        Assert.Equal(published, fixture.Accepted());
    }

    [Fact]
    public void BothDictionaryOperandsParticipateInNominalArityValidation() {
        using Fixture fixture = new();
        string enumShape = History("Key", "// field:1|2\n", kind: 2);
        string point = History("Point", "// field:1|17|p0\n", kind: 2, arity: 1);
        Assert.Throws<SchemaHistoryException>(() => fixture.Publish(enumShape, point,
            History("WrongKey", "// field:1|15|d(nS2V5(b2),nUG9pbnQ=(b2))\n")));
        Assert.Empty(fixture.Accepted());
        Assert.Throws<SchemaHistoryException>(() => fixture.Publish(enumShape, point,
            History("WrongValue", "// field:1|15|d(nS2V5(),nUG9pbnQ=())\n")));
        Assert.Empty(fixture.Accepted());
        fixture.Publish(enumShape, point, History("Good", "// field:1|15|d(nS2V5(),nUG9pbnQ=(b2))\n"));
        Assert.Equal(3, fixture.Accepted().Length);
    }

    internal static string History(string id, string fields, int arity = 0, int kind = 1) =>
        GenericTemplateHistoryTests.History(id, arity: arity, kind: kind, body: fields).Replace("history:3", "history:7");

    private sealed class Fixture : IDisposable {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "durable-dictionary-history-" + Guid.NewGuid().ToString("N"));
        private string HistoryPath => Path.Combine(_root, "accepted");
        internal Fixture() => Directory.CreateDirectory(_root);
        internal SchemaHistoryRecord Parse(string text) {
            string path = Path.Combine(_root, "input.dgschema");
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return SchemaHistoryTextCodec.ParseHistory(path);
        }
        internal void Accept(string text) {
            SchemaHistoryRecord record = Parse(text);
            Directory.CreateDirectory(HistoryPath);
            File.WriteAllText(Path.Combine(HistoryPath, SchemaHistoryTextCodec.GetHistoryFileName(record, text)), text, new UTF8Encoding(false));
        }
        internal void Publish(params string[] histories) {
            string path = Path.Combine(_root, "manifest.g.cs");
            File.WriteAllText(path, "// durable-graph-schema-history-manifest:7\n" +
                string.Concat(histories.Select(text => text.Replace("// durable-graph-schema-history:7\n", ""))), new UTF8Encoding(false));
            new SchemaHistoryTool().Publish(path, HistoryPath);
            new SchemaHistoryTool().Verify(path, HistoryPath);
        }
        internal string[] Accepted() => Directory.Exists(HistoryPath)
            ? Directory.GetFiles(HistoryPath).Order(StringComparer.Ordinal).Select(path => Path.GetFileName(path) + File.ReadAllText(path)).ToArray()
            : [];
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}

public sealed partial class DurableSchemaGeneratorTests {
    [Theory]
    [InlineData("d(b2,b4)")]
    [InlineData("l(d(b2,p0))")]
    [InlineData("nQm94(a2(d(b2,b4)))")]
    public void GeneratorEarlierHistoryVersionsRejectDictionaryAtEveryDepth(string pattern) {
        for (int version = 3; version <= 6; version++) {
            GeneratorTestRun run = RunGenerator("class Plain {}", new InMemoryAdditionalText("dictionary.dgschema",
                DictionaryHistoryTests.History("Old", "// field:1|15|" + pattern + "\n", arity: 1).Replace("history:7", "history:" + version)));
            Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0012");
        }
    }

    [Theory]
    [InlineData("1|15|d(b2)")]
    [InlineData("1|15|d(b2,b4,b2)")]
    [InlineData("1|16|d(b2,b4)|1")]
    [InlineData("1|17|d(p0,p1)")]
    [InlineData("1|18|q(d(b2,b4))")]
    public void GeneratorVersionSevenSharesMalformedDictionaryRejections(string field) {
        GeneratorTestRun run = RunGenerator("class Plain {}", new InMemoryAdditionalText("dictionary.dgschema",
            DictionaryHistoryTests.History("Bad", "// field:" + field + "\n", arity: 2)));
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0012");
    }
}
