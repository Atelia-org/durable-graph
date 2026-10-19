using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;
using System.Reflection;
using Atelia.FrameStore;
using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;
using State = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.State;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class RepositoryTagTests : IDisposable {
    private const string Prefix = "durable-repository-tags-";
    private readonly string _root = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1000000, 1);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TagsKeepStateOrEventFirstPositionAcrossBranchChangesReopenAndContinuation(bool eventFirst) {
        CommitId old;
        using (Repository repository = Create()) {
            using WorkingCopy main = eventFirst
                ? repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("main", new Node { Value = 1 }, parameters: NoRebase).Id)
                : repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", new Node { Value = 1 }, parameters: NoRebase).Id);
            old = main.BaseCommitId;
            repository.CreateTag("start", old);
            CommitId intermediate = main.CommitEvent(new Node { Value = 2 }, parameters: NoRebase).Id;
            CommitId advanced = main.CommitState(new Node { Value = 3 }, parameters: NoRebase).Id;
            Assert.Equal(old, repository.ResolveTag("start"));
            Assert.Equal(advanced, repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id);
            main.Dispose();
            BranchInfo historical = repository.ForkBranch(intermediate, "historical");
            Assert.Equal(intermediate, repository.ReadCurrentCommit(historical.Id).Commit.Id);
            Assert.Equal(advanced, repository.ReadCurrentCommit(main.BranchId).Commit.Id);
            Assert.Equal(old, repository.ResolveTag("start"));
        }
        using (Repository reopened = Repository.OpenExisting(_root, SharedReadModel.Models())) {
            CommitId selected = reopened.ResolveTag("start");
            Assert.Equal(old, selected);
            Assert.Equal(eventFirst ? CommitKind.Event : CommitKind.State, reopened.ReadCommitInfo(selected).Kind);
            Assert.Equal((byte)1, Assert.IsType<Node>(eventFirst ? reopened.LoadEvent(selected) : reopened.LoadState(selected)).Value);
            CommitId sourceHead = reopened.ReadCurrentCommit(BranchNamed(reopened, "main")).Commit.Id;
            Assert.NotEqual(selected, sourceHead);
            BranchInfo fromTag = reopened.ForkBranch(selected, "from-tag");
            Assert.Equal(selected, reopened.ReadCurrentCommit(fromTag.Id).Commit.Id);
            using WorkingCopy checkout = reopened.OpenWorkingCopy(BranchNamed(reopened, "from-tag"));
            using WorkingCopy fork = reopened.OpenWorkingCopy(reopened.ForkBranch(selected, "fork-from-tag").Id);
            Assert.Equal(selected, checkout.BaseCommitId);
            Assert.Equal(selected, fork.BaseCommitId);
            if (eventFirst) {
                Assert.Null(checkout.State);
                Assert.Null(fork.State);
            } else {
                Assert.Equal((byte)1, Assert.IsType<Node>(checkout.State).Value);
                Assert.Equal((byte)1, Assert.IsType<Node>(fork.State).Value);
                Assert.NotSame(checkout.State, fork.State);
            }
            CommitId saved = checkout.CommitState(new Node { Value = 8 }, parameters: NoRebase).Id;
            Assert.Equal(selected.Value, Resources(reopened).ReadPrevious(saved.Value)!.Id);
            CommitId nextEvent = fork.CommitEvent(new Node { Value = 7 }, parameters: NoRebase).Id;
            Assert.Equal(selected.Value, Resources(reopened).ReadPrevious(nextEvent.Value)!.Id);
            CommitId nextState = fork.CommitState(new Node { Value = 9 }, parameters: NoRebase).Id;
            Assert.Equal(nextEvent.Value, Resources(reopened).ReadPrevious(nextState.Value)!.Id);
            Assert.Equal(selected, reopened.ResolveTag("start"));
            Assert.Equal(sourceHead, reopened.ReadCurrentCommit(BranchNamed(reopened, "main")).Commit.Id);
        }
        using Repository verify = Repository.OpenReadOnlyExisting(_root, SharedReadModel.Models());
        Assert.Equal(old, verify.ResolveTag("start"));
        Assert.Equal((byte)8, Assert.IsType<Node>(verify.LoadState(verify.ReadCurrentCommit(BranchNamed(verify, "from-tag")).Commit.Id)).Value);
        Assert.Equal((byte)9, Assert.IsType<Node>(verify.LoadState(verify.ReadCurrentCommit(BranchNamed(verify, "fork-from-tag")).Commit.Id)).Value);
    }

    [Fact]
    public void TagsUseIndependentOrdinalNamesAndDuplicateCreationNeverWritesOrFaults() {
        using (Repository seed = Create()) {
            using WorkingCopy branch = seed.OpenWorkingCopy(seed.CreateRootBranchFromState("same", new Node { Value = 1 }, parameters: NoRebase).Id);
            seed.CreateTag("same", branch.BaseCommitId);
            CommitId next = branch.CommitState(new Node { Value = 2 }, parameters: NoRebase).Id;
            seed.CreateTag("same-other", next);
            // The tag namespace also does not reserve future branch names.
            seed.CreateTag("future-branch", next);
            seed.ForkBranch(next, "future-branch");
            Assert.Equal(new[] { "future-branch", "same" }, seed.ListBranches().Select(branch => branch.DisplayLabel).Order());
        }
        var before = SnapshotFiles();
        using (Repository repository = Repository.OpenExisting(_root, SharedReadModel.Models())) {
            CommitId lower = repository.ResolveTag("same"), upper = repository.ResolveTag("same-other");
            Assert.NotEqual(lower, upper);
            Assert.Equal((byte)1, Assert.IsType<Node>(repository.LoadState(lower)).Value);
            Assert.Equal((byte)2, Assert.IsType<Node>(repository.LoadState(upper)).Value);
            AssertOrdinaryFailure("DuplicateTagName", () => repository.CreateTag("same", lower));
            AssertOrdinaryFailure("DuplicateTagName", () => repository.CreateTag("same", upper));
            AssertOrdinaryFailure("TagNotFound", () => repository.ResolveTag("SAME"));
            Assert.False(repository.IsFaulted);
            Assert.Equal(lower, repository.ResolveTag("same"));
            Assert.Equal(upper, repository.ResolveTag("same-other"));
            Assert.Equal(upper, repository.ReadCurrentCommit(BranchNamed(repository, "same")).Commit.Id);
        }
        AssertFiles(before);
    }

    [Fact]
    public void MissingTagIsExplicitAndDoesNotFallBackToSameNamedBranch() {
        using (Repository seed = Create()) {
            using WorkingCopy main = seed.OpenWorkingCopy(seed.CreateRootBranchFromState("main", new Node(), parameters: NoRebase).Id);
        }
        var before = SnapshotFiles();
        using (Repository repository = Repository.OpenExisting(_root, new StateModelRegistry())) {
            AssertOrdinaryFailure("TagNotFound", () => repository.ResolveTag("main"));
            AssertOrdinaryFailure("TagNotFound", () => repository.ResolveTag("missing"));
            Assert.Equal("main", Assert.Single(repository.ListBranches()).DisplayLabel);
            Assert.False(repository.IsFaulted);
            Assert.Null(repository.PreparedStateEntry);
        }
        AssertFiles(before);
    }

    [Fact]
    public void CreatingTagWithEmptyRegistryAppendsManagementGraphAndPublishesOnlyBookmarkHead() {
        using (Repository seed = Create()) {
            using WorkingCopy main = seed.OpenWorkingCopy(seed.CreateRootBranchFromState("main", new Node { Value = 1 }, parameters: NoRebase).Id);
        }
        var before = SnapshotFiles();
        using (Repository metadata = Repository.OpenExisting(_root, new StateModelRegistry())) {
            CommitId head = metadata.ReadCurrentCommit(BranchNamed(metadata, "main")).Commit.Id;
            metadata.CreateTag("saved", head);
            Assert.Equal(head, metadata.ResolveTag("saved"));
            Assert.Equal(head, metadata.ReadCurrentCommit(BranchNamed(metadata, "main")).Commit.Id);
            Assert.Null(metadata.PreparedStateEntry);
        }
        string bookmarkFile = Path.Combine(_root, "versions", "refs", "00000003.rbf");
        var after = SnapshotFiles();
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var pair in before) {
            if (pair.Key == bookmarkFile) {
                Assert.True(after[pair.Key].Length > pair.Value.Length);
                Assert.Equal(pair.Value, after[pair.Key][..pair.Value.Length]);
            } else if (pair.Key.StartsWith(Path.Combine(_root, "data"), StringComparison.Ordinal)) {
                Assert.True(after[pair.Key].Length >= pair.Value.Length);
                Assert.Equal(pair.Value, after[pair.Key][..pair.Value.Length]);
            } else { Assert.Equal(pair.Value, after[pair.Key]); }
        }
        using Repository verify = Repository.OpenReadOnlyExisting(_root, new StateModelRegistry());
        Assert.Single(verify.ReadFrames(BranchNamed(verify, "main")));
    }

    [Fact]
    public void OldHandleSurvivesReopenWhileForeignAndDefaultHandlesRejectBeforeTagPublication() {
        CommitId old;
        using (Repository seed = Create()) {
            using WorkingCopy branch = seed.OpenWorkingCopy(seed.CreateRootBranchFromState("main", new Node { Value = 1 }, parameters: NoRebase).Id);
            old = branch.BaseCommitId;
            seed.CreateTag("saved", old);
        }
        var before = SnapshotFiles();
        using Repository foreign = Repository.CreateNew(_root + "-foreign", SharedReadModel.Models());
        using WorkingCopy foreignBranch = foreign.OpenWorkingCopy(foreign.CreateRootBranchFromState("main", new Node { Value = 2 }, parameters: NoRebase).Id);
        using (Repository repository = Repository.OpenExisting(_root, SharedReadModel.Models())) {
            CommitId current = repository.ResolveTag("saved");
            Assert.Equal(old, current);
            Assert.NotEqual(foreignBranch.BaseCommitId, current);
            Assert.Equal(current, old); // Value handles remain valid after closing and reopening their repository.
            Assert.Throws<KeyNotFoundException>(() => repository.CreateTag("foreign", foreignBranch.BaseCommitId));
            Assert.Throws<ArgumentException>(() => repository.CreateTag("null", default));
            AssertOrdinaryFailure("TagNotFound", () => repository.ResolveTag("stale"));
            AssertOrdinaryFailure("TagNotFound", () => repository.ResolveTag("foreign"));
            AssertOrdinaryFailure("TagNotFound", () => repository.ResolveTag("null"));
            Assert.False(repository.IsFaulted);
            Assert.Equal((byte)1, Assert.IsType<Node>(repository.LoadState(current)).Value);
            Assert.Equal(current, repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id);
        }
        AssertFiles(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadonlyEmptyRegistryResolvesTagsWithoutMaterializationOrDiskMutation(bool eventFirst) {
        CommitId old;
        using (Repository seed = Create()) {
            using WorkingCopy branch = eventFirst
                ? seed.OpenWorkingCopy(seed.CreateRootBranchFromEvent("main", new Node { Value = 5 }, parameters: NoRebase).Id)
                : seed.OpenWorkingCopy(seed.CreateRootBranchFromState("main", new Node { Value = 5 }, parameters: NoRebase).Id);
            old = branch.BaseCommitId;
            seed.CreateTag("saved", old);
        }
        var before = SnapshotFiles();
        using (Repository readOnly = Repository.OpenReadOnlyExisting(_root, new StateModelRegistry())) {
            GraphReadStatistics statistics = new();
            readOnly.RestorationStatistics = statistics;
            CommitId current = readOnly.ResolveTag("saved");
            Assert.Equal(old, current);
            Assert.Equal(old, current);
            Assert.Equal(readOnly.ReadCurrentCommit(BranchNamed(readOnly, "main")).Commit.Id, current);
            Assert.Equal(eventFirst ? CommitKind.Event : CommitKind.State, readOnly.ReadCommitInfo(current).Kind);
            Assert.Throws<InvalidOperationException>(() => readOnly.CreateTag("blocked", current));
            AssertOrdinaryFailure("TagNotFound", () => readOnly.ResolveTag("missing"));
            Assert.False(readOnly.IsFaulted);
            Assert.Null(readOnly.PreparedStateEntry);
            AssertNoGraphWork(statistics);
            // The empty catalog truly has no materialization capability.
            Assert.ThrowsAny<Exception>(() => readOnly.ReadCheckpoint(current));
        }
        AssertFiles(before);
    }

    [Fact]
    public void TagOperationsPreserveActiveCheckoutStateSavingBaselineAndPreparedSlot() {
        int allocations = 0, hydrations = 0, bodyPreparations = 0;
        using Repository repository = Create(SharedReadModel.Models(
            allocate: () => { allocations++; return new Node(); }, hydrate: _ => hydrations++,
            prepareBase: (in State state) => { bodyPreparations++; return SharedReadModel.Base(state); }));
        using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", new Node { Value = 1, Next = new Node { Value = 2 } }, parameters: NoRebase).Id);
        CommitId stateAddress = main.BaseCommitId;
        using WorkingCopy other = repository.OpenWorkingCopy(repository.ForkBranch(stateAddress, "other").Id);
        PreparedStateRestoration entry = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
        NormalizedGraphState baseline = main.Baseline!, otherBaseline = other.Baseline!;
        Node mainState = Assert.IsType<Node>(main.State), otherState = Assert.IsType<Node>(other.State);
        mainState.Value = 3;
        otherState.Value = 4;
        int allocated = allocations, hydrated = hydrations, prepared = bodyPreparations;
        GraphReadStatistics statistics = new();
        repository.RestorationStatistics = statistics;
        repository.CreateTag("main", stateAddress);
        repository.CreateTag("other", other.BaseCommitId);
        Assert.Equal(stateAddress, repository.ResolveTag("main"));
        Assert.Equal(other.BaseCommitId, repository.ResolveTag("other"));
        Assert.Same(entry, repository.PreparedStateEntry);
        Assert.Same(baseline, main.Baseline);
        Assert.Same(otherBaseline, other.Baseline);
        Assert.Same(mainState, main.State);
        Assert.Same(otherState, other.State);
        Assert.Equal(stateAddress, main.BaseCommitId);
        Assert.Equal(stateAddress, other.BaseCommitId);
        Assert.Equal(allocated, allocations);
        Assert.Equal(hydrated, hydrations);
        Assert.Equal(prepared, bodyPreparations);
        AssertNoGraphWork(statistics);
        Assert.Throws<InvalidOperationException>(() => repository.OpenWorkingCopy(BranchNamed(repository, "main")));
        Assert.Throws<InvalidOperationException>(() => repository.OpenWorkingCopy(BranchNamed(repository, "other")));
        CommitId savedMain = main.CommitState(parameters: NoRebase).Id, savedOther = other.CommitState(parameters: NoRebase).Id;
        Assert.Equal(stateAddress.Value, Resources(repository).ReadPrevious(savedMain.Value)!.Id);
        Assert.Equal(stateAddress.Value, Resources(repository).ReadPrevious(savedOther.Value)!.Id);
        Assert.Equal((byte)3, Assert.IsType<Node>(repository.LoadState(savedMain)).Value);
        Assert.Equal((byte)4, Assert.IsType<Node>(repository.LoadState(savedOther)).Value);
        Assert.Equal(stateAddress, repository.ResolveTag("main"));
        Assert.Equal(stateAddress, repository.ResolveTag("other"));
    }

    [Fact]
    public void TagCallsRejectDisposedRepository() {
        Repository repository = Create();
        CommitId address;
        try {
            using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", new Node(), parameters: NoRebase).Id);
            address = main.BaseCommitId;
            repository.CreateTag("saved", address);
        } finally { repository.Dispose(); }
        var before = SnapshotFiles();
        Assert.Throws<ObjectDisposedException>(() => repository.ResolveTag("saved"));
        Assert.Throws<ObjectDisposedException>(() => repository.CreateTag("blocked", address));
        AssertFiles(before);
    }

    [Fact]
    public void InvalidTagNamesRejectWithoutWritingWhileLengthBoundariesPersist() {
        string longest = new('a', RepositoryResources.MaxBookmarkNameCodeUnits);
        using (Repository seed = Create()) {
            using WorkingCopy main = seed.OpenWorkingCopy(seed.CreateRootBranchFromState("main", new Node(), parameters: NoRebase).Id);
            seed.CreateTag("a", main.BaseCommitId);
            seed.CreateTag(longest, main.BaseCommitId);
            seed.CreateTag("Uppercase", main.BaseCommitId);
            seed.CreateTag("trailing.lock", main.BaseCommitId);
            seed.CreateTag("标签/正文", main.BaseCommitId);
        }
        var before = SnapshotFiles();
        using (Repository repository = Repository.OpenExisting(_root, new StateModelRegistry())) {
            CommitId address = repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id;
            Assert.Equal(address, repository.ResolveTag("a"));
            Assert.Equal(address, repository.ResolveTag(longest));
            string?[] invalidNames = [null, "", "\uD800", "\uDC00", new string('a', RepositoryResources.MaxBookmarkNameCodeUnits + 1)];
            foreach (string? name in invalidNames) {
                AssertOrdinaryFailure("InvalidTagName", () => repository.CreateTag(name!, address));
                AssertOrdinaryFailure("InvalidTagName", () => repository.ResolveTag(name!));
                Assert.False(repository.IsFaulted);
            }
        }
        AssertFiles(before);
    }

    private static void AssertOrdinaryFailure(string errorName, Action action) {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(action);
        Assert.Contains($"[DurableGraph.{errorName}]", error.Message);
    }

    private static void AssertNoGraphWork(GraphReadStatistics statistics) {
        Assert.Equal(0, statistics.PreparedStateHits);
        Assert.Equal(0, statistics.PreparedStateMisses);
        Assert.Equal(0, statistics.PreparedGraphs);
        Assert.Equal(0, statistics.DecodedObjects);
        Assert.Equal(0, statistics.NormalizedObjects);
        Assert.Equal(0, statistics.AllocatedObjects);
        Assert.Equal(0, statistics.HydratedObjects);
        Assert.Equal(0, statistics.ReusedImmutableLeaves);
    }

    private Repository Create(StateModelRegistry? models = null) => Repository.CreateNew(_root, models ?? SharedReadModel.Models());
    private Dictionary<string, byte[]> SnapshotFiles() => Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => path, File.ReadAllBytes);
    private void AssertFiles(Dictionary<string, byte[]> before) {
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order());
        foreach (var pair in before) Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
    }
    public void Dispose() {
        SharedReadModel.DeleteFixture(_root, Prefix);
        SharedReadModel.DeleteFixture(_root + "-foreign", Prefix);
    }
}
