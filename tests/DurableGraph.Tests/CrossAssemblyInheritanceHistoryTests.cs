using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void CrossAssemblyInheritanceHistoryDeletesOldBaseClrAndOnlyUpgradesActualLeaf() {
        using AncestryHistoryDirectory baseHistory = new(), middleHistory = new(), leafHistory = new();
        GeneratorTestRun bases1 = RunCrossAssemblyGenerator(InheritanceHistoryBases(1), forceDefinitions: "true");
        var baseImage1 = EmitCrossAssemblyReference(bases1);
        GeneratorTestRun middle1 = RunCrossAssemblyGenerator(InheritanceHistoryMiddle(1), [baseImage1.Reference]);
        var middleImage1 = EmitCrossAssemblyReference(middle1);
        GeneratorTestRun app1 = RunCrossAssemblyGenerator(InheritanceHistoryApp(1), [baseImage1.Reference, middleImage1.Reference]);
        AssertSchemaOnlyCompiles(app1);
        PublishCrossInlineHistory(baseHistory, bases1);
        PublishCrossInlineHistory(middleHistory, middle1);
        PublishCrossInlineHistory(leafHistory, app1);
        var acceptedBases = baseHistory.ReadContents();
        var acceptedMiddle = middleHistory.ReadContents();
        var acceptedLeaf = leafHistory.ReadContents();
        Assert.Equal(2, acceptedBases.Count);
        Assert.Single(acceptedMiddle);
        Assert.Single(acceptedLeaf);
        using RawBaseDirectory directory = new();
        FrameAddress original;
        DurableSchema exact;
        using (var scope = new CrossAssemblyLoadScope(bases1, middle1, app1)) {
            Type host = scope.Load(app1).GetType("InheritanceHistoryApp.Host")!;
            original = host.GetMethod("Seed")!.CreateDelegate<Func<string, FrameAddress>>()(directory.Path);
            StateModelRegistry models = host.GetMethod("Models")!.CreateDelegate<Func<bool, StateModelRegistry>>()(false);
            using SegmentStore segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory.Path, "state"));
            using var schemaFile = RbfFile.OpenReadOnlyExisting(Path.Combine(directory.Path, "schemas.rbf"));
            SchemaStore schemas = new(schemaFile, readOnly: true);
            exact = Assert.Single(RevisionDecoder.ReadSnapshot(new(segments), schemas, original, models.Snapshot(schemas)).Objects).Schema!;
        }
        GeneratorTestRun bases2 = RunCrossAssemblyGenerator(InheritanceHistoryBases(2), history: baseHistory.ReadAdditionalTexts(), forceDefinitions: "true");
        var baseImage2 = EmitCrossAssemblyReference(bases2);
        GeneratorTestRun middle2 = RunCrossAssemblyGenerator(InheritanceHistoryMiddle(2), [baseImage2.Reference], middleHistory.ReadAdditionalTexts());
        var middleImage2 = EmitCrossAssemblyReference(middle2);
        GeneratorTestRun app2 = RunCrossAssemblyGenerator(InheritanceHistoryApp(2), [baseImage2.Reference, middleImage2.Reference], leafHistory.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(app2);
        PublishCrossInlineHistory(baseHistory, bases2);
        PublishCrossInlineHistory(middleHistory, middle2);
        PublishCrossInlineHistory(leafHistory, app2);
        Assert.Equal(4, baseHistory.ReadContents().Count);
        Assert.Equal(2, middleHistory.ReadContents().Count);
        Assert.Equal(2, leafHistory.ReadContents().Count);
        foreach (var item in acceptedBases) Assert.Equal(item.Value, baseHistory.ReadContents()[item.Key]);
        foreach (var item in acceptedMiddle) Assert.Equal(item.Value, middleHistory.ReadContents()[item.Key]);
        foreach (var item in acceptedLeaf) Assert.Equal(item.Value, leafHistory.ReadContents()[item.Key]);
        using var current = new CrossAssemblyLoadScope(bases2, middle2, app2);
        Assert.Null(current.Load(bases2).GetType("InheritanceHistoryBases.LegacyBase"));
        Assert.Null(current.Load(bases2).GetType("InheritanceHistoryBases.LegacyHidden"));
        Type currentHost = current.Load(app2).GetType("InheritanceHistoryApp.Host")!;
        StateModelRegistry currentModels = currentHost.GetMethod("Models")!.CreateDelegate<Func<bool, StateModelRegistry>>()(false);
        using (SegmentStore segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory.Path, "state")))
        using (var schemaFile = RbfFile.OpenReadOnlyExisting(Path.Combine(directory.Path, "schemas.rbf"))) {
            SchemaStore schemas = new(schemaFile, readOnly: true);
            var stored = Assert.Single(RevisionDecoder.ReadSnapshot(new(segments), schemas, original, currentModels.Snapshot(schemas)).Objects);
            Assert.Equal(exact, stored.Schema);
            Assert.Equal(1, stored.Schema!.Version);
            Assert.Equal(1, stored.Schema.BaseSchema!.Version);
            Assert.Equal(1, stored.Schema.BaseSchema.BaseSchema!.Version);
            Assert.Equal(1, stored.Schema.BaseSchema.BaseSchema.Fields[0].InlineSchema!.Version);
            Assert.Equal(0, currentHost.GetMethod("Callbacks")!.CreateDelegate<Func<int>>()());
        }
        FrameAddress[] addresses = currentHost.GetMethod("UpgradeAndSave")!.CreateDelegate<Func<string, FrameAddress[]>>()(directory.Path);
        using SegmentStore finalSegments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory.Path, "state"));
        using StateRevisionStore states = new(finalSegments);
        Assert.Equal(ObjectVersionKind.Base, Assert.Single(states.Read(addresses[0]).LocalObjects).Kind);
        foreach (int index in new[] { 1, 3 }) Assert.Empty(states.Read(addresses[index]).LocalObjects);
        ObjectVersionRecord delta = Assert.Single(states.Read(addresses[2]).LocalObjects);
        Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
        Assert.Equal(new byte[] { 0x10, 0xC6, 0x01 }, delta.Body.ToArray());
    }

    [Fact]
    public void CrossAssemblyInheritanceMissingLeafUpgradeOrBaseDefinitionDoesNotPublishOrRunBaseRules() {
        using AncestryHistoryDirectory baseHistory = new(), middleHistory = new(), leafHistory = new();
        GeneratorTestRun bases1 = RunCrossAssemblyGenerator(InheritanceHistoryBases(1), forceDefinitions: "true");
        var baseImage1 = EmitCrossAssemblyReference(bases1);
        GeneratorTestRun middle1 = RunCrossAssemblyGenerator(InheritanceHistoryMiddle(1), [baseImage1.Reference]);
        var middleImage1 = EmitCrossAssemblyReference(middle1);
        GeneratorTestRun app1 = RunCrossAssemblyGenerator(InheritanceHistoryApp(1), [baseImage1.Reference, middleImage1.Reference]);
        PublishCrossInlineHistory(baseHistory, bases1);
        PublishCrossInlineHistory(middleHistory, middle1);
        PublishCrossInlineHistory(leafHistory, app1);
        using RawBaseDirectory directory = new();
        FrameAddress original;
        using (var scope = new CrossAssemblyLoadScope(bases1, middle1, app1)) {
            original = scope.Load(app1).GetType("InheritanceHistoryApp.Host")!.GetMethod("Seed")!
                .CreateDelegate<Func<string, FrameAddress>>()(directory.Path);
        }
        GeneratorTestRun bases2 = RunCrossAssemblyGenerator(InheritanceHistoryBases(2), history: baseHistory.ReadAdditionalTexts(), forceDefinitions: "true");
        var baseImage2 = EmitCrossAssemblyReference(bases2);
        GeneratorTestRun middle2 = RunCrossAssemblyGenerator(InheritanceHistoryMiddle(2), [baseImage2.Reference], middleHistory.ReadAdditionalTexts());
        var middleImage2 = EmitCrossAssemblyReference(middle2);
        foreach (bool includeLeafUpgrade in new[] { false, true }) {
            GeneratorTestRun app = RunCrossAssemblyGenerator(InheritanceHistoryApp(2, includeLeafUpgrade), [baseImage2.Reference, middleImage2.Reference], leafHistory.ReadAdditionalTexts());
            AssertSchemaOnlyCompiles(app);
            using var scope = new CrossAssemblyLoadScope(bases2, middle2, app);
            Type host = scope.Load(app).GetType("InheritanceHistoryApp.Host")!;
            // Missing upgrade with all definitions, or all upgrades with a missing base catalog.
            Assert.True(host.GetMethod("LoadFails")!.CreateDelegate<Func<string, bool, bool>>()(directory.Path, includeLeafUpgrade));
        }
        GeneratorTestRun forgotLeaf = RunCrossAssemblyGenerator(InheritanceHistoryApp(1), [baseImage2.Reference, middleImage2.Reference], leafHistory.ReadAdditionalTexts());
        Assert.Contains(forgotLeaf.GeneratorDiagnostics, diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        using var repository = FixtureGraphRepository.OpenExisting(directory.Path, new StateModelRegistry());
        Assert.Equal(original, repository.HeadRevisionAddress);
        Assert.Single(leafHistory.ReadContents());
    }

    private static string InheritanceHistoryBases(int version) => $$"""
        using System;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using B=Atelia.DurableGraph.Generated.Family_4842;
        namespace InheritanceHistoryBases;
        public static class Trace { public static int Constructors,Initializers,BaseUpgrades,MiddleUpgrades,LeafUpgrades; public static int Init(){Initializers++;return 100;} }
        [DurableType("HV",{{version}})] internal readonly partial struct {{(version==1 ? "LegacyHidden" : "CurrentHidden")}} {
            [DurableField(1)] private readonly {{(version==1 ? "int" : "long")}} _number;
            public {{(version==1 ? "LegacyHidden" : "CurrentHidden")}}(int number) { _number=number; }
            public long Number=>_number;
        }
        [DurableType("HB",{{version}})] public abstract partial class {{(version==1 ? "LegacyBase" : "CurrentBase")}}:IDurableObject {
            [DurableField(1)] private readonly {{(version==1 ? "LegacyHidden" : "CurrentHidden")}} _hidden;
            [DurableField(2)] public {{(version==1 ? "LegacyBase" : "CurrentBase")}}? Self;
            [DurableField(3)] private readonly Guid _padding;
            [Transient] private int _cache=Trace.Init();
            protected {{(version==1 ? "LegacyBase" : "CurrentBase")}}(int number) { Trace.Constructors++;_hidden=new(number);_padding=new("3bb5385a-0f2b-4cbb-9ce7-273b3af88586"); }
            public long Number=>_hidden.Number;
            public bool Hydrated=>_cache==0 && _padding==new Guid("3bb5385a-0f2b-4cbb-9ce7-273b3af88586");
        }
        public static class Catalog { public static void Register(IStateModelRegistration models)=>Atelia.DurableGraph.Generated.DurableDefinitions.Register(models); }
        {{(version==2 ? """
        public static class BaseRules {
            [DurableUpgrade(typeof(CurrentBase),1)]
            public static void Upgrade(in B.V1 prior,out B.V2 next,UpgradeContext context) {
                Trace.BaseUpgrades++;next=new(new(prior.Segment0Field1.Segment0Field1+9000L),prior.Segment0Field2,prior.Segment0Field3);
            }
        }
        """ : "")}}
        """;

    private static string InheritanceHistoryMiddle(int version) => $$"""
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using InheritanceHistoryBases;
        using M=Atelia.DurableGraph.Generated.Family_484D;
        namespace InheritanceHistoryMiddle;
        [DurableType("HM",{{version}})] public abstract partial class Middle:{{(version==1 ? "LegacyBase" : "CurrentBase")}} {
            [DurableField(1)] private readonly int _middle;
            protected Middle(int number):base(number) { Trace.Constructors++;_middle=777; }
            public int MiddleValue=>_middle;
        }
        public static class Catalog { public static void Register(IStateModelRegistration models)=>Atelia.DurableGraph.Generated.DurableDefinitions.Register(models); }
        {{(version==2 ? """
        public static class MiddleRules {
            [DurableUpgrade(typeof(Middle),1)]
            public static void Upgrade(in M.V1 prior,out M.V2 next,UpgradeContext context) {
                Trace.MiddleUpgrades++;next=new(new(prior.Segment0Field1.Segment0Field1+8000L),prior.Segment0Field2,prior.Segment0Field3,prior.Segment1Field1);
            }
        }
        """ : "")}}
        """;

    private static string InheritanceHistoryApp(int version, bool leafUpgrade = true) => $$"""
        using System;
        using System.Collections.Generic;
        using System.IO;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Persistence;
        using Atelia.DurableGraph.Storage;
        using InheritanceHistoryBases;
        using InheritanceHistoryMiddle;
        using L=Atelia.DurableGraph.Generated.Family_484C;
        namespace InheritanceHistoryApp;
        [DurableType("HL",{{version}})] public sealed partial class Leaf:Middle {
            [DurableField(1)] public int LeafValue;
            public Leaf(int number):base(number) { Trace.Constructors++;LeafValue=88;Self=this; }
        }
        public static class Host {
            public static StateModelRegistry Models(bool missingBase) {
                var models=new StateModelRegistry();
                if(!missingBase)InheritanceHistoryBases.Catalog.Register(models);
                InheritanceHistoryMiddle.Catalog.Register(models);Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);return models;
            }
            public static FrameAddress Seed(string path) {
                using var repo=FixtureGraphRepository.CreateNew(path, Models(false));using var session=repo.Create(new Leaf(10000));return session.Commit(new(1000000,1));
            }
            public static int Callbacks()=>Trace.BaseUpgrades+Trace.MiddleUpgrades+Trace.LeafUpgrades;
            public static bool LoadFails(string path,bool missingBase) {
                Trace.BaseUpgrades=Trace.MiddleUpgrades=Trace.LeafUpgrades=0;
                using var repo=FixtureGraphRepository.OpenExisting(path, Models(missingBase));var prior=repo.HeadRevisionAddress;
                try {using var session=repo.Load<Leaf>();return false;}
                catch(InvalidDataException) { return repo.HeadRevisionAddress==prior && Callbacks()==0; }
            }
            public static FrameAddress[] UpgradeAndSave(string path) {
                Trace.BaseUpgrades=Trace.MiddleUpgrades=Trace.LeafUpgrades=Trace.Constructors=Trace.Initializers=0;
                var addresses=new List<FrameAddress>();var models=Models(false);
                using(var repo=FixtureGraphRepository.OpenExisting(path, models))using(var session=repo.Load<Leaf>()) {
                    var leaf=session.World;
                    if(leaf.Number!=11000 || leaf.MiddleValue!=777 || leaf.LeafValue!=98 || !leaf.Hydrated || !ReferenceEquals(leaf,leaf.Self))throw new InvalidOperationException("leaf complete DTO upgrade");
                    if(Trace.BaseUpgrades!=0 || Trace.MiddleUpgrades!=0 || Trace.LeafUpgrades!=1 || Trace.Constructors!=0 || Trace.Initializers!=0)throw new InvalidOperationException("unexpected base rules, constructor or initialization");
                    addresses.Add(session.Commit(new(1000000,1)));addresses.Add(session.Commit(new(1000000,1)));
                    leaf.LeafValue++;addresses.Add(session.Commit(new(1000000,1)));
                }
                using(var repo=FixtureGraphRepository.OpenExisting(path, models))using(var session=repo.Load<Leaf>()) {
                    if(session.World.Number!=11000 || session.World.LeafValue!=99 || Callbacks()!=1 || Trace.LeafUpgrades!=1)throw new InvalidOperationException("cold Delta or repeated upgrade");
                    addresses.Add(session.Commit(new(1000000,1)));
                }
                return addresses.ToArray();
            }
        }
        {{(version==2 && leafUpgrade ? """
        public static class LeafRules {
            [DurableUpgrade(typeof(Leaf),1)]
            public static void Upgrade(in L.V1 prior,out L.V2 next,UpgradeContext context) {
                Trace.LeafUpgrades++;next=new(new(prior.Segment0Field1.Segment0Field1+1000L),prior.Segment0Field2,prior.Segment0Field3,prior.Segment1Field1,prior.Segment2Field1+10);
            }
        }
        """ : "")}}
        """;
}
