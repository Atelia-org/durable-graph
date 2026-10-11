using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void InlineStructBodiesComposeNestedDeltaAndPreserveExactFloatingBits() {
        GeneratorTestRun run = RunGenerator(InlineBodySource);
        AssertSchemaOnlyCompiles(run);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("FusedDelta.Host")!;
        var prepare = host.GetMethod("Prepare1")!.CreateDelegate<Func<byte[], byte[], PreparedDeltaBody>>();
        var apply = host.GetMethod("Apply1")!.CreateDelegate<Func<byte[], byte[], byte[]>>();
        byte[][] states = new[] {
            "00000000000000", // bool false, uint zero, positive float zero, trailing bool false.
            "00000000008000", // Same values except negative float zero.
            "00000100C07F00", // NaN payload 1.
            "00000200C07F00", // NaN payload 2.
            "01020000008001", // Every nonempty leaf plus the sibling changes.
        }.Select(Convert.FromHexString).ToArray();
        foreach (byte[] prior in states) {
            foreach (byte[] current in states) {
                PreparedDeltaBody delta = prepare(prior, current);
                Assert.Equal(!prior.SequenceEqual(current), delta.HasChanges);
                Assert.Equal(current, apply(prior, delta.Body.ToArray()));
                if (prior.SequenceEqual(current)) Assert.Equal<byte>([0], delta.Body.ToArray());
            }
        }
        // Each owner has its own bitmap: Item, Middle, Leaf; Empty contributes no body.
        Assert.Equal(Convert.FromHexString("01010400000080"), prepare(states[0], states[1]).Body.ToArray());
        Assert.Equal(Convert.FromHexString("03010701020000008001"), prepare(states[0], states[4]).Body.ToArray());
        string generated = GeneratedSource(run, "DurableStates.g.cs");
        AssertGeneratedBodiesRemainStaticallyBound(generated);
        Assert.DoesNotContain("ValueSlotCodec", generated);
        AssertPrepareDeltaDoesNotPrecompare(generated);
        Assert.DoesNotContain("typeof(", generated.Split("namespace Atelia.DurableGraph.Generated {")[1]);
        Assert.Contains(".PrepareDeltaBody(in prior.Segment0Field1", generated);
        Assert.Contains("writer.WriteRawBytes(delta0.Body)", generated);
    }

    [Fact]
    public void InlineStructDeltaRejectsNestedNoopPaddingRedundancyTruncationAndTrailingData() {
        GeneratorTestRun run = RunGenerator(InlineBodySource);
        AssertSchemaOnlyCompiles(run);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("FusedDelta.Host")!;
        var apply = host.GetMethod("Apply1")!.CreateDelegate<Func<byte[], byte[], byte[]>>();
        byte[] prior = Convert.FromHexString("00000000000000");
        byte[] valid = Convert.FromHexString("03010701020000008001");
        for (int length = 0; length < valid.Length; length++) {
            Assert.Throws<EndOfStreamException>(() => apply(prior, valid[..length]));
        }
        foreach (string invalid in new[] {
            "04", // Item padding.
            "0104", // Middle padding.
            "010108", // Leaf padding.
            "0100", // Parent changed, Middle unchanged.
            "010100", // Middle changed, Leaf unchanged.
            "0102", // Empty value cannot have a changed bit.
            "01010100", // Redundant leaf bool.
            "01010200", // Redundant leaf uint.
            "01010400000000", // Redundant exact floating bits.
            "01010102", // Invalid bool.
            "0000", // Extra body after unchanged owner.
            "0301070102000000800100", // Valid Delta with tail.
        }) Assert.Throws<InvalidDataException>(() => apply(prior, Convert.FromHexString(invalid)));
        Assert.Equal(Convert.FromHexString("01020000008001"), apply(prior, valid));
        Assert.Equal(Convert.FromHexString("00000000000000"), prior);
    }

    [Fact]
    public void InlineStructGeneratedRefHydrationRestoresReadonlyFieldAndArraySlotWithoutConstructors() {
        GeneratorTestRun run = RunGenerator(InlineBodySource);
        AssertSchemaOnlyCompiles(run);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("FusedDelta.Host")!;
        Assert.True(host.GetMethod("RefHydration")!.CreateDelegate<Func<bool>>()());
    }

    private static string InlineBodySource => FusedDeltaPreamble + """
        [DurableType("inline.empty", 1)]
        public readonly partial struct Empty { }
        [DurableType("inline.leaf", 1)]
        public readonly partial struct Leaf {
            [DurableField(1)] private readonly bool _flag;
            [DurableField(2)] private readonly uint _number;
            [DurableField(3)] private readonly float _float;
            [Transient] private readonly int _transient;
            public static int ConstructorCalls;
            public Leaf() { _flag = true; _number = 99; _float = 42; _transient = 91; ConstructorCalls++; }
            public bool IsRestored => _flag && _number == 2 && BitConverter.SingleToUInt32Bits(_float) == 0x80000000 && _transient == 0;
        }
        [DurableType("inline.middle", 1)]
        public readonly partial struct Middle {
            [DurableField(1)] private readonly Leaf _leaf;
            [DurableField(2)] private readonly Empty _empty;
            public bool IsRestored => _leaf.IsRestored;
        }
        [DurableType("inline.item", 1)]
        public sealed partial class Item : IDurableObject {
            [DurableField(1)] public readonly Middle Value;
            [DurableField(2)] public readonly bool Tail;
        }
        public static class Host {
        """ + FusedDeltaHostMethods("Item", 1) + """
            public static bool RefHydration() {
                Leaf.ConstructorCalls = 0;
                var reader = new BareValueReader(Convert.FromHexString("01020000008001"));
                var state = Item.__DurableState.ReadBaseBodyV1(ref reader);
                reader.EnsureFullyConsumed();
                var objects = new ObjectReadTable(StringReadTable.Decode(Array.Empty<(ObjectId, ReadOnlyMemory<byte>)>()),
                    new System.Collections.Generic.Dictionary<ObjectId, IDurableObject>());
                var item = Item.__DurableState.Allocate();
                Item.__DurableState.Hydrate(item, in state, objects);
                var array = new Middle[1];
                Middle.__DurableState.Hydrate(ref array[0], in state.Segment0Field1, objects);
                var captured = Item.__DurableState.Capture(item);
                var prepared = Item.__DurableState.PrepareBaseBody(in captured);
                return item.Value.IsRestored && item.Tail && array[0].IsRestored && Leaf.ConstructorCalls == 0 &&
                    prepared.Body.SequenceEqual(Convert.FromHexString("01020000008001"));
            }
        }
        """;
}
