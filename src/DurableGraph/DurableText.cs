using System.Text;

namespace Atelia.DurableGraph;

/// <summary>A mutable, reference-identity text object with immutable content snapshots.</summary>
/// <remarks>Use the editor serially. All positions and lengths count UTF-16 code units.</remarks>
public sealed class DurableText {
    private TextPieceView _view;
    private object? _sourceToken;
    private string? _visitText;

    /// <summary>Creates an editor with the supplied text, or empty text when called without arguments.</summary>
    public DurableText(string text = "") {
        ArgumentNullException.ThrowIfNull(text);
        _view = TextPieceView.FromString(text);
    }

    private DurableText(TextPieceView view, object sourceToken) {
        _view = view;
        _sourceToken = sourceToken;
    }

    /// <summary>The current number of UTF-16 code units.</summary>
    public int Length => _view.Length;

    /// <summary>Replaces a range in the current UTF-16 coordinates.</summary>
    /// <param name="start">Zero-based offset. The end of the text is a valid insertion point.</param>
    /// <param name="length">Number of code units to remove. Use zero to insert.</param>
    /// <param name="replacement">Text to insert. Use an empty string to delete the range.</param>
    public void Replace(int start, int length, string replacement) {
        ArgumentNullException.ThrowIfNull(replacement);
        TextPieceView.ValidateRange(Length, start, length);
        long nextLength = (long)Length - length + replacement.Length;
        if (nextLength > int.MaxValue) { throw new ArgumentOutOfRangeException(nameof(replacement)); }
        if (length == 0 && replacement.Length == 0) { return; }
        SetView(_view.Apply([new(start, length, replacement)], (int)nextLength));
    }

