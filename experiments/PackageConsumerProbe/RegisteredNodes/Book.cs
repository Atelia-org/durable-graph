using System.Security.Cryptography;
using System.Text.Json;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using RegisteredNodes.Pages;

namespace RegisteredNodes;

[DurableType("Example.RegisteredNode.Book", 1)]
public sealed partial class Book : IDurableObject {
    [DurableField(1)] public Page Home = new();
    [DurableField(2)] public List<Page> Order = [];
    [DurableField(3)] public Dictionary<string, Page> Named = [];
}

internal static class Probe {
    internal static StateModelRegistry Models() {
        var models = new StateModelRegistry();
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
        Page.Declaration.Register(models);
        return models;
    }

    internal static void Require(bool condition, string detail) {
        if (!condition) { throw new InvalidDataException(detail); }
    }

    internal static int Check(Book book, int expected) {
        Page a = book.Home;
        Page b = book.Named["other"];
        bool edited = expected == 34;
        Require(a != b && a.Rating == expected && b.Rating == 20, "Wrong restored page or private value.");
        Require(book.Order.Count == 3 && book.Order[0] == a && book.Order[1] == b && book.Order[2] == (edited ? b : a), "List alias mismatch.");
        Require(book.Named.Count == 2 && book.Named["home"] == (edited ? b : a), "Dictionary alias mismatch.");
        Require(a.Named == book.Named && b.Named == book.Named && a.Order == book.Order && b.Order == book.Order, "Lost node to container identity.");
        Require(a.Next == (expected == 21 ? a : b) && b.Next == (edited ? b : a), "Lost node cycle or rewired edge.");
        Require(a.Links.Length == 4 && a.Links[0] == a && a.Links[1] == (edited ? b : null) && a.Links[2] == b && a.Links[3] == b,
            "Repeated edges lost order, null or aliases.");
        return expected;
    }

    internal static void Receipt(string path, string stage, params int[] ratings) {
        var loaded = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic &&
            (assembly.GetName().Name!.StartsWith("Atelia.", StringComparison.Ordinal) ||
             assembly.GetName().Name!.StartsWith("RegisteredNodes.Pages", StringComparison.Ordinal)))
            .Select(assembly => new {
                name = assembly.GetName().Name!,
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)))
            }).OrderBy(item => item.name);
        File.WriteAllText(path, JsonSerializer.Serialize(new { stage, ratings, assemblies = loaded }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
