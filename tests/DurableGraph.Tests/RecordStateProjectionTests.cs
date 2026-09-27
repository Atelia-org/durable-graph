using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Reflection;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void RecordProjectionReadsStorageAndHydratesWithoutConstructorsOrPropertyCalls() {
        GeneratorTestRun run = RunGenerator("""
            using System;
            using System.Collections.Generic;
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using Atelia.DurableGraph.Serialization;
            using States = Atelia.DurableGraph.Generated.Family_526563;
            [DurableType("Rec", 1)]
            public partial record struct Rec([field:DurableField(1)] int Number) {
                public static int Constructors, Initializers, Getters, Setters;
                [field:DurableField(2)] public int Value {
                    get { Getters++; return field + 1000; }
                    set { Setters++; field = value; }
                } = Initialize();
                [field:Transient] public int Scratch { get; set; }
                private static int Initialize() { Initializers++; return 7; }
                public Rec() : this(0) { Constructors++; throw new Exception("Do not construct during hydration"); }
            }
            public static class Host {
                public static bool Probe() {
                    Rec source = new(3) { Value = 9, Scratch = 4 };
                    var schema = new DurableSchema("Rec", 1, SchemaKind.InlineValue,
                        new DurableFieldInfo(1,TypeTag.Int32), new DurableFieldInfo(2,TypeTag.Int32));
                    var slot = new DurableFieldInfo(1,TypeTag.InlineValue,inlineSchema:schema);
                    using var context = new CaptureSession().BeginCapture();
                    var first = Rec.__DurableProjection.Capture(in source,context,slot);
                    if (first.Segment0Field1 != 3 || first.Segment0Field2 != 9) return false;
                    source.Scratch = 88;
                    var transient = Rec.__DurableProjection.Capture(in source,context,slot);
                    if (!States.BodyV1.StateEquals(in first,in transient,schema)) return false;
                    var changed = source with { Number = 4 };
                    var current = Rec.__DurableProjection.Capture(in changed,context,slot);
                    var delta = States.BodyV1.PrepareDelta(in first,in current,schema);
                    var deltaReader = new BinaryPayloadReader(delta.Body);
                    var applied = States.BodyV1.Apply(ref deltaReader,in first,schema);
                    deltaReader.EnsureFullyConsumed();
                    if (!delta.HasChanges || !States.BodyV1.StateEquals(in applied,in current,schema)) return false;
                    if (!delta.Body.SequenceEqual(new byte[] {1,8})) return false;
                    var prepared = States.BodyV1.PrepareBase(in first,schema);
                    if (!prepared.Body.SequenceEqual(new byte[] {6,18})) return false;
                    var reader = new BinaryPayloadReader(prepared.Body);
                    var decoded = States.BodyV1.Read(ref reader,schema);
                    reader.EnsureFullyConsumed();
                    Rec restored = default;
                    var objects = new ObjectReadTable(new Dictionary<ObjectId,object>());
                    Rec.__DurableProjection.Hydrate(ref restored,in decoded,objects,slot);
                    var restoredState = Rec.__DurableProjection.Capture(in restored,context,slot);
                    if (!States.BodyV1.StateEquals(in first,in restoredState,schema) || restored.Scratch != 0) return false;
                    if (Rec.Constructors != 0 || Rec.Initializers != 1 || Rec.Getters != 0 || Rec.Setters != 1) return false;
                    restored.Deconstruct(out int number);
                    if (number != 3 || restored != (restored with { }) || restored == changed) return false;
                    source.Number = 111;
                    return first.Segment0Field1 == 3 && first.Segment0Field2 == 9 && source.Scratch == 88;
                }
            }
            """);
        AssertSchemaOnlyCompiles(run);
        string generated = GeneratedSource(run, "DurableGenericStates.g.cs");
        Assert.Contains("partial record struct Rec", generated);
        Assert.Contains("Name = \"<Value>k__BackingField\"", generated);
        Assert.Contains("ref global::System.Runtime.CompilerServices.Unsafe.AsRef(in value)", generated);
        Assert.DoesNotContain("value.Value", generated);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        Assert.True(assembly.GetType("Host")!.GetMethod("Probe")!.CreateDelegate<Func<bool>>()());
        Type record = assembly.GetType("Rec")!;
        Assert.Equal(3, record.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length);
        Assert.DoesNotContain(record.GetMethods(BindingFlags.Static | BindingFlags.Public),
            method => method.Name.StartsWith("__DurableRead_", StringComparison.Ordinal));
    }

    [Fact]
    public void RecordReadonlyGenericProjectionPreservesReferencesAndSkipsInitAndDefaultConstructor() {
        GeneratorTestRun run = RunGenerator("""
            using System;
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using Atelia.DurableGraph.Persistence;
            using Atelia.DurableGraph.Generated;
            [DurableType("Key",1)]
            public readonly partial record struct Key<T>([field:DurableField(1)] T Part) {
                public static int Constructors, Initializers, Getters, Inits;
                [field:DurableField(2)] public int Extra {
                    get { Getters++; return field + 100; }
                    init { Inits++; field = value; }
                } = Initialize();
                [field:Transient] public int Scratch { get; init; }
                private static int Initialize() { Initializers++; return 5; }
                public Key() : this(default!) { Constructors++; throw new Exception("No default construction"); }
                public int Raw => Extra - 100;
            }
            [DurableType("World",1)] public partial class World : IDurableObject {
                [DurableField(1)] public Key<int> Number;
                [DurableField(2)] public Key<string> Text;
                [DurableField(3)] public string Alias;
                public World(string text) {
                    Number = new(17) { Extra = 7, Scratch = 8 };
                    Text = new(text) { Extra = 9, Scratch = 10 };
                    Alias = text;
                }
            }
            public static class Host {
                public static bool Probe(string path) {
                    var models = new StateModelRegistry(); DurableDefinitions.Register(models);
                    string text = new string(new[] {'s','a','m','e'});
                    var world = new World(text);
                    using (var repository = FixtureGraphRepository.CreateNew(path, models)) {
                        using var session = repository.Create(world);
                        session.Commit(new(100,100));
                        world.Number = world.Number with { Part = 23 };
                        session.Commit(new(100,100));
                    }
                    if (Key<int>.Getters != 0 || Key<string>.Getters != 0) return false;
                    using (var repository = FixtureGraphRepository.OpenExisting(path, models)) {
                        using var session = repository.Load<World>();
                        var restored = session.World;
                        if (restored.Number.Part != 23 || !ReferenceEquals(restored.Text.Part,restored.Alias) ||
                            ReferenceEquals(restored.Alias,text) || restored.Text.Scratch != 0 || restored.Number.Scratch != 0) return false;
                        if (Key<int>.Constructors != 0 || Key<string>.Constructors != 0 ||
                            Key<int>.Initializers != 1 || Key<string>.Initializers != 1 ||
                            Key<int>.Inits != 1 || Key<string>.Inits != 1 ||
                            Key<int>.Getters != 0 || Key<string>.Getters != 0) return false;
                        if (restored.Number.Raw != 7 || restored.Text.Raw != 9) return false;
                        restored.Number.Deconstruct(out int part);
                        if (part != 23 || restored.Number != (restored.Number with { })) return false;
                        session.Commit(new(100,100));
                    }
                    return true;
                }
            }
            """);
        AssertSchemaOnlyCompiles(run);
        string generated = GeneratedSource(run, "DurableGenericStates.g.cs");
        Assert.Contains("partial record struct Key<T>", generated);
        Assert.DoesNotContain("partial record struct Key<T>(", generated);
        Assert.Contains("Name = \"<Part>k__BackingField\"", generated);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        using RawBaseDirectory directory = new();
        Assert.True(assembly.GetType("Host")!.GetMethod("Probe")!.CreateDelegate<Func<string,bool>>()(directory.Path));
    }
}
