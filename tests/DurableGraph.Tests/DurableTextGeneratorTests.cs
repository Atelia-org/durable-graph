using Atelia.DurableGraph.Build;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void DurableTextAloneUsesStructuralObjectReferenceGeneration() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            [DurableType("Body",1)] public partial class Body : IDurableObject {
                [DurableField(1)] public DurableText? Text;
            }
            """);
        AssertSchemaOnlyCompiles(run);
        string generated = GeneratedSource(run, "DurableGenericStates.g.cs");
        Assert.Contains("global::Atelia.DurableGraph.Schema.TypeExpr.Text", generated);
        Assert.Contains("context.CaptureObject(field", generated);
        Assert.Contains("objects.ResolveObject<global::Atelia.DurableGraph.DurableText", generated);
        Assert.Contains("visitor.VisitObject(", generated);
        string manifest = GeneratedSource(run, "DurableGraphSchemaHistoryCandidates.g.cs");
        Assert.Contains("manifest:10", manifest);
        Assert.Contains("// field:1|15|t", manifest);
    }

    [Fact]
    public void DurableTextComposesThroughGenericBaseInlineAndContainers() {
        GeneratorTestRun run = RunGenerator("""
            using System.Collections.Generic;
            using Atelia.DurableGraph;
            [DurableType("Box",1)] public partial class Box<T> : IDurableObject { [DurableField(1)] public T Value; }
            [DurableType("Part",1)] public partial struct Part<T> { [DurableField(1)] public T Value; }
            [DurableType("World",1)] public partial class World : Box<DurableText> {
                [DurableField(1)] public DurableText? Body;
                [DurableField(2)] public List<DurableText> Bodies;
                [DurableField(3)] public DurableText[,] Matrix;
                [DurableField(4)] public Dictionary<DurableText,List<DurableText[]>> Map;
                [DurableField(5)] public Part<DurableText> Part;
            }
            """);
        AssertSchemaOnlyCompiles(run);
        string manifest = GeneratedSource(run, "DurableGraphSchemaHistoryCandidates.g.cs");
        Assert.Contains("// base:nQm94(t)|1", manifest);
        Assert.Contains("// field:2|15|l(t)", manifest);
        Assert.Contains("// field:3|15|a2(t)", manifest);
        Assert.Contains("// field:4|15|d(t,l(a1(t)))", manifest);
        Assert.Contains("// field:5|16|nUGFydA==(t)|1", manifest);
    }

    [Fact]
    public void DurableTextHistoryPublishVerifyAndReloadPreserveNonTextVersionNineBytes() {
        using AncestryHistoryDirectory history = new();
        const string source = """
            using Atelia.DurableGraph;
            [DurableType("Plain",1)] public partial class Plain : IDurableObject { [DurableField(1)] public int Number; }
            [DurableType("Body",1)] public partial class Body : IDurableObject { [DurableField(1)] public DurableText Text; }
            """;
        GeneratorTestRun first = RunGenerator(source);
        AssertSchemaOnlyCompiles(first);
        SchemaHistoryTool tool = new();
        string manifest = history.WriteManifest(first);
        tool.Publish(manifest, history.History);
        tool.Verify(manifest, history.History);
        var before = Directory.GetFiles(history.History, "*.dgschema").ToDictionary(path => path, File.ReadAllText);
        Assert.Equal(2, before.Count);
        Assert.Single(before.Values, text => text.StartsWith("// durable-graph-schema-history:9\n", StringComparison.Ordinal));
        Assert.Single(before.Values, text => text.StartsWith("// durable-graph-schema-history:10\n", StringComparison.Ordinal));
        GeneratorTestRun second = RunGenerator(source, history.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(second);
        Assert.Contains("0 schema-history record(s); 2 already exact", tool.Publish(history.WriteManifest(second), history.History).Message);
        tool.Verify(history.WriteManifest(second), history.History);
        foreach (var entry in before) Assert.Equal(entry.Value, File.ReadAllText(entry.Key));
    }

    [Fact]
    public void DurableTextEveryOldHistoryVersionRejectsDirectAndNestedText() {
        using BclHistoryFixture fixture = new();
        for (int version = 1; version <= 9; version++) {
            foreach (string pattern in new[] { "t", "l(t)", "a1(t)", "d(b2,t)", "nUGhhbnRvbQ==(t)" }) {
                string text = BclHistory("Old", "// field:1|15|" + pattern + "\n", version);
                Assert.Throws<SchemaHistoryException>(() => fixture.Parse(text));
                Assert.Throws<SchemaHistoryException>(() => fixture.ParseManifest(text));
                GeneratorTestRun run = RunGenerator("class Plain {}", new InMemoryAdditionalText("old.dgschema", text));
                Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0012");
            }
        }
    }

    [Theory]
    [InlineData("15|t()")]
    [InlineData("15|t(b2)")]
    [InlineData("15|q(t)")]
    [InlineData("18|q(t)")]
    [InlineData("16|t|1")]
    [InlineData("17|t")]
    public void DurableTextHistoryRejectsMalformedOrValueTextSlots(string field) {
        using BclHistoryFixture fixture = new();
        string text = BclHistory("Bad", "// field:1|" + field + "\n", 10);
        Assert.Throws<SchemaHistoryException>(() => fixture.Parse(text));
        Assert.Throws<SchemaHistoryException>(() => fixture.ParseManifest(text));
        GeneratorTestRun run = RunGenerator("class Plain {}", new InMemoryAdditionalText("bad.dgschema", text));
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0012");
    }

    [Fact]
    public void DurableTextVersionTenRoundTripsEveryReferencePositionAndRejectsDowngrade() {
        using BclHistoryFixture fixture = new();
        const string body = "// base:nUGFyZW50(t)|1\n// field:1|15|t\n// field:2|15|l(t)\n" +
            "// field:3|15|a4(t)\n// field:4|15|d(t,nUGhhbnRvbQ==(t))\n";
        string text = BclHistory("World", body, 10);
        SchemaHistoryRecord record = fixture.Parse(text);
        Assert.Equal(text, SchemaHistoryTextCodec.RenderHistory(record));
        Assert.True(record.ShapeEquals(Assert.Single(fixture.ParseManifest(text))));
        Assert.Throws<SchemaHistoryException>(() => SchemaHistoryTextCodec.RenderHistory(record, 9));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DurableTextCrossAssemblyInlineExportsAndImportedHistoryAreCanonical(bool containsText) {
        using AncestryHistoryDirectory history = new();
        const string librarySource = """
            using Atelia.DurableGraph;
            namespace Remote {
                [DurableType("Part",1)] public partial struct Part { [DurableField(1)] public DurableText Body; }
            }
            """;
        GeneratorTestRun library = RunCrossAssemblyGenerator(containsText ? librarySource : librarySource.Replace("DurableText Body", "int Body"),
            forceDefinitions: "true");
        var reference = EmitCrossAssemblyReference(library).Reference;
        const string source = """
            using Atelia.DurableGraph;
            [DurableType("World",1)] public partial class World : IDurableObject { [DurableField(1)] public Remote.Part Part; }
            """;
        GeneratorTestRun app = RunCrossAssemblyGenerator(source, [reference]);
        AssertSchemaOnlyCompiles(app);
        Assert.Contains(containsText ? "manifest:10" : "manifest:9", GeneratedSource(library, "DurableGraphSchemaExports.g.cs"));
        // The local record has a named inline slot; its imported dependency owns Text.
        Assert.Contains("manifest:9", GeneratedSource(app, "DurableGraphSchemaHistoryCandidates.g.cs"));
        PublishCrossInlineHistory(history, app);
        GeneratorTestRun again = RunCrossAssemblyGenerator(source, [reference], history: history.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(again);
        PublishCrossInlineHistory(history, again);
    }

    [Fact]
    public void DurableTextSourceLookalikeAndReadOnlySnapshotAreNotDurableFields() {
        GeneratorTestRun fake = RunGenerator("""
            using Atelia.DurableGraph;
            namespace Atelia.DurableGraph { public sealed class DurableText {} }
            [DurableType("Bad",1)] public partial class Bad : IDurableObject { [DurableField(1)] public DurableText Body; }
            """);
        Assert.Contains(fake.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0007");
        GeneratorTestRun snapshot = RunGenerator("""
            using Atelia.DurableGraph;
            [DurableType("Bad",1)] public partial class Bad : IDurableObject { [DurableField(1)] public TextSnapshot Body; }
            """);
        Assert.Contains(snapshot.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0007");
    }
}
