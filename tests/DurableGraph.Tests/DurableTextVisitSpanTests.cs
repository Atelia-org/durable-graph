using System.Text.RegularExpressions;

namespace Atelia.DurableGraph.Tests;

public sealed class DurableTextVisitSpanTests {
    [Fact]
    public void EmptyTextInvokesVisitorOnceAndReturnsItsResult() {
        DurableText text = new();
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
    public void SinglePieceBorrowsOnlyTheVisibleSliceOfItsBackingString() {
        const string backing = "<<甲\0\uD800乙>>";
        DurableText text = new(backing);
        text.ApplyEdits([new(0, 2, ""), new(backing.Length - 2, 2, "")]);

        string result = text.VisitSpan(backing, static (span, source) => {
            Assert.True(source.AsSpan().Overlaps(span, out int offset));
            Assert.Equal(2, offset);
            return span.ToString();
        });

        Assert.Equal("甲\0\uD800乙", result);
    }

    [Fact]
    public void LiteralAndRegexVisitorsFindCrossPieceTextImmediatelyAfterEdits() {
        DurableText text = new("😀\r\n钥匙在仓库。");
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
        DurableText text = new("去仓库");
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
}
