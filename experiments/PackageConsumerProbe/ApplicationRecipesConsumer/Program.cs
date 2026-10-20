using System.Text.Json;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using ApplicationRecipes;

if (args.Length != 3) { throw new ArgumentException("seed|verify <database> <navigation.json>"); }
if (args[0] == "seed") { Seed(args[1], args[2]); }
else if (args[0] == "verify") { Verify(args[1], args[2]); }
else { throw new ArgumentException("Unknown lane."); }
Console.WriteLine($"ApplicationRecipes:{args[0]}:Passed");

static StateModelRegistry Models() {
    var models = new StateModelRegistry();
    Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
    return models;
}

static byte[] Preview(GardenState state) => SummaryCodec.Encode(SummaryCodec.FromState(state));

static void Seed(string path, string navigationPath) {
    using var repo = Repository.CreateNew(path, Models());
    var initial = new GardenState();
    BranchInfo main = repo.CreateRootBranchFromState("温室", initial, Preview(initial));
    using var selection = new BranchSelection(repo, main.Id);
    var state = (GardenState)selection.Current.State!;
    CommitId bookmark = default;
    for (int day = 1; day <= 3; day++) {
        state.Day = day;
        state.Flowers = day;
        // Finish all edits, project, then immediately commit. In a concurrent app,
        // the same application lock covers edits, projection AND the entire call.
        CommitInfo saved = selection.Current.CommitState(Preview(state));
        AssertPreview(repo, saved);
        if (day == 1) { bookmark = saved.Id; }
    }

    NavigationChecks.Run(main.Id, bookmark);
    PreviewSummaryChecks.Run();
    Need(JsonSerializer.Serialize(main.Id) == "{}" && JsonSerializer.Serialize(bookmark) == "{}",
        "This sample must demonstrate why explicit ID adaptation is needed.");

    BranchInfo child = selection.Fork(bookmark, "温室");
    Need(selection.PendingBranchId == child.Id && selection.Current.BranchId == main.Id,
        "Creating a branch must retain the selected working copy.");
    selection.OpenPending();
    Need(selection.Current.BranchId == child.Id && selection.Current.BaseCommitId == bookmark,
        "An empty fork has its own write target but inherits the selected commit.");
    // Navigation is an application preference, not part of DG's atomic save.
    // This sample creates the file once; production preference-file replacement is app-owned.
    using var file = new FileStream(navigationPath, FileMode.CreateNew, FileAccess.Write);
    using var writer = new StreamWriter(file);
    writer.Write(NavigationCodec.Encode("garden", selection.Current.BranchId, bookmark));
    // No child commit yet: verify must reopen and continue an inherited position.
}

static void Verify(string path, string navigationPath) {
    Navigation navigation = NavigationCodec.Decode(File.ReadAllText(navigationPath));
    // A web host maps allowed application keys to paths. It does not accept arbitrary
    // client-supplied paths, nor assume that decoding an ID chooses a repository.
    Need(navigation.RepositoryKey == "garden", "Unknown application repository key.");
    Need(IdText.TryReadBranch(navigation.LastUsedBranch, out BranchId selected), "Missing branch.");
    Need(IdText.TryReadCommit(navigation.BookmarkCommit, out CommitId bookmark), "Missing bookmark.");
    BranchId parent;
    CommitId parentTip;
    using (var browse = Repository.OpenReadOnlyExisting(path, new StateModelRegistry())) {
        BranchInfo[] branches = browse.ListBranches();
        Need(branches.Length == 2 && branches.All(x => x.DisplayLabel == "温室"), "Duplicate labels changed.");
        // Reordering a directory must not change the saved selection.
        Need(branches.Reverse().Single(x => x.Id == selected).Id == selected, "Selection relied on list order.");
        parent = browse.ReadCommitInfo(bookmark).CreatedOnBranchId;
        parentTip = browse.ReadCurrentCommit(parent).Commit.Id;
        var inherited = browse.ReadCurrentCommit(selected);
        Need(inherited.BranchId == selected && inherited.Commit.Id == bookmark
            && inherited.Commit.CreatedOnBranchId == parent, "Selected branch was confused with commit origin.");
        Need(browse.ReadFrames(selected).Count == 2, "Fork manufactured a commit.");
        foreach (CommitInfo commit in browse.ReadFrames(parent)) {
            SummaryReadResult preview = SummaryCodec.Read(commit.Summary); // Read errors are outside the decoder.
            Need(preview.Status == SummaryStatus.Available, "Preview required domain models.");
        }
    }
    using (var repo = Repository.OpenExisting(path, Models())) {
        using var work = repo.OpenWorkingCopy(selected);
        var state = (GardenState)work.State!;
        Need(state.Day == 1 && work.BranchId == selected, "Cold selection restored the wrong state or target.");
        state.Day = 2;
        CommitInfo continued = work.CommitState(Preview(state));
        AssertPreview(repo, continued);
        Need(continued.CreatedOnBranchId == selected && repo.ReadCurrentCommit(parent).Commit.Id == parentTip,
            "Continuation modified the source branch.");
        Need(((GardenState)repo.LoadState(bookmark)).Day == 1 && continued.Id != bookmark,
            "The fixed bookmark moved with the branch.");
        foreach (var branch in repo.ListBranches()) {
            foreach (var commit in repo.ReadFrames(branch.Id)) { AssertPreview(repo, commit); }
        }
    }
    VerifyMissingModels(path + "-missing");
    VerifyGroupedPreviews(path + "-groups");
}

