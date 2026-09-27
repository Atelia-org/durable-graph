using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Build;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Storage;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordClassImplicitOwnerUpgradeCompilesItsRecordAdapterAndRuns(bool withContext) {
        using AncestryHistoryDirectory history = new();
        var tool = new SchemaHistoryTool();
        GeneratorTestRun first = RunGenerator(RecordClassImplicitUpgradeSource(1, withContext));
        AssertSchemaOnlyCompiles(first);
        tool.Publish(history.WriteManifest(first), history.History);
        using RawBaseDirectory directory = new();
        EmitAndLoad(first.OutputCompilation).GetType("Host")!.GetMethod("Seed")!
            .CreateDelegate<Func<string, FrameAddress>>()(directory.Path);
        GeneratorTestRun current = RunGenerator(RecordClassImplicitUpgradeSource(2, withContext), history.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(current);
        tool.Publish(history.WriteManifest(current), history.History);
        tool.Verify(history.WriteManifest(current), history.History);
        var assembly = EmitAndLoad(current.OutputCompilation);
        FrameAddress[] addresses = assembly.GetType("Host")!.GetMethod("LoadAndSave")!
            .CreateDelegate<Func<string, FrameAddress[]>>()(directory.Path);
        Assert.Equal(1, assembly.GetType("R")!.GetField("Upgrades")!.GetValue(null));
        using SegmentStore segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory.Path, "state"));
        using StateRevisionStore states = new(segments);
        Assert.Equal(ObjectVersionKind.Base, Assert.Single(states.Read(addresses[0]).LocalObjects).Kind);
        Assert.Equal(ObjectVersionKind.Delta, Assert.Single(states.Read(addresses[1]).LocalObjects).Kind);
        Assert.Empty(states.Read(addresses[2]).LocalObjects);
    }

    [Fact]
    public void RecordClassConversionReusesClassAncestryHistoryAndPersistedObjectsWithoutUpgrade() {
        using AncestryHistoryDirectory history = new();
        SchemaHistoryTool tool = new();
        // Package builds opt into this catalog even before the model uses a generic or record declaration.
        GeneratorTestRun ordinary = RunCrossAssemblyGenerator(RecordClassHistorySource(false), forceDefinitions: "true");
        AssertSchemaOnlyCompiles(ordinary);
        tool.Publish(history.WriteManifest(ordinary), history.History);
        var originals = history.ReadContents();
        using RawBaseDirectory directory = new();
        var priorAssembly = EmitAndLoad(ordinary.OutputCompilation);
        FrameAddress original = priorAssembly.GetType("Host")!.GetMethod("Seed")!
            .CreateDelegate<Func<string, FrameAddress>>()(directory.Path);

        GeneratorTestRun record = RunGenerator(RecordClassHistorySource(true), history.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(record);
        Assert.Equal(GeneratedSource(ordinary, "DurableGraphSchemaHistoryCandidates.g.cs"),
            GeneratedSource(record, "DurableGraphSchemaHistoryCandidates.g.cs"));
        tool.Publish(history.WriteManifest(record), history.History);
        tool.Verify(history.WriteManifest(record), history.History);
        Assert.Equal(originals.Count, history.ReadContents().Count);
        foreach (var item in originals) Assert.Equal(item.Value, history.ReadContents()[item.Key]);

        var currentAssembly = EmitAndLoad(record.OutputCompilation);
        Type currentHost = currentAssembly.GetType("Host")!;
        FrameAddress[] saved = currentHost.GetMethod("LoadAndSave")!
            .CreateDelegate<Func<string, FrameAddress[]>>()(directory.Path);
        StateModelRegistry currentModels = currentHost.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>()();
        using SegmentStore segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory.Path, "state"));
        using StateRevisionStore states = new(segments);
        Assert.Empty(states.Read(saved[0]).LocalObjects); // Same layout needs neither Upgrade nor a replacement Base.
        ObjectVersionRecord changed = Assert.Single(states.Read(saved[1]).LocalObjects);
        Assert.Equal(ObjectVersionKind.Delta, changed.Kind);
        Assert.Equal(Assert.Single(states.Read(original).LocalObjects).ObjectId, changed.ObjectId);
        Assert.Empty(states.Read(saved[2]).LocalObjects);
        DurableSchema before = EnumHistoryRegistry(priorAssembly).Snapshot().ResolveCurrentModel(priorAssembly.GetType("Leaf")!).CurrentSchema;
        DurableSchema after = currentModels.Snapshot().ResolveCurrentModel(currentAssembly.GetType("Leaf")!).CurrentSchema;
        Assert.Equal(before, after);
        Assert.Null(after.BaseSchema!.BaseSchema);
    }

    [Fact]
    public void RecordClassBaseVersionChangeRequiresDerivedVersionEvenWhenSlotsStayTheSame() {
        using AncestryHistoryDirectory history = new();
        string source = RecordClassHistorySource(true);
        GeneratorTestRun first = RunGenerator(source);
        AssertSchemaOnlyCompiles(first);
        new SchemaHistoryTool().Publish(history.WriteManifest(first), history.History);
        GeneratorTestRun forgotBase = RunGenerator(source.Replace("int Number", "long Number"), history.ReadAdditionalTexts());
        Assert.Contains(forgotBase.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0015");
        GeneratorTestRun forgotLeaf = RunGenerator(source.Replace("\"Base\",1", "\"Base\",2"), history.ReadAdditionalTexts());
        Assert.Contains(forgotLeaf.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0015" && diagnostic.GetMessage().Contains("Leaf"));
        GeneratorTestRun advanced = RunGenerator(source.Replace("\"Base\",1", "\"Base\",2").Replace("\"Leaf\",1", "\"Leaf\",2"), history.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(advanced);
        var assembly = EmitAndLoad(advanced.OutputCompilation);
        DurableSchema current = EnumHistoryRegistry(assembly).Snapshot().ResolveCurrentModel(assembly.GetType("Leaf")!).CurrentSchema;
        Assert.Equal(2, current.Version);
        Assert.Equal(2, current.BaseSchema!.Version);
    }

    private static string RecordClassHistorySource(bool record) => """
        using System;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Generated;
        using Atelia.DurableGraph.Persistence;
        using Atelia.DurableGraph.Storage;
        """ + (record ? """
        [DurableType("Base",1)] public abstract partial record Base([field:DurableField(1)] int Number) : IDurableObject;
        [DurableType("Leaf",1)] public sealed partial record Leaf(int Number) : Base(Number) {
            [field:DurableField(1)] public long Amount { get; set; }
            [field:DurableField(2)] public long Padding { get; init; }
        }
        """ : """
        [DurableType("Base",1)] public abstract partial class Base : IDurableObject {
            [DurableField(1)] public int Number;
            protected Base(int number) { Number=number; }
        }
        [DurableType("Leaf",1)] public sealed partial class Leaf : Base {
            [DurableField(1)] public long Amount;
            [DurableField(2)] public long Padding;
            public Leaf(int number) : base(number) { }
        }
        """) + """
        public static class Host {
            public static StateModelRegistry Models() { var m=new StateModelRegistry();DurableDefinitions.Register(m);return m; }
            public static FrameAddress Seed(string path) {
                using var repo=FixtureGraphRepository.CreateNew(path, Models());
                using var session=repo.Create(new Leaf(17){Amount=18,Padding=long.MaxValue});
                return session.Commit(new(1000000,1));
            }
            public static FrameAddress[] LoadAndSave(string path) {
                FrameAddress first,second,third;
                using(var repo=FixtureGraphRepository.OpenExisting(path, Models()))using(var session=repo.Load<Leaf>()) {
                    if(session.World.Number!=17 || session.World.Amount!=18 || session.World.Padding!=long.MaxValue)throw new Exception("old class layout");
                    first=session.Commit(new(1000000,1));session.World.Amount=23;second=session.Commit(new(1000000,1));
                }
                using(var repo=FixtureGraphRepository.OpenExisting(path, Models()))using(var session=repo.Load<Leaf>()) {
                    if(session.World.Number!=17 || session.World.Amount!=23)throw new Exception("record Delta layout");
                    third=session.Commit(new(1000000,1));
                }
                return new[]{first,second,third};
            }
        }
        """;

    private static string RecordClassImplicitUpgradeSource(int version, bool withContext) => $$"""
        using System;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Generated;
        using Atelia.DurableGraph.Persistence;
        using Atelia.DurableGraph.Storage;
        using F=Atelia.DurableGraph.Generated.Family_52;
        [DurableType("R",{{version}})] public sealed partial record R([field:DurableField(1)] {{(version == 1 ? "int" : "long")}} Amount) : IDurableObject {
            public static int Upgrades;
            [DurableField(2)] public long Extra=long.MaxValue;
            [DurableField(3)] public long Padding=long.MaxValue;
            {{(version == 1 ? "" : "private static void UpgradeStateV1ToV2(in F.V1 prior,out F.V2 next" +
                (withContext ? ",UpgradeContext context" : "") + ") { Upgrades++;next=new(prior.Segment0Field1,prior.Segment0Field2,prior.Segment0Field3); }")}}
        }
        public static class Host {
            public static StateModelRegistry Models(){var m=new StateModelRegistry();DurableDefinitions.Register(m);return m;}
            public static FrameAddress Seed(string path) {
                using var repo=FixtureGraphRepository.CreateNew(path, Models());using var session=repo.Create(new R(17));
                return session.Commit(new(1000000,1));
            }
            public static FrameAddress[] LoadAndSave(string path) {
                FrameAddress first,second,third;
                using(var repo=FixtureGraphRepository.OpenExisting(path, Models()))using(var session=repo.Load<R>()) {
                    if(session.World.Amount!=17 || session.World.Extra!=long.MaxValue || R.Upgrades!=1)throw new Exception("record upgrade");
                    first=session.Commit(new(1000000,1));session.World.Extra=3;second=session.Commit(new(1000000,1));
                }
                using(var repo=FixtureGraphRepository.OpenExisting(path, Models()))using(var session=repo.Load<R>()) {
                    if(session.World.Amount!=17 || session.World.Extra!=3 || R.Upgrades!=1)throw new Exception("upgraded Delta");
                    third=session.Commit(new(1000000,1));
                }
                return new[]{first,second,third};
            }
        }
        """;
}
