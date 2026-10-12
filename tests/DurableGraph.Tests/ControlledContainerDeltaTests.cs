using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed class ControlledContainerDeltaTests {
    private static readonly ListLayout ListLayout = new(new(1, TypeTag.Int32));
    private static readonly DictionaryLayout DictionaryLayout = new(new(1, TypeTag.UInt32), new(2, TypeTag.Int32));

    [Fact]
    public void AppendEncodesOnlyTheTailWithoutComparingTheOldPrefix() {
        int[] before = Enumerable.Range(1, 256).ToArray();
        int[] tail = [257, 258];
        int[] expected = [.. before, .. tail];
        FrozenListState<int> prior = new(before), current = new(expected);
        CountingValueOps.Reset();
        PreparedDeltaBody controlled = ListStateBody<int, CountingValueOps>.PrepareAppendDelta(prior.Count, tail, ListLayout);
        Assert.True(controlled.HasChanges);
        Assert.Equal(0, CountingValueOps.Comparisons);
        Assert.Equal(0, CountingValueOps.DeltaPreparations);
        Assert.Equal(tail.Length, CountingValueOps.BaseWrites);

        PreparedDeltaBody compared = ListStateBody<int, Int32StateOps>.PrepareDelta(prior, current, ListLayout,
            ListDeltaAlgorithm.Position);
        Assert.Equal(compared.Body.ToArray(), controlled.Body.ToArray());
        tail[0] = -1;
        BareValueReader reader = new(controlled.Body);
        FrozenListState<int> restored = ListStateBody<int, Int32StateOps>.ApplyDelta(ref reader, prior, ListLayout);
        reader.EnsureFullyConsumed();
        Assert.Equal(expected, restored.Elements.ToArray());
        Assert.Equal(before, prior.Elements.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void EmptyAppendIsNoChangeWithoutOldElementWork(int priorCount) {
        FrozenListState<int> prior = new(Enumerable.Range(1, priorCount).ToArray());
        CountingValueOps.Reset();
        PreparedDeltaBody delta = ListStateBody<int, CountingValueOps>.PrepareAppendDelta(prior.Count, [], ListLayout);
        Assert.False(delta.HasChanges);
        Assert.Equal(0, CountingValueOps.Comparisons);
        Assert.Equal(0, CountingValueOps.DeltaPreparations);
        Assert.Equal(0, CountingValueOps.BaseWrites);
        BareValueReader reader = new(delta.Body);
        Assert.Equal(prior.Elements.ToArray(), ListStateBody<int, Int32StateOps>.ApplyDelta(ref reader, prior, ListLayout).Elements.ToArray());
        reader.EnsureFullyConsumed();
    }

    [Fact]
    public void AppendRejectsInvalidOrOverflowingCountsBeforeEncodingElements() {
        CountingValueOps.Reset();
        Assert.Throws<ArgumentOutOfRangeException>(() => ListStateBody<int, CountingValueOps>.PrepareAppendDelta(-1, [], ListLayout));
        Assert.Throws<ArgumentOutOfRangeException>(() => ListStateBody<int, CountingValueOps>.PrepareAppendDelta(Array.MaxLength + 1, [], ListLayout));
        Assert.Throws<ArgumentOutOfRangeException>(() => ListStateBody<int, CountingValueOps>.PrepareAppendDelta(Array.MaxLength, [1], ListLayout));
        Assert.Equal(0, CountingValueOps.BaseWrites);
    }

    [Fact]
    public void ReducedDictionaryOperationsAvoidCompleteKeyIndexingAndUnchangedValuePreparation() {
        const int count = 256;
        DictionaryEntryState<uint, int>[] before = Enumerable.Range(1, count)
            .Select(i => new DictionaryEntryState<uint, int>((uint)i, i * 10)).ToArray();
        DictionaryEntryState<uint, int>[] after = before.Where(entry => entry.Key != 2)
            .Select(entry => entry.Key == 1 ? new DictionaryEntryState<uint, int>(1, 11) : entry)
            .Append(new(257, 2570)).ToArray();
        FrozenDictionaryState<uint, int> prior = new(DictionaryComparerKind.ScalarDefault, before);
        FrozenDictionaryState<uint, int> current = new(DictionaryComparerKind.ScalarDefault, after);
        CountingKeyOps.Writes = 0;
        CountingValueOps.Reset();
        PreparedDeltaBody controlled = DictionaryStateBody<uint, int, CountingKeyOps, CountingValueOps>.PrepareDeltaFromOperations(
            DictionaryLayout, [2], [new(1, 10, 11), new(3, 30, 30)], [new(257, 2570)]);
        Assert.True(controlled.HasChanges);
        Assert.Equal(4, CountingKeyOps.Writes); // Includes the supplied no-op Patch key, but no retained key indexing.
        Assert.Equal(2, CountingValueOps.DeltaPreparations);
        Assert.Equal(1, CountingValueOps.BaseWrites);
        Assert.Equal(0, CountingValueOps.Comparisons);

        CountingKeyOps.Writes = 0;
        CountingValueOps.Reset();
        PreparedDeltaBody compared = DictionaryStateBody<uint, int, CountingKeyOps, CountingValueOps>.PrepareDelta(prior, current, DictionaryLayout);
        Assert.Equal(prior.Count + current.Count, CountingKeyOps.Writes); // The comparison path still indexes both complete endpoints.
        Assert.Equal(count - 1, CountingValueOps.DeltaPreparations);
        Assert.Equal(1, CountingValueOps.BaseWrites);
        Assert.Equal(compared.Body.ToArray(), controlled.Body.ToArray());
        FrozenDictionaryState<uint, int> restored = ApplyDictionary(controlled, prior);
        Assert.Equal(after, restored.Entries.ToArray());
        Assert.Equal(before, prior.Entries.ToArray());
    }

    [Fact]
    public void DictionaryNoChangePatchIsOmittedAndEmptyOperationsDoNoElementWork() {
        CountingKeyOps.Writes = 0;
        CountingValueOps.Reset();
        PreparedDeltaBody empty = DictionaryStateBody<uint, int, CountingKeyOps, CountingValueOps>.PrepareDeltaFromOperations(
            DictionaryLayout, [], [], []);
        Assert.False(empty.HasChanges);
        Assert.Equal(new byte[] { 0, 0, 0 }, empty.Body.ToArray());
        Assert.Equal(0, CountingKeyOps.Writes);
        Assert.Equal(0, CountingValueOps.DeltaPreparations);
        Assert.Equal(0, CountingValueOps.BaseWrites);

        PreparedDeltaBody noOp = DictionaryStateBody<uint, int, CountingKeyOps, CountingValueOps>.PrepareDeltaFromOperations(
            DictionaryLayout, [], [new(1, 10, 10)], []);
        Assert.False(noOp.HasChanges);
        Assert.Equal(empty.Body.ToArray(), noOp.Body.ToArray());
        Assert.Equal(1, CountingKeyOps.Writes);
        Assert.Equal(1, CountingValueOps.DeltaPreparations);
        Assert.Equal(0, CountingValueOps.BaseWrites);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void DictionaryRejectsRepeatedKeysWithinAndAcrossOperationGroups(int duplicateCase) {
        uint[] removed = duplicateCase switch { 0 => [1, 1], 3 or 4 => [1], _ => [] };
        DictionaryValueChange<uint, int>[] patched = duplicateCase switch {
            1 => [new(1, 10, 10), new(1, 10, 11)],
            3 => [new(1, 10, 11)],
            5 => [new(1, 10, 10)],
            _ => [],
        };
        DictionaryEntryState<uint, int>[] added = duplicateCase switch { 2 => [new(1, 10), new(1, 11)], 4 or 5 => [new(1, 20)], _ => [] };
        Assert.Throws<InvalidDataException>(() => DictionaryStateBody<uint, int, UInt32StateOps, Int32StateOps>.PrepareDeltaFromOperations(
            DictionaryLayout, removed, patched, added));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ApplyingControlledOperationsStillChecksExactPriorMembership(int operationCase) {
        FrozenDictionaryState<uint, int> prior = new(DictionaryComparerKind.ScalarDefault, [new(1, 10)]);
        uint[] removed = operationCase == 0 ? [9] : [];
        DictionaryValueChange<uint, int>[] patched = operationCase == 1 ? [new(9, 0, 90)] : [];
        DictionaryEntryState<uint, int>[] added = operationCase == 2 ? [new(1, 20)] : [];
        PreparedDeltaBody delta = DictionaryStateBody<uint, int, UInt32StateOps, Int32StateOps>.PrepareDeltaFromOperations(
            DictionaryLayout, removed, patched, added);
        Assert.Throws<InvalidDataException>(() => ApplyDictionary(delta, prior));
        Assert.Equal(new[] { new DictionaryEntryState<uint, int>(1, 10) }, prior.Entries.ToArray());
    }

    private static FrozenDictionaryState<uint, int> ApplyDictionary(PreparedDeltaBody delta, FrozenDictionaryState<uint, int> prior) {
        BareValueReader reader = new(delta.Body);
        FrozenDictionaryState<uint, int> state = DictionaryStateBody<uint, int, UInt32StateOps, Int32StateOps>.ApplyDelta(ref reader, prior, DictionaryLayout);
        reader.EnsureFullyConsumed();
        return state;
    }

    private readonly struct CountingKeyOps : IStateOps<uint> {
        internal static int Writes;
        public static bool StateEquals(in uint left, in uint right, DurableFieldInfo slot) => UInt32StateOps.StateEquals(in left, in right, slot);
        public static void WriteBase(ref BareValueWriter writer, in uint state, DurableFieldInfo slot) {
            Writes++;
            UInt32StateOps.WriteBase(ref writer, in state, slot);
        }
        public static uint ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => UInt32StateOps.ReadBase(ref reader, slot);
        public static PreparedDeltaBody PrepareDelta(in uint prior, in uint current, DurableFieldInfo slot) => UInt32StateOps.PrepareDelta(in prior, in current, slot);
        public static uint ApplyDelta(ref BareValueReader reader, in uint prior, DurableFieldInfo slot) => UInt32StateOps.ApplyDelta(ref reader, in prior, slot);
        public static void VisitReferences(in uint state, IStateReferenceVisitor visitor, DurableFieldInfo slot) { }
    }

    private readonly struct CountingValueOps : IStateOps<int> {
        internal static int BaseWrites, DeltaPreparations, Comparisons;
        internal static void Reset() { BaseWrites = 0; DeltaPreparations = 0; Comparisons = 0; }
        public static bool StateEquals(in int left, in int right, DurableFieldInfo slot) {
            Comparisons++;
            return Int32StateOps.StateEquals(in left, in right, slot);
        }
        public static void WriteBase(ref BareValueWriter writer, in int state, DurableFieldInfo slot) {
            BaseWrites++;
            Int32StateOps.WriteBase(ref writer, in state, slot);
        }
        public static int ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => Int32StateOps.ReadBase(ref reader, slot);
        public static PreparedDeltaBody PrepareDelta(in int prior, in int current, DurableFieldInfo slot) {
            DeltaPreparations++;
            return Int32StateOps.PrepareDelta(in prior, in current, slot);
        }
        public static int ApplyDelta(ref BareValueReader reader, in int prior, DurableFieldInfo slot) => Int32StateOps.ApplyDelta(ref reader, in prior, slot);
        public static void VisitReferences(in int state, IStateReferenceVisitor visitor, DurableFieldInfo slot) { }
    }
}
