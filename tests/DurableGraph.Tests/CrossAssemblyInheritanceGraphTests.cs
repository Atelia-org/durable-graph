using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrossAssemblyInheritanceGraphPreservesHiddenStateIdentityAndWholeLeafDeltas(bool reverse) {
        GeneratorTestRun bases = RunCrossAssemblyGenerator(InheritanceGraphBaseSource, forceDefinitions: "true");
        var baseImage = EmitCrossAssemblyReference(bases);
        GeneratorTestRun middle = RunCrossAssemblyGenerator(InheritanceGraphMiddleSource, [baseImage.Reference]);
        var middleImage = EmitCrossAssemblyReference(middle);
        GeneratorTestRun app = RunCrossAssemblyGenerator(InheritanceGraphApplicationSource, [baseImage.Reference, middleImage.Reference]);
        AssertSchemaOnlyCompiles(app);
        foreach (var consumer in new[] { middle, app }) {
            string generated = GeneratedSource(consumer, "DurableGenericStates.g.cs");
            Assert.DoesNotContain("typeof(global::InheritanceBases.Hidden", generated);
            Assert.DoesNotContain("global::InheritanceBases.HiddenPair", generated);
            Assert.DoesNotContain("global::InheritanceBases.HiddenPoint", generated);
            Assert.DoesNotContain(consumer.OutputCompilation.GetDiagnostics(), d => d.Id is "CS0433" or "CS0436");
        }
        using var scope = new CrossAssemblyLoadScope(bases, middle, app);
        Type host = scope.Load(app).GetType("InheritanceApp.Host")!;
        var models = host.GetMethod("Models")!.CreateDelegate<Func<bool, StateModelRegistry>>()(reverse);
        using RawBaseDirectory directory = new();
        FrameAddress[] addresses = host.GetMethod("Exercise")!
            .CreateDelegate<Func<string, bool, FrameAddress[]>>()(directory.Path, reverse);
        using SegmentStore segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory.Path, "state"));
        using var schemaFile = RbfFile.OpenReadOnlyExisting(Path.Combine(directory.Path, "schemas.rbf"));
        SchemaStore schemas = new(schemaFile, readOnly: true);
        using StateRevisionStore states = new(segments);
        var initial = RevisionDecoder.ReadSnapshot(states, schemas, addresses[0], models.Snapshot(schemas));
        var leaf = Assert.Single(initial.Objects, item => item.Schema?.SchemaId == "inherit.Leaf");
        var other = Assert.Single(initial.Objects, item => item.Schema?.SchemaId == "inherit.Other");
        Assert.Equal(3, initial.Objects.Count); // Two actual leaves and their shared List; no base/inline object rows.
        Assert.Equal("inherit.Local", leaf.Schema!.BaseSchema!.SchemaId);
        Assert.Equal("inherit.Middle", leaf.Schema.BaseSchema.BaseSchema!.SchemaId);
        Assert.Equal("inherit.Base", leaf.Schema.BaseSchema.BaseSchema.BaseSchema!.SchemaId);
        foreach (int index in new[] { 1, 6, 7 }) {
            Assert.Empty(states.Read(addresses[index]).LocalObjects);
            Assert.Empty(states.Read(addresses[index]).RemovedObjectIds);
        }
        var child = Assert.Single(states.Read(addresses[2]).LocalObjects);
        Assert.Equal(other.Id.Value, child.ObjectId);
        Assert.Equal(ObjectVersionKind.Delta, child.Kind);
        // Eleven flattened fields require one two-byte bitmap. Base and leaf segments
        // must never be encoded as separate Delta bodies, even across assembly boundaries.
        byte[][] goldens = [[0x10, 0x00, 22], [0x00, 0x04, 90], [0x10, 0x04, 24, 92]];
        for (int index = 0; index < goldens.Length; index++) {
            var delta = Assert.Single(states.Read(addresses[index + 3]).LocalObjects);
            Assert.Equal(leaf.Id.Value, delta.ObjectId);
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
            Assert.Equal(goldens[index], delta.Body.ToArray());
        }
        var final = RevisionDecoder.ReadSnapshot(states, schemas, addresses[^1], models.Snapshot(schemas));
        Assert.Equal(initial.Objects.Select(item => item.Id).OrderBy(id => id.Value), final.Objects.Select(item => item.Id).OrderBy(id => id.Value));
    }

    private const string InheritanceGraphBaseSource = """
        using System;
        using System.Collections.Generic;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        namespace InheritanceBases;
        public static class Trace {
            public static int Constructors,Initializers;
            public static int Initialize() { Initializers++;return 123; }
        }
        [DurableType("inherit.HiddenPair",1)] internal readonly partial struct HiddenPair<T> {
            [DurableField(1)] private readonly T _value;
            [DurableField(2)] private readonly int _stamp;
            public HiddenPair(T value) { _value=value;_stamp=9876; }
            public T Value=>_value;
            public int Stamp=>_stamp;
        }
        [DurableType("inherit.HiddenPoint",1)] internal readonly partial struct HiddenPoint {
            [DurableField(1)] private readonly int _value;
            public HiddenPoint(int value) { _value=value; }
            public int Value=>_value;
        }
        [DurableType("inherit.Base",1)] public abstract partial class Base<T>:IDurableObject {
            [DurableField(1)] private readonly HiddenPair<T> _pair;
            [DurableField(2)] private readonly HiddenPoint? _optional;
            [DurableField(3)] public Base<T>? Peer;
            [DurableField(4)] public List<Base<T>> Items;
            [DurableField(5)] public int Ancestor=10;
            [DurableField(6)] private readonly int _padding6=10000;
            [DurableField(7)] private readonly int _padding7=20000;
            [DurableField(8)] private readonly Guid _padding8=new("da4ba670-6682-453e-a117-a4e3a5a72f2d");
            [Transient] private int _cache=Trace.Initialize();
            protected Base(T value,bool present) { Trace.Constructors++;_pair=new(value);_optional=present?new HiddenPoint(4567):null;Items=new(); }
            public T Value=>_pair.Value;
            public bool CheckBase(bool present,bool hydrated)=>_pair.Stamp==9876 && _optional.HasValue==present &&
                (!present || _optional!.Value.Value==4567) && _padding6==10000 && _padding7==20000 &&
                _padding8==new Guid("da4ba670-6682-453e-a117-a4e3a5a72f2d") && (!hydrated || _cache==0);
        }
        public static class Catalog {
            public static void Register(IStateModelRegistration models)=>Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
        }
        """;

    private const string InheritanceGraphMiddleSource = """
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using InheritanceBases;
        namespace InheritanceMiddle;
        [DurableType("inherit.Middle",1)] public abstract partial class Middle<T>:Base<T> {
            [DurableField(1)] private readonly int _middle;
            protected Middle(T value,bool present):base(value,present) { Trace.Constructors++;_middle=33333; }
            public int MiddleValue=>_middle;
        }
        [DurableType("inherit.Other",1)] public partial class Other<T>:Middle<T> {
            [DurableField(1)] public int OtherValue=55;
            public Other(T value):base(value,false) { Trace.Constructors++; }
        }
        public static class Catalog {
            public static void Register(IStateModelRegistration models)=>Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
        }
        """;

    private const string InheritanceGraphApplicationSource = """
        using System;
        using System.Collections.Generic;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Persistence;
        using Atelia.DurableGraph.Storage;
        using InheritanceBases;
        using InheritanceMiddle;
        namespace InheritanceApp;
        [DurableType("inherit.Local",1)] public abstract partial class Local<T>:Middle<T> {
            [DurableField(1)] private readonly int _local;
            protected Local(T value):base(value,true) { Trace.Constructors++;_local=44444; }
            public int LocalValue=>_local;
        }
        [DurableType("inherit.Leaf",1)] public sealed partial class Leaf<T>:Local<T> {
            [DurableField(1)] public int LeafValue=44;
            [Transient] private int _leafCache=Trace.Initialize();
            public Leaf(T value):base(value) { Trace.Constructors++; }
            public bool LeafHydrated=>_leafCache==0;
        }
        public static class Host {
            public static StateModelRegistry Models(bool reverse) {
                var models=new StateModelRegistry();
                if(reverse) { Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);InheritanceMiddle.Catalog.Register(models);InheritanceBases.Catalog.Register(models); }
                else { InheritanceBases.Catalog.Register(models);InheritanceMiddle.Catalog.Register(models);Atelia.DurableGraph.Generated.DurableDefinitions.Register(models); }
                return models;
            }
            static void Require(bool value,string message) { if(!value)throw new InvalidOperationException(message); }
            static Leaf<int> Seed() {
                var world=new Leaf<int>(12345);var other=new Other<int>(54321);
                world.Peer=world;other.Peer=world;world.Items.Add(world);world.Items.Add(other);world.Items.Add(world);other.Items=world.Items;
                return world;
            }
            static void Check(Leaf<int> world) {
                var other=(Other<int>)world.Items[1];
                Require(world.Value==12345 && world.CheckBase(true,true) && world.MiddleValue==33333 && world.LocalValue==44444 && world.LeafHydrated,"private readonly inherited state and transient initialization");
                Require(other.Value==54321 && other.CheckBase(false,true) && other.OtherValue==56,"second polymorphic leaf");
                Require(ReferenceEquals(world,world.Peer) && ReferenceEquals(world,other.Peer) && ReferenceEquals(world,world.Items[0]) && ReferenceEquals(world,world.Items[2]) && ReferenceEquals(other.Items,world.Items),"base references and List polymorphism preserve identity");
                Require(world.Ancestor==12 && world.LeafValue==46,"whole leaf Delta chain");
                Require(Trace.Constructors==0 && Trace.Initializers==0,"restoration executed constructor or initializer");
            }
            public static FrameAddress[] Exercise(string path,bool reverse) {
                var models=Models(reverse);var world=Seed();var addresses=new List<FrameAddress>();
                using(var repo=FixtureGraphRepository.CreateNew(path, models))using(var session=repo.Create(world)) {
                    void Save()=>addresses.Add(session.Commit(new(1000000,1)));
                    Save();Save();((Other<int>)world.Items[1]).OtherValue++;Save();
                    world.Ancestor++;Save();world.LeafValue++;Save();world.Ancestor++;world.LeafValue++;Save();Save();
                }
                Trace.Constructors=Trace.Initializers=0;
                using(var repo=FixtureGraphRepository.OpenExisting(path, models))using(var session=repo.Load<Leaf<int>>()) {
                    Check(session.World);addresses.Add(session.Commit(new(1000000,1)));
                }
                return addresses.ToArray();
            }
        }
        """;
}
