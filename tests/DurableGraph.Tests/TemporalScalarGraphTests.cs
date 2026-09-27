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
    public void TemporalScalarGraphPreservesOffsetsInEveryShapeAndDictionarySemanticsAcrossColdCommits() {
        GeneratorTestRun run = RunGenerator(TemporalScalarGraphSource);
        AssertSchemaOnlyCompiles(run);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("TemporalScalarGraph.Host")!;
        StateModelRegistry models = host.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>()();
        object[] fixture = host.GetMethod("FreezeFixture")!.CreateDelegate<Func<object[]>>()();
        var world = (IDurableObject)fixture[0];
        StateModelSnapshot captureModels = models.Snapshot();
        CaptureSession captureSession = new();
        CapturedGraph Capture() {
            using var context = captureSession.BeginCapture(captureModels);
            captureModels.ResolveCurrentModel(world.GetType()).AddRoot(context, world);
            var graph = context.Seal();
            captureSession.Accept(graph);
            return graph;
        }
        CapturedGraph frozen = Capture();
        var frozenBases = frozen.Objects.Where(row => row.Preparation is not null)
            .Select(row => (Row: row, Bytes: row.Preparation!.PrepareBase(row).Body.ToArray())).ToArray();
        ((Action)fixture[1])();
        CapturedGraph mutated = Capture();
        Assert.True(frozenBases.Count(prior => prior.Row.Preparation!.PrepareDelta(prior.Row,
            mutated.Objects.Single(row => row.Id == prior.Row.Id)).HasChanges) >= 9);
        foreach (var prior in frozenBases) {
            Assert.Equal(prior.Bytes, prior.Row.Preparation!.PrepareBase(prior.Row).Body.ToArray());
        }
        using RawBaseDirectory directory = new();
        FrameAddress[] addresses = host.GetMethod("SaveAndLoad")!.CreateDelegate<Func<string, FrameAddress[]>>()(directory.Path);
        using SegmentStore segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory.Path, "state"));
        using var schemaFile = RbfFile.OpenReadOnlyExisting(Path.Combine(directory.Path, "schemas.rbf"));
        SchemaStore schemas = new(schemaFile, readOnly: true);
        using StateRevisionStore states = new(segments);
        StateModelSnapshot snapshot = models.Snapshot(schemas);
        DecodedRevision initial = RevisionDecoder.ReadSnapshot(states, schemas, addresses[0], snapshot);
        ObjectStateRecord timestamps = Assert.Single(initial.Objects, row => row.Kind == ObjectStateKind.Dictionary &&
            row.Layout.Dictionary!.KeySlot.TypeTag == TypeTag.DateTimeOffset &&
            ((IFrozenDictionaryState)row.Content).ComparerKind == DictionaryComparerKind.ScalarDefault);
        foreach (int index in new[] { 1, 13, 15, 17 }) {
            Assert.Empty(states.Read(addresses[index]).LocalObjects);
            Assert.Empty(states.Read(addresses[index]).RemovedObjectIds);
        }
        for (int index = 2; index <= 12; index++) { Assert.NotEmpty(states.Read(addresses[index]).LocalObjects); }
        DateTimeOffset before = new(2026, 9, 10, 8, 0, 0, TimeSpan.FromHours(8));
        DateTimeOffset after = before.ToOffset(TimeSpan.Zero), value = before.AddDays(100);
        ObjectVersionRecord replacement = Assert.Single(states.Read(addresses[11]).LocalObjects, row => row.ObjectId == timestamps.Id.Value);
        Assert.Equal(ObjectVersionKind.Delta, replacement.Kind);
        BinaryPayloadReader reader = new(replacement.Body);
        Assert.Equal(1U, reader.ReadUInt32());
        Assert.True(before.EqualsExact(reader.ReadDateTimeOffset()));
        Assert.Equal(0U, reader.ReadUInt32());
        Assert.Equal(1U, reader.ReadUInt32());
        Assert.True(after.EqualsExact(reader.ReadDateTimeOffset()));
        Assert.True(value.EqualsExact(reader.ReadDateTimeOffset()));
        reader.EnsureFullyConsumed();
        ObjectVersionRecord patch = Assert.Single(states.Read(addresses[12]).LocalObjects, row => row.ObjectId == timestamps.Id.Value);
        Assert.Equal(ObjectVersionKind.Delta, patch.Kind);
        reader = new(patch.Body);
        Assert.Equal(0U, reader.ReadUInt32());
        Assert.Equal(1U, reader.ReadUInt32());
        Assert.True(after.EqualsExact(reader.ReadDateTimeOffset()));
        Assert.True(value.ToOffset(TimeSpan.Zero).EqualsExact(reader.ReadDateTimeOffset()));
        Assert.Equal(0U, reader.ReadUInt32());
        reader.EnsureFullyConsumed();
        ObjectStateRecord composite = Assert.Single(initial.Objects, row => row.Kind == ObjectStateKind.Dictionary &&
            row.Layout.Dictionary!.KeySlot.TypeTag == TypeTag.InlineValue);
        ObjectVersionRecord keyPatch = Assert.Single(states.Read(addresses[14]).LocalObjects, row => row.ObjectId == composite.Id.Value);
        Assert.Equal(ObjectVersionKind.Delta, keyPatch.Kind);
        reader = new(keyPatch.Body);
        Assert.Equal(1U, reader.ReadUInt32());
        Assert.Equal(1, reader.ReadInt32());
        Assert.True(before.EqualsExact(reader.ReadDateTimeOffset()));
        Assert.Equal(0U, reader.ReadUInt32());
        Assert.Equal(1U, reader.ReadUInt32());
        Assert.Equal(1, reader.ReadInt32());
        Assert.True(after.EqualsExact(reader.ReadDateTimeOffset()));
        Assert.Equal(10, reader.ReadInt32());
        reader.EnsureFullyConsumed();
        Assert.All(states.Read(addresses[16]).LocalObjects, row => Assert.Equal(ObjectVersionKind.Delta, row.Kind));
        Assert.NotEmpty(states.Read(addresses[16]).LocalObjects);
        // Read an old revision after all edits: old complete key/value representations remain intact.
        var oldEntry = timestamps.GetDictionaryState<DateTimeOffset, DateTimeOffset>().Entries.ToArray().Single(p => p.Key == before);
        Assert.True(before.EqualsExact(oldEntry.Key));
        Assert.True(value.EqualsExact(oldEntry.Value));
        DecodedRevision final = RevisionDecoder.ReadSnapshot(states, schemas, addresses[^1], snapshot);
        foreach (ObjectStateRecord row in final.Objects.Where(row => row.Kind == ObjectStateKind.Dictionary)) {
            var slot = row.Layout.Dictionary!;
            Assert.Equal(slot.KeySlot.TypeTag == TypeTag.InlineValue || slot.ValueSlot.TypeTag == TypeTag.Int32
                ? DictionaryComparerKind.Application : DictionaryComparerKind.ScalarDefault,
                ((IFrozenDictionaryState)row.Content).ComparerKind);
        }
    }

    private const string TemporalScalarGraphSource = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Generated;
        using Atelia.DurableGraph.Persistence;
        using Atelia.DurableGraph.Storage;
        namespace TemporalScalarGraph;
        [DurableType("temporal.Value",1)] public readonly partial record struct Value<T>(
            [field:DurableField(1)] DateOnly Date,
            [field:DurableField(2)] T Timestamp,
            [field:DurableField(3)] TimeOnly Time);
        [DurableType("temporal.Plain",1)] public partial struct Plain {
            [DurableField(1)] public DateTimeOffset Timestamp;
        }
        [DurableType("temporal.Key",1)] public readonly partial record struct Key(
            [field:DurableField(1)] int Number,
            [field:DurableField(2)] DateTimeOffset Timestamp);
        [DurableType("temporal.Box",1)] public partial class Box<T>:IDurableObject {
            [DurableField(1)] public T Value=default!;
        }
        [DurableType("temporal.World",1)] public partial class World:IDurableObject {
            [DurableField(1)] public DateOnly Date;
            [DurableField(2)] public DateTimeOffset Timestamp;
            [DurableField(3)] public TimeOnly Time;
            [DurableField(4)] public DateTimeOffset? Optional;
            [DurableField(5)] public Value<DateTimeOffset> Record;
            [DurableField(6)] public Box<DateTimeOffset> Box=new();
            [DurableField(7)] public DateTimeOffset[] Vector=[];
            [DurableField(8)] public DateTimeOffset[,] Matrix=new DateTimeOffset[1,1];
            [DurableField(9)] public DateTimeOffset[,,] Cube=new DateTimeOffset[1,1,1];
            [DurableField(10)] public DateTimeOffset[,,,] Quad=new DateTimeOffset[1,1,1,1];
            [DurableField(11)] public List<DateTimeOffset> List=new();
            [DurableField(12)] public Dictionary<DateTimeOffset,DateTimeOffset> Timestamps=new();
            [DurableField(13)] public Dictionary<DateOnly,TimeOnly> Dates=new();
            [DurableField(14)] public Dictionary<TimeOnly,DateOnly> Times=new();
            [DurableField(15)] public Dictionary<DateTimeOffset,int> Distinct=new(new ExactComparer());
            [DurableField(16)] public DateOnly?[] OptionalDates=[];
            [DurableField(17)] public List<TimeOnly?> OptionalTimes=new();
            [DurableField(18)] public Value<DateTimeOffset>?[] OptionalRecords=[];
            [DurableField(19)] public List<Dictionary<DateOnly,TimeOnly>[]> Views=new();
            [DurableField(20)] public DateOnly[,] DateMatrix=new DateOnly[1,1];
            [DurableField(21)] public TimeOnly[,,] TimeCube=new TimeOnly[1,1,1];
            [DurableField(22)] public Value<DateOnly>[,,,] RecordQuad=new Value<DateOnly>[1,1,1,1];
            [DurableField(23)] public Dictionary<Key,int> Composite=new(new KeyComparer());
            [DurableField(24)] public Plain Inline;
        }
        public sealed class ExactComparer:IEqualityComparer<DateTimeOffset> {
            public bool Equals(DateTimeOffset x,DateTimeOffset y)=>x.EqualsExact(y);
            public int GetHashCode(DateTimeOffset x)=>HashCode.Combine(x.Ticks,x.Offset);
        }
        public sealed class KeyComparer:IEqualityComparer<Key> {
            public bool Equals(Key x,Key y)=>x.Number==y.Number;
            public int GetHashCode(Key x)=>x.Number;
        }
        public static class Host {
            private static readonly DateOnly Date=new(2024,2,29);
            private static readonly DateTimeOffset A=new(2026,9,10,8,0,0,TimeSpan.FromHours(8));
            private static readonly DateTimeOffset B=A.ToOffset(TimeSpan.Zero);
            public static StateModelRegistry Models() {
                var models=new StateModelRegistry();DurableDefinitions.Register(models);
                models.UseDictionaryComparer<DateTimeOffset,int>(new ExactComparer());
                models.UseDictionaryComparer<Key,int>(new KeyComparer());return models;
            }
            private static void Require(bool value,string message) {if(!value)throw new InvalidOperationException(message);}
            private static void Exact(DateTimeOffset actual,DateTimeOffset expected,string name)=>Require(actual.EqualsExact(expected),name);
            private static World Seed() {
                var w=new World {Date=Date,Timestamp=A,Time=TimeOnly.MaxValue,Optional=A,
                    Record=new(Date,A,TimeOnly.MinValue),Vector=Enumerable.Repeat(A,32).ToArray(),Inline=new(){Timestamp=A}};
                w.Box.Value=A;w.Matrix[0,0]=w.Cube[0,0,0]=w.Quad[0,0,0,0]=A;
                w.List.AddRange(w.Vector);
                for(int i=0;i<32;i++) {w.Timestamps.Add(A.AddDays(i),A.AddDays(100+i));w.Composite.Add(new(i+1,A),10+i);}
                w.Dates.Add(Date,TimeOnly.MaxValue);w.Dates.Add(DateOnly.MinValue,TimeOnly.MinValue);
                w.Times.Add(TimeOnly.MaxValue,Date);w.Times.Add(TimeOnly.MinValue,DateOnly.MaxValue);
                w.Distinct.Add(A,10);w.Distinct.Add(B,20);
                w.OptionalDates=new DateOnly?[]{null,DateOnly.MinValue,Date};
                w.OptionalTimes.AddRange(new TimeOnly?[]{null,TimeOnly.MinValue,TimeOnly.MaxValue});
                w.OptionalRecords=new Value<DateTimeOffset>?[]{null,w.Record};
                w.Views.Add(new[]{w.Dates});w.DateMatrix[0,0]=Date;w.TimeCube[0,0,0]=TimeOnly.MaxValue;
                w.RecordQuad[0,0,0,0]=new(Date,DateOnly.MaxValue,TimeOnly.MinValue);return w;
            }
            private static void Check(World w) {
                Require(w.Date==Date && w.Time==TimeOnly.MaxValue,"direct values");
                Exact(w.Timestamp,B,"direct offset");Exact(w.Optional!.Value,B,"nullable offset");
                Exact(w.Record.Timestamp,B,"record offset");Exact(w.Box.Value,B,"generic offset");Exact(w.Inline.Timestamp,B,"inline offset");
                Exact(w.Vector[0],B,"vector offset");Exact(w.Matrix[0,0],B,"matrix offset");
                Exact(w.Cube[0,0,0],B,"cube offset");Exact(w.Quad[0,0,0,0],B,"quad offset");Exact(w.List[0],B,"list offset");
                Exact(w.Vector[1],A,"untouched vector");Exact(w.List[1],A,"untouched list");
                var entry=w.Timestamps.Single(p=>p.Key==A);Exact(entry.Key,B,"replacement key");Exact(entry.Value,A.AddDays(100).ToOffset(TimeSpan.Zero),"value patch");
                Require(w.Distinct.Count==2 && w.Distinct[A]==10 && w.Distinct[B]==20,"application exact comparer");
                Require(w.Dates[Date]==TimeOnly.MaxValue && w.Dates[DateOnly.MinValue]==TimeOnly.MinValue,"DateOnly default key");
                Require(w.Times[TimeOnly.MaxValue]==Date && w.Times[TimeOnly.MinValue]==DateOnly.MaxValue,"TimeOnly default key");
                Require(ReferenceEquals(w.Views[0][0],w.Dates),"nested shared dictionary");
                Require(w.OptionalDates[0] is null && w.OptionalDates[1]==DateOnly.MinValue && w.OptionalDates[2]==Date,"nullable date vector");
                Require(w.OptionalTimes[0] is null && w.OptionalTimes[1]==TimeOnly.MinValue && w.OptionalTimes[2]==TimeOnly.MaxValue,"nullable time list");
                Require(w.OptionalRecords[0] is null,"nullable record array");Exact(w.OptionalRecords[1]!.Value.Timestamp,A,"independent record copy");
                Require(w.DateMatrix[0,0]==Date && w.TimeCube[0,0,0]==TimeOnly.MaxValue && w.RecordQuad[0,0,0,0]==new Value<DateOnly>(Date,DateOnly.MaxValue,TimeOnly.MinValue),"multidimensional compositions");
                Require(w.Composite.ContainsKey(new(1,A.AddDays(200))),"application lookup ignores timestamp");
                Exact(w.Composite.Single(p=>p.Key.Number==1).Key.Timestamp,B,"complete composite key timestamp");
            }
            public static object[] FreezeFixture() {
                var w=Seed();
                return new object[]{w,(Action)(()=>{
                    w.Timestamp=B;w.Optional=B;w.Record=w.Record with {Timestamp=B};w.Inline.Timestamp=B;
                    w.Box.Value=B;w.Vector[0]=B;w.Matrix[0,0]=B;w.Cube[0,0,0]=B;w.Quad[0,0,0,0]=B;w.List[0]=B;
                    var value=w.Timestamps[A];w.Timestamps.Remove(A);w.Timestamps.Add(B,value);
                    var key=w.Composite.Keys.Single(k=>k.Number==1);w.Composite.Remove(key);w.Composite.Add(key with {Timestamp=B},10);
                })};
            }
            public static FrameAddress[] SaveAndLoad(string path) {
                var addresses=new List<FrameAddress>();var models=Models();var w=Seed();
                using(var repo=FixtureGraphRepository.CreateNew(path, models))using(var session=repo.Create(w)) {
                    void Save()=>addresses.Add(session.Commit(new(1000000,1)));
                    Save();Save();
                    w.Timestamp=B;w.Inline.Timestamp=B;Save();w.Optional=B;Save();w.Record=w.Record with {Timestamp=B};Save();
                    w.Box.Value=B;Save();w.Vector[0]=B;Save();w.Matrix[0,0]=B;Save();
                    w.Cube[0,0,0]=B;Save();w.Quad[0,0,0,0]=B;Save();w.List[0]=B;Save();
                    var value=w.Timestamps[A];w.Timestamps.Remove(A);w.Timestamps.Add(B,value);Save();
                    w.Timestamps[B]=value.ToOffset(TimeSpan.Zero);Save();
                    w.Timestamps[A]=w.Timestamps[B];Require(w.Timestamps.Single(p=>p.Key==A).Key.EqualsExact(B),"indexer preserves original key");Save();
                    var key=w.Composite.Keys.Single(k=>k.Number==1);var v=w.Composite[key];w.Composite.Remove(key);w.Composite.Add(key with {Timestamp=B},v);Save();
                }
                using(var repo=FixtureGraphRepository.OpenExisting(path, models))using(var session=repo.Load<World>()) {
                    Check(session.World);addresses.Add(session.Commit(new(1000000,1)));
                    session.World.List[1]=B;addresses.Add(session.Commit(new(1000000,1)));
                }
                using(var repo=FixtureGraphRepository.OpenExisting(path, models))using(var session=repo.Load<World>()) {
                    Exact(session.World.List[1],B,"delta after cold load");addresses.Add(session.Commit(new(1000000,1)));
                }
                return addresses.ToArray();
            }
        }
        """;
}
