using Atelia.DurableGraph;
using Atelia.Text;

namespace TextEditorConsumer;

[DurableType("TextEditor.Consumer.World", 1)]
public sealed partial class World : IDurableObject {
    [DurableField(1)] public GraphRef Original;
    [DurableField(2)] public GraphRef Latest;
}

[DurableType("TextEditor.Consumer.Entry", 1)]
public sealed partial class Entry : IDurableObject {
    [DurableField(1)] public TextEditor Body = new();
    [DurableField(2)] public TextEditor Alias = new();
    [DurableField(3)] public List<TextEditor> Bodies = [];
    [DurableField(4)] public TextEditor? Missing;
    [DurableField(5)] public TextEditor Empty = new();
    [DurableField(6)] public string Gist = "one-line";
}
