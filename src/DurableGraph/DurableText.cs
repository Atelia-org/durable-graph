using System.Buffers;
using System.Text;

namespace Atelia.DurableGraph;

/// <summary>A mutable, reference-identity text object with immutable content snapshots.</summary>
/// <remarks>Use serially. Saving a graph does not turn snapshots into persistence credentials.</remarks>
public sealed class DurableText {
    private TextPieceView _view;
    private object? _sourceToken;

    public DurableText(string text = "") {
        ArgumentNullException.ThrowIfNull(text);
        _view = TextPieceView.FromString(text);
    }

    private DurableText(TextPieceView view, object sourceToken) {
        _view = view;
        _sourceToken = sourceToken;
    }

    public int Length => _view.Length;

    /// <summary>Replaces a range in the current UTF-16 coordinates.</summary>
    public void Replace(int start, int length, string replacement) {
        ArgumentNullException.ThrowIfNull(replacement);
        TextPieceView.ValidateRange(Length, start, length);
        long nextLength = (long)Length - length + replacement.Length;
        if (nextLength > int.MaxValue) { throw new ArgumentOutOfRangeException(nameof(replacement)); }
        if (length == 0 && replacement.Length == 0) { return; }
        _view = _view.Apply([new(start, length, replacement)], (int)nextLength);
    }

    /// <summary>Applies a batch whose coordinates all refer to the text on entry.</summary>
    /// <remarks>
    /// The input is copied and validated before publication. Overlapping replacements and
    /// insertions inside a replacement are rejected. At a common start, zero-length inserts
    /// keep their input order and precede the single nonempty-range replacement.
    /// Do not mutate the supplied list during this call.
    /// </remarks>
    public void ApplyEdits(IReadOnlyList<TextEdit> edits) {
        TextPieceView entry = _view;
        TextEdit[] normalized = Normalize(entry.Length, edits, out int nextLength);
        // Count/indexers belong to arbitrary caller code. A nested edit remains visible,
        // but must not make this outer batch apply old coordinates to a different view.
        if (!ReferenceEquals(entry, _view)) { throw new InvalidOperationException("The text changed while reading the edit batch."); }
        if (normalized.Length != 0) { _view = entry.Apply(normalized, nextLength); }
    }

    public TextSnapshot Snapshot() => new(_view);

    /// <summary>Synchronously visits the complete UTF-16 text as it was on entry and returns the visitor's result.</summary>
    /// <param name="context">Caller state passed to the visitor.</param>
    /// <param name="visitor">Callback invoked once, including for empty text. Its span is valid for this call only.</param>
    /// <remarks>
    /// Empty and single-piece text require no copy. Each visit to fragmented text copies the full
    /// content into a rented buffer before invoking the callback. The visitor may edit this object
    /// and visit it again on the same thread. The nested visit sees the edits while the outer span
    /// stays stable. Visitor exceptions propagate and completed edits remain visible.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="visitor"/> is null.</exception>
    public TResult VisitSpan<TContext, TResult>(TContext context, Func<ReadOnlySpan<char>, TContext, TResult> visitor) {
        ArgumentNullException.ThrowIfNull(visitor);
        TextPieceView view = _view;
        if (view.Length == 0) { return visitor(ReadOnlySpan<char>.Empty, context); }
        if (view.PieceCount == 1) { return visitor(view.Pieces[0].Memory.Span, context); }

        char[] buffer = ArrayPool<char>.Shared.Rent(view.Length);
        try {
            Span<char> content = buffer.AsSpan(0, view.Length);
            view.CopyTo(0, content);
            return visitor(content, context);
        } finally { ArrayPool<char>.Shared.Return(buffer); }
    }

    public void CopyTo(int start, Span<char> destination) => _view.CopyTo(start, destination);
    public void AppendTo(StringBuilder destination) => AppendTo(destination, 0, Length);
    public void AppendTo(StringBuilder destination, int start, int length) {
        ArgumentNullException.ThrowIfNull(destination);
        TextPieceView.ValidateRange(Length, start, length);
        _view.AppendTo(destination, start, length);
    }
    public override string ToString() => _view.ToText();

