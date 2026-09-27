using Atelia.DurableGraph.Runtime;
using System.Reflection;
using System.Runtime.Loader;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrossAssemblyGraphComposesOrdinaryAndFamilyCatalogsAndResumesColdDeltas(bool reverseRegistration) {
        GeneratorTestRun ordinary = RunCrossAssemblyGenerator(CrossAssemblyOrdinaryLibrary);
        AssertSchemaOnlyCompiles(ordinary);
        var ordinaryImage = EmitCrossAssemblyReference(ordinary);
        GeneratorTestRun values = RunCrossAssemblyGenerator(CrossAssemblyValueLibrary);
        AssertSchemaOnlyCompiles(values);
        var valuesImage = EmitCrossAssemblyReference(values);
        GeneratorTestRun app = RunCrossAssemblyGenerator(CrossAssemblyGraphApplication,
            [ordinaryImage.Reference, valuesImage.Reference]);
        AssertSchemaOnlyCompiles(app);
        Assert.DoesNotContain(app.OutputCompilation.GetDiagnostics(), d => d.Id is "CS0436" or "CS0433");
        using var loaded = new CrossAssemblyLoadScope(ordinary, values, app);
        Type host = loaded.Load(app).GetType("CrossApp.Host")!;
        using RawBaseDirectory directory = new();
        FrameAddress[] addresses = host.GetMethod("Exercise")!
            .CreateDelegate<Func<string, bool, FrameAddress[]>>()(directory.Path, reverseRegistration);
        StateModelRegistry models = host.GetMethod("Models")!
            .CreateDelegate<Func<bool, StateModelRegistry>>()(reverseRegistration);
        using SegmentStore segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory.Path, "state"));
        using var schemaFile = RbfFile.OpenReadOnlyExisting(Path.Combine(directory.Path, "schemas.rbf"));
        SchemaStore schemas = new(schemaFile, readOnly: true);
        using StateRevisionStore states = new(segments);
        var initial = RevisionDecoder.ReadSnapshot(states, schemas, addresses[0], models.Snapshot(schemas));
        ObjectStateRecord world = Assert.Single(initial.Objects, r => r.Schema?.SchemaId == "cross.World");
        ObjectStateRecord special = Assert.Single(initial.Objects, r => r.Schema?.SchemaId == "cross.Special");
        ObjectStateRecord dropped = Assert.Single(initial.Objects, r => r.Schema?.SchemaId == "cross.Node");
        foreach (int index in new[] { 1, 4, 6 }) {
            Assert.Empty(states.Read(addresses[index]).LocalObjects);
            Assert.Empty(states.Read(addresses[index]).RemovedObjectIds);
        }
        ObjectVersionRecord childChange = Assert.Single(states.Read(addresses[2]).LocalObjects);
        Assert.Equal(special.Id.Value, childChange.ObjectId);
        Assert.Equal(ObjectVersionKind.Delta, childChange.Kind);
        Assert.DoesNotContain(states.Read(addresses[2]).LocalObjects, r => r.ObjectId == world.Id.Value);
        Assert.Equal(dropped.Id.Value, Assert.Single(states.Read(addresses[3]).RemovedObjectIds));
        Assert.NotEmpty(states.Read(addresses[5]).LocalObjects);
        Assert.All(states.Read(addresses[5]).LocalObjects, r => Assert.Equal(ObjectVersionKind.Delta, r.Kind));
        var final = RevisionDecoder.ReadSnapshot(states, schemas, addresses[^1], models.Snapshot(schemas));
        Assert.DoesNotContain(final.Objects, r => r.Id == dropped.Id);
        Assert.Contains(final.Objects, r => r.Id == special.Id);
        Assert.Contains(final.Objects, r => r.Id == world.Id);
    }

    // Each assembly is emitted independently. Only product/platform dependencies fall through
    // to the default context; source model identities resolve from this test's DLL images.
    private sealed class CrossAssemblyLoadScope : AssemblyLoadContext, IDisposable {
        private readonly Dictionary<string, byte[]> _images = new(StringComparer.Ordinal);

        public CrossAssemblyLoadScope(params GeneratorTestRun[] runs) : base(isCollectible: true) {
            foreach (var run in runs) {
                _images.Add(run.OutputCompilation.AssemblyName!, EmitCrossAssemblyReference(run).Image);
            }
        }

        public Assembly Load(GeneratorTestRun run) => LoadFromAssemblyName(new AssemblyName(run.OutputCompilation.AssemblyName!));

        protected override Assembly? Load(AssemblyName name) {
            if (!_images.TryGetValue(name.Name!, out byte[]? image)) { return null; }
            using MemoryStream stream = new(image);
            return LoadFromStream(stream);
        }

        public void Dispose() => Unload();
    }

    private const string CrossAssemblyOrdinaryLibrary = """
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        namespace CrossOrdinary;
        [DurableType("cross.Node",1)] public partial class Node:IDurableObject {
            [DurableField(1)] public int Value=10000;
            [DurableField(2)] public int A=20000;
            [DurableField(3)] public int B=30000;
            [DurableField(4)] public int C=40000;
            [DurableField(5)] public Node Next;
        }
        [DurableType("cross.Special",1)] public partial class Special:Node {
            [DurableField(1)] public int Extra=50000;
        }
        public static class Catalog {
            public static void Register(IStateModelRegistration models) {
                models.Register(Node.__DurableState.Model);models.Register(Special.__DurableState.Model);
            }
        }
        """;

    private const string CrossAssemblyValueLibrary = """
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        namespace CrossValues;
        [DurableType("cross.Point",1)] public partial struct Point {
            [DurableField(1)] public int X;
            [DurableField(2)] public int Y;
        }
        [DurableType("cross.Key",1)] public readonly partial record struct Key([field:DurableField(1)] int X);
        [DurableType("cross.Record",1)] public readonly partial record struct Record<T>([field:DurableField(1)] T Value);
        [DurableType("cross.Mode",1)] public enum Mode:byte { First=1, Second=2 }
        [DurableType("cross.RemoteBox",1)] public partial class Box<T>:IDurableObject {
            [DurableField(1)] public T Value;
        }
        public static class Catalog {
            public static void Register(IStateModelRegistration models)=>Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
        }
        """;

    private const string CrossAssemblyGraphApplication = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Persistence;
        using Atelia.DurableGraph.Storage;
        using CrossOrdinary;
        using CrossValues;
        namespace CrossApp;
        [DurableType("cross.LocalPoint",1)] public partial struct LocalPoint { [DurableField(1)] public int X; }
        [DurableType("cross.LocalBox",1)] public partial class LocalBox<T>:IDurableObject { [DurableField(1)] public T Value; }
        [DurableType("cross.Inline",1)] public partial struct Inline<T> { [DurableField(1)] public T Value; }
        [DurableType("cross.Phantom",1)] public partial struct Phantom<T> { [DurableField(1)] public int Number; }
        [DurableType("cross.ArrayInline",1)] public partial struct ArrayInline<T> { [DurableField(1)] public T[] Values; }
        [DurableType("cross.LocalNode",1)] public partial class LocalNode:IDurableObject {
            [DurableField(1)] public Box<LocalNode> Back;
        }
        [DurableType("cross.World",1)] public partial class World:IDurableObject {
            [DurableField(1)] public Node Child;
            [DurableField(2)] public readonly Node Alias;
            [DurableField(3)] public Node Dropped;
            [DurableField(4)] public LocalBox<Point> Local;
            [DurableField(5)] public Box<LocalPoint> Remote;
            [DurableField(6)] public Inline<Point> Inline;
            [DurableField(7)] public Phantom<Point> Phantom;
            [DurableField(8)] public ArrayInline<Point> ArrayInline;
            [DurableField(9)] public Point[,] Matrix;
            [DurableField(10)] public Point[,,] Cube;
            [DurableField(11)] public Point[,,,] Quad;
            [DurableField(12)] public List<Point?> Points;
            [DurableField(13)] public Dictionary<Key,Node> Nodes;
            [DurableField(14)] public LocalBox<Mode?> Mode;
            [DurableField(15)] public LocalBox<Record<int>> Record;
            [DurableField(16)] public LocalNode Cycle;
            [DurableField(17)] public LocalBox<Node> OrdinaryParameter;
            public World(Node node) { Child=Alias=node; }
        }
        public static class Catalog {
            public static void Register(IStateModelRegistration models)=>Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
        }
        public static class Host {
            public static StateModelRegistry Models(bool reverse) {
                var models=new StateModelRegistry();
                if(reverse) { Catalog.Register(models);CrossValues.Catalog.Register(models);CrossOrdinary.Catalog.Register(models); }
                else { CrossOrdinary.Catalog.Register(models);CrossValues.Catalog.Register(models);Catalog.Register(models); }
                return models;
            }
            static void Require(bool ok,string why) {if(!ok)throw new InvalidOperationException(why);}
            static World Seed() {
                var node=new Special();node.Next=node;
                var point=new Point{X=12345,Y=54321};
                var w=new World(node){Dropped=new Node(),Local=new(){Value=point},Remote=new(){Value=new(){X=42}},
                    Inline=new(){Value=point},Phantom=new(){Number=17},ArrayInline=new(){Values=Enumerable.Repeat(point,32).ToArray()},
                    Matrix=new Point[1,32],Cube=new Point[1,1,32],Quad=new Point[1,1,1,32],
                    Points=Enumerable.Repeat<Point?>(point,32).ToList(),Nodes=new(){[new Key(7)]=node},
                    Mode=new(){Value=CrossValues.Mode.Second},Record=new(){Value=new(99)},
                    Cycle=new(),OrdinaryParameter=new(){Value=node}};
                for(int i=0;i<32;i++){w.Matrix[0,i]=point;w.Cube[0,0,i]=point;w.Quad[0,0,0,i]=point;}
                w.Points[1]=null;w.Cycle.Back=new(){Value=w.Cycle};return w;
            }
            static void Check(World w,int x) {
                Require(w.Child is Special s && s.Extra==50000 && s.Value==10001,"external derived fields");
                Require(ReferenceEquals(w.Child,w.Alias) && ReferenceEquals(w.Child,w.Child.Next),"readonly/shared/self");
                Require(ReferenceEquals(w.Child,w.Nodes[new Key(7)]) && ReferenceEquals(w.Child,w.OrdinaryParameter.Value),"dictionary and ordinary parameter identity");
                Require(ReferenceEquals(w.Cycle,w.Cycle.Back.Value),"cross-library generic cycle");
                Require(w.Dropped is null && w.Local.Value.X==12345 && w.Remote.Value.X==42,"two-way generic value composition");
                Require(w.Inline.Value.Y==54321 && w.Phantom.Number==17,"dynamic inline and phantom");
                Require(w.Mode.Value==CrossValues.Mode.Second && w.Record.Value.Value==99,"external enum/record parameters");
                Require(w.ArrayInline.Values[0].X==x && w.Matrix[0,0].X==x && w.Cube[0,0,0].X==x && w.Quad[0,0,0,0].X==x,"array ranks");
                Require(w.Points[0]!.Value.X==x && w.Points[1] is null,"nullable external value List");
            }
            public static FrameAddress[] Exercise(string path,bool reverse) {
                var addresses=new List<FrameAddress>();var models=Models(reverse);var world=Seed();
                using(var repo=FixtureGraphRepository.CreateNew(path, models))using(var session=repo.Create(world)) {
                    void Save()=>addresses.Add(session.Commit(new(1000000,1)));
                    Save();Save();world.Child.Value++;Save();world.Dropped=null;Save();
                }
                using(var repo=FixtureGraphRepository.OpenExisting(path, models))using(var session=repo.Load<World>()) {
                    var w=session.World;Check(w,12345);addresses.Add(session.Commit(new(1000000,1)));
                    w.ArrayInline.Values[0].X++;w.Matrix[0,0].X++;w.Cube[0,0,0].X++;w.Quad[0,0,0,0].X++;
                    var p=w.Points[0]!.Value;p.X++;w.Points[0]=p;addresses.Add(session.Commit(new(1000000,1)));
                }
                using(var repo=FixtureGraphRepository.OpenExisting(path, models))using(var session=repo.Load<World>()) {
                    Check(session.World,12346);addresses.Add(session.Commit(new(1000000,1)));
                }
                return addresses.ToArray();
            }
        }
        """;
}
