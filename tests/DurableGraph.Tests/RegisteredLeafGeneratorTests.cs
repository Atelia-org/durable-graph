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

    [Theory]
    [InlineData("DurableLeaf")]
    [InlineData("DurableNode")]
    public void ExternalRegisteredObjectKeepsItsIdentityAcrossFieldsAndPublishedHistory(string marker) {
        var library = EmitCrossAssemblyReference(RunCrossAssemblyGenerator($$"""
            using Atelia.DurableGraph.Serialization;
            namespace Plugin;
            [{{marker}}("Item", 2)] public sealed class Item {}
            """)).Reference;
        const string source = """
            using System.Collections.Generic;
            using Atelia.DurableGraph;
            using Plugin;
            [DurableType("Box",1)] public partial class Box<T> : IDurableObject { [DurableField(1)] public T? Value; }
            [DurableType("Host",1)] public partial class Host : Box<Item> {
                [DurableField(1)] public Item? Direct;
                [DurableField(2)] public Dictionary<Item,List<Item[]>>? Nested;
            }
            """;
        GeneratorTestRun app = RunCrossAssemblyGenerator(source, [library]);
        AssertSchemaOnlyCompiles(app);
        const string item = "nSXRlbQ==()";
        string manifest = GeneratedSource(app, "DurableGraphSchemaHistoryCandidates.g.cs");
        Assert.Contains("// base:nQm94(" + item + ")|1", manifest);
        Assert.Contains("// field:1|15|" + item, manifest);
        Assert.Contains("// field:2|15|d(" + item + ",l(a1(" + item + ")))", manifest);
        Assert.Contains("context.CaptureObject(", GeneratedSource(app, "DurableGenericStates.g.cs"));
        using AncestryHistoryDirectory files = new();
        SchemaHistoryTool tool = new();
        tool.Publish(files.WriteManifest(app), files.History);
        GeneratorTestRun replay = RunCrossAssemblyGenerator(source, [library], history: files.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(replay);
        tool.Verify(files.WriteManifest(replay), files.History);
    }

    [Theory]
    [InlineData("DurableLeaf")]
    [InlineData("DurableNode")]
    public void DirectRegisteredFieldAloneGeneratesTheObjectProjection(string marker) {
        GeneratorTestRun app = RunGenerator($$"""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Serialization;
            namespace Foreign { public interface IDurableObject {} }
            [{{marker}}("Only")] public sealed class Only : Foreign.IDurableObject {}
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

    [Theory]
    [InlineData("DurableLeaf")]
    [InlineData("DurableNode")]
    public void ImportedInlineShapeWithRegisteredObjectRetainsItsReferenceHistory(string marker) {
        using AncestryHistoryDirectory files = new();
        GeneratorTestRun library = RunCrossAssemblyGenerator($$"""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Serialization;
            namespace Plugin {
                [{{marker}}("Item")] public sealed class Item {}
                [DurableType("Inline",1)] public partial struct Inline { [DurableField(1)] public Item? Value; }
            }
            """, forceDefinitions: "true");
        var reference = EmitCrossAssemblyReference(library).Reference;
        const string source = """
            using Atelia.DurableGraph;
            [DurableType("Host",1)] public partial class Host : IDurableObject { [DurableField(1)] public Plugin.Inline Value; }
            """;
        GeneratorTestRun first = RunCrossAssemblyGenerator(source, [reference]);
        AssertSchemaOnlyCompiles(first);
        Assert.Contains("field:1|15|nSXRlbQ==()", GeneratedSource(library, "DurableGraphSchemaExports.g.cs"));
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
    [InlineData("[DurableNode(\"Node\"), DurableLeaf(\"Leaf\")] public sealed class Bad {}", "Bad")]
    [InlineData("[DurableLeaf(\"Leaf\"), DurableNode(\"Node\")] public sealed class Bad {}", "Bad")]
    [InlineData("[DurableNode(\"Node\"), DurableType(\"Model\",1)] public sealed partial class Bad : IDurableObject {}", "Bad")]
    [InlineData("[DurableNode(\"Node\")] public class Bad {}", "Bad")]
    public void UnregistrableObjectIsRejectedAtNestedField(string declaration, string fieldType) {
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
    public void LookalikeNodeMarkerFromAPluginHasNoEffectOnNominalIdentity() {
        var library = EmitCrossAssemblyReference(RunCrossAssemblyGenerator("""
            using Atelia.DurableGraph.Serialization;
            namespace Atelia.DurableGraph.Serialization {
                public sealed class DurableNodeAttribute : System.Attribute {
                    public DurableNodeAttribute(string id, int version = 1) {}
                }
            }
            namespace Plugin {
                [DurableNode("Fake")] public sealed class Fake {}
                [DurableNode("Fake"), DurableLeaf("Real")] public sealed class Real {}
            }
            """)).Reference;
        const string goodSource = """
            using Atelia.DurableGraph;
            [DurableType("Host",1)] public partial class Host : IDurableObject {
                [DurableField(1)] public Plugin.Real? Value;
            }
            """;
        GeneratorTestRun good = RunCrossAssemblyGenerator(goodSource, [library]);
        AssertSchemaOnlyCompiles(good);
        Assert.Contains("// field:1|15|nUmVhbA==()", GeneratedSource(good, "DurableGraphSchemaHistoryCandidates.g.cs"));
        GeneratorTestRun bad = RunCrossAssemblyGenerator(goodSource.Replace("Plugin.Real", "Plugin.Fake"), [library]);
        Assert.Contains(bad.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0007");
    }

    [Fact]
    public void NodeDeclarationUsesTheSameUnicodeAndVersionContractAsLeaves() {
        Assert.Throws<ArgumentNullException>("id", () => new Serialization.DurableNodeAttribute(null!));
        Assert.Throws<ArgumentException>("id", () => new Serialization.DurableNodeAttribute(" "));
        Assert.Throws<ArgumentException>("id", () => new Serialization.DurableNodeAttribute("node" + (char)0xd800));
        Assert.Throws<ArgumentOutOfRangeException>("version", () => new Serialization.DurableNodeAttribute("例.🐈", 0));
        var node = new Serialization.DurableNodeAttribute("例.🐈");
        Assert.Equal("例.🐈", node.Id);
        Assert.Equal(1, node.Version);
    }

    [Theory]
    [InlineData("DurableLeaf")]
    [InlineData("DurableNode")]
    public void ClosedUpgradeOwnerWithAnUnregistrableArgumentReportsADiagnostic(string marker) {
        GeneratorTestRun run = RunGenerator($$"""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Serialization;
            [{{marker}}("Invalid", 0)] public sealed class Invalid {}
            [DurableType("Box",1)] public partial class Box<T> : IDurableObject {}
            public static class Rules {
                [DurableUpgrade(typeof(Box<Invalid>), 1)] public static void InvalidRule() {}
            }
            """);
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.GetMessage().Contains("unsupported type argument", StringComparison.Ordinal));
        Assert.DoesNotContain(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "CS8785");
    }
}
