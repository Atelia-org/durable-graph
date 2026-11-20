using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Serialization;

namespace RegisteredNodes.Pages;

[DurableNode(Family, 2)]
public sealed partial class Page {
    public static DeclaredNode<Page> Declaration { get; } = new(static () => new(), CurrentSlots(), 1);
}
