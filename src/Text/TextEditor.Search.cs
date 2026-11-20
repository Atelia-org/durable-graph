using System.Text.RegularExpressions;

namespace Atelia.Text;

public sealed partial class TextEditor {
    /// <summary>Finds non-overlapping occurrences of a nonempty, ordinal literal in the current text.</summary>
    /// <param name="pattern">The exact text to find.</param>
    /// <param name="maxMatches">Maximum number of matches to return. Zero tests only for existence via <see cref="TextSearchResult.HasMore"/>.</param>
    /// <param name="maxExcerptLength">Positive maximum number of UTF-16 code units per excerpt.</param>
    /// <remarks>CRLF, CR and LF end a line. The terminator belongs to the preceding line.</remarks>
    public TextSearchResult Search(string pattern, int maxMatches = 8, int maxExcerptLength = 120) {
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        ValidateSearchLimits(maxMatches, maxExcerptLength);
        return VisitSpan((pattern, maxMatches, maxExcerptLength), static (body, query) =>
            FindLiteral(body, query.pattern, query.maxMatches, overlap: false).SearchResult(body, query.maxExcerptLength));
    }

    /// <summary>Finds matches in the current text using the supplied regex, including zero-width matches.</summary>
    /// <param name="pattern">Regex with the caller's options and timeout. Match exceptions propagate.</param>
    /// <param name="maxMatches">Maximum number to select in regex enumeration order. The result lists these matches in document order.</param>
    /// <param name="maxExcerptLength">Positive maximum number of UTF-16 code units per excerpt.</param>
    /// <remarks>CRLF, CR and LF end a line. A zero-width match at EOF belongs to the last line, including a final empty line.</remarks>
    public TextSearchResult Search(Regex pattern, int maxMatches = 8, int maxExcerptLength = 120) {
        ArgumentNullException.ThrowIfNull(pattern);
        ValidateSearchLimits(maxMatches, maxExcerptLength);
        return VisitSpan((pattern, maxMatches, maxExcerptLength), static (body, query) => {
            MatchSelection selected = new(query.maxMatches);
            foreach (var match in query.pattern.EnumerateMatches(body)) {
                if (!selected.Add(match.Index, match.Length)) { break; }
            }
            return selected.SearchResult(body, query.maxExcerptLength, query.pattern.RightToLeft);
        });
    }

