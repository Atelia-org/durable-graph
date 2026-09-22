using Atelia.DurableGraph;
using Atelia.DurableGraph.Runtime;

namespace ImmutableLeafProbe;

// Shared by both consumers through a <Compile Link> so the Family-positive and
// sibling-negative builds compile the identical original model declaration.
[DurableType("probe.leaf", 1)]
public partial class Leaf : IDurableObject {
    [DurableField(1)] public readonly int Value;

    public Leaf() { }

    public Leaf(int value) => Value = value;
}
