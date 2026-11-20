namespace Atelia.Text;

/// <summary>A match and a bounded slice of the text examined by the search.</summary>
/// <param name="Start">Zero-based UTF-16 offset of the match in that version of the text.</param>
/// <param name="Length">Match length in UTF-16 code units. A regex match can be empty.</param>
/// <param name="Line">One-based line containing the first code unit or the empty match position.</param>
/// <param name="EndLine">One-based line of the last code unit, or <paramref name="Line"/> for an empty match.</param>
/// <param name="ExcerptStart">Zero-based UTF-16 offset of the first code unit in the excerpt.</param>
/// <param name="Excerpt">Verbatim slice around the match. It can contain line breaks or show only part of a long match.</param>
/// <param name="TruncatedAfter">Whether the source text continues after the excerpt.</param>
public readonly record struct TextMatch(int Start, int Length, long Line, long EndLine,
    int ExcerptStart, string Excerpt, bool TruncatedAfter) {
    /// <summary>Whether the source text continues before the excerpt.</summary>
    public bool TruncatedBefore => ExcerptStart > 0;
}

/// <summary>Matches in document order, with excerpts owned by the result.</summary>
/// <param name="Matches">The selected matches.</param>
/// <param name="HasMore">True if the search found at least one additional match beyond the limit.</param>
public sealed record TextSearchResult(IReadOnlyList<TextMatch> Matches, bool HasMore) {
    internal static readonly TextSearchResult Empty = new(Array.Empty<TextMatch>(), false);
}

/// <summary>Outcome of replacing one uniquely identified literal.</summary>
public enum ReplaceUniqueStatus {
    /// <summary>Exactly one match was found and its replacement accepted.</summary>
    Replaced,
    /// <summary>No occurrence of the old text was found.</summary>
    NotFound,
    /// <summary>Several possible starts were found. The editor preserved the text.</summary>
    Ambiguous,
}

/// <summary>Outcome of a unique replacement and the locations found before the edit.</summary>
/// <param name="Status">Whether the edit was accepted or could not identify a unique range.</param>
/// <param name="Candidates">Bounded candidates in document order, including the replaced range on success.</param>
/// <param name="HasMore">True when at least one additional candidate exceeded the limit.</param>
public sealed record ReplaceUniqueResult(ReplaceUniqueStatus Status, IReadOnlyList<ReplaceCandidate> Candidates, bool HasMore);

/// <summary>One possible location of the supplied old text, with adjacent text from the same version.</summary>
/// <param name="Start">Zero-based UTF-16 offset of the old text.</param>
/// <param name="Length">UTF-16 length of the old text.</param>
/// <param name="Line">One-based line containing its first code unit.</param>
/// <param name="EndLine">One-based line containing its last code unit.</param>
/// <param name="Before">Verbatim text immediately preceding the candidate. Use a suffix to extend the old text on the left.</param>
/// <param name="After">Verbatim text immediately following the candidate. Use a prefix to extend the old text on the right.</param>
/// <param name="TruncatedAfter">Whether the source continues beyond <paramref name="After"/>.</param>
public readonly record struct ReplaceCandidate(int Start, int Length, long Line, long EndLine,
    string Before, string After, bool TruncatedAfter) {
    /// <summary>Whether the source continues before <see cref="Before"/>.</summary>
    public bool TruncatedBefore => Start > Before.Length;
}
