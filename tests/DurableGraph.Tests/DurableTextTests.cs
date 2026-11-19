using System.Text;

namespace Atelia.DurableGraph.Tests;

public sealed class DurableTextTests {
    [Fact]
    public void OriginalCoordinatesAndSamePointInsertionsPublishOneResult() {
        DurableText text = new("abcdef");
        TextSnapshot before = text.Snapshot();
        text.ApplyEdits([
            new(4, 1, "Z"), new(1, 2, "Q"), new(1, 0, "x"),
            new(3, 0, "!"), new(1, 0, "y"), new(6, 0, "end")
        ]);
        Assert.Equal("axyQ!dZfend", text.ToString());
        Assert.Equal("abcdef", before.ToString());
        object token = text.CaptureState().ContentToken;
        text.ApplyEdits([]);
        text.Replace(0, 0, "");
        Assert.Same(token, text.CaptureState().ContentToken);
    }

    [Fact]
    public void InvalidBatchesAndRangesDoNotChangeContentOrIdentity() {
        DurableText text = new("abcdef");
        object token = text.CaptureState().ContentToken;
        TextEdit[][] invalid = [
            [new(-1, 0, "x")], [new(7, 0, "x")], [new(0, -1, "x")],
            [new(5, int.MaxValue, "x")], [new(0, 0, null!)],
            [new(1, 3, "x"), new(2, 0, "y")],
            [new(1, 3, "x"), new(3, 2, "y")],
            [new(1, 1, ""), new(1, 2, "y")]
        ];
        foreach (TextEdit[] batch in invalid) {
            Assert.ThrowsAny<ArgumentException>(() => text.ApplyEdits(batch));
            Assert.Equal("abcdef", text.ToString());
            Assert.Same(token, text.CaptureState().ContentToken);
        }
        Assert.Throws<ArgumentNullException>(() => text.ApplyEdits(null!));
        Assert.Throws<ArgumentNullException>(() => new DurableText(null!));
        Assert.Throws<ArgumentNullException>(() => text.Replace(0, 1, null!));
        Assert.Throws<ArgumentOutOfRangeException>("length", () => text.Replace(0, -1, ""));
        Assert.Throws<ArgumentOutOfRangeException>("start", () => text.CopyTo(7, Span<char>.Empty));
        Assert.Throws<ArgumentOutOfRangeException>("destination", () => text.CopyTo(5, new char[2]));
        StringBuilder destination = new("prefix");
        Assert.Throws<ArgumentOutOfRangeException>("length", () => text.AppendTo(destination, 0, 7));
        var snapshot = text.Snapshot();
        Assert.Throws<ArgumentOutOfRangeException>("length", () => snapshot.AppendTo(destination, 0, 7));
        Assert.Throws<ArgumentOutOfRangeException>("destination", () => snapshot.CopyTo(5, new char[2]));
        Assert.Equal("prefix", destination.ToString());
        Assert.Equal("abcdef", text.ToString());
    }

