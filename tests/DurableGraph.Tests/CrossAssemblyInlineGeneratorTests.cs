using Atelia.DurableGraph.Schema;
using System.Collections.Immutable;
using System.Text;
using Atelia.DurableGraph.Build;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    private const string InlineLibrarySource = """
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        namespace Remote {
            [DurableType("Point",1)] public readonly partial struct Point {
                [DurableField(1)] private readonly int _x;
                public Point(int x) => _x=x;
            }
            [DurableType("Pair",1)] public partial struct Pair<T> { [DurableField(1)] public T Item; }
            [DurableType("Key",1)] public readonly partial record struct Key([field:DurableField(1)] int Code);
            [DurableType("Choice",1)] public enum Choice:byte { One=1 }
            [DurableType("Hidden",1)] internal partial struct Hidden { [DurableField(1)] public int X; }
            [DurableType("Shell",1)] public partial struct Shell { [DurableField(1)] private Hidden _hidden; }
        }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrossAssemblyInlineMetadataAndReferenceAssemblyRetainExecutionContract(bool referenceAssembly) {
        GeneratorTestRun library = RunCrossAssemblyGenerator(InlineLibrarySource, forceDefinitions:"true");
        AssertSchemaOnlyCompiles(library);
        using MemoryStream stream = new();
        EmitResult emit = library.OutputCompilation.Emit(stream, options:new EmitOptions(metadataOnly:referenceAssembly, includePrivateMembers:!referenceAssembly));
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        MetadataReference reference = MetadataReference.CreateFromImage(ImmutableArray.CreateRange(stream.ToArray()));
        GeneratorTestRun app = RunCrossAssemblyGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("World",1)] public partial class World:IDurableObject {
                [DurableField(1)] public Remote.Point Position;
                [DurableField(2)] public Remote.Shell Shell;
            }
            """, [reference]);
        AssertSchemaOnlyCompiles(app);
        string body = GeneratedSource(app,"DurableGenericStates.g.cs");
        Assert.Contains("global::Remote.Point.__DurableProjection.Capture",body);
        Assert.Contains("global::Atelia.DurableGraph.Generated.Family_506F696E74.BodyV1",body);
        Assert.DoesNotContain("public static class Family_506F696E74",body);
        Assert.DoesNotContain("Remote.Hidden",body);
        Assert.Contains("// references-sha256:",GeneratedSource(app,"DurableGraphSchemaHistoryCandidates.g.cs"));
        Assert.Contains("// reference|",GeneratedSource(app,"DurableGraphSchemaHistoryReferences.g.cs"));
        string exports = GeneratedSource(app,"DurableGraphSchemaExports.g.cs");
        Assert.Contains("(2, \"World\"", exports);
        Assert.DoesNotContain("(1, \"Point\"", exports);
    }

    [Theory]
    [InlineData("Remote.Point")]
    [InlineData("Remote.Point?")]
    [InlineData("Remote.Key")]
    [InlineData("Remote.Choice")]
    [InlineData("Remote.Pair<LocalPoint>")]
    [InlineData("Remote.Pair<T>")]
    [InlineData("LocalInline")]
    public void CrossAssemblyInlineFixedShapeMatrix(string fieldType) {
        var library = EmitCrossAssemblyReference(RunCrossAssemblyGenerator(InlineLibrarySource,forceDefinitions:"true"));
        GeneratorTestRun app=RunCrossAssemblyGenerator($$"""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("LocalPoint",1)] public partial struct LocalPoint { [DurableField(1)] public int X; }
            [DurableType("LocalInline",1)] public partial struct LocalInline { [DurableField(1)] public Remote.Point Position; }
            [DurableType("Base",1)] public partial class Base:IDurableObject { [DurableField(1)] public Remote.Point Position; }
            [DurableType("World",1)] public partial class World<T>:Base { [DurableField(1)] public {{fieldType}} Value; }
            """,[library.Reference]);
        AssertSchemaOnlyCompiles(app);
        string exports=GeneratedSource(app,"DurableGraphSchemaExports.g.cs");
        Assert.Contains("LocalInline",exports);
        Assert.DoesNotContain("(1, \"Point\"",exports);
    }

    [Fact]
    public void CrossAssemblyInlineOrdinaryLibraryReportsOptInAndNominalUseDoesNotRequestExports() {
        const string source="using Atelia.DurableGraph; [DurableType(\"Point\",1)] public partial struct Point { [DurableField(1)] public int X; }";
        var library=EmitCrossAssemblyReference(RunCrossAssemblyGenerator(source));
        GeneratorTestRun fixedUse=RunCrossAssemblyGenerator("using Atelia.DurableGraph; [DurableType(\"World\",1)] public partial class World:IDurableObject { [DurableField(1)] public Point Value; }",[library.Reference]);
        Assert.Contains(fixedUse.GeneratorDiagnostics,d=>d.Id=="DG0023"&&d.GetMessage().Contains("DurableGraphGenerateDefinitions=true"));
        GeneratorTestRun nominal=RunCrossAssemblyGenerator("using Atelia.DurableGraph; [DurableType(\"World\",1)] public partial class World:IDurableObject { [DurableField(1)] public Point[] Value=[]; }",[library.Reference]);
        AssertSchemaOnlyCompiles(nominal);
        Assert.DoesNotContain("references-sha256",GeneratedSource(nominal,"DurableGraphSchemaHistoryCandidates.g.cs"));
    }

    [Theory]
    [InlineData(3,false)]
    [InlineData(2,false)] // A supported class contract is invalid for an inline import.
    [InlineData(1,true)]
    public void CrossAssemblyInlineRejectsUnknownContractOrMissingHelpers(int contract,bool missingHelpers) {
        string manifest=InlinePointManifest;
        string source=$$"""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [assembly:DurableSchemaExport({{contract}},"Point",1,{{Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(manifest,true)}})]
            [DurableType("Point",1)] public struct Point { public int X; }
            """;
        MetadataReference library=EmitCrossAssemblyRawReference(source);
        GeneratorTestRun app=RunCrossAssemblyGenerator("using Atelia.DurableGraph; [DurableType(\"World\",1)] public partial class World:IDurableObject { [DurableField(1)] public Point Value; }",[library]);
        Assert.Contains(app.GeneratorDiagnostics,d=>d.Id=="DG0023" && d.GetMessage().Contains(missingHelpers ? "helpers" : "execution contract"));
        Assert.DoesNotContain(app.GeneratorDiagnostics,d=>d.Id=="CS8785");
    }

    [Fact]
    public void CrossAssemblyInlineRejectsMultipleOwnersButIgnoresUnusedBadExports() {
        var good=EmitCrossAssemblyReference(RunCrossAssemblyGenerator(InlineLibrarySource,forceDefinitions:"true"));
        MetadataReference bad=EmitCrossAssemblyRawReference("[assembly:Atelia.DurableGraph.DurableSchemaExport(9,\"Point\",1,\"bad\")] public class Other {}");
        GeneratorTestRun conflict=RunCrossAssemblyGenerator("using Atelia.DurableGraph; [DurableType(\"World\",1)] public partial class World:IDurableObject { [DurableField(1)] public Remote.Point Value; }",[good.Reference,bad]);
        Assert.Contains(conflict.GeneratorDiagnostics,d=>d.Id=="DG0023"&&d.GetMessage().Contains("owners"));
        GeneratorTestRun unused=RunCrossAssemblyGenerator("using Atelia.DurableGraph; [DurableType(\"World\",1)] public partial class World:IDurableObject { [DurableField(1)] public int Value; }",[good.Reference,bad]);
        AssertSchemaOnlyCompiles(unused);
    }

    [Fact]
    public void CrossAssemblyInlineRejectsTransitiveForeignDependencyClaimedLocally() {
        var library=EmitCrossAssemblyReference(RunCrossAssemblyGenerator(InlineLibrarySource,forceDefinitions:"true"));
        // Shell's private fixed dependency is Hidden. Identical local layout still cannot replace its owner.
        GeneratorTestRun app=RunCrossAssemblyGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("Hidden",1)] public partial struct LocalHidden { [DurableField(1)] public int X; }
            [DurableType("World",1)] public partial class World:IDurableObject { [DurableField(1)] public Remote.Shell Value; }
            """,[library.Reference]);
        Assert.Contains(app.GeneratorDiagnostics,d=>d.Id=="DG0023"&&d.GetMessage().Contains("owned locally"));
    }

    [Fact]
    public void CrossAssemblyInlineHistoryOnlyAndRuleOnlyImportsRetainForeignBodies() {
        using AncestryHistoryDirectory libraryHistory = new();
        using AncestryHistoryDirectory appHistory = new();
        var tool = new SchemaHistoryTool();
        const string libraryV1Source = "using Atelia.DurableGraph; [DurableType(\"Point\",1)] public partial struct Point { [DurableField(1)] public int X; }";
        GeneratorTestRun libraryV1 = RunCrossAssemblyGenerator(libraryV1Source, forceDefinitions: "true");
        tool.Publish(libraryHistory.WriteManifest(libraryV1), libraryHistory.History);
        var referenceV1 = EmitCrossAssemblyReference(libraryV1).Reference;
        const string appV1Source = "using Atelia.DurableGraph; [DurableType(\"World\",1)] public partial class World:IDurableObject { [DurableField(1)] public Point Position; }";
        GeneratorTestRun appV1 = RunCrossAssemblyGenerator(appV1Source, [referenceV1]);
        AssertSchemaOnlyCompiles(appV1);
        string referencePath = Path.Combine(appHistory.History, "../references.g.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(referencePath)!);
        File.WriteAllText(referencePath, GeneratedSource(appV1, "DurableGraphSchemaHistoryReferences.g.cs"));
        tool.Publish(appHistory.WriteManifest(appV1), appHistory.History, referencePath);
        Assert.Single(Directory.GetFiles(appHistory.History, "*.dgschema"));
        GeneratorTestRun libraryV2 = RunCrossAssemblyGenerator("using Atelia.DurableGraph; [DurableType(\"Point\",2)] public partial struct NewPoint { [DurableField(1)] public long X; }",
            history: libraryHistory.ReadAdditionalTexts(), forceDefinitions: "true");
        var referenceV2 = EmitCrossAssemblyReference(libraryV2).Reference;
        GeneratorTestRun appV2 = RunCrossAssemblyGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using W=Atelia.DurableGraph.Generated.Family_576F726C64;
            [DurableType("World",2)] public partial class World:IDurableObject {
                [DurableField(2)] public int Count;
                public static void UpgradeV1ToV2(in W.V1 prior,out W.V2 next) => next=new(prior.Segment0Field1.Segment0Field1);
            }
            """, [referenceV2], appHistory.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(appV2);
        Assert.Contains("Family_506F696E74.V1", GeneratedSource(appV2, "DurableGenericStates.g.cs"));
        Assert.DoesNotContain("public static class Family_506F696E74", GeneratedSource(appV2, "DurableGenericStates.g.cs"));
        GeneratorTestRun ruleOnly = RunCrossAssemblyGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using P=Atelia.DurableGraph.Generated.Family_506F696E74;
            [ValueUpgradeRuleSet] public sealed class Rules;
            public static class Converter {
                [DurableValueUpgrade(typeof(Rules),"Point",1,2)]
                public static void Upgrade(in P.V1 prior,out P.V2 next,UpgradeContext context) => next=new(prior.Segment0Field1);
            }
            """, [referenceV2]);
        AssertSchemaOnlyCompiles(ruleOnly);
        Assert.Contains("references-sha256", GeneratedSource(ruleOnly, "DurableGraphSchemaHistoryCandidates.g.cs"));
        Assert.DoesNotContain("schema-begin", GeneratedSource(ruleOnly, "DurableGraphSchemaHistoryCandidates.g.cs"));
        Assert.DoesNotContain("public static class Family_", GeneratedSource(ruleOnly, "DurableGenericStates.g.cs"));
        File.WriteAllText(referencePath, GeneratedSource(ruleOnly, "DurableGraphSchemaHistoryReferences.g.cs"));
        string ruleManifest = Path.Combine(appHistory.History, "../rules-candidates.g.cs");
        File.WriteAllText(ruleManifest, GeneratedSource(ruleOnly, "DurableGraphSchemaHistoryCandidates.g.cs"));
        Assert.Contains("published 0", tool.Publish(ruleManifest, Path.Combine(appHistory.History, "../rules-history"), referencePath).Message);
    }

    [Fact]
    public void CrossAssemblyInlineLocalAndExternalLayoutsPreserveOwnerHistoryDtoArityAndBodyBytes() {
        const string point = "[DurableType(\"Point\",1)] public partial struct Point { [DurableField(1)] public int X; }";
        GeneratorTestRun library = RunCrossAssemblyGenerator("using Atelia.DurableGraph; " + point +
            "public static class PointCatalog { public static void Register(IStateModelRegistration models)=>Atelia.DurableGraph.Generated.DurableDefinitions.Register(models); }", forceDefinitions: "true");
        var reference = EmitCrossAssemblyReference(library).Reference;
        string localSource = CrossAssemblyPlainSource + CrossAssemblyBodyHost(true);
        string externalSource = CrossAssemblyPlainSource.Replace(point, "") + CrossAssemblyBodyHost(true).Replace(
            "=> Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);",
            "{ PointCatalog.Register(models); Atelia.DurableGraph.Generated.DurableDefinitions.Register(models); }");
        GeneratorTestRun local = RunCrossAssemblyGenerator(localSource, forceDefinitions: "true");
        GeneratorTestRun external = RunCrossAssemblyGenerator(externalSource, [reference]);
        AssertSchemaOnlyCompiles(local);
        AssertSchemaOnlyCompiles(external);
        using AncestryHistoryDirectory localHistory = new();
        using AncestryHistoryDirectory externalHistory = new();
        SchemaHistoryTool tool = new();
        tool.Publish(localHistory.WriteManifest(local), localHistory.History);
        string refs = Path.Combine(externalHistory.History, "../references.g.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(refs)!);
        File.WriteAllText(refs, GeneratedSource(external, "DurableGraphSchemaHistoryReferences.g.cs"));
        tool.Publish(externalHistory.WriteManifest(external), externalHistory.History, refs);
        string externalRecord = Assert.Single(Directory.GetFiles(externalHistory.History, "*.dgschema"));
        Assert.Equal(File.ReadAllBytes(Path.Combine(localHistory.History, Path.GetFileName(externalRecord))), File.ReadAllBytes(externalRecord));
        using var loaded = new CrossAssemblyLoadScope(library, local, external);
        DurableSchema? priorSchema = null;
        foreach (GeneratorTestRun run in new[] { local, external }) {
            var assembly = loaded.Load(run);
            Type host = assembly.GetType("BodyHost")!;
            var models = new Persistence.StateModelRegistry();
            host.GetMethod("Register")!.Invoke(null, [models]);
            DurableSchema schema = models.CreateEnvironment().ResolveCurrentModel(assembly.GetType("Item")!).CurrentSchema;
            if (priorSchema is not null) Assert.Equal(priorSchema, schema);
            priorSchema = schema;
            Assert.Empty(assembly.GetType("Atelia.DurableGraph.Generated.Family_4974656D+V1")!.GetGenericArguments());
            var write = host.GetMethod("Base")!.CreateDelegate<Func<byte[], DurableSchema, byte[]>>();
            var delta = host.GetMethod("Delta")!.CreateDelegate<Func<byte[], byte[], DurableSchema, Serialization.PreparedDeltaBody>>();
            byte[] before = [2, 7, 9, 6];
            byte[] after = [4, 8, 9, 10];
            Assert.Equal(before, write(before, schema));
            Assert.Equal(after, write(after, schema));
            Assert.Equal(new byte[] { 11, 4, 8, 1, 10 }, delta(before, after, schema).Body.ToArray());
            Assert.False(delta(after, after, schema).HasChanges);
        }
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("arity")]
    [InlineData("version")]
    [InlineData("truncated")]
    [InlineData("duplicate")]
    [InlineData("cycle")]
    public void CrossAssemblyInlineRejectsInvalidSelectedTemplateMaterial(string defect) {
        string manifest = defect switch {
            "kind" => InlinePointManifest.Replace("// kind:2", "// kind:1"),
            "arity" => InlinePointManifest.Replace("// arity:0", "// arity:1"),
            "version" => InlinePointManifest.Replace("// version:1", "// version:2"),
            "truncated" => InlinePointManifest[..^1],
            _ => InlinePointManifest,
        };
        // Use the serializer's canonical pattern expression rather than inventing a wire syntax.
        if (defect == "cycle") {
            var example = RunCrossAssemblyGenerator("using Atelia.DurableGraph; [DurableType(\"Point\",1)] public partial struct Point { [DurableField(1)] public int X; } [DurableType(\"Holder\",1)] public partial struct Holder { [DurableField(1)] public Point Value; }", forceDefinitions: "true");
            string line = GeneratedSource(example, "DurableGraphSchemaHistoryCandidates.g.cs").Split('\n').Single(value => value.StartsWith("// field:1|16|"));
            manifest = InlinePointManifest.Replace("// field:1|2", line);
        }
        string attribute = "[assembly:Atelia.DurableGraph.DurableSchemaExport(1,\"Point\",1," + SymbolDisplay.FormatLiteral(manifest, true) + ")]";
        string source = attribute + (defect == "duplicate" ? attribute : "") +
            "[Atelia.DurableGraph.DurableType(\"Point\",1)] public struct Point { public int X; }";
        MetadataReference library = EmitCrossAssemblyRawReference(source);
        GeneratorTestRun app = RunCrossAssemblyGenerator("using Atelia.DurableGraph; [DurableType(\"World\",1)] public partial class World:IDurableObject { [DurableField(1)] public Point Value; }", [library]);
        Assert.Contains(app.GeneratorDiagnostics, d => d.Id == (defect == "cycle" ? "DG0019" : "DG0023"));
        Assert.DoesNotContain(app.GeneratorDiagnostics, d => d.Id == "CS8785");
    }

    private const string InlinePointManifest="// durable-graph-schema-history-manifest:9\n// schema-begin\n// schema-id-base64:UG9pbnQ=\n// version:1\n// kind:2\n// arity:0\n// field:1|2\n// schema-end\n";
}
