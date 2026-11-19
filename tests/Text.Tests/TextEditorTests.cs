using Atelia.Binary;
using System.Text;

namespace Atelia.Text.Tests;

public sealed class TextEditorTests {
    [Fact]
    public void OriginalCoordinatesAndSamePointInsertionsPublishOneResult() {
        TextEditor text = new("abcdef");
        TextSnapshot before = text.Snapshot();
        Assert.True(text.TryApplyEdits(before, [
            new(4, 1, "Z"), new(1, 2, "Q"), new(1, 0, "x"),
            new(3, 0, "!"), new(1, 0, "y"), new(6, 0, "end"), new(2, 0, "")
        ], out var failure));
        Assert.Equal(TextEditFailureKind.None, failure.Kind);
        Assert.Null(failure.EditIndex);
        Assert.Null(failure.ConflictingEditIndex);
        Assert.Equal("axyQ!dZfend", text.ToString());
        Assert.Equal("abcdef", before.ToString());
        TextSnapshot current = text.Snapshot();
        Assert.True(text.TryApplyEdits(current, [], out _));
        Assert.True(text.TryApplyEdits(current, [new(2, 0, "")], out _));
        text.Replace(0, 0, "");
        Assert.Same(current.ContentToken, text.CaptureState(null).ContentToken);
    }

    [Fact]
    public void ArgumentErrorsDoNotChangeContentOrDestination() {
        TextEditor text = new("abcdef");
        var snapshot = text.Snapshot();
        Assert.Throws<ArgumentNullException>("expected", () => text.TryApplyEdits(null!, [], out _));
        Assert.Throws<ArgumentNullException>("edits", () => text.TryApplyEdits(snapshot, null!, out _));
        Assert.Throws<ArgumentNullException>(() => new TextEditor(null!));
        Assert.Throws<ArgumentNullException>(() => text.Replace(0, 1, null!));
        Assert.Throws<ArgumentOutOfRangeException>("length", () => text.Replace(0, -1, ""));
        Assert.Throws<ArgumentOutOfRangeException>("start", () => text.CopyTo(7, Span<char>.Empty));
        Assert.Throws<ArgumentOutOfRangeException>("destination", () => text.CopyTo(5, new char[2]));
        StringBuilder destination = new("prefix");
        Assert.Throws<ArgumentOutOfRangeException>("length", () => text.AppendTo(destination, 0, 7));
        Assert.Throws<ArgumentOutOfRangeException>("length", () => snapshot.AppendTo(destination, 0, 7));
        Assert.Throws<ArgumentOutOfRangeException>("destination", () => snapshot.CopyTo(5, new char[2]));
        Assert.Equal("prefix", destination.ToString());
        Assert.Equal("abcdef", text.ToString());
    }

    [Theory]
    [InlineData(-1, 0, "", TextEditFailureKind.InvalidStart)]
    [InlineData(7, 0, "", TextEditFailureKind.InvalidStart)]
    [InlineData(1, -1, "", TextEditFailureKind.InvalidLength)]
    [InlineData(5, int.MaxValue, "", TextEditFailureKind.InvalidLength)]
    [InlineData(0, 0, null, TextEditFailureKind.MissingReplacement)]
    public void InvalidBatchEntryReportsOriginalIndexAndPublishesNothing(
        int start, int length, string? replacement, TextEditFailureKind kind) {
        TextEditor text = new("abcdef");
        TextSnapshot before = text.Snapshot();
        TextEdit append = new(6, 0, "!");
        Assert.False(text.TryApplyEdits(before, [append, new(start, length, replacement!)], out var failure));
        Assert.Equal(kind, failure.Kind);
        Assert.Equal(1, failure.EditIndex);
        Assert.Null(failure.ConflictingEditIndex);
        Assert.Same(before.View, text.Snapshot().View);
        Assert.Equal("abcdef", text.ToString());
    }

    [Theory]
    [InlineData(3, 0)] // Insertion strictly inside [1,4).
    [InlineData(3, 2)] // Partial overlap.
    [InlineData(1, 2)] // Two replacements at the same start.
    public void ConflictIdentifiesBothOriginalIndicesAndPublishesNothing(int start, int length) {
        TextEditor text = new("abcdef");
        TextSnapshot before = text.Snapshot();
        TextEdit[] batch = [new(start, length, "x"), new(6, 0, "!"), new(1, 3, "")];
        Assert.False(text.TryApplyEdits(before, batch, out var failure));
        Assert.Equal(TextEditFailureKind.ConflictingEdits, failure.Kind);
        Assert.True((failure.EditIndex, failure.ConflictingEditIndex) is (0, 2) or (2, 0));
        Assert.Same(before.View, text.Snapshot().View);
    }

