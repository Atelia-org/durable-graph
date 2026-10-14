using Atelia.DurableGraph;

namespace HistoryCapabilityPackageConsumerProbe;

// This is a migration shell, not the retired business implementation. Its abstract current
// model must never be allocated; source rows still require complete normalization/validation.
[DurableType("package.history-legacy", 2)]
public abstract partial class Legacy : IDurableObject {
    [DurableField(1)] private World? _world = null;
    internal static int UpgradeCalls = 0;
    internal static bool ProduceInvalidReference = false;
    internal static int LastHistoricalValue = 0;

    // Keep the field visibly used without adding business behavior to the migration shell.
    private World? MigrationReference => _world;

#if !READERS_ONLY
    private static void UpgradeStateV1ToV2(in __DurableState.V1 old, out __DurableState.V2 next) {
        UpgradeCalls++;
        LastHistoricalValue = old.Segment0Field1;
        if (old.Segment0Field3 != 638_625_600_000_000_000) {
            throw new InvalidOperationException("Historical content changed Legacy's creation timestamp.");
        }
        if (old.Segment0Field2.IsNull) { throw new InvalidOperationException("The old Legacy self-reference was not decoded."); }
        next = new(new ObjectId(ProduceInvalidReference ? uint.MaxValue : 0));
    }
#endif
}
