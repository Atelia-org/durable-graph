using Atelia.DurableGraph;

namespace DevelopmentSchemaPackage;

[DurableType("probe.development.widget", 1)]
public sealed partial class Widget : IDurableObject {
    [DurableField(1)] public int Value;
    [DurableField(2)] public List<int> Numbers = [];
    [DurableField(3)] public Widget? Child;
    public static int UpgradeCalls;
}
