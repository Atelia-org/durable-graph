using Atelia.DurableGraph;
using S = global::Atelia.DurableGraph.Generated.Family_70726F62652E646576656C6F706D656E742E776964676574;

namespace DevelopmentSchemaPackage;

[DurableType("probe.development.widget", 2)]
public sealed partial class Widget : IDurableObject {
    [DurableField(1)] public double Value;
    [DurableField(2)] public List<int> Numbers = [];
    [DurableField(3)] public Widget? Child;
    public static int UpgradeCalls;

    private static void UpgradeStateV1ToV2(in S.V1 prior, out S.V2 next) {
        UpgradeCalls++;
        next = new((double)prior.Segment0Field1 + 200.5, prior.Segment0Field2, prior.Segment0Field3);
    }
}
