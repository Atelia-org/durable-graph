using Atelia.Binary;
using Atelia.DurableGraph.Schema;
using System.Reflection;
using Atelia.DurableGraph.Build;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    // Independently specified wire bytes: signed integer bounds use ZigZag,
    // char preserves an isolated UTF-16 surrogate, and floats preserve their bits.
    private const string ScalarLowGolden =
        "01FF80FFFF03FFFF03FFFFFFFF0FFFFFFFFF0FFFFFFFFFFFFFFFFFFF01FFFFFFFFFFFFFFFFFF0100D80080000000800000000000000080";
    private const string ScalarHighGolden =
        "00007FFEFF0300FEFFFFFF0F00FEFFFFFFFFFFFFFFFF0100FFDF357E4523C17FBC9A78563412F87F";

    [Fact]
    public void ScalarDtoCapturesAllKindsAndMatchesIndependentBoundaryAndFloatingBitGoldens() {
        GeneratorTestRun run = RunGenerator(ScalarDtoSource);
        AssertSchemaOnlyCompiles(run);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        var encode = assembly.GetType("ScalarDtos.Host")!.GetMethod("RoundTrip")!
            .CreateDelegate<Func<bool, byte[]>>();
        Assert.Equal(Convert.FromHexString(ScalarLowGolden), encode(false));
        Assert.Equal(Convert.FromHexString(ScalarHighGolden), encode(true));

        DurableSchema schema = ReadSchemaOnly(assembly.GetType("ScalarDtos.Item")!, 1);
        Assert.Equal<int>([1, 5, 6, 7, 8, 2, 9, 3, 10, 11, 12, 13, 14],
            schema.Fields.Select(field => (int)field.TypeTag));
        string generated = GeneratedSource(run, "DurableStates.g.cs");
        foreach (string method in new[] { "Boolean", "Byte", "SByte", "VarInt16", "VarUInt16", "VarInt32", "VarUInt32", "VarInt64", "VarUInt64", "CharLE", "HalfLE", "SingleLE", "DoubleLE" }) {
            Assert.Contains("writer.Write" + method + "(", generated);
            Assert.Contains("reader.Read" + method + "()", generated);
        }
        Assert.DoesNotContain("ValueSlotCodec", generated);
        AssertGeneratedBodiesRemainStaticallyBound(generated);
    }

    [Fact]
    public void ScalarDtoNewTagsSurvivePublisherHistoryAndExactAncestorReplacement() {
        using AncestryHistoryDirectory files = new();
        SchemaHistoryTool publisher = new();
        string initialSource = ScalarDtoSource.Replace("public sealed partial class Item", "public partial class Item") + """

            [DurableType("scalar.leaf", 1)]
            public sealed partial class Leaf : Item {
                [DurableField(1)] private uint _leaf = 128;
                public Leaf() : base(false) { }
            }
            public static class LeafHost {
                public static byte[] Capture() {
                    var value = Leaf.__DurableState.Capture(new Leaf());
                    var buffer = new ArrayBufferWriter<byte>();
                    var writer = new BareValueWriter(buffer);
                    Leaf.__DurableState.WriteBaseBody(ref writer, in value);
                    return buffer.WrittenSpan.ToArray();
                }
            }
            """;
        GeneratorTestRun initial = RunGenerator(initialSource);
        AssertSchemaOnlyCompiles(initial);
        byte[] saved = EmitAndLoad(initial.OutputCompilation).GetType("ScalarDtos.LeafHost")!
            .GetMethod("Capture")!.CreateDelegate<Func<byte[]>>()();
        Assert.Equal(Convert.FromHexString(ScalarLowGolden + "8001"), saved);
        publisher.Publish(files.WriteManifest(initial), files.History);
        Dictionary<string, string> original = files.ReadContents();
        GeneratorTestRun updated = RunGenerator(ScalarHistoryCurrentSource, files.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(updated);
        Assembly assembly = EmitAndLoad(updated.OutputCompilation);
        Assert.Null(assembly.GetType("ScalarDtos.Item"));
        Type leaf = assembly.GetType("ScalarDtos.Leaf")!;
        DurableSchema historical = ReadSchemaOnly(leaf, 1);
        Assert.Equal(1, historical.BaseSchema!.Version);
        Assert.Equal<int>([1, 5, 6, 7, 8, 2, 9, 3, 10, 11, 12, 13, 14],
            historical.BaseSchema.Fields.Select(field => (int)field.TypeTag));
        Assert.Equal(TypeTag.Int32, Assert.Single(ReadSchemaOnly(leaf, 2).BaseSchema!.Fields).TypeTag);
        Assert.Equal(saved, assembly.GetType("ScalarDtos.Host")!.GetMethod("RoundTripV1")!
            .CreateDelegate<Func<byte[], byte[]>>()(saved));
        publisher.Publish(files.WriteManifest(updated), files.History);
        publisher.Verify(files.WriteManifest(updated), files.History);
        foreach ((string name, string contents) in original) {
            Assert.Equal(contents, File.ReadAllText(Path.Combine(files.History, name)));
        }
        GeneratorTestRun reloaded = RunGenerator(ScalarHistoryCurrentSource, files.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(reloaded);
        Assert.Equal(GeneratedSource(updated, "DurableStates.g.cs"), GeneratedSource(reloaded, "DurableStates.g.cs"));
    }

    [Fact]
    public void ScalarDtoSameVersionRetypeFailsAgainstPublishedHistory() {
        using AncestryHistoryDirectory files = new();
        SchemaHistoryTool publisher = new();
        GeneratorTestRun initial = RunGenerator(ScalarDtoSource);
        AssertSchemaOnlyCompiles(initial);
        publisher.Publish(files.WriteManifest(initial), files.History);
        GeneratorTestRun changed = RunGenerator(
            ScalarDtoSource.Replace("private ushort _u16;", "private uint _u16;"), files.ReadAdditionalTexts());
        Assert.Contains(changed.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0015");
        Assert.DoesNotContain(changed.GeneratedSources, source => source.HintName == "DurableStates.g.cs");
        GeneratorTestRun changedCandidate = RunGenerator(
            ScalarDtoSource.Replace("private ushort _u16;", "private uint _u16;"));
        AssertSchemaOnlyCompiles(changedCandidate);
        string changedManifest = files.WriteManifest(changedCandidate);
        Assert.Throws<SchemaHistoryException>(() => publisher.Publish(changedManifest, files.History));
        Assert.Throws<SchemaHistoryException>(() => publisher.Verify(changedManifest, files.History));
    }

    [Theory]
    [InlineData(17)]
    [InlineData(999)]
    public void ScalarHistoryRejectsUnknownTagsInGeneratorAndPublisher(int tag) {
        var history = SchemaHistory("unknown.dgschema", "unknown", 1, (1, tag));
        GeneratorTestRun run = RunGenerator(ScalarDtoSource, history);
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0012");
        Assert.DoesNotContain(run.GeneratedSources, source => source.HintName == "DurableStates.g.cs");
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableFieldInfo(1, (TypeTag)tag));

        using AncestryHistoryDirectory files = new();
        GeneratorTestRun valid = RunGenerator(ScalarDtoSource);
        string manifest = files.WriteManifest(valid);
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("// field:1|1", "// field:1|" + tag));
        Assert.Throws<SchemaHistoryException>(() => new SchemaHistoryTool().Publish(manifest, files.History));
        Assert.False(Directory.Exists(files.History) && Directory.GetFiles(files.History).Length > 0);
    }

    [Theory]
    [InlineData("Custom")]
    [InlineData("System")]
    public void ScalarTypeRecognitionRejectsUserDefinedHalf(string namespaceName) {
        GeneratorTestRun run = RunGenerator($$"""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            namespace {{namespaceName}} { public struct Half { public int Value; } }
            [DurableType("fake-half", 1)]
            public sealed partial class Item : IDurableObject {
                [DurableField(1)] private {{namespaceName}}.Half _value;
            }
            """);
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0007");
        Assert.DoesNotContain(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "CS8785");
        Assert.DoesNotContain(run.GeneratedSources, source => source.HintName == "DurableStates.g.cs");
    }

    [Theory]
    [InlineData("Choice")]
    [InlineData("nint")]
    [InlineData("nuint")]
    [InlineData("System.Int128")]
    [InlineData("Composite")]
    public void ScalarTypeRecognitionKeepsUnsupportedValueKindsOutsideTheSlice(string fieldType) {
        GeneratorTestRun run = RunGenerator($$"""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            public enum Choice { First, Second }
            public struct Composite { public int Value; }
            [DurableType("unsupported-scalar", 1)]
            public sealed partial class Item : IDurableObject {
                [DurableField(1)] private {{fieldType}} _value;
            }
            """);
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0007");
        Assert.DoesNotContain(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "CS8785");
        Assert.DoesNotContain(run.GeneratedSources, source => source.HintName == "DurableStates.g.cs");
    }

    private const string ScalarDtoSource = """
        using System;
        using System.Buffers;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Serialization;
        namespace ScalarDtos;
        [DurableType("scalar.item", 1)]
        public sealed partial class Item : IDurableObject {
            [DurableField(1)] private bool _bool;
            [DurableField(2)] private byte _byte;
            [DurableField(3)] private sbyte _sbyte;
            [DurableField(4)] private short _i16;
            [DurableField(5)] private ushort _u16;
            [DurableField(6)] private int _i32;
            [DurableField(7)] private uint _u32;
            [DurableField(8)] private long _i64;
            [DurableField(9)] private ulong _u64;
            [DurableField(10)] private char _char;
            [DurableField(11)] private Half _half;
            [DurableField(12)] private float _single;
            [DurableField(13)] private double _double;
            public Item(bool high) {
                _bool = !high; _byte = high ? (byte)0 : byte.MaxValue; _sbyte = high ? sbyte.MaxValue : sbyte.MinValue;
                _i16 = high ? short.MaxValue : short.MinValue; _u16 = high ? (ushort)0 : ushort.MaxValue;
                _i32 = high ? int.MaxValue : int.MinValue; _u32 = high ? 0 : uint.MaxValue;
                _i64 = high ? long.MaxValue : long.MinValue; _u64 = high ? 0 : ulong.MaxValue;
                _char = high ? '\uDFFF' : '\uD800';
                _half = BitConverter.UInt16BitsToHalf(high ? (ushort)0x7E35 : (ushort)0x8000);
                _single = BitConverter.UInt32BitsToSingle(high ? 0x7FC12345u : 0x80000000u);
                _double = BitConverter.UInt64BitsToDouble(high ? 0x7FF8123456789ABCul : 0x8000000000000000ul);
            }
            public void Mutate() { _byte = 1; _half = (Half)1; _single = 1; _double = 1; }
        }
        public static class Host {
            public static byte[] RoundTrip(bool high) {
                var domain = new Item(high);
                var original = Item.__DurableState.Capture(domain);
                domain.Mutate();
                var buffer = new ArrayBufferWriter<byte>();
                var writer = new BareValueWriter(buffer);
                Item.__DurableState.WriteBaseBody(ref writer, in original);
                var reader = new BareValueReader(buffer.WrittenSpan);
                var value = Item.__DurableState.ReadBaseBodyV1(ref reader);
                reader.EnsureFullyConsumed();
                if (value.Segment0Field1 != original.Segment0Field1 || value.Segment0Field2 != original.Segment0Field2 ||
                    value.Segment0Field3 != original.Segment0Field3 || value.Segment0Field4 != original.Segment0Field4 ||
                    value.Segment0Field5 != original.Segment0Field5 || value.Segment0Field6 != original.Segment0Field6 ||
                    value.Segment0Field7 != original.Segment0Field7 || value.Segment0Field8 != original.Segment0Field8 ||
                    value.Segment0Field9 != original.Segment0Field9 || value.Segment0Field10 != original.Segment0Field10 ||
                    BitConverter.HalfToUInt16Bits(value.Segment0Field11) != BitConverter.HalfToUInt16Bits(original.Segment0Field11) ||
                    BitConverter.SingleToUInt32Bits(value.Segment0Field12) != BitConverter.SingleToUInt32Bits(original.Segment0Field12) ||
                    BitConverter.DoubleToUInt64Bits(value.Segment0Field13) != BitConverter.DoubleToUInt64Bits(original.Segment0Field13)) {
                    throw new Exception("Read changed a captured scalar or floating-point bit pattern.");
                }
                return buffer.WrittenSpan.ToArray();
            }
        }
        """;

    private const string ScalarHistoryCurrentSource = """
        using System;
        using System.Buffers;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Serialization;
        namespace ScalarDtos;
        [DurableType("scalar.item", 2)]
        public partial class CurrentBase : IDurableObject {
            [DurableField(1)] private int _replacement = 42;
        }
        [DurableType("scalar.leaf", 2)]
        public sealed partial class Leaf : CurrentBase {
            [DurableField(2)] private bool _replacement = true;
        }
        public static class Host {
            public static byte[] RoundTripV1(byte[] bytes) {
                var reader = new BareValueReader(bytes);
                var historical = Leaf.__DurableState.ReadBaseBodyV1(ref reader);
                reader.EnsureFullyConsumed();
                if (historical.Segment1Field1 != 128u || historical.Segment0Field10 != '\uD800' ||
                    BitConverter.HalfToUInt16Bits(historical.Segment0Field11) != 0x8000 ||
                    BitConverter.SingleToUInt32Bits(historical.Segment0Field12) != 0x80000000u ||
                    BitConverter.DoubleToUInt64Bits(historical.Segment0Field13) != 0x8000000000000000ul) {
                    throw new Exception("Historical scalar layout changed.");
                }
                var buffer = new ArrayBufferWriter<byte>();
                var writer = new BareValueWriter(buffer);
                Leaf.__DurableState.WriteBaseBody(ref writer, in historical);
                return buffer.WrittenSpan.ToArray();
            }
        }
        """;
}
