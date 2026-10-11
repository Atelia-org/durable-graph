using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed class ObjectStateComparisonTests {
    private static readonly DurableSchema Schema = new("comparison.owner", 1, new DurableFieldInfo(1, TypeTag.Int32));
    private static readonly StateValueBinding IntSlot = new(new(1, TypeTag.Int32), typeof(int), typeof(ComparisonOnlyOps),
        typeof(int), typeof(IdentityValueProjection<int>));

    [Fact]
    public void OptionalTypedProofNeverFallsBackToPreparers() {
        ObjectStateRecord left = new(new(1), Schema, 3);
        ObjectStateRecord equal = new(new(2), Schema, 3);
        ObjectStateRecord changed = new(new(1), Schema, 4);
        ICapturedStatePreparation absent = Preparation();
        Assert.False(absent.ProvesSameState(left, left));
        Assert.False(absent.ProvesSameState(left, equal));
        ICapturedStatePreparation present = Preparation((in int a, in int b) => a == b);
        Assert.True(present.ProvesSameState(left, equal)); // Identity is an outer GraphReader responsibility.
        Assert.False(present.ProvesSameState(left, changed));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TypedProofValidatesBothExactSchemasAndDtosEvenWithoutCapability(bool hasProof, bool invalidLeft) {
        int calls = 0;
        ICapturedStatePreparation preparation = Preparation(hasProof ? (in int a, in int b) => { calls++; return a == b; } : null);
        ObjectStateRecord valid = new(new(1), Schema, 3);
        ObjectStateRecord[] invalid = [
            new(new(1), new DurableSchema("comparison.owner", 1, new DurableFieldInfo(1, TypeTag.Int64)), 3),
            new(new(1), Schema, 3L),
            new(new(1), "wrong kind"),
        ];
        foreach (ObjectStateRecord bad in invalid) {
            Assert.Throws<InvalidOperationException>(() => preparation.ProvesSameState(invalidLeft ? bad : valid, invalidLeft ? valid : bad));
        }
        Assert.Equal(0, calls);
    }

    [Fact]
    public void TypedProofPropagatesTheOriginalCallbackError() {
        NotSupportedException failure = new("A registered comparison failed; it is not missing.");
        int calls = 0;
        ICapturedStatePreparation preparation = Preparation((in int a, in int b) => { calls++; throw failure; });
        ObjectStateRecord value = new(new(1), Schema, 3);
        Assert.Same(failure, Assert.Throws<NotSupportedException>(() => preparation.ProvesSameState(value, value)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ArrayProofUsesEveryDimensionAndEveryElementWithoutEncoding() {
        ArrayLayout layout = new(TypeExprKind.Rank2Array, IntSlot.Slot);
        ICapturedStatePreparation preparation = (ICapturedStatePreparation)ArrayObjectBinding.Create(typeof(int[,]), layout, IntSlot);
        ObjectStateRecord Row(ArrayShape shape, params int[] values) => new(new(1), layout, new FrozenArrayState<int>(shape, values));
        ObjectStateRecord left = Row(new(2, 3), 1, 2, 3, 4, 5, 6);
        Assert.True(preparation.ProvesSameState(left, Row(new(2, 3), 1, 2, 3, 4, 5, 6)));
        Assert.False(preparation.ProvesSameState(left, Row(new(3, 2), 1, 2, 3, 4, 5, 6)));
        Assert.False(preparation.ProvesSameState(left, Row(new(2, 3), 1, 2, 3, 4, 5, 7)));
        Assert.False(preparation.ProvesSameState(left, Row(new(2, 2), 1, 2, 3, 4)));
        Assert.True(preparation.ProvesSameState(Row(new(0, 3)), Row(new(0, 3))));
        Assert.False(preparation.ProvesSameState(Row(new(0, 3)), Row(new(0, 4))));
    }

    [Fact]
    public void ListProofUsesCountAndOrderedElementsWithoutEncoding() {
        ListLayout layout = new(IntSlot.Slot);
        ICapturedStatePreparation preparation = (ICapturedStatePreparation)ListObjectBinding.Create(typeof(List<int>), layout, IntSlot);
        ObjectStateRecord Row(params int[] values) => new(new(1), layout, new FrozenListState<int>(values));
        Assert.True(preparation.ProvesSameState(Row(1, 2, 3), Row(1, 2, 3)));
        Assert.True(preparation.ProvesSameState(Row(), Row()));
        Assert.False(preparation.ProvesSameState(Row(1, 2, 3), Row(1, 2)));
        Assert.False(preparation.ProvesSameState(Row(1, 2, 3), Row(1, 2, 4)));
        Assert.False(preparation.ProvesSameState(Row(1, 2, 3), Row(2, 1, 3)));
    }

    [Fact]
    public void DictionaryProofUsesMetadataAndEveryKeyAndValueWithoutEncoding() {
        DictionaryLayout layout = new(IntSlot.Slot, IntSlot.WithFieldId(2).Slot);
        ICapturedStatePreparation preparation = (ICapturedStatePreparation)DictionaryObjectBinding.Create(
            typeof(Dictionary<int, int>), layout, IntSlot, IntSlot);
        // Construct already-owned records directly: normal capture/read separately validates canonical keys using its writer.
        ObjectStateRecord Row(DictionaryComparerKind kind, params DictionaryEntryState<int, int>[] values) =>
            new(new(1), layout, new FrozenDictionaryState<int, int>(kind, values));
        const DictionaryComparerKind standard = DictionaryComparerKind.ScalarDefault;
        ObjectStateRecord left = Row(standard, new(1, 10), new(2, 20));
        Assert.True(preparation.ProvesSameState(left, Row(standard, new(1, 10), new(2, 20))));
        Assert.True(preparation.ProvesSameState(Row(standard), Row(standard)));
        Assert.False(preparation.ProvesSameState(left, Row(DictionaryComparerKind.Application, new(1, 10), new(2, 20))));
        Assert.False(preparation.ProvesSameState(left, Row(standard, new DictionaryEntryState<int, int>(1, 10))));
        Assert.False(preparation.ProvesSameState(left, Row(standard, new(1, 10), new(3, 20))));
        Assert.False(preparation.ProvesSameState(left, Row(standard, new(1, 10), new(2, 21))));
        Assert.False(preparation.ProvesSameState(left, Row(standard, new(2, 20), new(1, 10))));
        Assert.Throws<InvalidDataException>(() => preparation.ProvesSameState(left, Row(DictionaryComparerKind.StringOrdinal)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContainersValidateBothRecordsBeforeDecidingEquality(bool invalidLeft) {
        ArrayLayout arrayLayout = new(TypeExprKind.VectorArray, IntSlot.Slot);
        ListLayout listLayout = new(IntSlot.Slot);
        DictionaryLayout dictionaryLayout = new(IntSlot.Slot, IntSlot.WithFieldId(2).Slot);
        (ICapturedStatePreparation Preparation, ObjectStateRecord Valid, ObjectStateRecord Invalid)[] cases = [
            ((ICapturedStatePreparation)ArrayObjectBinding.Create(typeof(int[]), arrayLayout, IntSlot),
                new(new(1), arrayLayout, new FrozenArrayState<int>(new(0), [])),
                new(new(1), arrayLayout, new FrozenArrayState<long>(new(1), [1]))),
            ((ICapturedStatePreparation)ListObjectBinding.Create(typeof(List<int>), listLayout, IntSlot),
                new(new(1), listLayout, new FrozenListState<int>([])),
                new(new(1), listLayout, new FrozenListState<long>([1]))),
            ((ICapturedStatePreparation)DictionaryObjectBinding.Create(typeof(Dictionary<int, int>), dictionaryLayout, IntSlot, IntSlot),
                new(new(1), dictionaryLayout, new FrozenDictionaryState<int, int>(DictionaryComparerKind.ScalarDefault, [])),
                new(new(1), dictionaryLayout, new FrozenDictionaryState<int, long>(DictionaryComparerKind.ScalarDefault, [new(1, 1)]))),
        ];
        foreach (var item in cases) {
            Assert.Throws<InvalidOperationException>(() => item.Preparation.ProvesSameState(
                invalidLeft ? item.Invalid : item.Valid, invalidLeft ? item.Valid : item.Invalid));
        }
    }

    private static ICapturedStatePreparation Preparation(StateEquality<int>? equality = null) => new CapturedStatePreparation<int>(Schema,
        (in int state) => throw new InvalidOperationException("Comparison must not prepare Base."),
        (in int prior, in int current) => throw new InvalidOperationException("Comparison must not prepare Delta."), equality);

    private readonly struct ComparisonOnlyOps : IStateOps<int> {
        public static bool StateEquals(in int left, in int right, DurableFieldInfo slot) => left == right;
        public static void WriteBase(ref BareValueWriter writer, in int state, DurableFieldInfo slot) => throw new InvalidOperationException("Comparison encoded a slot.");
        public static PreparedDeltaBody PrepareDelta(in int prior, in int current, DurableFieldInfo slot) => throw new InvalidOperationException("Comparison prepared a slot Delta.");
        public static int ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => throw new NotSupportedException();
        public static int ApplyDelta(ref BareValueReader reader, in int prior, DurableFieldInfo slot) => throw new NotSupportedException();
        public static void VisitReferences(in int state, IStateReferenceVisitor visitor, DurableFieldInfo slot) { }
    }
}
