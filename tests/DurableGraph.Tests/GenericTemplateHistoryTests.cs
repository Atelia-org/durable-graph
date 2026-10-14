using System.Text;
using Atelia.DurableGraph.Build;

namespace Atelia.DurableGraph.Tests;

public sealed class GenericTemplateHistoryTests {
    [Fact]
    public void VersionThreeGoldenKeepsDefinitionArityAndScopedPatterns() {
        using Fixture fixture = new();
        string text = History("Box", arity: 2,
            body: "// base:nQmFzZQ==(p1)|3\n// field:1|17|p0\n// field:2|15|nQm94(p1,p0)\n// field:3|16|nUGFpcg==(p0)|2\n");
        SchemaHistoryRecord parsed = fixture.Parse(text);
        Assert.Equal(text, SchemaHistoryTextCodec.RenderHistory(parsed, 3));
        Assert.Equal(2, parsed.Arity);
        Assert.Equal(new SchemaHistoryKey("Base", 3), parsed.BaseSchema);
        Assert.Equal("nQmFzZQ==(p1)", parsed.BaseType!.ToString());
        Assert.Equal("p0", parsed.Fields[0].ValuePattern.ToString());
        Assert.Equal("nQm94(p1,p0)", parsed.Fields[1].ValuePattern.ToString());
        Assert.Equal(new SchemaHistoryKey("Pair", 2), parsed.Fields[2].InlineSchema);
    }

    [Theory]
    [InlineData("1|17|p01")]
    [InlineData("1|17|p1")]
    [InlineData("1|17|p32")]
    [InlineData("1|17|b2")]
    [InlineData("1|15|p0")]
    [InlineData("1|15|nQg==(b15)")]
    [InlineData("1|15|nQg==(p0,)")]
    [InlineData("1|15|nQg== (p0)")]
    [InlineData("1|15|nQh==(p0)")]
    [InlineData("1|16|nQg==(p0)|01")]
    public void VersionThreeRejectsNoncanonicalOrUnboundPatterns(string field) {
        using Fixture fixture = new();
        Assert.Throws<SchemaHistoryException>(() => fixture.Parse(History("Box", arity: 1, body: $"// field:{field}\n")));
    }

    [Theory]
    [InlineData("00")]
    [InlineData("+1")]
    [InlineData("33")]
    [InlineData("-1")]
    public void VersionThreeRejectsInvalidDeclarationArity(string arity) {
        using Fixture fixture = new();
        Assert.Throws<SchemaHistoryException>(() => fixture.Parse(History("Box").Replace("// arity:0", "// arity:" + arity)));
    }

    [Fact]
    public void TemplatePublishIsImmutableAndKindArityApplyAcrossVersions() {
        using Fixture fixture = new();
        string box = History("Box", arity: 1, body: "// field:1|17|p0\n");
        fixture.Publish(box);
        string[] before = fixture.Accepted();
        fixture.Publish(box);
        Assert.Equal(before, fixture.Accepted());
        Assert.Throws<SchemaHistoryException>(() => fixture.Publish(box.Replace("1|17|p0", "1|2")));
        Assert.Throws<SchemaHistoryException>(() => fixture.Publish(History("Box", version: 2, arity: 2)));
        Assert.Throws<SchemaHistoryException>(() => fixture.Publish(History("Box", version: 2, kind: 2, arity: 1)));
        Assert.Equal(before, fixture.Accepted());
    }

    [Fact]
    public void ExactDependenciesKeepFixedVersionsAndAcceptedHistoryCannotBeRepaired() {
        using Fixture fixture = new();
        string pair = History("Pair", kind: 2, arity: 1, body: "// field:1|17|p0\n");
        string box = History("Box", arity: 1, body: "// field:1|16|nUGFpcg==(p0)|1\n");
        fixture.Accept(box); // A broken accepted directory must not be patched from this build's candidates.
        string[] before = fixture.Accepted();
        Assert.Throws<SchemaHistoryException>(() => fixture.Publish(pair, box));
        Assert.Equal(before, fixture.Accepted());
    }

