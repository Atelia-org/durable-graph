using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;

namespace GroupCommitConsumer;

[DurableType("GroupSeed", 1)]
public sealed partial class Seed : IDurableObject {
    [DurableField(1)] public int Number;
}
[DurableType("GroupWorld", 1)]
public sealed partial class World : IDurableObject {
    [DurableField(1)] public int Swords;
}
[DurableType("GroupPlayer", 1)]
public sealed partial class Player : IDurableObject {
    [DurableField(1)] public int Swords;
}
[DurableType("GroupEvent", 1)]
public sealed partial class Event : IDurableObject {
    [DurableField(1)] public int Code;
}

internal static class Program {
    private static StateModelRegistry Models() {
        StateModelRegistry models = new();
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
        return models;
    }
    private static void Main(string[] args) {
        if (args.Length != 2) { throw new ArgumentException("Expected seed|continue|verify and database path."); }
        switch (args[0]) {
            case "seed": Create(args[1]); break;
            case "continue": Continue(args[1]); break;
            case "verify": Verify(args[1]); break;
            default: throw new ArgumentException("Unknown lane.");
        }
    }
    private static BranchId Find(Repository repo, string label) => repo.ListBranches().Single(b => b.DisplayLabel == label).Id;
    private static void Need(bool value, string message) { if (!value) { throw new InvalidOperationException(message); } }
    private static void Rejected(Action action) {
        try { action(); } catch (InvalidOperationException error) when (error.Message == "The commit request's original working-copy baseline has changed.") { return; }
        throw new InvalidOperationException("An expired request was accepted.");
    }
    private static void Create(string path) {
        using var repo = Repository.CreateNew(path, Models());
        using var worldWork = repo.OpenWorkingCopy(repo.CreateRootBranchFromEvent("world", new Seed { Number = 1 }).Id);
        using var playerWork = repo.OpenWorkingCopy(repo.CreateRootBranchFromEvent("player", new Seed { Number = 2 }).Id);
        World world = new() { Swords = 1 };
        Player player = new() { Swords = 0 };
        byte[] summary = [1, 2, 3];
        var worldRequest = worldWork.CreateStateCommitRequest(world, summary);
        var playerRequest = playerWork.CreateStateCommitRequest(player);
        // Requests retain roots, not a snapshot. Summary bytes are owned separately.
        world.Swords = 0;
        player.Swords = 1;
        summary[0] = 9;
        var saved = repo.CommitBranches([worldRequest, playerRequest]);
        Need(saved.Count == 2 && saved[worldWork.BranchId].Summary.Span.SequenceEqual(new byte[] { 1, 2, 3 }), "Group result or summary ownership changed.");
        Need(worldWork.BaseCommitId == saved[worldWork.BranchId].Id && playerWork.BaseCommitId == saved[playerWork.BranchId].Id, "Both bases must advance.");
        Need(worldWork.StateSourceCommitId == worldWork.BaseCommitId && playerWork.StateSourceCommitId == playerWork.BaseCommitId, "State baselines must advance.");
        repo.CreateTag("world-transfer", saved[worldWork.BranchId].Id);
        repo.CreateTag("player-transfer", saved[playerWork.BranchId].Id);
        BranchInfo alternate = repo.ForkBranch(saved[worldWork.BranchId].Id, "world-alternate");
        Need(repo.ListRootBranches().Select(branch => branch.Id).ToHashSet().SetEquals([worldWork.BranchId, playerWork.BranchId]), "Only the two roots should be listed.");
        Need(repo.ListBranchSubtree(worldWork.BranchId).Select(branch => branch.Id).ToHashSet().SetEquals([worldWork.BranchId, alternate.Id]), "An unedited fork must be in its parent subtree.");
        Rejected(() => repo.CommitBranches([worldRequest, playerRequest]));
        Need(!repo.IsFaulted, "Expired requests should leave the owner healthy.");
        Console.WriteLine("GroupSeed:TwoNewSchemas:DeferredCapture:OwnedSummary:AllBases");
    }
    private static void Continue(string path) {
        using var repo = Repository.OpenExisting(path, Models());
        using var world = repo.OpenWorkingCopy(Find(repo, "world"));
        using var player = repo.OpenWorkingCopy(Find(repo, "player"));
        Need(((World)world.State!).Swords == 0 && ((Player)player.State!).Swords == 1, "Transferred item was lost or duplicated.");
        CommitId playerState = player.StateSourceCommitId!.Value;
        var expired = player.CreateStateCommitRequest();
        ((World)world.State!).Swords = 2;
        ((Player)player.State!).Swords = 99; // An Event does not save this edit.
        var saved = repo.CommitBranches([world.CreateStateCommitRequest(), player.CreateEventCommitRequest(new Event { Code = 7 })]);
        Need(saved.Count == 2 && saved[world.BranchId].Kind == CommitKind.State && saved[player.BranchId].Kind == CommitKind.Event, "Mixed roles were lost.");
        Need(player.BaseCommitId == saved[player.BranchId].Id && player.StateSourceCommitId == playerState, "Event changed State baseline.");
        Need(world.StateSourceCommitId == saved[world.BranchId].Id, "State failed to advance.");
        Rejected(() => repo.CommitBranches([expired]));
        Need(!repo.IsFaulted, "Event-expired request faulted owner.");
        Console.WriteLine("GroupContinue:ColdState:MixedRoles:EventKeepsState:ExpiredRequest");
    }
    private static void Verify(string path) {
        // Directory browsing does not need the business model registry.
        using (var directory = Repository.OpenReadOnlyExisting(path, new StateModelRegistry())) {
            BranchId world = Find(directory, "world"), player = Find(directory, "player"), alternate = Find(directory, "world-alternate");
            Need(directory.ListRootBranches().Select(branch => branch.Id).ToHashSet().SetEquals([world, player]), "Cold roots changed.");
            Need(directory.ListBranchSubtree(world).Select(branch => branch.Id).ToHashSet().SetEquals([world, alternate]), "Cold subtree lost the unedited fork.");
            Need(directory.ReadCurrentCommit(alternate).Commit.Id == directory.ResolveTag("world-transfer"), "Parent advancement moved the fork's inherited commit.");
        }
        using var repo = Repository.OpenReadOnlyExisting(path, Models());
        CommitId worldCurrent = repo.ReadCurrentCommit(Find(repo, "world")).Commit.Id;
        CommitId playerCurrent = repo.ReadCurrentCommit(Find(repo, "player")).Commit.Id;
        Need(((World)repo.LoadState(worldCurrent)).Swords == 2, "World continuation changed.");
        Need(((Event)repo.LoadEvent(playerCurrent)).Code == 7, "Event continuation changed.");
        LoadedState savedPlayer = repo.LoadLatestSavedState(playerCurrent);
        Need(savedPlayer.StateCommitId == repo.ResolveTag("player-transfer") && ((Player)savedPlayer.State!).Swords == 1, "Event persisted an uncommitted State edit.");
        Need(((World)repo.LoadState(repo.ResolveTag("world-transfer"))).Swords == 0, "Old World commit changed.");
        Need(repo.ReadCommitInfo(repo.ResolveTag("world-transfer")).Summary.Span.SequenceEqual(new byte[] { 1, 2, 3 }), "The durable summary changed.");
        Console.WriteLine("GroupVerify:ColdBothSchemas:ExactTags:EventKeepsSavedState:Tree");
    }
}
