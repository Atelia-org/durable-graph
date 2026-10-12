namespace Atelia.DurableGraph.Persistence.Tests;

// Compiled by the actual generator in this test project, rather than a handwritten DTO.
[DurableType("P0Widget", 1)]
internal sealed partial class DB087Widget : IDurableObject {
    [DurableField(1)] internal int Value = 0;
    [DurableField(2)] internal List<int> Numbers = [];
    [DurableField(3)] internal DB087Widget? Child = null;
}
