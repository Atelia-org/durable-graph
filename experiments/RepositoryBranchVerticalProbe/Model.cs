using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Schema;

namespace RepositoryBranchVerticalProbe;

[DurableType("db096.vertical.world", 1)]
public sealed partial class World : IDurableObject {
    [DurableField(1)] public int Progress;
    [DurableField(2)] public int[] Inventory = Enumerable.Range(0, 64).ToArray();
    [DurableField(3)] public string[] Notes = Enumerable.Range(0, 32).Select(i => $"地点 {i}").ToArray();

    public static StateModelRegistry Models() {
        var models = new StateModelRegistry();
        // The probe is a Persistence friend for physical assertions, which also exposes
        // its internal generated registry. C# selects this assembly's generated registry.
#pragma warning disable CS0436
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
#pragma warning restore CS0436
        return models;
    }
}

[DurableType("db096.vertical.action", 1)]
public sealed partial class ActionRecord : IDurableObject {
    [DurableField(1)] public int Number;
    [DurableField(2)] public string Text = "探索";
}
