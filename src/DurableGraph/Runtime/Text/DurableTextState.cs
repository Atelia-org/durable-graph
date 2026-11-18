namespace Atelia.DurableGraph;

// Frozen capture content and provenance identity. There is no live editor, old state,
// persistent address, or recursive source snapshot behind either token.
internal sealed class DurableTextState {
    internal DurableTextState(TextSnapshot content, object? sourceToken = null) {
        ArgumentNullException.ThrowIfNull(content);
        Content = content;
        SourceToken = sourceToken;
    }
    internal TextSnapshot Content { get; }
    internal object ContentToken => Content.ContentToken;
    internal object? SourceToken { get; }

    internal IReadOnlyList<TextSourcePiece> BuildSourcePlan() {
        List<TextSourcePiece> plan = [];
        foreach (TextPiece piece in Content.View.Pieces) {
            if (piece.BaseOffset >= 0) {
                if (plan.Count != 0 && plan[^1].IsCopy &&
                    (long)plan[^1].BaseOffset + plan[^1].Length == piece.BaseOffset) {
                    TextSourcePiece last = plan[^1];
                    plan[^1] = TextSourcePiece.Copy(last.BaseOffset, checked(last.Length + piece.Length));
                } else { plan.Add(TextSourcePiece.Copy(piece.BaseOffset, piece.Length)); }
            } else { plan.Add(TextSourcePiece.New(piece.Memory)); }
        }
        return plan.AsReadOnly();
    }
}

internal readonly record struct TextSourcePiece(bool IsCopy, int BaseOffset, int Length, ReadOnlyMemory<char> Literal) {
    internal static TextSourcePiece Copy(int start, int length) => new(true, start, length, default);
    internal static TextSourcePiece New(ReadOnlyMemory<char> literal) => new(false, -1, literal.Length, literal);
}

// Owned only by the short-lived capture/save context, never by a snapshot or frozen DTO.
internal sealed class DurableTextAcceptance(DurableText owner, TextPieceView capturedView,
    TextPieceView nextView, object sourceToken) {
    internal DurableText Owner { get; } = owner;
    internal TextPieceView CapturedView { get; } = capturedView;
    internal TextPieceView NextView { get; } = nextView;
    internal object SourceToken { get; } = sourceToken;
}
