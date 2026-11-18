using Atelia.DurableGraph;

namespace DurableTextConsumer;

[DurableType("DurableText.Consumer.World", 1)]
public sealed partial class World : IDurableObject {
    [DurableField(1)] public GraphRef Original;
    [DurableField(2)] public GraphRef Latest;
}

[DurableType("DurableText.Consumer.Entry", 1)]
public sealed partial class Entry : IDurableObject {
    [DurableField(1)] public DurableText Body = new();
    [DurableField(2)] public DurableText Alias = new();
    [DurableField(3)] public List<DurableText> Bodies = [];
    [DurableField(4)] public DurableText? Missing;
    [DurableField(5)] public DurableText Empty = new();
    [DurableField(6)] public string Gist = "one-line";
}
