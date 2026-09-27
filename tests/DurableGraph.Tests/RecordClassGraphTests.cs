using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void RecordClassGraphsPreserveIdentityAndCompareOnlyFrozenPersistentState() {
        GeneratorTestRun run = RunGenerator(RecordClassGraphSource);
        AssertSchemaOnlyCompiles(run);
        var assembly = EmitAndLoad(run.OutputCompilation);
        Type host = assembly.GetType("RecordClasses.Host")!;
        using RawBaseDirectory directory = new();
        FrameAddress[] addresses = host.GetMethod("Run")!.CreateDelegate<Func<string, FrameAddress[]>>()(directory.Path);
        StateModelRegistry models = host.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>()();
        using SegmentStore segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory.Path, "state"));
        using var schemaFile = RbfFile.OpenReadOnlyExisting(Path.Combine(directory.Path, "schemas.rbf"));
        SchemaStore schemas = new(schemaFile, readOnly: true);
        using StateRevisionStore states = new(segments);
        StateModelSnapshot snapshot = models.Snapshot(schemas);
        DecodedRevision first = RevisionDecoder.ReadSnapshot(states, schemas, addresses[0], snapshot);
        DecodedRevision final = RevisionDecoder.ReadSnapshot(states, schemas, addresses[3], snapshot);
        ObjectStateRecord[] oldFacts = first.Objects.Where(row => row.Schema?.Type == TypeExpr.Named("Damage")).ToArray();
        ObjectStateRecord[] newFacts = final.Objects.Where(row => row.Schema?.Type == TypeExpr.Named("Damage")).ToArray();
        Assert.Equal(2, oldFacts.Length); // Two equal records, not one equality-interned record.
        Assert.Equal(3, newFacts.Length); // with creates a third identity; the original remains reachable.
        Assert.All(oldFacts, old => Assert.Contains(newFacts, current => current.Id == old.Id));
        Assert.Empty(states.Read(addresses[1]).LocalObjects); // Only Transient changed, despite business inequality.
        ObjectVersionRecord changed = Assert.Single(states.Read(addresses[2]).LocalObjects);
        ObjectId ignoredId = Assert.Single(first.Objects, row => row.Schema?.Type == TypeExpr.Named("Ignored")).Id;
        Assert.Equal(ignoredId.Value, changed.ObjectId);
        Assert.Equal(ObjectVersionKind.Delta, changed.Kind); // Business equality ignored a persisted timestamp.
        Assert.Contains(states.Read(addresses[3]).LocalObjects,
            row => row.Kind == ObjectVersionKind.Base && !oldFacts.Any(old => old.Id.Value == row.ObjectId));
        Assert.Empty(states.Read(addresses[4]).LocalObjects); // Cold materialization recaptures the same complete state.
        Assert.Empty(states.Read(addresses[4]).RemovedObjectIds);
    }

    [Fact]
    public void RecordClassHydrationBypassesConstructorsInitializersCopyAndPropertyCode() {
        GeneratorTestRun run = RunGenerator("""
            using System;
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using Atelia.DurableGraph.Generated;
            using Atelia.DurableGraph.Persistence;
            [DurableType("Guard",1)] public sealed partial record Guard : IDurableObject {
                public static int Constructors,Initializers,Getters,Inits,Copies;
                public static bool ForbidConstruction;
                [DurableField(1)] private readonly long _secret;
                [field:DurableField(2)] public int Number {get;}
                [field:DurableField(3)] public int Value {
                    get {Getters++;return field+1000;}
                    init {Inits++;field=value;}
                }
                [Transient] private int _scratch=Initialize();
                private static int Initialize(){Initializers++;return 91;}
                public Guard(int number) {
                    Constructors++;if(ForbidConstruction)throw new Exception("constructor");
                    _secret=123456789;Number=number;Value=29;
                }
                private Guard(Guard prior) {Copies++;throw new Exception("copy constructor");}
                public bool Check() => _secret==123456789 && _scratch==0 && Number==17;
                public int Computed=>throw new Exception("computed getter");
            }
            public static class Host {
                public static bool Run(string path) {
                    var m=new StateModelRegistry();DurableDefinitions.Register(m);
                    using(var repo=FixtureGraphRepository.CreateNew(path, m))using(var session=repo.Create(new Guard(17))) {
                        session.Commit(new(1000000,1));
                    }
                    Guard.ForbidConstruction=true;
                    using(var repo=FixtureGraphRepository.OpenExisting(path, m))using(var session=repo.Load<Guard>()) {
                        if(!session.World.Check() || Guard.Constructors!=1 || Guard.Initializers!=1 ||
                            Guard.Getters!=0 || Guard.Inits!=1 || Guard.Copies!=0)return false;
                        if(session.World.Value!=1029)return false;
                        session.Commit(new(1000000,1));
                    }
                    return Guard.Getters==1 && Guard.Constructors==1 && Guard.Initializers==1 && Guard.Inits==1 && Guard.Copies==0;
                }
            }
            """);
        AssertSchemaOnlyCompiles(run);
        var assembly = EmitAndLoad(run.OutputCompilation);
        using RawBaseDirectory directory = new();
        Assert.True(assembly.GetType("Host")!.GetMethod("Run")!.CreateDelegate<Func<string, bool>>()(directory.Path));
        Assert.Equal(4, assembly.GetType("Guard")!.GetFields(System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).Length);
    }

    private const string RecordClassGraphSource = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Generated;
        using Atelia.DurableGraph.Persistence;
        using Atelia.DurableGraph.Storage;
        namespace RecordClasses;
        [DurableType("Fact",1)] public abstract partial record Fact([field:DurableField(1)] string Actor) : IDurableObject;
        [DurableType("Damage",1)] public sealed partial record Damage(string Actor,[field:DurableField(1)] int Amount) : Fact(Actor);
        [DurableType("Point",1)] public readonly partial record struct Point([field:DurableField(1)] int X);
        [DurableType("Root",1)] public abstract partial record Root<T>([field:DurableField(1)] T Part) : IDurableObject;
        [DurableType("Box",1)] public sealed partial record Box<T>(T Part,[field:DurableField(1)] Damage Fact) : Root<T>(Part);
        [DurableType("Node",1)] public sealed partial record Node : IDurableObject {
            [DurableField(1)] public Node? Next;
            [DurableField(2)] public World? Owner;
            public bool Equals(Node? other)=>throw new Exception("business Equals must never run");
            public override int GetHashCode()=>throw new Exception("business hash must never run");
        }
        [DurableType("Ignored",1)] public sealed partial record Ignored([field:DurableField(1)] int Key) : IDurableObject {
            [DurableField(2)] public long Timestamp;
            [DurableField(3)] public long Padding=long.MaxValue;
            public bool Equals(Ignored? other)=>other is not null && Key==other.Key;
            public override int GetHashCode()=>Key;
        }
        [DurableType("Transient",1)] public sealed partial record ScratchRecord([field:DurableField(1)] int Key) : IDurableObject {
            [field:Transient] public int Scratch {get;set;}
        }
        [DurableType("World",1)] public sealed partial class World : IDurableObject {
            [DurableField(1)] public Damage First=null!;
            [DurableField(2)] public Damage Equal=null!;
            [DurableField(3)] public Damage Alias=null!;
            [DurableField(4)] public Damage Copy=null!;
            [DurableField(5)] public Node Node=null!;
            [DurableField(6)] public Node Self=null!;
            [DurableField(7)] public Ignored Ignored=new(7){Timestamp=31};
            [DurableField(8)] public ScratchRecord Scratch=new(7){Scratch=9};
            [DurableField(9)] public Box<Point?> Box=null!;
            [DurableField(10)] public Fact[] Facts=[];
            [DurableField(11)] public List<Damage> List=new();
            [DurableField(12)] public Dictionary<int,Damage> Map=new();
        }
        public static class Host {
            public static StateModelRegistry Models(){var m=new StateModelRegistry();DurableDefinitions.Register(m);return m;}
            private static void Require(bool value,string reason){if(!value)throw new Exception(reason);}
            private static World Seed() {
                var w=new World();w.First=new("actor",3);w.Equal=new("actor",3);w.Alias=w.First;w.Copy=w.First;
                Require(w.First==w.Equal && !ReferenceEquals(w.First,w.Equal),"equal distinct setup");
                w.Node=new(){Owner=w};var second=new Node(){Owner=w,Next=w.Node};w.Node.Next=second;
                w.Self=new(){Owner=w};w.Self.Next=w.Self;
                w.Box=new(new Point(19),w.First);w.Facts=new Fact[]{w.First,w.Equal};
                w.List.Add(w.First);w.List.Add(w.Equal);w.Map.Add(1,w.First);return w;
            }
            private static void Check(World w) {
                Require(w.First==w.Equal && !ReferenceEquals(w.First,w.Equal),"equal distinct roundtrip");
                Require(ReferenceEquals(w.Alias,w.First),"shared reference");
                Require(w.Copy==w.First && !ReferenceEquals(w.Copy,w.First),"with identity");
                Require(ReferenceEquals(w.Node.Next!.Next,w.Node) && ReferenceEquals(w.Node.Owner,w) &&
                    ReferenceEquals(w.Node.Next.Owner,w) && ReferenceEquals(w.Self.Next,w.Self),"cycles");
                Require(w.Ignored.Timestamp==32 && w.Ignored.Padding==long.MaxValue && w.Scratch.Scratch==0,"persistent vs transient");
                Require(w.Box.Part!.Value.X==19 && ReferenceEquals(w.Box.Fact,w.First),"generic nullable inline and reference inheritance");
                Require(ReferenceEquals(w.Facts[0],w.First) && ReferenceEquals(w.Facts[1],w.Equal) &&
                    ReferenceEquals(w.List[0],w.First) && ReferenceEquals(w.List[1],w.Equal) && ReferenceEquals(w.Map[1],w.First),"containers");
            }
            public static FrameAddress[] Run(string path) {
                var result=new List<FrameAddress>();var m=Models();var w=Seed();
                using(var repo=FixtureGraphRepository.CreateNew(path, m))using(var session=repo.Create(w)) {
                    result.Add(session.Commit(new(1000000,1)));
                    var beforeScratch=w.Scratch with {};w.Scratch.Scratch=10;
                    Require(beforeScratch!=w.Scratch,"Transient participates in compiler equality");
                    result.Add(session.Commit(new(1000000,1)));
                    var beforeTimestamp=w.Ignored with {};w.Ignored.Timestamp=32;
                    Require(beforeTimestamp==w.Ignored,"business equality ignores timestamp");
                    result.Add(session.Commit(new(1000000,1)));
                    w.Copy=w.First with {};result.Add(session.Commit(new(1000000,1)));
                }
                using(var repo=FixtureGraphRepository.OpenExisting(path, m))using(var session=repo.Load<World>()) {
                    Require(!ReferenceEquals(session.World,w),"fresh cold world");Check(session.World);
                    result.Add(session.Commit(new(1000000,1)));
                }
                return result.ToArray();
            }
        }
        """;
}
