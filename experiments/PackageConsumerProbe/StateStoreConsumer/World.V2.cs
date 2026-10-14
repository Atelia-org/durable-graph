using Atelia.DurableGraph;

namespace StateStorePackageConsumerProbe;

[DurableType("package.restore-world", 2)]
public sealed partial class World : IDurableObject {
    [DurableField(1)] private int _score;
    [DurableField(2)] private readonly string _name;
    [DurableField(3)] private readonly int _generation;
    [DurableField(4)] private readonly ulong _createdAtTicks;
    [DurableField(5)] private readonly Guid _fixtureId;
    [DurableField(6)] private readonly Guid _fixtureGroup;
    [Transient] private int _cache = 37;
    private static int _constructorCalls;
    private static int _upgradeCalls;

    public World(int score, string name) {
        _constructorCalls++;
        _score = score; _name = name; _generation = -1; _createdAtTicks = 638_625_600_000_000_000;
        _fixtureId = default; _fixtureGroup = default;
    }

    private static void UpgradeStateV1ToV2(in __DurableState.V1 old, out __DurableState.V2 next) {
        _upgradeCalls++;
        next = new(old.Segment0Field1 + 100, old.Segment0Field2, 73, old.Segment0Field4, default, default);
    }

}
