using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed class ListRangeDeltaTests {
    private static readonly ListLayout Layout = new(new(1, TypeTag.Int32));

    [Fact]
    public void SparseChainDoesNotAllocateAFullListForEachDelta() {
        const int count = 4096, depth = 32;
        byte[][] bodies = new byte[depth + 1][];
        bodies[0] = ListStateBody<int, Int32StateOps>.PrepareBase(new(new int[count]), Layout).Body.ToArray();
        for (int i = 1; i <= depth; i++) {
            // CopyAndPatch over all 4096 elements. Alternate the first value between 1 and 2.
            bodies[i] = [0x80, 0x20, 3, 0, 0x80, 0x20, 1, (byte)(i % 2 == 1 ? 2 : 4), 0];
        }
        Bodies source = new(bodies);
        ListStateReader<int, Int32StateOps> reader = new(Layout);
        reader.Read(new(1), source);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var result = reader.Read(new(1), source).GetListState<int>();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(2, result[0]);
        Assert.Equal(count, result.Count);
        Assert.True(allocated < 4 * count * sizeof(int), $"Sparse List replay allocated {allocated} bytes.");
    }

    [Fact]
    public void ChainWithRepeatedSourcesAndSizeChangesPreservesDeliveredStatesAfterFailure() {
        byte[] baseBody = [4, 2, 4, 6, 8];                   // [1, 2, 3, 4]
        byte[] common = [4, 3, 0, 4, 1, 18, 4, 16, 0];       // [9, 2, 3, 8]
        ListStateReader<int, Int32StateOps> reader = new(Layout);
        var first = reader.Read(new(1), new Bodies(baseBody, common)).GetListState<int>();
        byte[] repeat = [4, 3, 0, 2, 1, 12, 0, 1, 0, 2];    // First range patched, second reuses original [9, 2].
        var repeated = reader.Read(new(2), new Bodies(baseBody, common, repeat)).GetListState<int>();
        Assert.Equal(new[] { 6, 2, 9, 2 }, repeated.Elements.ToArray());
        Bodies sibling = new(baseBody, common, repeat,
            [4, 1, 0, 4],                                   // [6, 2, 9, 2]
            [2, 1, 0, 2],                                   // Shrink to [6, 2].
            [3, 1, 0, 2, 2, 1, 14],                         // Append 7.
            [3, 3, 0, 3, 2, 10, 0]);                        // Patch to [6, 5, 7].
        var second = reader.Read(new(2), sibling).GetListState<int>();
        Assert.Throws<InvalidDataException>(() => reader.Read(new(1),
            new Bodies(baseBody, common, [4, 3, 0, 4, 1, 20, 1, 22, 0]))); // Repeated index after a valid write.
        Assert.Equal(new[] { 9, 2, 3, 8 }, first.Elements.ToArray());
        Assert.Equal(new[] { 6, 5, 7 }, second.Elements.ToArray());
        Assert.Equal(second.Elements.ToArray(), reader.Read(new(2), sibling).GetListState<int>().Elements.ToArray());
    }

    private sealed class Bodies(params byte[][] bodies) : IStateBodySource {
        public int Count => bodies.Length;
        public ReadOnlySpan<byte> GetBody(int index) => bodies[index];
    }

    [Theory]
    [InlineData(ListDeltaAlgorithm.LocalResync)]
    [InlineData(ListDeltaAlgorithm.BoundedMyers)]
    public void InsertAndDeleteReuseTheUnchangedSuffix(ListDeltaAlgorithm algorithm) {
        FrozenListState<int> prior = new(new[] { 1, 2, 3, 4 });
        PreparedDeltaBody inserted = ListStateBody<int, Int32StateOps>.PrepareDelta(prior, new(new[] { 1, 9, 2, 3, 4 }), Layout, algorithm);
        Assert.Equal(new byte[] { 5, 1, 0, 1, 2, 1, 18, 1, 1, 3 }, inserted.Body.ToArray());
        Assert.Equal(new[] { 1, 9, 2, 3, 4 }, Apply(prior, inserted.Body));
        PreparedDeltaBody removed = ListStateBody<int, Int32StateOps>.PrepareDelta(prior, new(new[] { 1, 3, 4 }), Layout, algorithm);
        Assert.Equal(new byte[] { 3, 1, 0, 1, 1, 2, 2 }, removed.Body.ToArray());
        Assert.Equal(new[] { 1, 3, 4 }, Apply(prior, removed.Body));
    }

    [Theory]
    [InlineData(ListDeltaAlgorithm.LocalResync)]
    [InlineData(ListDeltaAlgorithm.BoundedMyers)]
    public void ShiftedRangeCoalescesSparseChangesAndCopy(ListDeltaAlgorithm algorithm) {
        FrozenListState<int> prior = new(new[] { 1, 2, 3, 4 });
        PreparedDeltaBody delta = ListStateBody<int, Int32StateOps>.PrepareDelta(prior, new(new[] { 1, 9, 2, 8, 4 }), Layout, algorithm);
        Assert.Equal(new byte[] { 5, 1, 0, 1, 2, 1, 18, 3, 1, 3, 2, 16, 0 }, delta.Body.ToArray());
        Assert.Equal(new[] { 1, 9, 2, 8, 4 }, Apply(prior, delta.Body));
    }

    [Fact]
    public void RepeatedSourceRangesHaveIndependentPatches() {
        FrozenListState<int> prior = new(new[] { 1, 2 });
        byte[] bytes = [4, 3, 0, 2, 1, 18, 0, 3, 0, 2, 2, 16, 0];
        Assert.Equal(new[] { 9, 2, 1, 8 }, Apply(prior, bytes));
        Assert.Equal(new[] { 1, 2 }, prior.Elements.ToArray());
        for (int length = 0; length < bytes.Length; length++) {
            byte[] truncated = bytes[..length];
            Exception? error = Record.Exception(() => Apply(prior, truncated));
            Assert.True(error is InvalidDataException or EndOfStreamException, $"Accepted prefix length {length}.");
        }
    }

    [Fact]
    public void SourceOrderIsIndependentFromOutputOrderAndLiteralSize() {
        Assert.Equal(new[] { 3, 4, 1, 2 }, Apply(new(new[] { 1, 2, 3, 4 }), new byte[] { 4, 1, 2, 2, 1, 0, 2 }));
        int[] large = Enumerable.Range(0, 1024).ToArray();
        // A five-byte Copy instruction reconstructs more elements than its literal byte count permits.
        Assert.Equal(large, Apply(new(large), new byte[] { 128, 8, 1, 0, 128, 8 }));
    }

    [Theory]
    [InlineData(ListDeltaAlgorithm.Position)]
    [InlineData(ListDeltaAlgorithm.LocalResync)]
    [InlineData(ListDeltaAlgorithm.BoundedMyers)]
    [InlineData(ListDeltaAlgorithm.Adaptive)]
    public void RandomSequencesRoundtripWithOneReaderAndIndependentInputs(ListDeltaAlgorithm algorithm) {
        Random random = new(49621);
        for (int trial = 0; trial < 350; trial++) {
            int[] oldValues = Enumerable.Range(0, random.Next(0, 40)).Select(_ => random.Next(-3, 4)).ToArray();
            int[] newValues = trial % 5 == 0 ? oldValues.ToArray() :
                Enumerable.Range(0, random.Next(0, 40)).Select(_ => random.Next(-3, 4)).ToArray();
            FrozenListState<int> prior = new(oldValues), current = new(newValues);
            PreparedDeltaBody delta = ListStateBody<int, Int32StateOps>.PrepareDelta(prior, current, Layout, algorithm);
            Assert.Equal(!oldValues.SequenceEqual(newValues), delta.HasChanges);
            Assert.Equal(newValues, Apply(prior, delta.Body));
            Assert.Equal(oldValues, prior.Elements.ToArray());
            Assert.Equal(newValues, current.Elements.ToArray());
            Assert.Equal(delta.Body.ToArray(), ListStateBody<int, Int32StateOps>.PrepareDelta(prior, current, Layout, algorithm).Body.ToArray());
        }
    }

    [Fact]
    public void ChangedPairRejectsInconsistentStaticOperations() {
        Assert.Throws<InvalidOperationException>(() => ListStateBody<int, BrokenOps>.PrepareDelta(
            new(new[] { 1 }), new(new[] { 2 }), Layout));
        Assert.Throws<ArgumentOutOfRangeException>(() => ListStateBody<int, Int32StateOps>.PrepareDelta(
            new([]), new([]), Layout, (ListDeltaAlgorithm)99));
    }

    private static int[] Apply(FrozenListState<int> prior, ReadOnlySpan<byte> bytes) {
        BareValueReader reader = new(bytes);
        FrozenListState<int> state = ListStateBody<int, Int32StateOps>.ApplyDelta(ref reader, prior, Layout);
        reader.EnsureFullyConsumed();
        return state.Elements.ToArray();
    }

    private readonly struct BrokenOps : IStateOps<int> {
        public static bool StateEquals(in int left, in int right, DurableFieldInfo slot) => left == right;
        public static void WriteBase(ref BareValueWriter writer, in int value, DurableFieldInfo slot) => Int32StateOps.WriteBase(ref writer, in value, slot);
        public static int ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => Int32StateOps.ReadBase(ref reader, slot);
        public static PreparedDeltaBody PrepareDelta(in int prior, in int current, DurableFieldInfo slot) => new(false, []);
        public static int ApplyDelta(ref BareValueReader reader, in int prior, DurableFieldInfo slot) => Int32StateOps.ApplyDelta(ref reader, in prior, slot);
        public static void VisitReferences(in int value, IStateReferenceVisitor visitor, DurableFieldInfo slot) { }
    }
}
