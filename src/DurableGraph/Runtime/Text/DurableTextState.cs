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
}

// Owned only by the short-lived capture/save context, never by a snapshot or frozen DTO.
internal sealed class DurableTextAcceptance(DurableText owner, TextPieceView capturedView,
    TextPieceView nextView, object sourceToken) {
    internal DurableText Owner { get; } = owner;
    internal TextPieceView CapturedView { get; } = capturedView;
    internal TextPieceView NextView { get; } = nextView;
    internal object SourceToken { get; } = sourceToken;
}
