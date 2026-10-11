using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed class ListRangeDeltaTests {
    private static readonly ListLayout Layout = new(new(1, TypeTag.Int32));

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
