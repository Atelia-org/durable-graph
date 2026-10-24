using Atelia.DurableGraph;

namespace EventReferenceConsumer;

[DurableType("EventReference.Consumer.State", 1)]
public sealed partial class ReferenceState : IDurableObject {
    [DurableField(1)] public EventRef Direct;
    [DurableField(2)] public EventRef? Optional;
    [DurableField(3)] public EventRef? Absent;
    [DurableField(4)] public EventRef Unbound;
    [DurableField(5)] public List<EventRef> References = [];
    [DurableField(6)] public EventRef[] ReferenceArray = [];
    [DurableField(7)] public int Progress;
    [Transient] public int Scratch = 41;

    public static bool RejectConstruction;
    public ReferenceState() {
        if (RejectConstruction) { throw new InvalidOperationException("Restoration executed the domain constructor."); }
    }
}

[DurableType("EventReference.Consumer.Fact", 1)]
public sealed partial class Fact : IDurableObject {
    [DurableField(1)] public long Sequence;
    [DurableField(2)] public string Text = "";
}
