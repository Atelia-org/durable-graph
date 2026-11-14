using Atelia.DurableGraph;

namespace GraphHeapConsumer;

[DurableType("GraphHeap.Consumer.World", 1)]
public sealed partial class World : IDurableObject {
    [DurableField(1)] public Dictionary<EntryId, GraphRef> Entries = [];
    [DurableField(2)] public GraphRef Original;
    [DurableField(3)] public GraphRef Unbound;
    [Transient] public int Scratch = 41;
}

[DurableType("GraphHeap.Consumer.EntryId", 1)]
public readonly partial struct EntryId(uint value) : IEquatable<EntryId> {
    [DurableField(1)] private readonly uint _value = value;
    public bool Equals(EntryId other) => _value == other._value;
    public override bool Equals(object? obj) => obj is EntryId other && Equals(other);
    public override int GetHashCode() => _value.GetHashCode();
}

[DurableType("GraphHeap.Consumer.Entry", 1)]
public sealed partial class Entry : IDurableObject {
    [DurableField(1)] public string Text = "";
    [DurableField(2)] public Detail? Left;
    [DurableField(3)] public Detail? Right;
    [Transient] public int Scratch = 42;

    public static bool RejectConstruction;
    public Entry() {
        if (RejectConstruction) { throw new InvalidOperationException("Restoration ran the entry constructor."); }
    }
}

[DurableType("GraphHeap.Consumer.Detail", 1)]
public sealed partial class Detail : IDurableObject {
    [DurableField(1)] public int Value;
    [DurableField(2)] public Detail? Next;
}
