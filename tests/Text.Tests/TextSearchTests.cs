using System.Text.RegularExpressions;

namespace Atelia.Text.Tests;

public sealed class TextSearchTests {
    [Fact]
    public void LiteralMatchesAcrossPiecesHaveVerbatimOwnedExcerpts() {
        TextEditor editor = new("记\r\n门\r乙北门\n");
        editor.Replace(3, 0, "北");
        var result = editor.Search("北门");
        Assert.False(result.HasMore);
        Assert.Equal([(3, 2L), (7, 3L)], result.Matches.Select(hit => (hit.Start, hit.Line)));
        string original = editor.ToString();
        editor.Replace(0, editor.Length, "changed");
        Assert.All(result.Matches, hit => {
            Assert.Equal(original, hit.Excerpt);
            Assert.Equal("北门", hit.Excerpt.Substring(hit.Start - hit.ExcerptStart, hit.Length));
            Assert.False(hit.TruncatedBefore);
            Assert.False(hit.TruncatedAfter);
        });
        Assert.Empty(editor.Search("北门").Matches);
    }

    [Fact]
    public void LengthAndCountBoundSearchWithoutClaimingAnUnseenMatch() {
        string source = new string('a', 50_000) + "甲从北门出发" + new string('b', 60_000) + "乙过北门抵达";
        TextEditor editor = new(source);
        var first = editor.Search("北门", maxMatches: 1, maxExcerptLength: 10);
        var item = Assert.Single(first.Matches);
        Assert.True(first.HasMore);
        Assert.Equal("aa甲从北门出发bb", item.Excerpt);
        Assert.True(item.TruncatedBefore);
        Assert.True(item.TruncatedAfter);
        var all = editor.Search("北门", maxMatches: 2, maxExcerptLength: 10);
        Assert.False(all.HasMore);
        Assert.EndsWith("乙过北门抵达", all.Matches[1].Excerpt);
        Assert.False(all.Matches[1].TruncatedAfter);
        Assert.True(editor.Search("门", maxMatches: 0).HasMore);
        Assert.False(editor.Search("南门", maxMatches: 0).HasMore);
        Assert.True(editor.Search(new Regex("门"), maxMatches: 0).HasMore);
    }

    [Fact]
    public void RegexOptionsZeroWidthAndPhysicalLineEndingsArePreserved() {
        TextEditor editor = new("A\r\nb\rc\n");
        var crossing = Assert.Single(editor.Search(new Regex("a\r\nb", RegexOptions.IgnoreCase)).Matches);
        Assert.Equal((0, 4, 1L, 2L), (crossing.Start, crossing.Length, crossing.Line, crossing.EndLine));
        var selected = editor.Search(new Regex(@"(?=\n)|\z", RegexOptions.RightToLeft), maxMatches: 2);
        Assert.True(selected.HasMore);
        Assert.Equal([(6, 3L), (7, 4L)], selected.Matches.Select(hit => (hit.Start, hit.Line)));
        Assert.All(selected.Matches, hit => Assert.Equal(0, hit.Length));
        Assert.Equal([1L, 3L, 4L], editor.Search(new Regex(@"(?=\n)|\z")).Matches.Select(hit => hit.Line));
        Assert.Equal(1L, Assert.Single(new TextEditor().Search(new Regex(@"\z")).Matches).Line);
    }

    [Fact]
    public void AmbiguityIncludesOverlappingStartsAndLeavesEditorVersionIntact() {
        TextEditor editor = new("aaaa");
        var before = editor.Snapshot();
        Assert.Equal([0, 2], editor.Search("aa").Matches.Select(hit => hit.Start));
        var edit = editor.ReplaceUnique("aa", "!", maxCandidates: 2);
        Assert.Equal(ReplaceUniqueStatus.Ambiguous, edit.Status);
        Assert.True(edit.HasMore);
        Assert.Equal([0, 1], edit.Candidates.Select(hit => hit.Start));
        Assert.True(editor.TryApplyEdits(before, [], out _));
        Assert.Equal("aaaa", editor.ToString());
        var exact = new TextEditor("aaa").ReplaceUnique("aa", "");
        Assert.Equal(ReplaceUniqueStatus.Ambiguous, exact.Status);
        Assert.False(exact.HasMore);
    }

    [Fact]
    public void AmbiguityShowsBothImmediateNeighborsEvenWhenTheLiteralExceedsTheBudget() {
        string oldText = new('x', 200);
        string first = "甲班开门" + oldText + "交东队收尾";
        string second = "乙班进门" + oldText + "由西队签收";
        TextEditor editor = new(first + "\r\n" + second);
        var edit = editor.ReplaceUnique(oldText, "新正文", maxExcerptLength: 6);
        Assert.Equal(ReplaceUniqueStatus.Ambiguous, edit.Status);
        Assert.False(edit.HasMore);
        Assert.Equal([("班开门", "交东队", 1L), ("班进门", "由西队", 2L)],
            edit.Candidates.Select(c => (c.Before, c.After, c.Line)));
        Assert.All(edit.Candidates, c => {
            Assert.True(c.TruncatedBefore && c.TruncatedAfter);
            Assert.Equal(6, c.Before.Length + c.After.Length);
            Assert.Equal(oldText, editor.ToString().Substring(c.Start, c.Length));
        });
        var defaultReceipt = editor.ReplaceUnique(oldText, "新正文");
        Assert.EndsWith("乙班进门", defaultReceipt.Candidates[1].Before);
        Assert.StartsWith("由西队", defaultReceipt.Candidates[1].After);
        Assert.All(defaultReceipt.Candidates, c => Assert.True(c.Before.Length + c.After.Length <= 120));
        var target = edit.Candidates[1];
        Assert.Equal(ReplaceUniqueStatus.Replaced, editor.ReplaceUnique(
            target.Before + oldText + target.After, target.Before + "新正文" + target.After).Status);
        Assert.Equal(first + "\r\n乙班进门新正文由西队签收", editor.ToString());
    }

