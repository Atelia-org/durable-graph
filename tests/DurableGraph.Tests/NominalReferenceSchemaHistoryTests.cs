using Atelia.DurableGraph.Schema;
using System.Reflection;
using System.Text;
using Atelia.DurableGraph.Build;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    private const string NominalHistory = "// durable-graph-schema-history:1\n// schema-begin\n" +
        "// schema-id-base64:QQ==\n// version:1\n// field:1|15|Qg==\n// schema-end\n";

    [Fact]
    public void NominalReferenceSchemasAllowSelfAndMutualReferencesWithoutStaticCycles() {
        GeneratorTestRun run = RunGenerator(NominalSource(1));
        AssertSchemaOnlyCompiles(run);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        DurableSchema a = ReadSchemaOnly(assembly.GetType("Nominal.A")!, 1);
        DurableSchema b = ReadSchemaOnly(assembly.GetType("Nominal.B")!, 1);
        Assert.Equal("B", Assert.Single(a.Fields).TargetSchemaId);
        Assert.Equal(new[] { "A", "B" }, b.Fields.Select(field => field.TargetSchemaId));
        Assert.Null(a.BaseSchema);
        Assert.Null(b.BaseSchema);
        string generated = GeneratedSource(run, "DurableSchemas.g.cs");
        Assert.DoesNotContain("B.GetSchema", generated);
        Assert.DoesNotContain("A.GetSchema", generated);
    }

    [Fact]
    public void TargetVersionChangesDoNotPropagateButNominalFamilyChangesDo() {
        var aHistory = new InMemoryAdditionalText("a.dgschema", NominalHistory);
        var bHistory = new InMemoryAdditionalText("b.dgschema", NominalHistory
            .Replace("schema-id-base64:QQ==", "schema-id-base64:Qg==")
            .Replace("// field:1|15|Qg==", "// field:1|15|QQ==\n// field:2|15|Qg=="));
        GeneratorTestRun updatedTarget = RunGenerator(NominalSource(2), aHistory, bHistory);
        AssertSchemaOnlyCompiles(updatedTarget);
        GeneratorTestRun changedFamily = RunGenerator(NominalSource(1).Replace("public B Next;", "public A Next;"), aHistory, bHistory);
        Assert.Contains(changedFamily.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0015");
    }

    [Fact]
    public void HistoricalNominalMetadataDoesNotNeedDeletedTargetClrType() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("A", 2)]
            public partial class A : IDurableObject { [DurableField(1)] public int Value; }
            """, new InMemoryAdditionalText("a.dgschema", NominalHistory));
        AssertSchemaOnlyCompiles(run);
        Type owner = EmitAndLoad(run.OutputCompilation).GetType("A")!;
        Assert.Equal(new DurableFieldInfo(1, TypeTag.ObjectReference, "B"), Assert.Single(ReadSchemaOnly(owner, 1).Fields));
        Assert.Equal(TypeTag.Int32, Assert.Single(ReadSchemaOnly(owner, 2).Fields).TypeTag);
    }

    [Fact]
    public void NominalHistoryGoldenSurvivesBuildPublishVerifyAndRejectsSameVersionConflict() {
        string directory = Path.Combine(Path.GetTempPath(), "durable-nominal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            string file = Path.Combine(directory, "a.dgschema");
            File.WriteAllText(file, NominalHistory, new UTF8Encoding(false));
            SchemaHistoryRecord record = SchemaHistoryTextCodec.ParseHistory(file);
            Assert.Equal(NominalHistory.Replace("history:1", "history:9").Replace("// version:1\n", "// version:1\n// kind:1\n// arity:0\n")
                    .Replace("|15|Qg==", "|15|nQg==()"),
                SchemaHistoryTextCodec.RenderHistory(record));
            Assert.Equal("B", Assert.Single(record.Fields).TargetSchemaId);
            string manifest = Path.Combine(directory, "manifest.g.cs");
            File.WriteAllText(manifest, NominalHistory.Replace("// durable-graph-schema-history:1", "// durable-graph-schema-history-manifest:1"));
            string history = Path.Combine(directory, "history");
            SchemaHistoryTool tool = new();
            tool.Publish(manifest, history);
            tool.Verify(manifest, history);
            string original = File.ReadAllText(Assert.Single(Directory.GetFiles(history)));
            File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("|Qg==", "|Qw=="));
            Assert.Throws<SchemaHistoryException>(() => tool.Publish(manifest, history));
            Assert.Throws<SchemaHistoryException>(() => tool.Verify(manifest, history));
            Assert.Equal(original, File.ReadAllText(Assert.Single(Directory.GetFiles(history))));
        }
        finally {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("1|15")]
    [InlineData("1|15|")]
    [InlineData("1|15|IA==")]
    [InlineData("1|15|Qg==|QQ==")]
    [InlineData("1|15|Qh==")]
    [InlineData("1|15|/w==")]
    [InlineData("01|15|Qg==")]
    [InlineData("1|015|Qg==")]
    [InlineData("1|2|Qg==")]
    [InlineData("1|16|Qg==")]
    public void BothHistoryParsersRejectMalformedNominalField(string field) {
        string malformed = NominalHistory.Replace("1|15|Qg==", field);
        GeneratorTestRun run = RunGenerator(NominalSource(1), new InMemoryAdditionalText("bad.dgschema", malformed));
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0012");
        string file = Path.GetTempFileName();
        try {
            File.WriteAllText(file, malformed, new UTF8Encoding(false));
            Assert.Throws<SchemaHistoryException>(() => SchemaHistoryTextCodec.ParseHistory(file));
        }
        finally { File.Delete(file); }
    }

    private static string NominalSource(int targetVersion) => $$"""
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        namespace Nominal;
        [DurableType("A", 1)]
        public partial class A : IDurableObject {
            [DurableField(1)] public B Next;
        }
        [DurableType("B", {{targetVersion}})]
        public partial class B : IDurableObject {
            [DurableField(1)] public A Back;
            [DurableField(2)] public B Self;
        }
        """;
}
