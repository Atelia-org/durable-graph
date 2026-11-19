namespace Atelia.Text;

// Frozen capture content and provenance identity. There is no live editor, old state,
// persistent address, or recursive source snapshot behind either token.
internal sealed class FrozenText {
    internal FrozenText(TextSnapshot content, object? sourceToken = null) {
        ArgumentNullException.ThrowIfNull(content);
        Content = content;
        SourceToken = sourceToken;
    }
    internal TextSnapshot Content { get; }
    internal object ContentToken => Content.ContentToken;
    internal object? SourceToken { get; }
}
