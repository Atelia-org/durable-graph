using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void RecordStructGraphCombinesNullableGenericContainersAndIdentityCyclesAcrossCommits() {
        Type host = CompileRecordGraphHost();
        using RawBaseDirectory directory = new();
        FrameAddress[] addresses = host.GetMethod("SaveAndLoad")!.CreateDelegate<Func<string, FrameAddress[]>>()(directory.Path);
        StateModelRegistry models = host.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>()();
        using SegmentStore segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory.Path, "state"));
        using var schemaFile = RbfFile.OpenReadOnlyExisting(Path.Combine(directory.Path, "schemas.rbf"));
        SchemaStore schemas = new(schemaFile, readOnly: true);
        using StateRevisionStore states = new(segments);
        StateModelSnapshot snapshot = models.Snapshot(schemas);
        DecodedRevision initial = RevisionDecoder.ReadSnapshot(states, schemas, addresses[0], snapshot);
        ObjectStateRecord primary = Assert.Single(initial.Objects, row => row.Kind == ObjectStateKind.Dictionary &&
            row.Layout.Dictionary!.KeySlot.InlineSchema?.Type == TypeExpr.Named("record.Key", TypeExpr.Builtin(TypeTag.Int32)) &&
            ((IFrozenDictionaryState)row.Content).ComparerKind == DictionaryComparerKind.CurrentDefault);
        ObjectStateRecord application = Assert.Single(initial.Objects, row => row.Kind == ObjectStateKind.Dictionary &&
            ((IFrozenDictionaryState)row.Content).ComparerKind == DictionaryComparerKind.Application);
        foreach (FrameAddress address in addresses) {
            DecodedRevision decoded = RevisionDecoder.ReadSnapshot(states, schemas, address, snapshot);
            Assert.Equal(DictionaryComparerKind.CurrentDefault, ((IFrozenDictionaryState)decoded.GetRequired(primary.Id).Content).ComparerKind);
            Assert.Equal(DictionaryComparerKind.Application, ((IFrozenDictionaryState)decoded.GetRequired(application.Id).Content).ComparerKind);
        }
        foreach (int index in new[] { 1, 5, 7 }) {
            Assert.Empty(states.Read(addresses[index]).LocalObjects);
            Assert.Empty(states.Read(addresses[index]).RemovedObjectIds);
        }
        ObjectVersionRecord Local(int index) => Assert.Single(states.Read(addresses[index]).LocalObjects,
            row => row.ObjectId == primary.Id.Value);
        foreach (int index in new[] { 2, 3, 4, 6 }) {
            Assert.Equal(ObjectVersionKind.Delta, Local(index).Kind);
        }
        // Independent wire inspection: complete persisted Key, not current business equality, selects replacement.
        var timestamp = ReadRecordKeyReplacement(Local(2).Body, 100, 999);
        Assert.Equal(timestamp.OldName, timestamp.NewName);
        var identity = ReadRecordKeyReplacement(Local(3).Body, 999, 999);
        Assert.Equal(timestamp.NewName, identity.OldName);
        Assert.NotEqual(identity.OldName, identity.NewName);
        ReadRecordValuePatch(Local(4).Body, 2222);
        ReadRecordValuePatch(Local(6).Body, 2227);
    }

    [Fact]
    public void RecordStructKeyWhoseSynthesizedEqualityUsesTransientCannotHideCanonicalCollision() {
        Type host = CompileRecordGraphHost();
        object[] fixture = host.GetMethod("BadKeys")!.CreateDelegate<Func<object[]>>()();
        StateModelSnapshot models = ((StateModelRegistry)fixture[0]).Snapshot();
        IDurableObject world = (IDurableObject)fixture[1];
        CaptureSession session = new();
        using CaptureContext context = session.BeginCapture(models);
        models.ResolveCurrentModel(world.GetType()).AddRoot(context, world);
        // C# accepts both keys because Scratch participates in its synthesized Equals. DTOs deliberately omit it.
        Assert.Throws<InvalidDataException>(() => context.Seal());
        Assert.Equal(2, ((Func<int>)fixture[2])());
    }

    private static Type CompileRecordGraphHost() {
        GeneratorTestRun run = RunGenerator(RecordGraphSource);
        AssertSchemaOnlyCompiles(run);
        return EmitAndLoad(run.OutputCompilation).GetType("RecordGraph.Host")!;
    }

    private static (ObjectId OldName, ObjectId NewName) ReadRecordKeyReplacement(ReadOnlySpan<byte> body, long before, long after) {
        BinaryPayloadReader reader = new(body);
        Assert.Equal(1U, reader.ReadUInt32()); // Remove
        Assert.Equal(0, reader.ReadInt32());
        ObjectId oldName = new(reader.ReadUInt32());
        Assert.Equal(before, reader.ReadInt64());
        Assert.Equal(0U, reader.ReadUInt32()); // PatchValue
        Assert.Equal(1U, reader.ReadUInt32()); // Add
        Assert.Equal(0, reader.ReadInt32());
        ObjectId newName = new(reader.ReadUInt32());
        Assert.Equal(after, reader.ReadInt64());
        Assert.Equal(1000, reader.ReadInt32());
        Assert.NotEqual(0U, reader.ReadUInt32()); // Value.Owner still names World.
        reader.EnsureFullyConsumed();
        return (oldName, newName);
    }

    private static void ReadRecordValuePatch(ReadOnlySpan<byte> body, int amount) {
        BinaryPayloadReader reader = new(body);
        Assert.Equal(0U, reader.ReadUInt32());
        Assert.Equal(1U, reader.ReadUInt32());
        Assert.Equal(1, reader.ReadInt32()); // Key.Part
        Assert.NotEqual(0U, reader.ReadUInt32()); // Key.Name
        Assert.Equal(101L, reader.ReadInt64());
        Assert.Equal(1U, reader.ReadUInt32()); // Value's inline field mask, only Part changes.
        Assert.Equal(amount, reader.ReadInt32());
        Assert.Equal(0U, reader.ReadUInt32());
        reader.EnsureFullyConsumed();
    }

    private const string RecordGraphSource = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Runtime.CompilerServices;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Generated;
        using Atelia.DurableGraph.Persistence;
        using Atelia.DurableGraph.Storage;
        namespace RecordGraph;
        [DurableType("record.Key",1)] public readonly partial record struct Key<T>(
            [field:DurableField(1)] T Part,
            [field:DurableField(2)] string? Name,
            [field:DurableField(3)] long Timestamp) where T:IEquatable<T> {
            public bool Equals(Key<T> other)=>Part.Equals(other.Part) && StringComparer.Ordinal.Equals(Name,other.Name);
            public override int GetHashCode()=>HashCode.Combine(Part,Name);
        }
        [DurableType("record.Value",1)] public readonly partial record struct Value<T>(
            [field:DurableField(1)] T Part,
            [field:DurableField(2)] World? Owner) {
            [field:Transient] public int Scratch {get;init;}
        }
        [DurableType("record.Scope",1)] public readonly partial record struct Scope(
            [field:DurableField(1)] int? Region,
            [field:DurableField(2)] World Owner,
            [field:DurableField(3)] Dictionary<Key<Scope>,Value<int>> Map) {
            public bool Equals(Scope other)=>Region==other.Region && ReferenceEquals(Owner,other.Owner) && ReferenceEquals(Map,other.Map);
            public override int GetHashCode()=>HashCode.Combine(Region,RuntimeHelpers.GetHashCode(Owner),RuntimeHelpers.GetHashCode(Map));
        }
        [DurableType("record.BadKey",1)] public readonly partial record struct BadKey(
            [field:DurableField(1)] int Number, [field:Transient] int Scratch);
        [DurableType("record.BadWorld",1)] public partial class BadWorld:IDurableObject {
            [DurableField(1)] public Dictionary<BadKey,int> Keys=new();
        }
        [DurableType("record.Box",1)] public partial class Box<T>:IDurableObject { [DurableField(1)] public T Value=default!; }
        [DurableType("record.World",1)] public partial class World:IDurableObject {
            [DurableField(1)] public Dictionary<Key<int>,Value<int>> Primary=new();
            [DurableField(2)] public Dictionary<Key<int>,Value<int>> Application=new(new KeyComparer());
            [DurableField(3)] public Dictionary<Key<Scope>,Value<int>> Complex=new();
            [DurableField(4)] public Value<Scope> Direct;
            [DurableField(5)] public Value<Scope>? Optional;
            [DurableField(6)] public Value<Scope>?[] Array=[];
            [DurableField(7)] public List<Value<Scope>?> List=new();
            [DurableField(8)] public Box<Value<Scope>> Box=new();
            [DurableField(9)] public Dictionary<Key<int>,Value<int>>? Alias;
            [DurableField(10)] public List<Dictionary<Key<int>,Value<int>>[]> Views=new();
            [DurableField(11)] public string OriginalName="";
            public override bool Equals(object? other)=>throw new InvalidOperationException("World Equals cannot run");
            public override int GetHashCode()=>throw new InvalidOperationException("World hash cannot run");
        }
        public sealed class KeyComparer:IEqualityComparer<Key<int>> {
            public bool Equals(Key<int> x,Key<int> y)=>x.Equals(y);
            public int GetHashCode(Key<int> key)=>key.GetHashCode();
        }
        public static class Host {
            public static StateModelRegistry Models() {
                var models=new StateModelRegistry();DurableDefinitions.Register(models);
                // Returning Default intentionally exercises Application mode preservation during cold recapture.
                models.UseDictionaryComparer<Key<int>,Value<int>>(EqualityComparer<Key<int>>.Default);return models;
            }
            private static World Seed() {
                var w=new World();
                for(int i=0;i<32;i++) {
                    string name=new string(("name-"+i).ToCharArray());var key=new Key<int>(i,name,100+i);
                    var value=new Value<int>(1000+i,w){Scratch=91};w.Primary.Add(key,value);w.Application.Add(key,value);
                    if(i==0)w.OriginalName=name;
                }
                var scope=new Scope(null,w,w.Complex);
                w.Complex.Add(new(scope,null,5),new(8,w));
                w.Direct=new(scope,w){Scratch=99};w.Optional=w.Direct;
                w.Array=new Value<Scope>?[]{null,w.Direct};w.List.AddRange(w.Array);w.Box.Value=w.Direct;
                w.Alias=w.Primary;w.Views.Add(new[]{w.Primary,w.Application});return w;
            }
            private static void Replace(World w,bool name) {
                var key=w.Primary.Keys.Single(k=>k.Part==0);var value=w.Primary[key];w.Primary.Remove(key);
                w.Primary.Add(name ? key with {Name=new string(key.Name!.ToCharArray())} : key with {Timestamp=999},value);
            }
            private static void Require(bool condition,string text) {if(!condition)throw new InvalidOperationException(text);}
            private static void Check(World w) {
                Require(ReferenceEquals(w.Primary,w.Alias) && ReferenceEquals(w.Primary,w.Views[0][0]) && ReferenceEquals(w.Application,w.Views[0][1]),"shared dictionaries");
                void ScopeCheck(Value<Scope> value) {
                    Require(ReferenceEquals(value.Owner,w) && ReferenceEquals(value.Part.Owner,w) && ReferenceEquals(value.Part.Map,w.Complex),"record references and cycle");
                    Require(value.Part.Region is null && value.Scratch==0,"nullable component and transient");
                }
                ScopeCheck(w.Direct);ScopeCheck(w.Optional!.Value);ScopeCheck(w.Array[1]!.Value);
                ScopeCheck(w.List[1]!.Value);ScopeCheck(w.Box.Value);Require(w.Array[0] is null && w.List[0] is null,"absent nullable record");
                var complex=w.Complex.Single();Require(ReferenceEquals(complex.Key.Part.Map,w.Complex) && ReferenceEquals(complex.Value.Owner,w),"key and value references");
                Require(w.Complex.ContainsKey(new(new Scope(null,w,w.Complex),null,-999)),"nested record lookup");
                var key=w.Primary.Keys.Single(k=>k.Part==0);
                Require(key.Timestamp==999 && key.Name==w.OriginalName && !ReferenceEquals(key.Name,w.OriginalName),"complete key persistence");
                Require(w.Primary.ContainsKey(new(0,new string(w.OriginalName.ToCharArray()),-1)),"current business lookup");
                Require(w.Application.Count==32 && w.Application.All(p=>ReferenceEquals(p.Value.Owner,w) && p.Value.Scratch==0),"application contents");
            }
            public static FrameAddress[] SaveAndLoad(string path) {
                var result=new List<FrameAddress>();var models=Models();var world=Seed();var map=world.Primary;
                using(var repo=FixtureGraphRepository.CreateNew(path, models))using(var session=repo.Create(world)) {
                    result.Add(session.Commit(new(1000000,1)));
                    world.Direct=world.Direct with {Scratch=55};
                    result.Add(session.Commit(new(1000000,1)));
                    Replace(world,false);result.Add(session.Commit(new(1000000,1)));
                    Replace(world,true);result.Add(session.Commit(new(1000000,1)));
                    var key=world.Primary.Keys.Single(k=>k.Part==1);world.Primary[key]=world.Primary[key] with {Part=2222};
                    result.Add(session.Commit(new(1000000,1)));
                    Require(ReferenceEquals(world,session.World) && ReferenceEquals(map,world.Primary),"working identities");
                }
                using(var repo=FixtureGraphRepository.OpenExisting(path, models))using(var session=repo.Load<World>()) {
                    Check(session.World);result.Add(session.Commit(new(1000000,1)));
                    var key=session.World.Primary.Keys.Single(k=>k.Part==1);
                    session.World.Primary[key]=session.World.Primary[key] with {Part=2227};
                    result.Add(session.Commit(new(1000000,1)));
                }
                using(var repo=FixtureGraphRepository.OpenExisting(path, models))using(var session=repo.Load<World>()) {
                    Check(session.World);Require(session.World.Primary.Single(p=>p.Key.Part==1).Value.Part==2227,"value patch after cold");
                    result.Add(session.Commit(new(1000000,1)));
                }
                return result.ToArray();
            }
            public static object[] BadKeys() {
                var w=new BadWorld();w.Keys.Add(new(7,1),1);w.Keys.Add(new(7,2),2);
                return new object[]{Models(),w,(Func<int>)(()=>w.Keys.Count)};
            }
        }
        """;
}
