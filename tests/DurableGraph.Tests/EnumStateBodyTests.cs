using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using System.Reflection;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    public static IEnumerable<object[]> EnumIntegerGoldenCases() {
        yield return ["sbyte", new object[] { (sbyte)0, (sbyte)1, sbyte.MinValue, sbyte.MaxValue }, new[] { "00", "01", "80", "7F" }];
        yield return ["byte", new object[] { (byte)0, (byte)1, byte.MaxValue }, new[] { "00", "01", "FF" }];
        yield return ["short", new object[] { (short)0, (short)1, short.MinValue, short.MaxValue }, new[] { "00", "02", "FFFF03", "FEFF03" }];
        yield return ["ushort", new object[] { (ushort)0, (ushort)1, ushort.MaxValue }, new[] { "00", "01", "FFFF03" }];
        yield return ["int", new object[] { 0, 1, int.MinValue, int.MaxValue }, new[] { "00", "02", "FFFFFFFF0F", "FEFFFFFF0F" }];
        yield return ["uint", new object[] { 0u, 1u, uint.MaxValue }, new[] { "00", "01", "FFFFFFFF0F" }];
        yield return ["long", new object[] { 0L, 1L, long.MinValue, long.MaxValue }, new[] { "00", "02", "FFFFFFFFFFFFFFFFFF01", "FEFFFFFFFFFFFFFFFF01" }];
        yield return ["ulong", new object[] { 0UL, 1UL, ulong.MaxValue }, new[] { "00", "01", "FFFFFFFFFFFFFFFFFF01" }];
    }

    [Theory]
    [MemberData(nameof(EnumIntegerGoldenCases))]
    public void EnumGeneratedBodiesPreserveAllIntegerBitsAndReuseInlineDelta(string underlying, object[] values, string[] golden) {
        GeneratorTestRun run = RunGenerator($$"""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [System.Flags, DurableType("enum.body", 1)]
            public enum Bits : {{underlying}} { Zero = 0, One = 1, Alias = 1 }
            """);
        AssertSchemaOnlyCompiles(run);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        StateModelRegistry registry = new();
        assembly.GetType("Atelia.DurableGraph.Generated.DurableDefinitions")!.GetMethod("Register")!.Invoke(null, [registry]);
        Type domain = assembly.GetType("Bits")!;
        StateValueBinding binding = registry.CreateEnvironment().ResolveCurrentValue(domain);
        Assert.Equal(TypeExpr.Named("enum.body"), binding.Slot.InlineSchema!.Type);
        Assert.NotEqual(domain, binding.StateType);
        Assert.Single(binding.Slot.InlineSchema.Fields);
        typeof(DurableSchemaGeneratorTests).GetMethod(nameof(CheckEnumBodies), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(domain, binding.StateType, binding.StateOpsType, binding.ProjectionType!)
            .Invoke(null, [binding.Slot, values, golden]);
    }

    private static void CheckEnumBodies<TEnum, TState, TOps, TProjection>(DurableFieldInfo slot, object[] integers, string[] golden)
        where TEnum : struct, Enum where TState : unmanaged where TOps : IStateOps<TState>
        where TProjection : IValueProjection<TEnum, TState> {
        TState[] states = new TState[integers.Length];
        for (int i = 0; i < integers.Length; i++) {
            TEnum value = (TEnum)Enum.ToObject(typeof(TEnum), integers[i]);
            states[i] = TProjection.Capture(in value, null!, slot);
            byte[] bytes = Convert.FromHexString(golden[i]);
            Assert.Equal(bytes, EncodeEnumState<TState, TOps>(states[i], slot));
            TState decoded = DecodeEnumState<TState, TOps>(bytes, slot);
            TEnum hydrated = default;
            TProjection.Hydrate(ref hydrated, in decoded, null!, slot);
            Assert.Equal(value, hydrated);
            Assert.True(TOps.StateEquals(states[i], decoded, slot));
            // Enum has no references, even when every bit is set.
            TOps.VisitReferences(in decoded, null!, slot);
            BareValueReader nested = new(bytes.Concat(new byte[] { 91 }).ToArray());
            _ = TOps.ReadBase(ref nested, slot);
            Assert.Equal(91, nested.ReadByte());
            nested.EnsureFullyConsumed();
            for (int length = 0; length < bytes.Length; length++) {
                byte[] truncated = bytes[..length];
                Assert.Throws<EndOfStreamException>(() => DecodeEnumState<TState, TOps>(truncated, slot));
            }
        }
        for (int p = 0; p < states.Length; p++) {
            for (int c = 0; c < states.Length; c++) {
                TState prior = states[p], current = states[c];
                PreparedDeltaBody delta = TOps.PrepareDelta(in prior, in current, slot);
                Assert.Equal(p != c, delta.HasChanges);
                Assert.Equal(p == c, TOps.StateEquals(in prior, in current, slot));
                byte[] expected = p == c ? [0] : new byte[] { 1 }.Concat(Convert.FromHexString(golden[c])).ToArray();
                Assert.Equal(expected, delta.Body.ToArray());
                if (p == c) {
                    // The enclosing owner omits unchanged children. A nested
                    // patch claiming to change an enum cannot contain a no-op.
                    Assert.Throws<InvalidDataException>(() => ApplyEnumDelta<TState, TOps>(expected, prior, slot));
                } else {
                    TState applied = ApplyEnumDelta<TState, TOps>(expected, prior, slot);
                    Assert.True(TOps.StateEquals(in current, in applied, slot));
                }
                Assert.Equal(Convert.FromHexString(golden[p]), EncodeEnumState<TState, TOps>(prior, slot));
            }
        }
        TState zero = states[0];
        Assert.Throws<EndOfStreamException>(() => ApplyEnumDelta<TState, TOps>([], zero, slot));
        Assert.Throws<EndOfStreamException>(() => ApplyEnumDelta<TState, TOps>([1], zero, slot));
        Assert.Throws<InvalidDataException>(() => ApplyEnumDelta<TState, TOps>([2], zero, slot));
        Assert.Throws<InvalidDataException>(() => ApplyEnumDelta<TState, TOps>([1, 0], zero, slot));
        Assert.Throws<InvalidDataException>(() => ApplyEnumDelta<TState, TOps>([0, 0], zero, slot));
        Assert.Throws<InvalidDataException>(() => DecodeEnumState<TState, TOps>([0, 0], slot));
    }

    private static byte[] EncodeEnumState<TState, TOps>(TState state, DurableFieldInfo slot)
        where TState : unmanaged where TOps : IStateOps<TState> {
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        TOps.WriteBase(ref writer, in state, slot);
        return buffer.WrittenSpan.ToArray();
    }

    private static TState DecodeEnumState<TState, TOps>(byte[] bytes, DurableFieldInfo slot)
        where TState : unmanaged where TOps : IStateOps<TState> {
        BareValueReader reader = new(bytes);
        TState result = TOps.ReadBase(ref reader, slot);
        reader.EnsureFullyConsumed();
        return result;
    }

    private static TState ApplyEnumDelta<TState, TOps>(byte[] bytes, TState prior, DurableFieldInfo slot)
        where TState : unmanaged where TOps : IStateOps<TState> {
        BareValueReader reader = new(bytes);
        TState result = TOps.ApplyDelta(ref reader, in prior, slot);
        reader.EnsureFullyConsumed();
        return result;
    }
}
