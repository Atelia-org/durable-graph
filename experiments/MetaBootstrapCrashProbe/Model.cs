extern alias persistence;
using Atelia.DurableGraph;
using persistence::Atelia.DurableGraph.Persistence;

namespace MetaBootstrapCrash;

[DurableType("CrashWidget", 1)]
public sealed partial class Widget : IDurableObject {
    [DurableField(1)] public int Value;
    [DurableField(2)] public List<int> Numbers = [];
    [DurableField(3)] public Widget? Child;
}

internal static class HostModel {
    internal static StateModelRegistry Models() {
        StateModelRegistry models = new();
        models.Register(global::Atelia.DurableGraph.Generated.Family_4372617368576964676574.Definition);
        return models;
    }

    internal static Widget Ring() {
        List<int> numbers = Enumerable.Range(0, 128).ToList();
        Widget root = new() { Value = 1, Numbers = numbers };
        root.Child = new() { Value = 2, Numbers = numbers, Child = root };
        return root;
    }

    internal static Widget Check(IDurableObject? value, int atSeven, int atEight = 8) {
        if (value is not Widget root || root.Child is not Widget child || root.Value != 1 || child.Value != 2 ||
            !ReferenceEquals(root, child.Child) || !ReferenceEquals(root.Numbers, child.Numbers) || root.Numbers.Count != 128) {
            throw new InvalidDataException("Widget ring, shared List or field values disagree.");
        }
        for (int i = 0; i < 128; i++) {
            int expected = i == 7 ? atSeven : i == 8 ? atEight : i;
            if (root.Numbers[i] != expected) { throw new InvalidDataException($"List element {i} disagrees."); }
        }
        return root;
    }
}
