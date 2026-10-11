using Atelia.Binary;
using Atelia.DurableGraph.Schema;
using System.Reflection;
using Microsoft.CodeAnalysis;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    private const string GeneratedStateChain = """
        using System;
        using System.Buffers;
        using System.IO;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Serialization;
        namespace BinaryBodies;

        [DurableType("body.base", 1)]
        public abstract partial class Base : IDurableObject {
            [DurableField(9)] private int _number;
            [Transient] private int _cache = 73;
            [DurableField(2)] private bool _flag;
            protected Base(bool flag, int number) { _flag = flag; _number = number; }
            public bool Flag => _flag;
            public int Number => _number;
            public int Cache => _cache;
        }
        [DurableType("body.middle", 1)]
        public partial class Middle : Base {
            [DurableField(2)] private long _wide;
            protected Middle(bool flag, int number, long wide) : base(flag, number) { _wide = wide; }
            public long Wide => _wide;
        }
        [DurableType("body.empty", 1)]
        public partial class Empty : Middle {
            protected Empty(bool flag, int number, long wide) : base(flag, number, wide) { }
        }
        [DurableType("body.leaf", 1)]
        public sealed partial class Leaf : Empty {
            [DurableField(2)] private int _last;
            public Leaf(bool flag, int number, long wide, int last) : base(flag, number, wide) { _last = last; }
            public int Last => _last;
        }
        public static class Host {
            public static byte[] Write(bool flag, int number, long wide, int last) {
                var buffer = new ArrayBufferWriter<byte>();
                var writer = new BareValueWriter(buffer);
                var state = Leaf.__DurableState.Capture(new Leaf(flag, number, wide, last));
                Leaf.__DurableState.WriteBaseBody(ref writer, in state);
                return buffer.WrittenSpan.ToArray();
            }
            public static long[] Read(byte[] bytes, bool requireEnd) {
                var domain = new Leaf(false, 101, 102, 103);
                var value = Leaf.__DurableState.Capture(domain);
                var reader = new BareValueReader(bytes);
                int error = 0;
                try {
                    value = Leaf.__DurableState.ReadBaseBodyV1(ref reader);
                    if (requireEnd) { reader.EnsureFullyConsumed(); }
                }
                catch (EndOfStreamException) { error = 1; }
                catch (InvalidDataException) { error = 2; }
                return [error, reader.ConsumedCount, reader.RemainingCount,
                    value.Segment0Field2 ? 1 : 0, value.Segment0Field9, value.Segment1Field2, value.Segment3Field2, domain.Cache];
            }
            public static int NullCapture() {
                try { Leaf.__DurableState.Capture(null!); }
                catch (ArgumentNullException) { return 1; }
                return 0;
            }
            public static byte[] WriteBaseOfDerived() {
                var buffer = new ArrayBufferWriter<byte>();
                var writer = new BareValueWriter(buffer);
                var state = Base.__DurableState.Capture(new Leaf(true, -1, 999, 888));
                Base.__DurableState.WriteBaseBody(ref writer, in state);
                return buffer.WrittenSpan.ToArray();
            }
            public static byte[] FailedWrite() {
                var buffer = new FailAfterFirstAdvance();
                var writer = new BareValueWriter(buffer);
                var state = Leaf.__DurableState.Capture(new Leaf(true, int.MinValue, 999, 888));
                try { Leaf.__DurableState.WriteBaseBody(ref writer, in state); }
                catch (IOException) { return buffer.Buffer.WrittenSpan.ToArray(); }
                throw new Exception("Expected downstream failure.");
            }
            private sealed class FailAfterFirstAdvance : IBufferWriter<byte> {
                internal readonly ArrayBufferWriter<byte> Buffer = new();
                private int _advances;
                public void Advance(int count) {
                    if (_advances++ == 1) { throw new IOException("Injected write failure."); }
                    Buffer.Advance(count);
                }
                public Memory<byte> GetMemory(int sizeHint = 0) => Buffer.GetMemory(sizeHint);
                public Span<byte> GetSpan(int sizeHint = 0) => Buffer.GetSpan(sizeHint);
            }
        }
        """;

    [Fact]
    public void GeneratedBaseBodyExecutesPrivateBaseFirstFieldsInLocalIdOrderWithGoldenExtremes() {
        GeneratorTestRun run = RunGenerator(GeneratedStateChain);
        AssertSchemaOnlyCompiles(run);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        var write = GeneratedStateDelegate<Func<bool, int, long, int, byte[]>>(assembly, "Write");
        var read = GeneratedStateDelegate<Func<byte[], bool, long[]>>(assembly, "Read");
        byte[] minimum = [1, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 1,
            0xFE, 0xFF, 0xFF, 0xFF, 0x0F];
        byte[] maximum = [0, 0xFE, 0xFF, 0xFF, 0xFF, 0x0F,
            0xFE, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 1,
            0xFF, 0xFF, 0xFF, 0xFF, 0x0F];
        Assert.Equal(minimum, write(true, int.MinValue, long.MinValue, int.MaxValue));
        Assert.Equal(maximum, write(false, int.MaxValue, long.MaxValue, int.MinValue));
        Assert.Equal<long>([0, 21, 0, 1, int.MinValue, long.MinValue, int.MaxValue, 73], read(minimum, true));
        Assert.Equal<long>([0, 21, 0, 0, int.MaxValue, long.MaxValue, int.MinValue, 73], read(maximum, true));
        Assert.Equal<byte>([1, 1], GeneratedStateDelegate<Func<byte[]>>(assembly, "WriteBaseOfDerived")());

        string generated = GeneratedStateText(run);
        AssertGeneratedBodiesRemainStaticallyBound(generated);
        foreach (string forbidden in new[] { "ValueSlotCodec", "PrimitiveSlotCodecs", "DynamicInvoke", "delegate", "(object)", "System.Reflection" }) {
            Assert.DoesNotContain(forbidden, generated);
        }
        Assert.Contains("global::BinaryBodies.Base.__DurableState.Capture(value)", generated);
        Assert.Contains("global::BinaryBodies.Empty.__DurableState.Capture(value)", generated);
        Assert.Contains("writer.WriteBoolean(value.Segment0Field2)", generated);
        Assert.Contains("writer.WriteVarInt32(value.Segment0Field9)", generated);
        Assert.Contains("writer.WriteVarInt64(value.Segment1Field2)", generated);
        Assert.DoesNotContain("value._cache", generated);
        Assert.DoesNotContain("Serializer", generated);
    }

    [Fact]
    public void GeneratedStateNullCaptureAndBaseBodyReadFailuresKeepOriginalDtoWhileReaderCanAdvance() {
        GeneratorTestRun run = RunGenerator(GeneratedStateChain);
        AssertSchemaOnlyCompiles(run);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        Assert.Equal(1, GeneratedStateDelegate<Func<int>>(assembly, "NullCapture")());
        Assert.Equal<byte>([1], GeneratedStateDelegate<Func<byte[]>>(assembly, "FailedWrite")());
        var read = GeneratedStateDelegate<Func<byte[], bool, long[]>>(assembly, "Read");
        Assert.Equal<long>([2, 0, 1, 0, 101, 102, 103, 73], read([2], true));
        // A completed Boolean advances the reader; no partial DTO is assigned.
        Assert.Equal<long>([1, 1, 1, 0, 101, 102, 103, 73], read([1, 0x80], true));
        // Earlier declaration layers were read, but leaf failure still preserves the entire caller DTO.
        Assert.Equal<long>([1, 3, 1, 0, 101, 102, 103, 73], read([1, 1, 3, 0x80], true));
        Assert.Equal<long>([0, 4, 1, 1, -1, -2, -3, 73], read([1, 1, 3, 5, 99], false));
        Assert.Equal<long>([2, 4, 1, 1, -1, -2, -3, 73], read([1, 1, 3, 5, 99], true));
    }

    [Fact]
    public void GeneratedBaseBodyInvalidBooleanDoesNotExposeEarlierReadField() {
        GeneratorTestRun run = RunGenerator("""
            using System;
            using System.IO;
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using Atelia.DurableGraph.Serialization;
            namespace BinaryBodies;
            [DurableType("body.boolean", 1)]
            public sealed partial class Item : IDurableObject {
                [DurableField(2)] private bool _flag = true;
                [DurableField(1)] private int _number = 73;
                public bool Flag => _flag;
                public int Number => _number;
            }
            public static class Host {
                public static int[] Read() {
                    var reader = new BareValueReader(new byte[] { 1, 2 });
                    var domain = new Item();
                    var value = Item.__DurableState.Capture(domain);
                    try { value = Item.__DurableState.ReadBaseBodyV1(ref reader); }
                    catch (InvalidDataException) {
                        return [value.Segment0Field1, value.Segment0Field2 ? 1 : 0, reader.ConsumedCount, reader.RemainingCount, domain.Number];
                    }
                    throw new Exception("Expected invalid Boolean failure.");
                }
            }
            """);
        AssertSchemaOnlyCompiles(run);
        Assert.Equal<int>([73, 1, 1, 1, 73], GeneratedStateDelegate<Func<int[]>>(EmitAndLoad(run.OutputCompilation), "Read")());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void GeneratedStateRequiresSuccessfulOwnAndAncestorHistory(bool ancestor, bool missingHistory) {
        string source = ancestor ? $$"""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("body.base", {{(missingHistory ? 2 : 1)}})]
            public abstract partial class Base : IDurableObject { [DurableField(1)] private long _base; }
            [DurableType("body.leaf", 1)]
            public sealed partial class Leaf : Base { [DurableField(1)] private int _leaf; }
            """ : $$"""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("body.base", {{(missingHistory ? 2 : 1)}})]
            public sealed partial class Base : IDurableObject { [DurableField(1)] private long _base; }
            """;
        AdditionalText[] history = missingHistory ? [] : [SchemaHistory("old.dgschema", "body.base", 1, (1, 2))];
        GeneratorTestRun run = RunGenerator(source, history);
        Assert.Contains(run.GeneratorDiagnostics, IsError);
        Assert.DoesNotContain("static void WriteBaseBody(", GeneratedStateText(run));
    }

    [Fact]
    public void ReferenceCaptureGeneratedStateSupportsPrimitiveLeafWhenAncestorContainsString() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("body.base", 1)]
            public abstract partial class Base : IDurableObject { [DurableField(1)] private string _text = "base"; }
            [DurableType("body.leaf", 1)]
            public sealed partial class Leaf : Base { [DurableField(1)] private int _leaf; }
            """);
        AssertSchemaOnlyCompiles(run);
        string generated = GeneratedStateText(run);
        Assert.Contains("CaptureString(value._text)", generated);
        Type body = EmitAndLoad(run.OutputCompilation).GetType("Leaf")!
            .GetNestedType("__DurableState", BindingFlags.NonPublic)!;
        Assert.Equal(typeof(ObjectId), body.GetNestedType("V1", BindingFlags.NonPublic)!
            .GetField("Segment0Field1", BindingFlags.Instance | BindingFlags.NonPublic)!.FieldType);
        Assert.Equal(2, body.GetMethod("Capture", BindingFlags.Static | BindingFlags.NonPublic)!.GetParameters().Length);
    }

    [Theory]
    [InlineData(false, "DG0012")]
    [InlineData(true, "DG0013")]
    public void GeneratedStateRejectsUnrelatedHistoryParseOrDuplicateConflict(bool duplicateConflict, string diagnosticId) {
        AdditionalText[] history = duplicateConflict ? [
            SchemaHistory("first.dgschema", "unrelated", 1, (1, 2)),
            SchemaHistory("second.dgschema", "unrelated", 1, (1, 3)),
        ] : [
            new InMemoryAdditionalText("malformed.dgschema", "// durable-graph-schema-history:1\n// not-schema-history\n"),
        ];
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("body.valid", 1)]
            public sealed partial class Valid : IDurableObject { [DurableField(1)] private int _value; }
            """, history);
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == diagnosticId);
        Assert.DoesNotContain(run.GeneratedSources, source => source.HintName == "DurableStates.g.cs");
        Assert.DoesNotContain("static void WriteBaseBody(", GeneratedStateText(run));
    }

    [Fact]
    public void GeneratedStateProvidesDistinctTypedBodiesForCurrentAndHistoricalVersions() {
        GeneratorTestRun run = RunGenerator("""
            using System;
            using System.Buffers;
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using Atelia.DurableGraph.Serialization;
            namespace BinaryBodies;
            [DurableType("body.versioned", 2)]
            public sealed partial class Versioned : IDurableObject {
                [DurableField(1)] private long _value = long.MinValue;
            }
            public static class Host {
                public static byte[] Write() {
                    var buffer = new ArrayBufferWriter<byte>();
                    var writer = new BareValueWriter(buffer);
                    var state = Versioned.__DurableState.Capture(new Versioned());
                    Versioned.__DurableState.WriteBaseBody(ref writer, in state);
                    return buffer.WrittenSpan.ToArray();
                }
            }
            """, SchemaHistory("v1.dgschema", "body.versioned", 1, (1, 2)));
        AssertSchemaOnlyCompiles(run);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        Type type = assembly.GetType("BinaryBodies.Versioned")!;
        Assert.Equal(TypeTag.Int32, Assert.Single(ReadSchemaOnly(type, 1).Fields).TypeTag);
        Assert.Equal(TypeTag.Int64, Assert.Single(ReadSchemaOnly(type, 2).Fields).TypeTag);
        Assert.Equal<byte>([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 1],
            GeneratedStateDelegate<Func<byte[]>>(assembly, "Write")());
        Type body = type.GetNestedType("__DurableState", BindingFlags.NonPublic)!;
        Assert.Equal(new[] { "AddRoot", "Allocate", "ApplyDeltaBodyV1", "ApplyDeltaBodyV2", "Capture", "Hydrate", "Normalize", "PrepareBaseBody", "PrepareBaseBody", "PrepareDeltaBody", "PrepareDeltaBody", "ReadBaseBodyV1", "ReadBaseBodyV2", "RegisterModel", "RegisterReaders", "StateEquals", "StateEquals", "ValidateStringReferences", "ValidateStringReferences", "VisitReferences", "VisitReferences", "WriteBaseBody", "WriteBaseBody" }, body.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Select(method => method.Name).OrderBy(name => name).ToArray());
        AssertGeneratedBodiesRemainStaticallyBound(GeneratedStateText(run));
        Assert.DoesNotContain("__DurableSnapshot", GeneratedStateText(run));
    }

    private static T GeneratedStateDelegate<T>(Assembly assembly, string method) where T : Delegate =>
        assembly.GetType("BinaryBodies.Host")!.GetMethod(method)!.CreateDelegate<T>();

    private static string GeneratedStateText(GeneratorTestRun run) =>
        string.Join("\n", run.GeneratedSources.Select(source => source.SourceText.ToString()));
}
