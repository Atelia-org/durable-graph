using Atelia.DurableGraph;

namespace PrivateContainerPackage;

[DurableType("pc.root", 1)]
public sealed partial class Root : IDurableObject {
    [DurableField(1)] public List<Point?> Numbers = [];
    [DurableField(2)] public Root? Child;
}

[DurableType("pc.point", 1)]
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
