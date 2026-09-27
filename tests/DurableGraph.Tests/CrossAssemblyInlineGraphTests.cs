using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrossAssemblyFixedInlineGraphPreservesProjectionIdentityAndNestedDeltas(bool reverseRegistration) {
        GeneratorTestRun library = RunCrossAssemblyGenerator(CrossInlineValues, forceDefinitions: "true");
        var image = EmitCrossAssemblyReference(library);
        GeneratorTestRun app = RunCrossAssemblyGenerator(CrossInlineApplication, [image.Reference]);
        AssertSchemaOnlyCompiles(app);
        Assert.DoesNotContain(app.OutputCompilation.GetDiagnostics(), d => d.Id is "CS0436" or "CS0433");
        using var scope = new CrossAssemblyLoadScope(library, app);
        Type host = scope.Load(app).GetType("InlineApp.Host")!;
        StateModelRegistry registry = host.GetMethod("Models")!
            .CreateDelegate<Func<bool, StateModelRegistry>>()(reverseRegistration);

        // Frozen state owns the entire inline copy, including a library-private child.
        var seed = host.GetMethod("Seed")!.CreateDelegate<Func<IDurableObject>>();
        var mutate = host.GetMethod("MutateValue")!.CreateDelegate<Action<IDurableObject>>();
        IDurableObject world = seed();
        var snapshot = registry.Snapshot();
        var binding = snapshot.ResolveCurrentModel(world.GetType());
        CaptureSession capture = new();
        using (CaptureContext first = capture.BeginCapture(snapshot)) {
            binding.AddRoot(first, world);
            var frozen = first.Seal();
            var before = capture.Prepare(frozen).Objects.Select(x => x.BaseBody.Body.ToArray()).ToArray();
            mutate(world);
            var after = capture.Prepare(frozen).Objects.Select(x => x.BaseBody.Body.ToArray()).ToArray();
            Assert.Equal(before.Length, after.Length);
            for (int i = 0; i < before.Length; i++) { Assert.Equal(before[i], after[i]); }
            capture.Accept(frozen);
        }
        using (CaptureContext second = capture.BeginCapture(snapshot)) {
            binding.AddRoot(second, world);
            var next = capture.Prepare(second.Seal());
            var changed = Assert.Single(next.Objects, x => x.DeltaBody?.HasChanges == true);
            Assert.Equal("inline.World", changed.Current.Schema!.SchemaId);
        }

        using RawBaseDirectory directory = new();
        FrameAddress[] addresses = host.GetMethod("Exercise")!
            .CreateDelegate<Func<string, bool, FrameAddress[]>>()(directory.Path, reverseRegistration);
        using SegmentStore segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory.Path, "state"));
        using var schemaFile = RbfFile.OpenReadOnlyExisting(Path.Combine(directory.Path, "schemas.rbf"));
        SchemaStore schemas = new(schemaFile, readOnly: true);
        using StateRevisionStore states = new(segments);
        var initial = RevisionDecoder.ReadSnapshot(states, schemas, addresses[0], registry.Snapshot(schemas));
        ObjectStateRecord root = Assert.Single(initial.Objects, row => row.Schema?.SchemaId == "inline.World");
        ObjectStateRecord node = Assert.Single(initial.Objects, row => row.Schema?.SchemaId == "inline.Node");
        foreach (int index in new[] { 1, 4, 5 }) {
            Assert.Empty(states.Read(addresses[index]).LocalObjects);
            Assert.Empty(states.Read(addresses[index]).RemovedObjectIds);
        }
        var childChange = Assert.Single(states.Read(addresses[2]).LocalObjects);
        Assert.Equal(node.Id.Value, childChange.ObjectId);
        Assert.Equal(ObjectVersionKind.Delta, childChange.Kind);
        var inlineChange = Assert.Single(states.Read(addresses[3]).LocalObjects);
        Assert.Equal(root.Id.Value, inlineChange.ObjectId);
        Assert.Equal(ObjectVersionKind.Delta, inlineChange.Kind);
        Assert.Equal(initial.Objects.Count, RevisionDecoder.ReadSnapshot(states, schemas,
            addresses[^1], registry.Snapshot(schemas)).Objects.Count);
    }

    private const string CrossInlineValues = """
        using System.Collections.Generic;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        namespace InlineValues;
        [DurableType("inline.Hidden",1)] internal readonly partial struct Hidden {
            [DurableField(1)] private readonly int _value;
            public Hidden(int value) { _value=value; }
            public int Value=>_value;
        }
        [DurableType("inline.Point",1)] public partial struct Point {
            [DurableField(1)] public int X;
            [DurableField(2)] public readonly int Y;
            [DurableField(3)] private readonly Hidden _implementation;
            public Point(int x,int y) { X=x;Y=y;_implementation=new(31415); }
            public int HiddenValue=>_implementation.Value;
        }
        [DurableType("inline.Record",1)] public readonly partial record struct Record([field:DurableField(1)] int Value);
        [DurableType("inline.Mode",1)] public enum Mode:ushort { Named=7 }
        [DurableType("inline.Pair",1)] public partial struct Pair<T> {
            [DurableField(1)] public T Value;
            [DurableField(2)] public Point Origin;
        }
        [DurableType("inline.Node",1)] public partial class Node:IDurableObject {
            [DurableField(1)] public int Value=10000;
            [DurableField(2)] public int Padding1=20000;
            [DurableField(3)] public int Padding2=30000;
            [DurableField(4)] public int Padding3=40000;
            [DurableField(5)] public Node Next;
        }
        [DurableType("inline.Payload",1)] public partial struct Payload {
            [DurableField(1)] public Point Position;
            [DurableField(2)] public string Text;
            [DurableField(3)] public Node Link;
            [DurableField(4)] public List<Node> Nodes;
        }
        public static class Catalog {
            public static void Register(IStateModelRegistration models)=>Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
        }
        """;

    private const string CrossInlineApplication = """
        using System;
        using System.Collections.Generic;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Persistence;
        using Atelia.DurableGraph.Storage;
        using InlineValues;
        namespace InlineApp;
        [DurableType("inline.Local",1)] public partial struct Local { [DurableField(1)] public int Value; }
        [DurableType("inline.Key",1)] public readonly partial record struct Key(
            [field:DurableField(1)] Record Region,[field:DurableField(2)] int Index);
        [DurableType("inline.Wrapper",1)] public partial struct Wrapper<T> {
            [DurableField(1)] public Pair<T> Pair;
            [DurableField(2)] public Point Fixed;
        }
        [DurableType("inline.Base",1)] public abstract partial class Base:IDurableObject {
            [DurableField(1)] private readonly Point _basePoint;
            protected Base(Point point) { _basePoint=point; }
            public Point BasePoint=>_basePoint;
        }
        [DurableType("inline.World",1)] public partial class World:Base {
            [DurableField(1)] public Payload Payload;
            [DurableField(2)] public Point? Optional;
            [DurableField(3)] public Point? Absent;
            [DurableField(4)] public readonly Record Record;
            [DurableField(5)] public Mode Mode;
            [DurableField(6)] public Pair<Local> Pair;
            [DurableField(7)] public Wrapper<Local> Wrapper;
            [DurableField(8)] public Dictionary<Key,Node> Map;
            [DurableField(9)] public string Alias;
            public World(Point point):base(point) { Record=new(12345); }
        }
        public static class Host {
            public static StateModelRegistry Models(bool reverse) {
                var models=new StateModelRegistry();
                if(reverse) { Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);Catalog.Register(models); }
                else { Catalog.Register(models);Atelia.DurableGraph.Generated.DurableDefinitions.Register(models); }
                return models;
            }
            public static IDurableObject Seed() {
                var point=new Point(12345,54321);var node=new Node();node.Next=node;
                var text=new string(new[]{'s','h','a','r','e','d'});
                var pair=new Pair<Local>{Value=new(){Value=67890},Origin=point};
                return new World(point) { Payload=new(){Position=point,Text=text,Link=node,Nodes=new(){node,node}},
                    Optional=point,Mode=(Mode)65000,Pair=pair,Wrapper=new(){Pair=pair,Fixed=point},
                    Map=new(){[new(new(9),7)]=node},Alias=text };
            }
            public static void MutateValue(IDurableObject value) { ((World)value).Payload.Position.X++; }
            static void Require(bool condition,string why) { if(!condition)throw new InvalidOperationException(why); }
            static void Check(World w) {
                Require(w.Payload.Position.X==12346 && w.Payload.Position.Y==54321 && w.Payload.Position.HiddenValue==31415,"private nested implementation");
                Require(w.BasePoint.X==12345 && w.BasePoint.HiddenValue==31415,"local base segment");
                Require(w.Optional!.Value.X==12345 && w.Absent is null && w.Record.Value==12345 && (ushort)w.Mode==65000,"nullable readonly record and unknown enum");
                Require(w.Pair.Value.Value==67890 && w.Pair.Origin.X==12345 && w.Wrapper.Pair.Value.Value==67890 && w.Wrapper.Fixed.X==12345,"fixed and open generic composition");
                Require(ReferenceEquals(w.Payload.Text,w.Alias),"shared string through value");
                var node=w.Payload.Link;
                Require(node.Value==10001 && ReferenceEquals(node,node.Next) && ReferenceEquals(node,w.Map[new(new(9),7)]),"cycle and composite key");
                Require(ReferenceEquals(node,w.Payload.Nodes[0]) && ReferenceEquals(node,w.Payload.Nodes[1]),"shared external value container");
            }
            public static FrameAddress[] Exercise(string path,bool reverse) {
                var models=Models(reverse);var addresses=new List<FrameAddress>();var world=(World)Seed();
                using(var repo=FixtureGraphRepository.CreateNew(path, models))using(var session=repo.Create(world)) {
                    void Save()=>addresses.Add(session.Commit(new(1000000,1)));
                    Save();Save();world.Payload.Link.Value++;Save();MutateValue(world);Save();Save();
                }
                using(var repo=FixtureGraphRepository.OpenExisting(path, models))using(var session=repo.Load<World>()) {
                    Check(session.World);addresses.Add(session.Commit(new(1000000,1)));
                }
                return addresses.ToArray();
            }
        }
        """;
}
