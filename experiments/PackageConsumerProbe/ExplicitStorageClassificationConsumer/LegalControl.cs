using Atelia.DurableGraph;

namespace ExplicitStoragePackage;

[DurableType("probe.explicit.control", 1)]
public sealed partial class LegalControl : IDurableObject {
    [DurableField(1)] public int Value;
}
