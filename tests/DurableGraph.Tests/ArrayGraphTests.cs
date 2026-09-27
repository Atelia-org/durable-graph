using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void GeneratedArrayGraphComposesAllRanksAndSlotsAcrossCommitColdReadAndRemoval() {
        ArrayGraphFixture fixture = CompileArrayGraph();
        using RawBaseDirectory directory = new();
        FrameAddress first, unchanged, vectorChange, compositeChange, replaced, removed;
        ObjectId worldId;
        object originalWorld, originalVector;
        using (FixtureGraphRepository repository = FixtureGraphRepository.CreateNew(directory.Path, fixture.Models())) {
            using IDisposable session = (IDisposable)fixture.CreateSession(repository);
            originalWorld = fixture.World(session);
            originalVector = fixture.Vector(originalWorld);
            fixture.Check(originalWorld, 0, false, false);
            first = fixture.Commit(session);
            worldId = fixture.WorldId(session);
            unchanged = fixture.Commit(session);
            fixture.Change(originalWorld, 1);
            vectorChange = fixture.Commit(session);
            fixture.Change(originalWorld, 2);
            compositeChange = fixture.Commit(session);
            fixture.Replace(originalWorld);
            replaced = fixture.Commit(session);
            fixture.Detach(originalWorld);
            removed = fixture.Commit(session);
            Assert.Same(originalWorld, fixture.World(session));
            Assert.Same(originalVector, fixture.Vector(originalWorld));
            fixture.Check(originalWorld, 2, true, true);
        }

        using (SegmentStore segments = SegmentStore.OpenExisting(Path.Combine(directory.Path, "state")))
        using (IRbfFile schemaFile = RbfFile.OpenExisting(Path.Combine(directory.Path, "schemas.rbf"))) {
            using StateRevisionStore states = new(segments);
            SchemaStore schemas = new(schemaFile);
            StateRevision initial = states.Read(first);
            Assert.All(initial.LocalObjects, row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
            Assert.Empty(states.Read(unchanged).LocalObjects);
            Assert.Empty(states.Read(unchanged).RemovedObjectIds);
            ObjectVersionRecord vectorDelta = Assert.Single(states.Read(vectorChange).LocalObjects);
            Assert.Equal(ObjectVersionKind.Delta, vectorDelta.Kind);
            Assert.NotEqual(worldId.Value, vectorDelta.ObjectId);
            Assert.Equal(2, states.ReadObjectVersionChain(vectorChange, vectorDelta.ObjectId).Records.Count);
            Assert.Equal(initial.LocalObjectIds.Order(), states.ReadLiveObjectHeadMap(compositeChange).Keys.Order());
            Assert.All(states.Read(compositeChange).LocalObjects, row => Assert.NotEqual(worldId.Value, row.ObjectId));
            Assert.NotEmpty(states.Read(compositeChange).LocalObjects);
            Assert.Empty(states.Read(compositeChange).RemovedObjectIds);
            uint hyperId = Assert.Single(initial.LocalObjects, row => {
                ArrayLayout? layout = BaseObjectBodyCodec.Decode(row.Body, schemas).Layout.Array;
                return layout?.Rank == 4 && layout.ElementSlot.TypeTag == TypeTag.String;
            }).ObjectId;
            Assert.Equal(ObjectVersionKind.Delta,
                Assert.Single(states.Read(compositeChange).LocalObjects, row => row.ObjectId == hyperId).Kind);

            StateRevision replacement = states.Read(replaced);
            uint retired = Assert.Single(replacement.RemovedObjectIds);
            Assert.Contains(retired, initial.LocalObjectIds);
            ObjectVersionRecord newArray = Assert.Single(replacement.LocalObjects, row => row.Kind == ObjectVersionKind.Base);
            Assert.True(newArray.ObjectId > initial.LocalObjectIds.Max());
            Assert.Equal(2, replacement.LocalObjects.Count); // World reference slot and the new array.
            Assert.Equal(2, states.Read(removed).RemovedObjectIds.Count); // Cyclic Node[] plus its sole Node.
            Assert.All(states.Read(removed).RemovedObjectIds, id => Assert.Contains(id, initial.LocalObjectIds));
            Assert.Contains(retired, states.ReadLiveObjectHeadMap(first).Keys);
            Assert.DoesNotContain(retired, states.ReadLiveObjectHeadMap(removed).Keys);

            fixture.Check(fixture.LoadOld(states, schemas, first, worldId), 0, false, false);
            fixture.Check(fixture.LoadOld(states, schemas, vectorChange, worldId), 1, false, false);
            fixture.Check(fixture.LoadOld(states, schemas, compositeChange, worldId), 2, false, false);
            fixture.Check(fixture.LoadOld(states, schemas, removed, worldId), 2, true, true);
        }

        FrameAddress reopened;
        using (FixtureGraphRepository repository = FixtureGraphRepository.OpenExisting(directory.Path, fixture.Models())) {
            using IDisposable session = (IDisposable)fixture.LoadSession(repository);
            object world = fixture.World(session);
            Assert.NotSame(originalWorld, world);
            Assert.NotSame(originalVector, fixture.Vector(world));
            fixture.Check(world, 2, true, true);
            reopened = fixture.Commit(session);
            Assert.Same(world, fixture.World(session));
        }
        using (SegmentStore segments = SegmentStore.OpenExisting(Path.Combine(directory.Path, "state"))) {
            using StateRevisionStore states = new(segments);
            Assert.Equal(removed, states.Read(reopened).ParentRevisionAddress);
            Assert.Empty(states.Read(reopened).LocalObjects);
            Assert.Empty(states.Read(reopened).RemovedObjectIds);
        }
    }

    [Fact]
    public void GeneratedExistingArrayPolicyChoosesSparseDeltaAndDenseBaseWithExactPayloadAccounting() {
        ArrayGraphFixture fixture = CompileArrayGraph();
        using RawBaseDirectory directory = new();
        FrameAddress first, sparse, dense;
        ObjectId worldId;
        using (FixtureGraphRepository repository = FixtureGraphRepository.CreateNew(directory.Path, fixture.Models())) {
            using IDisposable session = (IDisposable)fixture.CreateSession(repository);
            object world = fixture.World(session);
            object array = fixture.Vector(world);
            first = fixture.Commit(session);
            worldId = fixture.WorldId(session);
            fixture.Change(world, 1);
            sparse = fixture.Commit(session);
            fixture.Fill(world, 9);
            dense = fixture.Commit(session);
            Assert.Same(array, fixture.Vector(world));
        }

        using SegmentStore segments = SegmentStore.OpenExisting(Path.Combine(directory.Path, "state"));
        using IRbfFile schemaFile = RbfFile.OpenExisting(Path.Combine(directory.Path, "schemas.rbf"));
        using StateRevisionStore states = new(segments);
        SchemaStore schemas = new(schemaFile);
        ObjectVersionRecord delta = Assert.Single(states.Read(sparse).LocalObjects);
        ObjectVersionRecord replacementBase = Assert.Single(states.Read(dense).LocalObjects);
        Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
        Assert.Equal(ObjectVersionKind.Base, replacementBase.Kind);
        Assert.Equal(delta.ObjectId, replacementBase.ObjectId);
        Assert.Contains(delta.ObjectId, states.Read(first).LocalObjectIds);
        Assert.Empty(states.Read(dense).RemovedObjectIds);
        Assert.Equal(2, states.ReadObjectVersionChain(sparse, delta.ObjectId).Records.Count);
        ObjectVersionChain baseChain = states.ReadObjectVersionChain(dense, delta.ObjectId);
        Assert.Single(baseChain.Records);

        DecodedBaseObjectBody decoded = BaseObjectBodyCodec.Decode(replacementBase.Body, schemas);
        Assert.Equal(ObjectStateKind.Array, decoded.Kind);
        // This small directory has a one-byte ID; v4 plus that ID is exactly two bytes.
        Assert.InRange(decoded.RepresentationId.Value, 2u, 127u);
        Assert.Equal(2, replacementBase.Body.Length - decoded.Body.Length);
        Assert.Equal(ObjectVersionPayloadSize.GetBasePayloadBytes(replacementBase.Body.Length), baseChain.ReconstructionPayloadBytes);
        Assert.True(baseChain.ReconstructionPayloadBytes > ObjectVersionPayloadSize.GetBasePayloadBytes(decoded.Body.Length));
        fixture.Check(fixture.LoadOld(states, schemas, first, worldId), 0, false, false);
        fixture.Check(fixture.LoadOld(states, schemas, sparse, worldId), 1, false, false);
        fixture.CheckFilled(fixture.LoadOld(states, schemas, dense, worldId), 9);
    }

    [Fact]
    public void GeneratedArrayPreparedStateOwnsElementsAndNestedInlineCopies() {
        ArrayGraphFixture fixture = CompileArrayGraph();
        using RawBaseDirectory directory = new();
        using RawBaseDirectory schemaDirectory = new();
        Directory.CreateDirectory(schemaDirectory.Path);
        string schemaPath = Path.Combine(schemaDirectory.Path, "schemas.rbf");
        FrameAddress first;
        ObjectId worldId;
        using (SegmentStore segments = SegmentStore.CreateNew(directory.Path))
        using (IRbfFile schemaFile = RbfFile.CreateNew(schemaPath)) {
            using StateRevisionStore states = new(segments);
            SchemaStore schemas = new(schemaFile);
            object world = fixture.NewWorld();
            FixturePreparedWorldRevision prepared = fixture.PrepareNew(states, schemas, world);
            worldId = prepared.WorldId;
            fixture.Change(world, 1);
            fixture.Change(world, 2);
            fixture.Replace(world);
            fixture.Detach(world);
            first = states.Append(prepared.Revision);
        }
        using (SegmentStore segments = SegmentStore.OpenExisting(directory.Path))
        using (IRbfFile schemaFile = RbfFile.OpenExisting(schemaPath)) {
            using StateRevisionStore states = new(segments);
            SchemaStore schemas = new(schemaFile);
            fixture.Check(fixture.LoadOld(states, schemas, first, worldId), 0, false, false);
        }
    }

    [Theory]
    [InlineData(0)] // Actual Derived[] assigned to a declared Node[].
    [InlineData(1)] // Nonzero lower bound on a rectangular array.
    public void GeneratedArrayUnsupportedRuntimeShapeOrCovarianceDoesNotAdvanceSession(int failure) {
        ArrayGraphFixture fixture = CompileArrayGraph();
        using RawBaseDirectory directory = new();
        FrameAddress first, recovered;
        using (FixtureGraphRepository repository = FixtureGraphRepository.CreateNew(directory.Path, fixture.Models())) {
            using IDisposable session = (IDisposable)fixture.CreateSession(repository);
            object world = fixture.World(session);
            first = fixture.Commit(session);
            object old = fixture.Break(world, failure);
            Assert.ThrowsAny<Exception>(() => fixture.Commit(session));
            Assert.Equal(first, repository.HeadRevisionAddress);
            Assert.False(repository.IsFaulted);
            fixture.Repair(world, old, failure);
            recovered = fixture.Commit(session);
            fixture.Check(world, 0, false, false);
        }
        using SegmentStore segments = SegmentStore.OpenExisting(Path.Combine(directory.Path, "state"));
        using StateRevisionStore states = new(segments);
        StateRevision revision = states.Read(recovered);
        Assert.Equal(first, revision.ParentRevisionAddress);
        Assert.Empty(revision.LocalObjects);
        Assert.Empty(revision.RemovedObjectIds);
    }

    private static ArrayGraphFixture CompileArrayGraph() {
        GeneratorTestRun run = RunGenerator(ArrayGraphSource);
        AssertSchemaOnlyCompiles(run);
        return new(EmitAndLoad(run.OutputCompilation).GetType("ArrayGraph.Host")!);
    }

    private sealed class ArrayGraphFixture(Type host) {
        private T Method<T>(string name) where T : Delegate => host.GetMethod(name)!.CreateDelegate<T>();
        public object NewWorld() => Method<Func<object>>("NewWorld")();
        public StateModelRegistry Models() => Method<Func<StateModelRegistry>>("Models")();
        public object CreateSession(FixtureGraphRepository repository) => Method<Func<FixtureGraphRepository, object>>("CreateSession")(repository);
        public object LoadSession(FixtureGraphRepository repository) => Method<Func<FixtureGraphRepository, object>>("LoadSession")(repository);
        public object World(object session) => Method<Func<object, object>>("World")(session);
        public object Vector(object world) => Method<Func<object, object>>("Vector")(world);
        public ObjectId WorldId(object session) => Method<Func<object, ObjectId>>("WorldId")(session);
        public FrameAddress Commit(object session) => Method<Func<object, FrameAddress>>("Commit")(session);
        public void Change(object world, int stage) => Method<Action<object, int>>("Change")(world, stage);
        public void Fill(object world, int value) => Method<Action<object, int>>("Fill")(world, value);
        public void CheckFilled(object world, int value) => Method<Action<object, int>>("CheckFilled")(world, value);
        public void Replace(object world) => Method<Action<object>>("Replace")(world);
        public void Detach(object world) => Method<Action<object>>("Detach")(world);
        public object Break(object world, int failure) => Method<Func<object, int, object>>("Break")(world, failure);
        public void Repair(object world, object old, int failure) => Method<Action<object, object, int>>("Repair")(world, old, failure);
        public void Check(object world, int stage, bool replaced, bool detached) =>
            Method<Action<object, int, bool, bool>>("Check")(world, stage, replaced, detached);
        public FixturePreparedWorldRevision PrepareNew(StateRevisionStore states, SchemaStore schemas, object world) =>
            Method<Func<StateRevisionStore, SchemaStore, object, FixturePreparedWorldRevision>>("PrepareNew")(states, schemas, world);
        public object LoadOld(StateRevisionStore states, SchemaStore schemas, FrameAddress address, ObjectId worldId) =>
            Method<Func<StateRevisionStore, SchemaStore, FrameAddress, ObjectId, object>>("LoadOld")(states, schemas, address, worldId);
    }

    private const string ArrayGraphSource = """
        using System;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Persistence;
        using Atelia.DurableGraph.Storage;
        namespace ArrayGraph;

        [DurableType("array.graph.point", 1)]
        public partial struct Point {
            [DurableField(1)] public int X;
            [DurableField(2)] public World? Owner;
            [DurableField(3)] public int[]? Data;
        }
        [DurableType("array.graph.pair", 1)]
        public partial struct Pair<TLeft, TRight> {
            [DurableField(1)] public TLeft Left;
            [DurableField(2)] public TRight Right;
        }
        [DurableType("array.graph.holder", 1)]
        public partial class Holder<T> : IDurableObject {
            [DurableField(1)] public T[]? Items;
            [DurableField(2)] public T Value = default!;
        }
        [DurableType("array.graph.box", 1)]
        public partial class Box<T> : IDurableObject { [DurableField(1)] public T Value = default!; }
        [DurableType("array.graph.link", 1)]
        public partial struct Link { [DurableField(1)] public Node[]? Back; }
        [DurableType("array.graph.node", 1)]
        public partial class Node : IDurableObject {
            [DurableField(1)] public int Value;
            [DurableField(2)] public Link Link;
        }
        [DurableType("array.graph.derived", 1)]
        public partial class Derived : Node { }
        [DurableType("array.graph.world", 1)]
        public partial class World : IDurableObject {
            [DurableField(1)] public int[] Numbers = new int[128];
            [DurableField(2)] public int[,] Matrix = new int[2,3];
            [DurableField(3)] public double[,,] Cube = new double[2,1,2];
            [DurableField(4)] public string?[,,,] Hyper = new string?[1,1,1,128];
            [DurableField(5)] public Point[] Points = new Point[2];
            [DurableField(6)] public Pair<int,string>[] Pairs = new Pair<int,string>[1];
            [DurableField(7)] public Holder<int> IntHolder = new();
            [DurableField(8)] public Holder<int[]> ArrayHolder = new();
            [DurableField(9)] public Box<int[]> Box = new();
            [DurableField(10)] public int[][] Jagged = new int[3][];
            [DurableField(11)] public Point[][,] Mixed = new Point[1][,];
            [DurableField(12)] public int[] EmptyA = new int[0];
            [DurableField(13)] public int[] EmptyB = new int[0];
            [DurableField(14)] public int[] Replaceable = new int[] { 31,32 };
            [DurableField(15)] public Node[]? Island;
            [DurableField(16)] public Point[,] EmptyMatrix = new Point[2,0];
            [DurableField(17)] public int[,,] EmptyCube = new int[0,2,3];
            [DurableField(18)] public int[,,,] EmptyHyper = new int[1,2,0,4];
            public World() {
                Numbers[17]=7;
                Matrix[1,2]=11;
                Cube[1,0,1]=12;
                string first = new string('s', 4), equal = new string('s', 4);
                Hyper[0,0,0,0]=first; Hyper[0,0,0,1]=first; Hyper[0,0,0,2]=equal; Hyper[0,0,0,3]=string.Empty; Hyper[0,0,0,4]=first;
                Points[0]=new Point { X=21, Owner=this, Data=Numbers };
                Points[1]=new Point { X=22, Owner=this, Data=Numbers };
                Pairs[0]=new Pair<int,string> { Left=23, Right=first };
                IntHolder.Items=Numbers; IntHolder.Value=42;
                Jagged[0]=Numbers; Jagged[1]=Numbers; Jagged[2]=EmptyA;
                ArrayHolder.Items=Jagged; ArrayHolder.Value=Numbers; Box.Value=Numbers;
                Mixed[0]=new Point[1,1]; Mixed[0][0,0]=Points[0];
                Island=new Node[1]; Island[0]=new Node { Value=51, Link=new Link { Back=Island } };
            }
            public void Check(int stage, bool replaced, bool detached) {
                if (Numbers.Length!=128 || Numbers[17]!=(stage==0?7:8) || Matrix.GetLength(0)!=2 || Matrix.GetLength(1)!=3 ||
                    Matrix[1,2]!=(stage<2?11:111) || Cube.GetLength(0)!=2 || Cube.GetLength(1)!=1 || Cube.GetLength(2)!=2 ||
                    Cube[1,0,1]!=(stage<2?12:112)) throw new Exception("Scalar array content or shape changed");
                if (Hyper.Rank!=4 || Hyper.GetLength(3)!=128 || !ReferenceEquals(Hyper[0,0,0,0],Hyper[0,0,0,1]) ||
                    ReferenceEquals(Hyper[0,0,0,0],Hyper[0,0,0,2]) || Hyper[0,0,0,0]!=Hyper[0,0,0,2] ||
                    !ReferenceEquals(Hyper[0,0,0,3],string.Empty) ||
                    !ReferenceEquals(Hyper[0,0,0,4],Hyper[0,0,0,stage<2?0:2])) throw new Exception("String array identity changed");
                if (Points[0].X!=(stage<2?21:121) || !ReferenceEquals(Points[0].Owner,this) ||
                    !ReferenceEquals(Points[0].Data,Numbers) || !ReferenceEquals(Points[1].Owner,this) ||
                    Pairs[0].Left!=(stage<2?23:123) || !ReferenceEquals(Pairs[0].Right,Hyper[0,0,0,0]))
                    throw new Exception("Inline/generic array element changed");
                if (!ReferenceEquals(IntHolder.Items,Numbers) || IntHolder.Value!=42 ||
                    !ReferenceEquals(ArrayHolder.Items,Jagged) || !ReferenceEquals(ArrayHolder.Value,Numbers) ||
                    !ReferenceEquals(Box.Value,Numbers) || !ReferenceEquals(Jagged[0],Numbers) ||
                    !ReferenceEquals(Jagged[1],Numbers) || !ReferenceEquals(Jagged[2],EmptyA))
                    throw new Exception("Generic or jagged sharing changed");
                if (Mixed.Length!=1 || Mixed[0].Rank!=2 || Mixed[0][0,0].X!=21 ||
                    !ReferenceEquals(Mixed[0][0,0].Owner,this) || !ReferenceEquals(Mixed[0][0,0].Data,Numbers))
                    throw new Exception("Mixed rank graph changed");
                if (ReferenceEquals(EmptyA,EmptyB) || EmptyA.Length!=0 || EmptyB.Length!=0 ||
                    EmptyMatrix.GetLength(0)!=2 || EmptyMatrix.GetLength(1)!=0 ||
                    EmptyCube.GetLength(0)!=0 || EmptyCube.GetLength(1)!=2 || EmptyCube.GetLength(2)!=3 ||
                    EmptyHyper.GetLength(0)!=1 || EmptyHyper.GetLength(1)!=2 || EmptyHyper.GetLength(2)!=0 || EmptyHyper.GetLength(3)!=4)
                    throw new Exception("Empty array identity or complete shape changed");
                if (Replaceable.Length!=(replaced?3:2) || Replaceable[0]!=(replaced?91:31)) throw new Exception("Replacement changed");
                if (detached ? Island!=null : Island==null || Island[0].Value!=51 || !ReferenceEquals(Island[0].Link.Back,Island))
                    throw new Exception("Struct to array cycle changed");
            }
        }
        public static class Host {
            public static StateModelRegistry Models() {
                StateModelRegistry models=new();
                Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
                return models;
            }
            public static object NewWorld() => new World();
            public static object CreateSession(FixtureGraphRepository repository) => repository.Create(new World());
            public static object LoadSession(FixtureGraphRepository repository) => repository.Load<World>();
            public static object World(object session) => ((FixtureGraphSession<World>)session).World;
            public static object Vector(object world) => ((World)world).Numbers;
            public static ObjectId WorldId(object session) => ((FixtureGraphSession<World>)session).WorldId!.Value;
            public static FrameAddress Commit(object session) => ((FixtureGraphSession<World>)session).Commit(new(1000000,1));
            public static void Check(object world,int stage,bool replaced,bool detached) => ((World)world).Check(stage,replaced,detached);
            public static void Change(object world,int stage) {
                World value=(World)world;
                if (stage==1) { value.Numbers[17]=8; return; }
                value.Matrix[1,2]=111; value.Cube[1,0,1]=112; value.Hyper[0,0,0,4]=value.Hyper[0,0,0,2];
                value.Points[0].X=121; value.Pairs[0].Left=123;
            }
            public static void Replace(object world) => ((World)world).Replaceable=new int[] {91,92,93};
            public static void Fill(object world,int value) => Array.Fill(((World)world).Numbers,value);
            public static void CheckFilled(object world,int value) {
                World root=(World)world;
                foreach(int element in root.Numbers) if(element!=value) throw new Exception("Dense Base did not retain all elements");
                if(!ReferenceEquals(root.Numbers,root.Box.Value) || !ReferenceEquals(root.Numbers,root.Jagged[0]))
                    throw new Exception("Dense Base changed shared identity");
            }
            public static void Detach(object world) => ((World)world).Island=null;
            public static object Break(object world,int failure) {
                World value=(World)world;
                if(failure==0) { object old=value.Island!; value.Island=new Derived[0]; return old; }
                object matrix=value.Matrix;
                value.Matrix=(int[,])Array.CreateInstance(typeof(int),new[]{2,3},new[]{1,1});
                return matrix;
            }
            public static void Repair(object world,object old,int failure) {
                if(failure==0) ((World)world).Island=(Node[])old;
                else ((World)world).Matrix=(int[,])old;
            }
            public static FixturePreparedWorldRevision PrepareNew(StateRevisionStore states,SchemaStore schemas,object world) =>
                FixtureLoadedWorld.PrepareNew(states,schemas,(World)world,Models(),new(1000000,1));
            public static object LoadOld(StateRevisionStore states,SchemaStore schemas,FrameAddress address,ObjectId worldId) =>
                FixtureLoadedWorld.Load<World>(states,schemas,address,worldId,Models()).World;
        }
        """;
}
