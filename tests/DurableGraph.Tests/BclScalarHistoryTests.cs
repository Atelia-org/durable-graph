using Atelia.DurableGraph.Runtime;
using System.Text;
using Atelia.DurableGraph.Build;
using Atelia.DurableGraph.Persistence;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void BclScalarCurrentManifestPreservesFixedVersionSevenFileWithoutSchemaUpgrade() {
        const string original = "// durable-graph-schema-history:7\n// schema-begin\n// schema-id-base64:V29ybGQ=\n" +
            "// version:1\n// kind:1\n// arity:0\n// field:1|2\n// schema-end\n";
        const string fileName = "schema.78ae647dc5544d227130a0682a51e30bc7777fbb6d8a8f17007463a3ecd1d524.V1.be236c5b43375082727a61e7e1296ff9ea65cfe5665d2d073de1f21c15d66beb.dgschema";
        using AncestryHistoryDirectory history = new();
        Directory.CreateDirectory(history.History);
        string path = Path.Combine(history.History, fileName);
        byte[] originalBytes = Encoding.UTF8.GetBytes(original);
        File.WriteAllBytes(path, originalBytes);
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("World",1)] public partial class World:IDurableObject { [DurableField(1)] public int Renamed; }
            """, history.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(run);
        Assert.Contains("manifest:9", GeneratedSource(run, "DurableGraphSchemaHistoryCandidates.g.cs"));
        SchemaHistoryTool tool = new();
        Assert.Equal("published 0 schema-history record(s); 1 already exact", tool.Publish(history.WriteManifest(run), history.History).Message);
        tool.Verify(history.WriteManifest(run), history.History);
        Assert.Equal(path, Assert.Single(Directory.GetFiles(history.History)));
        Assert.Equal(originalBytes, File.ReadAllBytes(path));
        Assert.Equal(fileName, SchemaHistoryTextCodec.GetHistoryFileName(SchemaHistoryTextCodec.ParseHistory(path), original));
    }

    [Theory]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(21)]
    public void BclScalarEveryOldHistoryHeaderRejectsDirectAndNestedLeaves(int tag) {
        using BclHistoryFixture fixture = new();
        for (int version = 1; version <= 7; version++) {
            Reject($"// field:1|{tag}\n", version);
            if (version < 3) continue;
            // Nominal arguments are not exact dependencies, but their format capabilities still apply.
            Reject($"// field:1|15|nUGhhbnRvbQ==(b{tag})\n", version);
            Reject($"// base:nUGFyZW50(b{tag})|1\n", version);
            if (version >= 4) Reject($"// field:1|15|a2(b{tag})\n", version);
            if (version >= 5) Reject($"// field:1|15|l(b{tag})\n", version);
            if (version >= 6) Reject($"// field:1|18|q(b{tag})\n", version);
            if (version >= 7) Reject($"// field:1|15|d(b2,l(q(b{tag})))\n", version);
        }

        void Reject(string body, int version) {
            string text = BclHistory("Old", body, version);
            Assert.Throws<SchemaHistoryException>(() => fixture.Parse(text));
            Assert.Throws<SchemaHistoryException>(() => fixture.ParseManifest(text));
            GeneratorTestRun run = RunGenerator("class Plain {}", new InMemoryAdditionalText("old.dgschema", text));
            Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0012");
        }
    }

    [Fact]
    public void BclScalarVersionEightHistoryPreservesNominalLeavesAndBuiltinGaps() {
        using BclHistoryFixture fixture = new();
        const string body = "// base:nUGFyZW50(b19)|1\n// field:1|19\n// field:2|20\n// field:3|21\n" +
            "// field:4|15|nUGhhbnRvbQ==(b20)\n// field:5|18|q(b21)\n// field:6|15|d(b19,l(q(b20)))\n";
        string text = BclHistory("World", body, 8);
        SchemaHistoryRecord record = fixture.Parse(text);
        Assert.Equal(text, SchemaHistoryTextCodec.RenderHistory(record, 8));
        Assert.Equal("nUGFyZW50(b19)", record.BaseType!.ToString());
        Assert.Equal(new[] { "b19", "b20", "b21", "nUGhhbnRvbQ==(b20)", "q(b21)", "d(b19,l(q(b20)))" },
            record.Fields.Select(field => field.ValuePattern.ToString()));
        Assert.Throws<SchemaHistoryException>(() => SchemaHistoryTextCodec.RenderHistory(record, 7));
        foreach (int gap in new[] { 0, 15, 16, 17, 18, 22 }) {
            Assert.Throws<SchemaHistoryException>(() => fixture.Parse(BclHistory("Bad", $"// field:1|15|l(b{gap})\n", 8)));
        }
        foreach (string invalid in new[] { "1|19|b19", "1|20|nUG9pbnQ=()|1", "1|21|q(b21)", "1|18|b19", "1|22" }) {
            Assert.Throws<SchemaHistoryException>(() => fixture.Parse(BclHistory("Bad", $"// field:{invalid}\n", 8)));
        }
    }

    [Fact]
    public void BclScalarRetainedInlineHistoryReadsExactStateAfterOldDomainTypeDeletion() {
        using AncestryHistoryDirectory history = new();
        GeneratorTestRun first = RunGenerator("""
            using System;
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("Money",1)] public readonly partial record struct OldMoney(
                [field:DurableField(1)] Guid Id, [field:DurableField(2)] decimal Amount, [field:DurableField(3)] TimeSpan Duration);
            [DurableType("World",1)] public partial class World:IDurableObject {
                [DurableField(1)] public OldMoney Value=new(new Guid("00112233-4455-6677-8899-aabbccddeeff"),1.00m,TimeSpan.MinValue);
            }
            """);
        AssertSchemaOnlyCompiles(first);
        ObjectStateRecord prior = CaptureRecordHistoryWorld(first);
        new SchemaHistoryTool().Publish(history.WriteManifest(first), history.History);
        Assert.All(history.ReadContents().Values, text => Assert.StartsWith("// durable-graph-schema-history:9\n", text));
        GeneratorTestRun next = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("World",2)] public partial class World:IDurableObject { [DurableField(1)] public long Value; }
            """, history.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(next);
        var assembly = EmitAndLoad(next.OutputCompilation);
        Assert.Null(assembly.GetType("OldMoney"));
        StateReaderBinding reader = EnumHistoryRegistry(assembly).CreateEnvironment().ResolveReader(prior.Schema!);
        ObjectStateRecord decoded = reader.Read(prior.Id, new StateModelBodySource(prior.Preparation!.PrepareBase(prior).Body.ToArray()));
        object money = StateModelField(decoded, "Segment0Field1")!;
        Assert.Equal(new Guid("00112233-4455-6677-8899-aabbccddeeff"), money.GetType().GetField("Segment0Field1")!.GetValue(money));
        Assert.Equal(new[] { 100, 0, 0, 0x00020000 }, decimal.GetBits((decimal)money.GetType().GetField("Segment0Field2")!.GetValue(money)!));
        Assert.Equal(TimeSpan.MinValue, money.GetType().GetField("Segment0Field3")!.GetValue(money));
    }

    private static string BclHistory(string id, string body, int formatVersion) =>
        $"// durable-graph-schema-history:{formatVersion}\n// schema-begin\n// schema-id-base64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(id)) +
        "\n// version:1\n" + (formatVersion >= 2 ? "// kind:1\n" : "") + (formatVersion >= 3 ? "// arity:0\n" : "") + body + "// schema-end\n";

    private sealed class BclHistoryFixture : IDisposable {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "durable-bcl-history-" + Guid.NewGuid().ToString("N"));
        internal BclHistoryFixture() => Directory.CreateDirectory(_root);
        internal SchemaHistoryRecord Parse(string text) {
            string path = Path.Combine(_root, "input.dgschema");
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return SchemaHistoryTextCodec.ParseHistory(path);
        }
        internal IReadOnlyList<SchemaHistoryRecord> ParseManifest(string text) {
            string path = Path.Combine(_root, "manifest.g.cs");
            File.WriteAllText(path, text.Replace("// durable-graph-schema-history:", "// durable-graph-schema-history-manifest:"), new UTF8Encoding(false));
            return SchemaHistoryTextCodec.ParseManifest(path);
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