    /// <summary>Applies a batch whose UTF-16 coordinates all refer to the text on entry.</summary>
    /// <param name="edits">Edits in any order. Validation errors for individual edits identify their original input index.</param>
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
        if (normalized.Length != 0) { SetView(entry.Apply(normalized, nextLength)); }
    }

    /// <summary>Captures an immutable view of the current text. Later edits and saves leave it readable.</summary>
    public TextSnapshot Snapshot() => new(_view);

    /// <summary>Synchronously visits the complete UTF-16 text as it was on entry and returns the visitor's result.</summary>
    /// <typeparam name="TContext">Caller state passed by value. It can be a ref struct, such as a span over caller-owned storage.</typeparam>
    /// <typeparam name="TResult">The visitor's result type. A ref struct cannot be returned through this method.</typeparam>
    /// <param name="context">State passed to the visitor, so a static callback can use it without capturing locals.</param>
    /// <param name="visitor">Callback invoked once, including for empty text. Its span is valid for this call only.</param>
    /// <remarks>
    /// Empty and single-piece text require no copy. The first visit to a fragmented version copies
    /// the full text into a string. This editor reuses that string for later visits to the same
    /// version. The visitor may edit this object and visit it again on the same thread. The nested
    /// visit sees the edits while the outer span stays stable. Visitor exceptions propagate and
    /// completed edits remain visible.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="visitor"/> is null.</exception>
    public TResult VisitSpan<TContext, TResult>(TContext context, Func<ReadOnlySpan<char>, TContext, TResult> visitor)
        where TContext : allows ref struct {
        ArgumentNullException.ThrowIfNull(visitor);
        TextPieceView view = _view;
        if (view.Length == 0) { return visitor(ReadOnlySpan<char>.Empty, context); }
        if (view.PieceCount == 1) { return visitor(view.Pieces[0].Memory.Span, context); }

        string content = _visitText ??= view.ToText();
        return visitor(content.AsSpan(), context);
    }

    /// <summary>Copies exactly <c>destination.Length</c> UTF-16 code units starting at <paramref name="start"/>.</summary>
    public void CopyTo(int start, Span<char> destination) => _view.CopyTo(start, destination);
    /// <summary>Appends the complete current text to the builder, preserving its existing contents.</summary>
    public void AppendTo(StringBuilder destination) => AppendTo(destination, 0, Length);
    /// <summary>Appends a UTF-16 range to the builder. A destination failure can leave a prefix appended.</summary>
    public void AppendTo(StringBuilder destination, int start, int length) {
        ArgumentNullException.ThrowIfNull(destination);
        TextPieceView.ValidateRange(Length, start, length);
        _view.AppendTo(destination, start, length);
    }
    /// <summary>Returns the current text, preserving every UTF-16 code unit including isolated surrogates.</summary>
    /// <remarks>Reuses the current version's string if a visit already materialized it. Calling this method alone does not populate the visit cache.</remarks>
    public override string ToString() => _visitText ?? _view.ToText();

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
            if (byPieces || byBacking) {
                string? cached = ReferenceEquals(view.ContentToken, _view.ContentToken) ? _visitText : null;
                next = TextPieceView.FromString(cached ?? view.ToText(), sourceOffset: 0, view.ContentToken);
            } else { next = view.Rebind(); }
        }
        return new(this, view, next, captured.ContentToken);
    }

    // No allocation, user callbacks, or recapture after graph publication. Editing after
    // capture invalidates the pending physical state even if the text later becomes equal.
    internal bool TryInstall(DurableTextAcceptance prepared) {
        if (!ReferenceEquals(prepared.Owner, this) || !ReferenceEquals(prepared.CapturedView, _view)) { return false; }
        SetView(prepared.NextView);
        _sourceToken = prepared.SourceToken;
        return true;
    }

    private void SetView(TextPieceView next) {
        // A save may rebind the pieces without changing their content.
        if (next.PieceCount <= 1 || !ReferenceEquals(next.ContentToken, _view.ContentToken)) { _visitText = null; }
        _view = next;
    }

    private static TextEdit[] Normalize(int length, IReadOnlyList<TextEdit> edits, out int nextLength) {
        ArgumentNullException.ThrowIfNull(edits);
        long total = length;
        var indexed = new (TextEdit Edit, int Order)[edits.Count];
        for (int i = 0; i < indexed.Length; i++) {
            TextEdit edit = edits[i];
            if (edit.Replacement is null) {
                throw new ArgumentNullException(nameof(edits), $"edits[{i}].Replacement must contain a string.");
            }
            if (edit.Start < 0 || edit.Start > length) {
                throw new ArgumentOutOfRangeException(nameof(edits), edit.Start, $"edits[{i}].Start is outside the text.");
            }
            if (edit.Length < 0 || edit.Length > length - edit.Start) {
                throw new ArgumentOutOfRangeException(nameof(edits), edit.Length, $"edits[{i}].Length is outside the available range.");
            }
            total += (long)edit.Replacement.Length - edit.Length;
            indexed[i] = (edit, i);
        }
        if (total > int.MaxValue) { throw new ArgumentOutOfRangeException(nameof(edits), "The resulting text is too long."); }
        Array.Sort(indexed, static (a, b) => {
            int order = a.Edit.Start.CompareTo(b.Edit.Start);
            return order != 0 ? order : a.Order.CompareTo(b.Order);
        });
        List<TextEdit> result = [];
        int previousEnd = 0;
        for (int i = 0; i < indexed.Length;) {
            int start = indexed[i].Edit.Start;
            if (start < previousEnd) { throw new ArgumentException($"edits[{indexed[i].Order}].Start falls inside another edit.", nameof(edits)); }
            int removed = 0;
            string? replacement = null;
            List<string> inserted = [];
            while (i < indexed.Length && indexed[i].Edit.Start == start) {
                var (edit, order) = indexed[i++];
                if (edit.Length == 0) { inserted.Add(edit.Replacement); }
                else {
                    if (replacement is not null) { throw new ArgumentException($"edits[{order}].Start already has a replacement.", nameof(edits)); }
                    removed = edit.Length;
                    replacement = edit.Replacement;
                }
            }
            if (replacement is not null) { inserted.Add(replacement); }
            string text = inserted.Count == 1 ? inserted[0] : string.Concat(inserted);
            if (removed != 0 || text.Length != 0) { result.Add(new(start, removed, text)); }
            previousEnd = start + removed;
        }
        nextLength = (int)total; // Valid, disjoint ranges can remove at most the original length.
        return result.ToArray();
    }
}
