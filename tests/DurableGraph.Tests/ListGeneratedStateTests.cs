using Atelia.DurableGraph.Schema;
using System.Reflection;
using Atelia.DurableGraph.Build;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void ListCompositionsUseReferenceStateSlotsAndRecursiveNominalPatterns() {
        GeneratorTestRun run = RunGenerator("""
            using System.Collections.Generic;
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("Point",1)] public partial struct Point { [DurableField(1)] public int X; }
            [DurableType("Pair",1)] public partial struct Pair<T,U> {
                [DurableField(1)] public T First;
                [DurableField(2)] public U Second;
                [DurableField(3)] public List<T> Nested;
            }
            [DurableType("Recursive",1)] public partial struct Recursive { [DurableField(1)] public List<Recursive> Children; }
            [DurableType("Box",1)] public partial class Box<T>:IDurableObject { [DurableField(1)] public T Value; }
            [DurableType("Derived",1)] public partial class Derived:Box<List<Point>> {}
            [DurableType("World",1)] public partial class World:IDurableObject {
                [DurableField(1)] public List<List<int>> Nested;
                [DurableField(2)] public List<Point[,]> Arrays;
                [DurableField(3)] public List<World>[] Lists;
                [DurableField(4)] public Pair<List<Point>,int> Pair;
                [DurableField(5)] public Recursive Recursive;
            }
            """);
        AssertSchemaOnlyCompiles(run);
        string generated = GeneratedSource(run, "DurableGenericStates.g.cs");
        Assert.Contains("global::Atelia.DurableGraph.Schema.TypeExpr.List(", generated);
        Assert.Contains("context.CaptureObject(field", generated);
        Assert.Contains("objects.ResolveObject<", generated);
        Assert.Contains("global::Atelia.DurableGraph.ObjectId", generated);
        string manifest = GeneratedSource(run, "DurableGraphSchemaHistoryCandidates.g.cs");
        Assert.Contains("manifest:9", manifest);
        Assert.Contains("// field:1|15|l(l(b2))", manifest);
        Assert.Contains("// field:2|15|l(a2(nUG9pbnQ=()))", manifest);
        Assert.Contains("// field:3|15|a1(l(nV29ybGQ=()))", manifest);
        Assert.Contains("// field:3|15|l(p0)", manifest);
        Assert.Contains("// base:nQm94(l(nUG9pbnQ=()))|1", manifest);
    }

    [Theory]
    [InlineData("List<object>")]
    [InlineData("List<int[,,,,]>")]
    [InlineData("IList<int>")]
    [InlineData("ChildList")]
    public void UnsupportedListElementsInterfacesAndSubclassesAreRejected(string fieldType) {
        GeneratorTestRun run = RunGenerator("using System.Collections.Generic; using Atelia.DurableGraph; " +
            "public class ChildList:List<int> {} [DurableType(\"Bad\",1)] public partial class Bad:IDurableObject { " +
            "[DurableField(1)] public " + fieldType + " Value; }");
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0007");
    }

    [Fact]
    public void SourceDefinedFrameworkNamedListIsNotRecognizedAsTheBclConstructor() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            namespace System.Collections.Generic { public class List<T> {} }
            [DurableType("Bad",1)] public partial class Bad:IDurableObject {
                [DurableField(1)] public System.Collections.Generic.List<int> Value;
            }
            """);
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0007");
    }

    [Fact]
    public void DurableUserListHasNamedIdentityWhileBclListKeepsBuiltinIdentity() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("UserList",1)] public partial class List<T>:IDurableObject { [DurableField(1)] public T Value; }
            [DurableType("World",1)] public partial class World:IDurableObject {
                [DurableField(1)] public List<int> User;
                [DurableField(2)] public System.Collections.Generic.List<int> Builtin;
            }
            """);
        AssertSchemaOnlyCompiles(run);
        string manifest = GeneratedSource(run, "DurableGraphSchemaHistoryCandidates.g.cs");
        Assert.Contains("// field:1|15|nVXNlckxpc3Q=(b2)", manifest);
        Assert.Contains("// field:2|15|l(b2)", manifest);
    }

    [Fact]
    public void ListElementVersionChangeDoesNotChangeReferencingOwnerSchema() {
        using AncestryHistoryDirectory history = new();
        const string source = """
            using System.Collections.Generic;
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("Point",1)] public partial struct Point { [DurableField(1)] public int X; }
            [DurableType("World",1)] public partial class World:IDurableObject { [DurableField(1)] public List<Point> Points; }
            """;
        GeneratorTestRun first = RunGenerator(source);
        AssertSchemaOnlyCompiles(first);
        new SchemaHistoryTool().Publish(history.WriteManifest(first), history.History);
        GeneratorTestRun second = RunGenerator(source.Replace("\"Point\",1", "\"Point\",2").Replace("public int X;", "public long X;"), history.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(second);
        string manifest = GeneratedSource(second, "DurableGraphSchemaHistoryCandidates.g.cs");
        Assert.Contains("// field:1|15|l(nUG9pbnQ=())", manifest);
        Assert.DoesNotContain(second.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0016");
    }

    [Fact]
    public void ClosedOwnerUpgradeRegistrationPreservesListTypeArguments() {
        using AncestryHistoryDirectory history = new();
        GeneratorTestRun first = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("Box",1)] public partial class Box<T>:IDurableObject { [DurableField(1)] public T Value; }
            """);
        AssertSchemaOnlyCompiles(first);
        new SchemaHistoryTool().Publish(history.WriteManifest(first), history.History);
        GeneratorTestRun second = RunGenerator("""
            using System.Collections.Generic;
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using States=Atelia.DurableGraph.Generated.Family_426F78;
            [DurableType("Box",2)] public partial class Box<T>:IDurableObject {
                [DurableField(1)] public T Value;
                [DurableField(2)] public int Count;
            }
            public static class Upgrades {
                [DurableUpgrade(typeof(Box<List<int>>),1)]
                public static void Upgrade(in States.V1<ObjectId> prior,out States.V2<ObjectId> next,UpgradeContext context) =>
                    next=new(prior.Segment0Field1,0);
            }
            """, history.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(second);
        string generated = GeneratedSource(second, "DurableGenericStates.g.cs");
        Assert.Contains("closedOwner: global::Atelia.DurableGraph.Schema.TypeExpr.Named(\"Box\", global::Atelia.DurableGraph.Schema.TypeExpr.List(", generated);
    }

    [Fact]
    public void GeneratedListsFreezeRestoreAndRetainRecursiveValueReferenceIdentity() {
        GeneratorTestRun run = RunGenerator("""
            using System;
            using System.Collections.Generic;
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using Atelia.DurableGraph.Persistence;
            using Atelia.DurableGraph.Generated;
            [DurableType("Recursive",1)] public partial struct Recursive {
                [DurableField(1)] public int Value;
                [DurableField(2)] public List<Recursive> Children;
            }
            [DurableType("Box",1)] public partial class Box<T>:IDurableObject { [DurableField(1)] public T Value; }
            [DurableType("World",1)] public partial class World:IDurableObject {
                [DurableField(1)] public List<Recursive> Items;
                [DurableField(2)] public Box<List<Recursive>> Alias;
                [DurableField(3)] public List<int[]> Arrays;
                [DurableField(4)] public List<List<string>> Strings;
                public static World Create() {
                    var items=new List<Recursive>(); items.Add(new Recursive { Value=7,Children=items });
                    var shared=new List<string>{"x","y"};
                    return new World { Items=items,Alias=new Box<List<Recursive>>{Value=items},
                        Arrays=new List<int[]>{new int[]{1,2}}, Strings=new List<List<string>>{shared,shared} };
                }
                public bool Check() => Items.Count==2 && Items[0].Value==7 && Items[1].Value==9 &&
                    ReferenceEquals(Items,Items[0].Children) && ReferenceEquals(Items,Alias.Value) &&
                    Arrays[0][1]==2 && ReferenceEquals(Strings[0],Strings[1]) && Strings[0][1]=="y";
            }
            public static class Host {
                public static bool Probe(string path) {
                    var models=new StateModelRegistry(); DurableDefinitions.Register(models);
                    var world=World.Create();
                    using(var repository=FixtureGraphRepository.CreateNew(path, models)) {
                        using var session=repository.Create(world);
                        session.Commit(new(100,100));
                        world.Items.Add(new Recursive {Value=9,Children=world.Items});
                        session.Commit(new(100,100));
                        if(!ReferenceEquals(session.World,world)) return false;
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
