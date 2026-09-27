using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Reflection;
using Atelia.DurableGraph.Build;
using Atelia.DurableGraph.Persistence;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Theory]
    [InlineData("bool")]
    [InlineData("byte")]
    [InlineData("sbyte")]
    [InlineData("short")]
    [InlineData("ushort")]
    [InlineData("int")]
    [InlineData("uint")]
    [InlineData("long")]
    [InlineData("ulong")]
    [InlineData("char")]
    [InlineData("System.Half")]
    [InlineData("float")]
    [InlineData("double")]
    public void NullableScalarSlotsUseUnmanagedFamilyOperands(string scalar) {
        GeneratorTestRun run = RunGenerator($$"""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("World",1)] public partial class World:IDurableObject {
                [DurableField(1)] private readonly {{scalar}}? _value;
            }
            """);
        AssertSchemaOnlyCompiles(run);
        string generated = GeneratedSource(run, "DurableGenericStates.g.cs");
        Assert.Contains("public readonly struct V1<TState0>", generated);
        Assert.Contains("TOps0.WriteBase", generated);
        Assert.Contains("where TState0: unmanaged", generated);
        Assert.Contains("TypeExpr.Nullable(", generated);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        StateModelRegistry models = new();
        assembly.GetType("Atelia.DurableGraph.Generated.DurableDefinitions")!.GetMethod("Register")!
            .Invoke(null, [models]);
        DurableSchema schema = models.Snapshot().ResolveCurrentModel(assembly.GetType("World")!).CurrentSchema;
        Assert.Equal(TypeTag.Nullable, schema.Fields[0].TypeTag);
    }

    [Fact]
    public void NullableCompositionsRetainParameterExpressionsAndFixedChildVersions() {
        GeneratorTestRun run = RunGenerator("""
            using System.Collections.Generic;
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("Point",1)] public partial struct Point { [DurableField(1)] public int X; }
            [DurableType("Cell",1)] public partial struct Cell<T> where T:struct {
                [DurableField(1)] public T? Optional;
            }
            [DurableType("Box",1)] public partial class Box<T>:IDurableObject { [DurableField(1)] public T Value; }
            [DurableType("World",1)] public partial class World:IDurableObject {
                [DurableField(1)] public Point? Point;
                [DurableField(2)] public Cell<Point>? Cell;
                [DurableField(3)] public Box<Point?> Box;
                [DurableField(4)] public List<Point?>[] Lists;
                [DurableField(5)] public Point?[, ,] Cubes;
                [DurableField(6)] public Cell<int>?[,,,] Rank4;
            }
            """);
        AssertSchemaOnlyCompiles(run);
        string manifest = GeneratedSource(run, "DurableGraphSchemaHistoryCandidates.g.cs");
        Assert.Contains("manifest:9", manifest);
        Assert.Contains("// field:1|18|q(p0)", manifest);
        Assert.Contains("// field:1|18|q(nUG9pbnQ=())|1", manifest);
        Assert.Contains("// field:2|18|q(nQ2VsbA==(nUG9pbnQ=()))|1", manifest);
        Assert.Contains("// field:3|15|nQm94(q(nUG9pbnQ=()))", manifest);
        Assert.Contains("// field:4|15|a1(l(q(nUG9pbnQ=())))", manifest);
        string generated = GeneratedSource(run, "DurableGenericStates.g.cs");
        Assert.Contains("TypeExpr.Nullable(global::Atelia.DurableGraph.Schema.TypeExpr.Parameter(0))", generated);
        Assert.Contains("where T : struct", generated);
    }

    [Theory]
    [InlineData("Choice?")]
    [InlineData("Plain?")]
    [InlineData("System.Int128?")]
    public void NullableDoesNotAdmitUnsupportedValueOperands(string valueType) {
        GeneratorTestRun run = RunGenerator($$"""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            public enum Choice { A, B }
            public struct Plain { public int X; }
            [DurableType("Bad",1)] public partial class Bad:IDurableObject {
                [DurableField(1)] public {{valueType}} Value;
            }
            """);
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0007");
        Assert.DoesNotContain(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "CS8785");
    }

    [Fact]
    public void NullableChildUpgradeRequiresOwnerVersionButReferenceChildVersionDoesNot() {
        using AncestryHistoryDirectory history = new();
        const string source = """
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("Node",1)] public partial class Node:IDurableObject { [DurableField(1)] public int X; }
            [DurableType("Point",1)] public partial struct Point {
                [DurableField(1)] public int X;
                [DurableField(2)] public Node Node;
            }
            [DurableType("World",1)] public partial class World:IDurableObject { [DurableField(1)] public Point? Point; }
            """;
        GeneratorTestRun first = RunGenerator(source);
        AssertSchemaOnlyCompiles(first);
        new SchemaHistoryTool().Publish(history.WriteManifest(first), history.History);
        GeneratorTestRun changed = RunGenerator(source.Replace("\"Point\",1", "\"Point\",2"), history.ReadAdditionalTexts());
        Assert.Contains(changed.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0015");
        GeneratorTestRun referenceChanged = RunGenerator(source.Replace("\"Node\",1", "\"Node\",2"), history.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(referenceChanged);
    }

    [Fact]
    public void AddingNullableSelectsFamilyNamesAndKeepsOldOrdinaryReaderForExplicitUpgrade() {
        using AncestryHistoryDirectory history = new();
        GeneratorTestRun first = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("World",1)] public partial class World:IDurableObject { [DurableField(1)] public int Value; }
            """);
        AssertSchemaOnlyCompiles(first);
        Assert.Contains(first.GeneratedSources, source => source.HintName == "DurableStates.g.cs");
        new SchemaHistoryTool().Publish(history.WriteManifest(first), history.History);
        GeneratorTestRun next = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using States=Atelia.DurableGraph.Generated.Family_576F726C64;
            [DurableType("World",2)] public partial class World:IDurableObject { [DurableField(1)] public int? Value; }
            public static class Changes {
                [DurableUpgrade(typeof(World),1)]
                public static void Upgrade(in States.V1 prior,out States.V2<NullableState<int>> next,UpgradeContext context) =>
                    next = new(new NullableState<int>(prior.Segment0Field1));
            }
            public static class Host { public static object Prior() => new States.V1(17); }
            """, history.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(next);
        Assembly assembly = EmitAndLoad(next.OutputCompilation);
        StateModelRegistry models = new();
        assembly.GetType("Atelia.DurableGraph.Generated.DurableDefinitions")!.GetMethod("Register")!.Invoke(null, [models]);
        StateModelBinding model = models.Snapshot().ResolveCurrentModel(assembly.GetType("World")!);
        object prior = assembly.GetType("Host")!.GetMethod("Prior")!.Invoke(null, null)!;
        ObjectStateRecord upgraded = model.Normalize(new(new(7), new DurableSchema("World", 1, new DurableFieldInfo(1, TypeTag.Int32)), prior));
        NullableState<int> value = Assert.IsType<NullableState<int>>(StateModelField(upgraded, "Segment0Field1"));
        Assert.True(value.HasValue);
        Assert.Equal(17, value.Value);
    }

    [Fact]
    public void DeletedNullableChildDomainStillGeneratesHistoricalExactReader() {
        using AncestryHistoryDirectory history = new();
        GeneratorTestRun first = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("Point",1)] public partial struct Point { [DurableField(1)] public int X; }
            [DurableType("World",1)] public partial class World:IDurableObject { [DurableField(1)] public Point? Value; }
            """);
        AssertSchemaOnlyCompiles(first);
        new SchemaHistoryTool().Publish(history.WriteManifest(first), history.History);
        GeneratorTestRun next = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("World",2)] public partial class World:IDurableObject { [DurableField(1)] public int? Value; }
            """, history.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(next);
        Assembly assembly = EmitAndLoad(next.OutputCompilation);
        Assert.Null(assembly.GetType("Point"));
        StateModelRegistry models = new();
        assembly.GetType("Atelia.DurableGraph.Generated.DurableDefinitions")!.GetMethod("Register")!.Invoke(null, [models]);
        DurableSchema point = new("Point", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int32));
        DurableSchema old = new("World", 1, DurableFieldInfo.Nullable(1, new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: point)));
        StateReaderBinding reader = models.Snapshot().ResolveReader(old);
        Assert.Equal(old, reader.Schema);
        ObjectStateRecord decoded = reader.Read(new(1), new StateModelBodySource([1, 14]));
        object state = StateModelField(decoded, "Segment0Field1")!;
        Assert.True((bool)state.GetType().GetProperty("HasValue")!.GetValue(state)!);
        object child = state.GetType().GetProperty("Value")!.GetValue(state)!;
        Assert.Equal(7, child.GetType().GetField("Segment0Field1")!.GetValue(child));
    }

    [Theory]
    [InlineData(3, "1|15|nQm94(q(b2))")]
    [InlineData(4, "1|15|a1(q(b2))")]
    [InlineData(5, "1|15|l(q(b2))")]
    [InlineData(5, "1|18|q(b2)")]
    [InlineData(6, "1|18|q(b4)")]
    [InlineData(6, "1|18|q(l(b2))")]
    [InlineData(6, "1|18|q(q(b2))")]
    [InlineData(6, "1|18|q(nUG9pbnQ=())")]
    [InlineData(6, "1|18|q(p0)|1")]
    [InlineData(6, "1|18|q(b2)|1")]
    public void NullableHistoryUsesVersionSixAndStrictChildGrammar(int version, string field) {
        string history = GenericTemplateHistoryTests.History("Optional", arity: 1, body: "// field:" + field + "\n")
            .Replace("history:3", "history:" + version);
        GeneratorTestRun run = RunGenerator("class Plain {}", new InMemoryAdditionalText("optional.dgschema", history));
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0012");
        Assert.DoesNotContain(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "CS8785");
    }

    [Fact]
    public void NullableInsideGenericArgumentCannotReclassifyReferenceDefinitionAsValue() {
        string owner = GenericTemplateHistoryTests.History("Owner", body: "// field:1|15|nQm94(q(nTm9kZQ==()))\n")
            .Replace("history:3", "history:6");
        string node = GenericTemplateHistoryTests.History("Node", body: "// field:1|2\n");
        GeneratorTestRun run = RunGenerator("class Plain {}",
            new InMemoryAdditionalText("owner.dgschema", owner), new InMemoryAdditionalText("node.dgschema", node));
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0019");
    }

    [Fact]
    public void NullableGeneratedProjectionCommitsGenericsReadonlyCollectionsAndCycleReferences() {
        GeneratorTestRun run = RunGenerator("""
            using System;
            using System.Collections.Generic;
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using Atelia.DurableGraph.Persistence;
            using Atelia.DurableGraph.Generated;
            [DurableType("Point",1)] public readonly partial struct Point {
                [DurableField(1)] public readonly int X;
                [DurableField(2)] public readonly World Back;
                public Point(int x,World back) { X=x; Back=back; }
            }
            [DurableType("Cell",1)] public partial struct Cell<T> where T:struct { [DurableField(1)] public T? Value; }
            [DurableType("Box",1)] public partial class Box<T>:IDurableObject { [DurableField(1)] public T Value; }
            [DurableType("World",1)] public partial class World:IDurableObject {
                [DurableField(1)] public Point? Point;
                [DurableField(2)] public Cell<Point>? Cell;
                [DurableField(3)] public Box<Point?> Box;
                [DurableField(4)] public List<Point?> Items;
                [DurableField(5)] public List<Point?> Alias;
                [DurableField(6)] public Point?[] Vector;
                [DurableField(7)] public Point?[,] Rank2;
                [DurableField(8)] public Point?[,,] Rank3;
                [DurableField(9)] public Point?[,,,] Rank4;
                [DurableField(10)] private readonly int? _readonly;
                public World(int value) {
                    _readonly=value; var point=new Point(value,this); Point=point;
                    Cell=new Cell<Point>{Value=point}; Box=new Box<Point?>{Value=point};
                    Items=new List<Point?>{null,point}; Alias=Items; Vector=new Point?[]{point,null};
                    Rank2=new Point?[1,1]; Rank2[0,0]=point;
                    Rank3=new Point?[1,1,1]; Rank3[0,0,0]=point;
                    Rank4=new Point?[1,1,1,1]; Rank4[0,0,0,0]=point;
                }
                public void Change() { Point=null; Items[0]=new Point(9,this); Items[1]=null; Vector[1]=new Point(10,this); }
                public bool Check() => Point==null && _readonly==7 && ReferenceEquals(Cell.Value.Value.Value.Back,this) &&
                    ReferenceEquals(Box.Value.Value.Back,this) && Items.Count==2 && Items[0].Value.X==9 && !Items[1].HasValue &&
                    ReferenceEquals(Items,Alias) && ReferenceEquals(Items[0].Value.Back,this) &&
                    Vector[1].Value.X==10 && ReferenceEquals(Rank2[0,0].Value.Back,this) &&
                    ReferenceEquals(Rank3[0,0,0].Value.Back,this) && ReferenceEquals(Rank4[0,0,0,0].Value.Back,this);
            }
            public static class Host {
                public static bool Probe(string path) {
                    var models=new StateModelRegistry(); DurableDefinitions.Register(models);
                    using(var repository=FixtureGraphRepository.CreateNew(path, models)) {
                        var world=new World(7); using var session=repository.Create(world);
                        session.Commit(new(100,100)); world.Change(); session.Commit(new(100,100));
                        if(!ReferenceEquals(world,session.World)) return false;
                    }
                    using(var repository=FixtureGraphRepository.OpenExisting(path, models)) {
                        using var session=repository.Load<World>();
                        if(!session.World.Check()) return false;
                        session.Commit(new(100,100));
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
}
