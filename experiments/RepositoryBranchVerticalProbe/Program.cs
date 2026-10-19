using System.Runtime.InteropServices;
using Atelia.DurableGraph;
using RepositoryBranchVerticalProbe;

string path = args.Length == 0
    ? Path.GetFullPath(Path.Combine(".artifacts", "db096-branch-vertical", Guid.NewGuid().ToString("N")))
    : Path.GetFullPath(args[0]);
if (Directory.Exists(path)) { throw new ArgumentException("Use a new directory; this probe retains its evidence."); }
Directory.CreateDirectory(Path.GetDirectoryName(path)!);
int checks = 0;
BranchId parentId, childId, eventOnlyId;
CommitId initial, stateOne, eventTwo, parentTip, childState, childSameValue, eventOnlyTip;

// This uses real Repository publication and generated business models throughout.
using (var repo = Repository.CreateNew(path, World.Models())) {
    byte[] summary = [1, 2, 3];
    var initialObject = new World();
    var parent = repo.CreateRootBranchFromState("遗迹/小绿瓶", initialObject, summary);
    parentId = RoundTripBranch(parent.Id);
    initial = RoundTripCommit(repo.ReadCurrentCommit(parentId).Commit.Id);
    summary[0] = 99;
    Check(repo.ReadCommitInfo(initial).Summary.Span.SequenceEqual(new byte[] { 1, 2, 3 }), "creation owns summary input");
    Check(parent.ForkedFromCommitId is null, "root has no origin");
    using (var work = repo.OpenWorkingCopy(parentId)) {
        Check(!ReferenceEquals(initialObject, work.State), "opening restores rather than retaining creation instance");
        var state = (World)work.State!;
        work.CommitEvent(new ActionRecord { Number = 1 });
        state.Progress = 1;
        state.Inventory[3] = 101;
        stateOne = work.CommitState(state, new byte[] { 4 }).Id;
        eventTwo = RoundTripCommit(work.CommitEvent(new ActionRecord { Number = 2 }, new byte[] { 5 }).Id);
        // The fork anchor is deliberately older than the parent's eventual current commit.
        state.Progress = 2;
        state.Inventory[4] = 102;
        parentTip = work.CommitState(state).Id;
        Check(repo.ReadCommitInfo(parentTip).Summary.IsEmpty, "default summary is empty, not inherited");
    }
    var child = repo.ForkBranch(eventTwo, "遗迹/小绿瓶");
    childId = RoundTripBranch(child.Id);
    Check(parentId != childId, "duplicate labels have distinct identities");
    Check(child.ForkedFromCommitId == eventTwo, "fork retains exact old anchor");
    Check(repo.ReadCurrentCommit(childId).Commit.Id == eventTwo, "fork creates no synthetic commit");
    Check(repo.ReadCurrentCommit(parentId).Commit.Id == parentTip, "fork leaves parent current intact");
    Check(repo.ReadFrames(childId).Count == 4 && repo.ReadFrames(childId)[^1].Id == eventTwo, "child inherits exactly four real commits, stopping at the old point");
    repo.CreateTag("固定旧事件", eventTwo);
    var eventOnly = repo.CreateRootBranchFromEvent("工具循环", new ActionRecord { Number = 10 });
    eventOnlyId = RoundTripBranch(eventOnly.Id);
    using (var work = repo.OpenWorkingCopy(eventOnlyId)) {
        Check(work.State is null && work.StateSourceCommitId is null, "event-only branch has no fabricated State");
        eventOnlyTip = work.CommitEvent(new ActionRecord { Number = 11 }).Id;
    }
    Check(repo.ListBranches().Length == 3, "directory lists only the three registered branches");
}

