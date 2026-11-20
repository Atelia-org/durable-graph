using Atelia.Binary;
using System.Buffers;
using System.Diagnostics;
using Xunit.Abstractions;

namespace Atelia.Text.Tests;

public sealed class TextBodyCodecTests(ITestOutputHelper output) {
    [Fact]
    public void BaseGoldenBytesPreserveAllUtf16UnitsAndUseASoftEstimate() {
        string text = "A\0\r\n\u4e2d\ud83d\ude00\ud800x\udfff";
        FrozenText state = new TextEditor(text).CaptureState(null);
        byte[] bytes = TextBodyCodec.PrepareBase(state).Body.ToArray();
        Assert.Equal(new byte[] { 10, 20, 65, 0, 0, 0, 13, 0, 10, 0, 0x2d, 0x4e,
            0x3d, 0xd8, 0, 0xde, 0, 0xd8, 120, 0, 0xff, 0xdf }, bytes);
        Assert.Equal(bytes.Length, TextBodyCodec.EstimateBaseSize(state));
        Assert.Equal(text, ReadBase(bytes).Content.ToString());
        Assert.Equal(new byte[] { 0 }, TextBodyCodec.PrepareBase(new TextEditor().CaptureState(null)).Body.ToArray());
        foreach (int length in new[] { 127, 128, 16383, 16384 }) {
            FrozenText boundary = new TextEditor(new string('x', length)).CaptureState(null);
            byte[] encoded = TextBodyCodec.PrepareBase(boundary).Body.ToArray();
            BareValueReader reader = new(encoded);
            Assert.Equal((uint)length, reader.ReadVarUInt32());
            Assert.Equal(BareValueEncoding.MeasureVarUInt32((uint)length), reader.ConsumedCount);
            Assert.Equal(new string('x', length), ReadBase(encoded).Content.ToString());
            Assert.Equal(BareValueEncoding.MeasureVarUInt32((uint)length) + 1 + 2L * length,
                TextBodyCodec.EstimateBaseSize(boundary));
            Assert.True(TextBodyCodec.EstimateBaseSize(boundary) > encoded.Length);
        }
        // A UTF-16 segment with a two-byte Bare header is underestimated, so the
        // planner estimate must never be used as an exact buffer or installed H.
        FrozenText chinese = new TextEditor(new string('中', 64)).CaptureState(null);
        Assert.Equal(130, TextBodyCodec.EstimateBaseSize(chinese));
        Assert.Equal(131, TextBodyCodec.PrepareBase(chinese).Body.Length);
    }

