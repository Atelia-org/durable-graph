using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace RegisteredNodes.Pages;

[DurableNode(Family, 1)]
public sealed partial class Page {
    public string? RetiredLabel;

    public static DeclaredNode<Page> Declaration { get; } = new(static () => new(), [
        .. CurrentSlots(),
        DeclaredMember<Page>.Reference<string>(TypeExpr.Builtin(TypeTag.String), p => p.RetiredLabel, (p, label) => p.RetiredLabel = label),
    ]);
}
