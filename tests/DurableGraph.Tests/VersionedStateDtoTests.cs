using Atelia.Binary;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void StateDtoCaptureOwnsScalarCopyAndGeneratedApiOnlyWritesImmutableDtos() {
        GeneratorTestRun run = RunGenerator("""
            using System;
            using System.Buffers;
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using Atelia.DurableGraph.Serialization;
            namespace BinaryBodies;
            [DurableType("state.copy", 1)]
            public sealed partial class Item : IDurableObject {
                [DurableField(8)] private long _wide = -2;
                [DurableField(1)] private int _number = -1;
                [DurableField(3)] private bool _flag = true;
                [Transient] private object _cache = new();
                public void Mutate() { _wide = 900; _number = 100; _flag = false; _cache = new(); }
            }
            public static class Host {
                public static byte[] CaptureThenMutate() {
                    var domain = new Item();
                    var captured = Item.__DurableState.Capture(domain);
                    domain.Mutate();
                    var buffer = new ArrayBufferWriter<byte>();
                    var writer = new BareValueWriter(buffer);
                    Item.__DurableState.WriteBaseBody(ref writer, in captured);
                    var current = Item.__DurableState.Capture(domain);
                    Item.__DurableState.WriteBaseBody(ref writer, in current);
                    return buffer.WrittenSpan.ToArray();
                }
            }
            """);
        AssertSchemaOnlyCompiles(run);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        Assert.Equal<byte>([1, 1, 3, 0xC8, 1, 0, 0x88, 0x0E],
            GeneratedStateDelegate<Func<byte[]>>(assembly, "CaptureThenMutate")());
        Type domain = assembly.GetType("BinaryBodies.Item")!;
        Type body = domain.GetNestedType("__DurableState", BindingFlags.NonPublic)!;
        Type dto = body.GetNestedType("V1", BindingFlags.NonPublic)!;
        Assert.True(dto.IsValueType);
        Assert.NotNull(dto.GetCustomAttribute<IsReadOnlyAttribute>());
        FieldInfo[] fields = dto.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.Equal(new[] { "Segment0Field1", "Segment0Field3", "Segment0Field8" }, fields.Select(field => field.Name).Order().ToArray());
        Assert.All(fields, field => Assert.True(field.IsInitOnly));
        Assert.All(fields, field => Assert.True(field.FieldType.IsValueType));
        Assert.Same(ReadSchemaOnly(domain, 1), dto.GetProperty("Schema", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null));
        MethodInfo write = body.GetMethod("WriteBaseBody", BindingFlags.Static | BindingFlags.NonPublic)!;
        ParameterInfo stateParameter = write.GetParameters()[1];
        Assert.Equal(dto.MakeByRefType(), stateParameter.ParameterType);
        Assert.True(stateParameter.IsIn);
        Assert.Null(body.GetMethod("Read", BindingFlags.Static | BindingFlags.NonPublic));
        Assert.Equal(dto, body.GetMethod("ReadBaseBodyV1", BindingFlags.Static | BindingFlags.NonPublic)!.ReturnType);
        MethodInfo prepare = body.GetMethod("PrepareDeltaBody", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(typeof(Atelia.DurableGraph.Serialization.PreparedDeltaBody), prepare.ReturnType);
        Assert.All(prepare.GetParameters(), parameter => {
            Assert.Equal(dto.MakeByRefType(), parameter.ParameterType);
            Assert.True(parameter.IsIn);
        });
        MethodInfo prepareBase = body.GetMethod("PrepareBaseBody", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(typeof(Atelia.DurableGraph.Serialization.PreparedBaseBody), prepareBase.ReturnType);
        ParameterInfo baseParameter = Assert.Single(prepareBase.GetParameters());
        Assert.Equal(dto.MakeByRefType(), baseParameter.ParameterType);
        Assert.True(baseParameter.IsIn);
        Assert.Equal(new[] { "AddRoot", "Allocate", "ApplyDeltaBodyV1", "Capture", "Hydrate", "Normalize", "PrepareBaseBody", "PrepareDeltaBody", "ReadBaseBodyV1", "RegisterModel", "RegisterReaders", "StateEquals", "ValidateStringReferences", "VisitReferences", "WriteBaseBody" },
            body.GetMethods(BindingFlags.Static | BindingFlags.NonPublic).Select(method => method.Name).Order().ToArray());
    }

    [Fact]
    public void StateDtoHistoryKeepsDeletedAndRetypedFieldsWithExactSchemaAndGoldenBytes() {
        GeneratorTestRun run = RunGenerator("""
            using System;
            using System.Buffers;
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using Atelia.DurableGraph.Serialization;
            namespace BinaryBodies;
            [DurableType("state.history", 3)]
            public sealed partial class Item : IDurableObject {
                [DurableField(7)] private bool _current = true;
            }
            public static class Host {
                public static byte[] RoundTripVersions() {
                    var reader = new BareValueReader(new byte[] { 1, 1, 3, 5, 1 });
                    var v1 = Item.__DurableState.ReadBaseBodyV1(ref reader);
                    var v2 = Item.__DurableState.ReadBaseBodyV2(ref reader);
                    var v3 = Item.__DurableState.ReadBaseBodyV3(ref reader);
                    reader.EnsureFullyConsumed();
                    if (v1.Segment0Field1 != -1 || !v1.Segment0Field9 ||
                        v2.Segment0Field1 != -2L || v2.Segment0Field4 != -3 || !v3.Segment0Field7) {
                        throw new Exception("Historical DTO fields do not match their exact layout.");
                    }
                    var buffer = new ArrayBufferWriter<byte>();
                    var writer = new BareValueWriter(buffer);
                    Item.__DurableState.WriteBaseBody(ref writer, in v1);
                    Item.__DurableState.WriteBaseBody(ref writer, in v2);
                    Item.__DurableState.WriteBaseBody(ref writer, in v3);
                    return buffer.WrittenSpan.ToArray();
                }
            }
            """,
            SchemaHistory("v1.dgschema", "state.history", 1, (1, 2), (9, 1)),
            SchemaHistory("v2.dgschema", "state.history", 2, (1, 3), (4, 2)));
        AssertSchemaOnlyCompiles(run);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        Assert.Equal<byte>([1, 1, 3, 5, 1], GeneratedStateDelegate<Func<byte[]>>(assembly, "RoundTripVersions")());
        Type domain = assembly.GetType("BinaryBodies.Item")!;
        Type body = domain.GetNestedType("__DurableState", BindingFlags.NonPublic)!;
        for (int version = 1; version <= 3; version++) {
            Type dto = body.GetNestedType("V" + version, BindingFlags.NonPublic)!;
            Assert.Same(ReadSchemaOnly(domain, version), dto.GetProperty("Schema", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null));
        }

        Type v1 = body.GetNestedType("V1", BindingFlags.NonPublic)!;
        Type v2 = body.GetNestedType("V2", BindingFlags.NonPublic)!;
        Type v3 = body.GetNestedType("V3", BindingFlags.NonPublic)!;
        Assert.Equal(typeof(int), v1.GetField("Segment0Field1", BindingFlags.Instance | BindingFlags.NonPublic)!.FieldType);
        Assert.Equal(typeof(long), v2.GetField("Segment0Field1", BindingFlags.Instance | BindingFlags.NonPublic)!.FieldType);
        Assert.Null(v2.GetField("Segment0Field9", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Null(v3.GetField("Segment0Field1", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Equal(v3, body.GetMethod("Capture", BindingFlags.Static | BindingFlags.NonPublic)!.ReturnType);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReferenceCaptureStateDtoSupportsHistoricalStringIncludingRemovedAncestor(bool inAncestor) {
        AdditionalText historical = SchemaHistory("old.dgschema", inAncestor ? "state.old-base" : "state.item", 1, (1, 4));
        AdditionalText[] history = inAncestor ? [
            historical,
            StateDtoHistory("state.item", 1, "state.old-base", 1),
        ] : [historical];
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("state.item", 2)]
            public sealed partial class Item : IDurableObject { [DurableField(1)] private int _current; }
            """, history);
        AssertSchemaOnlyCompiles(run);
        Type body = EmitAndLoad(run.OutputCompilation).GetType("Item")!
            .GetNestedType("__DurableState", BindingFlags.NonPublic)!;
        Assert.Equal(typeof(ObjectId), body.GetNestedType("V1", BindingFlags.NonPublic)!
            .GetField("Segment0Field1", BindingFlags.Instance | BindingFlags.NonPublic)!.FieldType);
        Assert.Single(body.GetMethod("Capture", BindingFlags.Static | BindingFlags.NonPublic)!.GetParameters());
    }

    [Fact]
    public void ReferenceCaptureStateDtoScalarLeafKeepsContextFreeCaptureWithHistoricalBaseString() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("state.base", 2)]
            public abstract partial class Base : IDurableObject { [DurableField(1)] private int _current; }
            [DurableType("state.leaf", 1)]
            public sealed partial class Leaf : Base { [DurableField(2)] private bool _flag; }
            """, SchemaHistory("base-v1.dgschema", "state.base", 1, (1, 4)));
        AssertSchemaOnlyCompiles(run);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        Type baseBody = assembly.GetType("Base")!.GetNestedType("__DurableState", BindingFlags.NonPublic)!;
        Assert.Equal(typeof(ObjectId), baseBody.GetNestedType("V1", BindingFlags.NonPublic)!
            .GetField("Segment0Field1", BindingFlags.Instance | BindingFlags.NonPublic)!.FieldType);
        Type leafBody = assembly.GetType("Leaf")!.GetNestedType("__DurableState", BindingFlags.NonPublic)!;
        Assert.Single(leafBody.GetMethod("Capture", BindingFlags.Static | BindingFlags.NonPublic)!.GetParameters());
    }

    [Fact]
    public void StateDtoEmptyLayoutReturnsCompletedValueWithoutConsumingBytes() {
        GeneratorTestRun run = RunGenerator("""
            using System;
            using System.Buffers;
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using Atelia.DurableGraph.Serialization;
            namespace BinaryBodies;
            [DurableType("state.empty", 1)]
            public sealed partial class Item : IDurableObject { }
            public static class Host {
                public static int[] RoundTrip() {
                    var reader = new BareValueReader(new byte[] { 99 });
                    var state = Item.__DurableState.ReadBaseBodyV1(ref reader);
                    var buffer = new ArrayBufferWriter<byte>();
                    var writer = new BareValueWriter(buffer);
                    Item.__DurableState.WriteBaseBody(ref writer, in state);
                    var captured = Item.__DurableState.Capture(new Item());
                    Item.__DurableState.WriteBaseBody(ref writer, in captured);
                    return [reader.ConsumedCount, reader.RemainingCount, buffer.WrittenCount];
                }
            }
            """);
        AssertSchemaOnlyCompiles(run);
        Assert.Equal<int>([0, 1, 0], GeneratedStateDelegate<Func<int[]>>(EmitAndLoad(run.OutputCompilation), "RoundTrip")());
    }

    private static AdditionalText StateDtoHistory(string schemaId, int version, string baseId, int baseVersion) {
        string path = $"{schemaId}-{version}.dgschema";
        string content = SchemaHistory(path, schemaId, version, (1, 2)).GetText()!.ToString();
        string versionLine = $"// version:{version}\n";
        content = content.Replace(versionLine, versionLine +
            $"// base:{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(baseId))}|{baseVersion}\n");
        return new InMemoryAdditionalText(path, content);
    }
}
