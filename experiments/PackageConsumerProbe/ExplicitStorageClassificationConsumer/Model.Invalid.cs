using Atelia.DurableGraph;

namespace ExplicitStoragePackage;

[DurableType("probe.explicit.subject", 1)]
public sealed partial class Subject : IDurableObject {
    [field: DurableField(1)] public int Value { get; set; }
    [DurableField(2)] public int Control;
}
