using Atelia.DurableGraph;

namespace SplitRepositoryPackage;

// Real generated Event consumer; its declaration remains unreachable from State.
[DurableType("probe.private.unreachable", 1)]
public sealed partial class CatalogEvent : IDurableObject {
    [DurableField(1)] public int Value;
}