    internal DurableTextState CaptureState() => new(new TextSnapshot(_view), _sourceToken);

    internal static DurableText Restore(DurableTextState baseline) {
        ArgumentNullException.ThrowIfNull(baseline);
        return new(baseline.Content.View.Rebind(), baseline.ContentToken);
    }

    // Preparing may allocate, but never modifies this editor. The pending capture owns
    // the result until graph installation; Event capture must not invoke installation.
    internal DurableTextAcceptance PrepareAcceptance(DurableTextState captured) {
        ArgumentNullException.ThrowIfNull(captured);
        TextPieceView view = captured.Content.View;
        TextPieceView next;
        if (ReferenceEquals(captured.SourceToken, captured.ContentToken)) {
            // An unchanged, already rebound body needs neither a piece walk nor compaction.
            next = view;
        } else {
            bool byPieces = view.PieceCount >= 2 &&
                (long)view.PieceCount * view.PieceCount >= ((long)view.Length + 15) / 16;
            bool byBacking = view.LargestBacking > 4L * Math.Max(view.Length, 1) &&
                (long)view.LargestBacking - view.Length >= 4096;
            next = byPieces || byBacking
                ? TextPieceView.FromString(view.ToText(), sourceOffset: 0, view.ContentToken)
                : view.Rebind();
        }
        return new(this, view, next, captured.ContentToken);
    }

    // No allocation, user callbacks, or recapture after graph publication. Editing after
    // capture invalidates the pending physical state even if the text later becomes equal.
    internal bool TryInstall(DurableTextAcceptance prepared) {
        if (!ReferenceEquals(prepared.Owner, this) || !ReferenceEquals(prepared.CapturedView, _view)) { return false; }
        _view = prepared.NextView;
        _sourceToken = prepared.SourceToken;
        return true;
    }

    private static TextEdit[] Normalize(int length, IReadOnlyList<TextEdit> edits, out int nextLength) {
        ArgumentNullException.ThrowIfNull(edits);
        long total = length;
        var indexed = new (TextEdit Edit, int Order)[edits.Count];
        for (int i = 0; i < indexed.Length; i++) {
            TextEdit edit = edits[i];
            ArgumentNullException.ThrowIfNull(edit.Replacement);
            TextPieceView.ValidateRange(length, edit.Start, edit.Length);
            total += (long)edit.Replacement.Length - edit.Length;
            indexed[i] = (edit, i);
        }
        if (total < 0 || total > int.MaxValue) { throw new ArgumentOutOfRangeException(nameof(edits)); }
        nextLength = (int)total;
        Array.Sort(indexed, static (a, b) => {
            int order = a.Edit.Start.CompareTo(b.Edit.Start);
            return order != 0 ? order : a.Order.CompareTo(b.Order);
        });
        List<TextEdit> result = [];
        int previousEnd = 0;
        for (int i = 0; i < indexed.Length;) {
            int start = indexed[i].Edit.Start;
            if (start < previousEnd) { throw new ArgumentException("Overlapping edits.", nameof(edits)); }
            int removed = 0;
            string? replacement = null;
            List<string> inserted = [];
            while (i < indexed.Length && indexed[i].Edit.Start == start) {
                TextEdit edit = indexed[i++].Edit;
                if (edit.Length == 0) { inserted.Add(edit.Replacement); }
                else {
                    if (replacement is not null) { throw new ArgumentException("Multiple replacements at one position.", nameof(edits)); }
                    removed = edit.Length;
                    replacement = edit.Replacement;
                }
            }
            if (replacement is not null) { inserted.Add(replacement); }
            string text = inserted.Count == 1 ? inserted[0] : string.Concat(inserted);
            if (removed != 0 || text.Length != 0) { result.Add(new(start, removed, text)); }
            previousEnd = start + removed;
        }
        return result.ToArray();
    }
}
