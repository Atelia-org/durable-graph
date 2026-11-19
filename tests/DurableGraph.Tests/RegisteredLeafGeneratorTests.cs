using Atelia.DurableGraph.Build;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Theory]
    [InlineData(9, "15|t")]
    [InlineData(9, "15|l(t)")]
    [InlineData(10, "2")]
    public void HistoryReadersRequireTheSupportedHeaderAndNominalLeafPattern(int format, string field) {
        using BclHistoryFixture fixture = new();
        string record = BclHistory("Host", "// field:1|" + field + "\n", format);
        Assert.Throws<SchemaHistoryException>(() => fixture.Parse(record));
        Assert.Throws<SchemaHistoryException>(() => fixture.ParseManifest(record));
        var run = RunGenerator("class Plain {}", new InMemoryAdditionalText("archived.dgschema", record));
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0012");
    }

    [Fact]
    public void ExternalLeafKeepsItsNominalIdentityAcrossFieldsAndPublishedHistory() {
        var library = EmitCrossAssemblyReference(RunCrossAssemblyGenerator("""
            using Atelia.DurableGraph.Serialization;
            namespace Plugin;
            [DurableLeaf("Leaf", 2)] public sealed class Leaf {}
            """)).Reference;
        const string source = """
            using System.Collections.Generic;
            using Atelia.DurableGraph;
            using Plugin;
            [DurableType("Box",1)] public partial class Box<T> : IDurableObject { [DurableField(1)] public T? Value; }
            [DurableType("Host",1)] public partial class Host : Box<Leaf> {
                [DurableField(1)] public Leaf? Direct;
                [DurableField(2)] public Dictionary<int,List<Leaf[]>>? Nested;
            }
            """;
        GeneratorTestRun app = RunCrossAssemblyGenerator(source, [library]);
        AssertSchemaOnlyCompiles(app);
        const string leaf = "nTGVhZg==()";
        string manifest = GeneratedSource(app, "DurableGraphSchemaHistoryCandidates.g.cs");
        Assert.Contains("// base:nQm94(" + leaf + ")|1", manifest);
        Assert.Contains("// field:1|15|" + leaf, manifest);
        Assert.Contains("// field:2|15|d(b2,l(a1(" + leaf + ")))", manifest);
        Assert.Contains("context.CaptureObject(", GeneratedSource(app, "DurableGenericStates.g.cs"));
        using AncestryHistoryDirectory files = new();
        SchemaHistoryTool tool = new();
        tool.Publish(files.WriteManifest(app), files.History);
        GeneratorTestRun replay = RunCrossAssemblyGenerator(source, [library], history: files.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(replay);
        tool.Verify(files.WriteManifest(replay), files.History);
    }

    [Fact]
    public void DirectLeafFieldAloneGeneratesTheRegisteredObjectProjection() {
        GeneratorTestRun app = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Serialization;
            namespace Foreign { public interface IDurableObject {} }
            [DurableLeaf("Only")] public sealed class Only : Foreign.IDurableObject {}
            [DurableType("Host",1)] public partial class Host : Atelia.DurableGraph.IDurableObject {
                [DurableField(1)] public Only? Value;
            }
            """);
        AssertSchemaOnlyCompiles(app);
        string source = GeneratedSource(app, "DurableGenericStates.g.cs");
        Assert.Contains("TypeExpr.Named(\"Only\")", source);
        Assert.Contains("context.CaptureObject(", source);
        Assert.Contains("objects.ResolveObject<global::Only", source);
    }

    [Fact]
    public void ImportedInlineShapeWithLeafRetainsItsReferenceHistory() {
        using AncestryHistoryDirectory files = new();
        GeneratorTestRun library = RunCrossAssemblyGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Serialization;
            namespace Plugin {
                [DurableLeaf("Leaf")] public sealed class Leaf {}
                [DurableType("Inline",1)] public partial struct Inline { [DurableField(1)] public Leaf? Value; }
            }
            """, forceDefinitions: "true");
        var reference = EmitCrossAssemblyReference(library).Reference;
        const string source = """
            using Atelia.DurableGraph;
            [DurableType("Host",1)] public partial class Host : IDurableObject { [DurableField(1)] public Plugin.Inline Value; }
            """;
        GeneratorTestRun first = RunCrossAssemblyGenerator(source, [reference]);
        AssertSchemaOnlyCompiles(first);
        Assert.Contains("field:1|15|nTGVhZg==()", GeneratedSource(library, "DurableGraphSchemaExports.g.cs"));
        PublishCrossInlineHistory(files, first);
        GeneratorTestRun repeat = RunCrossAssemblyGenerator(source, [reference], history: files.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(repeat);
        PublishCrossInlineHistory(files, repeat);
    }

    [Theory]
    [InlineData("[DurableLeaf(\" \")] public sealed class Bad {}", "Bad")]
    [InlineData("[DurableLeaf(\"Bad\",0)] public sealed class Bad {}", "Bad")]
    [InlineData("[DurableLeaf(\"Bad\")] internal sealed class Bad {}", "Bad")]
    [InlineData("public class Outer { [DurableLeaf(\"Bad\")] public sealed class Bad {} }", "Outer.Bad")]
    [InlineData("[DurableLeaf(\"Bad\")] public sealed class Bad<T> {}", "Bad<int>")]
    [InlineData("interface IMarker : IDurableObject {} [DurableLeaf(\"Bad\")] public sealed class Bad : IMarker {}", "Bad")]
    public void UnregistrableLeafIsRejectedAtNestedField(string declaration, string fieldType) {
        GeneratorTestRun run = RunGenerator($$"""
            using System.Collections.Generic;
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Serialization;
            {{declaration}}
            [DurableType("Host",1)] public partial class Host : IDurableObject {
                [DurableField(1)] internal List<{{fieldType}}>? Values;
            }
            """);
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0007");
        Assert.DoesNotContain(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "CS8785");
    }

    [Fact]
    public void ClosedUpgradeOwnerWithAnUnregistrableLeafReportsADiagnostic() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Serialization;
            [DurableLeaf("Invalid", 0)] public sealed class Invalid {}
            [DurableType("Box",1)] public partial class Box<T> : IDurableObject {}
            public static class Rules {
                [DurableUpgrade(typeof(Box<Invalid>), 1)] public static void InvalidRule() {}
            }
            """);
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.GetMessage().Contains("unsupported type argument", StringComparison.Ordinal));
        Assert.DoesNotContain(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "CS8785");
    }
}
