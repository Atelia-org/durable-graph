using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using System.Reflection;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Theory]
    [InlineData("Dictionary<Point,int>")]
    [InlineData("List<Dictionary<Point,int>>")]
    [InlineData("Dictionary<int,Dictionary<Point,int>>")]
    [InlineData("Dictionary<Key<Point>,Point?>[]")]
    public void CompositeDictionarySupportedStructKeysUseExistingReferenceRepresentation(string fieldType) {
        GeneratorTestRun run = RunGenerator("using System; using System.Collections.Generic; using Atelia.DurableGraph; " +
            "[DurableType(\"Point\",1)] public partial struct Point { [DurableField(1)] public int X; } " +
            "[DurableType(\"Key\",1)] public partial struct Key<T> { [DurableField(1)] public T Part; } " +
            "[DurableType(\"World\",1)] public partial class World:IDurableObject { [DurableField(1)] public " + fieldType + " Value; }");
        AssertSchemaOnlyCompiles(run);
        string generated = GeneratedSource(run, "DurableGenericStates.g.cs");
        Assert.Contains("TypeExpr.Dictionary(", generated);
        Assert.Contains("global::Atelia.DurableGraph.ObjectId", generated);
        Assert.DoesNotContain("__DurableKeyComparer", generated);
        Assert.Contains("manifest:9", GeneratedSource(run, "DurableGraphSchemaHistoryCandidates.g.cs"));
    }

    [Theory]
    [InlineData("Dictionary<Point?,int>")]
    [InlineData("Dictionary<(int,int),int>")]
    [InlineData("Dictionary<UnregisteredKey,int>")]
    public void CompositeDictionaryDoesNotWidenRootNullableTupleOrUnregisteredTypes(string fieldType) {
        GeneratorTestRun run = RunGenerator("using System.Collections.Generic; using Atelia.DurableGraph; " +
            "[DurableType(\"Point\",1)] public partial struct Point { [DurableField(1)] public int X; } " +
            "public struct UnregisteredKey {public int X;} " +
            "[DurableType(\"World\",1)] public partial class World:IDurableObject { [DurableField(1)] public " + fieldType + " Value; }");
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0007");
    }

    [Fact]
    public void CompositeDictionaryCaptureAndDiffNeverInvokeDomainEqualityAndKeepAllKeyFields() {
        Type host = CompileCompositeDictionaryHost();
        object[] fixture = host.GetMethod("Fixture")!.CreateDelegate<Func<object[]>>()();
        StateModelSnapshot models = ((StateModelRegistry)fixture[0]).Snapshot();
        IDurableObject world = (IDurableObject)fixture[1];
        StateModelBinding model = models.ResolveCurrentModel(world.GetType());
        var calls = (Func<int>)fixture[6];
        CaptureSession session = new();
        CapturedGraph Capture() {
            int before = calls();
            using CaptureContext context = session.BeginCapture(models);
            model.AddRoot(context, world);
            CapturedGraph graph = context.Seal();
            Assert.Equal(before, calls());
            session.Accept(graph);
            return graph;
        }
        ObjectStateRecord Primary(CapturedGraph graph) => Assert.Single(graph.Objects,
            row => row.Kind == ObjectStateKind.Dictionary && row.Layout.Dictionary!.KeySlot.InlineSchema?.Type ==
                TypeExpr.Named("GKey", TypeExpr.Builtin(TypeTag.Int32)));

        CapturedGraph first = Capture();
        ObjectStateRecord prior = Primary(first);
        ICapturedStatePreparation preparation = prior.Preparation!;
        byte[] frozenBase = preparation.PrepareBase(prior).Body.ToArray();
        ((Action)fixture[3])(); // Change only the ignored, non-persistent Debug field.
        ObjectStateRecord transient = Primary(Capture());
        int beforePrepare = calls();
        Assert.False(preparation.PrepareDelta(prior, transient).HasChanges);
        Assert.Equal(beforePrepare, calls());

        ((Action)fixture[2])(); // Replace the actual key, changing its persistent Timestamp.
        ObjectStateRecord timestamp = Primary(Capture());
        int beforeTimestampPrepare = calls();
        PreparedDeltaBody firstDelta = preparation.PrepareDelta(transient, timestamp);
        var firstChange = ReadCompositeKeyReplacement(firstDelta.Body, 100, 999);
        Assert.Equal(firstChange.OldName, firstChange.NewName);
        Assert.Equal(beforeTimestampPrepare, calls());

        ((Action)fixture[4])(); // Same lookup key, but a distinct string instance is now stored.
        CapturedGraph last = Capture();
        ObjectStateRecord changedString = Primary(last);
        int beforeLastPrepare = calls();
        PreparedDeltaBody secondDelta = preparation.PrepareDelta(timestamp, changedString);
        var secondChange = ReadCompositeKeyReplacement(secondDelta.Body, 999, 999);
        Assert.NotEqual(secondChange.OldName, secondChange.NewName);
        Assert.Equal(beforeLastPrepare, calls());
        Assert.Equal(firstChange.NewName, secondChange.OldName);
        Assert.Equal(first.Objects.Single(row => row.Id == secondChange.OldName).StringContent,
            last.Objects.Single(row => row.Id == secondChange.NewName).StringContent);
        Assert.Equal(prior.Id, changedString.Id);
        Assert.Equal(DictionaryComparerKind.CurrentDefault, ((IFrozenDictionaryState)changedString.Content).ComparerKind);

        ((Action)fixture[5])(); // Erase the mutable source after all captures.
        Assert.Equal(frozenBase, preparation.PrepareBase(prior).Body.ToArray());
        Assert.Equal(secondDelta.Body.ToArray(), preparation.PrepareDelta(timestamp, changedString).Body.ToArray());
    }

    [Fact]
    public void CompositeDictionaryDefaultGraphCommitsAndColdReopensWithGenericReadonlyIdentityComponents() {
        Type host = CompileCompositeDictionaryHost();
        using RawBaseDirectory directory = new();
        FrameAddress[] addresses = host.GetMethod("SaveAndLoad")!.CreateDelegate<Func<string, FrameAddress[]>>()(directory.Path);
        var models = host.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>()();
        using SegmentStore segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory.Path, "state"));
        using var file = RbfFile.OpenReadOnlyExisting(Path.Combine(directory.Path, "schemas.rbf"));
        SchemaStore schemas = new(file, readOnly: true);
        using StateRevisionStore states = new(segments);
        StateModelSnapshot snapshot = models.Snapshot(schemas);
        DecodedRevision first = RevisionDecoder.ReadSnapshot(states, schemas, addresses[0], snapshot);
        ObjectStateRecord primary = Assert.Single(first.Objects, row => row.Kind == ObjectStateKind.Dictionary &&
            row.Layout.Dictionary!.KeySlot.InlineSchema?.Type == TypeExpr.Named("GKey", TypeExpr.Builtin(TypeTag.Int32)));
        StateValueBinding key = snapshot.ResolveStoredValue(primary.Layout.Dictionary!.KeySlot);
        MethodInfo read = typeof(DurableSchemaGeneratorTests).GetMethod(nameof(ReadCompositePrimary), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(key.StateType, key.StateOpsType);
        ObjectId originalName = default;
        for (int index = 0; index < addresses.Length; index++) {
            DecodedRevision decoded = RevisionDecoder.ReadSnapshot(states, schemas, addresses[index], snapshot);
            ObjectStateRecord row = decoded.GetRequired(primary.Id);
            Assert.Equal(DictionaryComparerKind.CurrentDefault, ((IFrozenDictionaryState)row.Content).ComparerKind);
            var entries = (Dictionary<int, (ObjectId Name, long Timestamp, int Value)>)read.Invoke(null, [row])!;
            Assert.Equal(32, entries.Count);
            Assert.Equal(index < 2 ? 100L : 999L, entries[0].Timestamp);
            Assert.Equal(index < 4 ? 1001 : index < 7 ? 2222 : 2227, entries[1].Value);
            if (index == 0) originalName = entries[0].Name;
            if (index < 3) Assert.Equal(originalName, entries[0].Name);
            else Assert.NotEqual(originalName, entries[0].Name);
            Assert.Equal("name-0", decoded.GetRequired(entries[0].Name).StringContent);
        }
        foreach (int index in new[] { 1, 6, 8 }) {
            Assert.Empty(states.Read(addresses[index]).LocalObjects);
            Assert.Empty(states.Read(addresses[index]).RemovedObjectIds);
        }
        foreach (int index in new[] { 2, 3, 4, 7 }) {
            Assert.Equal(ObjectVersionKind.Delta, Assert.Single(states.Read(addresses[index]).LocalObjects,
                row => row.ObjectId == primary.Id.Value).Kind);
        }
        Assert.DoesNotContain(states.Read(addresses[5]).LocalObjects, row => row.ObjectId == primary.Id.Value);
        ObjectVersionRecord timeChange = Assert.Single(states.Read(addresses[2]).LocalObjects, row => row.ObjectId == primary.Id.Value);
        var replacement = ReadCompositeKeyReplacement(timeChange.Body, 100, 999);
        Assert.Equal(originalName, replacement.OldName);
        Assert.Equal(originalName, replacement.NewName);
    }

    private static Type CompileCompositeDictionaryHost() {
        GeneratorTestRun run = RunGenerator(CompositeDictionarySource);
        AssertSchemaOnlyCompiles(run);
        return EmitAndLoad(run.OutputCompilation).GetType("CompositeKeys.Host")!;
    }

    private static (ObjectId OldName, ObjectId NewName) ReadCompositeKeyReplacement(ReadOnlySpan<byte> body, long priorTime, long currentTime) {
        BinaryPayloadReader reader = new(body);
        Assert.Equal(1U, reader.ReadUInt32());
        Assert.Equal(0, reader.ReadInt32());
        ObjectId oldName = new(reader.ReadUInt32());
        Assert.Equal(priorTime, reader.ReadInt64());
        Assert.Equal(0U, reader.ReadUInt32());
        Assert.Equal(1U, reader.ReadUInt32());
        Assert.Equal(0, reader.ReadInt32());
        ObjectId newName = new(reader.ReadUInt32());
        Assert.Equal(currentTime, reader.ReadInt64());
        Assert.Equal(1000, reader.ReadInt32());
        reader.EnsureFullyConsumed();
        return (oldName, newName);
    }

    private static Dictionary<int, (ObjectId Name, long Timestamp, int Value)> ReadCompositePrimary<K, KOps>(ObjectStateRecord row)
        where K : unmanaged where KOps : IStateOps<K> {
        Dictionary<int, (ObjectId, long, int)> result = [];
        foreach (var entry in row.GetDictionaryState<K, int>().Entries) {
            ArrayBufferWriter<byte> buffer = new();
            BinaryPayloadWriter writer = new(buffer);
            K key = entry.Key;
            KOps.WriteBase(ref writer, in key, row.Layout.Dictionary!.KeySlot);
            BinaryPayloadReader reader = new(buffer.WrittenSpan);
            int part = reader.ReadInt32();
            result.Add(part, (new(reader.ReadUInt32()), reader.ReadInt64(), entry.Value));
            reader.EnsureFullyConsumed();
        }
        return result;
    }

    private const string CompositeDictionarySource = """
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
        namespace CompositeKeys;
        [DurableType("PlainKey",1)] public readonly partial struct PlainKey : IEquatable<PlainKey> {
            [DurableField(1)] private readonly int _number;
            [DurableField(2)] private readonly long _timestamp;
            [Transient] private readonly int _debug;
            public static int Calls, Constructions;
            public PlainKey(int number,long timestamp,int debug=0) { _number=number;_timestamp=timestamp;_debug=debug;Constructions++; }
            public int Number=>_number;
            public long Timestamp=>_timestamp;
            public int Debug=>_debug;
            public bool Equals(PlainKey other) {Calls++;return _number==other._number;}
            public override bool Equals(object? other)=>other is PlainKey key && Equals(key);
            public override int GetHashCode() {Calls++;return _number;}
        }
        [DurableType("GKey",1)] public partial struct Key<T> : IEquatable<Key<T>> where T:IEquatable<T> {
            [DurableField(1)] public T Part;
            [DurableField(2)] public string? Name;
            [DurableField(3)] public long Timestamp;
            [Transient] public int Debug;
            public static int Calls;
            public bool Equals(Key<T> other) {Calls++;return Part.Equals(other.Part) && StringComparer.Ordinal.Equals(Name,other.Name);}
            public override bool Equals(object? other)=>other is Key<T> key && Equals(key);
            public override int GetHashCode() {Calls++;return HashCode.Combine(Part,Name);}
        }
        [DurableType("Scope",1)] public readonly partial struct Scope : IEquatable<Scope> {
            [DurableField(1)] private readonly int _tenant;
            [DurableField(2)] private readonly int? _region;
            [DurableField(3)] private readonly World _owner;
            [DurableField(4)] private readonly Dictionary<Key<Scope>,Payload> _map;
            [Transient] private readonly int _cache;
            public static int Calls, Constructions;
            public Scope(int tenant,int? region,World owner,Dictionary<Key<Scope>,Payload> map,int cache=77) {
                _tenant=tenant;_region=region;_owner=owner;_map=map;_cache=cache;Constructions++;
            }
            public int Tenant=>_tenant;
            public int? Region=>_region;
            public World Owner=>_owner;
            public Dictionary<Key<Scope>,Payload> Map=>_map;
            public int Cache=>_cache;
            public bool Equals(Scope other) {Calls++;return _tenant==other._tenant && _region==other._region && ReferenceEquals(_owner,other._owner) && ReferenceEquals(_map,other._map);}
            public override bool Equals(object? other)=>other is Scope scope && Equals(scope);
            public override int GetHashCode() {Calls++;return HashCode.Combine(_tenant,_region,RuntimeHelpers.GetHashCode(_owner),RuntimeHelpers.GetHashCode(_map));}
        }
        [DurableType("Payload",1)] public partial struct Payload {
            [DurableField(1)] public int Amount;
            [DurableField(2)] public World? Owner;
        }
        [DurableType("Box",1)] public partial class Box<T>:IDurableObject { [DurableField(1)] public T Value=default!; }
        [DurableType("World",1)] public partial class World:IDurableObject {
            [DurableField(1)] public Dictionary<Key<int>,int> Primary=new();
            [DurableField(2)] public Dictionary<PlainKey,int> Plain=new();
            [DurableField(3)] public Dictionary<Key<Scope>,Payload> Complex=new();
            [DurableField(4)] public Dictionary<Key<int>,int>? Alias;
            [DurableField(5)] public List<Dictionary<Key<int>,int>[]> Views=new();
            [DurableField(6)] public Box<Dictionary<Key<int>,int>> Box=new();
            [DurableField(7)] public Dictionary<int,Dictionary<Key<Scope>,Payload>> Nested=new();
            [DurableField(8)] public string OriginalName="";
            [DurableField(9)] public int Score;
            public override bool Equals(object? other)=>throw new InvalidOperationException("World equality must not run");
            public override int GetHashCode()=>throw new InvalidOperationException("World hash must not run");
        }
        public static class Host {
            public static StateModelRegistry Models() {var m=new StateModelRegistry();DurableDefinitions.Register(m);return m;}
            private static World Seed() {
                var w=new World();
                for(int i=0;i<32;i++) {
                    string name=new string(("name-"+i).ToCharArray());
                    w.Primary.Add(new(){Part=i,Name=name,Timestamp=100+i,Debug=17},1000+i);
                    w.Plain.Add(new PlainKey(i,300+i,91),2000+i);
                    var scope=new Scope(i,i%2==0?null:i,w,w.Complex);
                    w.Complex.Add(new(){Part=scope,Name=i%2==0?null:name,Timestamp=500+i},new(){Amount=4000+i,Owner=w});
                    if(i==0)w.OriginalName=name;
                }
                w.Alias=w.Primary;w.Views.Add(new[]{w.Primary});w.Box.Value=w.Primary;w.Nested.Add(7,w.Complex);
                return w;
            }
            private static void Replace(World w,int change) {
                var old=w.Primary.Keys.Single(k=>k.Part==0);int value=w.Primary[old];w.Primary.Remove(old);
                if(change==0)old.Debug++;
                if(change==1)old.Timestamp=999;
                if(change==2)old.Name=new string(old.Name!.ToCharArray());
                w.Primary.Add(old,value);
            }
            public static object[] Fixture() {
                var w=Seed();return new object[]{Models(),w,(Action)(()=>Replace(w,1)),(Action)(()=>Replace(w,0)),
                    (Action)(()=>Replace(w,2)),(Action)(()=>w.Primary.Clear()),
                    (Func<int>)(()=>Key<int>.Calls+Key<Scope>.Calls+PlainKey.Calls+Scope.Calls)};
            }
            private static void Require(bool value,string message) {if(!value)throw new InvalidOperationException(message);}
            private static void Check(World w) {
                Require(ReferenceEquals(w.Primary,w.Alias) && ReferenceEquals(w.Primary,w.Views[0][0]) && ReferenceEquals(w.Primary,w.Box.Value),"shared primary");
                Require(ReferenceEquals(w.Complex,w.Nested[7]),"nested map");
                var first=w.Primary.Keys.Single(k=>k.Part==0);
                Require(first.Timestamp==999 && first.Name==w.OriginalName && !ReferenceEquals(first.Name,w.OriginalName),"complete stored key");
                Require(w.Primary.ContainsKey(new(){Part=0,Name=new string(w.OriginalName.ToCharArray()),Timestamp=-1}),"default generic lookup ignores timestamp");
                Require(w.Plain[new PlainKey(0,-500)]==2000,"default ordinary lookup ignores timestamp");
                foreach(var pair in w.Plain)Require(pair.Key.Timestamp==300+pair.Key.Number && pair.Key.Debug==0,"private readonly hydration");
                foreach(var pair in w.Complex) {
                    var s=pair.Key.Part;
                    Require(s.Region==(s.Tenant%2==0?null:s.Tenant) && s.Cache==0,"nested nullable and transient");
                    Require(ReferenceEquals(s.Owner,w) && ReferenceEquals(s.Map,w.Complex) && ReferenceEquals(pair.Value.Owner,w),"identity cycles");
                    Require(pair.Value.Amount==4000+s.Tenant,"nested payload");
                    Require(w.Complex.ContainsKey(new(){Part=new Scope(s.Tenant,s.Region,w,w.Complex,0),Name=pair.Key.Name,Timestamp=-1}),"identity-key lookup");
                }
            }
            public static FrameAddress[] SaveAndLoad(string path) {
                var addresses=new List<FrameAddress>();var models=Models();var w=Seed();var original=w.Primary;
                using(var repo=FixtureGraphRepository.CreateNew(path, models))using(var session=repo.Create(w)) {
                    addresses.Add(session.Commit(new(1000000,1)));
                    Replace(w,0);addresses.Add(session.Commit(new(1000000,1)));
                    Replace(w,1);addresses.Add(session.Commit(new(1000000,1)));
                    Replace(w,2);addresses.Add(session.Commit(new(1000000,1)));
                    var key=w.Primary.Keys.Single(k=>k.Part==1);w.Primary[key]=2222;addresses.Add(session.Commit(new(1000000,1)));
                    w.Score++;addresses.Add(session.Commit(new(1000000,1)));
                    Require(ReferenceEquals(w,session.World) && ReferenceEquals(original,session.World.Primary),"same instances after commit");
                }
                using(var repo=FixtureGraphRepository.OpenExisting(path, models)) {
                    int constructors=Scope.Constructions+PlainKey.Constructions;
                    using var session=repo.Load<World>();
                    Require(constructors==Scope.Constructions+PlainKey.Constructions,"restore bypasses constructors");
                    Check(session.World);addresses.Add(session.Commit(new(1000000,1)));
                    var key=session.World.Primary.Keys.Single(k=>k.Part==1);session.World.Primary[key]+=5;
                    addresses.Add(session.Commit(new(1000000,1)));
                }
                using(var repo=FixtureGraphRepository.OpenExisting(path, models))using(var session=repo.Load<World>()) {
                    Check(session.World);Require(session.World.Primary[session.World.Primary.Keys.Single(k=>k.Part==1)]==2227,"value delta after cold load");
                    addresses.Add(session.Commit(new(1000000,1)));
                }
                return addresses.ToArray();
            }
        }
        """;
}
