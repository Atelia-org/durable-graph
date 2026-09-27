using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Build;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TemporalScalarGeneratedBodiesUseStaticCodecsAndPreserveOffset(bool family) {
        GeneratorTestRun run = RunGenerator(family ? TemporalScalarFamilyBodySource :
            FusedDeltaSource(["System.DateOnly", "System.TimeOnly", "System.DateTimeOffset"]));
        AssertSchemaOnlyCompiles(run);
        string generated = GeneratedSource(run, family ? "DurableGenericStates.g.cs" : "DurableStates.g.cs");
        if (!family) Assert.DoesNotContain(run.GeneratedSources, item => item.HintName == "DurableGenericStates.g.cs");
        foreach (string type in new[] { "DateOnly", "TimeOnly", "DateTimeOffset" }) {
            Assert.Contains("writer.Write" + type + "(", generated);
            Assert.Contains("reader.Read" + type + "()", generated);
        }
        Assert.Contains(".EqualsExact(", generated);
        Assert.DoesNotContain("ValueSlotCodec", generated);
        Assert.DoesNotContain("PrimitiveSlotCodecs", generated);
        Type host = EmitAndLoad(run.OutputCompilation).GetType(family ? "Host" : "FusedDelta.Host")!;
        var prepare = host.GetMethod("Prepare1")!.CreateDelegate<Func<byte[], byte[], PreparedDeltaBody>>();
        var apply = host.GetMethod("Apply1")!.CreateDelegate<Func<byte[], byte[], byte[]>>();
        // Independent bytes: day 1, time tick 1, UTC MinValue represented as clock +01h / +02h.
        // DTO default Equals considers these timestamps equal; persistent comparison must not.
        const string offset1 = "80D0918E860178";
        const string offset2 = "80A0A39C8C02F001";
        byte[] prior = Convert.FromHexString("0101" + offset1);
        byte[] current = Convert.FromHexString("0101" + offset2);
        PreparedDeltaBody delta = prepare(prior, current);
        Assert.True(delta.HasChanges);
        Assert.Equal(Convert.FromHexString("04" + offset2), delta.Body.ToArray());
        Assert.Equal(current, apply(prior, delta.Body.ToArray()));
        Assert.False(prepare(current, current).HasChanges);
        Assert.Throws<InvalidDataException>(() => apply(prior, Convert.FromHexString("04" + offset1)));
        Assert.Throws<InvalidDataException>(() => apply(prior, delta.Body.ToArray().Concat(new byte[] { 0 }).ToArray()));
        // Change both other leaves independently, including their greatest representable values.
        byte[] maxDate = Convert.FromHexString("DAF3DE0101" + offset2);
        PreparedDeltaBody dayDelta = prepare(current, maxDate);
        Assert.Equal(Convert.FromHexString("01DAF3DE01"), dayDelta.Body.ToArray());
        Assert.Equal(maxDate, apply(current, dayDelta.Body.ToArray()));
        byte[] maxTime = Convert.FromHexString("DAF3DE01FFFFA6D39219" + offset2);
        PreparedDeltaBody timeDelta = prepare(maxDate, maxTime);
        Assert.Equal(Convert.FromHexString("02FFFFA6D39219"), timeDelta.Body.ToArray());
        Assert.Equal(maxTime, apply(maxDate, timeDelta.Body.ToArray()));
        if (family) {
            var equal = host.GetMethod("Equal")!.CreateDelegate<Func<byte[], byte[], bool>>();
            Assert.False(equal(prior, current));
            Assert.True(equal(current, current));
        }
    }

    [Fact]
    public void TemporalScalarsComposeThroughRecordGenericNullableBaseAndEveryContainerShape() {
        GeneratorTestRun run = RunGenerator(TemporalScalarCompositionSource);
        AssertSchemaOnlyCompiles(run);
        string manifest = GeneratedSource(run, "DurableGraphSchemaHistoryCandidates.g.cs");
        Assert.Contains("manifest:9", manifest);
        Assert.Contains("// base:nQmFzZQ==(b24)|1", manifest);
        Assert.Contains("q(b24)", manifest);
        Assert.Contains("a4(b22)", manifest);
        Assert.Contains("nUGhhbnRvbQ==(b23)", manifest);
        var action = EmitAndLoad(run.OutputCompilation).GetType("Host")!.GetMethod("RoundTrip")!
            .CreateDelegate<Func<string, bool>>();
        string path = Path.Combine(Path.GetTempPath(), "temporal-scalar-generator-" + Guid.NewGuid().ToString("N"));
        try { Assert.True(action(path)); }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    [Theory]
    [InlineData("DateOnly", "System")]
    [InlineData("DateOnly", "Custom")]
    [InlineData("TimeOnly", "System")]
    [InlineData("TimeOnly", "Custom")]
    [InlineData("DateTimeOffset", "System")]
    [InlineData("DateTimeOffset", "Custom")]
    public void TemporalScalarRecognitionRejectsSourceDefinedLookalikes(string name, string ns) {
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
    [InlineData("int", "System.DateOnly")]
    [InlineData("long", "System.TimeOnly")]
    [InlineData("System.TimeSpan", "System.DateTimeOffset")]
    public void TemporalScalarRetypeRequiresExplicitOwnerVersion(string before, string after) {
        string Source(string type) => "using Atelia.DurableGraph; [DurableType(\"World\",1)] " +
            "public partial class World:IDurableObject { [DurableField(1)] public " + type + " Value; }";
        using AncestryHistoryDirectory history = new();
        GeneratorTestRun first = RunGenerator(Source(before));
        AssertSchemaOnlyCompiles(first);
        new SchemaHistoryTool().Publish(history.WriteManifest(first), history.History);
        GeneratorTestRun changed = RunGenerator(Source(after), history.ReadAdditionalTexts());
        Assert.Contains(changed.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0015");
    }

    [Theory]
    [InlineData("System.DateTime")]
    [InlineData("object")]
    [InlineData("System.Numerics.BigInteger")]
    [InlineData("System.Collections.Generic.Dictionary<System.DateOnly?,int>")]
    public void TemporalSupportDoesNotAdmitDeferredValueOrBoxedOrNullableRootKey(string type) {
        GeneratorTestRun run = RunGenerator($$"""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("World",1)] public partial class World:IDurableObject {
                [DurableField(1)] public {{type}} Value;
            }
            """);
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0007");
        Assert.DoesNotContain(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "CS8785");
    }

    private const string TemporalScalarFamilyBodySource = """
        using System;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Serialization;
        using Body = Atelia.DurableGraph.Generated.Family_4974656D.BodyV1;
        [DurableType("Trigger",1)] public partial struct Trigger<T> { [DurableField(1)] public T Value; }
        [DurableType("Item",1)] public partial class Item:IDurableObject {
            [DurableField(1)] public DateOnly Date;
            [DurableField(2)] public TimeOnly Time;
            [DurableField(3)] public DateTimeOffset Timestamp;
        }
        public static class Host {
            private static readonly DurableSchema Schema = new("Item",1,
                new DurableFieldInfo(1,TypeTag.DateOnly),new DurableFieldInfo(2,TypeTag.TimeOnly),new DurableFieldInfo(3,TypeTag.DateTimeOffset));
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

    private const string TemporalScalarCompositionSource = """
        using System;
        using System.Collections.Generic;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Persistence;
        using Atelia.DurableGraph.Generated;
        [DurableType("Part",1)] public readonly partial record struct Part(
            [field:DurableField(1)] DateOnly Date,[field:DurableField(2)] DateTimeOffset Timestamp,[field:DurableField(3)] TimeOnly Time);
        [DurableType("Pair",1)] public partial struct Pair<T,U> { [DurableField(1)] public T First; [DurableField(2)] public U Second; }
        [DurableType("Base",1)] public partial class Base<T>:IDurableObject { [DurableField(1)] public T Value; }
        [DurableType("Phantom",1)] public partial class Phantom<T>:IDurableObject { [DurableField(1)] public int Number; }
        [DurableType("World",1)] public partial class World:Base<DateTimeOffset> {
            [DurableField(1)] public readonly DateOnly Date;
            [DurableField(2)] public DateTimeOffset? Optional;
            [DurableField(3)] public Pair<DateOnly,TimeOnly> Pair;
            [DurableField(4)] public Part Part;
            [DurableField(5)] public DateOnly[] Vector=[];
            [DurableField(6)] public DateTimeOffset[,] Plane=new DateTimeOffset[1,1];
            [DurableField(7)] public TimeOnly[,,] Cube=new TimeOnly[1,1,1];
            [DurableField(8)] public DateOnly[,,,] Rank4=new DateOnly[1,1,1,1];
            [DurableField(9)] public List<DateTimeOffset?> List=new();
            [DurableField(10)] public Dictionary<DateOnly,List<DateTimeOffset?>> Map=new();
            [DurableField(11)] public Dictionary<TimeOnly,Part> Times=new();
            [DurableField(12)] public List<Dictionary<DateTimeOffset,DateOnly[]>[]> Nested=new();
            [DurableField(13)] public Phantom<TimeOnly> Phantom=new();
            [DurableField(14)] public Pair<DateTimeOffset?,Part?> Wrapped;
            public World(DateOnly date) { Date=date; }
        }
        public static class Host {
            public static bool RoundTrip(string path) {
                var models=new StateModelRegistry();DurableDefinitions.Register(models);
                var date=new DateOnly(2024,2,29);var time=TimeOnly.MaxValue;
                var stamp=new DateTimeOffset(2026,9,10,8,0,0,TimeSpan.FromHours(8));
                var changed=stamp.ToOffset(TimeSpan.Zero);
                var world=new World(date){Value=stamp,Optional=stamp,Pair=new(){First=date,Second=time},Part=new(date,stamp,time)};
                world.Vector=[date];world.Plane[0,0]=stamp;world.Cube[0,0,0]=time;world.Rank4[0,0,0,0]=date;
                world.List.AddRange(new DateTimeOffset?[]{null,stamp});world.Map.Add(date,world.List);
                world.Times.Add(time,world.Part);world.Nested.Add(new[]{new Dictionary<DateTimeOffset,DateOnly[]>{{stamp,world.Vector}}});
                world.Wrapped=new(){First=stamp,Second=world.Part};
                using(var repo=FixtureGraphRepository.CreateNew(path, models))using(var session=repo.Create(world)) {
                    session.Commit(new(1000000,1));
                    world.Value=changed;world.Optional=changed;world.Part=world.Part with {Timestamp=changed};
                    world.Plane[0,0]=changed;world.List[1]=changed;world.Wrapped.First=changed;
                    session.Commit(new(1000000,1));
                }
                using(var repo=FixtureGraphRepository.OpenExisting(path, models))using(var session=repo.Load<World>()) {
                    var w=session.World;
                    if(w.Date!=date || !w.Value.EqualsExact(changed) || !w.Optional!.Value.EqualsExact(changed) ||
                        !w.Part.Timestamp.EqualsExact(changed) || !w.Plane[0,0].EqualsExact(changed) ||
                        !w.List[1]!.Value.EqualsExact(changed) || !w.Wrapped.First!.Value.EqualsExact(changed)) return false;
                    if(w.Pair.First!=date || w.Pair.Second!=time || w.Cube[0,0,0]!=time || w.Rank4[0,0,0,0]!=date ||
                        w.Times[time].Time!=time || w.Wrapped.Second!.Value.Date!=date ||
                        !ReferenceEquals(w.Map[date],w.List) || !ReferenceEquals(w.Nested[0][0][stamp],w.Vector)) return false;
                    session.Commit(new(1000000,1));return true;
                }
            }
        }
        """;
}
