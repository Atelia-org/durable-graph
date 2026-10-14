using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Build;
using Atelia.DurableGraph.Persistence;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordStructAndOrdinaryStructShareHistorySchemaAndBodyAcrossMemberRenames(bool recordFirst) {
        const string ordinary = """
            [DurableType("Point",1)] public partial struct OrdinaryPoint {
                [DurableField(1)] public int X;
                [DurableField(2)] public long Y;
            }
            """;
        const string record = """
            [DurableType("Point",1)] public readonly partial record struct RecordPoint(
                [field:DurableField(2)] long RenamedSecond,
                [field:DurableField(1)] int RenamedFirst);
            """;
        static string Source(string definition, bool isRecord) => """
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            // Keep both compilations on Family: the test concerns persistent layout, not old generated API names.
            [DurableType("FamilyTrigger",1)] public partial struct FamilyTrigger<T> { [DurableField(1)] public T Value; }
            """ + definition + "[DurableType(\"World\",1)] public partial class World:IDurableObject { " +
            (isRecord ? "[DurableField(1)] public RecordPoint Value=new(123456789L,17);" :
                "[DurableField(1)] public OrdinaryPoint Value=new(){X=17,Y=123456789L};") + " }";
        string before = Source(recordFirst ? record : ordinary, recordFirst);
        string after = Source(recordFirst ? ordinary : record, !recordFirst);
        using AncestryHistoryDirectory history = new();
        SchemaHistoryTool tool = new();
        GeneratorTestRun first = RunGenerator(before);
        AssertSchemaOnlyCompiles(first);
        tool.Publish(history.WriteManifest(first), history.History);
        Dictionary<string, string> originals = history.ReadContents();
        GeneratorTestRun next = RunGenerator(after, history.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(next);
        Assert.Equal(GeneratedSource(first, "DurableGraphSchemaHistoryCandidates.g.cs"),
            GeneratedSource(next, "DurableGraphSchemaHistoryCandidates.g.cs"));
        tool.Publish(history.WriteManifest(next), history.History);
        tool.Verify(history.WriteManifest(next), history.History);
        Assert.Equal(originals.Count, history.ReadContents().Count);
        foreach ((string file, string contents) in originals) {
            Assert.Equal(contents, File.ReadAllText(Path.Combine(history.History, file)));
        }
        ObjectStateRecord prior = CaptureRecordHistoryWorld(first);
        ObjectStateRecord current = CaptureRecordHistoryWorld(next);
        Assert.Equal(prior.Schema, current.Schema);
        Assert.Equal(prior.Preparation!.PrepareBase(prior).Body.ToArray(),
            current.Preparation!.PrepareBase(current).Body.ToArray());
        Assert.Equal(SchemaKind.InlineValue, Assert.Single(current.Schema!.Fields).InlineSchema!.Kind);
    }

    [Fact]
    public void RecordStructInlineVersionChangeRequiresEveryInlineOwnerToAdvance() {
        const string source = """
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("Leaf",1)] public readonly partial record struct Leaf([field:DurableField(1)] int Number);
            [DurableType("Envelope",1)] public readonly partial record struct Envelope([field:DurableField(1)] Leaf Value);
            [DurableType("World",1)] public partial class World:IDurableObject { [DurableField(1)] public Envelope Value; }
            """;
        using AncestryHistoryDirectory history = new();
        GeneratorTestRun first = RunGenerator(source);
        AssertSchemaOnlyCompiles(first);
        new SchemaHistoryTool().Publish(history.WriteManifest(first), history.History);
        GeneratorTestRun changedField = RunGenerator(source.Replace("int Number", "long Number"), history.ReadAdditionalTexts());
        Assert.Contains(changedField.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0015");
        string bumped = source;
        foreach (string name in new[] { "Leaf", "Envelope" }) {
            bumped = bumped.Replace($"\"{name}\",1", $"\"{name}\",2");
            GeneratorTestRun missingOwner = RunGenerator(bumped, history.ReadAdditionalTexts());
            Assert.Contains(missingOwner.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0015");
        }
        AssertSchemaOnlyCompiles(RunGenerator(bumped.Replace("\"World\",1", "\"World\",2"), history.ReadAdditionalTexts()));
    }

    [Fact]
    public void DeletedRecordStructRetainsExactReaderWithoutCurrentDomainDefinition() {
        using AncestryHistoryDirectory history = new();
        GeneratorTestRun first = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("Point",1)] public readonly partial record struct OldPoint([field:DurableField(1)] int Number);
            [DurableType("World",1)] public partial class World:IDurableObject { [DurableField(1)] public OldPoint Value=new(17); }
            """);
        AssertSchemaOnlyCompiles(first);
        ObjectStateRecord old = CaptureRecordHistoryWorld(first);
        new SchemaHistoryTool().Publish(history.WriteManifest(first), history.History);
        GeneratorTestRun next = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("World",2)] public partial class World:IDurableObject { [DurableField(1)] public long Value; }
            """, history.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(next);
        var assembly = EmitAndLoad(next.OutputCompilation);
        Assert.Null(assembly.GetType("OldPoint"));
        StateReaderBinding reader = EnumHistoryRegistry(assembly).CreateEnvironment().ResolveReader(old.Schema!);
        ObjectStateRecord decoded = reader.Read(old.Id, new StateModelBodySource(old.Preparation!.PrepareBase(old).Body.ToArray()));
        object point = StateModelField(decoded, "Segment0Field1")!;
        Assert.Equal(17, point.GetType().GetField("Segment0Field1")!.GetValue(point));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyRecordDictionaryRequiresExplicitCapabilityForEitherChangedSlot(bool keyChanges) {
        const string source = """
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using System.Collections.Generic;
            [DurableType("Key",1)] public readonly partial record struct Key([field:DurableField(1)] int KeyNumber);
            [DurableType("Value",1)] public readonly partial record struct Payload([field:DurableField(1)] int ValueNumber);
            [DurableType("World",1)] public partial class World:IDurableObject {
                [DurableField(1)] public Dictionary<Key,Payload> Map=new();
            }
            """;
        using AncestryHistoryDirectory history = new();
        GeneratorTestRun first = RunGenerator(source);
        AssertSchemaOnlyCompiles(first);
        var firstAssembly = EmitAndLoad(first.OutputCompilation);
        ModelExecutionEnvironment originalModels = EnumHistoryRegistry(firstAssembly).CreateEnvironment();
        IDurableObject world = (IDurableObject)Activator.CreateInstance(firstAssembly.GetType("World")!)!;
        using CaptureContext capture = new CaptureSession().BeginCapture(originalModels);
        originalModels.ResolveCurrentModel(world.GetType()).AddRoot(capture, world);
        ObjectStateRecord stored = Assert.Single(capture.Seal().Objects, row => row.Kind == ObjectStateKind.Dictionary);
        Assert.Equal(0, ((IFrozenDictionaryState)stored.Content).Count);
        new SchemaHistoryTool().Publish(history.WriteManifest(first), history.History);

        string changed = keyChanges ? source.Replace("\"Key\",1", "\"Key\",2").Replace("int KeyNumber", "long KeyNumber") :
            source.Replace("\"Value\",1", "\"Value\",2").Replace("int ValueNumber", "long ValueNumber");
        GeneratorTestRun next = RunGenerator(changed, history.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(next);
        var currentAssembly = EmitAndLoad(next.OutputCompilation);
        ModelExecutionEnvironment current = EnumHistoryRegistry(currentAssembly).CreateEnvironment();
        Type mapType = currentAssembly.GetType("World")!.GetField("Map")!.FieldType;
        Assert.True(current.TryGetCurrentObjectBinding(mapType, out ObjectBinding? target));
        // Empty content does not excuse a missing record value Upgrade. World's reference Schema itself stayed v1.
        Assert.Equal(1, current.ResolveCurrentModel(currentAssembly.GetType("World")!).CurrentSchema.Version);
        Assert.Throws<InvalidDataException>(() => current.NormalizeDictionary(stored, Assert.IsAssignableFrom<DictionaryObjectBinding>(target)));
    }

    private static ObjectStateRecord CaptureRecordHistoryWorld(GeneratorTestRun run) {
        var assembly = EmitAndLoad(run.OutputCompilation);
        ModelExecutionEnvironment snapshot = EnumHistoryRegistry(assembly).CreateEnvironment();
        IDurableObject world = (IDurableObject)Activator.CreateInstance(assembly.GetType("World")!)!;
        CaptureSession session = new();
        using CaptureContext capture = session.BeginCapture(snapshot);
        snapshot.ResolveCurrentModel(world.GetType()).AddRoot(capture, world);
        return Assert.Single(capture.Seal().Objects);
    }
}
