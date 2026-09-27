using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Reflection;
using Atelia.DurableGraph.Build;
using Atelia.DurableGraph.Persistence;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void GenericBodiesUseUnmanagedStateTemplatesAndStrictNestedDelta() {
        GeneratorTestRun run = RunGenerator("""
            using System;
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using Atelia.DurableGraph.Serialization;
            using BoxStates = Atelia.DurableGraph.Generated.Family_426F78;
            using PairStates = Atelia.DurableGraph.Generated.Family_50616972;
            [DurableType("Box", 1)] public partial class Box<T> : IDurableObject { [DurableField(1)] public T Value = default!; }
            [DurableType("Pair", 1)] public readonly partial struct Pair<T> {
                [DurableField(1)] private readonly T _left;
                [DurableField(2)] private readonly T _right;
            }
            [DurableType("Phantom", 1)] public partial class Phantom<T> : IDurableObject { }
            public static class Host {
                public static bool Probe() {
                    var pairSchema = new DurableSchema(TypeExpr.Named("Pair", TypeExpr.Builtin(TypeTag.Int32)), 1,
                        SchemaKind.InlineValue, new DurableFieldInfo(1,TypeTag.Int32), new DurableFieldInfo(2,TypeTag.Int32));
                    var schema = new DurableSchema(TypeExpr.Named("Box", pairSchema.Type),1,
                        new DurableFieldInfo(1,TypeTag.InlineValue,inlineSchema:pairSchema));
                    var prior = new BoxStates.V1<PairStates.V1<int>>(new(1,2));
                    var current = new BoxStates.V1<PairStates.V1<int>>(new(1,9));
                    var prepared = BoxStates.BodyV1<PairStates.V1<int>,PairStates.BodyV1<int,Int32StateOps>>.PrepareDelta(in prior,in current,schema);
                    var reader = new BinaryPayloadReader(prepared.Body);
                    var restored = BoxStates.BodyV1<PairStates.V1<int>,PairStates.BodyV1<int,Int32StateOps>>.Apply(ref reader,in prior,schema);
                    reader.EnsureFullyConsumed();
                    bool rejected = false;
                    reader = new BinaryPayloadReader(new byte[]{1,0});
                    try { BoxStates.BodyV1<PairStates.V1<int>,PairStates.BodyV1<int,Int32StateOps>>.Apply(ref reader,in prior,schema); }
                    catch (System.IO.InvalidDataException) { rejected = true; }
                    return prepared.HasChanges && prepared.Body.SequenceEqual(new byte[]{1,2,18}) &&
                        restored.Segment0Field1.Segment0Field2 == 9 && rejected &&
                        !System.Runtime.CompilerServices.RuntimeHelpers.IsReferenceOrContainsReferences<BoxStates.V1<PairStates.V1<int>>>();
                }
            }
            """);
        AssertSchemaOnlyCompiles(run);
        string generated = GeneratedSource(run, "DurableGenericStates.g.cs");
        Assert.Contains("TOps0.PrepareDelta", generated);
        Assert.Contains("public readonly struct V1<TState0>", generated);
        Assert.DoesNotContain("DynamicInvoke", generated);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        Assert.True(assembly.GetType("Host")!.GetMethod("Probe")!.CreateDelegate<Func<bool>>()());
        Assert.False(assembly.GetType("Atelia.DurableGraph.Generated.Family_5068616E746F6D+V1")!.IsGenericType);
    }

    [Fact]
    public void GenericReadonlyProjectionInheritanceAndExpandingReferencesCommitAndColdRestore() {
        GeneratorTestRun run = RunGenerator("""
            using System;
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using Atelia.DurableGraph.Persistence;
            using Atelia.DurableGraph.Generated;
            [DurableType("Pair",1)] public readonly partial struct Pair<T> {
                [DurableField(1)] private readonly T _left;
                [DurableField(2)] private readonly T _right;
                public Pair(T left,T right) { _left=left; _right=right; }
                public T Left => _left; public T Right => _right;
            }
            [DurableType("Box",1)] public partial class Box<T> : IDurableObject {
                [DurableField(1)] private T _value;
                public Box(T value) { _value=value; }
                public T Value => _value; public void Set(T value) => _value=value;
            }
            [DurableType("Node",1)] public partial class Node<T> : IDurableObject {
                [DurableField(1)] public T Value;
                [DurableField(2)] public Node<Node<T>>? Next;
                public Node(T value) { Value=value; }
            }
            [DurableType("Base",1)] public partial class Base<U> : IDurableObject where U:class, IDurableObject {
                [DurableField(1)] private readonly U _baseValue;
                public Base(U value) { _baseValue=value; }
                public U BaseValue => _baseValue;
            }
            [DurableType("Derived",1)] public partial class Derived<T,U> : Base<U> where T:class where U:class, IDurableObject {
                [DurableField(1)] private readonly T _derivedValue;
                public Derived(T value,U other):base(other) { _derivedValue=value; }
                public T DerivedValue => _derivedValue;
            }
            [DurableType("World",1)] public partial class World:IDurableObject {
                public static int Constructors;
                [DurableField(1)] private readonly Box<Pair<int>> _numbers;
                [DurableField(2)] private readonly Derived<string,Node<int>> _mixed;
                [DurableField(3)] private readonly Box<string> _equal;
                [DurableField(4)] private readonly Box<string> _shared;
                [DurableField(5)] private readonly Box<uint> _number;
                [DurableField(6)] private readonly Box<string> _empty;
                [DurableField(7)] private readonly Box<string> _null;
                [DurableField(8)] private readonly Box<World> _back;
                public World(int initial) {
                    Constructors++;
                    string first=new string(new[]{'s','a','m','e'}), second=new string(new[]{'s','a','m','e'});
                    _numbers=new(new(initial,2)); _mixed=new(first,new(17));
                    _equal=new(second); _shared=new(first); _number=new(27);
                    _empty=new(string.Empty); _null=new(null!); _back=new(this);
                }
                public void Change() => _numbers.Set(new(8,9));
                public bool Check(int left,int right) => _numbers.Value.Left==left && _numbers.Value.Right==right &&
                    _mixed.BaseValue.Value==17 && _mixed.BaseValue.Next==null && _number.Value==27 &&
                    _mixed.DerivedValue==_equal.Value && !ReferenceEquals(_mixed.DerivedValue,_equal.Value) &&
                    ReferenceEquals(_mixed.DerivedValue,_shared.Value) && ReferenceEquals(_empty.Value,string.Empty) &&
                    _null.Value==null && ReferenceEquals(_back.Value,this);
            }
            public static class Host {
                public static bool Probe(string path) {
                    var models=new StateModelRegistry(); DurableDefinitions.Register(models);
                    World original=new(1);
                    using (var repository=FixtureGraphRepository.CreateNew(path, models)) {
                        using var session=repository.Create(original);
                        session.Commit(new(100,100)); original.Change(); session.Commit(new(100,100));
                        if (!ReferenceEquals(original,session.World) || !original.Check(8,9)) return false;
                    }
                    using (var repository=FixtureGraphRepository.OpenExisting(path, models)) {
                        using var session=repository.Load<World>();
                        if (ReferenceEquals(original,session.World) || !session.World.Check(8,9) || World.Constructors!=1) return false;
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

    [Fact]
    public void GenericConstraintsAreCopiedButRefLikeArgumentsRemainOutsideSupportedShapes() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            public interface IMarker { }
            [DurableType("Constrained",1)] public partial class Constrained<T,U> : IDurableObject where T:class,IMarker,new() where U:T {
                [DurableField(1)] public U Value=default!;
            }
            [DurableType("Value",1)] public readonly partial struct Value<T> where T:unmanaged {
                [DurableField(1)] private readonly T _value;
            }
            """);
        AssertSchemaOnlyCompiles(run);
        string generated = GeneratedSource(run,"DurableGenericStates.g.cs");
        Assert.Contains("where T : class, global::IMarker, new() where U : T",generated);
        GeneratorTestRun rejected = RunGenerator("using Atelia.DurableGraph; [DurableType(\"Bad\",1)] public partial struct Bad<T> where T:allows ref struct { [DurableField(1)] public T Value; }");
        Assert.Contains(rejected.GeneratorDiagnostics, diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenericCompilationAdaptsPrivateLegacyTwoParameterOwnerUpgradeWithLocalAliases(bool globalAliases) {
        using AncestryHistoryDirectory history = new();
        GeneratorTestRun first = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("Box",1)] public partial class Box<T>:IDurableObject { [DurableField(1)] public T Value=default!; }
            [DurableType("World",1)] public partial class World:IDurableObject { [DurableField(1)] public int Value; }
            """);
        AssertSchemaOnlyCompiles(first);
        new SchemaHistoryTool().Publish(history.WriteManifest(first),history.History);
        string source = """
            using OldState=Atelia.DurableGraph.Generated.Family_576F726C64.V1;
            using NewState=Atelia.DurableGraph.Generated.Family_576F726C64.V2;
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using Atelia.DurableGraph.Persistence;
            [DurableType("Box",1)] public partial class Box<T>:IDurableObject { [DurableField(1)] public T Value=default!; }
            [DurableType("World",2)] public partial class World:IDurableObject {
                [DurableField(1)] public long Value;
                private static void UpgradeStateV1ToV2(in OldState prior,out NewState next) => next=new(prior.Segment0Field1+7L);
            }
            public static class Host {
                public static StateModelRegistry Models() { var result=new StateModelRegistry(); Atelia.DurableGraph.Generated.DurableDefinitions.Register(result); return result; }
                public static object Prior() => new OldState(5);
            }
            """;
        if (globalAliases) source = source.Replace("using OldState=", "global using OldState=")
            .Replace("using NewState=", "global using NewState=");
        GeneratorTestRun second = RunGenerator(source,history.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(second);
        Assembly assembly=EmitAndLoad(second.OutputCompilation);
        Type host=assembly.GetType("Host")!;
        StateModelRegistry models=host.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>()();
        StateModelBinding model=models.Snapshot().ResolveCurrentModel(assembly.GetType("World")!);
        ObjectStateRecord prior=new(new(19),new DurableSchema("World",1,new DurableFieldInfo(1,TypeTag.Int32)),
            host.GetMethod("Prior")!.CreateDelegate<Func<object>>()());
        ObjectStateRecord current=model.Normalize(prior);
        Assert.Equal(new ObjectId(19),current.Id);
        Assert.Equal(12L,StateModelField(current,"Segment0Field1"));
        Assert.Equal(5,StateModelField(prior,"Segment0Field1"));
    }

    [Fact]
    public void GenericCompilationDiagnosesFormerDomainNestedUpgradeDtoNames() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("Box",1)] public partial class Box<T>:IDurableObject { [DurableField(1)] public T Value=default!; }
            [DurableType("World",2)] public partial class World:IDurableObject {
                [DurableField(1)] public long Value;
                private static void UpgradeStateV1ToV2(in __DurableState.V1 prior,out __DurableState.V2 next) => next=new(prior.Segment0Field1);
            }
            """,SchemaHistory("world.v1.dgschema","World",1,(1,2)));
        Assert.Contains(run.GeneratorDiagnostics,diagnostic => diagnostic.Id=="DG0020" &&
            diagnostic.GetMessage().Contains("Family_576F726C64",StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitUpgradeAttributeSelectsFamilyStateEngineForNonGenericDefinitions() {
        using AncestryHistoryDirectory history = new();
        GeneratorTestRun first = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("World",1)] public partial class World:IDurableObject { [DurableField(1)] public int Value; }
            """);
        AssertSchemaOnlyCompiles(first);
        new SchemaHistoryTool().Publish(history.WriteManifest(first),history.History);
        GeneratorTestRun second = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using Atelia.DurableGraph.Persistence;
            using WorldStates=Atelia.DurableGraph.Generated.Family_576F726C64;
            [DurableType("World",2)] public partial class World:IDurableObject { [DurableField(1)] public long Value; }
            internal static class Upgrades {
                [DurableUpgrade(typeof(World),1)]
                internal static void Upgrade(in WorldStates.V1 prior,out WorldStates.V2 next,UpgradeContext context) {
                    if (context.ObjectId.Value!=31 || context.SourceObjectSchema.Version!=1 || context.TargetObjectSchema.Version!=2)
                        throw new System.InvalidOperationException("wrong invocation context");
                    next=new(prior.Segment0Field1+11L);
                }
            }
            public static class Host {
                public static StateModelRegistry Models() { var result=new StateModelRegistry(); Atelia.DurableGraph.Generated.DurableDefinitions.Register(result); return result; }
                public static object Prior() => new WorldStates.V1(5);
            }
            """,history.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(second);
        Assert.Contains("public static class Family_576F726C64",GeneratedSource(second,"DurableGenericStates.g.cs"));
        Assembly assembly=EmitAndLoad(second.OutputCompilation);
        Type host=assembly.GetType("Host")!;
        StateModelRegistry models=host.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>()();
        StateModelBinding model=models.Snapshot().ResolveCurrentModel(assembly.GetType("World")!);
        ObjectStateRecord source=new(new(31),new DurableSchema("World",1,new DurableFieldInfo(1,TypeTag.Int32)),
            host.GetMethod("Prior")!.CreateDelegate<Func<object>>()());
        Assert.Equal(16L,StateModelField(model.Normalize(source),"Segment0Field1"));
    }

    [Theory]
    [InlineData("typeof(string)","internal static class Upgrades {","}")]
    [InlineData("typeof(Value)","internal static class Upgrades {","}")]
    [InlineData("typeof(World)","internal static class Outer { internal static class Upgrades {","} }")]
    [InlineData("typeof(World)","internal static class Upgrades<T> {","}")]
    public void ExplicitUpgradeAttributeRejectsUnsupportedOwnerOrHostInsteadOfIgnoringIt(string owner,string hostStart,string hostEnd) {
        string source="""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("World",2)] public partial class World:IDurableObject { [DurableField(1)] public long Value; }
            [DurableType("Value",1)] public partial struct Value { [DurableField(1)] public int X; }
            @@HOST_START@@
                [DurableUpgrade(@@OWNER@@,1)]
                internal static void Upgrade(in int prior,out int next,UpgradeContext context) => next=prior;
            @@HOST_END@@
            """.Replace("@@OWNER@@",owner).Replace("@@HOST_START@@",hostStart).Replace("@@HOST_END@@",hostEnd);
        GeneratorTestRun run=RunGenerator(source,SchemaHistory("world.v1.dgschema","World",1,(1,2)));
        Assert.Contains(run.GeneratorDiagnostics,diagnostic => diagnostic.Id=="DG0020");
    }
}
