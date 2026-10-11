using Atelia.Binary;
using System.Reflection;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void FusedDeltaAllScalarSlotsHaveExactReconstructionAndReusablePreparedContent() {
        string[] types = ["bool", "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "char", "System.Half", "float", "double", "string?"];
        GeneratorTestRun run = RunGenerator(FusedDeltaSource(types));
        AssertSchemaOnlyCompiles(run);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("FusedDelta.Host")!;
        var prepare = host.GetMethod("Prepare1")!.CreateDelegate<Func<byte[], byte[], PreparedDeltaBody>>();
        var apply = host.GetMethod("Apply1")!.CreateDelegate<Func<byte[], byte[], byte[]>>();

        // Independent canonical scalar fragments, including surrogate char, signed bounds,
        // NaN payloads and a multi-byte string ObjectId. No generated encoder builds these inputs.
        string[] low = ["00", "00", "00", "00", "00", "00", "00", "00", "00", "0000", "0000", "00000000", "0000000000000000", "00"];
        string[] high = ["01", "FF", "80", "FFFF03", "FFFF03", "FFFFFFFF0F", "FFFFFFFF0F", "FFFFFFFFFFFFFFFFFF01", "FFFFFFFFFFFFFFFFFF01", "00D8", "357E", "4523C17F", "BC9A78563412F87F", "8001"];
        byte[] prior = Convert.FromHexString(string.Concat(low));
        byte[] priorCopy = prior.ToArray();
        PreparedDeltaBody unchanged = prepare(prior, prior);
        Assert.False(unchanged.HasChanges);
        Assert.Equal<byte>([0, 0], unchanged.Body.ToArray());
        Assert.Equal(prior, apply(prior, unchanged.Body.ToArray()));

        for (int slot = 0; slot < types.Length; slot++) {
            string[] changed = low.ToArray();
            changed[slot] = high[slot];
            byte[] current = Convert.FromHexString(string.Concat(changed));
            PreparedDeltaBody delta = prepare(prior, current);
            byte[] mask = [0, 0];
            mask[slot / 8] = (byte)(1 << (slot % 8));
            byte[] expected = mask.Concat(Convert.FromHexString(high[slot])).ToArray();
            Assert.True(delta.HasChanges);
            Assert.Equal(expected, delta.Body.ToArray());
            Assert.Equal(current, apply(prior, delta.Body.ToArray()));
            Assert.True(delta.Body.Length < current.Length);
            Assert.Equal(expected, prepare(prior, current).Body.ToArray());

            // The actual PreparedDeltaBody crosses the generated assembly boundary. Its bytes remain
            // usable after later Prepare calls and mutation of inputs and separately obtained copies.
            byte[] savedCurrent = current.ToArray();
            Array.Clear(current);
            byte[] externalCopy = delta.Body.ToArray();
            Array.Clear(externalCopy);
            _ = prepare(prior, prior);
            Assert.Equal(expected, delta.Body.ToArray());
            Assert.Equal(savedCurrent, apply(prior, delta.Body.ToArray()));
            Assert.Equal(savedCurrent, apply(prior, delta.Body.ToArray()));
        }

        byte[] all = Convert.FromHexString(string.Concat(high));
        PreparedDeltaBody allDelta = prepare(prior, all);
        Assert.True(allDelta.HasChanges);
        Assert.Equal(new byte[] { 0xFF, 0x3F }.Concat(all).ToArray(), allDelta.Body.ToArray());
        Assert.True(allDelta.Body.Length > all.Length);
        Assert.Equal(all, apply(prior, allDelta.Body.ToArray()));
        Assert.False(prepare(all, all).HasChanges); // Equal NaNs must not become updates.
        Assert.Equal(priorCopy, prior);

        string generated = GeneratedSource(run, "DurableStates.g.cs");
        AssertGeneratedBodiesRemainStaticallyBound(generated);
        AssertPrepareDeltaDoesNotPrecompare(generated);
        foreach (string forbidden in new[] { "WriteDelta(", "EstimateDelta(", "ValueSlotCodec", "PrimitiveSlotCodecs", "System.Reflection" }) {
            Assert.DoesNotContain(forbidden, generated);
        }
    }

    [Theory]
    [InlineData(0, "", "", "", false)]
    [InlineData(1, "00", "01", "0101", true)]
    [InlineData(8, "0000000000000000", "0100000000000001", "810101", true)]
    [InlineData(9, "000000000000000000", "000000000000000101", "80010101", true)]
    public void FusedDeltaBitmapBoundariesHaveIndependentGoldens(int slots, string priorHex, string currentHex, string deltaHex, bool changed) {
        GeneratorTestRun run = RunGenerator(FusedDeltaSource(Enumerable.Repeat("bool", slots).ToArray()));
        AssertSchemaOnlyCompiles(run);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("FusedDelta.Host")!;
        var prepare = host.GetMethod("Prepare1")!.CreateDelegate<Func<byte[], byte[], PreparedDeltaBody>>();
        var apply = host.GetMethod("Apply1")!.CreateDelegate<Func<byte[], byte[], byte[]>>();
        byte[] prior = Convert.FromHexString(priorHex);
        byte[] current = Convert.FromHexString(currentHex);
        PreparedDeltaBody delta = prepare(prior, current);
        Assert.Equal(changed, delta.HasChanges);
        Assert.Equal(Convert.FromHexString(deltaHex), delta.Body.ToArray());
        Assert.Equal(current, apply(prior, delta.Body.ToArray()));
        PreparedDeltaBody equal = prepare(current, current);
        Assert.False(equal.HasChanges);
        Assert.Equal(new byte[(slots + 7) / 8], equal.Body.ToArray());
        Assert.Equal(current, apply(current, equal.Body.ToArray()));
    }

    [Fact]
    public void FusedDeltaFloatingComparisonPreservesSignedZeroAndEveryNaNPayload() {
        GeneratorTestRun run = RunGenerator(FusedDeltaSource(["System.Half", "float", "double"]));
        AssertSchemaOnlyCompiles(run);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("FusedDelta.Host")!;
        var prepare = host.GetMethod("Prepare1")!.CreateDelegate<Func<byte[], byte[], PreparedDeltaBody>>();
        var apply = host.GetMethod("Apply1")!.CreateDelegate<Func<byte[], byte[], byte[]>>();
        byte[][] states = new[] {
            "0000000000000000000000000000", // +0, +0, +0
            "0080000000800000000000000080", // -0, -0, -0
            "017E0100C07F010000000000F87F", // NaNs, payload 1
            "027E0200C07F020000000000F87F", // NaNs, payload 2
        }.Select(Convert.FromHexString).ToArray();
        foreach (byte[] prior in states) {
            Assert.Throws<InvalidDataException>(() => apply(prior, new byte[] { 7 }.Concat(prior).ToArray()));
            foreach (byte[] current in states) {
                PreparedDeltaBody delta = prepare(prior, current);
                Assert.Equal(!prior.SequenceEqual(current), delta.HasChanges);
                Assert.Equal(current, apply(prior, delta.Body.ToArray()));
                Assert.Equal(prior.SequenceEqual(current) ? (byte)0 : (byte)7, delta.Body[0]);
            }
        }
    }

    [Fact]
    public void FusedDeltaRejectsMalformedOrRedundantChangesAndLeavesPriorUsableAfterLateFailure() {
        GeneratorTestRun run = RunGenerator(FusedDeltaSource(["bool", "uint", "int"]));
        AssertSchemaOnlyCompiles(run);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("FusedDelta.Host")!;
        var prepare = host.GetMethod("Prepare1")!.CreateDelegate<Func<byte[], byte[], PreparedDeltaBody>>();
        var apply = host.GetMethod("Apply1")!.CreateDelegate<Func<byte[], byte[], byte[]>>();
        byte[] prior = [0, 0, 0];
        byte[] current = [1, 0x80, 1, 2];
        byte[] valid = [7, 1, 0x80, 1, 2];
        Assert.Equal(valid, prepare(prior, current).Body.ToArray());
        for (int length = 0; length < valid.Length; length++) {
            Assert.Throws<EndOfStreamException>(() => apply(prior, valid[..length]));
        }
        foreach (string invalid in new[] {
            "08", // Padding bit.
            "0100", // Redundant bool update.
            "0200", // Redundant uint update.
            "0400", // Redundant int update.
            "0102", // Invalid bool.
            "028000", // Redundant semantic uint update in a redundant codeword.
            "028080808010", // UInt32 overflow.
            "0701800100", // Late redundant value after two valid updates.
            "0000", // No-op plus trailing data.
            "070180010200", // Valid changed body plus trailing data.
        }) {
            Assert.Throws<InvalidDataException>(() => apply(prior, Convert.FromHexString(invalid)));
        }
        Assert.Equal<byte>([0, 0, 0], prior);
        Assert.Equal(current, apply(prior, valid));
        Assert.Equal(new byte[] { 0, 0, 2 }, apply(prior, Convert.FromHexString("048200")));
        Assert.Equal(current, apply(prior, Convert.FromHexString("070180018200")));
        Assert.True(host.GetMethod("LateFailureKeepsDto")!.CreateDelegate<Func<bool>>()());
    }

    private static string FusedDeltaSource(string[] types) => FusedDeltaPreamble + """
        [DurableType("fused.item", 1)]
        public sealed partial class Item : IDurableObject {
        """ + string.Join("\n", types.Select((type, index) => $"[DurableField({index * 7 + 1})] private {type} _field{index};")) + "\n}\n" +
        "public static class Host {\n" + FusedDeltaHostMethods("Item", 1) +
        (types.SequenceEqual(new[] { "bool", "uint", "int" }) ? """
            public static bool LateFailureKeepsDto() {
                var reader = new BareValueReader(new byte[] { 0, 0, 0 });
                var prior = Item.__DurableState.ReadBaseBodyV1(ref reader);
                var bad = new BareValueReader(new byte[] { 7, 1, 128, 1, 0 });
                try { Item.__DurableState.ApplyDeltaBodyV1(ref bad, in prior); return false; }
                catch (System.IO.InvalidDataException) { }
                var buffer = new ArrayBufferWriter<byte>();
                var writer = new BareValueWriter(buffer);
                Item.__DurableState.WriteBaseBody(ref writer, in prior);
                return buffer.WrittenSpan.SequenceEqual(new byte[] { 0, 0, 0 });
            }
            """ : "") + "\n}";

    private const string FusedDeltaPreamble = """
        using System;
        using System.Buffers;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Serialization;
        namespace FusedDelta;

        """;

    private static string FusedDeltaHostMethods(string type, int version) => $$"""
        public static PreparedDeltaBody Prepare{{version}}(byte[] oldBytes, byte[] newBytes) {
            var oldReader = new BareValueReader(oldBytes);
            var prior = {{type}}.__DurableState.ReadBaseBodyV{{version}}(ref oldReader);
            oldReader.EnsureFullyConsumed();
            var newReader = new BareValueReader(newBytes);
            var current = {{type}}.__DurableState.ReadBaseBodyV{{version}}(ref newReader);
            newReader.EnsureFullyConsumed();
            return {{type}}.__DurableState.PrepareDeltaBody(in prior, in current);
        }
        public static byte[] Apply{{version}}(byte[] oldBytes, byte[] delta) {
            var oldReader = new BareValueReader(oldBytes);
            var prior = {{type}}.__DurableState.ReadBaseBodyV{{version}}(ref oldReader);
            oldReader.EnsureFullyConsumed();
            var deltaReader = new BareValueReader(delta);
            var restored = {{type}}.__DurableState.ApplyDeltaBodyV{{version}}(ref deltaReader, in prior);
            deltaReader.EnsureFullyConsumed();
            var buffer = new ArrayBufferWriter<byte>();
            var writer = new BareValueWriter(buffer);
            {{type}}.__DurableState.WriteBaseBody(ref writer, in restored);
            return buffer.WrittenSpan.ToArray();
        }

        """;
}