// All owners/caches are closed before the next open; this is not a new-process crash test.
using (var repo = Repository.OpenExisting(path, World.Models())) {
    Check(repo.ResolveTag("固定旧事件") == eventTwo, "bookmark survives reopen");
    var current = repo.ReadCurrentCommit(childId);
    Check(current.BranchId == childId && current.Commit.Id == eventTwo && current.Commit.CreatedOnBranchId == parentId,
        "empty child retains selected branch and inherited commit owner separately");
    Check(repo.ReadBranchInfo(childId).ForkedFromCommitId == eventTwo, "origin survives reopen");
    var restored = repo.LoadLatestSavedState(eventTwo);
    Check(restored.StateCommitId == stateOne && ((World)restored.State!).Progress == 1, "old Event selects nearest preceding State");
    Check(repo.GetPreviousState(eventTwo) == stateOne, "previous State handle identifies the exact saved baseline");
    Check(((World)restored.State!).Inventory[3] == 101 && ((World)restored.State!).Inventory[4] == 4,
        "parent's later changes do not leak into old-point restore");
    Check(repo.LoadLatestSavedState(eventOnlyTip).State is null, "event-only history stays stateless after reopen");
    using (var work = repo.OpenWorkingCopy(childId)) {
        Check(work.BranchId == childId && work.BaseCommitId == eventTwo && work.StateSourceCommitId == stateOne,
            "working copy keeps logical tip separate from State source");
        var state = (World)work.State!;
        state.Progress = 3;
        var childEvent = work.CommitEvent(new ActionRecord { Number = 3 });
        Check(work.StateSourceCommitId == stateOne && work.BaseCommitId == childEvent.Id, "Event advances tip without accepting State edits");
        Check(((World)repo.LoadLatestSavedState(childEvent.Id).State!).Progress == 1, "Event does not save dirty State");
        state.Inventory[5] = 103;
        childState = work.CommitState(state, new byte[] { 7, 8 }).Id;
        Check(ReferenceEquals(state, work.State), "State commit installs the supplied instance");
        childSameValue = work.CommitState().Id;
        Check(childSameValue != childState, "same-value save has a fresh commit identity");
        Check(work.StateSourceCommitId == childSameValue, "same-value save becomes accepted State source");
    }
    var pair = repo.ReadPair(parentTip, childState);
    Check(((World)pair.First).Progress == 2 && ((World)pair.Second).Progress == 3, "Pair follows each sibling's own source");
    var crossRoot = repo.ReadPair(eventOnlyTip, childState);
    Check(((ActionRecord)crossRoot.First).Number == 11 && ((World)crossRoot.Second).Progress == 3, "Pair supports different roots and kinds in input order");
    var samePair = repo.ReadPair(childState, childState);
    Check(((World)samePair.First).Progress == 3 && ((World)samePair.Second).Progress == 3, "Pair accepts the same point twice");
    Check(repo.ReadCurrentCommit(parentId).Commit.Id == parentTip, "child publication does not move parent");
    Check(repo.EnumerateEvents(childSameValue).Select(info => ((ActionRecord)repo.LoadEvent(info.Id)).Number).SequenceEqual(new[] { 3, 2, 1 }),
        "lazy event enumeration crosses fork history without holding a lease over application reads");
    var firstDescription = repo.ReadCommitInfo(childState);
    Check(MemoryMarshal.TryGetArray(firstDescription.Summary, out ArraySegment<byte> owned), "summary owns accessible description storage");
    owned.Array![owned.Offset] = 200;
    Check(repo.ReadCommitInfo(childState).Summary.Span.SequenceEqual(new byte[] { 7, 8 }), "mutating one description cannot change another or persisted summary");

    // S0,E1,S1,S2: the selected State needs S1, but its previous Event still needs S0.
    var counterexample = repo.CreateRootBranchFromState("停止条件反例", new World { Progress = 20 });
    using var counter = repo.OpenWorkingCopy(counterexample.Id);
    var olderEvent = counter.CommitEvent(new ActionRecord { Number = 21 });
    ((World)counter.State!).Progress = 21;
    counter.CommitState();
    ((World)counter.State!).Progress = 22;
    var latest = counter.CommitState();
    var checkpoint = (StateCheckpoint)repo.ReadCheckpoint(latest.Id);
    Check(((World)checkpoint.State).Progress == 22 && ((ActionRecord)checkpoint.PreviousEvent!).Number == 21,
        "Checkpoint reaches previous Event beyond two nearest States");
    Check(checkpoint.PreviousEventCommit?.Id == olderEvent.Id, "Checkpoint reports exact previous Event identity");
}

using (var repo = Repository.OpenExisting(path, World.Models())) {
    Check(repo.ReadCurrentCommit(childId).Commit.Id == childSameValue, "same-value commit survives second reopen");
    Check(repo.ResolveTag("固定旧事件") == eventTwo, "bookmark remains fixed after child and parent progress");
    Check(((World)repo.LoadState(initial)).Progress == 0, "initial State remains readable");
    Check(repo.ReadCommitInfo(childSameValue).Summary.IsEmpty, "default summary stays empty on disk");
    using var work = repo.OpenWorkingCopy(childId);
    var state = (World)work.State!;
    Check(state.Progress == 3 && state.Inventory[3] == 101 && state.Inventory[4] == 4 && state.Inventory[5] == 103,
        "cold child content follows inherited and local changes");
    state.Progress = 4;
    work.CommitState();
}
using (var repo = Repository.OpenReadOnlyExisting(path, World.Models())) {
    Check(((World)repo.LoadLatestSavedState(repo.ReadCurrentCommit(childId).Commit.Id).State!).Progress == 4,
        "second cold restore supports another durable save");
}
IntegrityChecks.Run();
Console.WriteLine($"PASS: {checks} checks; real Repository API; same-process complete close/reopen.");
Console.WriteLine($"Evidence repository: {path}");

void Check(bool condition, string message) {
    if (!condition) { throw new InvalidOperationException($"FAIL: {message}"); }
    ++checks;
}
BranchId RoundTripBranch(BranchId id) {
    Span<byte> bytes = stackalloc byte[BranchId.EncodedSize];
    Check(id.TryWrite(bytes) && BranchId.TryRead(bytes, out var decoded) && decoded == id, "BranchId codec round trip");
    BranchId.TryRead(bytes, out var result);
    return result;
}
CommitId RoundTripCommit(CommitId id) {
    Span<byte> bytes = stackalloc byte[CommitId.EncodedSize];
    Check(id.TryWrite(bytes) && CommitId.TryRead(bytes, out var decoded) && decoded == id, "CommitId codec round trip");
    CommitId.TryRead(bytes, out var result);
    return result;
}
