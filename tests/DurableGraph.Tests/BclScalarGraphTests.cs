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
    public void BclScalarGraphPreservesRepresentationsAcrossEveryContainerShapeAndColdCommit() {
        GeneratorTestRun run = RunGenerator(BclScalarGraphSource);
        AssertSchemaOnlyCompiles(run);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("BclScalarGraph.Host")!;
        using RawBaseDirectory directory = new();
        FrameAddress[] addresses = host.GetMethod("SaveAndLoad")!.CreateDelegate<Func<string, FrameAddress[]>>()(directory.Path);
        StateModelRegistry models = host.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>()();
        using SegmentStore segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory.Path, "state"));
        using var schemaFile = RbfFile.OpenReadOnlyExisting(Path.Combine(directory.Path, "schemas.rbf"));
        SchemaStore schemas = new(schemaFile, readOnly: true);
        using StateRevisionStore states = new(segments);
        StateModelSnapshot snapshot = models.Snapshot(schemas);
        DecodedRevision initial = RevisionDecoder.ReadSnapshot(states, schemas, addresses[0], snapshot);
        ObjectStateRecord decimals = Assert.Single(initial.Objects, row => row.Kind == ObjectStateKind.Dictionary &&
            row.Layout.Dictionary!.KeySlot.TypeTag == TypeTag.Decimal &&
            ((IFrozenDictionaryState)row.Content).ComparerKind == DictionaryComparerKind.ScalarDefault);
        foreach (int index in new[] { 1, 13 }) {
            Assert.Empty(states.Read(addresses[index]).LocalObjects);
            Assert.Empty(states.Read(addresses[index]).RemovedObjectIds);
        }
        // Each isolated scale-only edit must persist, including the enclosing World and generic Box bodies.
        for (int index = 2; index <= 12; index++) {
            Assert.NotEmpty(states.Read(addresses[index]).LocalObjects);
        }
        ObjectVersionRecord replacement = Assert.Single(states.Read(addresses[11]).LocalObjects,
            row => row.ObjectId == decimals.Id.Value);
        Assert.Equal(ObjectVersionKind.Delta, replacement.Kind);
        BinaryPayloadReader reader = new(replacement.Body);
        Assert.Equal(1U, reader.ReadUInt32());
        Assert.Equal(decimal.GetBits(1.0m), decimal.GetBits(reader.ReadDecimal()));
        Assert.Equal(0U, reader.ReadUInt32());
        Assert.Equal(1U, reader.ReadUInt32());
        Assert.Equal(decimal.GetBits(1.00m), decimal.GetBits(reader.ReadDecimal()));
        Assert.Equal(decimal.GetBits(10.0m), decimal.GetBits(reader.ReadDecimal()));
        reader.EnsureFullyConsumed();
        ObjectVersionRecord patch = Assert.Single(states.Read(addresses[12]).LocalObjects,
            row => row.ObjectId == decimals.Id.Value);
        Assert.Equal(ObjectVersionKind.Delta, patch.Kind);
        reader = new(patch.Body);
        Assert.Equal(0U, reader.ReadUInt32());
        Assert.Equal(1U, reader.ReadUInt32());
        Assert.Equal(decimal.GetBits(1.00m), decimal.GetBits(reader.ReadDecimal()));
        Assert.Equal(decimal.GetBits(10.00m), decimal.GetBits(reader.ReadDecimal()));
        Assert.Equal(0U, reader.ReadUInt32());
        reader.EnsureFullyConsumed();
        // Earlier frozen states are unchanged after all same-instance mutations and subsequent reads.
        FrozenDictionaryState<decimal, decimal> old = decimals.GetDictionaryState<decimal, decimal>();
        Assert.Equal(decimal.GetBits(1.0m), decimal.GetBits(old.Entries.ToArray().Single(p => p.Key == 1m).Key));
        Assert.Equal(decimal.GetBits(10.0m), decimal.GetBits(old.Entries.ToArray().Single(p => p.Key == 1m).Value));
        DecodedRevision final = RevisionDecoder.ReadSnapshot(states, schemas, addresses[^1], snapshot);
        foreach (ObjectStateRecord row in final.Objects.Where(row => row.Kind == ObjectStateKind.Dictionary)) {
            Assert.Equal(row.Layout.Dictionary!.KeySlot.TypeTag == TypeTag.Decimal &&
                row.Layout.Dictionary.ValueSlot.TypeTag == TypeTag.Int32
                    ? DictionaryComparerKind.Application : DictionaryComparerKind.ScalarDefault,
                ((IFrozenDictionaryState)row.Content).ComparerKind);
        }
    }

    private const string BclScalarGraphSource = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Generated;
        using Atelia.DurableGraph.Persistence;
        using Atelia.DurableGraph.Storage;
        namespace BclScalarGraph;
        [DurableType("bcl.Value",1)] public readonly partial record struct Value<T>(
            [field:DurableField(1)] Guid Id,
            [field:DurableField(2)] T Amount,
            [field:DurableField(3)] TimeSpan Duration);
        [DurableType("bcl.Box",1)] public partial class Box<T>:IDurableObject {
            [DurableField(1)] public T Value=default!;
        }
        [DurableType("bcl.World",1)] public partial class World:IDurableObject {
            [DurableField(1)] public Guid Id;
            [DurableField(2)] public decimal Amount;
            [DurableField(3)] public TimeSpan Duration;
            [DurableField(4)] public decimal? Optional;
            [DurableField(5)] public Value<decimal> Record;
            [DurableField(6)] public Box<decimal> Box=new();
            [DurableField(7)] public decimal[] Vector=[];
            [DurableField(8)] public decimal[,] Matrix=new decimal[1,1];
            [DurableField(9)] public decimal[,,] Cube=new decimal[1,1,1];
            [DurableField(10)] public decimal[,,,] Quad=new decimal[1,1,1,1];
            [DurableField(11)] public List<decimal> List=new();
            [DurableField(12)] public Dictionary<decimal,decimal> Decimals=new();
            [DurableField(13)] public Dictionary<Guid,TimeSpan> Identifiers=new();
            [DurableField(14)] public Dictionary<TimeSpan,Guid> Durations=new();
            [DurableField(15)] public Dictionary<decimal,int> Distinct=new(new BitsComparer());
            [DurableField(16)] public Guid?[] OptionalIds=[];
            [DurableField(17)] public List<TimeSpan?> OptionalDurations=new();
            [DurableField(18)] public Value<decimal>?[] OptionalRecords=[];
            [DurableField(19)] public List<Dictionary<Guid,TimeSpan>[]> Views=new();
            [DurableField(20)] public Guid[,] IdMatrix=new Guid[1,1];
            [DurableField(21)] public TimeSpan[,,] TickCube=new TimeSpan[1,1,1];
            [DurableField(22)] public Value<Guid>[,,,] RecordQuad=new Value<Guid>[1,1,1,1];
        }
        public sealed class BitsComparer:IEqualityComparer<decimal> {
            public bool Equals(decimal x,decimal y)=>decimal.GetBits(x).SequenceEqual(decimal.GetBits(y));
            public int GetHashCode(decimal x) {var b=decimal.GetBits(x);return HashCode.Combine(b[0],b[1],b[2],b[3]);}
        }
        public static class Host {
            private static readonly Guid Id=Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
            public static StateModelRegistry Models() {
                var models=new StateModelRegistry();DurableDefinitions.Register(models);
                models.UseDictionaryComparer<decimal,int>(new BitsComparer());return models;
            }
            private static void Require(bool value,string message) {if(!value)throw new InvalidOperationException(message);}
            private static void Bits(decimal actual,decimal expected,string name)=>Require(decimal.GetBits(actual).SequenceEqual(decimal.GetBits(expected)),name);
            private static World Seed() {
                var w=new World {Id=Id,Amount=1.0m,Duration=TimeSpan.MinValue,Optional=1.0m,
                    Record=new(Id,1.0m,TimeSpan.MaxValue),Vector=Enumerable.Repeat(1.0m,32).ToArray()};
                w.Box.Value=1.0m;w.Matrix[0,0]=w.Cube[0,0,0]=w.Quad[0,0,0,0]=1.0m;
                w.List.AddRange(w.Vector);
                for(int i=1;i<=32;i++)w.Decimals.Add(new decimal(i*10,0,0,false,1),new decimal(i*100,0,0,false,1));
                w.Identifiers.Add(Id,TimeSpan.MinValue);w.Identifiers.Add(Guid.Empty,TimeSpan.MaxValue);
                w.Durations.Add(TimeSpan.MinValue,Id);w.Durations.Add(TimeSpan.MaxValue,Guid.Empty);
                w.Distinct.Add(1.0m,10);w.Distinct.Add(1.00m,20);
                w.OptionalIds=new Guid?[]{null,Guid.Empty,Id};
                w.OptionalDurations.AddRange(new TimeSpan?[]{null,TimeSpan.Zero,TimeSpan.MinValue,TimeSpan.MaxValue});
                w.OptionalRecords=new Value<decimal>?[]{null,w.Record};
                w.Views.Add(new[]{w.Identifiers});w.IdMatrix[0,0]=Id;w.TickCube[0,0,0]=TimeSpan.MinValue;
                w.RecordQuad[0,0,0,0]=new(Id,Id,TimeSpan.MaxValue);return w;
            }
            private static void Check(World w) {
                Require(w.Id==Id && w.Duration==TimeSpan.MinValue,"direct BCL values");
                Bits(w.Amount,1.00m,"direct scale");Bits(w.Optional!.Value,1.00m,"nullable scale");
                Bits(w.Record.Amount,1.00m,"record scale");Bits(w.Box.Value,1.00m,"generic scale");
                Bits(w.Vector[0],1.00m,"vector scale");Bits(w.Matrix[0,0],1.00m,"matrix scale");
                Bits(w.Cube[0,0,0],1.00m,"cube scale");Bits(w.Quad[0,0,0,0],1.00m,"quad scale");Bits(w.List[0],1.00m,"list scale");
                Bits(w.Vector[1],1.0m,"untouched vector");Bits(w.List[1],1.0m,"untouched list");
                var entry=w.Decimals.Single(p=>p.Key==1m);Bits(entry.Key,1.00m,"replacement key");Bits(entry.Value,10.00m,"value patch");
                Require(w.Distinct.Count==2 && w.Distinct[1.0m]==10 && w.Distinct[1.00m]==20,"application bits comparer");
                Require(w.Identifiers[Id]==TimeSpan.MinValue && w.Identifiers[Guid.Empty]==TimeSpan.MaxValue,"Guid default key");
                Require(w.Durations[TimeSpan.MinValue]==Id && w.Durations[TimeSpan.MaxValue]==Guid.Empty,"TimeSpan default key");
                Require(ReferenceEquals(w.Views[0][0],w.Identifiers),"nested shared Dictionary");
                Require(w.OptionalIds[0] is null && w.OptionalIds[1]==Guid.Empty && w.OptionalIds[2]==Id,"nullable Guid vector");
                Require(w.OptionalDurations[0] is null && w.OptionalDurations[1]==TimeSpan.Zero && w.OptionalDurations[2]==TimeSpan.MinValue && w.OptionalDurations[3]==TimeSpan.MaxValue,"nullable TimeSpan list");
                Require(w.OptionalRecords[0] is null,"nullable record array");Bits(w.OptionalRecords[1]!.Value.Amount,1.0m,"independent record copy");
                Require(w.IdMatrix[0,0]==Id && w.TickCube[0,0,0]==TimeSpan.MinValue && w.RecordQuad[0,0,0,0]==new Value<Guid>(Id,Id,TimeSpan.MaxValue),"BCL multidimensional combinations");
            }
            public static FrameAddress[] SaveAndLoad(string path) {
                var addresses=new List<FrameAddress>();var models=Models();var w=Seed();
                using(var repo=FixtureGraphRepository.CreateNew(path, models))using(var session=repo.Create(w)) {
                    void Save()=>addresses.Add(session.Commit(new(1000000,1)));
                    Save();Save();
                    w.Amount=1.00m;Save();w.Optional=1.00m;Save();w.Record=w.Record with {Amount=1.00m};Save();
                    w.Box.Value=1.00m;Save();w.Vector[0]=1.00m;Save();w.Matrix[0,0]=1.00m;Save();
                    w.Cube[0,0,0]=1.00m;Save();w.Quad[0,0,0,0]=1.00m;Save();w.List[0]=1.00m;Save();
                    // Indexer alone preserves an existing numeric-equal key; explicitly replace its stored representation.
                    decimal value=w.Decimals[1.0m];w.Decimals.Remove(1.0m);w.Decimals.Add(1.00m,value);Save();
                    w.Decimals[1.00m]=10.00m;Save();
                }
                using(var repo=FixtureGraphRepository.OpenExisting(path, models))using(var session=repo.Load<World>()) {
                    Check(session.World);addresses.Add(session.Commit(new(1000000,1)));
                }
                return addresses.ToArray();
            }
        }
        """;
}
