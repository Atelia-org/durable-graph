using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Collections.Immutable;
using System.Reflection;
using Atelia.DurableGraph.Build;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    public void CrossAssemblyDefinitionsOptionSelectsExistingGenerationPath(string? option, bool family) {
        GeneratorTestRun run = RunCrossAssemblyGenerator(CrossAssemblyPlainSource, forceDefinitions: option);
        AssertSchemaOnlyCompiles(run);
        Assert.Equal(family, run.GeneratedSources.Any(item => item.HintName == "DurableGenericStates.g.cs"));
        Assert.Equal(!family, run.GeneratedSources.Any(item => item.HintName == "DurableStates.g.cs"));
        if (family) {
            string output = GeneratedSource(run, "DurableGenericStates.g.cs");
            Assert.Contains("internal static class DurableDefinitions", output);
            Assert.Contains("public static class Family_", output);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("true")]
    public void CrossAssemblyDefinitionsFalseDoesNotDisableAutomaticFamily(string? option) {
        GeneratorTestRun run = RunCrossAssemblyGenerator("using Atelia.DurableGraph; [DurableType(\"Generic\",1)] public partial struct Generic<T> { [DurableField(1)] public T Value; }", forceDefinitions: option);
        AssertSchemaOnlyCompiles(run);
        Assert.Contains(run.GeneratedSources, item => item.HintName == "DurableGenericStates.g.cs");
    }

    [Theory]
    [InlineData("Auto")]
    [InlineData("1")]
    [InlineData("tru")]
    public void CrossAssemblyDefinitionsRejectsInvalidOption(string option) {
        GeneratorTestRun run = RunCrossAssemblyGenerator(CrossAssemblyPlainSource, forceDefinitions: option);
        Diagnostic error = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("DG0022", error.Id);
        Assert.Contains("DurableGraphGenerateDefinitions", error.GetMessage());
        Assert.Empty(run.GeneratedSources);
    }

    [Fact]
    public void CrossAssemblyDefinitionsPreservesSchemaHistoryAndBodyBytes() {
        GeneratorTestRun ordinary = RunCrossAssemblyGenerator(CrossAssemblyPlainSource + CrossAssemblyBodyHost(false));
        GeneratorTestRun family = RunCrossAssemblyGenerator(CrossAssemblyPlainSource + CrossAssemblyBodyHost(true), forceDefinitions: "true");
        AssertSchemaOnlyCompiles(ordinary);
        AssertSchemaOnlyCompiles(family);
        Assert.Equal(GeneratedSource(ordinary, "DurableGraphSchemaHistoryCandidates.g.cs"),
            GeneratedSource(family, "DurableGraphSchemaHistoryCandidates.g.cs"));
        using AncestryHistoryDirectory history = new();
        SchemaHistoryTool tool = new();
        tool.Publish(history.WriteManifest(ordinary), history.History);
        var accepted = Directory.GetFiles(history.History, "*.dgschema").ToDictionary(file => Path.GetFileName(file)!, File.ReadAllBytes);
        Assert.Equal("published 0 schema-history record(s); 2 already exact", tool.Publish(history.WriteManifest(family), history.History).Message);
        tool.Verify(history.WriteManifest(family), history.History);
        foreach (string file in Directory.GetFiles(history.History, "*.dgschema")) Assert.Equal(accepted[Path.GetFileName(file)], File.ReadAllBytes(file));
        AssertSchemaOnlyCompiles(RunCrossAssemblyGenerator(CrossAssemblyPlainSource, history: history.ReadAdditionalTexts(), forceDefinitions: "true"));

        Assembly oldAssembly = EmitAndLoad(ordinary.OutputCompilation);
        Assembly newAssembly = EmitAndLoad(family.OutputCompilation);
        StateModelBinding Bind(Assembly assembly) {
            var models = new StateModelRegistry();
            assembly.GetType("BodyHost")!.GetMethod("Register")!.Invoke(null, [models]);
            return models.Snapshot().ResolveCurrentModel(assembly.GetType("Item")!);
        }
        DurableSchema oldSchema = Bind(oldAssembly).CurrentSchema;
        DurableSchema newSchema = Bind(newAssembly).CurrentSchema;
        Assert.Equal(oldSchema, newSchema);
        // Int32 1, string ObjectId 7, durable ObjectId 9, nested Point.X 3.
        byte[] prior = [2, 7, 9, 6];
        byte[] current = [4, 8, 9, 6];
        foreach (Assembly assembly in new[] { oldAssembly, newAssembly }) {
            Type host = assembly.GetType("BodyHost")!;
            var baseBody = host.GetMethod("Base")!.CreateDelegate<Func<byte[], DurableSchema, byte[]>>();
            var delta = host.GetMethod("Delta")!.CreateDelegate<Func<byte[], byte[], DurableSchema, PreparedDeltaBody>>();
            Assert.Equal(prior, baseBody(prior, oldSchema));
            Assert.Equal(current, baseBody(current, oldSchema));
            PreparedDeltaBody changed = delta(prior, current, oldSchema);
            Assert.True(changed.HasChanges);
            Assert.Equal(new byte[] { 3, 4, 8 }, changed.Body.ToArray());
            PreparedDeltaBody nested = delta(current, [4, 8, 9, 10], oldSchema);
            Assert.True(nested.HasChanges);
            Assert.Equal(new byte[] { 8, 1, 10 }, nested.Body.ToArray());
            PreparedDeltaBody unchanged = delta(current, current, oldSchema);
            Assert.False(unchanged.HasChanges);
            Assert.Equal(new byte[] { 0 }, unchanged.Body.ToArray());
        }
    }

    [Fact]
    public void CrossAssemblyMetadataSupportsNominalAndDynamicTypeCombinationsWithoutImportingDefinitions() {
        var library = EmitCrossAssemblyReference(RunCrossAssemblyGenerator(CrossAssemblyRemoteSource, forceDefinitions: "true"));
        GeneratorTestRun app = RunCrossAssemblyGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using System.Collections.Generic;
            [DurableType("LocalPoint",1)] public partial struct LocalPoint { [DurableField(1)] public int X; }
            [DurableType("LocalInline",1)] public partial struct LocalInline<T> { [DurableField(1)] public T Value; }
            [DurableType("Phantom",1)] public partial struct Phantom<T> { [DurableField(1)] public int Value; }
            [DurableType("LocalBox",1)] public partial class LocalBox<T>:IDurableObject { [DurableField(1)] public T Value; }
            [DurableType("World",1)] public partial class World:IDurableObject {
                [DurableField(1)] public Remote.Node? Node;
                [DurableField(2)] public Remote.Point[] Points=[];
                [DurableField(3)] public List<Remote.Point?> Values=new();
                [DurableField(4)] public Dictionary<Remote.Key,Remote.Node> Nodes=new();
                [DurableField(5)] public LocalBox<Remote.Point>? Box;
                [DurableField(6)] public Remote.Box<LocalPoint>? Reverse;
                [DurableField(7)] public LocalInline<Remote.Point> Inline;
                [DurableField(8)] public Phantom<Remote.Point> Phantom;
                [DurableField(9)] public Remote.Choice[,] Plane=new Remote.Choice[0,0];
                [DurableField(10)] public Remote.Key[,,] Cube=new Remote.Key[0,0,0];
                [DurableField(11)] public Remote.Point[,,,] Rank4=new Remote.Point[0,0,0,0];
            }
            public static class AppCatalog {
                public static void Register(IStateModelRegistration models) => Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
            }
            """, [library.Reference]);
        AssertSchemaOnlyCompiles(app);
        Assert.DoesNotContain(app.OutputCompilation.GetDiagnostics(), error => error.Id is "CS0433" or "CS0436");
        string manifest = GeneratedSource(app, "DurableGraphSchemaHistoryCandidates.g.cs");
        string generated = GeneratedSource(app, "DurableGenericStates.g.cs");
        foreach (string id in new[] { "RemotePoint", "RemoteNode", "RemoteBox", "RemoteKey", "RemoteChoice" }) {
            Assert.DoesNotContain("// schema-id-base64:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(id)), manifest);
            Assert.DoesNotContain("public static class Family_" + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(id)), generated);
        }
    }

    [Theory]
    [InlineData("int", "Remote.Node")]
    [InlineData("int", "Remote.Box<int>")]
    public void CrossAssemblyMetadataSupportsExternalBase(string field, string parent) {
        var library = EmitCrossAssemblyReference(RunCrossAssemblyGenerator(CrossAssemblyRemoteSource, forceDefinitions: "true"));
        GeneratorTestRun run = RunCrossAssemblyGenerator($$"""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("World",1)] public partial class World:{{parent}} { [DurableField(1)] public {{field}} OwnValue; }
            """, [library.Reference]);
        AssertSchemaOnlyCompiles(run);
        Assert.DoesNotContain(run.GeneratorDiagnostics, error => error.Id == "CS8785");
    }

    [Fact]
    public void CrossAssemblyMetadataSupportsFixedExternalValueNestedInsideLocalStruct() {
        var library = EmitCrossAssemblyReference(RunCrossAssemblyGenerator(CrossAssemblyRemoteSource, forceDefinitions: "true"));
        GeneratorTestRun run = RunCrossAssemblyGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("Inner",1)] public partial struct Inner { [DurableField(1)] public Remote.Point Point; }
            [DurableType("World",1)] public partial class World:IDurableObject { [DurableField(1)] public Inner Value; }
            """, [library.Reference]);
        AssertSchemaOnlyCompiles(run);
        Assert.DoesNotContain(run.GeneratorDiagnostics, error => error.Id == "CS8785");
    }

    [Theory]
    [InlineData("public class Bad {}")]
    [InlineData("[DurableType(\"Bad\",0)] public class Bad:IDurableObject {}")]
    [InlineData("[DurableType(\" \",1)] public struct Bad {}")]
    [InlineData("[DurableType(\"Bad\",1)] public class Bad {}")]
    [InlineData("[DurableType(\"Bad\",1)] public record Bad;")]
    [InlineData("[DurableType(\"Bad\",1)] public ref struct Bad {}")]
    public void CrossAssemblyMetadataRejectsUnsupportedAndInvalidExternalNominalShapes(string declaration) {
        MetadataReference reference = EmitCrossAssemblyRawReference("using Atelia.DurableGraph; namespace Remote { " + declaration + " }");
        GeneratorTestRun run = RunCrossAssemblyGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("World",1)] public partial class World:IDurableObject { [DurableField(1)] public Remote.Bad[] Values=[]; }
            """, [reference]);
        Assert.Contains(run.GeneratorDiagnostics, error => error.Id == "DG0007");
        Assert.DoesNotContain(run.GeneratorDiagnostics, error => error.Id == "CS8785");
    }

    [Theory]
    [InlineData("[DurableType(\"Bad\",1)] internal struct Bad {}", "Remote.Bad")]
    [InlineData("public class Outer { [DurableType(\"Bad\",1)] public struct Bad {} }", "Remote.Outer.Bad")]
    [InlineData("[DurableType(\"Bad\",1)] public class Bad<T>:IDurableObject where T:allows ref struct {}", "Remote.Bad<int>")]
    public void CrossAssemblyMetadataRejectsAccessibilityNestingAndRefLikeParameters(string declaration, string fieldType) {
        MetadataReference reference = EmitCrossAssemblyRawReference("using Atelia.DurableGraph; namespace Remote { " + declaration + " }");
        GeneratorTestRun run = RunCrossAssemblyGenerator($$"""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("World",1)] public partial class World:IDurableObject { [DurableField(1)] public {{fieldType}}[] Values=[]; }
            """, [reference]);
        Assert.Contains(run.GeneratorDiagnostics, error => error.Id == "DG0007");
        Assert.DoesNotContain(run.GeneratorDiagnostics, error => error.Id == "CS8785");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CrossAssemblyMetadataRejectsCounterfeitFrameworkSymbols(bool fakeAttribute) {
        string fake = fakeAttribute ? """
            namespace Atelia.DurableGraph {
                public class DurableTypeAttribute:System.Attribute { public DurableTypeAttribute(string id,int version) {} }
            }
            namespace Remote { [Atelia.DurableGraph.DurableType("Bad",1)] public struct Bad {} }
            """ : """
            namespace Atelia.DurableGraph { public interface IDurableObject {} }
            namespace Remote { [Atelia.DurableGraph.DurableType("Bad",1)] public class Bad:Atelia.DurableGraph.IDurableObject {} }
            """;
        MetadataReference reference = EmitCrossAssemblyRawReference(fake).WithAliases(ImmutableArray.Create("foreign"));
        GeneratorTestRun app = RunCrossAssemblyGenerator("""
            extern alias foreign;
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("World",1)] public partial class World:IDurableObject { [DurableField(1)] public foreign::Remote.Bad[] Values=[]; }
            """, [reference]);
        Assert.Contains(app.GeneratorDiagnostics, error => error.Id == "DG0007");
        Assert.DoesNotContain(app.GeneratorDiagnostics, error => error.Id == "CS8785");
    }

    private static MetadataReference EmitCrossAssemblyRawReference(string source) {
        CSharpCompilation compilation = CSharpCompilation.Create("RawMetadata_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source, ParseOptions)],
            PlatformReferences().Append(MetadataReference.CreateFromFile(typeof(IDurableObject).Assembly.Location)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using MemoryStream stream = new();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return MetadataReference.CreateFromImage(ImmutableArray.CreateRange(stream.ToArray()));
    }

    private const string CrossAssemblyPlainSource = """
        using System;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Serialization;
        [DurableType("Point",1)] public partial struct Point { [DurableField(1)] public int X; }
        [DurableType("Item",1)] public partial class Item:IDurableObject {
            [DurableField(1)] public int Count;
            [DurableField(2)] public string? Text;
            [DurableField(3)] public Item? Other;
            [DurableField(4)] public Point Position;
        }
        """;

    private static string CrossAssemblyBodyHost(bool family) => $$"""
        public static class BodyHost {
            public static void Register(IStateModelRegistration models) => {{(family ? "Atelia.DurableGraph.Generated.DurableDefinitions.Register(models)" : "models.Register(Item.__DurableState.Model)")}};
            public static byte[] Base(byte[] bytes, DurableSchema schema) {
                var reader=new BareValueReader(bytes);
                var state={{(family ? "Atelia.DurableGraph.Generated.Family_4974656D.BodyV1.Read(ref reader,schema)" : "Item.__DurableState.ReadBaseBodyV1(ref reader)")}};
                reader.EnsureFullyConsumed();
                return {{(family ? "Atelia.DurableGraph.Generated.Family_4974656D.BodyV1.PrepareBase(in state,schema)" : "Item.__DurableState.PrepareBaseBody(in state)")}}.Body.ToArray();
            }
            public static PreparedDeltaBody Delta(byte[] prior,byte[] current,DurableSchema schema) {
                var p=new BareValueReader(prior);var c=new BareValueReader(current);
                var a={{(family ? "Atelia.DurableGraph.Generated.Family_4974656D.BodyV1.Read(ref p,schema)" : "Item.__DurableState.ReadBaseBodyV1(ref p)")}};
                var b={{(family ? "Atelia.DurableGraph.Generated.Family_4974656D.BodyV1.Read(ref c,schema)" : "Item.__DurableState.ReadBaseBodyV1(ref c)")}};
                return {{(family ? "Atelia.DurableGraph.Generated.Family_4974656D.BodyV1.PrepareDelta(in a,in b,schema)" : "Item.__DurableState.PrepareDeltaBody(in a,in b)")}};
            }
        }
        """;

    private const string CrossAssemblyRemoteSource = """
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        namespace Remote;
        [DurableType("RemotePoint",1)] public partial struct Point { [DurableField(1)] public int X; }
        [DurableType("RemoteKey",1)] public readonly partial record struct Key([field:DurableField(1)] int X);
        [DurableType("RemoteChoice",1)] public enum Choice { A,B }
        [DurableType("RemoteNode",1)] public partial class Node:IDurableObject { [DurableField(1)] public int X; }
        [DurableType("RemoteBox",1)] public partial class Box<T>:IDurableObject { [DurableField(1)] public T Value; }
        [DurableType("RemoteInline",1)] public partial struct Inline<T> { [DurableField(1)] public T Value; }
        public static class Catalog { public static void Register(IStateModelRegistration models) => Atelia.DurableGraph.Generated.DurableDefinitions.Register(models); }
        """;
}
