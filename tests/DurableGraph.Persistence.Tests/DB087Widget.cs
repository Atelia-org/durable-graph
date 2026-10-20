namespace Atelia.DurableGraph.Persistence.Tests;

// Compiled by the actual generator in this test project, rather than a handwritten DTO.
[DurableType("P0Widget", 1)]
internal sealed partial class DB087Widget : IDurableObject {
    [DurableField(1)] internal int Value = 0;
    [DurableField(2)] internal List<int> Numbers = [];
    [DurableField(3)] internal DB087Widget? Child = null;
}

// Fixture observations use real persisted identities and strict Head history. These
// helpers do not provide a legacy Repository surface or infer a source from its graph.
internal static class DB096TestAccess {
    internal static BranchId BranchNamed(Repository repository, string label) =>
        repository.ListBranches().Single(branch => branch.DisplayLabel == label).Id;
    internal static RepositoryResources Resources(Repository repository) =>
        (RepositoryResources)typeof(Repository).GetField("_resources", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(repository)!;
    internal static HistoryGraphRecord Describe(Repository repository, CommitId id) => Describe(Resources(repository), id);
    internal static HistoryGraphRecord Describe(RepositoryResources resources, CommitId id) => resources.ReadDescriptor(id.Value);
    internal static CommitId? Previous(Repository repository, CommitId id) =>
        Resources(repository).ReadPrevious(id.Value) is { } previous ? new CommitId(previous.Id) : null;
    internal static StoredGraphRevision Snapshot(Repository repository, CommitId id) => Snapshot(Resources(repository), id);
    internal static StoredGraphRevision Snapshot(RepositoryResources resources, CommitId id) =>
        resources.GraphStore.ReadBusinessGraph(Describe(resources, id).Address,
            Describe(resources, id).Kind == CommitKind.Event ? null
                : resources.FindNearestState(id.Value, inclusive: false)?.Address);
}