    [Fact]
    public void BatchErrorsIdentifyTheOriginalInputIndexAndField() {
        DurableText text = new("abcdef");
        TextEdit append = new(6, 0, "!");
        Assert.Contains("edits[1].Replacement", Assert.Throws<ArgumentNullException>("edits",
            () => text.ApplyEdits([append, default])).Message);
        Assert.Contains("edits[1].Start", Assert.Throws<ArgumentOutOfRangeException>("edits",
            () => text.ApplyEdits([append, new(-1, 0, "")])).Message);
        Assert.Contains("edits[1].Length", Assert.Throws<ArgumentOutOfRangeException>("edits",
            () => text.ApplyEdits([append, new(3, 4, "")])).Message);
        // The first input is second after sorting and overlaps the preceding removal.
        Assert.Contains("edits[0].Start", Assert.Throws<ArgumentException>("edits",
            () => text.ApplyEdits([new(1, 4, ""), new(0, 5, "")])).Message);
        Assert.Contains("edits[2].Start", Assert.Throws<ArgumentException>("edits",
            () => text.ApplyEdits([append, new(1, 1, "a"), new(1, 1, "b")])).Message);
        Assert.Equal("abcdef", text.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReentrantCountOrIndexerCannotOverwriteTheNestedEdit(bool mutateInCount) {
        DurableText text = new("abc");
        IReadOnlyList<TextEdit> input = new ReentrantEdits(text, mutateInCount);
        Assert.Throws<InvalidOperationException>(() => text.ApplyEdits(input));
        Assert.Equal("!abc", text.ToString());
    }

    private sealed class ReentrantEdits(DurableText text, bool mutateInCount) : IReadOnlyList<TextEdit> {
        public int Count {
            get {
                if (mutateInCount) { text.Replace(0, 0, "!"); }
                return 1;
            }
        }
        public TextEdit this[int index] {
            get {
                if (!mutateInCount) { text.Replace(0, 0, "!"); }
                return new(1, 1, "X");
            }
        }
        public IEnumerator<TextEdit> GetEnumerator() => throw new NotSupportedException();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void DecoderBorrowsBoundedPhysicalSlicesWithoutRetainingSourceViews() {
        DurableText source = new("ab");
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
        DurableTextState baseline = new(decoded);
        DurableText editor = DurableText.Restore(baseline);
        editor.Replace(0, 1, "B");
        AssertPlan(editor.CaptureState(), baseline);
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
        DurableText text = new("a\uD83D");
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
        DurableTextState captured = text.CaptureState();
        Assert.True(text.TryInstall(text.PrepareAcceptance(captured)));
        text.Replace(0, text.Length, "different");
        Assert.Equal(expected, frozen.ToString());
        Assert.Equal(expected, string.Concat(chunks.Select(chunk => chunk.ToString())));
    }

    [Fact]
    public void OrdinalSearchCrossesPiecesAndHandlesRepeatedPrefixesAndEmptyNeedles() {
        DurableText text = new("aa");
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
        Assert.Equal(0, new DurableText().Snapshot().IndexOf(""));
        Assert.Equal(-1, new DurableText().Snapshot().IndexOf("a"));
    }

    [Fact]
    public void PrepareIsPureInstallationIsConditionalAndContentIdentitySurvivesCompaction() {
        DurableText text = new("abcdefgh");
        text.Replace(1, 1, "X");
        DurableTextState captured = text.CaptureState();
        TextPieceView before = text.Snapshot().View;
        DurableTextAcceptance prepared = text.PrepareAcceptance(captured);
        Assert.Same(before, text.Snapshot().View);
        Assert.Null(text.CaptureState().SourceToken);
        Assert.False(new DurableText("aXcdefgh").TryInstall(prepared));
        Assert.True(text.TryInstall(prepared));
        DurableTextState accepted = text.CaptureState();
        Assert.Same(captured.ContentToken, accepted.ContentToken);
        Assert.Same(captured.ContentToken, accepted.SourceToken);
        Assert.Equal(1, accepted.Content.View.PieceCount);

        // An untouched body does not rebuild physical pieces or invent another content identity.
        TextPieceView rebound = accepted.Content.View;
        Assert.True(text.TryInstall(text.PrepareAcceptance(accepted)));
        Assert.Same(rebound, text.Snapshot().View);
        Assert.Same(accepted.ContentToken, text.CaptureState().ContentToken);

        DurableTextState stale = text.CaptureState();
        DurableTextAcceptance obsolete = text.PrepareAcceptance(stale);
        text.Replace(0, 1, "!");
        text.Replace(0, 1, "a"); // Equal content does not make an old physical-state install safe.
        Assert.False(text.TryInstall(obsolete));
        Assert.Equal(stale.Content.ToString(), text.ToString());
        Assert.NotSame(stale.ContentToken, text.CaptureState().ContentToken);
    }

    [Fact]
    public void LogicalCopyCoalescesAcrossPhysicalStringsAndRestoreBindsExactBaseline() {
        DurableText text = new(new string('a', 256));
        text.Replace(256, 0, "BC");
        DurableTextState first = text.CaptureState();
        Assert.Equal(2, first.Content.View.PieceCount); // T=5: no physical flattening.
        Assert.True(text.TryInstall(text.PrepareAcceptance(first)));
        Assert.Equal(2, text.Snapshot().View.PieceCount);
        TextSourcePiece whole = Assert.Single(text.CaptureState().BuildSourcePlan());
        Assert.True(whole.IsCopy);
        Assert.Equal(0, whole.BaseOffset);
        Assert.Equal(258, whole.Length);

        DurableText a = DurableText.Restore(first), b = DurableText.Restore(first);
        string original = first.Content.ToString();
        Assert.Same(first.ContentToken, a.CaptureState().SourceToken);
        Assert.Same(first.ContentToken, a.CaptureState().ContentToken);
        a.Replace(1, 2, "XYZ");
        Assert.Equal(first.Content.ToString(), b.ToString());
        Assert.Equal(original, first.Content.ToString());
        AssertPlan(a.CaptureState(), first);
        AssertPlan(b.CaptureState(), first);

        // A second editor's saved identity cannot authenticate this source plan.
        DurableTextState changed = a.CaptureState();
        Assert.True(a.TryInstall(a.PrepareAcceptance(changed)));
        a.Replace(0, 0, "!");
        Assert.Same(changed.ContentToken, a.CaptureState().SourceToken);
        Assert.NotSame(first.ContentToken, a.CaptureState().SourceToken);
    }

    [Theory]
    [InlineData(1024, 6, false)] // P=7 < ceil(sqrt(1024)/4)=8.
    [InlineData(1024, 7, true)]
    [InlineData(1025, 7, false)] // Non-square threshold is 9, not 8.
    [InlineData(1025, 8, true)]
    [InlineData(0, 1, true)]
    public void PieceThresholdUsesCurrentLengthAndAnExactCeiling(int initialLength, int inserts, bool flatten) {
        // Replacements of a prefix produce one piece per replacement and a suffix.
        DurableText text = new(new string('a', initialLength));
        if (initialLength == 0) {
            text.Replace(0, 0, "ab");
            text.Replace(1, 1, "c");
        } else {
            for (int i = 0; i < inserts; i++) { text.Replace(i, 1, i % 2 == 0 ? "b" : "c"); }
        }
        DurableTextState captured = text.CaptureState();
        int pieces = captured.Content.View.PieceCount;
        Assert.True(pieces > 1);
        Assert.True(text.TryInstall(text.PrepareAcceptance(captured)));
        Assert.Equal(flatten ? 1 : pieces, text.Snapshot().View.PieceCount);
        Assert.Same(captured.ContentToken, text.CaptureState().ContentToken);
        AssertPlan(text.CaptureState(), captured);
    }

    [Fact]
    public void BackingGuardCopiesOnlyAtAcceptanceAndOldSnapshotRemainsReadable() {
        string original = new('a', 8192);
        DurableText text = new(original);
        DurableTextState initial = text.CaptureState();
        Assert.True(text.TryInstall(text.PrepareAcceptance(initial)));
        TextSnapshot old = text.Snapshot();
        text.Replace(128, text.Length - 128, "");
        DurableTextState shrunk = text.CaptureState();
        Assert.Equal(8192, shrunk.Content.View.LargestBacking);
        DurableTextAcceptance pending = text.PrepareAcceptance(shrunk);
        Assert.Equal(8192, text.Snapshot().View.LargestBacking);
        Assert.True(text.TryInstall(pending));
        Assert.Equal(128, text.Snapshot().View.LargestBacking);
        Assert.Equal(original, old.ToString());
        Assert.Equal(new string('a', 128), shrunk.Content.ToString());
        AssertPlan(text.CaptureState(), shrunk);
    }

    [Fact]
    public void ThresholdTracksChangedLengthWithoutInventingAHistoryOwningToken() {
        DurableText text = new(new string('a', 4096));
        for (int i = 0; i < 9; i++) { text.Replace(i, 1, i % 2 == 0 ? "b" : "c"); }
        DurableTextState longState = text.CaptureState();
        Assert.True(text.TryInstall(text.PrepareAcceptance(longState)));
        Assert.Equal(10, text.Snapshot().View.PieceCount); // T=16.
        Assert.IsType<object>(text.CaptureState().SourceToken);
        Assert.IsType<object>(text.CaptureState().ContentToken);

        text.Replace(1024, 3072, "");
        DurableTextState shortState = text.CaptureState();
        Assert.Equal(10, shortState.Content.View.PieceCount);
        Assert.True(text.TryInstall(text.PrepareAcceptance(shortState)));
        Assert.Equal(1, text.Snapshot().View.PieceCount); // T=8; backing guard is false at exactly 4x.
        Assert.Equal(1024, text.Snapshot().View.LargestBacking);
        AssertPlan(text.CaptureState(), shortState);
        Assert.Equal(4096, longState.Content.Length);
    }

    [Fact]
    public void RandomBatchesSourcesSearchAndRetainedSnapshotsMatchFlatOracle() {
        Random random = new(1011801);
        DurableText text = new("a\0\uD800中\uDC00\r\nxyz");
        string expected = text.ToString();
        DurableTextState baseline = text.CaptureState();
        Assert.True(text.TryInstall(text.PrepareAcceptance(baseline)));
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
            text.ApplyEdits(edits);
            Assert.Equal(expected, text.ToString());
            AssertPlan(text.CaptureState(), baseline);
            if (step % 17 == 0) { held.Add((text.Snapshot(), expected)); }
            if (step % 13 == 0) { AssertAllRanges(text, expected); }
            string needle = replacements[random.Next(replacements.Length)];
            int searchStart = random.Next(expected.Length + 1);
            Assert.Equal(expected.IndexOf(needle, searchStart, StringComparison.Ordinal), text.Snapshot().IndexOf(needle, searchStart));
            if (step % 23 == 0) {
                DurableTextState captured = text.CaptureState();
                Assert.True(text.TryInstall(text.PrepareAcceptance(captured)));
                baseline = captured;
                AssertPlan(text.CaptureState(), baseline);
            }
            foreach (var item in held) { Assert.Equal(item.Text, item.Snapshot.ToString()); }
        }
    }

    private static void AssertPlan(DurableTextState state, DurableTextState baseline) {
        Assert.Same(baseline.ContentToken, state.SourceToken);
        StringBuilder replay = new();
        foreach (TextSourcePiece piece in state.BuildSourcePlan()) {
            Assert.True(piece.Length > 0);
            if (piece.IsCopy) { baseline.Content.AppendTo(replay, piece.BaseOffset, piece.Length); }
            else { replay.Append(piece.Literal.Span); }
        }
        Assert.Equal(state.Content.ToString(), replay.ToString());
    }

    private static void AssertAllRanges(DurableText text, string expected) {
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
