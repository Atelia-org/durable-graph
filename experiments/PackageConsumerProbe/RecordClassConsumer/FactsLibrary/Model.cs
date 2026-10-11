using Atelia.DurableGraph;
using RecordClassLibrary.Base;
#if HISTORY_V3
using DamageStates = Atelia.DurableGraph.Generated.Family_5265636F726444616D616765;
#endif

namespace RecordClassLibrary.Facts;

#if HISTORY_V1
[DurableType("RecordDamage", 1)]
public sealed partial class Damage<T> : Fact<T> {
    [DurableField(1)] public readonly int Amount;
    public Damage(T actor, int amount) : base(actor) { Amount = amount; }
#elif HISTORY_V2
[DurableType("RecordDamage", 1)]
public sealed partial record Damage<T>(T Actor, [field: DurableField(1)] int Amount) : Fact<T>(Actor) {
#else
[DurableType("RecordDamage", 2)]
public sealed partial record Damage<T>(T Actor, [field: DurableField(1)] long Amount) : Fact<T>(Actor) {
#endif
    // Mutable storage is intentional: record syntax does not imply deep immutability.
    [DurableField(2)] public long Counter = 638_625_600_000_000_000;
    // A real unchanged Stamp at the long endpoint occupies 10 varint bytes.
    // That makes Base larger than Counter's 9-byte change plus the 12-byte prior.
    [DurableField(3)] public readonly long Stamp = long.MaxValue;
}

public static class FactCatalog {
    public static int UpgradeCalls;
    public static void Register(IStateModelRegistration models) => Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
#if HISTORY_V3
    [DurableUpgrade(typeof(Damage<>), 1)]
    public static void Upgrade<TState>(in DamageStates.V1<TState> old,
        out DamageStates.V2<TState> next, UpgradeContext context) where TState : unmanaged {
        UpgradeCalls++;
        next = new(old.Segment0Field1, (long)old.Segment1Field1, old.Segment1Field2, old.Segment1Field3);
    }
#endif
}
