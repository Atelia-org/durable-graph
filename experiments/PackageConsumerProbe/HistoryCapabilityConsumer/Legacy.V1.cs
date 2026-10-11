using Atelia.DurableGraph;

namespace HistoryCapabilityPackageConsumerProbe;

[DurableType("package.history-legacy", 1)]
public sealed partial class Legacy : IDurableObject {
    [DurableField(1)] private int _value;
    [DurableField(2)] private Legacy? _next;
    // Persisted business content; its scalar value and history remain unchanged under DB-086.
    [DurableField(3)] private readonly ulong _createdAtTicks;

    internal Legacy(int value) { _value = value; _next = this; _createdAtTicks = 638_625_600_000_000_000; }
    internal Legacy SnapshotEvent() => new(_value);
    internal void Change(int value) { _value = value; }
    internal bool HasSelfCycle => ReferenceEquals(this, _next) && _createdAtTicks == 638_625_600_000_000_000;
}
