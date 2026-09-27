using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Reflection;
using Atelia.DurableGraph.Build;
using Atelia.DurableGraph.Persistence;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Theory]
    [InlineData("public")]
    [InlineData("internal")]
    public void EnumUsesExternalProjectionAndPrivateReadonlyOwnerField(string accessibility) {
        GeneratorTestRun run = RunGenerator($$"""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using Atelia.DurableGraph.Persistence;
            using Atelia.DurableGraph.Generated;
            [DurableType("Mode",1)] {{accessibility}} enum Mode:byte { Idle=0, Moving=1 }
            [DurableType("World",1)] public partial class World:IDurableObject {
                [DurableField(1)] private readonly Mode _mode;
                public World() { _mode=(Mode)253; }
                public bool Check() => (byte)_mode==253;
            }
            public static class Host {
                public static bool Probe(string path) {
                    var models=new StateModelRegistry(); DurableDefinitions.Register(models);
                    using(var repo=FixtureGraphRepository.CreateNew(path, models)) {
                        using var session=repo.Create(new World()); session.Commit(new(100,100));
                    }
                    using(var repo=FixtureGraphRepository.OpenExisting(path, models)) {
                        using var session=repo.Load<World>(); return session.World.Check();
                    }
                }
            }
            """);
        AssertSchemaOnlyCompiles(run);
        string generated = GeneratedSource(run, "DurableGenericStates.g.cs");
        Assert.DoesNotContain("partial enum", generated);
        Assert.Contains(accessibility + " static class EnumProjection_", generated);
        Assert.Contains("new((byte)value)", generated);
        Assert.DoesNotContain("Enum.IsDefined", generated);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        using RawBaseDirectory directory = new();
        Assert.True(assembly.GetType("Host")!.GetMethod("Probe")!.CreateDelegate<Func<string, bool>>()(directory.Path));
    }

    [Fact]
    public void DeletedEnumHistorySelectsFamilyWithoutChangingUnrelatedOrdinaryGeneration() {
        GeneratorTestRun ordinary = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("World",1)] public partial class World:IDurableObject { [DurableField(1)] public byte Value; }
            """);
        AssertSchemaOnlyCompiles(ordinary);
        Assert.Contains(ordinary.GeneratedSources, source => source.HintName == "DurableStates.g.cs");
        using AncestryHistoryDirectory history = new();
        GeneratorTestRun first = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("Mode",1)] public enum Mode:byte { Idle }
            [DurableType("World",1)] public partial class World:IDurableObject { [DurableField(1)] public Mode Value; }
            """);
        AssertSchemaOnlyCompiles(first);
        new SchemaHistoryTool().Publish(history.WriteManifest(first), history.History);
        GeneratorTestRun next = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("World",2)] public partial class World:IDurableObject { [DurableField(1)] public byte Value; }
            """, history.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(next);
        Assembly assembly = EmitAndLoad(next.OutputCompilation);
        Assert.Null(assembly.GetType("Mode"));
        StateModelRegistry models = new();
        assembly.GetType("Atelia.DurableGraph.Generated.DurableDefinitions")!.GetMethod("Register")!.Invoke(null, [models]);
        DurableSchema mode = new("Mode",1,SchemaKind.InlineValue,new DurableFieldInfo(1,TypeTag.Byte));
        DurableSchema old = new("World",1,new DurableFieldInfo(1,TypeTag.InlineValue,inlineSchema:mode));
        ObjectStateRecord decoded = models.Snapshot().ResolveReader(old).Read(new(1),new StateModelBodySource([253]));
        object value = StateModelField(decoded,"Segment0Field1")!;
        Assert.Equal((byte)253,value.GetType().GetField("Segment0Field1")!.GetValue(value));
        GeneratorTestRun pure = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [ValueUpgradeRuleSet(AllowKeepExact=true)] public static class Rules { }
            """, history.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(pure);
        Assert.Contains("Family_4D6F6465",GeneratedSource(pure,"DurableGenericStates.g.cs"));
    }

    [Fact]
    public void EnumKeepsDistinctNominalIdentityAndPermitsOrdinaryConstantNames() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("Mode",1)] public enum Mode:byte { Schema=0, GetSchema=1, __DurableSchemaHistory=2, __DurableProjection=3 }
            [DurableType("Other",1)] public enum Other:byte { Zero=0 }
            [DurableType("Box",1)] public partial class Box<T>:IDurableObject { [DurableField(1)] public T Value; }
            [DurableType("Phantom",1)] public partial struct Phantom<T> { }
            """);
        AssertSchemaOnlyCompiles(run);
        Assembly assembly=EmitAndLoad(run.OutputCompilation);
        StateModelRegistry models=new();
        assembly.GetType("Atelia.DurableGraph.Generated.DurableDefinitions")!.GetMethod("Register")!.Invoke(null,[models]);
        StateModelSnapshot snapshot=models.Snapshot();
        Type mode=assembly.GetType("Mode")!, other=assembly.GetType("Other")!, box=assembly.GetType("Box`1")!;
        StateValueBinding modeValue=snapshot.ResolveCurrentValue(mode), otherValue=snapshot.ResolveCurrentValue(other);
        Assert.NotEqual(modeValue.StateType,otherValue.StateType);
        Assert.NotEqual(modeValue.Slot.InlineSchema!.Type,otherValue.Slot.InlineSchema!.Type);
        Assert.NotEqual(snapshot.ResolveCurrentModel(box.MakeGenericType(mode)).CurrentSchema.Type,
            snapshot.ResolveCurrentModel(box.MakeGenericType(typeof(byte))).CurrentSchema.Type);
        StateValueBinding phantom=snapshot.ResolveCurrentValue(assembly.GetType("Phantom`1")!.MakeGenericType(mode));
        Assert.Empty(phantom.Slot.InlineSchema!.Fields);
        Assert.Equal(TypeExpr.Named("Phantom",TypeExpr.Named("Mode")),phantom.Slot.InlineSchema.Type);
    }

    [Theory]
    [InlineData("sbyte", "sbyte.MinValue", "sbyte.MaxValue")]
    [InlineData("byte", "byte.MinValue", "byte.MaxValue")]
    [InlineData("short", "short.MinValue", "short.MaxValue")]
    [InlineData("ushort", "ushort.MinValue", "ushort.MaxValue")]
    [InlineData("int", "int.MinValue", "int.MaxValue")]
    [InlineData("uint", "uint.MinValue", "uint.MaxValue")]
    [InlineData("long", "long.MinValue", "long.MaxValue")]
    [InlineData("ulong", "ulong.MinValue", "ulong.MaxValue")]
    public void EnumAllUnderlyingIntegersComposeAndPreserveBits(string scalar, string minimum, string maximum) {
        GeneratorTestRun run = RunGenerator($$"""
            using System;
            using System.Collections.Generic;
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using Atelia.DurableGraph.Persistence;
            using Atelia.DurableGraph.Generated;
            [Flags,DurableType("Mode",1)] public enum Mode:{{scalar}} { Low={{minimum}}, Alias={{minimum}}, High={{maximum}} }
            [DurableType("Other",1)] public enum Other:{{scalar}} { Zero=0 }
            [DurableType("Box",1)] public partial class Box<T>:IDurableObject where T:Enum { [DurableField(1)] public T Value; }
            [DurableType("Cell",1)] public partial struct Cell<T> where T:struct,Enum { [DurableField(1)] public T? Value; }
            [DurableType("Raw",1)] public partial struct Raw<T> where T:unmanaged,Enum { [DurableField(1)] public T Value; }
            [DurableType("World",1)] public partial class World:IDurableObject {
                [DurableField(1)] public Mode Value;
                [DurableField(2)] public Mode? Optional;
                [DurableField(3)] public Box<Mode> Box;
                [DurableField(4)] public Cell<Mode> Cell;
                [DurableField(5)] public Raw<Mode> Raw;
                [DurableField(6)] public List<Mode?> Items;
                [DurableField(7)] public List<Mode?> Alias;
                [DurableField(8)] public Mode?[] Vector;
                [DurableField(9)] public Mode?[,] Rank2;
                [DurableField(10)] public Mode?[,,] Rank3;
                [DurableField(11)] public Mode?[,,,] Rank4;
                [DurableField(12)] public List<Mode?>[][] Jagged;
                [DurableField(13)] public Other Other;
                public World() {
                    Value=Mode.Low; Optional=null; Box=new Box<Mode>{Value=Mode.High};
                    Cell=new Cell<Mode>{Value=Mode.High}; Raw=new Raw<Mode>{Value=Mode.Low};
                    Items=new List<Mode?>{null,Mode.High}; Alias=Items; Vector=new Mode?[]{Mode.High,null};
                    Rank2=new Mode?[1,1]; Rank2[0,0]=Mode.High; Rank3=new Mode?[1,1,1]; Rank3[0,0,0]=Mode.High;
                    Rank4=new Mode?[1,1,1,1]; Rank4[0,0,0,0]=Mode.High; Jagged=new[] { new[] { Items } };
                }
                public void Change() { Value=(Mode)3; Optional=Mode.High; Items[0]=(Mode)3; Vector[1]=Mode.Low; }
                public bool Check() => ({{scalar}})Value==3 && Optional==Mode.High && Box.Value==Mode.High &&
                    Cell.Value==Mode.High && Raw.Value==Mode.Low && ReferenceEquals(Items,Alias) && Items[0]==(Mode)3 &&
                    Vector[1]==Mode.Low && Rank2[0,0]==Mode.High && Rank3[0,0,0]==Mode.High && Rank4[0,0,0,0]==Mode.High &&
                    ReferenceEquals(Jagged[0][0],Items);
            }
            public static class Host {
                public static bool Probe(string path) {
                    var models=new StateModelRegistry(); DurableDefinitions.Register(models);
                    using(var repo=FixtureGraphRepository.CreateNew(path, models)) {
                        var world=new World(); using var session=repo.Create(world); session.Commit(new(100,100));
                        world.Change(); session.Commit(new(100,100)); if(!ReferenceEquals(world,session.World)) return false;
                    }
                    using(var repo=FixtureGraphRepository.OpenExisting(path, models)) {
                        using var session=repo.Load<World>(); if(!session.World.Check()) return false; session.Commit(new(100,100));
                    }
                    return true;
                }
            }
            """);
        AssertSchemaOnlyCompiles(run);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        using RawBaseDirectory directory = new();
        Assert.True(assembly.GetType("Host")!.GetMethod("Probe")!.CreateDelegate<Func<string, bool>>()(directory.Path));
    }

    [Theory]
    [InlineData("[DurableType(\"Bad\",1)] file enum Bad { A }", "DG0001")]
    [InlineData("public class Outer { [DurableType(\"Bad\",1)] public enum Bad { A } }", "DG0001")]
    [InlineData("[DurableType(\"Bad\",1)] public enum Bad { [DurableField(1)] A }", "DG0009")]
    [InlineData("[DurableType(\"Bad\",1)] public enum Bad { [Transient] A }", "DG0009")]
    public void EnumRejectsUnsupportedDeclarationsAndConstantClassification(string source,string diagnostic) {
        GeneratorTestRun run=RunGenerator("using Atelia.DurableGraph; " + source);
        Assert.Contains(run.GeneratorDiagnostics,item=>item.Id==diagnostic);
        Assert.DoesNotContain(run.GeneratorDiagnostics,item=>item.Id=="CS8785");
    }

    [Fact]
    public void EnumConstantsDoNotChangeHistoryButUnderlyingIntegerAndVersionDo() {
        using AncestryHistoryDirectory history = new();
        const string source="""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("Mode",1)] public enum Mode:byte { A=0 }
            [DurableType("World",1)] public partial class World:IDurableObject { [DurableField(1)] public Mode Value; }
            """;
        GeneratorTestRun first=RunGenerator(source); AssertSchemaOnlyCompiles(first);
        new SchemaHistoryTool().Publish(history.WriteManifest(first),history.History);
        GeneratorTestRun constants=RunGenerator(source.Replace("A=0", "Renamed=3, Alias=3, Extra=255").Replace("public enum", "[System.Flags] public enum"),history.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(constants);
        Assert.Equal(GeneratedSource(first,"DurableGraphSchemaHistoryCandidates.g.cs"),GeneratedSource(constants,"DurableGraphSchemaHistoryCandidates.g.cs"));
        GeneratorTestRun changed=RunGenerator(source.Replace("Mode:byte","Mode:long"),history.ReadAdditionalTexts());
        Assert.Contains(changed.GeneratorDiagnostics,d=>d.Id=="DG0015");
        GeneratorTestRun version=RunGenerator(source.Replace("\"Mode\",1", "\"Mode\",2"),history.ReadAdditionalTexts());
        Assert.Contains(version.GeneratorDiagnostics,d=>d.Id=="DG0015");
    }
}
