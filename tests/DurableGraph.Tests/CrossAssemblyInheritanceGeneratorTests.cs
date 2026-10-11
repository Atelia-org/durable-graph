using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Collections.Immutable;
using Atelia.DurableGraph.Build;
using Atelia.DurableGraph.Persistence;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrossAssemblyInheritanceHiddenValuesAndParameterOriginsRoundTripFromMetadata(bool referenceAssembly) {
        GeneratorTestRun library = RunCrossAssemblyGenerator(InheritanceLibrary, forceDefinitions: "true");
        AssertSchemaOnlyCompiles(library);
        using MemoryStream image = new();
        EmitResult emitted = library.OutputCompilation.Emit(image, options: new EmitOptions(metadataOnly: referenceAssembly, includePrivateMembers: !referenceAssembly));
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        MetadataReference reference = MetadataReference.CreateFromImage(ImmutableArray.CreateRange(image.ToArray()));
        GeneratorTestRun app = RunCrossAssemblyGenerator(InheritanceApplication, [reference]);
        AssertSchemaOnlyCompiles(app);
        string generated = GeneratedSource(app, "DurableGenericStates.g.cs");
        Assert.DoesNotContain("Remote.Internal", generated);
        Assert.Contains("BindBaseProjection<global::Remote.Base<T, T, int>", generated);
        Assert.Contains("projection2 = context.ResolveCurrentValue(typeof(T))", generated);
        Assert.Contains("baseProjection.Hydrate", generated);
        Assert.Contains("var baseState = default(global::Atelia.DurableGraph.Generated.Family_456D707479.V1);", generated);
        Assert.Contains("(2, \"Base\"", GeneratedSource(library, "DurableGraphSchemaExports.g.cs"));
        Assert.DoesNotContain("(2, \"Base\"", GeneratedSource(app, "DurableGraphSchemaExports.g.cs"));
        using var scope = new CrossAssemblyLoadScope(library, app);
        Type host = scope.Load(app).GetType("App.Host")!;
        StateModelRegistry models = host.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>()();
        var snapshot = models.Snapshot();
        IDurableObject[] seeds = host.GetMethod("Seed")!.CreateDelegate<Func<IDurableObject[]>>()();
        var describe = host.GetMethod("Describe")!.CreateDelegate<Func<IDurableObject, string>>();
        foreach (IDurableObject seed in seeds) {
            StateModelBinding binding = snapshot.ResolveCurrentModel(seed.GetType());
            CaptureSession session = new();
            using CaptureContext capture = session.BeginCapture(snapshot);
            binding.AddRoot(capture, seed);
            ObjectStateRecord state = Assert.Single(capture.Seal().Objects);
            IDurableObject restored = binding.Allocate();
            binding.Hydrate(restored, state, new ObjectReadTable(new Dictionary<ObjectId, object>()));
            Assert.Equal(describe(seed), describe(restored));
        }
    }

    [Theory]
    [InlineData("constructor")]
    [InlineData("field")]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("apply-default")]
    [InlineData("delta")]
    [InlineData("contract")]
    public void CrossAssemblyInheritanceRejectsInvalidPublicExecutionContract(string defect) {
        // Retain only the public execution surface. Private
        // generated factories must not be required, and malformed helpers need no executable body.
        string publicSource = $$"""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [assembly:DurableSchemaExport({{(defect == "contract" ? 1 : 2)}},"Base",1,{{SymbolDisplay.FormatLiteral(BaseManifest, true)}})]
            [DurableType("Base",1)] public class Base:IDurableObject { }
            namespace Atelia.DurableGraph.Generated {
                public static class Family_42617365 {
                    public static readonly StateDefinitionBinding Definition = null!;
                    public readonly struct V1 {
                        public readonly {{(defect == "field" ? "long" : "int")}} Segment0Field1;
                        {{(defect == "constructor" ? "private" : "public")}} V1(int valueSegment0Field1) { Segment0Field1=valueSegment0Field1; }
                    }
                    public readonly struct BodyV1 {
                        public static void Write({{(defect == "write" ? "in" : "ref")}} global::Atelia.Binary.BareValueWriter writer,in V1 value,DurableSchema schema) { }
                        public static V1 Read({{(defect == "read" ? "in" : "ref")}} global::Atelia.Binary.BareValueReader reader,DurableSchema schema)=>default;
                        public static V1 Apply(ref global::Atelia.Binary.BareValueReader reader,in V1 prior,DurableSchema schema,bool requireChanges={{(defect == "apply-default" ? "true" : "false")}})=>default;
                        public static void Visit(in V1 value,IStateReferenceVisitor visitor,DurableSchema schema) { }
                        public static Serialization.PreparedBaseBody PrepareBase(in V1 value,DurableSchema schema)=>default;
                        public static Serialization.PreparedDeltaBody PrepareDelta(in V1 prior,{{(defect == "delta" ? "" : "in")}} V1 current,DurableSchema schema)=>default;
                        public static bool StateEquals(in V1 left,in V1 right,DurableSchema schema)=>true;
                    }
                }
            }
            """;
        MetadataReference reference = EmitCrossAssemblyRawReference(publicSource);
        GeneratorTestRun consumer = RunCrossAssemblyGenerator("using Atelia.DurableGraph; [DurableType(\"Leaf\",1)] public partial class Leaf:Base { }", [reference]);
        Assert.Contains(consumer.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0023");
        Assert.DoesNotContain(consumer.GeneratorDiagnostics, diagnostic => diagnostic.Id == "CS8785");
    }

    [Fact]
    public void CrossAssemblyInheritanceLocalAndSplitHaveIdenticalHistoryDtoAndWholeBodyDelta() {
        const string ancestor = """
            namespace Remote {
                [Atelia.DurableGraph.DurableType("Ancestor",1)] public partial class Ancestor<T,U>:Atelia.DurableGraph.IDurableObject {
                    [Atelia.DurableGraph.DurableField(1)] public T First;
                    [Atelia.DurableGraph.DurableField(2)] public U Second;
                    [Atelia.DurableGraph.DurableField(3)] public int C;
                    [Atelia.DurableGraph.DurableField(4)] public int D;
                    [Atelia.DurableGraph.DurableField(5)] public int E;
                    [Atelia.DurableGraph.DurableField(6)] public int F;
                    [Atelia.DurableGraph.DurableField(7)] public int G;
                    [Atelia.DurableGraph.DurableField(8)] public int H;
                    [Atelia.DurableGraph.DurableField(9)] public int I;
                }
                public static class Catalog { public static void Register(Atelia.DurableGraph.IStateModelRegistration models)=>Atelia.DurableGraph.Generated.DurableDefinitions.Register(models); }
            }
            """;
        const string descendant = """
            namespace App {
                [Atelia.DurableGraph.DurableType("Leaf",1)] public partial class Leaf<T>:Remote.Ancestor<T,T> {
                    [Atelia.DurableGraph.DurableField(1)] public T Own;
                }
                public static class Host {
                    public static Atelia.DurableGraph.Persistence.StateModelRegistry Models() {
                        var models=new Atelia.DurableGraph.Persistence.StateModelRegistry();
                        REMOTE_REGISTER
                        Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);return models;
                    }
                    public static Atelia.DurableGraph.IDurableObject Seed()=>new Leaf<int>{First=1,Second=2,Own=3};
                    public static void Mutate(Atelia.DurableGraph.IDurableObject value) { var leaf=(Leaf<int>)value;leaf.First=4;leaf.Own=5; }
                }
            }
            """;
        GeneratorTestRun library = RunCrossAssemblyGenerator(ancestor, forceDefinitions: "true");
        MetadataReference reference = EmitCrossAssemblyReference(library).Reference;
        GeneratorTestRun local = RunCrossAssemblyGenerator(ancestor + descendant.Replace("REMOTE_REGISTER", ""), forceDefinitions: "true");
        GeneratorTestRun split = RunCrossAssemblyGenerator(descendant.Replace("REMOTE_REGISTER", "Remote.Catalog.Register(models);"), [reference]);
        AssertSchemaOnlyCompiles(local);
        AssertSchemaOnlyCompiles(split);
        using AncestryHistoryDirectory localHistory = new();
        using AncestryHistoryDirectory splitHistory = new();
        SchemaHistoryTool tool = new();
        tool.Publish(localHistory.WriteManifest(local), localHistory.History);
        string refsPath = Path.Combine(splitHistory.History, "../refs.g.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(refsPath)!);
        File.WriteAllText(refsPath, GeneratedSource(split, "DurableGraphSchemaHistoryReferences.g.cs"));
        tool.Publish(splitHistory.WriteManifest(split), splitHistory.History, refsPath);
        string record = Assert.Single(Directory.GetFiles(splitHistory.History, "*.dgschema"));
        Assert.Equal(File.ReadAllBytes(Path.Combine(localHistory.History, Path.GetFileName(record))), File.ReadAllBytes(record));
        using var scope = new CrossAssemblyLoadScope(library, local, split);
        DurableSchema? exact = null;
        foreach (GeneratorTestRun run in new[] { local, split }) {
            var assembly = scope.Load(run);
            Type host = assembly.GetType("App.Host")!;
            var models = host.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>()().Snapshot();
            IDurableObject seed = host.GetMethod("Seed")!.CreateDelegate<Func<IDurableObject>>()();
            var model = models.ResolveCurrentModel(seed.GetType());
            if (exact is not null) Assert.Equal(exact, model.CurrentSchema);
            exact = model.CurrentSchema;
            Assert.Single(assembly.GetType("Atelia.DurableGraph.Generated.Family_4C656166+V1`1")!.GetGenericArguments());
            CaptureSession session = new();
            using (CaptureContext capture = session.BeginCapture(models)) {
                model.AddRoot(capture, seed);
                CapturedGraph frozen = capture.Seal();
                var prepared = Assert.Single(session.Prepare(frozen).Objects);
                Assert.Equal(new byte[] { 2, 4, 0, 0, 0, 0, 0, 0, 0, 6 }, prepared.BaseBody.Body.ToArray());
                session.Accept(frozen);
            }
            host.GetMethod("Mutate")!.CreateDelegate<Action<IDurableObject>>()(seed);
            using CaptureContext next = session.BeginCapture(models);
            model.AddRoot(next, seed);
            var changed = Assert.Single(session.Prepare(next.Seal()).Objects);
            Assert.Equal(new byte[] { 1, 2, 8, 10 }, changed.DeltaBody!.Body.ToArray());
        }
    }

    [Fact]
    public void CrossAssemblyInheritanceRetainedLeafImportsDeletedBaseHistoryWithoutPublishingIt() {
        using AncestryHistoryDirectory libraryHistory = new();
        using AncestryHistoryDirectory consumerHistory = new();
        SchemaHistoryTool tool = new();
        GeneratorTestRun library1 = RunCrossAssemblyGenerator("using Atelia.DurableGraph; [DurableType(\"Base\",1)] public partial class OldBase:IDurableObject { [DurableField(1)] public int X; }", forceDefinitions: "true");
        tool.Publish(libraryHistory.WriteManifest(library1), libraryHistory.History);
        GeneratorTestRun consumer1 = RunCrossAssemblyGenerator("using Atelia.DurableGraph; [DurableType(\"Leaf\",1)] public partial class Leaf:OldBase { [DurableField(1)] public int Y; }", [EmitCrossAssemblyReference(library1).Reference]);
        AssertSchemaOnlyCompiles(consumer1);
        string refsPath = Path.Combine(consumerHistory.History, "../refs.g.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(refsPath)!);
        File.WriteAllText(refsPath, GeneratedSource(consumer1, "DurableGraphSchemaHistoryReferences.g.cs"));
        tool.Publish(consumerHistory.WriteManifest(consumer1), consumerHistory.History, refsPath);
        GeneratorTestRun library2 = RunCrossAssemblyGenerator("using Atelia.DurableGraph; [DurableType(\"Base\",2)] public partial class NewBase:IDurableObject { [DurableField(1)] public long X; public static void UpgradeV1ToV2(in Atelia.DurableGraph.Generated.Family_42617365.V1 prior,out Atelia.DurableGraph.Generated.Family_42617365.V2 next)=>next=new(prior.Segment0Field1); }",
            history: libraryHistory.ReadAdditionalTexts(), forceDefinitions: "true");
        AssertSchemaOnlyCompiles(library2);
        // Current candidate Anchor has no external ancestry. Retained Leaf alone demands Base v1.
        GeneratorTestRun retained = RunCrossAssemblyGenerator("using Atelia.DurableGraph; [DurableType(\"Anchor\",1)] public partial class Anchor:IDurableObject { }",
            [EmitCrossAssemblyReference(library2).Reference], consumerHistory.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(retained);
        string generated = GeneratedSource(retained, "DurableGenericStates.g.cs");
        Assert.Contains("public static class Family_4C656166", generated);
        Assert.DoesNotContain("public static class Family_42617365", generated);
        Assert.DoesNotContain("OldBase", generated);
        Assert.Contains("(2, \"Leaf\", 1", GeneratedSource(retained, "DurableGraphSchemaExports.g.cs"));
        File.WriteAllText(refsPath, GeneratedSource(retained, "DurableGraphSchemaHistoryReferences.g.cs"));
        Assert.Contains("published 1", tool.Publish(consumerHistory.WriteManifest(retained), consumerHistory.History, refsPath).Message);
        Assert.Equal(2, Directory.GetFiles(consumerHistory.History, "*.dgschema").Length);

        MetadataReference missingOld = EmitCrossAssemblyRawReference("[assembly:Atelia.DurableGraph.DurableSchemaExport(2,\"Base\",2,\"invalid-unused\")] public class Unrelated { }");
        GeneratorTestRun missing = RunCrossAssemblyGenerator("using Atelia.DurableGraph; [DurableType(\"Anchor\",1)] public partial class Anchor:IDurableObject { }",
            [missingOld], consumerHistory.ReadAdditionalTexts());
        Assert.Contains(missing.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0023" && diagnostic.GetMessage().Contains("missing or duplicate exported version"));
    }

    [Theory]
    [InlineData("state-constraint")]
    [InlineData("ops-constraint")]
    [InlineData("dto-ref-struct")]
    [InlineData("body-ref-struct")]
    [InlineData("mutable-dto")]
    public void CrossAssemblyInheritanceRejectsStrongerConstraintsAndRefLikeHelpers(string defect) {
        GeneratorTestRun exemplar = RunCrossAssemblyGenerator("using Atelia.DurableGraph; [DurableType(\"Base\",1)] public partial class Base<T>:IDurableObject { [DurableField(1)] public T Value; }", forceDefinitions: "true");
        AssertSchemaOnlyCompiles(exemplar);
        string manifest = (string)exemplar.OutputCompilation.Assembly.GetAttributes().Single(attribute => attribute.AttributeClass?.Name == "DurableSchemaExportAttribute").ConstructorArguments[3].Value!;
        string stateConstraint = defect == "state-constraint" ? ", global::System.IComparable<TState>" : "";
        string source = $$"""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [assembly:DurableSchemaExport(2,"Base",1,{{SymbolDisplay.FormatLiteral(manifest, true)}})]
            [DurableType("Base",1)] public class Base<T>:IDurableObject { }
            namespace Atelia.DurableGraph.Generated {
                public static class Family_42617365 {
                    public static readonly StateDefinitionBinding Definition = null!;
                    public {{(defect == "mutable-dto" ? "" : "readonly")}} {{(defect == "dto-ref-struct" ? "ref" : "")}} struct V1<TState> where TState:unmanaged{{stateConstraint}} {
                        public readonly TState Segment0Field1;
                        public V1(TState value) { Segment0Field1=value; }
                    }
                    public readonly {{(defect == "body-ref-struct" ? "ref" : "")}} struct BodyV1<TState,TOps>
                        where TState:unmanaged{{stateConstraint}}
                        where TOps:struct,IStateOps<TState>{{(defect == "ops-constraint" ? ",global::System.IDisposable" : "")}} {
                        public static void Write(ref global::Atelia.Binary.BareValueWriter writer,in V1<TState> value,DurableSchema schema) { }
                        public static V1<TState> Read(ref global::Atelia.Binary.BareValueReader reader,DurableSchema schema)=>default;
                        public static V1<TState> Apply(ref global::Atelia.Binary.BareValueReader reader,in V1<TState> prior,DurableSchema schema,bool requireChanges=false)=>default;
                        public static void Visit(in V1<TState> value,IStateReferenceVisitor visitor,DurableSchema schema) { }
                        public static Serialization.PreparedBaseBody PrepareBase(in V1<TState> value,DurableSchema schema)=>default;
                        public static Serialization.PreparedDeltaBody PrepareDelta(in V1<TState> prior,in V1<TState> current,DurableSchema schema)=>default;
                        public static bool StateEquals(in V1<TState> left,in V1<TState> right,DurableSchema schema)=>true;
                    }
                }
            }
            """;
        MetadataReference reference = EmitCrossAssemblyRawReference(source);
        GeneratorTestRun consumer = RunCrossAssemblyGenerator("using Atelia.DurableGraph; [DurableType(\"Leaf\",1)] public partial class Leaf<T>:Base<T> { }", [reference]);
        Assert.Contains(consumer.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0023" && diagnostic.GetMessage().Contains("helpers"));
        Assert.DoesNotContain(consumer.GeneratorDiagnostics, diagnostic => diagnostic.Id == "CS8785");
    }

    [Fact]
    public void CrossAssemblyInheritanceDistinctNominalArgumentsCanBothUseObjectIdState() {
        GeneratorTestRun library = RunCrossAssemblyGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("Base",1)] public partial class Base<T,U>:IDurableObject {
                [DurableField(1)] private T _first;
                [DurableField(2)] private U _second;
                protected Base(T first,U second) { _first=first;_second=second; }
                public T First=>_first; public U Second=>_second;
            }
            public static class Catalog { public static void Register(IStateModelRegistration models)=>Atelia.DurableGraph.Generated.DurableDefinitions.Register(models); }
            """, forceDefinitions: "true");
        GeneratorTestRun app = RunCrossAssemblyGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using Atelia.DurableGraph.Persistence;
            [DurableType("NodeA",1)] public partial class NodeA:IDurableObject { [DurableField(1)] public int A; }
            [DurableType("NodeB",1)] public partial class NodeB:IDurableObject { [DurableField(1)] public int B; }
            [DurableType("Leaf",1)] public partial class Leaf:Base<NodeA,NodeB> { public Leaf():base(new NodeA{A=17},new NodeB{B=29}) { } }
            public static class Host {
                public static StateModelRegistry Models() { var models=new StateModelRegistry();Catalog.Register(models);Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);return models; }
                public static IDurableObject Seed()=>new Leaf();
                public static string Describe(IDurableObject value) {var leaf=(Leaf)value;return $"{leaf.First.A}/{leaf.Second.B}";}
            }
            """, [EmitCrossAssemblyReference(library).Reference]);
        AssertSchemaOnlyCompiles(app);
        using var scope = new CrossAssemblyLoadScope(library, app);
        var assembly = scope.Load(app);
        Type host = assembly.GetType("Host")!;
        var snapshot = host.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>()().Snapshot();
        IDurableObject seed = host.GetMethod("Seed")!.CreateDelegate<Func<IDurableObject>>()();
        var binding = snapshot.ResolveCurrentModel(seed.GetType());
        Assert.Equal(new[] { typeof(ObjectId), typeof(ObjectId) }, binding.GetType().GenericTypeArguments[1].GenericTypeArguments);
        Assert.NotEqual(binding.CurrentSchema.BaseSchema!.Fields[0].TargetType, binding.CurrentSchema.BaseSchema.Fields[1].TargetType);
        CaptureSession session = new();
        using CaptureContext capture = session.BeginCapture(snapshot);
        ObjectId rootId = binding.AddRoot(capture, seed);
        var graph = capture.Seal();
        Assert.Equal(3, graph.Objects.Count);
        Dictionary<ObjectId, object> instances = new();
        foreach (ObjectStateRecord row in graph.Objects) instances.Add(row.Id, snapshot.ResolveCurrentModel(assembly.GetType(row.Schema!.SchemaId)!).Allocate());
        ObjectReadTable table = new(instances);
        foreach (ObjectStateRecord row in graph.Objects) snapshot.ResolveCurrentModel(instances[row.Id].GetType()).Hydrate(instances[row.Id], row, table);
        Assert.Equal("17/29", host.GetMethod("Describe")!.CreateDelegate<Func<IDurableObject, string>>()((IDurableObject)instances[rootId]));
    }

    private const string BaseManifest = "// durable-graph-schema-history-manifest:9\n// schema-begin\n// schema-id-base64:QmFzZQ==\n// version:1\n// kind:1\n// arity:0\n// field:1|2\n// schema-end\n";

    private const string InheritanceLibrary = """
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        namespace Remote {
            [DurableType("Pair",1)] internal readonly partial struct InternalPair<T> {
                [DurableField(1)] private readonly T _value;
                public InternalPair(T value) { _value=value; }
                public T Value=>_value;
            }
            [DurableType("Point",1)] internal readonly partial struct InternalPoint {
                [DurableField(1)] private readonly int _value;
                public InternalPoint(int value) { _value=value; }
                public int Value=>_value;
            }
            [DurableType("Base",1)] public abstract partial class Base<T,U,P>:IDurableObject {
                [DurableField(1)] private readonly InternalPair<T>? _pair;
                [DurableField(2)] private readonly InternalPoint? _point;
                [DurableField(3)] private readonly T _first;
                [DurableField(4)] private readonly U _second;
                protected Base(T first,U second) { _pair=new(first); _point=new(57); _first=first; _second=second; }
                public string DescribeBase()=> $"{_pair!.Value.Value}/{_point!.Value.Value}/{_first}/{_second}";
            }
            [DurableType("Empty",1)] public abstract partial class Empty<T>:IDurableObject { }
            public static class Catalog {
                public static void Register(IStateModelRegistration models)=>Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
            }
        }
        """;

    private const string InheritanceApplication = """
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Persistence;
        namespace App {
            [DurableType("Repeat",1)] public partial class Repeat<T>:Remote.Base<T,T,int> {
                [DurableField(1)] private readonly T _own;
                public Repeat(T first,T second,T own):base(first,second) { _own=own; }
                public string Describe()=>DescribeBase()+"/"+_own;
            }
            [DurableType("Closed",1)] public partial class Closed:Remote.Base<int,int,string> {
                public Closed():base(10,20) { }
                public string Describe()=>DescribeBase();
            }
            [DurableType("Swapped",1)] public partial class Swapped<T,U>:Remote.Base<U,T,int> {
                public Swapped(T first,U second):base(second,first) { }
                public string Describe()=>DescribeBase();
            }
            [DurableType("Phantom",1)] public partial class Phantom<T>:Remote.Base<int,int,T> {
                public Phantom():base(30,40) { }
                public string Describe()=>DescribeBase();
            }
            [DurableType("NoFields",1)] public partial class NoFields:Remote.Empty<int> { }
            public static class Host {
                public static StateModelRegistry Models() { var models=new StateModelRegistry();Remote.Catalog.Register(models);Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);return models; }
                public static IDurableObject[] Seed()=>new IDurableObject[]{new Repeat<int>(1,2,3),new Closed(),new Swapped<int,bool>(7,true),new Phantom<bool>(),new NoFields()};
                public static string Describe(IDurableObject value)=>value switch {
                    Repeat<int> x=>x.Describe(),Closed x=>x.Describe(),Swapped<int,bool> x=>x.Describe(),Phantom<bool> x=>x.Describe(),NoFields=>"empty",_=>throw new System.Exception()
                };
            }
        }
        """;
}
