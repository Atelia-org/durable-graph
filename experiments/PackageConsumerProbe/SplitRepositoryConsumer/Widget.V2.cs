using Atelia.DurableGraph;
using S = global::Atelia.DurableGraph.Generated.Family_7061636B6167652E73706C69742E776964676574;

namespace SplitRepositoryPackage;

[DurableType("package.split.widget", 2)]
public sealed partial class Widget : IDurableObject {
    [DurableField(1)] public long Value;
    [DurableField(2)] public List<int> Numbers = [];
    [DurableField(3)] public Widget? Child;
    public static int UpgradeCalls;

    private static void UpgradeStateV1ToV2(in S.V1 prior, out S.V2 next) {
        UpgradeCalls++;
        next = new((long)prior.Segment0Field1 + 100, prior.Segment0Field2, prior.Segment0Field3);
    }
}
