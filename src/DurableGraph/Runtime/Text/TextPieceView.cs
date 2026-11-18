using System.Text;

namespace Atelia.DurableGraph;

// Extracted from the DB-101 M1 native deferred-materialization experiment's selected
// sqrt(Length)/4 policy. Physical string slices and logical provenance share one piece;
// published lists are never changed. No experiment factory, storage, or counters remain.
internal sealed class TextPieceView {
    private readonly List<TextPiece> _pieces;
    private int[]? _ends;

    private TextPieceView(List<TextPiece> pieces, int length, int largestBacking, object? contentToken = null) {
        _pieces = pieces;
        Length = length;
        LargestBacking = largestBacking;
        ContentToken = contentToken ?? new object();
    }
    internal object ContentToken { get; }
    internal int Length { get; }
    internal int LargestBacking { get; }
    internal int PieceCount => _pieces.Count;
    internal IReadOnlyList<TextPiece> Pieces => _pieces;

    internal static TextPieceView FromString(string text, int sourceOffset = -1, object? contentToken = null) {
        ArgumentNullException.ThrowIfNull(text);
        List<TextPiece> pieces = [];
        if (text.Length != 0) { pieces.Add(new(text, 0, text.Length, sourceOffset)); }
        return new(pieces, text.Length, text.Length, contentToken);
    }

    // The decoder transfers this fresh list and must never mutate it afterwards.
    // Decoded Copy ranges borrow physical strings, not prior Views or provenance.
    internal static TextPieceView FromOwnedPieces(List<TextPiece> pieces, int length) {
        ArgumentNullException.ThrowIfNull(pieces);
        if (length < 0) { throw new ArgumentOutOfRangeException(nameof(length)); }
        long total = 0;
        int largest = 0;
        foreach (TextPiece piece in pieces) {
            if (piece.Text is null || piece.Length <= 0 || piece.Offset < 0 ||
                (long)piece.Offset + piece.Length > piece.Text.Length || piece.BaseOffset != -1) {
                throw new ArgumentException("Invalid decoded text piece.", nameof(pieces));
            }
            total += piece.Length;
            if (total > length) { throw new ArgumentException("Decoded pieces exceed the text length.", nameof(pieces)); }
            largest = Math.Max(largest, piece.Text.Length);
        }
        if (total != length) { throw new ArgumentException("Decoded pieces do not cover the text length.", nameof(pieces)); }
        return new(pieces, length, largest);
    }

    // On false the destination may contain a prefix; the decoder discards it and
    // uses its flat fallback. The limit is checked before creating another descriptor.
    internal bool TryAppendRangeTo(List<TextPiece> target, int start, int length, int maxPieces) {
        ArgumentNullException.ThrowIfNull(target);
        if (ReferenceEquals(target, _pieces)) { throw new ArgumentException("Cannot append into a published view.", nameof(target)); }
        ArgumentOutOfRangeException.ThrowIfNegative(maxPieces);
        ValidateRange(Length, start, length);
        if (target.Count > maxPieces) { return false; }
        if (length == 0) { return true; }
        int index = Locate(start, out int within);
        while (length != 0) {
            TextPiece piece = _pieces[index++];
            int count = Math.Min(length, piece.Length - within);
            TextPiece slice = new(piece.Text, checked(piece.Offset + within), count, -1);
            if (!TryAdd(target, slice, maxPieces)) { return false; }
            length -= count;
            within = 0;
        }
        return true;
    }

    internal TextPieceView Rebind() {
        List<TextPiece> rebound = new(PieceCount);
        int offset = 0;
        foreach (TextPiece piece in _pieces) {
            Add(rebound, piece with { BaseOffset = offset });
            offset = checked(offset + piece.Length);
        }
        return new(rebound, Length, LargestBacking, ContentToken);
    }

    internal TextPieceView Apply(IReadOnlyList<TextEdit> edits, int nextLength) {
        long capacity = Math.Min(nextLength, (long)PieceCount + 2L * edits.Count);
        if (capacity > Array.MaxLength) { throw new ArgumentOutOfRangeException(nameof(edits)); }
        List<TextPiece> result = new((int)capacity);
        int pieceIndex = 0, within = 0, position = 0, largest = 0;
        void Retain(TextPiece piece) {
            Add(result, piece);
            largest = Math.Max(largest, piece.Text.Length);
        }
        void AdvanceTo(int end, bool retain) {
            while (position < end) {
                TextPiece piece = _pieces[pieceIndex];
                int count = Math.Min(end - position, piece.Length - within);
                if (retain) { Retain(piece.Slice(within, count)); }
                position += count;
                within += count;
                if (within == piece.Length) { pieceIndex++; within = 0; }
            }
        }
        foreach (TextEdit edit in edits) {
            AdvanceTo(edit.Start, retain: true);
            if (edit.Replacement.Length != 0) { Retain(new(edit.Replacement, 0, edit.Replacement.Length, -1)); }
            AdvanceTo(edit.Start + edit.Length, retain: false);
        }
        AdvanceTo(Length, retain: true);
        return new(result, nextLength, largest);
    }