    [Fact]
    public void EachCapturedPhysicalPieceHasItsOwnBareEncoding() {
        // Construct UTF-16 strings inside the test: runner transport of Theory
        // string arguments may replace isolated surrogates before execution.
        (string[] Segments, byte[] Golden)[] cases = [
            ([], [0]),
            (["A"], [1, 3, 65]),
            (["AB"], [2, 5, 65, 66]),
            (["A", "B"], [2, 3, 65, 3, 66]),
            (["A", "中"], [2, 3, 65, 2, 0x2d, 0x4e]),
            (["\ud83d", "\ude00"], [2, 2, 0x3d, 0xd8, 2, 0, 0xde]),
        ];
        foreach ((string[] segments, byte[] golden) in cases) {
            TextEditor editor = Fragmented(segments);
            FrozenText frozen = editor.CaptureState(null);
            Assert.Equal(segments.Length, frozen.Content.View.PieceCount);
            editor.Replace(editor.Length, 0, "later");
            Assert.Equal(golden, TextBodyCodec.PrepareBase(frozen).Body.ToArray());
            Assert.Equal(string.Concat(segments), ReadBase(golden).Content.ToString());
        }
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 65, 0 })] // Legal nonpreferred UTF-16 representation of A.
    [InlineData(new byte[] { 0x81, 0, 3, 65 })] // Redundant total-length varint.
    [InlineData(new byte[] { 1, 0x83, 0, 65 })] // Redundant Bare header.
    public void BaseRetainsBinaryAcceptanceOfNonpreferredAndRedundantRepresentations(byte[] body) =>
        Assert.Equal("A", ReadBase(body).Content.ToString());

    [Fact]
    public void BaseChoosesEncodingFromThePhysicalSliceRatherThanItsWholeBacking() {
        TextEditor editor = new("\ud800AB\udfff");
        editor.Replace(0, 1, string.Empty);
        editor.Replace(2, 1, string.Empty);
        FrozenText frozen = editor.CaptureState(null);
        TextPiece piece = Assert.Single(frozen.Content.View.Pieces);
        Assert.Equal(1, piece.Offset);
        Assert.Equal(4, piece.Text.Length);
        Assert.Equal(new byte[] { 2, 5, 65, 66 }, TextBodyCodec.PrepareBase(frozen).Body.ToArray());
    }

    [Theory]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(4096)]
    public void ManySegmentsRestoreOneFlatBaseAndCopyAcrossFormerBoundaries(int length) {
        string expected = new(Enumerable.Range(0, length).Select(i => "a中\ud83d\ude00\ud800\0\r\n"[i % 8]).ToArray());
        TextEditor fragmented = Fragmented(expected.Select(unit => unit.ToString()).ToArray());
        Assert.Equal(length, fragmented.CaptureState(null).Content.View.PieceCount);
        byte[] body = TextBodyCodec.PrepareBase(fragmented.CaptureState(null)).Body.ToArray();
        BareValueReader header = new(body);
        Assert.Equal((uint)length, header.ReadVarUInt32());
        FrozenText restored = ReadBase(body);
        Assert.Equal(expected, restored.Content.ToString());
        Assert.Equal(1, restored.Content.View.PieceCount);
        Assert.Equal(expected, ReadBase(TextBodyCodec.PrepareBase(new TextEditor(expected).CaptureState(null)).Body.ToArray()).Content.ToString());
        TextEditor continued = TextEditor.Restore(restored);
        continued.Replace(length / 2, 1, "Z");
        byte[] delta = TextBodyCodec.PrepareDelta(restored, continued.CaptureState(null)).Body.ToArray();
        Assert.Equal((byte)1, delta[checked((int)BareValueEncoding.MeasureVarUInt32((uint)length))]);
        Assert.Equal(expected[..(length / 2)] + "Z" + expected[(length / 2 + 1)..], Apply(restored, delta).Content.ToString());
    }

    [Fact]
    public void DeltaGoldenBytesAndPhysicalFragmentationHaveOneCanonicalEncoding() {
        FrozenText prior = new TextEditor("ab").CaptureState(null);
        TextEditor one = TextEditor.Restore(prior), two = TextEditor.Restore(prior);
        one.Replace(1, 0, "XY");
        two.Replace(1, 0, "X");
        two.Replace(2, 0, "Y");
        byte[] expected = [4, 1, 0, 1, 2, 2, 88, 0, 89, 0, 1, 1, 1];
        Assert.Equal(expected, TextBodyCodec.PrepareDelta(prior, one.CaptureState(null)).Body.ToArray());
        Assert.Equal(expected, TextBodyCodec.PrepareDelta(prior, two.CaptureState(null)).Body.ToArray());
        Assert.Equal("aXYb", Apply(prior, expected).Content.ToString());
        Assert.Equal("ab", prior.Content.ToString());
    }

    [Fact]
    public void NetZeroAndMismatchedSourceUseActualPreviousContent() {
        FrozenText prior = new TextEditor("abc").CaptureState(null);
        TextEditor editor = TextEditor.Restore(prior);
        editor.Replace(1, 1, "X");
        editor.Replace(1, 1, "b");
        var unchanged = TextBodyCodec.PrepareDelta(prior, editor.CaptureState(null));
        Assert.False(unchanged.HasChanges);
        Assert.Equal(new byte[] { 3, 1, 0, 3 }, unchanged.Body.ToArray());
        Assert.False(TextBodyCodec.PrepareDelta(prior, new TextEditor("abc").CaptureState(null)).HasChanges);

        editor.Replace(1, 0, "X");
        TestReceipt.Save(editor);
        editor.Replace(2, 0, "Y");
        var fallback = TextBodyCodec.PrepareDelta(prior, editor.CaptureState(null));
        Assert.True(fallback.HasChanges);
        Assert.Equal((byte)2, fallback.Body[1]); // Full New, not Copy against another graph's baseline.
        Assert.Equal("aXYbc", Apply(prior, fallback.Body.ToArray()).Content.ToString());
        Assert.Equal(new byte[] { 0 }, TextBodyCodec.PrepareDelta(prior, new TextEditor().CaptureState(null)).Body.ToArray());
    }

    [Fact]
    public void EqualLengthDeltaChecksDisplacedCopiesEvenWhenTheirSourceIsExact() {
        foreach (string original in new[] { "aaaa", "abaa" }) {
            TextEditor editor = new();
            foreach (char c in original) { editor.Replace(editor.Length, 0, c.ToString()); }
            FrozenText prior = TestReceipt.Save(editor);
            editor.Replace(0, 1, "");
            editor.Replace(editor.Length, 0, "a");
            FrozenText edited = editor.CaptureState(null);
            Assert.Same(prior.ContentToken, edited.SourceToken);
            var delta = TextBodyCodec.PrepareDelta(prior, edited);
            Assert.Equal(original != "aaaa", delta.HasChanges);
            Assert.Equal(original[1..] + "a", Apply(prior, delta.Body.ToArray()).Content.ToString());
        }
    }

    [Fact]
    public void SamePositionCopiesCannotProveEqualityAgainstAnotherGraphsText() {
        FrozenText source = new TextEditor("abcde").CaptureState(null);
        TextEditor editor = TextEditor.Restore(source);
        editor.Replace(0, 1, "x");
        var foreign = new TextEditor("xbcQe").CaptureState(null);
        var delta = TextBodyCodec.PrepareDelta(foreign, editor.CaptureState(null));
        Assert.True(delta.HasChanges);
        Assert.Equal((byte)2, delta.Body[1]); // The literal prefix matches, but this source differs inside a Copy.
        Assert.Equal("xbcde", Apply(foreign, delta.Body.ToArray()).Content.ToString());
    }

    [Fact]
    public void ReaderIteratesDeepChainAndColdHydrateQualifiesFirstContinuation() {
        TextEditor editor = new("abcdefgh");
        FrozenText previous = TestReceipt.Save(editor);
        List<byte[]> bodies = [TextBodyCodec.PrepareBase(previous).Body.ToArray()];
        for (int index = 0; index < 128; index++) {
            editor.Replace(index % editor.Length, 1, index % 2 == 0 ? "X" : "Y");
            FrozenText current = TestReceipt.Save(editor);
            bodies.Add(TextBodyCodec.PrepareDelta(previous, current).Body.ToArray());
            previous = current;
        }
        FrozenText decoded = Replay(bodies);
        Assert.Equal(editor.ToString(), decoded.Content.ToString());
        TextEditor restored = TextEditor.Restore(decoded);
        Assert.Same(decoded.ContentToken, restored.CaptureState(null).SourceToken);
        restored.Replace(3, 0, "\ud800");
        var continuation = TextBodyCodec.PrepareDelta(decoded, restored.CaptureState(null));
        Assert.Equal((byte)1, continuation.Body[1]);
        Assert.Equal(restored.ToString(), Apply(decoded, continuation.Body.ToArray()).Content.ToString());
    }

    [Fact]
    public void RandomEditsRoundtripEveryAcceptedBaselineAndPreserveOldSnapshots() {
        Random random = new(53173);
        TextEditor editor = new("a\ud800b\r\n\udfff");
        string oracle = editor.ToString();
        FrozenText previous = TestReceipt.Save(editor);
        List<(TextSnapshot Snapshot, string Expected)> held = [];
        for (int turn = 0; turn < 250; turn++) {
            int start = random.Next(oracle.Length + 1);
            int removed = random.Next(Math.Min(oracle.Length - start, 5) + 1);
            string added = new(Enumerable.Range(0, random.Next(5)).Select(_ => "ax\0\r\n\ud800\udfff"[random.Next(7)]).ToArray());
            editor.Replace(start, removed, added);
            oracle = oracle[..start] + added + oracle[(start + removed)..];
            FrozenText current = TestReceipt.Save(editor);
            var delta = TextBodyCodec.PrepareDelta(previous, current);
            Assert.Equal(previous.Content.ToString() != oracle, delta.HasChanges);
            Assert.Equal(oracle, Apply(previous, delta.Body.ToArray()).Content.ToString());
            if (turn % 20 == 0) { held.Add((editor.Snapshot(), oracle)); }
            previous = current;
        }
        foreach (var item in held) { Assert.Equal(item.Expected, item.Snapshot.ToString()); }
    }

    public static IEnumerable<object[]> MalformedDeltas() {
        yield return [new byte[] { 1, 3 }]; // Invalid opcode.
        yield return [new byte[] { 1, 1, 0, 0 }]; // Zero Copy.
        yield return [new byte[] { 1, 2, 0 }]; // Zero New.
        yield return [new byte[] { 1, 1, 3, 1 }]; // Source outside predecessor.
        yield return [new byte[] { 2, 1, 1, 2 }]; // Source end outside predecessor.
        yield return [new byte[] { 1, 1, 0, 2 }]; // Result overflow.
        yield return [new byte[] { 2, 1, 0, 1, 1, 1, 1 }]; // Adjacent continuous Copies.
        yield return [new byte[] { 2, 2, 1, 65, 0, 2, 1, 66, 0 }]; // Adjacent New.
        yield return [new byte[] { 1, 2, 1, 65 }]; // Truncated code unit.
        yield return [new byte[] { 1, 1, 0 }]; // Truncated Copy.
        yield return [new byte[] { 2, 1, 0, 1 }]; // Underfilled result.
        yield return [new byte[] { 0, 0 }]; // Trailing byte.
        yield return [new byte[] { 0xff, 0xff, 0xff, 0xff, 0x0f }]; // Length overflow.
        yield return [new byte[] { 1, 1, 0xff, 0xff, 0xff, 0xff, 0x0f, 1 }]; // Source overflow.
    }

    [Theory]
    [MemberData(nameof(MalformedDeltas))]
    public void InvalidDeltaIsRejectedBeforeProducingContent(byte[] bytes) {
        FrozenText previous = new TextEditor("ab").CaptureState(null);
        Exception? error = Record.Exception(() => Apply(previous, bytes));
        Assert.True(error is InvalidDataException or EndOfStreamException, error?.ToString() ?? "Body was accepted.");
        Assert.Equal("ab", previous.Content.ToString());
    }

    public static IEnumerable<object[]> MalformedBases() {
        yield return [new byte[] { }, typeof(EndOfStreamException)];
        yield return [new byte[] { 0x80 }, typeof(EndOfStreamException)];
        yield return [new byte[] { 0, 0 }, typeof(InvalidDataException)]; // Empty body cannot have any tail.
        yield return [new byte[] { 1 }, typeof(InvalidDataException)]; // N exceeds available bytes, before allocation.
        yield return [new byte[] { 0xff, 0xff, 0xff, 0xff, 0x0f }, typeof(InvalidDataException)];
        yield return [new byte[] { 0x80, 0x80, 0x40, 3, 65 }, typeof(InvalidDataException)]; // Huge N, tiny body.
        yield return [new byte[] { 1, 0 }, typeof(InvalidDataException)]; // Empty UTF-16 segment.
        yield return [new byte[] { 1, 1 }, typeof(InvalidDataException)]; // Empty UTF-8 segment.
        yield return [new byte[] { 1, 3, 65, 0 }, typeof(InvalidDataException)]; // Tail after N has been filled.
        yield return [new byte[] { 1, 3, 65, 1 }, typeof(InvalidDataException)];
        yield return [new byte[] { 1, 3, 65, 3, 66 }, typeof(InvalidDataException)]; // Valid extra segment.
        yield return [new byte[] { 1, 5, 65, 66 }, typeof(InvalidDataException)]; // UTF-8 output too long.
        yield return [new byte[] { 1, 4, 65, 0, 66, 0 }, typeof(InvalidDataException)]; // UTF-16 output too long.
        yield return [new byte[] { 2, 3, 65 }, typeof(InvalidDataException)]; // Valid segment underfills N.
        yield return [new byte[] { 1, 3, 0xff }, typeof(InvalidDataException)];
        yield return [new byte[] { 1, 5, 0xc0, 0xaf }, typeof(InvalidDataException)]; // Overlong UTF-8.
        yield return [new byte[] { 1, 3, 0xc2 }, typeof(InvalidDataException)]; // Complete byte payload, incomplete UTF-8.
        yield return [new byte[] { 1, 0x80 }, typeof(EndOfStreamException)];
        yield return [new byte[] { 1, 5, 65 }, typeof(EndOfStreamException)]; // Truncated byte payload.
        yield return [new byte[] { 1, 2, 65 }, typeof(EndOfStreamException)];
    }

    [Theory]
    [MemberData(nameof(MalformedBases))]
    public void InvalidBaseIsRejectedWithoutDeliveringContent(byte[] bytes, Type expectedError) {
        Exception? error = Record.Exception(() => ReadBase(bytes));
        Assert.NotNull(error);
        Assert.Equal(expectedError, error.GetType());
    }

    [Fact]
    public void NoChangeDeltaOnTheSameFrozenContentDoesNotMaterializeTheLargeBase() {
        FrozenText state = new TextEditor(new string('x', 65536)).CaptureState(null);
        _ = TextBodyCodec.PrepareDelta(state, state);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var delta = TextBodyCodec.PrepareDelta(state, state);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.False(delta.HasChanges);
        Assert.True(bytes < state.Content.Length, $"NoChange preparation allocated {bytes} bytes.");
    }

    [Fact]
    public void CopyRangesCanReorderAndRepeatWithoutRecursiveViewsOrUnboundedPieces() {
        FrozenText prior = new TextEditor("abcd").CaptureState(null);
        Assert.Equal("dbca", Apply(prior, [4, 1, 3, 1, 1, 1, 2, 1, 0, 1]).Content.ToString());
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        writer.WriteVarUInt32(5000);
        for (int index = 0; index < 5000; index++) {
            writer.WriteByte(1); writer.WriteVarUInt32(0); writer.WriteVarUInt32(1);
        }
        FrozenText repeated = Apply(prior, buffer.WrittenSpan.ToArray());
        Assert.Equal(new string('a', 5000), repeated.Content.ToString());
        Assert.Equal(1, repeated.Content.View.PieceCount); // Bounded fallback before collecting 5000 descriptors.
        Assert.Equal("abcd", prior.Content.ToString());
    }

    [Fact]
    public void FinalColdStateReleasesDisproportionateSourceBacking() {
        FrozenText prior = new TextEditor(new string('x', 65536)).CaptureState(null);
        byte[] encoded = TextBodyCodec.PrepareBase(prior).Body.ToArray();
        FrozenText decoded = Replay([encoded, [1, 1, 0, 1]]);
        Assert.Equal("x", decoded.Content.ToString());
        Assert.Equal(1, decoded.Content.View.LargestBacking);
    }

    [Theory]
    [InlineData(1048576, 32, true)]
    [InlineData(65536, 1024, false)]
    public void ColdChainAllocationTracksLiteralsAndPiecesRatherThanVersionTimesLength(int length, int depth, bool spread) {
        byte[][] bodies = new byte[depth + 1][];
        bodies[0] = TextBodyCodec.PrepareBase(new TextEditor(new string('a', length)).CaptureState(null)).Body.ToArray();
        for (int turn = 0; turn < depth; turn++) {
            int position = spread ? 1 + 17 * turn : 7;
            bodies[turn + 1] = ReplacementBody(length, position, turn % 2 == 0 ? 'b' : 'c');
        }
        _ = Replay([new byte[] { 0 }]);
        long start = GC.GetAllocatedBytesForCurrentThread();
        long time = Stopwatch.GetTimestamp();
        FrozenText decoded = Replay(bodies);
        double elapsed = Stopwatch.GetElapsedTime(time).TotalMilliseconds;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        output.WriteLine($"Text cold replay: N={length}, depth={depth}, allocated={allocated}, elapsedMs={elapsed:F3}, pieces={decoded.Content.View.PieceCount}");
        // A loose mechanism guard, not a machine timing SLA: flattening every delta would
        // allocate >=128 MiB for either case, while literal/slice replay stays well below 8 MiB.
        Assert.True(allocated < 8L * 1024 * 1024, $"Cold replay allocated {allocated} bytes.");
        string text = decoded.Content.ToString();
        Assert.Equal(length, text.Length);
        if (spread) {
            for (int turn = 0; turn < depth; turn++) { Assert.Equal(turn % 2 == 0 ? 'b' : 'c', text[1 + 17 * turn]); }
        } else { Assert.Equal('c', text[7]); }
    }

    private static byte[] ReplacementBody(int length, int position, char value) {
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        writer.WriteVarUInt32((uint)length);
        writer.WriteByte(1); writer.WriteVarUInt32(0); writer.WriteVarUInt32((uint)position);
        writer.WriteByte(2); writer.WriteVarUInt32(1);
        writer.WriteByte((byte)value); writer.WriteByte((byte)(value >> 8));
        writer.WriteByte(1); writer.WriteVarUInt32((uint)(position + 1)); writer.WriteVarUInt32((uint)(length - position - 1));
        return buffer.WrittenSpan.ToArray();
    }

    private static FrozenText ReadBase(byte[] bytes) => TextBodyCodec.ReadBase(bytes);

    private static TextEditor Fragmented(params string[] segments) {
        TextEditor editor = new();
        foreach (string segment in segments) { editor.Replace(editor.Length, 0, segment); }
        return editor;
    }

    private static FrozenText Apply(FrozenText previous, byte[] bytes) {
        BareValueReader reader = new(bytes);
        return TextBodyCodec.ApplyDelta(ref reader, previous);
    }

    private static FrozenText Replay(IReadOnlyList<byte[]> bodies) {
        FrozenText state = ReadBase(bodies[0]);
        for (int index = 1; index < bodies.Count; index++) { state = Apply(state, bodies[index]); }
        return TextBodyCodec.FinishReplay(state);
    }
}