    [Fact]
    public void TotalLengthOverflowIsReportedBeforeConstructingTheResult() {
        TextEditor text = new();
        TextSnapshot before = text.Snapshot();
        TextEdit insert = new(0, 0, new string('x', 1024 * 1024));
        Assert.False(text.TryApplyEdits(before, Enumerable.Repeat(insert, 2048).ToArray(), out var failure));
        Assert.Equal(TextEditFailureKind.ResultTooLong, failure.Kind);
        Assert.Same(before.View, text.Snapshot().View);
    }

    [Fact]
    public void StaleCoordinatesAreRejectedEvenWhenTheMatchedTextStillAgrees() {
        TextEditor text = new("甲北门 乙北门");
        TextSnapshot before = text.Snapshot();
        int at = before.IndexOf("北门", 3);
        text.Replace(0, 0, "丙北门 ");
        Assert.Equal(at, text.Snapshot().IndexOf("北门", at));
        Assert.False(text.TryApplyEdits(before, [new(at, 2, "南门")], out var failure));
        Assert.Equal(TextEditFailureKind.StaleSnapshot, failure.Kind);
        Assert.Null(failure.EditIndex);
        Assert.Null(failure.ConflictingEditIndex);
        Assert.Equal("丙北门 甲北门 乙北门", text.ToString());

        text.Replace(0, 4, "");
        Assert.Equal(before.ToString(), text.ToString());
        // A stale request must not run user code, even after a change and reversal.
        var unreadable = new ReentrantEdits(() => throw new InvalidOperationException(), true);
        Assert.False(text.TryApplyEdits(before, unreadable, out failure));
        Assert.Equal(TextEditFailureKind.StaleSnapshot, failure.Kind);
    }

