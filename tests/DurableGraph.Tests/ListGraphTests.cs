using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void GeneratedListGraphPreservesFrozenContentSharingCyclesResizeAndRemoval() {
        GeneratorTestRun run = RunGenerator(ListGraphSource);
        AssertSchemaOnlyCompiles(run);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("ListGraph.Host")!;
        T Method<T>(string name) where T : Delegate => (T)host.GetMethod(name)!.CreateDelegate(typeof(T));
        var create = Method<Func<FixtureGraphRepository, object>>("Create");
        var load = Method<Func<FixtureGraphRepository, object>>("Load");
        var world = Method<Func<object, object>>("World");
        var numbers = Method<Func<object, object>>("Numbers");
        var commit = Method<Func<object, FrameAddress>>("Commit");
        var change = Method<Action<object, int>>("Change");
        var check = Method<Action<object, int>>("Check");
        using RawBaseDirectory directory = new();
        FrameAddress first, unchanged, appended, childOnly, replaced, removed;
        object originalWorld, originalNumbers;
        using (FixtureGraphRepository repository = FixtureGraphRepository.CreateNew(directory.Path, Method<Func<StateModelRegistry>>("Models")())) {
            using IDisposable session = (IDisposable)create(repository);
            originalWorld = world(session);
            originalNumbers = numbers(originalWorld);
            check(originalWorld, 0);
            first = commit(session);
            change(originalWorld, 0); // Capacity is outside persistent content.
            unchanged = commit(session);
            change(originalWorld, 1);
            appended = commit(session);
            check(originalWorld, 1);
            change(originalWorld, 2);
            childOnly = commit(session);
            change(originalWorld, 3);
            replaced = commit(session);
            change(originalWorld, 4);
            removed = commit(session);
            check(originalWorld, 4);
            Assert.Same(originalWorld, world(session));
            Assert.Same(originalNumbers, numbers(originalWorld));
        }
        using (SegmentStore segments = SegmentStore.OpenExisting(Path.Combine(directory.Path, "state")))
        using (IRbfFile file = RbfFile.OpenExisting(Path.Combine(directory.Path, "schemas.rbf"))) {
            using StateRevisionStore states = new(segments);
            SchemaStore schemas = new(file);
            Assert.Empty(states.Read(unchanged).LocalObjects);
            ObjectVersionRecord resize = Assert.Single(states.Read(appended).LocalObjects);
            Assert.Equal(ObjectVersionKind.Delta, resize.Kind);
            Assert.Equal(ObjectStateKind.List, BaseObjectBodyCodec.Decode(
                Assert.Single(states.Read(first).LocalObjects, row => row.ObjectId == resize.ObjectId).Body, schemas).Kind);
            ObjectVersionRecord child = Assert.Single(states.Read(childOnly).LocalObjects);
            Assert.NotEqual(resize.ObjectId, child.ObjectId);
            Assert.Equal(ObjectStateKind.Durable, BaseObjectBodyCodec.Decode(
                Assert.Single(states.Read(first).LocalObjects, row => row.ObjectId == child.ObjectId).Body, schemas).Kind);
            StateRevision replacement = states.Read(replaced);
            Assert.Single(replacement.RemovedObjectIds);
            Assert.Single(replacement.LocalObjects, row => row.Kind == ObjectVersionKind.Base);
            Assert.Equal(2, states.Read(removed).RemovedObjectIds.Count); // List and cyclic Node island.
            Assert.Equal(states.ReadLiveObjectHeadMap(first).Count - 2, states.ReadLiveObjectHeadMap(removed).Count);
        }
        FrameAddress resumed;
        using (FixtureGraphRepository repository = FixtureGraphRepository.OpenExisting(directory.Path, Method<Func<StateModelRegistry>>("Models")())) {
            using IDisposable session = (IDisposable)load(repository);
            check(world(session), 4);
            Assert.NotSame(originalWorld, world(session));
            Assert.NotSame(originalNumbers, numbers(world(session)));
            resumed = commit(session);
        }
        using (SegmentStore segments = SegmentStore.OpenExisting(Path.Combine(directory.Path, "state"))) {
            using StateRevisionStore states = new(segments);
            Assert.Empty(states.Read(resumed).LocalObjects);
            Assert.Empty(states.Read(resumed).RemovedObjectIds);
        }
    }

    [Fact]
    public void GeneratedListPreparedGraphOwnsNestedInlineAndReferenceValues() {
        GeneratorTestRun run = RunGenerator(ListGraphSource);
        AssertSchemaOnlyCompiles(run);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("ListGraph.Host")!;
        using RawBaseDirectory directory = new();
        Directory.CreateDirectory(directory.Path);
        using SegmentStore segments = SegmentStore.CreateNew(Path.Combine(directory.Path, "state"));
        using IRbfFile file = RbfFile.CreateNew(Path.Combine(directory.Path, "schemas.rbf"));
        using StateRevisionStore states = new(segments);
        SchemaStore schemas = new(file);
        var freeze = (Func<StateRevisionStore, SchemaStore, FixturePreparedWorldRevision>)host.GetMethod("PrepareAndMutate")!.CreateDelegate(
            typeof(Func<StateRevisionStore, SchemaStore, FixturePreparedWorldRevision>));
        FixturePreparedWorldRevision prepared = freeze(states, schemas);
        FrameAddress address = states.Append(prepared.Revision);
        var verify = (Action<StateRevisionStore, SchemaStore, FrameAddress, ObjectId>)host.GetMethod("LoadAndCheck")!.CreateDelegate(
            typeof(Action<StateRevisionStore, SchemaStore, FrameAddress, ObjectId>));
        verify(states, schemas, address, prepared.WorldId);
    }

    private const string ListGraphSource = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Persistence;
        using Atelia.DurableGraph.Storage;
        namespace ListGraph;
        [DurableType("list.graph.point",1)] public partial struct Point {
            [DurableField(1)] public int X;
            [DurableField(2)] public World? Owner;
            [DurableField(3)] public List<int>? Data;
        }
        [DurableType("list.graph.pair",1)] public partial struct Pair<T,U> {
            [DurableField(1)] public T Left;
            [DurableField(2)] public U Right;
        }
        [DurableType("list.graph.recursive",1)] public partial struct Recursive {
            [DurableField(1)] public List<Recursive>? Children;
        }
        [DurableType("list.graph.box",1)] public partial class Box<T> : IDurableObject {
            [DurableField(1)] public T Value = default!;
        }
        [DurableType("list.graph.node",1)] public partial class Node : IDurableObject {
            [DurableField(1)] public int Value;
            [DurableField(2)] public List<Node>? Back;
        }
        [DurableType("list.graph.derived",1)] public partial class Derived : Node { }
        [DurableType("list.graph.world",1)] public partial class World : IDurableObject {
            [DurableField(1)] public List<int> Numbers = Enumerable.Range(0,128).ToList();
            [DurableField(2)] public List<List<int>> Nested = new();
            [DurableField(3)] public List<Point> Points = new();
            [DurableField(4)] public List<Pair<int,string>> Pairs = new();
            [DurableField(5)] public List<Point[,]> Matrices = new();
            [DurableField(6)] public List<int>[] Arrays = new List<int>[1];
            [DurableField(7)] public Box<List<Point>> Box = new();
            [DurableField(8)] public Recursive Recursive;
            [DurableField(9)] public List<Node>? Island;
            [DurableField(10)] public List<int> Replaceable = new() {5,6};
            [DurableField(11)] public List<string> Strings = new();
            public World() {
                Nested.Add(Numbers); Nested.Add(Numbers); Arrays[0]=Numbers;
                Points.Add(new Point {X=7,Owner=this,Data=Numbers}); Box.Value=Points;
                string a=new string('x',3),b=new string('x',3);
                Strings.Add(a); Strings.Add(a); Strings.Add(b); Strings.Add(string.Empty);
                Pairs.Add(new Pair<int,string> {Left=8,Right=a});
                Matrices.Add(new Point[,] {{Points[0]}});
                Recursive.Children=new List<Recursive>(); Recursive.Children.Add(Recursive);
                Island=new List<Node>(); Island.Add(new Derived {Value=9,Back=Island});
            }
            public void Check(int stage) {
                if(Numbers.Count!=(stage==0?128:129) || Numbers[0]!=0 || (stage>0 && Numbers[128]!=0)) throw new Exception("resize");
                if(!ReferenceEquals(Nested[0],Numbers)||!ReferenceEquals(Nested[1],Numbers)||!ReferenceEquals(Arrays[0],Numbers)) throw new Exception("sharing");
                if(Points[0].X!=7||!ReferenceEquals(Points[0].Owner,this)||!ReferenceEquals(Points[0].Data,Numbers)||!ReferenceEquals(Box.Value,Points)) throw new Exception("inline");
                if(Pairs[0].Left!=8||!ReferenceEquals(Pairs[0].Right,Strings[0])||!ReferenceEquals(Strings[0],Strings[1])||ReferenceEquals(Strings[0],Strings[2])||!ReferenceEquals(Strings[3],string.Empty)) throw new Exception("strings");
                if(Matrices[0][0,0].X!=7||!ReferenceEquals(Matrices[0][0,0].Owner,this)) throw new Exception("array composition");
                if(!ReferenceEquals(Recursive.Children,Recursive.Children![0].Children)) throw new Exception("recursive struct");
                if(stage==4 ? Island!=null : Island==null||Island[0] is not Derived||Island[0].Value!=(stage>=2?10:9)||!ReferenceEquals(Island[0].Back,Island)) throw new Exception("cycle");
                if(Replaceable.Count!=2||Replaceable[0]!=5||Replaceable[1]!=6) throw new Exception("replacement");
            }
        }
        public static class Host {
            public static StateModelRegistry Models() { StateModelRegistry m=new(); Atelia.DurableGraph.Generated.DurableDefinitions.Register(m); return m; }
            public static object Create(FixtureGraphRepository r)=>r.Create(new World());
            public static object Load(FixtureGraphRepository r)=>r.Load<World>();
            public static object World(object s)=>((FixtureGraphSession<World>)s).World;
            public static object Numbers(object w)=>((World)w).Numbers;
            public static FrameAddress Commit(object s)=>((FixtureGraphSession<World>)s).Commit(new(1000000,1));
            public static void Check(object w,int stage)=>((World)w).Check(stage);
            public static void Change(object w,int stage) {
                World v=(World)w;
                if(stage==0) v.Numbers.Capacity+=100;
                if(stage==1) v.Numbers.Add(0);
                if(stage==2) v.Island![0].Value=10;
                if(stage==3) v.Replaceable=new List<int>(v.Replaceable);
                if(stage==4) v.Island=null;
            }
            public static FixturePreparedWorldRevision PrepareAndMutate(StateRevisionStore states,SchemaStore schemas) {
                World world=new();
                FixturePreparedWorldRevision result=FixtureLoadedWorld.PrepareNew(states,schemas,world,Models(),new(1000000,1));
                world.Numbers.Clear(); world.Points[0]=new Point {X=999}; world.Strings.Clear();
                return result;
            }
            public static void LoadAndCheck(StateRevisionStore states,SchemaStore schemas,FrameAddress address,ObjectId id) =>
                FixtureLoadedWorld.Load<World>(states,schemas,address,id,Models()).World.Check(0);
        }
        """;
}
