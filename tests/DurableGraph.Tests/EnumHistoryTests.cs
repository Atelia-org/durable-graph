using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Reflection;
using Atelia.DurableGraph.Build;
using Atelia.DurableGraph.Persistence;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void EnumConstantTableAndFlagsDoNotChangeAcceptedRepresentationHistory() {
        using AncestryHistoryDirectory history = new();
        const string source = """
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("Mode",1)] public enum Mode:byte { First=1, Second=2 }
            [DurableType("World",1)] public partial class World:IDurableObject { [DurableField(1)] public Mode Value; }
            """;
        GeneratorTestRun first = RunGenerator(source);
        AssertSchemaOnlyCompiles(first);
        SchemaHistoryTool tool = new();
        tool.Publish(history.WriteManifest(first), history.History);
        Dictionary<string, string> original = history.ReadContents();

        GeneratorTestRun changed = RunGenerator(source.Replace(
            "[DurableType(\"Mode\",1)] public enum Mode:byte { First=1, Second=2 }",
            "[System.Flags,DurableType(\"Mode\",1)] public enum Mode:byte { Renamed=1, Alias=1, Second=32, Added=128 }"),
            history.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(changed);
        Assert.Equal(GeneratedSource(first, "DurableGraphSchemaHistoryCandidates.g.cs"),
            GeneratedSource(changed, "DurableGraphSchemaHistoryCandidates.g.cs"));
        tool.Publish(history.WriteManifest(changed), history.History);
        tool.Verify(history.WriteManifest(changed), history.History);
        Assert.Equal(original.Count, history.ReadContents().Count);
        foreach ((string name, string content) in original) {
            Assert.Equal(content, File.ReadAllText(Path.Combine(history.History, name)));
        }
    }

    [Theory]
    [InlineData("sbyte")]
    [InlineData("ushort")]
    public void EnumSameVersionUnderlyingTypeChangeRejectsHistory(string underlying) {
        using AncestryHistoryDirectory history = new();
        const string source = """
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("Mode",1)] public enum Mode:byte { Zero=0 }
            [DurableType("World",1)] public partial class World:IDurableObject { [DurableField(1)] public Mode Value; }
            """;
        GeneratorTestRun first = RunGenerator(source);
        AssertSchemaOnlyCompiles(first);
        SchemaHistoryTool tool = new();
        tool.Publish(history.WriteManifest(first), history.History);
        GeneratorTestRun changed = RunGenerator(source.Replace("Mode:byte", "Mode:" + underlying), history.ReadAdditionalTexts());
        Assert.Contains(changed.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0015");
        GeneratorTestRun candidate = RunGenerator(source.Replace("Mode:byte", "Mode:" + underlying));
        AssertSchemaOnlyCompiles(candidate);
        Assert.Throws<SchemaHistoryException>(() => tool.Publish(history.WriteManifest(candidate), history.History));
        Assert.Equal(2, history.ReadContents().Count);
    }

    [Fact]
    public void EnumVersionChangePropagatesThroughInlineAndBaseEvenWithIdenticalIntegerLayout() {
        using AncestryHistoryDirectory history = new();
        const string source = """
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("Mode",1)] public enum Mode:byte { Zero=0 }
            [DurableType("Value",1)] public partial struct Value { [DurableField(1)] public Mode Mode; }
            [DurableType("Parent",1)] public partial class Parent:IDurableObject { [DurableField(1)] public Value Value; }
            [DurableType("World",1)] public partial class World:Parent { [DurableField(1)] public int Number; }
            """;
        GeneratorTestRun first = RunGenerator(source);
        AssertSchemaOnlyCompiles(first);
        new SchemaHistoryTool().Publish(history.WriteManifest(first), history.History);
        string bumped = source;
        foreach (string id in new[] { "Mode", "Value", "Parent" }) {
            bumped = bumped.Replace($"\"{id}\",1", $"\"{id}\",2");
            GeneratorTestRun missingOwnerBump = RunGenerator(bumped, history.ReadAdditionalTexts());
            Assert.Contains(missingOwnerBump.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0015");
        }
        GeneratorTestRun accepted = RunGenerator(bumped.Replace("\"World\",1", "\"World\",2"), history.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(accepted);
        Assembly assembly = EmitAndLoad(accepted.OutputCompilation);
        StateModelRegistry registry = EnumHistoryRegistry(assembly);
        DurableSchema schema = registry.CreateEnvironment().ResolveCurrentModel(assembly.GetType("World")!).CurrentSchema;
        Assert.Equal(2, schema.BaseSchema!.Version);
        DurableSchema value = Assert.Single(schema.BaseSchema.Fields).InlineSchema!;
        Assert.Equal(2, value.Version);
        DurableSchema mode = Assert.Single(value.Fields).InlineSchema!;
        Assert.Equal(2, mode.Version);
        Assert.Equal(TypeTag.Byte, Assert.Single(mode.Fields).TypeTag);
    }

    [Fact]
    public void EnumElementVersionDoesNotPropagateAcrossArrayOrListReference() {
        using AncestryHistoryDirectory history = new();
        const string source = """
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using System.Collections.Generic;
            [DurableType("Mode",1)] public enum Mode:byte { Zero=0 }
            [DurableType("World",1)] public partial class World:IDurableObject {
                [DurableField(1)] public Mode[] Array;
                [DurableField(2)] public List<Mode?> List;
            }
            """;
        GeneratorTestRun first = RunGenerator(source);
        AssertSchemaOnlyCompiles(first);
        new SchemaHistoryTool().Publish(history.WriteManifest(first), history.History);
        GeneratorTestRun next = RunGenerator(source.Replace("\"Mode\",1", "\"Mode\",2"), history.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(next);
        Assembly assembly = EmitAndLoad(next.OutputCompilation);
        StateBindingContext snapshot = EnumHistoryRegistry(assembly).CreateEnvironment();
        Assert.Equal(1, snapshot.ResolveCurrentModel(assembly.GetType("World")!).CurrentSchema.Version);
        Assert.True(snapshot.TryGetCurrentObjectBinding(assembly.GetType("Mode")!.MakeArrayType(), out ObjectBinding? array));
        Assert.Equal(2, Assert.IsAssignableFrom<ArrayObjectBinding>(array).ArrayLayout.ElementSlot.InlineSchema!.Version);
    }

    [Fact]
    public void DeletedEnumRetainsFamilyAndExactReaderWithoutAnyCurrentCompositeTrigger() {
        using AncestryHistoryDirectory history = new();
        GeneratorTestRun first = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("Mode",1)] public enum OldMode:byte { Named=1 }
            [DurableType("World",1)] public partial class World:IDurableObject { [DurableField(1)] public OldMode Value; }
            """);
        AssertSchemaOnlyCompiles(first);
        new SchemaHistoryTool().Publish(history.WriteManifest(first), history.History);
        GeneratorTestRun next = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("World",2)] public partial class World:IDurableObject { [DurableField(1)] public int Value; }
            """, history.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(next);
        Assert.Contains(next.GeneratedSources, source => source.HintName == "DurableGenericStates.g.cs");
        Assembly assembly = EmitAndLoad(next.OutputCompilation);
        Assert.Null(assembly.GetType("OldMode"));
        DurableSchema mode = new("Mode", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Byte));
        DurableSchema world = new("World", 1, new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: mode));
        StateReaderBinding reader = EnumHistoryRegistry(assembly).CreateEnvironment().ResolveReader(world);
        // 255 was never a declared enum constant; retained reading uses only the old byte layout.
        ObjectStateRecord decoded = reader.Read(new(1), new StateModelBodySource([255]));
        object value = StateModelField(decoded, "Segment0Field1")!;
        Assert.Equal((byte)255, value.GetType().GetField("Segment0Field1")!.GetValue(value));
    }

    private static StateModelRegistry EnumHistoryRegistry(Assembly assembly) {
        StateModelRegistry registry = new();
        assembly.GetType("Atelia.DurableGraph.Generated.DurableDefinitions")!.GetMethod("Register")!
            .CreateDelegate<Action<IStateDefinitionRegistration>>()(registry);
        return registry;
    }
}