    [Fact]
    public void CandidateContextUsesSpareBudgetAtEdgesAndPreservesCharacterPairs() {
        TextEditor editor = new("门甲😀乙丙门");
        var edit = editor.ReplaceUnique("门", "", maxExcerptLength: 3);
        Assert.Equal([("", "甲😀", false, true), ("乙丙", "", true, false)],
            edit.Candidates.Select(c => (c.Before, c.After, c.TruncatedBefore, c.TruncatedAfter)));
    }

    [Theory]
    [InlineData("😀x😁x😇", 2, "", "😁")]
    [InlineData("A😀x😁x", 3, "", "😁x")]
    [InlineData("Ax😁x", 1, "A", "")]
    [InlineData("😀x😁x😇", 1, "", "")]
    public void CandidateContextSharesUnusedSurrogateSpace(string body, int budget, string before, string after) {
        var result = new TextEditor(body).ReplaceUnique("x", "!", maxExcerptLength: budget);
        Assert.Equal(ReplaceUniqueStatus.Ambiguous, result.Status);
        var first = result.Candidates[0];
        Assert.Equal((before, after), (first.Before, first.After));
        foreach (var candidate in result.Candidates) {
            Assert.True(candidate.Before.Length + candidate.After.Length <= budget);
            Assert.Equal(candidate.Before + "x" + candidate.After,
                body.Substring(candidate.Start - candidate.Before.Length, candidate.Before.Length + 1 + candidate.After.Length));
        }
    }

    [Fact]
    public void CallsRecheckCurrentTextAndTreatReplacementLiterally() {
        TextEditor editor = new("a A");
        Assert.Equal(ReplaceUniqueStatus.NotFound, editor.ReplaceUnique("x", "a").Status);
        Assert.Equal(ReplaceUniqueStatus.Replaced, editor.ReplaceUnique("a", "$1").Status);
        var removed = editor.ReplaceUnique("$1 ", "");
        Assert.Equal(ReplaceUniqueStatus.Replaced, removed.Status);
        var candidate = Assert.Single(removed.Candidates);
        Assert.Equal(("", "A"), (candidate.Before, candidate.After));
        Assert.Equal("A", editor.ToString());
        var before = editor.Snapshot();
        Assert.Equal(ReplaceUniqueStatus.Replaced, editor.ReplaceUnique("A", "A").Status);
        Assert.True(editor.TryApplyEdits(before, [], out _));
    }

    [Fact]
    public void ClippingPreservesUtf16AndRemainsBoundedEvenWhenMatchExceedsBudget() {
        TextEditor editor = new("😀aaXbb😀");
        var hit = Assert.Single(editor.Search("X", maxExcerptLength: 7).Matches);
        Assert.Equal("aaXbb", hit.Excerpt);
        Assert.True(hit.TruncatedBefore && hit.TruncatedAfter);
        var longMatch = Assert.Single(editor.Search(new Regex(".+"), maxExcerptLength: 3).Matches);
        Assert.Equal("aXb", longMatch.Excerpt);
        Assert.Equal(editor.Length, longMatch.Length);
        Assert.Equal("\uDC00", Assert.Single(new TextEditor("\uDC00").Search("\uDC00").Matches).Excerpt);
    }

    [Fact]
    public void InvalidParametersReferencePublicNamesAndNeverEdit() {
        TextEditor editor = new("abc");
        Assert.Throws<ArgumentNullException>("pattern", () => editor.Search((string)null!));
        Assert.Throws<ArgumentNullException>("pattern", () => editor.Search((Regex)null!));
        Assert.Throws<ArgumentException>("pattern", () => editor.Search(""));
        Assert.Throws<ArgumentOutOfRangeException>("maxMatches", () => editor.Search("a", -1));
        Assert.Throws<ArgumentOutOfRangeException>("maxExcerptLength", () => editor.Search("a", maxExcerptLength: 0));
        Assert.Throws<ArgumentException>("oldText", () => editor.ReplaceUnique("", "a"));
        Assert.Throws<ArgumentNullException>("newText", () => editor.ReplaceUnique("a", null!));
        Assert.Throws<ArgumentOutOfRangeException>("maxCandidates", () => editor.ReplaceUnique("a", "b", 1));
        Assert.Equal("abc", editor.ToString());
    }
}
