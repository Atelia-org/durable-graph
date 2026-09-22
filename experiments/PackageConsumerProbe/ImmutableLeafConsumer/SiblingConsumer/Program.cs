using Atelia.DurableGraph.Runtime;
using System.Reflection;

namespace ImmutableLeafProbe;

internal static class Program {
    private static void Main() {
        // The sibling generator added mutable instance state in the same compiler pass; the
        // structural proof must reject the immutable-leaf claim at binding creation (C5/C6).
        // Binary-path models have no generated DurableDefinitions aggregator; the generated
        // internal __DurableState facade exposes the stable binding inside this assembly.
        if (ReadImmutableLeafFlag(Leaf.__DurableState.Model)) {
            throw new InvalidOperationException("Sibling-added mutable state must report IsImmutableLeaf=false.");
        }
        Console.WriteLine("ImmutableLeafSibling:Flag:False:True");
    }

    private static bool ReadImmutableLeafFlag(StateModelBinding binding) =>
        (bool)(typeof(StateModelBinding).GetProperty("IsImmutableLeaf", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Missing internal StateModelBinding.IsImmutableLeaf property.")
        ).GetValue(binding)!;
}
