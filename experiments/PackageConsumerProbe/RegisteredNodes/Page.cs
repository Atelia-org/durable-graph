using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;

namespace RegisteredNodes.Pages;

public sealed partial class Page {
    public const string Family = "Example.RegisteredNode.Page";
    public byte Rating;
    public Page? Next;
    public Page?[] Links = [];
    public List<Page>? Order;
    public Dictionary<string, Page>? Named;

    internal static DeclaredMember<Page>[] CurrentSlots() {
        var page = TypeExpr.Named(Family);
        var text = TypeExpr.Builtin(TypeTag.String);
        var rating = new DeclaredPrivateFormat<byte>("rating-0-100", 0,
            static (ref BareValueWriter w, byte n) => w.WriteByte(n),
            static (ref BareValueReader r) => {
                byte n = r.ReadByte();
                return n <= 100 ? n : throw new InvalidDataException("Rating exceeds 100.");
            });
        return [
            DeclaredMember<Page>.Private(rating, p => p.Rating, (p, n) => p.Rating = n),
            DeclaredMember<Page>.Reference<Page>(page, p => p.Next, (p, next) => p.Next = next),
            DeclaredMember<Page>.References<Page>(page, p => p.Links, (p, links) => p.Links = links),
            DeclaredMember<Page>.Reference<List<Page>>(TypeExpr.List(page), p => p.Order, (p, order) => p.Order = order),
            DeclaredMember<Page>.Reference<Dictionary<string, Page>>(TypeExpr.Dictionary(text, page), p => p.Named, (p, named) => p.Named = named),
        ];
    }
}
