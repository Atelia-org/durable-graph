using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using System.Runtime.CompilerServices;
using Atelia.DurableGraph.Serialization;
using Microsoft.CodeAnalysis;

namespace Atelia.DurableGraph.Tests;

public sealed class ObjectIdTests {
    [Fact]
    public void IdentityIsUnmanagedFourBytesWithNullAndUnsignedOrdering() {
        Assert.Equal(4, UnmanagedSize<ObjectId>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<ObjectId>());
        Assert.True(default(ObjectId).IsNull);
        Assert.False(new ObjectId(uint.MaxValue).IsNull);
        Assert.Equal(new ObjectId(7), new ObjectId(7));
        Assert.Equal(new ObjectId[] { default, new(1), new(uint.MaxValue) },
            new ObjectId[] { new(uint.MaxValue), default, new(1) }.OrderBy(id => id));
    }

    [Fact]
    public void NumericAndReferenceStateTypesAreDistinctWhileWireBytesRemainUInt32() {
        DurableFieldInfo numeric = new(1, TypeTag.UInt32);
        DurableFieldInfo text = new(1, TypeTag.String);
        DurableFieldInfo reference = DurableFieldInfo.Reference(1, TypeExpr.Named("Node"));
        Assert.True(BuiltinStateValues.TryBindStored(numeric, out StateValueBinding numericBinding));
        Assert.True(BuiltinStateValues.TryBindStored(text, out StateValueBinding stringBinding));
        Assert.True(BuiltinStateValues.TryBindStored(reference, out StateValueBinding referenceBinding));
        Assert.Equal(typeof(uint), numericBinding.StateType);
        Assert.Equal(typeof(ObjectId), stringBinding.StateType);
        Assert.Equal(typeof(ObjectId), referenceBinding.StateType);
        foreach (uint number in new uint[] { 0, 1, 127, 128, uint.MaxValue }) {
            ObjectId id = new(number);
            byte[] expected = Encode<uint, UInt32StateOps>(number, numeric);
            Assert.Equal(expected, Encode<ObjectId, StringIdStateOps>(id, text));
            Assert.Equal(expected, Encode<ObjectId, DurableIdStateOps>(id, reference));
            BareValueReader reader = new(expected);
            Assert.Equal(id, StringIdStateOps.ReadBase(ref reader, text));
            reader.EnsureFullyConsumed();
        }
    }

    private static int UnmanagedSize<T>() where T : unmanaged => Unsafe.SizeOf<T>();
    private static byte[] Encode<T, TOps>(T value, DurableFieldInfo slot)
        where T : unmanaged where TOps : IStateOps<T> {
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        TOps.WriteBase(ref writer, in value, slot);
        return buffer.WrittenSpan.ToArray();
    }
}

public sealed partial class DurableSchemaGeneratorTests {
    [Theory]
    [InlineData("public static ObjectId Convert(uint value) => value;")]
    [InlineData("public static uint Convert(ObjectId value) => value;")]
    public void ObjectIdRequiresExplicitConstructionAndUnwrapping(string method) {
        GeneratorTestRun run = RunGenerator("using Atelia.DurableGraph; public static class Host { " + method + " }");
        Assert.Contains(run.OutputCompilation.GetDiagnostics(), diagnostic =>
            diagnostic.Id == "CS0029" && diagnostic.Severity == DiagnosticSeverity.Error);
    }
}
