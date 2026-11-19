using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Atelia.Text.Tests;

public sealed class TextEditorVisitSpanTests {
    [Fact]
    public void EmptyTextInvokesVisitorOnceAndReturnsItsResult() {
        TextEditor text = new();
        List<int> lengths = [];
        var result = text.VisitSpan(lengths, static (span, observed) => {
            observed.Add(span.Length);
            return observed;
        });

        Assert.Same(lengths, result);
        Assert.Equal(0, Assert.Single(lengths));
        Assert.Throws<ArgumentNullException>("visitor", () => text.VisitSpan<int, int>(0, null!));
    }

    [Fact]
    public void StaticVisitorsReadAndWriteCallerOwnedSpanContext() {
        TextEditor text = new("north gate");
        text.Replace(0, 5, "south");
        ReadOnlySpan<char> needle = "find:th gate".AsSpan(5);
        Assert.Equal(3, text.VisitSpan(needle, static (body, query) => body.IndexOf(query, StringComparison.Ordinal)));

        Span<int> offsets = stackalloc int[2];
        int written = text.VisitSpan(offsets, static (body, result) => {
            int n = 0;
            for (int i = 0; i < body.Length && n < result.Length; i++) {
                if (body[i] == 't') { result[n++] = i; }
            }
            return n;
        });
        Assert.Equal(2, written);
        Assert.Equal(3, offsets[0]);
        Assert.Equal(8, offsets[1]);
    }

    [Fact]
    public void ToStringReusesOnlyAnAlreadyMaterializedCurrentVersion() {
        TextEditor text = new("ac");
        text.Replace(1, 0, "b");
        string cold = text.ToString();
        Assert.False(text.VisitSpan(cold, static (span, previous) => span == previous.AsSpan()));

        string warm = text.ToString();
        Assert.True(text.VisitSpan(warm, static (span, current) => span == current.AsSpan()));
        Assert.Same(warm, text.ToString());
        text.Replace(1, 1, "B");
        Assert.Equal("aBc", text.ToString());
        Assert.Equal("abc", warm);
    }

    [Fact]
    public void SinglePieceBorrowsOnlyTheVisibleSliceOfItsBackingString() {
        const string backing = "<<甲\0\uD800乙>>";
        TextEditor text = new(backing);
        Assert.True(text.TryApplyEdits(text.Snapshot(), [new(0, 2, ""), new(backing.Length - 2, 2, "")], out _));

        string result = text.VisitSpan(backing, static (span, source) => {
            Assert.True(source.AsSpan().Overlaps(span, out int offset));
            Assert.Equal(2, offset);
            return span.ToString();
        });

        Assert.Equal("甲\0\uD800乙", result);
    }

    [Fact]
    public void LiteralAndRegexVisitorsFindCrossPieceTextImmediatelyAfterEdits() {
        TextEditor text = new("😀\r\n钥匙在仓库。");
        text.Replace(7, 0, "北门");
        int index = text.VisitSpan("北门仓库", static (span, query) => span.IndexOf(query, StringComparison.Ordinal));
        Assert.Equal(7, index);

        Regex regex = new(@"(?<=钥匙在)[北南]门仓库(?=。)");
        static (int, string) FirstExcerpt(ReadOnlySpan<char> span, Regex query) {
            foreach (var hit in query.EnumerateMatches(span)) {
                return (hit.Index, span.Slice(hit.Index, hit.Length).ToString());
            }
            return (-1, string.Empty);
        }
        var original = text.VisitSpan(regex, FirstExcerpt);
        text.Replace(index, 1, "南");
        Assert.Equal((7, "南门仓库"), text.VisitSpan(regex, FirstExcerpt));
        Assert.Equal((7, "北门仓库"), original);
    }

    [Fact]
    public void NestedFailingVisitSeesEditWhileOuterSpanKeepsItsVersion() {
        TextEditor text = new("去仓库");
        text.Replace(1, 0, "北门");
        Assert.True(text.Snapshot().View.PieceCount > 1);
        InvalidOperationException failure = new("nested visitor");

        string original = text.VisitSpan((text, failure), static (outer, context) => {
            context.text.Replace(1, 1, "南");
            Assert.True(context.text.Snapshot().View.PieceCount > 1);
            var thrown = Assert.Throws<InvalidOperationException>(() =>
                context.text.VisitSpan<InvalidOperationException, bool>(context.failure, static (inner, error) => {
                    Assert.Equal("去南门仓库", inner.ToString());
                    throw error;
                }));
            Assert.Same(context.failure, thrown);
            return outer.ToString();
        });

        Assert.Equal("去北门仓库", original);
        Assert.Equal("去南门仓库", text.VisitSpan(0, static (span, _) => span.ToString()));
    }

