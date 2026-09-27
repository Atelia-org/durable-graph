using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Build;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BclScalarGeneratedBodiesUseStaticCodecsAndPreserveDecimalRepresentation(bool family) {
        GeneratorTestRun run = RunGenerator(family ? BclScalarFamilyBodySource :
            FusedDeltaSource(["System.Guid", "decimal", "System.TimeSpan"]));
        AssertSchemaOnlyCompiles(run);
        string generated = GeneratedSource(run, family ? "DurableGenericStates.g.cs" : "DurableStates.g.cs");
        if (!family) Assert.DoesNotContain(run.GeneratedSources, item => item.HintName == "DurableGenericStates.g.cs");
        foreach (string type in new[] { "Guid", "Decimal", "TimeSpan" }) {
            Assert.Contains("writer.Write" + type + "(", generated);
            Assert.Contains("reader.Read" + type + "()", generated);
        }
        Assert.Contains("ScalarStateEquality.DecimalEquals(in ", generated);
        Assert.DoesNotContain("ValueSlotCodec", generated);
        Assert.DoesNotContain("PrimitiveSlotCodecs", generated);
        Type host = EmitAndLoad(run.OutputCompilation).GetType(family ? "Host" : "FusedDelta.Host")!;
        var prepare = host.GetMethod("Prepare1")!.CreateDelegate<Func<byte[], byte[], PreparedDeltaBody>>();
        var apply = host.GetMethod("Apply1")!.CreateDelegate<Func<byte[], byte[], byte[]>>();
        // Independent three-slot Base bytes: network-order Guid, decimal [lo,mid,hi,flags], ticks ZigZag.
        const string guid = "00112233445566778899AABBCCDDEEFF";
        const string oneScale1 = "0A000000000000000000000000000100";
        const string oneScale2 = "64000000000000000000000000000200";
        byte[] prior = Convert.FromHexString(guid + oneScale1 + "01");
        byte[] current = Convert.FromHexString(guid + oneScale2 + "01");
        PreparedDeltaBody delta = prepare(prior, current);
        Assert.True(delta.HasChanges);
        Assert.Equal(Convert.FromHexString("02" + oneScale2), delta.Body.ToArray());
        Assert.Equal(current, apply(prior, delta.Body.ToArray()));
        Assert.False(prepare(current, current).HasChanges);
        Assert.Throws<InvalidDataException>(() => apply(prior, Convert.FromHexString("02" + oneScale1)));
        Assert.Throws<InvalidDataException>(() => apply(prior, delta.Body.ToArray().Concat(new byte[] { 0 }).ToArray()));

        byte[] zero = Convert.FromHexString(guid + "00000000000000000000000000000000" + "00");
        byte[] negativeZero = Convert.FromHexString(guid + "00000000000000000000000000000080" + "00");
        Assert.True(prepare(zero, negativeZero).HasChanges);
        Assert.Equal(negativeZero, apply(zero, prepare(zero, negativeZero).Body.ToArray()));
        byte[] other = Convert.FromHexString("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF" + oneScale2 + "FEFFFFFFFFFFFFFFFF01");
        Assert.Equal(other, apply(current, prepare(current, other).Body.ToArray()));
        if (family) {
            var equal = host.GetMethod("Equal")!.CreateDelegate<Func<byte[], byte[], bool>>();
            Assert.False(equal(prior, current));
            Assert.True(equal(current, current));
        }
    }

    [Fact]
    public void BclScalarsComposeThroughRecordGenericNullableBaseAndEveryContainerShape() {
        GeneratorTestRun run = RunGenerator(BclScalarCompositionSource);
        AssertSchemaOnlyCompiles(run);
        string manifest = GeneratedSource(run, "DurableGraphSchemaHistoryCandidates.g.cs");
        Assert.Contains("manifest:9", manifest);
        Assert.Contains("// base:nQmFzZQ==(b20)|1", manifest);
        Assert.Contains("q(b20)", manifest);
        Assert.Contains("a4(b19)", manifest);
        Assert.Contains("nUGhhbnRvbQ==(b21)", manifest);
        var action = EmitAndLoad(run.OutputCompilation).GetType("Host")!.GetMethod("RoundTrip")!
            .CreateDelegate<Func<string, bool>>();
        string path = Path.Combine(Path.GetTempPath(), "bcl-scalar-generator-" + Guid.NewGuid().ToString("N"));
        try { Assert.True(action(path)); }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    [Theory]
    [InlineData("Guid", "System")]
    [InlineData("Guid", "Custom")]
    [InlineData("TimeSpan", "System")]
    [InlineData("TimeSpan", "Custom")]
    [InlineData("Decimal", "System")]
    [InlineData("Decimal", "Custom")]
    public void BclScalarRecognitionRejectsSourceDefinedLookalikes(string name, string ns) {
        GeneratorTestRun run = RunGenerator($$"""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            namespace {{ns}} { public struct {{name}} { public int Data; } }
            [DurableType("Fake",1)] public partial class Fake:IDurableObject {
                [DurableField(1)] public {{ns}}.{{name}} Value;
            }
            """);
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0007");
        Assert.DoesNotContain(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "CS8785");
    }

    [Theory]
    [InlineData("long", "System.TimeSpan")]
    [InlineData("double", "decimal")]
    [InlineData("System.Guid", "decimal")]
    public void BclScalarRetypeRequiresExplicitOwnerVersion(string before, string after) {
        string Source(string type) => "using Atelia.DurableGraph; [DurableType(\"World\",1)] " +
            "public partial class World:IDurableObject { [DurableField(1)] public " + type + " Value; }";
        using AncestryHistoryDirectory history = new();
        GeneratorTestRun first = RunGenerator(Source(before));
        AssertSchemaOnlyCompiles(first);
        new SchemaHistoryTool().Publish(history.WriteManifest(first), history.History);
        GeneratorTestRun changed = RunGenerator(Source(after), history.ReadAdditionalTexts());
        Assert.Contains(changed.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0015");
    }

    private const string BclScalarFamilyBodySource = """
        using System;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Serialization;
        using Body = Atelia.DurableGraph.Generated.Family_4974656D.BodyV1;
        [DurableType("Trigger",1)] public partial struct Trigger<T> { [DurableField(1)] public T Value; }
        [DurableType("Item",1)] public partial class Item:IDurableObject {
            [DurableField(1)] public Guid Id;
            [DurableField(2)] public decimal Amount;
            [DurableField(3)] public TimeSpan Duration;
        }
        public static class Host {
            private static readonly DurableSchema Schema = new("Item",1,
                new DurableFieldInfo(1,TypeTag.Guid),new DurableFieldInfo(2,TypeTag.Decimal),new DurableFieldInfo(3,TypeTag.TimeSpan));
            public static PreparedDeltaBody Prepare1(byte[] oldBytes,byte[] newBytes) {
                var oldReader=new BinaryPayloadReader(oldBytes);var newReader=new BinaryPayloadReader(newBytes);
                var prior=Body.Read(ref oldReader,Schema);var current=Body.Read(ref newReader,Schema);
                oldReader.EnsureFullyConsumed();newReader.EnsureFullyConsumed();
                return Body.PrepareDelta(in prior,in current,Schema);
            }
            public static byte[] Apply1(byte[] oldBytes,byte[] delta) {
                var oldReader=new BinaryPayloadReader(oldBytes);var reader=new BinaryPayloadReader(delta);
                var prior=Body.Read(ref oldReader,Schema);var current=Body.Apply(ref reader,in prior,Schema);
                oldReader.EnsureFullyConsumed();reader.EnsureFullyConsumed();
                return Body.PrepareBase(in current,Schema).Body.ToArray();
            }
            public static bool Equal(byte[] left,byte[] right) {
                var l=new BinaryPayloadReader(left);var r=new BinaryPayloadReader(right);
                var first=Body.Read(ref l,Schema);var second=Body.Read(ref r,Schema);
                return Body.StateEquals(in first,in second,Schema);
            }
        }
        """;

    private const string BclScalarCompositionSource = """
        using System;
        using System.Collections.Generic;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Persistence;
        using Atelia.DurableGraph.Generated;
        [DurableType("Part",1)] public readonly partial record struct Part(
            [field:DurableField(1)] Guid Id,[field:DurableField(2)] decimal Amount,[field:DurableField(3)] TimeSpan Duration);
        [DurableType("Pair",1)] public partial struct Pair<T,U> { [DurableField(1)] public T First; [DurableField(2)] public U Second; }
        [DurableType("Base",1)] public partial class Base<T>:IDurableObject { [DurableField(1)] public T Value; }
        [DurableType("Phantom",1)] public partial class Phantom<T>:IDurableObject { [DurableField(1)] public int Number; }
        [DurableType("World",1)] public partial class World:Base<decimal> {
            [DurableField(1)] public readonly Guid Id;
            [DurableField(2)] public decimal? Optional;
            [DurableField(3)] public Pair<Guid,TimeSpan> Pair;
            [DurableField(4)] public Part Part;
            [DurableField(5)] public Guid[] Vector=[];
            [DurableField(6)] public decimal[,] Plane=new decimal[1,1];
            [DurableField(7)] public TimeSpan[,,] Cube=new TimeSpan[1,1,1];
            [DurableField(8)] public Guid[,,,] Rank4=new Guid[1,1,1,1];
            [DurableField(9)] public List<decimal?> List=new();
            [DurableField(10)] public Dictionary<Guid,List<decimal?>> Map=new();
            [DurableField(11)] public Dictionary<TimeSpan,Part> Durations=new();
            [DurableField(12)] public List<Dictionary<decimal,Guid[]>[]> Nested=new();
            [DurableField(13)] public Phantom<TimeSpan> Phantom=new();
            [DurableField(14)] public Pair<decimal?,Part?> Wrapped;
            public World(Guid id) { Id=id; }
        }
        public static class Host {
            private static int Flags(decimal value)=>decimal.GetBits(value)[3];
            public static bool RoundTrip(string path) {
                var models=new StateModelRegistry();DurableDefinitions.Register(models);
                var id=Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");var delay=TimeSpan.MinValue;
                var world=new World(id){Value=1.0m,Optional=1.0m,Pair=new(){First=id,Second=delay},Part=new(id,1.0m,delay)};
                world.Vector=[id];world.Plane[0,0]=1.0m;world.Cube[0,0,0]=delay;world.Rank4[0,0,0,0]=id;
                world.List.AddRange(new decimal?[]{null,1.0m});world.Map.Add(id,world.List);
                world.Durations.Add(delay,world.Part);world.Nested.Add(new[]{new Dictionary<decimal,Guid[]>{{1.0m,world.Vector}}});
                world.Wrapped=new(){First=1.0m,Second=world.Part};
                using(var repo=FixtureGraphRepository.CreateNew(path, models))using(var session=repo.Create(world)) {
                    session.Commit(new(1000000,1));
                    world.Value=1.00m;world.Optional=1.00m;world.Part=world.Part with {Amount=1.00m};
                    world.Plane[0,0]=1.00m;world.List[1]=1.00m;world.Wrapped.First=1.00m;
                    session.Commit(new(1000000,1));
                }
                using(var repo=FixtureGraphRepository.OpenExisting(path, models))using(var session=repo.Load<World>()) {
                    var w=session.World;
                    if(w.Id!=id || Flags(w.Value)!=0x20000 || Flags(w.Optional!.Value)!=0x20000 || Flags(w.Part.Amount)!=0x20000 ||
                        Flags(w.Plane[0,0])!=0x20000 || Flags(w.List[1]!.Value)!=0x20000 || Flags(w.Wrapped.First!.Value)!=0x20000) return false;
                    if(w.Pair.First!=id || w.Pair.Second!=delay || w.Cube[0,0,0]!=delay || w.Rank4[0,0,0,0]!=id ||
                        w.Durations[delay].Duration!=delay || w.Wrapped.Second!.Value.Id!=id ||
                        !ReferenceEquals(w.Map[id],w.List) || !ReferenceEquals(w.Nested[0][0][1m],w.Vector)) return false;
                    session.Commit(new(1000000,1));return true;
                }
            }
        }
        """;
}
