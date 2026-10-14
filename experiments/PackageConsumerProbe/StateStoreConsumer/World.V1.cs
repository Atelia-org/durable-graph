using Atelia.DurableGraph;

namespace StateStorePackageConsumerProbe;

// This first build publishes real package-generated history and seeds V1 state for the V2 process.
[DurableType("package.restore-world", 1)]
public sealed partial class World : IDurableObject {
    [DurableField(1)] private int _score;
    [DurableField(2)] private readonly string _name;
    [DurableField(4)] private readonly ulong _createdAtTicks;

    public World(int score, string name) { _score = score; _name = name; _createdAtTicks = 638_625_600_000_000_000; }

}
