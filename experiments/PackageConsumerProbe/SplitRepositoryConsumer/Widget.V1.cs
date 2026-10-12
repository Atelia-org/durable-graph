using Atelia.DurableGraph;

namespace SplitRepositoryPackage;

[DurableType("package.split.widget", 1)]
public sealed partial class Widget : IDurableObject {
    [DurableField(1)] public int Value;
    [DurableField(2)] public List<int> Numbers = [];
    [DurableField(3)] public Widget? Child;
}
