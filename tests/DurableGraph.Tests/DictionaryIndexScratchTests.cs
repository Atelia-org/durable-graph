using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed class DictionaryIndexScratchTests {
    // A synthetic codec makes one key cross both a writer chunk and the LOH threshold.
    // Each one-entry Base starts with a fresh scratch and supplies an independent oracle.
    [Fact]
    public void VariedKeySizesMatchIndependentEncodingsAndKeepTheLargeKeyDistinct() {
        var layout = new DictionaryLayout(new(1, TypeTag.Int32), new(2, TypeTag.Int32));
        DictionaryEntryState<int, int>[] entries = [new(100_000, 11), new(0, 22), new(7, 33), new(257, 44)];
        byte[] Encode(params DictionaryEntryState<int, int>[] source) =>
            DictionaryStateBody<int, int, PaddedKeyOps, Int32StateOps>.PrepareBase(
                new(DictionaryComparerKind.Application, source), layout).Body.ToArray();
        var expected = new List<byte> { (byte)DictionaryComparerKind.Application, (byte)entries.Length };
        foreach (var entry in entries) { expected.AddRange(Encode(entry).AsSpan(2)); }
        byte[] bytes = Encode(entries);
        Assert.Equal(expected.ToArray(), bytes);
        BareValueReader reader = new(bytes);
        var restored = DictionaryStateBody<int, int, PaddedKeyOps, Int32StateOps>.ReadBase(ref reader, layout);
        reader.EnsureFullyConsumed();
        Assert.Equal(entries, restored.Entries.ToArray());
        Assert.Throws<InvalidDataException>(() => Encode([.. entries, entries[0]]));
        Assert.Equal(new byte[] { (byte)DictionaryComparerKind.Application, 0 }, Encode());
    }

    private readonly struct PaddedKeyOps : IStateOps<int> {
        private static readonly byte[] Padding = Enumerable.Repeat((byte)0xA5, 100_000).ToArray();
        public static void WriteBase(ref BareValueWriter writer, in int state, DurableFieldInfo slot) {
            writer.WriteVarUInt32((uint)state);
            writer.WriteRawBytes(Padding.AsSpan(0, state));
        }
        public static int ReadBase(ref BareValueReader reader, DurableFieldInfo slot) {
            int size = checked((int)reader.ReadVarUInt32());
            if (!reader.ReadRawBytes(size).SequenceEqual(Padding.AsSpan(0, size))) { throw new InvalidDataException(); }
            return size;
        }
        public static bool StateEquals(in int left, in int right, DurableFieldInfo slot) => left == right;
        public static PreparedDeltaBody PrepareDelta(in int prior, in int current, DurableFieldInfo slot) => throw new NotSupportedException();
        public static int ApplyDelta(ref BareValueReader reader, in int prior, DurableFieldInfo slot) => throw new NotSupportedException();
        public static void VisitReferences(in int state, IStateReferenceVisitor visitor, DurableFieldInfo slot) { }
    }
}
