using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Text;
using Atelia.DurableGraph.Build;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrossAssemblyFixedInlineHistorySurvivesDeletedClrAndUsesExplicitValueUpgrade(bool empty) {
        using AncestryHistoryDirectory libraryHistory = new(), ownerHistory = new();
        GeneratorTestRun library1 = RunCrossAssemblyGenerator(CrossInlineHistoryLibrary(1), forceDefinitions: "true");
        var image1 = EmitCrossAssemblyReference(library1);
        GeneratorTestRun app1 = RunCrossAssemblyGenerator(CrossInlineHistoryApp(1), [image1.Reference]);
        AssertSchemaOnlyCompiles(app1);
        PublishCrossInlineHistory(libraryHistory, library1);
        PublishCrossInlineHistory(ownerHistory, app1);
        var libraryOriginal = libraryHistory.ReadContents();
        var ownerOriginal = ownerHistory.ReadContents();
        Assert.Single(libraryOriginal);
        Assert.Single(ownerOriginal); // Imported ExtValue belongs solely to its providing library.
        using RawBaseDirectory directory = new();
        FrameAddress original;
        DurableSchema exact;
        using (var scope = new CrossAssemblyLoadScope(library1, app1)) {
            Type host = scope.Load(app1).GetType("InlineHistoryApp.Host")!;
            original = host.GetMethod("Seed")!.CreateDelegate<Func<string, bool, FrameAddress>>()(directory.Path, empty);
            var models = host.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>()();
            using SegmentStore segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory.Path, "state"));
            using var schemaFile = RbfFile.OpenReadOnlyExisting(Path.Combine(directory.Path, "schemas.rbf"));
            SchemaStore schemas = new(schemaFile, readOnly: true);
            exact = Assert.Single(RevisionDecoder.ReadSnapshot(new(segments), schemas, original,
                models.Snapshot(schemas)).Objects, x => x.Schema?.SchemaId == "ExtWorld").Schema!;
        }

        GeneratorTestRun library2 = RunCrossAssemblyGenerator(CrossInlineHistoryLibrary(2),
            history: libraryHistory.ReadAdditionalTexts(), forceDefinitions: "true");
        var image2 = EmitCrossAssemblyReference(library2);
        GeneratorTestRun app2 = RunCrossAssemblyGenerator(CrossInlineHistoryApp(2), [image2.Reference],
            history: ownerHistory.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(app2);
        PublishCrossInlineHistory(libraryHistory, library2);
        PublishCrossInlineHistory(ownerHistory, app2);
        Assert.Equal(2, libraryHistory.ReadContents().Count);
        Assert.Equal(2, ownerHistory.ReadContents().Count);
        foreach (var pair in libraryOriginal) { Assert.Equal(pair.Value, libraryHistory.ReadContents()[pair.Key]); }
        foreach (var pair in ownerOriginal) { Assert.Equal(pair.Value, ownerHistory.ReadContents()[pair.Key]); }

        using var current = new CrossAssemblyLoadScope(library2, app2);
        Assert.Null(current.Load(library2).GetType("InlineHistoryValues.LegacyPoint"));
        Type currentHost = current.Load(app2).GetType("InlineHistoryApp.Host")!;
        var currentModels = currentHost.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>()();
        using (SegmentStore segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory.Path, "state")))
        using (var schemaFile = RbfFile.OpenReadOnlyExisting(Path.Combine(directory.Path, "schemas.rbf"))) {
            SchemaStore schemas = new(schemaFile, readOnly: true);
            var decoded = RevisionDecoder.ReadSnapshot(new(segments), schemas, original, currentModels.Snapshot(schemas));
            var world = Assert.Single(decoded.Objects, x => x.Schema?.SchemaId == "ExtWorld");
            Assert.Equal(exact, world.Schema);
            Assert.Equal(1, world.Schema!.Fields[0].InlineSchema!.Version);
            Assert.Equal(0, currentHost.GetMethod("CallbackCount")!.CreateDelegate<Func<int>>()());
        }
        FrameAddress[] addresses = currentHost.GetMethod("UpgradeAndSave")!
            .CreateDelegate<Func<string, bool, FrameAddress[]>>()(directory.Path, empty);
        using (SegmentStore segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory.Path, "state"))) {
            using StateRevisionStore states = new(segments);
            Assert.Equal(2, states.Read(addresses[0]).LocalObjects.Count);
            Assert.All(states.Read(addresses[0]).LocalObjects, x => Assert.Equal(ObjectVersionKind.Base, x.Kind));
            foreach (int index in new[] { 1, 3 }) { Assert.Empty(states.Read(addresses[index]).LocalObjects); }
            var delta = Assert.Single(states.Read(addresses[2]).LocalObjects);
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrossAssemblyFixedInlineMissingCapabilitiesFailBeforeAffectedCallbacksAndPublication(bool empty) {
        using AncestryHistoryDirectory libraryHistory = new(), ownerHistory = new();
        GeneratorTestRun library1 = RunCrossAssemblyGenerator(CrossInlineHistoryLibrary(1), forceDefinitions: "true");
        var image1 = EmitCrossAssemblyReference(library1);
        GeneratorTestRun app1 = RunCrossAssemblyGenerator(CrossInlineHistoryApp(1), [image1.Reference]);
        AssertSchemaOnlyCompiles(app1);
        PublishCrossInlineHistory(libraryHistory, library1);
        PublishCrossInlineHistory(ownerHistory, app1);
        using RawBaseDirectory directory = new();
        FrameAddress original;
        using (var scope = new CrossAssemblyLoadScope(library1, app1)) {
            original = scope.Load(app1).GetType("InlineHistoryApp.Host")!.GetMethod("Seed")!
                .CreateDelegate<Func<string, bool, FrameAddress>>()(directory.Path, empty);
        }
        GeneratorTestRun library2 = RunCrossAssemblyGenerator(CrossInlineHistoryLibrary(2),
            history: libraryHistory.ReadAdditionalTexts(), forceDefinitions: "true");
        var image2 = EmitCrossAssemblyReference(library2);
        // Leave only the optional slot's dependency unresolved: an absent value still
        // needs its complete historical conversion capability before any callback.
        string missingOptionalRule = CrossInlineHistoryApp(2)
            .Replace("[UpgradeDependency(\"position\",typeof(Rules),\"ExtWorld\",1,\"ExtWorld\",1)]", "", StringComparison.Ordinal)
            .Replace("var point=context.GetValueUpgrade<P.V1,P.V2>(\"position\")(in prior.Segment0Field1);",
                "var point=new P.V2(prior.Segment0Field1.Segment0Field1+1000L,prior.Segment0Field1.Segment0Field2);", StringComparison.Ordinal)
            .Replace("UseListElementUpgrades(typeof(Rules))", "UseListElementUpgrades(typeof(ListRules))", StringComparison.Ordinal)
            .Replace("[DurableValueUpgrade(typeof(Rules),", "[DurableValueUpgrade(typeof(ListRules),", StringComparison.Ordinal)
            + "\n[ValueUpgradeRuleSet] public sealed class ListRules;\n";
        GeneratorTestRun missingRules = RunCrossAssemblyGenerator(missingOptionalRule,
            [image2.Reference], history: ownerHistory.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(missingRules); // Exported templates do not imply a business conversion.
        using (var scope = new CrossAssemblyLoadScope(library2, missingRules)) {
            Type host = scope.Load(missingRules).GetType("InlineHistoryApp.Host")!;
            Assert.True(host.GetMethod("MissingRulesFails")!.CreateDelegate<Func<string, bool>>()(directory.Path));
        }
        GeneratorTestRun app2 = RunCrossAssemblyGenerator(CrossInlineHistoryApp(2), [image2.Reference],
            history: ownerHistory.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(app2);
        using (var scope = new CrossAssemblyLoadScope(library2, app2)) {
            Type host = scope.Load(app2).GetType("InlineHistoryApp.Host")!;
            var missing = host.GetMethod("MissingRegistrationFails")!.CreateDelegate<Func<string, bool, bool>>();
            Assert.True(missing(directory.Path, false)); // No external Definition at all.
            Assert.True(missing(directory.Path, true)); // Templates/current factory, but no historical factory.
            // Per-owner preflight: the earlier World may already have upgraded before
            // the List is reached, but this List must execute no element callbacks.
            Assert.Null(host.GetMethod("MissingListRuleFailure")!
                .CreateDelegate<Func<string, string?>>()(directory.Path));
        }
        // A fixed external layout change is visible during generation, unlike a nominal
        // reference change. Keeping owner v1 is rejected before any runtime publication.
        GeneratorTestRun forgotOwner = RunCrossAssemblyGenerator(CrossInlineHistoryApp(1, currentLibrary: true),
            [image2.Reference], history: ownerHistory.ReadAdditionalTexts());
        Assert.Contains(forgotOwner.GeneratorDiagnostics, d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        using var repository = FixtureGraphRepository.OpenExisting(directory.Path, new StateModelRegistry());
        Assert.Equal(original, repository.HeadRevisionAddress);
        Assert.Single(ownerHistory.ReadContents());
    }

    private static void PublishCrossInlineHistory(AncestryHistoryDirectory directory, GeneratorTestRun run) {
        string owned = directory.WriteManifest(run);
        var references = run.GeneratedSources.SingleOrDefault(x => x.HintName == "DurableGraphSchemaHistoryReferences.g.cs");
        string? referencePath = null;
        if (references.SourceText is not null) {
            referencePath = Path.Combine(Path.GetDirectoryName(owned)!, "references.g.cs");
            File.WriteAllText(referencePath, references.SourceText.ToString(), new UTF8Encoding(false));
        }
        SchemaHistoryTool tool = new();
        tool.Publish(owned, directory.History, referencePath);
        tool.Verify(owned, directory.History, referencePath);
    }

    private static string CrossInlineHistoryLibrary(int version) => $$"""
        using System;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        namespace InlineHistoryValues;
        [DurableType("ExtValue",{{version}})] public partial struct {{(version==1 ? "LegacyPoint" : "Point")}} {
            [DurableField(1)] public {{(version==1 ? "int" : "long")}} X;
            [DurableField(2)] public Guid Padding;
        }
        public static class Catalog {
            public static void Register(IStateModelRegistration models)=>Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
        }
        """;

    private static string CrossInlineHistoryApp(int version, bool rules = true, bool currentLibrary = false) => $$$$"""
        using System;
        using System.Collections.Generic;
        using System.IO;
        using System.Linq;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Persistence;
        using Atelia.DurableGraph.Storage;
        using P=Atelia.DurableGraph.Generated.Family_45787456616C7565;
        using W=Atelia.DurableGraph.Generated.Family_457874576F726C64;
        using Point=InlineHistoryValues.{{{{(version==1 && !currentLibrary ? "LegacyPoint" : "Point")}}}};
        namespace InlineHistoryApp;
        [DurableType("ExtWorld",{{{{version}}}})] public partial class World:IDurableObject {
            [DurableField(1)] public Point Position;
            [DurableField(2)] public Point? Optional;
            [DurableField(3)] public List<Point> Items=new();
            [DurableField(4)] public Guid Padding;
        }
        [ValueUpgradeRuleSet(AllowKeepExact=true,AllowNullableLifting=true)] public sealed class Rules;
        public static class Catalog {
            public static void Register(IStateModelRegistration models)=>Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
        }
        public static class Host {
            public static StateModelRegistry Models() {
                var models=new StateModelRegistry();InlineHistoryValues.Catalog.Register(models);Catalog.Register(models);
                models.UseListElementUpgrades(typeof(Rules));return models;
            }
            public static FrameAddress Seed(string path,bool empty) {
                var point=new Point{X=10000,Padding=new Guid("7a04a8b3-9ac2-4afd-ac86-7ab1b19e7b3f")};
                var world=new World{Position=point,Optional=empty?null:point,Padding=point.Padding};
                if(!empty)for(int i=0;i<16;i++)world.Items.Add(point);
                using var repo=FixtureGraphRepository.CreateNew(path, Models());using var session=repo.Create(world);
                return session.Commit(new(1000000,1));
            }
        {{{{(version==2 ? """
            public static int CallbackCount()=>Upgrades.OwnerCalls+Upgrades.ValueCalls;
            public static bool MissingRulesFails(string path) {
                Upgrades.OwnerCalls=Upgrades.ValueCalls=Upgrades.ListValueCalls=0;
                using var repo=FixtureGraphRepository.OpenExisting(path, Models());var head=repo.HeadRevisionAddress;
                try {using var session=repo.Load<World>();return false;}
                catch(InvalidDataException) {return repo.HeadRevisionAddress==head && CallbackCount()==0;}
            }
            public static bool MissingRegistrationFails(string path,bool keepCurrent) {
                Upgrades.OwnerCalls=Upgrades.ValueCalls=Upgrades.ListValueCalls=0;
                var models=new StateModelRegistry();Catalog.Register(models);models.UseListElementUpgrades(typeof(Rules));
                if(keepCurrent) {
                    var original=P.Definition;
                    models.Register(new StateDefinitionBinding(original.DefinitionId,original.Kind,original.Arity,
                        original.DomainTypeDefinition,original.Templates,currentValueFactory:original.CurrentValueFactory));
                }
                using var repo=FixtureGraphRepository.OpenExisting(path, models);var head=repo.HeadRevisionAddress;
                try {using var session=repo.Load<World>();return false;}
                catch(InvalidDataException) {return repo.HeadRevisionAddress==head && CallbackCount()==0;}
            }
            public static string? MissingListRuleFailure(string path) {
                Upgrades.OwnerCalls=Upgrades.ValueCalls=Upgrades.ListValueCalls=0;
                var models=new StateModelRegistry();Catalog.Register(models);InlineHistoryValues.Catalog.Register(models);
                using var repo=FixtureGraphRepository.OpenExisting(path, models);var head=repo.HeadRevisionAddress;
                try {using var session=repo.Load<World>();return "Load unexpectedly accepted the missing List rule selection.";}
                catch(InvalidDataException error) {
                    if(!error.Message.Contains("explicitly selected list element upgrade rule set",StringComparison.Ordinal))return "Unexpected failure: "+error;
                    if(repo.HeadRevisionAddress!=head)return "The failed Load changed the published head.";
                    if(Upgrades.ListValueCalls!=0)return $"List callbacks ran before List preflight: {Upgrades.ListValueCalls}; owner callbacks={Upgrades.OwnerCalls}; all value callbacks={Upgrades.ValueCalls}.";
                    return null;
                }
            }
            public static FrameAddress[] UpgradeAndSave(string path,bool empty) {
                Upgrades.OwnerCalls=Upgrades.ValueCalls=Upgrades.ListValueCalls=0;var models=Models();var addresses=new List<FrameAddress>();
                using(var repo=FixtureGraphRepository.OpenExisting(path, models))using(var session=repo.Load<World>()) {
                    var w=session.World;
                    if(w.Position.X!=11000 || w.Optional.HasValue==empty || (w.Optional.HasValue && w.Optional.Value.X!=11000) || w.Items.Count!=(empty?0:16) || w.Items.Any(x=>x.X!=11000))throw new InvalidOperationException("explicit upgraded values");
                    if(Upgrades.OwnerCalls!=1 || Upgrades.ValueCalls!=(empty?1:18) || Upgrades.ListValueCalls!=(empty?0:16))throw new InvalidOperationException("upgrade callbacks");
                    addresses.Add(session.Commit(new(1000000,1)));addresses.Add(session.Commit(new(1000000,1)));
                    w.Position.X++;addresses.Add(session.Commit(new(1000000,1)));
                }
                using(var repo=FixtureGraphRepository.OpenExisting(path, models))using(var session=repo.Load<World>()) {
                    if(session.World.Position.X!=11001 || Upgrades.OwnerCalls!=1 || Upgrades.ValueCalls!=(empty?1:18))throw new InvalidOperationException("cold delta or repeated upgrade");
                    addresses.Add(session.Commit(new(1000000,1)));
                }
                return addresses.ToArray();
            }
        """ : "")}}}}
        }
        """ + (version==2 ? """
        public static class Upgrades {
            public static int OwnerCalls,ValueCalls,ListValueCalls;
            [DurableUpgrade(typeof(World),1)]
            [UpgradeDependency("position",typeof(Rules),"ExtWorld",1,"ExtWorld",1)]
            [UpgradeDependency("optional",typeof(Rules),"ExtWorld",2,"ExtWorld",2)]
            public static void World(in W.V1<NullableState<P.V1>> prior,out W.V2<NullableState<P.V2>> next,UpgradeContext context) {
                OwnerCalls++;
                var point=context.GetValueUpgrade<P.V1,P.V2>("position")(in prior.Segment0Field1);
                var optional=context.GetValueUpgrade<NullableState<P.V1>,NullableState<P.V2>>("optional")(in prior.Segment0Field2);
                next=new(point,optional,prior.Segment0Field3,prior.Segment0Field4);
            }
        """ + (rules ? """
            [DurableValueUpgrade(typeof(Rules),"ExtValue",1,2)]
            public static void Value(in P.V1 prior,out P.V2 next,UpgradeContext context) {
                ValueCalls++;if(context.ListCount.HasValue)ListValueCalls++;
                next=new(prior.Segment0Field1+1000L,prior.Segment0Field2);
            }
        """ : "") + "}" : "");
}
