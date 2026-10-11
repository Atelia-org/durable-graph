using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Reflection;
using Atelia.DurableGraph.Build;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparedBaseGeneratedBodiesPreserveIndependentScalarAndReferenceGoldens(bool emptyLayout) {
        string[] types = emptyLayout ? [] : ["bool", "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "char", "System.Half", "float", "double", "string?"];
        string fields = string.Join("\n", types.Select((type, index) => $"[DurableField({index + 1})] private {type} _field{index};"));
        string source = FusedDeltaPreamble + """
            [DurableType("prepared.item", 1)]
            public sealed partial class Item : IDurableObject {
            """ + fields + "\n}\npublic static class Host {\n" + PreparedBaseHostMethod("Item", 1) + "\n}";
        GeneratorTestRun run = RunGenerator(source);
        AssertSchemaOnlyCompiles(run);
        var prepare = EmitAndLoad(run.OutputCompilation).GetType("FusedDelta.Host")!
            .GetMethod("PrepareBase1")!.CreateDelegate<Func<byte[], PreparedBaseBody>>();

        // Independently specified canonical values include all scalar kinds, raw NaN payloads,
        // a surrogate char, and a multi-byte string reference ID (not inline string content).
        string goldenHex = emptyLayout ? "" : string.Concat(new[] {
            "01", "FF", "80", "FFFF03", "FFFF03", "FFFFFFFF0F", "FFFFFFFF0F",
            "FFFFFFFFFFFFFFFFFF01", "FFFFFFFFFFFFFFFFFF01", "00D8", "357E",
            "4523C17F", "BC9A78563412F87F", "8001",
        });
        byte[] input = Convert.FromHexString(goldenHex);
        PreparedBaseBody prepared = prepare(input);
        Array.Clear(input);
        byte[] externalCopy = prepared.Body.ToArray();
        Array.Clear(externalCopy);
        _ = prepare(Convert.FromHexString(goldenHex));
        Assert.Equal(Convert.FromHexString(goldenHex), prepared.Body.ToArray());

        string generated = GeneratedSource(run, "DurableStates.g.cs");
        Assert.Contains("PreparedBaseBody PrepareBaseBody(in V1 current)", generated);
        Assert.Contains("WriteBaseBody(ref writer, in current);", generated);
        Assert.DoesNotContain("EstimateBase", generated);
    }

    [Fact]
    public void PreparedBaseHistoricalOverloadsUseExactRemovedAncestorLayouts() {
        using AncestryHistoryDirectory files = new();
        SchemaHistoryTool publisher = new();
        string initialSource = FusedDeltaPreamble + """
            [DurableType("prepared.base", 1)]
            public abstract partial class OldBase : IDurableObject {
                [DurableField(99)] private int _number;
            }
            [DurableType("prepared.leaf", 1)]
            public sealed partial class Leaf : OldBase {
                [DurableField(205)] private string? _name;
                [DurableField(99)] private bool _flag;
            }
            """;
        GeneratorTestRun initial = RunGenerator(initialSource);
        AssertSchemaOnlyCompiles(initial);
        publisher.Publish(files.WriteManifest(initial), files.History);
        string source = FusedDeltaPreamble + """
            [DurableType("prepared.base", 2)]
            public abstract partial class NewBase : IDurableObject {
                [DurableField(2)] private byte _small;
            }
            [DurableType("prepared.leaf", 2)]
            public sealed partial class Leaf : NewBase {
                [DurableField(1)] private uint _number;
            }
            public static class Host {
            """ + PreparedBaseHostMethod("Leaf", 1) + PreparedBaseHostMethod("Leaf", 2) + "\n}";
        GeneratorTestRun current = RunGenerator(source, files.ReadAdditionalTexts().Reverse().ToArray());
        AssertSchemaOnlyCompiles(current);
        Assembly assembly = EmitAndLoad(current.OutputCompilation);
        Assert.Null(assembly.GetType("FusedDelta.OldBase"));
        Type leaf = assembly.GetType("FusedDelta.Leaf")!;
        Assert.Equal(TypeTag.Int32, Assert.Single(ReadSchemaOnly(leaf, 1).BaseSchema!.Fields).TypeTag);
        Assert.Equal(TypeTag.Byte, Assert.Single(ReadSchemaOnly(leaf, 2).BaseSchema!.Fields).TypeTag);
        Type host = assembly.GetType("FusedDelta.Host")!;
        foreach (var (version, hex) in new[] { (1, "21018001"), (2, "FF808001") }) {
            var prepare = host.GetMethod("PrepareBase" + version)!.CreateDelegate<Func<byte[], PreparedBaseBody>>();
            Assert.Equal(Convert.FromHexString(hex), prepare(Convert.FromHexString(hex)).Body.ToArray());
        }
        MethodInfo[] overloads = leaf.GetNestedType("__DurableState", BindingFlags.NonPublic)!
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic).Where(method => method.Name == "PrepareBaseBody").ToArray();
        Assert.Equal(2, overloads.Length);
        Assert.All(overloads, method => Assert.True(Assert.Single(method.GetParameters()).ParameterType.IsByRef));
    }

    [Fact]
    public void PreparedBaseCapturesFrozenInheritedValuesAndStringIdsBeforeLaterDomainMutation() {
        string source = FusedDeltaPreamble + """
            using System.Linq;
            [DurableType("prepared.capture.base", 1)]
            public abstract partial class Base : IDurableObject {
                [DurableField(7)] private int _count = -17;
                protected void ChangeBase() { _count = 500; }
            }
            [DurableType("prepared.capture.leaf", 1)]
            public sealed partial class Leaf : Base {
                [DurableField(1)] private string _name = "A";
                [DurableField(2)] private long _total = 42;
                public void Change() { ChangeBase(); _name = "changed"; _total = 999; }
            }
            public static class Host {
                public static PreparedBaseBody Run() {
                    var session = new CaptureSession();
                    var context = session.BeginCapture();
                    var source = new Leaf();
                    Leaf.__DurableState.AddRoot(context, source);
                    var candidate = context.Seal();
                    var dto = candidate.Objects.Single(item => item.Kind == ObjectStateKind.Durable)
                        .GetState<Leaf.__DurableState.V1>();
                    source.Change();
                    var prepared = Leaf.__DurableState.PrepareBaseBody(in dto);
                    source.Change();
                    return prepared;
                }
            }
            """;
        GeneratorTestRun run = RunGenerator(source);
        AssertSchemaOnlyCompiles(run);
        PreparedBaseBody prepared = EmitAndLoad(run.OutputCompilation).GetType("FusedDelta.Host")!
            .GetMethod("Run")!.CreateDelegate<Func<PreparedBaseBody>>()();
        // Root is ID 1; its string is ID 2. Base segment is encoded before leaf fields.
        Assert.Equal<byte>([0x21, 0x02, 0x54], prepared.Body.ToArray());
    }

    private static string PreparedBaseHostMethod(string type, int version) => $$"""
        public static PreparedBaseBody PrepareBase{{version}}(byte[] body) {
            var reader = new BareValueReader(body);
            var dto = {{type}}.__DurableState.ReadBaseBodyV{{version}}(ref reader);
            reader.EnsureFullyConsumed();
            return {{type}}.__DurableState.PrepareBaseBody(in dto);
        }
        """;
}