static void VerifyMissingModels(string path) {
    BranchId parent;
    CommitId source;
    using (var seed = Repository.CreateNew(path, Models())) {
        var state = new GardenState { Day = 1 };
        parent = seed.CreateRootBranchFromState("source", state, Preview(state)).Id;
        source = seed.ReadCurrentCommit(parent).Commit.Id;
    }
    BranchId pending;
    var models = new StateModelRegistry();
    using (var missing = Repository.OpenExisting(path, models)) {
        pending = missing.ForkBranch(source, "created, not opened").Id;
        ExpectMissingModel(() => missing.OpenWorkingCopy(pending));
        Need(!missing.IsFaulted && missing.ListBranches().Length == 2, "Failed open undid creation or faulted owner.");
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
        ExpectMissingModel(() => missing.OpenWorkingCopy(pending)); // Existing configuration is frozen.
        Need(missing.ReadCurrentCommit(pending).Commit.Id == source, "Retry changed the inherited position.");
    }
    using var reopened = Repository.OpenExisting(path, models);
    using var work = reopened.OpenWorkingCopy(pending); // Retry OPEN of the same ID; never fork again.
    Need(reopened.ListBranches().Length == 2 && ((GardenState)work.State!).Day == 1, "Reopen duplicated the branch.");
    var restored = (GardenState)work.State!;
    restored.Day = 2;
    AssertPreview(reopened, work.CommitState(Preview(restored)));
}

static void VerifyGroupedPreviews(string path) {
    using var repo = Repository.CreateNew(path, Models());
    var a = new GardenState { Day = 1 };
    var b = new GardenState { Day = 1 };
    BranchId aId = repo.CreateRootBranchFromState("A", a, Preview(a)).Id;
    BranchId bId = repo.CreateRootBranchFromState("B", b, Preview(b)).Id;
    using var aw = repo.OpenWorkingCopy(aId);
    using var bw = repo.OpenWorkingCopy(bId);
    a = (GardenState)aw.State!;
    b = (GardenState)bw.State!;
    CommitRequest early = aw.CreateStateCommitRequest(a, Preview(a));
    a.Day = 2;
    CommitInfo mismatch = repo.CommitBranches([early])[aId];
    Need(((GardenState)repo.LoadState(mismatch.Id)).Day == 2
        && SummaryCodec.Read(mismatch.Summary).Preview!.Day == 1, "Deferred capture contract unexpectedly changed.");

    // Correct pattern: finish ALL edits, then project ALL summaries and submit without
    // any more mutations. A lock must cover this whole region in a concurrent app.
    a.Day = 3;
    b.Day = 2;
    var requests = new[] {
        aw.CreateStateCommitRequest(a, Preview(a)),
        bw.CreateStateCommitRequest(b, Preview(b)),
    };
    foreach (CommitInfo commit in repo.CommitBranches(requests).Values) { AssertPreview(repo, commit); }
}

static void ExpectMissingModel(Func<WorkingCopy> open) {
    try { using WorkingCopy unexpected = open(); }
    catch (InvalidDataException) { return; }
    throw new InvalidOperationException("Expected missing model restoration to fail.");
}

static void AssertPreview(Repository repo, CommitInfo commit) {
    SummaryReadResult decoded = SummaryCodec.Read(commit.Summary);
    var restored = (GardenState)repo.LoadState(commit.Id);
    Need(decoded.Status == SummaryStatus.Available && decoded.Preview == SummaryCodec.FromState(restored),
        "The saved preview disagrees with its exact State commit.");
}

static void Need(bool condition, string message) {
    if (!condition) { throw new InvalidOperationException(message); }
}