    [Fact]
    public void EditorsRestoredFromOneBaselineRejectEachOthersSnapshots() {
        FrozenText baseline = new TextEditor("abc").CaptureState(null);
        TextEditor left = TextEditor.Restore(baseline), right = TextEditor.Restore(baseline);
        TextSnapshot expected = left.Snapshot();
        Assert.Same(expected.ContentToken, right.Snapshot().ContentToken);
        Assert.False(right.TryApplyEdits(expected, [new(1, 1, "X")], out var failure));
        Assert.Equal(TextEditFailureKind.StaleSnapshot, failure.Kind);
        Assert.Equal("abc", right.ToString());
        Assert.True(left.TryApplyEdits(expected, [new(1, 1, "X")], out _));
        Assert.Equal("aXc", left.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReentrantCountOrIndexerCannotOverwriteTheNestedEdit(bool mutateInCount) {
        TextEditor text = new("abc");
        IReadOnlyList<TextEdit> input = new ReentrantEdits(() => text.Replace(0, 0, "!"), mutateInCount);
        Assert.False(text.TryApplyEdits(text.Snapshot(), input, out var failure));
        Assert.Equal(TextEditFailureKind.StaleSnapshot, failure.Kind);
        Assert.Equal("!abc", text.ToString());
    }

    [Theory]
    [InlineData(8, true)]
    [InlineData(256, false)]
    [InlineData(8, null)]
    public void SaveBeforePublicationUsesTheReboundSourceForRetainedRanges(int length, bool? saveInCount) {
        TextEditor text = new(new string('a', length));
        text.Replace(text.Length, 0, "bc");
        TextSnapshot before = text.Snapshot();
        TestReceipt receipt = new();
        FrozenText saved = text.CaptureState(receipt);
        void Save() => receipt.Publish();
        IReadOnlyList<TextEdit> input;
        if (saveInCount.HasValue) { input = new ReentrantEdits(Save, saveInCount.Value); }
        else {
            Save();
            input = [new(1, 1, "X")];
        }
        Assert.True(text.TryApplyEdits(before, input, out _));
        Assert.Equal("aX" + new string('a', length - 2) + "bc", text.ToString());
        var state = text.CaptureState(null);
        AssertPlan(state, saved);
        BareValueReader delta = new(TextBodyCodec.PrepareDelta(saved, state).Body);
        Assert.Equal((uint)before.Length, delta.ReadVarUInt32());
        AssertCopy(ref delta, 0, 1);
        Assert.Equal((byte)2, delta.ReadByte());
        Assert.Equal(1u, delta.ReadVarUInt32());
        Assert.Equal(new byte[] { (byte)'X', 0 }, delta.ReadRawBytes(2).ToArray());
        AssertCopy(ref delta, 2, (uint)before.Length - 2);
        delta.EnsureFullyConsumed();
    }

    private sealed class ReentrantEdits(Action action, bool actInCount) : IReadOnlyList<TextEdit> {
        public int Count {
            get {
                if (actInCount) { action(); }
                return 1;
            }
        }
        public TextEdit this[int index] {
            get {
                if (!actInCount) { action(); }
                return new(1, 1, "X");
            }
        }
        public IEnumerator<TextEdit> GetEnumerator() => throw new NotSupportedException();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void DecoderBorrowsBoundedPhysicalSlicesWithoutRetainingSourceViews() {
        TextEditor source = new("ab");
        source.Replace(2, 0, "\uD83D");
        source.Replace(3, 0, "\uDE00xyz");
        TextPieceView view = source.Snapshot().View;
        List<TextPiece> pieces = [];
        Assert.True(view.TryAppendRangeTo(pieces, 1, 4, maxPieces: 3));
        Assert.Equal(3, pieces.Count);
        Assert.All(pieces, piece => Assert.Equal(-1, piece.BaseOffset));
        TextSnapshot decoded = new(TextPieceView.FromOwnedPieces(pieces, 4));
        Assert.Equal("b\uD83D\uDE00x", decoded.ToString());
        Assert.NotSame(view.ContentToken, decoded.ContentToken);
        FrozenText baseline = new(decoded);
        TextEditor editor = TextEditor.Restore(baseline);
        editor.Replace(0, 1, "B");
        AssertPlan(editor.CaptureState(null), baseline);
        Assert.Equal("b\uD83D\uDE00x", decoded.ToString());

        List<TextPiece> capped = [];
        Assert.False(view.TryAppendRangeTo(capped, 0, view.Length, maxPieces: 2));
        Assert.Equal(2, capped.Count);
        List<TextPiece> contiguous = [];
        Assert.True(view.TryAppendRangeTo(contiguous, 0, 1, maxPieces: 1));
        Assert.True(view.TryAppendRangeTo(contiguous, 1, 1, maxPieces: 1));
        Assert.Single(contiguous); // At the bound, a physical merge remains legal.
        Assert.Equal("ab", new TextSnapshot(TextPieceView.FromOwnedPieces(contiguous, 2)).ToString());
        Assert.Throws<ArgumentException>(() => TextPieceView.FromOwnedPieces([], 1));
        Assert.Throws<ArgumentException>(() => TextPieceView.FromOwnedPieces([new("x", 0, 1, 0)], 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => view.TryAppendRangeTo([], 2, view.Length, 3));
    }

    [Fact]
    public void FragmentedUtf16ReadsAppendAndRetainedChunksPreserveEveryUnit() {
        TextEditor text = new("a\uD83D");
        text.Replace(text.Length, 0, "\uDE00\0");
        text.Replace(text.Length, 0, "\uD800中\r");
        text.Replace(text.Length, 0, "\n\uDC00z");
        const string expected = "a\uD83D\uDE00\0\uD800中\r\n\uDC00z";
        TextSnapshot frozen = text.Snapshot();
        ReadOnlyMemory<char>[] chunks = frozen.GetChunks().ToArray();
        Assert.True(frozen.View.PieceCount > 1);
        AssertAllRanges(text, expected);
        for (int start = 0; start <= expected.Length; start++) {
            for (int length = 0; length <= expected.Length - start; length++) {
                char[] data = new char[length];
                frozen.CopyTo(start, data);
                Assert.Equal(expected.Substring(start, length), new string(data));
                StringBuilder output = new("!");
                frozen.AppendTo(output, start, length);
                Assert.Equal("!" + expected.Substring(start, length), output.ToString());
            }
        }
        TestReceipt.Save(text);
        text.Replace(0, text.Length, "different");
        Assert.Equal(expected, frozen.ToString());
        Assert.Equal(expected, string.Concat(chunks.Select(chunk => chunk.ToString())));
    }

    [Fact]
    public void OrdinalSearchCrossesPiecesAndHandlesRepeatedPrefixesAndEmptyNeedles() {
        TextEditor text = new("aa");
        foreach (string part in new[] { "aaa", "b\uD83D", "\uDE00\0a", "aaab", "\uD800\r", "\nend" }) {
            text.Replace(text.Length, 0, part);
        }
        TextSnapshot snapshot = text.Snapshot();
        string expected = text.ToString();
        string[] needles = ["", "a", "aaaaab", "aaaab", "aab", "not there", "\uD83D\uDE00", "\0a", "\uD800", "\r\n", "end", expected];
        foreach (string needle in needles) {
            for (int start = 0; start <= expected.Length; start++) {
                Assert.Equal(expected.IndexOf(needle, start, StringComparison.Ordinal), snapshot.IndexOf(needle, start));
            }
        }
        Assert.Throws<ArgumentNullException>(() => snapshot.IndexOf(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.IndexOf("", -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.IndexOf("", snapshot.Length + 1));
        Assert.Equal(0, new TextEditor().Snapshot().IndexOf(""));
        Assert.Equal(-1, new TextEditor().Snapshot().IndexOf("a"));
    }

    [Fact]
    public void AcceptedReceiptCompactsOnlyItsCapturedViewAndPreservesTheContentIdentity() {
        TextEditor text = new("abcdefgh");
        text.Replace(1, 1, "X");
        TestReceipt receipt = new();
        FrozenText captured = text.CaptureState(receipt);
        TextSnapshot before = text.Snapshot();
        Assert.Same(before.View, captured.Content.View);
        Assert.Null(text.CaptureState(null).SourceToken);
        receipt.Publish();
        Assert.Same(before.View, text.Snapshot().View); // An ordinary snapshot shares the current view.
        FrozenText accepted = text.CaptureState(null);
        Assert.Same(captured.ContentToken, accepted.ContentToken);
        Assert.Same(captured.ContentToken, accepted.SourceToken);
        Assert.Equal(1, accepted.Content.View.PieceCount);

        // The same version is already rebound, so its next publication requires no physical work.
        TestReceipt.Save(text);
        Assert.Same(accepted.Content.View, text.CaptureState(null).Content.View);

        text.Replace(0, 1, "!");
        TestReceipt obsolete = new();
        FrozenText stale = text.CaptureState(obsolete);
        text.Replace(0, 1, "a");
        text.Replace(0, 1, "!");
        obsolete.Publish();
        Assert.Equal(stale.Content.ToString(), text.ToString());
        Assert.NotSame(stale.ContentToken, text.CaptureState(null).ContentToken);
        Assert.Same(accepted.ContentToken, text.CaptureState(null).SourceToken);
    }

    [Fact]
    public void ReadOnlyCapturePreservesAStateReceiptAndDiscardDoesNotAdvanceTheSource() {
        TextEditor editor = new("left right");
        editor.Replace(4, 1, ":");
        TestReceipt receipt = new();
        FrozenText state = editor.CaptureState(receipt);
        FrozenText observation = editor.CaptureState(null);
        Assert.Same(state.Content.View, observation.Content.View);
        Assert.Null(observation.SourceToken);

        receipt.Publish();
        editor.Replace(4, 1, "-");
        Assert.Same(state.ContentToken, editor.CaptureState(null).SourceToken);
        _ = editor.CaptureState(new TestReceipt()); // This candidate is discarded.
        editor.Replace(4, 1, "/");
        Assert.Same(state.ContentToken, editor.CaptureState(null).SourceToken);
    }

    [Fact]
    public void LatePublicationOfASupersededReceiptFallsBackToTheWholeLiteral() {
        TextEditor editor = new("abc");
        TestReceipt earlier = new();
        FrozenText previous = editor.CaptureState(earlier);
        _ = editor.CaptureState(new TestReceipt());
        earlier.Publish();
        editor.Replace(1, 1, "X");
        var delta = TextBodyCodec.PrepareDelta(previous, editor.CaptureState(null));
        Assert.Equal(new byte[] { 3, 2, 3, (byte)'a', 0, (byte)'X', 0, (byte)'c', 0 }, delta.Body.ToArray());
    }

    [Fact]
    public void LogicalCopyCoalescesAcrossPhysicalStringsAndRestoreBindsExactBaseline() {
        TextEditor text = new(new string('a', 256));
        text.Replace(256, 0, "BC");
        FrozenText first = TestReceipt.Save(text);
        Assert.Equal(2, first.Content.View.PieceCount);
        Assert.Equal(2, text.CaptureState(null).Content.View.PieceCount); // T=5: no physical flattening.
        // A real edit bypasses the unchanged-content shortcut. Both physical
        // strings must be emitted as a single Copy of the accepted text.
        text.Replace(text.Length, 0, "!");
        BareValueReader delta = new(TextBodyCodec.PrepareDelta(first, text.CaptureState(null)).Body);
        Assert.Equal(259u, delta.ReadVarUInt32());
        AssertCopy(ref delta, 0, 258);
        Assert.Equal(new byte[] { 2, 1, (byte)'!', 0 }, delta.ReadRawBytes(4).ToArray());
        delta.EnsureFullyConsumed();

        TextEditor a = TextEditor.Restore(first), b = TextEditor.Restore(first);
        string original = first.Content.ToString();
        Assert.Same(first.ContentToken, a.CaptureState(null).SourceToken);
        Assert.Same(first.ContentToken, a.CaptureState(null).ContentToken);
        a.Replace(1, 2, "XYZ");
        Assert.Equal(first.Content.ToString(), b.ToString());
        Assert.Equal(original, first.Content.ToString());
        AssertPlan(a.CaptureState(null), first);
        AssertPlan(b.CaptureState(null), first);

        // A second editor's saved identity cannot authenticate this source plan.
        FrozenText changed = TestReceipt.Save(a);
        a.Replace(0, 0, "!");
        Assert.Same(changed.ContentToken, a.CaptureState(null).SourceToken);
        Assert.NotSame(first.ContentToken, a.CaptureState(null).SourceToken);
    }

    [Theory]
    [InlineData(1024, 6, false)] // P=7 < ceil(sqrt(1024)/4)=8.
    [InlineData(1024, 7, true)]
    [InlineData(1025, 7, false)] // Non-square threshold is 9, not 8.
    [InlineData(1025, 8, true)]
    [InlineData(0, 1, true)]
    public void PieceThresholdUsesCurrentLengthAndAnExactCeiling(int initialLength, int inserts, bool flatten) {
        // Replacements of a prefix produce one piece per replacement and a suffix.
        TextEditor text = new(new string('a', initialLength));
        if (initialLength == 0) {
            text.Replace(0, 0, "ab");
            text.Replace(1, 1, "c");
        } else {
            for (int i = 0; i < inserts; i++) { text.Replace(i, 1, i % 2 == 0 ? "b" : "c"); }
        }
        FrozenText captured = TestReceipt.Save(text);
        int pieces = captured.Content.View.PieceCount;
        Assert.True(pieces > 1);
        Assert.Equal(flatten ? 1 : pieces, text.CaptureState(null).Content.View.PieceCount);
        Assert.Same(captured.ContentToken, text.CaptureState(null).ContentToken);
        AssertPlan(text.CaptureState(null), captured);
    }

    [Fact]
    public void BackingGuardReleasesExcessStorageAfterPublicationAndKeepsOldSnapshotReadable() {
        string original = new('a', 8192);
        TextEditor text = new(original);
        TestReceipt.Save(text);
        TextSnapshot old = text.Snapshot();
        text.Replace(128, text.Length - 128, "");
        FrozenText shrunk = TestReceipt.Save(text);
        Assert.Equal(8192, shrunk.Content.View.LargestBacking);
        Assert.Equal(8192, text.Snapshot().View.LargestBacking);
        Assert.Equal(128, text.CaptureState(null).Content.View.LargestBacking);
        Assert.Equal(original, old.ToString());
        Assert.Equal(new string('a', 128), shrunk.Content.ToString());
        AssertPlan(text.CaptureState(null), shrunk);
    }

    [Fact]
    public void ThresholdTracksChangedLengthWithoutInventingAHistoryOwningToken() {
        TextEditor text = new(new string('a', 4096));
        for (int i = 0; i < 9; i++) { text.Replace(i, 1, i % 2 == 0 ? "b" : "c"); }
        FrozenText longState = TestReceipt.Save(text);
        Assert.Equal(10, text.CaptureState(null).Content.View.PieceCount); // T=16.
        Assert.IsType<object>(text.CaptureState(null).SourceToken);
        Assert.IsType<object>(text.CaptureState(null).ContentToken);

        text.Replace(1024, 3072, "");
        FrozenText shortState = TestReceipt.Save(text);
        Assert.Equal(10, shortState.Content.View.PieceCount);
        Assert.Equal(1, text.CaptureState(null).Content.View.PieceCount); // T=8; backing guard is false at exactly 4x.
        Assert.Equal(1024, text.Snapshot().View.LargestBacking);
        AssertPlan(text.CaptureState(null), shortState);
        Assert.Equal(4096, longState.Content.Length);
    }

    [Fact]
    public void RandomBatchesSourcesSearchAndRetainedSnapshotsMatchFlatOracle() {
        Random random = new(1011801);
        TextEditor text = new("a\0\uD800中\uDC00\r\nxyz");
        string expected = text.ToString();
        FrozenText baseline = TestReceipt.Save(text);
        List<(TextSnapshot Snapshot, string Text)> held = [];
        string[] replacements = ["", "x", "\0", "\uD800", "\uDC00", "\r\n", "longer文本", "aaaaab"];
        for (int step = 0; step < 600; step++) {
            int start = random.Next(expected.Length + 1);
            int length = random.Next(expected.Length - start + 1);
            string inserted = replacements[random.Next(replacements.Length)];
            // Two disjoint replacements in the original coordinates; reverse oracle application.
            int end = start + length;
            TextEdit[] edits;
            if (end < expected.Length && random.Next(2) == 0) {
                int second = random.Next(end + 1, expected.Length + 1);
                int removed = random.Next(expected.Length - second + 1);
                edits = [new(second, removed, replacements[random.Next(replacements.Length)]), new(start, length, inserted)];
            } else { edits = [new(start, length, inserted)]; }
            foreach (TextEdit edit in edits.OrderByDescending(edit => edit.Start)) {
                expected = expected[..edit.Start] + edit.Replacement + expected[(edit.Start + edit.Length)..];
            }
            if (step % 2 == 0) { Array.Reverse(edits); }
            Assert.True(text.TryApplyEdits(text.Snapshot(), edits, out _));
            Assert.Equal(expected, text.ToString());
            AssertPlan(text.CaptureState(null), baseline);
            if (step % 17 == 0) { held.Add((text.Snapshot(), expected)); }
            if (step % 13 == 0) { AssertAllRanges(text, expected); }
            string needle = replacements[random.Next(replacements.Length)];
            int searchStart = random.Next(expected.Length + 1);
            Assert.Equal(expected.IndexOf(needle, searchStart, StringComparison.Ordinal), text.Snapshot().IndexOf(needle, searchStart));
            if (step % 23 == 0) {
                baseline = TestReceipt.Save(text);
                AssertPlan(text.CaptureState(null), baseline);
            }
            foreach (var item in held) { Assert.Equal(item.Text, item.Snapshot.ToString()); }
        }
    }

    private static void AssertPlan(FrozenText state, FrozenText baseline) {
        Assert.Same(baseline.ContentToken, state.SourceToken);
        StringBuilder replay = new();
        foreach (TextPiece piece in state.Content.View.Pieces) {
            Assert.True(piece.Length > 0);
            if (piece.BaseOffset >= 0) { baseline.Content.AppendTo(replay, piece.BaseOffset, piece.Length); }
            else { replay.Append(piece.Memory.Span); }
        }
        Assert.Equal(state.Content.ToString(), replay.ToString());
    }

    private static void AssertCopy(ref BareValueReader delta, uint offset, uint length) {
        Assert.Equal((byte)1, delta.ReadByte());
        Assert.Equal(offset, delta.ReadVarUInt32());
        Assert.Equal(length, delta.ReadVarUInt32());
    }

    private static void AssertAllRanges(TextEditor text, string expected) {
        for (int start = 0; start <= expected.Length; start++) {
            for (int count = 0; count <= expected.Length - start; count++) {
                char[] actual = new char[count];
                text.CopyTo(start, actual);
                Assert.Equal(expected.Substring(start, count), new string(actual));
                StringBuilder destination = new("prefix:");
                text.AppendTo(destination, start, count);
                Assert.Equal("prefix:" + expected.Substring(start, count), destination.ToString());
            }
        }
    }
}
