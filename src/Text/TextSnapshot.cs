using System.Text;

namespace Atelia.Text;

/// <summary>An immutable text version that stays readable independently of the editor that created it.</summary>
/// <remarks>A snapshot can retain backing strings larger than its visible content.</remarks>
public sealed class TextSnapshot {
    internal TextSnapshot(TextPieceView view, object? ownerToken = null) {
        View = view;
        OwnerToken = ownerToken;
    }
    internal TextPieceView View { get; }
    // An opaque identity lets a snapshot outlive the editor without retaining it or its newer versions.
    internal object? OwnerToken { get; }
    internal object ContentToken => View.ContentToken;
    internal static TextSnapshot FromString(string text) => new(TextPieceView.FromString(text));

    /// <summary>The number of UTF-16 code units, without newline or Unicode normalization.</summary>
    public int Length => View.Length;

    /// <summary>Copies exactly <c>destination.Length</c> UTF-16 code units starting at <paramref name="start"/>.</summary>
    /// <remarks>Pass a slice of a larger buffer to read only the requested range.</remarks>
    public void CopyTo(int start, Span<char> destination) => View.CopyTo(start, destination);

    /// <summary>Appends the complete text without clearing or taking ownership of the builder.</summary>
    public void AppendTo(StringBuilder destination) => AppendTo(destination, 0, Length);

    /// <summary>Appends a UTF-16 range. A destination failure does not roll back previous appends.</summary>
    public void AppendTo(StringBuilder destination, int start, int length) {
        ArgumentNullException.ThrowIfNull(destination);
        TextPieceView.ValidateRange(Length, start, length);
        View.AppendTo(destination, start, length);
    }

    /// <summary>Returns the content, preserving every UTF-16 code unit including isolated surrogates.</summary>
    public override string ToString() => View.ToText();

    /// <summary>Finds an ordinal match, including across physical pieces, at or after start.</summary>
    /// <remarks>An empty value matches start. A missing value returns -1. Offsets and lengths count UTF-16 code units.</remarks>
    public int IndexOf(string value, int start = 0) {
        ArgumentNullException.ThrowIfNull(value);
        TextPieceView.ValidateRange(Length, start, 0);
        if (value.Length == 0) { return start; }
        if (value.Length > Length - start) { return -1; }

        // KMP carries a match across pieces without materializing the text or restarting
        // at each prefix. The one-character path needs no prefix table.
        int[]? prefixes = value.Length == 1 ? null : new int[value.Length];
        if (prefixes is not null) {
            for (int i = 1, matched = 0; i < value.Length; i++) {
                while (matched != 0 && value[i] != value[matched]) { matched = prefixes[matched - 1]; }
                if (value[i] == value[matched]) { matched++; }
                prefixes[i] = matched;
            }
        }
        int offset = 0, match = 0;
        foreach (TextPiece piece in View.Pieces) {
            int skip = Math.Max(0, start - offset);
            if (skip < piece.Length) {
                ReadOnlySpan<char> span = piece.Memory.Span;
                if (prefixes is null) {
                    int found = span[skip..].IndexOf(value[0]);
                    if (found >= 0) { return offset + skip + found; }
                } else {
                    for (int i = skip; i < span.Length; i++) {
                        while (match != 0 && span[i] != value[match]) { match = prefixes[match - 1]; }
                        if (span[i] == value[match]) { match++; }
                        if (match == value.Length) { return offset + i - value.Length + 1; }
                    }
                }
            }
            offset += piece.Length;
        }
        return -1;
    }

    // Internal consumers share sequential reads; public callers do not acquire physical chunks.
    internal IEnumerable<ReadOnlyMemory<char>> GetChunks() => View.GetChunks();

    internal bool ContentEquals(TextSnapshot other) {
        ArgumentNullException.ThrowIfNull(other);
        if (ReferenceEquals(ContentToken, other.ContentToken)) { return true; }
        if (Length != other.Length) { return false; }
        using IEnumerator<ReadOnlyMemory<char>> left = GetChunks().GetEnumerator();
        using IEnumerator<ReadOnlyMemory<char>> right = other.GetChunks().GetEnumerator();
        ReadOnlyMemory<char> a = default, b = default;
        int remaining = Length;
        while (remaining != 0) {
            if (a.IsEmpty) {
                if (!left.MoveNext()) { throw new InvalidOperationException("Incomplete text pieces."); }
                a = left.Current;
            }
            if (b.IsEmpty) {
                if (!right.MoveNext()) { throw new InvalidOperationException("Incomplete text pieces."); }
                b = right.Current;
            }
            int count = Math.Min(a.Length, b.Length);
            if (!a.Span[..count].SequenceEqual(b.Span[..count])) { return false; }
            a = a[count..]; b = b[count..]; remaining -= count;
        }
        return true;
    }
}
