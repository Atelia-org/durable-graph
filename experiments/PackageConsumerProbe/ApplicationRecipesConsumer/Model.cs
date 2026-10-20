using Atelia.DurableGraph;

namespace ApplicationRecipes;

// A small demonstration model, not the Echo Garden game's complete rules.
[DurableType("recipes.garden", 1)]
public sealed partial class GardenState : IDurableObject {
    [DurableField(1)] public int Day;
    [DurableField(2)] public int Flowers;
    [DurableField(3)] public bool Won;
}