    private static void Add(List<TextPiece> pieces, TextPiece piece) => _ = TryAdd(pieces, piece, int.MaxValue);

    private static bool TryAdd(List<TextPiece> pieces, TextPiece piece, int maxPieces) {
        if (pieces.Count != 0) {
            TextPiece last = pieces[^1];
            bool contiguousSource = last.BaseOffset < 0 ? piece.BaseOffset < 0
                : piece.BaseOffset >= 0 && (long)last.BaseOffset + last.Length == piece.BaseOffset;
            if (contiguousSource && ReferenceEquals(last.Text, piece.Text) &&
                (long)last.Offset + last.Length == piece.Offset) {
                pieces[^1] = last with { Length = checked(last.Length + piece.Length) };
                return true;
            }
        }
        if (pieces.Count == maxPieces) { return false; }
        pieces.Add(piece);
        return true;
    }

    internal static void ValidateRange(int length, int start, int count) {
        if (start < 0 || start > length) { throw new ArgumentOutOfRangeException(nameof(start)); }
        if (count < 0 || count > length - start) { throw new ArgumentOutOfRangeException(nameof(count)); }
    }

    internal void CopyTo(int start, Span<char> destination) {
        ValidateRange(Length, start, destination.Length);
        if (destination.IsEmpty) { return; }
        int pieceIndex = Locate(start, out int within);
        while (!destination.IsEmpty) {
            ReadOnlySpan<char> source = _pieces[pieceIndex++].Memory.Span[within..];
            int count = Math.Min(source.Length, destination.Length);
            source[..count].CopyTo(destination);
            destination = destination[count..];
            within = 0;
        }
    }

    private int Locate(int start, out int within) {
        int pieceIndex = 0;
        within = start;
        if (start != 0 && PieceCount > 1) {
            int[] ends = Volatile.Read(ref _ends) ?? BuildIndex();
            int low = 0, high = ends.Length - 1;
            while (low < high) {
                int middle = low + (high - low) / 2;
                if (start < ends[middle]) { high = middle; }
                else { low = middle + 1; }
            }
            pieceIndex = low;
            within = start - (low == 0 ? 0 : ends[low - 1]);
        }
        return pieceIndex;
    }

    private int[] BuildIndex() {
        int[] ends = new int[PieceCount];
        int total = 0;
        for (int i = 0; i < ends.Length; i++) { ends[i] = total = checked(total + _pieces[i].Length); }
        return Interlocked.CompareExchange(ref _ends, ends, null) ?? ends;
    }

    internal IEnumerable<ReadOnlyMemory<char>> GetChunks() {
        foreach (TextPiece piece in _pieces) { yield return piece.Memory; }
    }

    internal void AppendTo(StringBuilder destination, int start, int length) {
        if (length == 0) { return; }
        int offset = 0;
        foreach (TextPiece piece in _pieces) {
            int skip = Math.Max(0, start - offset);
            if (skip < piece.Length) {
                int count = Math.Min(length, piece.Length - skip);
                destination.Append(piece.Memory.Span.Slice(skip, count));
                length -= count;
                start += count;
                if (length == 0) { return; }
            }
            offset += piece.Length;
        }
    }

    internal string ToText() {
        if (Length == 0) { return string.Empty; }
        if (PieceCount == 1 && _pieces[0] is { Offset: 0 } piece && piece.Length == piece.Text.Length) { return piece.Text; }
        return string.Create(Length, this, static (destination, view) => view.CopyTo(0, destination));
    }
}

internal readonly record struct TextPiece(string Text, int Offset, int Length, int BaseOffset) {
    internal ReadOnlyMemory<char> Memory => Text.AsMemory(Offset, Length);
    internal TextPiece Slice(int start, int length) => new(Text, checked(Offset + start), length,
        BaseOffset < 0 ? -1 : checked(BaseOffset + start));
}
