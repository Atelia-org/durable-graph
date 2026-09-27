using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
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
    public void CrossAssemblyExternalInlineHistoryUsesExplicitOwnerAndLibraryValueRulesEvenForEmptyLists(bool empty) {
        using AncestryHistoryDirectory remoteHistory = new(), appHistory = new();
        GeneratorTestRun remote1 = RunCrossAssemblyGenerator(CrossAssemblyHistoryRemote(1), forceDefinitions: "true");
        AssertSchemaOnlyCompiles(remote1);
        var remoteImage1 = EmitCrossAssemblyReference(remote1);
        GeneratorTestRun app1 = RunCrossAssemblyGenerator(CrossAssemblyHistoryApp(1), [remoteImage1.Reference]);
        AssertSchemaOnlyCompiles(app1);
        SchemaHistoryTool tool = new();
        tool.Publish(remoteHistory.WriteManifest(remote1), remoteHistory.History);
        tool.Publish(appHistory.WriteManifest(app1), appHistory.History);
        var remoteOriginal = remoteHistory.ReadContents();
        var appOriginal = appHistory.ReadContents();
        Assert.Single(remoteOriginal);
        Assert.Equal(2, appOriginal.Count); // The consuming assembly owns only World and Inline.
        using RawBaseDirectory directory = new();
        FrameAddress original;
        DurableSchema exactWorld;
        using (var oldScope = new CrossAssemblyLoadScope(remote1, app1)) {
            Type host = oldScope.Load(app1).GetType("CrossHistoryApp.Host")!;
            original = host.GetMethod("Seed")!.CreateDelegate<Func<string, bool, FrameAddress>>()(directory.Path, empty);
            var oldModels = host.GetMethod("Models")!.CreateDelegate<Func<bool, StateModelRegistry>>()(true);
            using SegmentStore segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory.Path, "state"));
            using var file = RbfFile.OpenReadOnlyExisting(Path.Combine(directory.Path, "schemas.rbf"));
            SchemaStore schemas = new(file, readOnly: true);
            var revision = RevisionDecoder.ReadSnapshot(new(segments), schemas, original, oldModels.Snapshot(schemas));
            exactWorld = Assert.Single(revision.Objects, row => row.Schema?.SchemaId == "CrossWorld").Schema!;
        }

        GeneratorTestRun remote2 = RunCrossAssemblyGenerator(CrossAssemblyHistoryRemote(2),
            history: remoteHistory.ReadAdditionalTexts(), forceDefinitions: "true");
        AssertSchemaOnlyCompiles(remote2);
        var remoteImage2 = EmitCrossAssemblyReference(remote2);
        GeneratorTestRun app2 = RunCrossAssemblyGenerator(CrossAssemblyHistoryApp(2), [remoteImage2.Reference],
            history: appHistory.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(app2);
        tool.Publish(remoteHistory.WriteManifest(remote2), remoteHistory.History);
        tool.Publish(appHistory.WriteManifest(app2), appHistory.History);
        tool.Verify(remoteHistory.WriteManifest(remote2), remoteHistory.History);
        tool.Verify(appHistory.WriteManifest(app2), appHistory.History);
        foreach (var pair in remoteOriginal) Assert.Equal(pair.Value, remoteHistory.ReadContents()[pair.Key]);
        foreach (var pair in appOriginal) Assert.Equal(pair.Value, appHistory.ReadContents()[pair.Key]);
        using var currentScope = new CrossAssemblyLoadScope(remote2, app2);
        Type currentHost = currentScope.Load(app2).GetType("CrossHistoryApp.Host")!;
        // Choosing no List value rule must fail even when there are no elements to visit.
        Assert.True(currentHost.GetMethod("MissingListRuleFailsWithoutPublishing")!
            .CreateDelegate<Func<string, bool>>()(directory.Path));
        var models = currentHost.GetMethod("Models")!.CreateDelegate<Func<bool, StateModelRegistry>>()(true);
        using (SegmentStore segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory.Path, "state")))
        using (var file = RbfFile.OpenReadOnlyExisting(Path.Combine(directory.Path, "schemas.rbf"))) {
            SchemaStore schemas = new(file, readOnly: true);
            var decoded = RevisionDecoder.ReadSnapshot(new(segments), schemas, original, models.Snapshot(schemas));
            var world = Assert.Single(decoded.Objects, row => row.Schema?.SchemaId == "CrossWorld");
            Assert.Equal(exactWorld, world.Schema);
            Assert.Equal(1, world.Schema!.Version);
            Assert.Equal(1, world.Schema.Fields[0].InlineSchema!.Version);
            Assert.Equal(1, world.Schema.Fields[0].InlineSchema!.Fields[0].InlineSchema!.Version);
        }
        FrameAddress[] addresses = currentHost.GetMethod("UpgradeAndSave")!
            .CreateDelegate<Func<string, bool, FrameAddress[]>>()(directory.Path, empty);
        using (SegmentStore segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory.Path, "state"))) {
            using StateRevisionStore states = new(segments);
            Assert.Equal(2, states.Read(addresses[0]).LocalObjects.Count);
            Assert.All(states.Read(addresses[0]).LocalObjects, row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
            foreach (int index in new[] { 1, 3 }) {
                Assert.Empty(states.Read(addresses[index]).LocalObjects);
                Assert.Empty(states.Read(addresses[index]).RemovedObjectIds);
            }
            Assert.Single(states.Read(addresses[2]).LocalObjects);
            if (!empty) { Assert.Equal(ObjectVersionKind.Delta, states.Read(addresses[2]).LocalObjects[0].Kind); }
        }
    }

    [Fact]
    public void CrossAssemblyMissingHistoricalValueFactoryAndForgottenOwnerBumpCannotUseCurrentLayoutAsHistory() {
        using AncestryHistoryDirectory remoteHistory = new(), appHistory = new();
        GeneratorTestRun remote1 = RunCrossAssemblyGenerator(CrossAssemblyHistoryRemote(1), forceDefinitions: "true");
        var remoteImage1 = EmitCrossAssemblyReference(remote1);
        GeneratorTestRun app1 = RunCrossAssemblyGenerator(CrossAssemblyHistoryApp(1), [remoteImage1.Reference]);
        AssertSchemaOnlyCompiles(app1);
        SchemaHistoryTool tool = new();
        tool.Publish(remoteHistory.WriteManifest(remote1), remoteHistory.History);
        tool.Publish(appHistory.WriteManifest(app1), appHistory.History);
        using RawBaseDirectory directory = new();
        DurableSchema prior;
        FrameAddress original;
        using (var oldScope = new CrossAssemblyLoadScope(remote1, app1)) {
            Type host = oldScope.Load(app1).GetType("CrossHistoryApp.Host")!;
            original = host.GetMethod("Seed")!.CreateDelegate<Func<string, bool, FrameAddress>>()(directory.Path, true);
            var models = host.GetMethod("Models")!.CreateDelegate<Func<bool, StateModelRegistry>>()(true);
            using SegmentStore segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory.Path, "state"));
            using var file = RbfFile.OpenReadOnlyExisting(Path.Combine(directory.Path, "schemas.rbf"));
            SchemaStore schemas = new(file, readOnly: true);
            prior = Assert.Single(RevisionDecoder.ReadSnapshot(new(segments), schemas, original, models.Snapshot(schemas)).Objects,
                row => row.Schema?.SchemaId == "CrossWorld").Schema!;
        }
        // Generate valid retained history first, then explicitly omit its historical value
        // capability at registration. The app's templates cannot synthesize that reader.
        GeneratorTestRun retired = RunCrossAssemblyGenerator(CrossAssemblyHistoryRemote(2, rules: false),
            history: remoteHistory.ReadAdditionalTexts(), forceDefinitions: "true");
        var retiredImage = EmitCrossAssemblyReference(retired);
        GeneratorTestRun app = RunCrossAssemblyGenerator(CrossAssemblyHistoryApp(1), [retiredImage.Reference],
            history: appHistory.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(app);
        using (var scope = new CrossAssemblyLoadScope(retired, app)) {
            var remoteAssembly = scope.Load(retired);
            var full = scope.Load(app).GetType("CrossHistoryApp.Host")!.GetMethod("Models")!
                .CreateDelegate<Func<bool, StateModelRegistry>>()(false);
            StateDefinitionBinding definition = full.Snapshot().GetDefinition("RemotePoint");
            StateModelRegistry models = new();
            scope.Load(app).GetType("CrossHistoryApp.Catalog")!.GetMethod("Register")!
                .CreateDelegate<Action<IStateModelRegistration>>()(models);
            models.Register(new StateDefinitionBinding(definition.DefinitionId, definition.Kind, definition.Arity,
                definition.DomainTypeDefinition, definition.Templates, currentValueFactory: definition.CurrentValueFactory));
            Assert.Equal(2, models.Snapshot().ResolveCurrentValue(remoteAssembly.GetType("CrossHistoryRemote.Point")!).Slot.InlineSchema!.Version);
            Assert.Throws<InvalidDataException>(() => models.Snapshot().ResolveReader(prior));
        }
        // Restoring the historical factory does not make a same-version changed owner legal.
        GeneratorTestRun changed = RunCrossAssemblyGenerator(CrossAssemblyHistoryRemote(2),
            history: remoteHistory.ReadAdditionalTexts(), forceDefinitions: "true");
        var changedImage = EmitCrossAssemblyReference(changed);
        GeneratorTestRun forgot = RunCrossAssemblyGenerator(CrossAssemblyHistoryApp(1), [changedImage.Reference],
            history: appHistory.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(forgot);
        using (var scope = new CrossAssemblyLoadScope(changed, forgot)) {
            Type host = scope.Load(forgot).GetType("CrossHistoryApp.Host")!;
            Assert.True(host.GetMethod("UnchangedOwnerFailsWithoutPublishing")!
                .CreateDelegate<Func<string, bool>>()(directory.Path));
        }
        using var repository = FixtureGraphRepository.OpenExisting(directory.Path, new StateModelRegistry());
        Assert.Equal(original, repository.HeadRevisionAddress);
    }

    private static string CrossAssemblyHistoryRemote(int version, bool rules = true) => $$"""
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using P=Atelia.DurableGraph.Generated.Family_52656D6F7465506F696E74;
        namespace CrossHistoryRemote;
        [DurableType("RemotePoint",{{version}})] public partial struct Point {
            [DurableField(1)] public {{(version == 1 ? "int" : "long")}} X;
        }
        [ValueUpgradeRuleSet] public sealed class Rules;
        public static class Catalog {
            public static void Register(IStateModelRegistration models)=>Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
        }
        """ + (version == 2 && rules ? """
        public static class Upgrades {
            public static int Calls;
            [DurableValueUpgrade(typeof(Rules),"RemotePoint",1,2)]
            public static void Point(in P.V1 prior,out P.V2 next,UpgradeContext context) {
                Calls++;next=new(prior.Segment0Field1+1000L);
            }
        }
        """ : "");

    private static string CrossAssemblyHistoryApp(int version) => $$$$"""
        using System;
        using System.Collections.Generic;
        using System.IO;
        using System.Linq;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Persistence;
        using Atelia.DurableGraph.Storage;
        using CrossHistoryRemote;
        using I=Atelia.DurableGraph.Generated.Family_4C6F63616C496E6C696E65;
        using W=Atelia.DurableGraph.Generated.Family_43726F7373576F726C64;
        using P=Atelia.DurableGraph.Generated.Family_52656D6F7465506F696E74;
        namespace CrossHistoryApp;
        [DurableType("LocalInline",{{{{version}}}})] public partial struct Inline<T> { [DurableField(1)] public T Value; }
        [DurableType("CrossWorld",{{{{version}}}})] public partial class World<T>:IDurableObject {
            [DurableField(1)] public T Value;
            [DurableField(2)] public List<Point> Points=new();
        }
        public static class Catalog {
            public static void Register(IStateModelRegistration models)=>Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
        }
        public static class Host {
            public static StateModelRegistry Models(bool selectList) {
                var models=new StateModelRegistry();CrossHistoryRemote.Catalog.Register(models);Catalog.Register(models);
                if(selectList)models.UseListElementUpgrades(typeof(CrossHistoryRemote.Rules));return models;
            }
            public static FrameAddress Seed(string path,bool empty) {
                var world=new World<Inline<Point>>{Value=new(){Value=new(){X=10000}}};
                if(!empty)for(int i=0;i<32;i++)world.Points.Add(new(){X=20000+i});
                using var repo=FixtureGraphRepository.CreateNew(path, Models(true));using var session=repo.Create(world);
                return session.Commit(new(1000000,1));
            }
            public static bool UnchangedOwnerFailsWithoutPublishing(string path) {
                using var repo=FixtureGraphRepository.OpenExisting(path, Models(true));var head=repo.HeadRevisionAddress;
                try {using var session=repo.Load<World<Inline<Point>>>();return false;}
                catch(InvalidDataException) {return repo.HeadRevisionAddress==head;}
                catch(SchemaConflictException) {return repo.HeadRevisionAddress==head;}
            }
        {{{{(version == 2 ? """
            public static bool MissingListRuleFailsWithoutPublishing(string path) {
                using var repo=FixtureGraphRepository.OpenExisting(path, Models(false));var head=repo.HeadRevisionAddress;
                try {using var session=repo.Load<World<Inline<Point>>>();return false;}
                catch(InvalidDataException) {return repo.HeadRevisionAddress==head;}
            }
            public static FrameAddress[] UpgradeAndSave(string path,bool empty) {
                Upgrades.OwnerCalls=Upgrades.InlineCalls=CrossHistoryRemote.Upgrades.Calls=0;
                var models=Models(true);var addresses=new List<FrameAddress>();
                using(var repo=FixtureGraphRepository.OpenExisting(path, models))using(var session=repo.Load<World<Inline<Point>>>()) {
                    var world=session.World;
                    if(world.Value.Value.X!=11000 || world.Points.Count!=(empty?0:32) || world.Points.Any(p=>p.X<21000))throw new InvalidOperationException("upgrade values");
                    if(Upgrades.OwnerCalls!=1 || Upgrades.InlineCalls!=1 || CrossHistoryRemote.Upgrades.Calls!=(empty?1:33))throw new InvalidOperationException("explicit callback counts");
                    addresses.Add(session.Commit(new(1000000,1)));addresses.Add(session.Commit(new(1000000,1)));
                    if(empty)world.Points.Add(new(){X=99999});else {var p=world.Points[0];p.X++;world.Points[0]=p;}
                    addresses.Add(session.Commit(new(1000000,1)));
                }
                using(var repo=FixtureGraphRepository.OpenExisting(path, models))using(var session=repo.Load<World<Inline<Point>>>()) {
                    if(session.World.Points[0].X!=(empty?99999:21001))throw new InvalidOperationException("cold delta");
                    if(Upgrades.OwnerCalls!=1 || CrossHistoryRemote.Upgrades.Calls!=(empty?1:33))throw new InvalidOperationException("repeated upgrade");
                    addresses.Add(session.Commit(new(1000000,1)));
                }
                return addresses.ToArray();
            }
        """ : "")}}}}
        }
        """ + (version == 2 ? """
        [ValueUpgradeRuleSet] public sealed class Rules;
        public static class Upgrades {
            public static int OwnerCalls,InlineCalls;
            [DurableUpgrade(typeof(World<>),1)]
            [UpgradeDependency("value",typeof(Rules),"CrossWorld",1,"CrossWorld",1)]
            public static void Owner<A,B>(in W.V1<A> prior,out W.V2<B> next,UpgradeContext context)
                where A:unmanaged where B:unmanaged {
                OwnerCalls++;var convert=context.GetValueUpgrade<A,B>("value");
                next=new(convert(in prior.Segment0Field1),prior.Segment0Field2);
            }
            [DurableValueUpgrade(typeof(Rules),"LocalInline",1,2)]
            public static void Inline(in I.V1<P.V1> prior,out I.V2<P.V2> next,UpgradeContext context) {
                InlineCalls++;CrossHistoryRemote.Upgrades.Point(in prior.Segment0Field1,out var point,context);next=new(point);
            }
        }
        """ : "");
}
