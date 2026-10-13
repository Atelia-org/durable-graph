using Atelia.DurableGraph;

namespace PrivateContainerPackage;

[DurableType("pc.root", 1)]
public sealed partial class Root : IDurableObject {
    [DurableField(1)] public List<Point?> Numbers = [];
    [DurableField(2)] public Root? Child;
}

[DurableType("pc.point", 2)]
public partial struct Point {
    [DurableField(1)] public long X;
    [DurableField(2)] public double Padding0;
    [DurableField(3)] public double Padding1;
    [DurableField(4)] public double Padding2;
}

[DurableType("pc.keeper", 1)]
public sealed partial class Keeper : IDurableObject {
    [DurableField(1)] public long Value;
}

[ValueUpgradeRuleSet(AllowNullableLifting = true)]
internal sealed class PointRules { }

internal static class PointUpgrades {
    [DurableValueUpgrade(typeof(PointRules), "pc.point", 1, 2)]
    internal static void Upgrade(in Atelia.DurableGraph.Generated.Family_70632E706F696E74.V1 prior,
        out Atelia.DurableGraph.Generated.Family_70632E706F696E74.V2 next, Atelia.DurableGraph.UpgradeContext context) {
        next = new(prior.Segment0Field1 + 5_000_000_000L, prior.Segment0Field2, prior.Segment0Field3, prior.Segment0Field4);
    }
}