    [Fact]
    public void NominalPatternsMayBeAbsentButMustAgreeOnArityAndReferenceKind() {
        using Fixture fixture = new();
        fixture.Publish(History("Owner", body: "// field:1|15|nR2hvc3Q=(b2)\n"));
        string[] before = fixture.Accepted();
        Assert.Throws<SchemaHistoryException>(() => fixture.Publish(History("Other", body: "// field:1|15|nR2hvc3Q=(b2,b2)\n")));
        Assert.Throws<SchemaHistoryException>(() => fixture.Publish(History("Ghost", kind: 2, arity: 1)));
        Assert.Equal(before, fixture.Accepted());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DeclaredNominalArityIsCheckedInsideTypeArgumentsWithoutWritingHistory(bool nested, bool targetFirst) {
        using Fixture fixture = new();
        string target = History("Box", arity: 1);
        string pattern = nested ? "nT3V0ZXI=(nQm94())" : "nQm94()";
        string owner = History("Owner", body: $"// field:1|15|{pattern}\n");
        string[] records = targetFirst ? [target, owner] : [owner, target];

        SchemaHistoryException publishError = Assert.Throws<SchemaHistoryException>(() => fixture.Publish(records));
        Assert.Contains("arity", publishError.Message);
        Assert.Empty(fixture.Accepted());

        foreach (string record in records) fixture.Accept(record);
        string[] before = fixture.Accepted();
        SchemaHistoryException verifyError = Assert.Throws<SchemaHistoryException>(() => fixture.Verify(records));
        Assert.Contains("arity", verifyError.Message);
        Assert.Equal(before, fixture.Accepted());
    }

    [Fact]
    public void PatternDepthIsBoundedWithoutATypeExpressionParserStackOverflow() {
        using Fixture fixture = new();
        string expression = "b2";
        for (int index = 0; index < 64; index++) expression = "nQg==(" + expression + ")";
        Assert.Throws<SchemaHistoryException>(() => fixture.Parse(History("Owner", body: "// field:1|15|" + expression + "\n")));
    }

    internal static string History(string id, int version = 1, int kind = 1, int arity = 0, string body = "") =>
        "// durable-graph-schema-history:3\n// schema-begin\n// schema-id-base64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(id)) +
        $"\n// version:{version}\n// kind:{kind}\n// arity:{arity}\n" + body + "// schema-end\n";

    private sealed class Fixture : IDisposable {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "durable-generic-history-" + Guid.NewGuid().ToString("N"));
        private string DirectoryPath => Path.Combine(_root, "accepted");
        public Fixture() => Directory.CreateDirectory(_root);
        public SchemaHistoryRecord Parse(string text) {
            string file = Path.Combine(_root, "input.dgschema");
            File.WriteAllText(file, text, SchemaHistoryTextCodec.Utf8NoBom);
            return SchemaHistoryTextCodec.ParseHistory(file);
        }
        public void Publish(params string[] histories) {
            new SchemaHistoryTool().Publish(WriteManifest(histories), DirectoryPath);
        }
        public void Verify(params string[] histories) => new SchemaHistoryTool().Verify(WriteManifest(histories), DirectoryPath);
        private string WriteManifest(string[] histories) {
            string manifest = Path.Combine(_root, "manifest.g.cs");
            File.WriteAllText(manifest, "// durable-graph-schema-history-manifest:3\n" +
                string.Concat(histories.Select(text => text.Replace("// durable-graph-schema-history:3\n", ""))), SchemaHistoryTextCodec.Utf8NoBom);
            return manifest;
        }
        public void Accept(string text) {
            SchemaHistoryRecord record = Parse(text);
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(Path.Combine(DirectoryPath, SchemaHistoryTextCodec.GetHistoryFileName(record, text)), text, SchemaHistoryTextCodec.Utf8NoBom);
        }
        public string[] Accepted() => Directory.Exists(DirectoryPath)
            ? Directory.GetFiles(DirectoryPath).Order(StringComparer.Ordinal).Select(path => Path.GetFileName(path) + File.ReadAllText(path)).ToArray() : [];
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}

public sealed partial class DurableSchemaGeneratorTests {
    [Theory]
    [InlineData("1|17|p01")]
    [InlineData("1|17|p1")]
    [InlineData("1|15|nQg==(b16)")]
    [InlineData("1|16|nQg==(p0)|0")]
    public void GeneratorHistoryParserSharesVersionThreePatternRejections(string field) {
        GeneratorTestRun run = RunGenerator("namespace Plain; class C { }", new InMemoryAdditionalText("box.dgschema",
            GenericTemplateHistoryTests.History("Box", arity: 1, body: "// field:" + field + "\n")));
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0012");
    }

    [Fact]
    public void GeneratorHistoryDetectsConflictingAbsentNominalArity() {
        GeneratorTestRun run = RunGenerator("namespace Plain; class C { }", new InMemoryAdditionalText("owner.dgschema",
            GenericTemplateHistoryTests.History("Owner", body: "// field:1|15|nR2hvc3Q=(b2)\n// field:2|15|nR2hvc3Q=(b2,b2)\n")));
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0019");
    }

    [Fact]
    public void GeneratorHistoryRejectsReferenceToAnInlineDefinition() {
        GeneratorTestRun run = RunGenerator("namespace Plain; class C { }",
            new InMemoryAdditionalText("owner.dgschema", GenericTemplateHistoryTests.History("Owner", body: "// field:1|15|nUG9pbnQ=()\n")),
            new InMemoryAdditionalText("point.dgschema", GenericTemplateHistoryTests.History("Point", kind: 2)));
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0019");
    }
}