    [Fact]
    public void LaterVisitsAndPublishedCompactionShareTheFirstVisitString() {
        TextEditor text = new("去仓库");
        text.Replace(1, 0, "北门");
        text.VisitSpan(text, static (span, editor) => {
            string cached = editor.ToString();
            Assert.True(span == cached.AsSpan());
            TestReceipt.Save(editor);
            Assert.True(editor.Snapshot().View.PieceCount > 1);
            Assert.Same(cached, editor.ToString());
            Assert.Same(cached, Assert.Single(editor.CaptureState(null).Content.View.Pieces).Text);
            Assert.True(editor.VisitSpan(cached, static (nested, s) => nested == s.AsSpan()));
            return 0;
        });
    }

    [Fact]
    public void SaveRebindOfFragmentedTextKeepsTheWarmedContent() {
        TextEditor text = new(new string('a', 32 * 1024));
        text.Replace(text.Length, 0, "!");
        static char ReadLast(ReadOnlySpan<char> span, int _) => span[^1];
        Func<ReadOnlySpan<char>, int, char> visit = ReadLast;
        Assert.Equal('!', text.VisitSpan(0, visit));

        FrozenText captured = TestReceipt.Save(text);
        var rebound = text.CaptureState(null).Content.View;
        Assert.NotSame(captured.Content.View, rebound);
        Assert.True(rebound.PieceCount > 1);
        Assert.Same(captured.ContentToken, rebound.ContentToken);

        long before = GC.GetAllocatedBytesForCurrentThread();
        char last = text.VisitSpan(0, visit);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal('!', last);
        Assert.True(allocated < (long)text.Length * sizeof(char), $"Allocated {allocated} bytes after rebind.");
    }

    [Fact]
    public void PublishingAStaleCaptureCannotUseOrDisplaceTheCurrentVersionCache() {
        TextEditor text = new("去仓库");
        text.Replace(1, 0, "北门");
        Assert.Equal("去北门仓库", text.VisitSpan(0, static (span, _) => span.ToString()));
        TestReceipt receipt = new();
        FrozenText north = text.CaptureState(receipt);
        text.Replace(1, 1, "南");
        text.VisitSpan(0, static (span, _) => span.Length);
        string south = text.ToString();

        receipt.Publish();
        FrozenText current = text.CaptureState(null);
        Assert.Null(current.SourceToken);
        Assert.Equal("去北门仓库", north.Content.ToString());
        Assert.Equal("去南门仓库", south);
        Assert.True(text.VisitSpan(south, static (span, cached) => span == cached.AsSpan()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EditToOnePieceReleasesCacheEvenWithAnOldSnapshot(bool useBatch) {
        TextEditor text = new("去仓库");
        text.Replace(1, 0, "北门");
        TextSnapshot snapshot = text.Snapshot();
        WeakReference<string> old = TrackCachedString(text);

        if (useBatch) { Assert.True(text.TryApplyEdits(snapshot, [new(0, text.Length, "已抵达")], out _)); }
        else { text.Replace(0, text.Length, "已抵达"); }
        Assert.Equal(1, text.Snapshot().View.PieceCount);
        GC.Collect();
        Assert.False(old.TryGetTarget(out _));
        Assert.Equal("去北门仓库", snapshot.ToString());
        GC.KeepAlive(text);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<string> TrackCachedString(TextEditor text) {
        text.VisitSpan(0, static (span, _) => span.Length);
        return new(text.ToString());
    }

    [Fact]
    public void HoldingAPublicSnapshotKeepsTheTextReadableAfterItsEditorIsCollected() {
        var (snapshot, editor) = CaptureSnapshot();
        GC.Collect();
        Assert.False(editor.TryGetTarget(out _));
        Assert.Equal("north gate", snapshot.ToString());
        GC.KeepAlive(snapshot);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static (TextSnapshot, WeakReference<TextEditor>) CaptureSnapshot() {
            TextEditor text = new("north");
            text.Replace(text.Length, 0, " gate");
            return (text.Snapshot(), new(text));
        }
    }
}
