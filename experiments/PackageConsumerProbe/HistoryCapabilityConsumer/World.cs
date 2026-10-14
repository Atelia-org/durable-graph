using Atelia.DurableGraph;

namespace HistoryCapabilityPackageConsumerProbe;

#if HISTORY_V1
[DurableType("package.history-world", 1)]
#else
[DurableType("package.history-world", 2)]
#endif
public sealed partial class World : IDurableObject {
    [DurableField(1)] private int _score;
#if HISTORY_V1
    [DurableField(2)] private Legacy? _legacy;

    internal World(int score, Legacy legacy) { _score = score; _legacy = legacy; }
    internal Legacy Legacy => _legacy!;
#else
    internal static int UpgradeCalls = 0;
    private World(int score) { _score = score; }

#if !READERS_ONLY
    private static void UpgradeStateV1ToV2(in __DurableState.V1 old, out __DurableState.V2 next) {
        UpgradeCalls++;
        // The old durable slot is an ID, so this historical DTO needs no Legacy CLR type.
        if (old.Segment0Field2.IsNull) { throw new InvalidOperationException("The historical World must reference Legacy."); }
        next = new(old.Segment0Field1 + 100);
    }
#endif
#endif
    internal World SnapshotEvent() {
#if HISTORY_V1
        return new World(_score, _legacy!.SnapshotEvent());
#else
        return new World(_score);
#endif
    }
    internal int Score => _score;
}
