using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Persistence;

using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

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
        using (RepositoryResources segments = RepositoryResources.OpenExisting(directory.Path))
        {
            var states = segments.GraphStore;
            Assert.Empty(DefaultRecords(states, unchanged));
            ObjectVersionRecord resize = Assert.Single(DefaultRecords(states, appended));
            Assert.Equal(ObjectVersionKind.Delta, resize.Kind);
            Assert.Equal(ObjectStateKind.List, DefaultLayout(states, first, resize.ObjectId).Kind);
            ObjectVersionRecord child = Assert.Single(DefaultRecords(states, childOnly));
            Assert.NotEqual(resize.ObjectId, child.ObjectId);
            Assert.Equal(ObjectStateKind.Durable, DefaultLayout(states, first, child.ObjectId).Kind);
            var replacement = states.ReadGraph(replaced);
            Assert.Single(DefaultRemoved(states, replacement.Address));
            ObjectVersionRecord newList = Assert.Single(DefaultRecords(states, replacement.Address),
                row => row.Kind == ObjectVersionKind.Base && row.ObjectId != child.ObjectId &&
                    DefaultLayout(states, replaced, row.ObjectId).Kind == ObjectStateKind.List);
            Assert.NotEqual(resize.ObjectId, newList.ObjectId);
            Assert.Equal(2, DefaultRemoved(states, removed).Count); // List and cyclic Node island.
            Assert.Equal(states.ReadGraph(first).ObjectHeads.Count - 2, states.ReadGraph(removed).ObjectHeads.Count);
        }
        FrameAddress resumed;
        using (FixtureGraphRepository repository = FixtureGraphRepository.OpenExisting(directory.Path, Method<Func<StateModelRegistry>>("Models")())) {
            using IDisposable session = (IDisposable)load(repository);
            check(world(session), 4);
            Assert.NotSame(originalWorld, world(session));
            Assert.NotSame(originalNumbers, numbers(world(session)));
            resumed = commit(session);
        }
        using (RepositoryResources segments = RepositoryResources.OpenExisting(directory.Path)) {
            var states = segments.GraphStore;
            Assert.Empty(DefaultRecords(states, resumed));
            Assert.Empty(DefaultRemoved(states, resumed));
        }
    }

    [Fact]
    public void GeneratedListPreparedGraphOwnsNestedInlineAndReferenceValues() {
        GeneratorTestRun run = RunGenerator(ListGraphSource);
        AssertSchemaOnlyCompiles(run);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("ListGraph.Host")!;
        object world = host.GetMethod("NewWorld")!.CreateDelegate<Func<object>>()();
        var models = host.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>();
        var mutate = host.GetMethod("MutatePrepared")!.CreateDelegate<Action<object>>();
        var check = host.GetMethod("Check")!.CreateDelegate<Action<object, int>>();
        using RawBaseDirectory directory = new();
        bool mutated = false;
        using (Repository repository = Repository.CreateNew(directory.Path, models())) {
            repository.OperationStageObserver = checkpoint => {
                if (checkpoint != RepositoryOperationStage.AfterPrepare) { return; }
                mutate(world);
                mutated = true;
            };
            using BranchCheckout checkout = repository.CreateSharedBranch("main", (IDurableObject)world);
            Assert.True(mutated);
            Assert.Same(world, checkout.State);
        }
        using Repository reopened = Repository.OpenReadOnlyExisting(directory.Path, models());
        check(reopened.ReadState(reopened.GetHead("main")), 0);
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
        using Atelia.FrameStore;
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
            public static object NewWorld() => new World();
            public static void MutatePrepared(object value) {
                World world=(World)value;
                world.Numbers.Clear(); world.Points[0]=new Point {X=999}; world.Strings.Clear();
            }
        }
        """;
}
