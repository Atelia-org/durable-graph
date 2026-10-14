using Atelia.DurableGraph.Runtime;
using System.Text;
using Atelia.DurableGraph.Build;
using Atelia.DurableGraph.Persistence;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void TemporalScalarVersionNineManifestPreservesFixedVersionEightFileWithoutSchemaUpgrade() {
        const string original = "// durable-graph-schema-history:8\n// schema-begin\n// schema-id-base64:V29ybGQ=\n" +
            "// version:1\n// kind:1\n// arity:0\n// field:1|2\n// schema-end\n";
        const string fileName = "schema.78ae647dc5544d227130a0682a51e30bc7777fbb6d8a8f17007463a3ecd1d524.V1.958cfe863bc851f76004b891e166314d921bb24354306e1678d7d24f82455eff.dgschema";
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
    [InlineData(22)]
    [InlineData(23)]
    [InlineData(24)]
    public void TemporalScalarEveryOldHistoryHeaderRejectsDirectAndNestedLeaves(int tag) {
        using BclHistoryFixture fixture = new();
        for (int version = 1; version <= 8; version++) {
            Reject($"// field:1|{tag}\n", version);
            if (version < 3) continue;
            // Nominal-only operands must obey the same gates as exact value slots.
            Reject($"// field:1|15|nUGhhbnRvbQ==(b{tag})\n", version);
            Reject($"// base:nUGFyZW50(b{tag})|1\n", version);
            if (version >= 4) {
                for (int rank = 1; rank <= 4; rank++) Reject($"// field:1|15|a{rank}(b{tag})\n", version);
            }
            if (version >= 5) Reject($"// field:1|15|l(b{tag})\n", version);
            if (version >= 6) Reject($"// field:1|18|q(b{tag})\n", version);
            if (version >= 7) {
                Reject($"// field:1|15|d(b{tag},b2)\n", version);
                Reject($"// field:1|15|d(b2,l(q(b{tag})))\n", version);
            }
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
    public void TemporalScalarVersionNineHistoryPreservesNominalLeavesAndRejectsInvalidOperands() {
        using BclHistoryFixture fixture = new();
        const string body = "// base:nUGFyZW50(b22)|1\n// field:1|22\n// field:2|23\n// field:3|24\n" +
            "// field:4|15|nUGhhbnRvbQ==(b23)\n// field:5|18|q(b24)\n// field:6|15|d(b22,l(q(b24)))\n";
        string text = BclHistory("World", body, 9);
        SchemaHistoryRecord record = fixture.Parse(text);
        Assert.Equal(text, SchemaHistoryTextCodec.RenderHistory(record));
        Assert.Equal("nUGFyZW50(b22)", record.BaseType!.ToString());
        Assert.Equal(new[] { "b22", "b23", "b24", "nUGhhbnRvbQ==(b23)", "q(b24)", "d(b22,l(q(b24)))" },
            record.Fields.Select(field => field.ValuePattern.ToString()));
        Assert.True(record.ShapeEquals(Assert.Single(fixture.ParseManifest(text))));
        Assert.Throws<SchemaHistoryException>(() => SchemaHistoryTextCodec.RenderHistory(record, 8));
        foreach (int gap in new[] { 0, 15, 16, 17, 18, 25, 255 }) {
            Assert.Throws<SchemaHistoryException>(() => fixture.Parse(BclHistory("Bad", $"// field:1|15|l(b{gap})\n", 9)));
        }
        foreach (string invalid in new[] { "1|22|b22", "1|23|nUG9pbnQ=()|1", "1|24|q(b24)", "1|18|b22", "1|25" }) {
            string malformed = BclHistory("Bad", $"// field:{invalid}\n", 9);
            Assert.Throws<SchemaHistoryException>(() => fixture.Parse(malformed));
            Assert.Throws<SchemaHistoryException>(() => fixture.ParseManifest(malformed));
            GeneratorTestRun run = RunGenerator("class Plain {}", new InMemoryAdditionalText("bad.dgschema", malformed));
            Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0012");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void TemporalScalarHistoryExtensionPreservesEachOldFormatAcceptedConstructors(int version) {
        using BclHistoryFixture fixture = new();
        List<string> fields = ["2", "14"];
        if (version >= 3) fields.Add("15|nUGhhbnRvbQ==(b2)");
        if (version >= 4) fields.AddRange(["15|a1(b2)", "15|a2(a3(a4(b2)))"]);
        if (version >= 5) fields.Add("15|l(a1(b2))");
        if (version >= 6) fields.AddRange(["18|q(b2)", "15|l(q(b2))"]);
        if (version >= 7) fields.Add("15|d(b4,l(q(b2)))");
        if (version >= 8) fields.AddRange(["19", "20", "21", "15|nUGhhbnRvbQ==(b19)", "15|d(b20,a1(l(q(b21))))"]);
        string text = BclHistory("Old", string.Concat(fields.Select((field, index) => $"// field:{index + 1}|{field}\n")), version);
        SchemaHistoryRecord record = fixture.Parse(text);
        Assert.Equal(text, SchemaHistoryTextCodec.RenderHistory(record, version));
        Assert.True(record.ShapeEquals(Assert.Single(fixture.ParseManifest(text))));
        GeneratorTestRun run = RunGenerator("class Plain {}", new InMemoryAdditionalText("old.dgschema", text));
        Assert.DoesNotContain(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0012");
    }

    [Fact]
    public void TemporalScalarRetainedInlineHistoryReadsExactStateAfterOldDomainTypeDeletion() {
        using AncestryHistoryDirectory history = new();
        GeneratorTestRun first = RunGenerator("""
            using System;
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("Schedule",1)] public readonly partial record struct OldSchedule(
                [field:DurableField(1)] DateOnly Day, [field:DurableField(2)] TimeOnly Time, [field:DurableField(3)] DateTimeOffset Stamp);
            [DurableType("World",1)] public partial class World:IDurableObject {
                [DurableField(1)] public OldSchedule Value = new(new DateOnly(2024,2,29), TimeOnly.MaxValue,
                    new DateTimeOffset(2024,2,29,23,59,59,TimeSpan.FromHours(14)));
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
        Assert.Null(assembly.GetType("OldSchedule"));
        StateReaderBinding reader = EnumHistoryRegistry(assembly).CreateEnvironment().ResolveReader(prior.Schema!);
        ObjectStateRecord decoded = reader.Read(prior.Id, new StateModelBodySource(prior.Preparation!.PrepareBase(prior).Body.ToArray()));
        object schedule = StateModelField(decoded, "Segment0Field1")!;
        Assert.Equal(new DateOnly(2024,2,29), schedule.GetType().GetField("Segment0Field1")!.GetValue(schedule));
        Assert.Equal(TimeOnly.MaxValue, schedule.GetType().GetField("Segment0Field2")!.GetValue(schedule));
        DateTimeOffset stamp = (DateTimeOffset)schedule.GetType().GetField("Segment0Field3")!.GetValue(schedule)!;
        Assert.True(new DateTimeOffset(2024,2,29,23,59,59,TimeSpan.FromHours(14)).EqualsExact(stamp));
    }
}