    /// <summary>Replaces the nonempty ordinal literal only when it has exactly one possible start in the current text.</summary>
    /// <param name="oldText">Exact text to locate. Overlapping occurrences are distinct candidates.</param>
    /// <param name="newText">Literal replacement. An empty string deletes the unique match.</param>
    /// <param name="maxCandidates">Maximum number of pre-edit candidates to return, at least two.</param>
    /// <param name="maxExcerptLength">Positive combined budget for the verbatim text immediately before and after each candidate, in UTF-16 code units.</param>
    public ReplaceUniqueResult ReplaceUnique(string oldText, string newText, int maxCandidates = 4, int maxExcerptLength = 120) {
        ArgumentException.ThrowIfNullOrEmpty(oldText);
        ArgumentNullException.ThrowIfNull(newText);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCandidates, 2);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxExcerptLength);
        var found = VisitSpan((oldText, maxCandidates, maxExcerptLength), static (body, query) =>
            FindLiteral(body, query.oldText, query.maxCandidates, overlap: true).ReplacementResult(body, query.maxExcerptLength));
        if (found.Status == ReplaceUniqueStatus.Replaced && !string.Equals(oldText, newText, StringComparison.Ordinal)) {
            Replace(found.Candidates[0].Start, oldText.Length, newText);
        }
        return found;
    }

    private static void ValidateSearchLimits(int maxMatches, int maxExcerptLength) {
        ArgumentOutOfRangeException.ThrowIfNegative(maxMatches);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxExcerptLength);
    }

    private static MatchSelection FindLiteral(ReadOnlySpan<char> body, string pattern, int limit, bool overlap) {
        MatchSelection selected = new(limit);
        int cursor = 0;
        while (true) {
            int offset = body[cursor..].IndexOf(pattern, StringComparison.Ordinal);
            if (offset < 0) { return selected; }
            int start = cursor + offset;
            if (!selected.Add(start, pattern.Length)) { return selected; }
            cursor = start + (overlap ? 1 : pattern.Length);
        }
    }

    private readonly record struct Location(int Start, int Length, long Line, long EndLine);

    private struct MatchSelection(int limit) {
        private List<(int Start, int Length)>? _ranges;
        private bool _hasMore;

        public bool Add(int start, int length) {
            if ((_ranges?.Count ?? 0) == limit) {
                _hasMore = true;
                return false;
            }
            (_ranges ??= []).Add((start, length));
            return true;
        }

        public readonly TextSearchResult SearchResult(ReadOnlySpan<char> body, int budget, bool rightToLeft = false) =>
            _ranges is null && !_hasMore ? TextSearchResult.Empty : new(Render(body, budget, rightToLeft, SearchExcerpt), _hasMore);

        public readonly ReplaceUniqueResult ReplacementResult(ReadOnlySpan<char> body, int budget) {
            var candidates = Render(body, budget, rightToLeft: false, ReplacementContext);
            var status = candidates.Length switch {
                0 => ReplaceUniqueStatus.NotFound,
                1 => ReplaceUniqueStatus.Replaced,
                _ => ReplaceUniqueStatus.Ambiguous
            };
            return new(status, candidates, _hasMore);
        }

        private readonly T[] Render<T>(ReadOnlySpan<char> body, int budget, bool rightToLeft,
            Func<ReadOnlySpan<char>, Location, int, T> describe) {
            if (_ranges is null) { return []; }
            var matches = new T[_ranges.Count];
            LineCursor begins = default, ends = default;
            for (int i = 0; i < matches.Length; i++) {
                var (start, length) = _ranges[rightToLeft ? matches.Length - 1 - i : i];
                long line = begins.At(body, start);
                // End positions also advance in document order, including overlapping equal-length literals.
                if (begins.Offset >= ends.Offset) { ends = begins; }
                long endLine = ends.At(body, start + Math.Max(0, length - 1));
                matches[i] = describe(body, new(start, length, line, endLine), budget);
            }
            return matches;
        }
    }

    private static TextMatch SearchExcerpt(ReadOnlySpan<char> body, Location hit, int budget) {
        int window = Math.Min(budget, body.Length);
        int centeredStart = Math.Clamp(hit.Start + (hit.Length - window) / 2, 0, body.Length - window);
        int left = centeredStart;
        int right = left + window;
        if (SplitsSurrogate(body, left)) { left++; }
        if (SplitsSurrogate(body, right)) { right--; }
        if (left == right && window != 0) {
            if (window == 2) {
                // The window straddled adjacent surrogate pairs. Either complete pair fits.
                left -= 2;
            }
            else if (AdjacentSingleUnit(body, hit, centeredStart) is int at) {
                left = at;
                right = at + 1;
            }
        }
        return new(hit.Start, hit.Length, hit.Line, hit.EndLine, left, body[left..right].ToString(), right < body.Length);
    }

    // A one-unit window can move to a neighbor of the central pair within the match.
    private static int? AdjacentSingleUnit(ReadOnlySpan<char> body, Location hit, int center) {
        int pairStart = char.IsHighSurrogate(body[center]) ? center : center - 1;
        if (pairStart > hit.Start && !char.IsSurrogate(body[pairStart - 1])) { return pairStart - 1; }
        int after = pairStart + 2;
        if (after < hit.Start + hit.Length && !char.IsSurrogate(body[after])) { return after; }
        return null;
    }

    private static ReplaceCandidate ReplacementContext(ReadOnlySpan<char> body, Location hit, int budget) {
        int end = hit.Start + hit.Length;
        // Give both sides a share, then reuse space freed by a text boundary or a surrogate pair.
        int left = ContextStart(body, hit.Start, budget / 2);
        int right = end + Math.Min(budget - (hit.Start - left), body.Length - end);
        if (right > end && SplitsSurrogate(body, right)) { right--; }
        left = ContextStart(body, hit.Start, budget - (right - end));
        return new(hit.Start, hit.Length, hit.Line, hit.EndLine, body[left..hit.Start].ToString(),
            body[end..right].ToString(), right < body.Length);
    }

    private static int ContextStart(ReadOnlySpan<char> body, int end, int budget) {
        int start = end - Math.Min(end, budget);
        return start < end && SplitsSurrogate(body, start) ? start + 1 : start;
    }

    private static bool SplitsSurrogate(ReadOnlySpan<char> body, int at) =>
        at > 0 && at < body.Length && char.IsSurrogatePair(body[at - 1], body[at]);

    private struct LineCursor {
        public int Offset { get; private set; }
        private long _lineBreaks;

        public long At(ReadOnlySpan<char> body, int target) {
            while (Offset < target) {
                int relative = body[Offset..target].IndexOfAny('\r', '\n');
                if (relative < 0) { break; }
                int at = Offset + relative;
                Offset = at + 1;
                if (body[at] == '\n' || Offset == body.Length || body[Offset] != '\n') { _lineBreaks++; }
            }
            Offset = target;
            return _lineBreaks + 1;
        }
    }
}
