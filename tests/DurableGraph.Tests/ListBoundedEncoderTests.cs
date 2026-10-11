using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed class ListBoundedEncoderTests {
    private static readonly ListLayout Layout = new(new(1, TypeTag.Int32));

    [Theory]
    [InlineData(ListDeltaAlgorithm.Position)]
    [InlineData(ListDeltaAlgorithm.LocalResync)]
    [InlineData(ListDeltaAlgorithm.BoundedMyers)]
    public void ExplicitWritersKeepTheLateMultiplePatchGolden(ListDeltaAlgorithm algorithm) {
        PreparedDeltaBody delta = ListStateBody<int, Int32StateOps>.PrepareDelta(
            new(new[] { 1, 2, 3, 4, 5 }), new(new[] { 1, 2, 9, 4, 8 }), Layout, algorithm);
        Assert.Equal(new byte[] { 5, 3, 0, 5, 3, 18, 5, 16, 0 }, delta.Body.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void ManualRangeGoldensAndExclusiveCeilingsPreserveCompleteBodies(int scenario) {
        Fixture fixture = GetFixture(scenario);
        FrozenListState<int> prior = new(fixture.Prior), current = new(fixture.Current);
        Assert.True(ListStateBody<int, Int32StateOps>.TryEncodePlan(
            prior, current, Layout, fixture.Ranges, null, out PreparedDeltaBody? unbounded, out int completeBytes));
        Assert.NotNull(unbounded);
        Assert.True(unbounded.HasChanges);
        Assert.Equal(fixture.Expected, unbounded.Body.ToArray());
        Assert.Equal(fixture.Expected.Length, completeBytes);
        BareValueReader reader = new(unbounded.Body);
        Assert.Equal(fixture.Current, ListStateBody<int, Int32StateOps>.ApplyDelta(ref reader, prior, Layout).Elements.ToArray());
        reader.EnsureFullyConsumed();

        // UInt32 writes reserve up to five bytes even when only one is advanced. The bound is actual output.
        Assert.True(ListStateBody<int, Int32StateOps>.TryEncodePlan(
            prior, current, Layout, fixture.Ranges, completeBytes + 1, out PreparedDeltaBody? accepted, out int acceptedBytes));
        Assert.NotNull(accepted);
        Assert.Equal(fixture.Expected, accepted.Body.ToArray());
        Assert.Equal(completeBytes, acceptedBytes);
        foreach (int ceiling in new[] { completeBytes, completeBytes - 1 }) {
            Assert.False(ListStateBody<int, Int32StateOps>.TryEncodePlan(
                prior, current, Layout, fixture.Ranges, ceiling, out PreparedDeltaBody? rejected, out int cutoffBytes));
            Assert.Null(rejected);
            Assert.InRange(cutoffBytes, ceiling, completeBytes);
        }
        Assert.Equal(fixture.Prior, prior.Elements.ToArray());
        Assert.Equal(fixture.Current, current.Elements.ToArray());
    }

    [Fact]
    public void ZeroByteStructElementsStillObserveHeadersAndTheFinalCeiling() {
        ListLayout layout = new(new(1, TypeTag.InlineValue,
            inlineSchema: new DurableSchema("bounded.list.empty", 1, SchemaKind.InlineValue)));
        FrozenListState<Empty> prior = new(new Empty[1]), current = new(new Empty[4]);
        ListDeltaRange[] ranges = [new(0, 0, 1), new(-1, 1, 3)];
        EmptyOps.BaseCalls = 0;
        Assert.True(ListStateBody<Empty, EmptyOps>.TryEncodePlan(prior, current, layout, ranges, 7,
            out PreparedDeltaBody? accepted));
        Assert.NotNull(accepted);
        Assert.Equal(new byte[] { 4, 1, 0, 1, 2, 3 }, accepted.Body.ToArray());
        Assert.Equal(3, EmptyOps.BaseCalls);
        BareValueReader reader = new(accepted.Body);
        Assert.Equal(4, ListStateBody<Empty, EmptyOps>.ApplyDelta(ref reader, prior, layout).Count);
        reader.EnsureFullyConsumed();

        EmptyOps.BaseCalls = 0;
        Assert.False(ListStateBody<Empty, EmptyOps>.TryEncodePlan(prior, current, layout, ranges, 6,
            out PreparedDeltaBody? rejected, out int writtenBytes));
        Assert.Null(rejected);
        Assert.Equal(6, writtenBytes);
        Assert.Equal(0, EmptyOps.BaseCalls);
    }

    [Theory]
    [InlineData(1, 0)] // Count: do not even compare the first pair.
    [InlineData(4, 1)] // CopyAndPatch range header: do not prepare its first child.
    [InlineData(5, 1)] // Patch index: do not prepare its first child.
    public void CountRangeAndPatchIndexCutoffsAvoidSubsequentCalls(int ceiling, int comparisons) {
        SentinelOps.Reset();
        SentinelOps.ThrowOnDelta = 1;
        if (comparisons == 0) { SentinelOps.ThrowOnEquals = 1; }
        Assert.False(ListStateBody<int, SentinelOps>.TryEncodePlan(
            new(new[] { 0, 0 }), new(new[] { 1, 2 }), Layout, new[] { new ListDeltaRange(0, 0, 2) }, ceiling,
            out PreparedDeltaBody? rejected, out int writtenBytes));
        Assert.Null(rejected);
        Assert.Equal(ceiling, writtenBytes);
        Assert.Equal(comparisons, SentinelOps.Compared.Count);
        Assert.Empty(SentinelOps.Prepared);
    }

    [Theory]
    [InlineData(3, 0)] // New header.
    [InlineData(4, 1)] // First completed literal.
    public void NewCutoffsDoNotCallTheNextLiteral(int ceiling, int baseCalls) {
        SentinelOps.Reset();
        SentinelOps.ThrowOnBase = baseCalls == 0 ? 1 : 2;
        Assert.False(ListStateBody<int, SentinelOps>.TryEncodePlan(
            new([]), new(new[] { 1, 2 }), Layout, new[] { new ListDeltaRange(-1, 0, 2) }, ceiling,
            out PreparedDeltaBody? rejected, out int writtenBytes));
        Assert.Null(rejected);
        Assert.Equal(ceiling, writtenBytes);
        Assert.Equal(baseCalls, SentinelOps.Written.Count);
    }

    [Fact]
    public void ChildBodyCutoffDoesNotCompareTheNextElement() {
        SentinelOps.Reset();
        SentinelOps.ThrowOnEquals = 2;
        Assert.False(ListStateBody<int, SentinelOps>.TryEncodePlan(
            new(new[] { 0, 0 }), new(new[] { 1, 2 }), Layout, new[] { new ListDeltaRange(0, 0, 2) }, 6,
            out PreparedDeltaBody? rejected, out int writtenBytes));
        Assert.Null(rejected);
        Assert.Equal(6, writtenBytes);
        Assert.Equal(new[] { 1 }, SentinelOps.Compared);
        Assert.Equal(new[] { 1 }, SentinelOps.Prepared);
    }

    [Fact]
    public void TerminatorCutoffDoesNotEnterTheNextRange() {
        SentinelOps.Reset();
        SentinelOps.ThrowOnEquals = 2;
        Assert.False(ListStateBody<int, SentinelOps>.TryEncodePlan(
            new(new[] { 0, 2 }), new(new[] { 1, 2 }), Layout,
            new[] { new ListDeltaRange(0, 0, 1), new ListDeltaRange(1, 1, 1) }, 7,
            out PreparedDeltaBody? rejected, out int writtenBytes));
        Assert.Null(rejected);
        Assert.Equal(7, writtenBytes);
        Assert.Equal(new[] { 1 }, SentinelOps.Compared);
    }

    [Fact]
    public void CopyCutoffDoesNotWriteTheNextRange() {
        SentinelOps.Reset();
        SentinelOps.ThrowOnBase = 2;
        Assert.False(ListStateBody<int, SentinelOps>.TryEncodePlan(
            new(new[] { 1 }), new(new[] { 1, 2 }), Layout,
            new[] { new ListDeltaRange(0, 0, 1), new ListDeltaRange(-1, 1, 1) }, 4,
            out PreparedDeltaBody? rejected, out int writtenBytes));
        Assert.Null(rejected);
        Assert.Equal(4, writtenBytes);
        Assert.Empty(SentinelOps.Written);
    }

    [Fact]
    public void OneAtomicChildCanOvershootButNoSubsequentChildRuns() {
        SentinelOps.Reset();
        SentinelOps.DeltaPayloadBytes = 64;
        SentinelOps.ThrowOnEquals = 2;
        Assert.False(ListStateBody<int, SentinelOps>.TryEncodePlan(
            new(new[] { 0, 0 }), new(new[] { 1, 2 }), Layout, new[] { new ListDeltaRange(0, 0, 2) }, 7,
            out PreparedDeltaBody? rejected, out int writtenBytes));
        Assert.Null(rejected);
        Assert.Equal(69, writtenBytes); // Five outer bytes followed by one indivisible 64-byte child body.
        Assert.Equal(new[] { 1 }, SentinelOps.Prepared);
    }

    [Fact]
    public void OneAtomicLiteralCanOvershootButNoSubsequentLiteralRuns() {
        SentinelOps.Reset();
        SentinelOps.BasePayloadBytes = 64;
        SentinelOps.ThrowOnBase = 2;
        Assert.False(ListStateBody<int, SentinelOps>.TryEncodePlan(
            new([]), new(new[] { 1, 2 }), Layout, new[] { new ListDeltaRange(-1, 0, 2) }, 7,
            out PreparedDeltaBody? rejected, out int writtenBytes));
        Assert.Null(rejected);
        Assert.Equal(67, writtenBytes); // Three outer bytes followed by one indivisible 64-byte literal.
        Assert.Equal(new[] { 1 }, SentinelOps.Written);
    }

    [Fact]
    public void ErrorsBeforeTheCeilingPropagateInsteadOfPretendingToLose() {
        FrozenListState<int> prior = new(new[] { 0 }), current = new(new[] { 1 });
        ListDeltaRange[] source = [new(0, 0, 1)], literal = [new(-1, 0, 1)];
        SentinelOps.Reset();
        SentinelOps.ThrowOnEquals = 1;
        Assert.Throws<CodecFailureException>(() => ListStateBody<int, SentinelOps>.TryEncodePlan(
            prior, current, Layout, source, 100, out _));
        SentinelOps.Reset();
        SentinelOps.ThrowOnBase = 1;
        Assert.Throws<CodecFailureException>(() => ListStateBody<int, SentinelOps>.TryEncodePlan(
            prior, current, Layout, literal, 100, out _));
        SentinelOps.Reset();
        SentinelOps.ThrowOnDelta = 1;
        Assert.Throws<CodecFailureException>(() => ListStateBody<int, SentinelOps>.TryEncodePlan(
            prior, current, Layout, source, 100, out _));
        SentinelOps.Reset();
        SentinelOps.InconsistentDelta = true;
        Assert.Throws<InvalidOperationException>(() => ListStateBody<int, SentinelOps>.TryEncodePlan(
            prior, current, Layout, source, 100, out _));
        // A branch after the cutoff is intentionally unvisited, including its semantic error.
        Assert.False(ListStateBody<int, SentinelOps>.TryEncodePlan(prior, current, Layout, source, 5, out _));
    }

    [Fact]
    public void NoChangeSkipsThePlanOverrideAndAllElementEncoding() {
        SentinelOps.Reset();
        SentinelOps.ThrowOnBase = 1;
        SentinelOps.ThrowOnDelta = 1;
        PreparedDeltaBody unchanged = ListStateBody<int, SentinelOps>.PrepareDelta(
            new(new[] { 1, 2 }), new(new[] { 1, 2 }), Layout,
            planFactory: (prior, current, slot) => throw new CodecFailureException());
        Assert.False(unchanged.HasChanges);
        Assert.Equal(new byte[] { 2, 1, 0, 2 }, unchanged.Body.ToArray());
        Assert.Empty(SentinelOps.Prepared);
        Assert.Empty(SentinelOps.Written);
    }

    private static Fixture GetFixture(int scenario) {
        if (scenario == 0) {
            return new([1, 2, 3, 4, 5, 6], [1, 2, 7, 8, 3, 4, 9, 10],
                [new(0, 0, 2), new(-1, 2, 2), new(2, 4, 4)],
                [8, 1, 0, 2, 2, 2, 14, 16, 3, 2, 4, 3, 18, 4, 20, 0]);
        }
        if (scenario == 1) {
            return new([1, 2, 3, 4], [3, 9, 1, 2, 8, 4, 1, 2],
                [new(2, 0, 2), new(0, 2, 2), new(2, 4, 2), new(0, 6, 2)],
                [8, 3, 2, 2, 2, 18, 0, 1, 0, 2, 3, 2, 2, 1, 16, 0, 1, 0, 2]);
        }
        if (scenario == 2) {
            int[] current = new int[128];
            current[126] = 63;
            current[127] = 64;
            return new(new int[256], current, [new(127, 0, 128)],
                [128, 1, 3, 127, 128, 1, 127, 126, 128, 1, 128, 1, 0]);
        }
        if (scenario == 3) { return new(new int[256], new int[127], [new(128, 0, 127)], [127, 1, 128, 1, 127]); }
        if (scenario == 4) { return new([1], [], [], [0]); }
        return new([], [127, 128], [new(-1, 0, 2)], [2, 2, 2, 254, 1, 128, 2]);
    }

    private sealed record Fixture(int[] Prior, int[] Current, ListDeltaRange[] Ranges, byte[] Expected);
    private sealed class CodecFailureException : Exception { }

    private readonly struct SentinelOps : IStateOps<int> {
        public static readonly List<int> Compared = [], Written = [], Prepared = [];
        public static int? ThrowOnEquals, ThrowOnBase, ThrowOnDelta;
        public static int BasePayloadBytes, DeltaPayloadBytes;
        public static bool InconsistentDelta;

        public static void Reset() {
            Compared.Clear();
            Written.Clear();
            Prepared.Clear();
            ThrowOnEquals = ThrowOnBase = ThrowOnDelta = null;
            BasePayloadBytes = DeltaPayloadBytes = 0;
            InconsistentDelta = false;
        }
        public static bool StateEquals(in int left, in int right, DurableFieldInfo slot) {
            Compared.Add(right);
            if (right == ThrowOnEquals) { throw new CodecFailureException(); }
            return left == right;
        }
        public static void WriteBase(ref BareValueWriter writer, in int value, DurableFieldInfo slot) {
            Written.Add(value);
            if (value == ThrowOnBase) { throw new CodecFailureException(); }
            if (BasePayloadBytes > 0) {
                writer.WriteRawBytes(new byte[BasePayloadBytes]);
                return;
            }
            Int32StateOps.WriteBase(ref writer, in value, slot);
        }
        public static int ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => Int32StateOps.ReadBase(ref reader, slot);
        public static PreparedDeltaBody PrepareDelta(in int prior, in int current, DurableFieldInfo slot) {
            Prepared.Add(current);
            if (current == ThrowOnDelta) { throw new CodecFailureException(); }
            if (InconsistentDelta) { return new(false, []); }
            if (DeltaPayloadBytes > 0) { return new(true, new byte[DeltaPayloadBytes]); }
            return Int32StateOps.PrepareDelta(in prior, in current, slot);
        }
        public static int ApplyDelta(ref BareValueReader reader, in int prior, DurableFieldInfo slot) =>
            Int32StateOps.ApplyDelta(ref reader, in prior, slot);
        public static void VisitReferences(in int value, IStateReferenceVisitor visitor, DurableFieldInfo slot) { }
    }

    private readonly struct Empty { }
    private readonly struct EmptyOps : IStateOps<Empty> {
        public static int BaseCalls;
        public static bool StateEquals(in Empty left, in Empty right, DurableFieldInfo slot) => true;
        public static void WriteBase(ref BareValueWriter writer, in Empty value, DurableFieldInfo slot) { BaseCalls++; }
        public static Empty ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => default;
        public static PreparedDeltaBody PrepareDelta(in Empty prior, in Empty current, DurableFieldInfo slot) => new(false, []);
        public static Empty ApplyDelta(ref BareValueReader reader, in Empty prior, DurableFieldInfo slot) => throw new InvalidDataException();
        public static void VisitReferences(in Empty value, IStateReferenceVisitor visitor, DurableFieldInfo slot) { }
    }
}
