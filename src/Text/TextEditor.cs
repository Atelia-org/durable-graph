using System.Text;
using Atelia.DurableGraph.Serialization;

namespace Atelia.Text;

/// <summary>A mutable, reference-identity text object with immutable content snapshots.</summary>
/// <remarks>Use the editor serially. All positions and lengths count UTF-16 code units.</remarks>
[DurableLeaf("Atelia.Text.TextEditor", 1)]
public sealed partial class TextEditor {
    private TextPieceView _view;
    private object? _sourceToken;
    private string? _visitText;
    private object? _snapshotOwnerToken;
    // The receipt describes this physical view only. It carries no text or repository state.
    private LeafAcceptance? _pending;

    /// <summary>Creates an editor with the supplied text, or empty text when called without arguments.</summary>
    public TextEditor(string text = "") {
        ArgumentNullException.ThrowIfNull(text);
        _view = TextPieceView.FromString(text);
    }

    private TextEditor(TextPieceView view, object sourceToken) {
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
        ConsumeAcceptedBaseline();
        SetView(_view.Apply([new(start, length, replacement)], (int)nextLength));
    }

    /// <summary>Applies all edits if this editor still has the content version captured by <paramref name="expected"/>.</summary>
    /// <param name="expected">A snapshot from this editor. Saving alone preserves its validity, including when a save reorganizes the pieces.</param>
    /// <param name="edits">Ranges in the snapshot's UTF-16 coordinates, in any order. Do not mutate the list during this call.</param>
    /// <param name="failure">On rejection, the reason and applicable zero-based input indices. On success, the default value.</param>
    /// <returns>True on success, including an empty batch. On false, this call publishes no edits.</returns>
    /// <remarks>
    /// Overlapping removals and insertions strictly inside a removal conflict. At a common start,
    /// insertions keep input order and precede the one allowed removal. A zero-length edit with an
    /// empty replacement is ignored after range validation. An edit followed by an undo makes an
    /// older snapshot stale. Edits made by reentrant list accessors remain visible.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The snapshot or the list is null.</exception>
    public bool TryApplyEdits(TextSnapshot expected, IReadOnlyList<TextEdit> edits, out TextEditFailure failure) {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(edits);
        if (_snapshotOwnerToken is null || !ReferenceEquals(expected.OwnerToken, _snapshotOwnerToken) ||
            !ReferenceEquals(expected.ContentToken, _view.ContentToken)) {
            failure = new(TextEditFailureKind.StaleSnapshot);
            return false;
        }

        var batch = new TextEdit[edits.Count];
        for (int i = 0; i < batch.Length; i++) { batch[i] = edits[i]; }
        // Count/indexers can call back into the editor. A save may have installed new
        // provenance for the same content. Always apply to the latest physical view.
        ConsumeAcceptedBaseline();
        TextPieceView current = _view;
        if (!ReferenceEquals(expected.ContentToken, current.ContentToken)) {
            failure = new(TextEditFailureKind.StaleSnapshot);
            return false;
        }

        failure = TextEditBatch.Prepare(batch, current.Length, out int count, out int nextLength);
        if (failure.Kind != TextEditFailureKind.None) { return false; }
        if (count != 0) { SetView(current.Apply(batch.AsSpan(0, count), nextLength)); }
        return true;
    }

    /// <summary>Captures an immutable view of the current text. Later edits and saves leave it readable.</summary>
    /// <remarks>Taking a snapshot shares the current pieces in constant time. A snapshot may retain their backing strings.</remarks>
    public TextSnapshot Snapshot() => new(_view, _snapshotOwnerToken ??= new object());

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

    internal FrozenText CaptureState(LeafAcceptance? acceptance) {
        ConsumeAcceptedBaseline();
        // Concurrently prepared graph candidates can supersede a pending receipt. Missing an
        // older acceptance merely loses its Copy optimization. Event captures supply no receipt.
        if (acceptance is not null && !ReferenceEquals(_sourceToken, _view.ContentToken)) {
            _pending = acceptance;
        }
        return new(new TextSnapshot(_view), _sourceToken);
    }

    internal static TextEditor Restore(FrozenText baseline) {
        ArgumentNullException.ThrowIfNull(baseline);
        return new(baseline.Content.View.Rebind(), baseline.ContentToken);
    }

    private void ConsumeAcceptedBaseline() {
        if (_pending?.IsAccepted != true) { return; }
        TextPieceView view = _view;
        bool byPieces = view.PieceCount >= 2 &&
            (long)view.PieceCount * view.PieceCount >= ((long)view.Length + 15) / 16;
        bool byBacking = view.LargestBacking > 4L * Math.Max(view.Length, 1) &&
            (long)view.LargestBacking - view.Length >= 4096;
        TextPieceView next = byPieces || byBacking
            ? TextPieceView.FromString(_visitText ?? view.ToText(), sourceOffset: 0, view.ContentToken)
            : view.Rebind();
        SetView(next);
        _sourceToken = view.ContentToken;
    }

    private void SetView(TextPieceView next) {
        // A later acceptance for the old physical view cannot be applied to this one.
        _pending = null;
        // A save may rebind the pieces without changing their content.
        if (next.PieceCount <= 1 || !ReferenceEquals(next.ContentToken, _view.ContentToken)) { _visitText = null; }
        _view = next;
    }
}
