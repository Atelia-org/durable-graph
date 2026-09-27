using System.Reflection;
using Atelia.RbfSegmentStore;
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
        CheckpointAddress old;
        using (Repository repository = Create()) {
            using BranchCheckout main = eventFirst
                ? repository.CreateBranchFromEvent("main", new Node { Value = 1 }, NoRebase)
                : repository.CreateBranch("main", new Node { Value = 1 }, NoRebase);
            old = main.Head;
            repository.CreateTag("start", old);
            CheckpointAddress intermediate = main.CommitEvent(new Node { Value = 2 }, NoRebase);
            CheckpointAddress advanced = main.CommitState(new Node { Value = 3 }, NoRebase);
            Assert.Equal(old, repository.ResolveTag("start"));
            Assert.Equal(advanced, repository.GetHead("main"));
            main.Dispose();
            repository.MoveBranch("main", advanced, intermediate);
            Assert.Equal(intermediate, repository.GetHead("main"));
            Assert.Equal(old, repository.ResolveTag("start"));
        }
        using (Repository reopened = Repository.OpenExisting(_root, SharedReadModel.Models())) {
            CheckpointAddress selected = reopened.ResolveTag("start");
            Assert.NotEqual(old, selected);
            Assert.Equal(old.Address, selected.Address);
            Assert.Equal(old.RevisionAddress, selected.RevisionAddress);
            Assert.Equal(old.RootId, selected.RootId);
            Assert.Equal(eventFirst ? GraphFrameKind.Event : GraphFrameKind.State, selected.Kind);
            Assert.Equal((byte)1, Assert.IsType<Node>(eventFirst ? reopened.ReadEvent(selected) : reopened.ReadState(selected)).Value);
            CheckpointAddress sourceHead = reopened.GetHead("main");
            Assert.NotEqual(selected, sourceHead);
            Assert.Equal(selected, reopened.CreateBranch("from-tag", selected));
            using BranchCheckout checkout = reopened.Checkout("from-tag");
            using BranchCheckout fork = reopened.Fork("fork-from-tag", selected);
            Assert.Equal(selected, checkout.Head);
            Assert.Equal(selected, fork.Head);
            if (eventFirst) {
                Assert.Null(checkout.State);
                Assert.Null(fork.State);
            } else {
                Assert.Equal((byte)1, Assert.IsType<Node>(checkout.State).Value);
                Assert.Equal((byte)1, Assert.IsType<Node>(fork.State).Value);
                Assert.NotSame(checkout.State, fork.State);
            }
            Assert.Equal(selected.Address, checkout.CommitState(new Node { Value = 8 }, NoRebase).Parent);
            CheckpointAddress nextEvent = fork.CommitEvent(new Node { Value = 7 }, NoRebase);
            Assert.Equal(selected.Address, nextEvent.Parent);
            Assert.Equal(nextEvent.Address, fork.CommitState(new Node { Value = 9 }, NoRebase).Parent);
            Assert.Equal(selected, reopened.ResolveTag("start"));
            Assert.Equal(sourceHead, reopened.GetHead("main"));
        }
        using Repository verify = Repository.OpenReadOnlyExisting(_root, SharedReadModel.Models());
        Assert.Equal(old.Address, verify.ResolveTag("start").Address);
        Assert.Equal((byte)8, Assert.IsType<Node>(verify.ReadState(verify.GetHead("from-tag"))).Value);
        Assert.Equal((byte)9, Assert.IsType<Node>(verify.ReadState(verify.GetHead("fork-from-tag"))).Value);
    }

    [Fact]
    public void TagsUseIndependentOrdinalNamesAndDuplicateCreationNeverWritesOrFaults() {
        using (Repository seed = Create()) {
            using BranchCheckout branch = seed.CreateBranch("same", new Node { Value = 1 }, NoRebase);
            seed.CreateTag("same", branch.Head);
            CheckpointAddress next = branch.CommitState(new Node { Value = 2 }, NoRebase);
            seed.CreateTag("same-other", next);
            // The tag namespace also does not reserve future branch names.
            seed.CreateTag("future-branch", next);
            seed.CreateBranch("future-branch", next);
            Assert.Equal(new[] { "future-branch", "same" }, seed.ListBranches().Order());
        }
        var before = SnapshotFiles();
        using (Repository repository = Repository.OpenExisting(_root, SharedReadModel.Models())) {
            CheckpointAddress lower = repository.ResolveTag("same"), upper = repository.ResolveTag("same-other");
            Assert.NotEqual(lower, upper);
            Assert.Equal((byte)1, Assert.IsType<Node>(repository.ReadState(lower)).Value);
            Assert.Equal((byte)2, Assert.IsType<Node>(repository.ReadState(upper)).Value);
            AssertOrdinaryFailure("TagAlreadyExists", () => repository.CreateTag("same", lower));
            AssertOrdinaryFailure("TagAlreadyExists", () => repository.CreateTag("same", upper));
            AssertOrdinaryFailure("TagNameInvalid", () => repository.ResolveTag("SAME"));
            Assert.False(repository.IsFaulted);
            Assert.Equal(lower, repository.ResolveTag("same"));
            Assert.Equal(upper, repository.ResolveTag("same-other"));
            Assert.Equal(upper, repository.GetHead("same"));
        }
        AssertFiles(before);
    }

    [Fact]
    public void MissingTagIsExplicitAndDoesNotFallBackToSameNamedBranch() {
        using (Repository seed = Create()) {
            using BranchCheckout main = seed.CreateBranch("main", new Node(), NoRebase);
        }
        var before = SnapshotFiles();
        using (Repository repository = Repository.OpenExisting(_root, new StateModelRegistry())) {
            AssertOrdinaryFailure("TagNotFound", () => repository.ResolveTag("main"));
            AssertOrdinaryFailure("TagNotFound", () => repository.ResolveTag("missing"));
            Assert.Equal("main", Assert.Single(repository.ListBranches()));
            Assert.False(repository.IsFaulted);
            Assert.Null(repository.PreparedStateEntry);
        }
        AssertFiles(before);
    }

    [Fact]
    public void CreatingTagWithEmptyRegistryChangesOnlyExistingTagMetadataLog() {
        using (Repository seed = Create()) {
            using BranchCheckout main = seed.CreateBranch("main", new Node { Value = 1 }, NoRebase);
        }
        var before = SnapshotFiles();
        using (Repository metadata = Repository.OpenExisting(_root, new StateModelRegistry())) {
            CheckpointAddress head = metadata.GetHead("main");
            metadata.CreateTag("saved", head);
            Assert.Equal(head, metadata.ResolveTag("saved"));
            Assert.Equal(head, metadata.GetHead("main"));
            Assert.Null(metadata.PreparedStateEntry);
        }
        string tagLog = Path.Combine(_root, "journal", "refs", "ref-op-log.rbf");
        Assert.Contains(tagLog, before.Keys);
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order());
        foreach (var pair in before) {
            byte[] after = File.ReadAllBytes(pair.Key);
            if (string.Equals(pair.Key, tagLog, StringComparison.OrdinalIgnoreCase)) {
                Assert.True(after.Length > pair.Value.Length);
            } else {
                Assert.Equal(pair.Value, after);
            }
        }
        // Chronological traversal has a separate writable forward-plan disk cache.
        // Keep it outside the tag-only write window, and inspect history read-only.
        using Repository verify = Repository.OpenReadOnlyExisting(_root, new StateModelRegistry());
        Assert.Single(verify.ReadFrames("main"));
    }

    [Fact]
    public void OldForeignAndNullHandlesRejectBeforeTagPublicationAndNewResolutionIsUsable() {
        CheckpointAddress old;
        using (Repository seed = Create()) {
            using BranchCheckout branch = seed.CreateBranch("main", new Node { Value = 1 }, NoRebase);
            old = branch.Head;
            seed.CreateTag("saved", old);
        }
        var before = SnapshotFiles();
        using Repository foreign = Repository.CreateNew(_root + "-foreign", SharedReadModel.Models(),
            new() { NewStoreLayout = RbfSegmentStoreLayout.Flat });
        using BranchCheckout foreignBranch = foreign.CreateBranch("main", new Node { Value = 2 }, NoRebase);
        using (Repository repository = Repository.OpenExisting(_root, SharedReadModel.Models())) {
            CheckpointAddress current = repository.ResolveTag("saved");
            Assert.NotEqual(old, current);
            Assert.NotEqual(foreignBranch.Head, current);
            Assert.Throws<ArgumentException>(() => repository.CreateTag("stale", old));
            Assert.Throws<ArgumentException>(() => repository.CreateTag("foreign", foreignBranch.Head));
            Assert.Throws<ArgumentNullException>(() => repository.CreateTag("null", null!));
            AssertOrdinaryFailure("TagNotFound", () => repository.ResolveTag("stale"));
            AssertOrdinaryFailure("TagNotFound", () => repository.ResolveTag("foreign"));
            AssertOrdinaryFailure("TagNotFound", () => repository.ResolveTag("null"));
            Assert.False(repository.IsFaulted);
            Assert.Equal((byte)1, Assert.IsType<Node>(repository.ReadState(current)).Value);
            Assert.Equal(current, repository.GetHead("main"));
        }
        AssertFiles(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadonlyEmptyRegistryResolvesTagsWithoutMaterializationOrDiskMutation(bool eventFirst) {
        CheckpointAddress old;
        using (Repository seed = Create()) {
            using BranchCheckout branch = eventFirst
                ? seed.CreateBranchFromEvent("main", new Node { Value = 5 }, NoRebase)
                : seed.CreateBranch("main", new Node { Value = 5 }, NoRebase);
            old = branch.Head;
            seed.CreateTag("saved", old);
        }
        var before = SnapshotFiles();
        using (Repository readOnly = Repository.OpenReadOnlyExisting(_root, new StateModelRegistry())) {
            GraphReadStatistics statistics = new();
            readOnly.RestorationStatistics = statistics;
            CheckpointAddress current = readOnly.ResolveTag("saved");
            Assert.NotEqual(old, current);
            Assert.Equal(old.Address, current.Address);
            Assert.Equal(readOnly.GetHead("main"), current);
            Assert.Equal(eventFirst ? GraphFrameKind.Event : GraphFrameKind.State, current.Kind);
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
        using BranchCheckout main = repository.CreateBranch("main", new Node { Value = 1, Next = new Node { Value = 2 } }, NoRebase);
        CheckpointAddress stateAddress = main.Head;
        using BranchCheckout other = repository.Fork("other", stateAddress);
        PreparedStateRestoration entry = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
        NormalizedRevision baseline = Baseline(main.Workspace), otherBaseline = Baseline(other.Workspace);
        Node mainState = Assert.IsType<Node>(main.State), otherState = Assert.IsType<Node>(other.State);
        mainState.Value = 3;
        otherState.Value = 4;
        int allocated = allocations, hydrated = hydrations, prepared = bodyPreparations;
        GraphReadStatistics statistics = new();
        repository.RestorationStatistics = statistics;
        repository.CreateTag("main", stateAddress);
        repository.CreateTag("other", other.Head);
        Assert.Equal(stateAddress, repository.ResolveTag("main"));
        Assert.Equal(other.Head, repository.ResolveTag("other"));
        Assert.Same(entry, repository.PreparedStateEntry);
        Assert.Same(baseline, Baseline(main.Workspace));
        Assert.Same(otherBaseline, Baseline(other.Workspace));
        Assert.Same(mainState, main.State);
        Assert.Same(otherState, other.State);
        Assert.Equal(stateAddress, main.Head);
        Assert.Equal(stateAddress, other.Head);
        Assert.Equal(allocated, allocations);
        Assert.Equal(hydrated, hydrations);
        Assert.Equal(prepared, bodyPreparations);
        AssertNoGraphWork(statistics);
        Assert.Throws<InvalidOperationException>(() => repository.Checkout("main"));
        Assert.Throws<InvalidOperationException>(() => repository.Checkout("other"));
        CheckpointAddress savedMain = main.CommitState(NoRebase), savedOther = other.CommitState(NoRebase);
        Assert.Equal(stateAddress.Address, savedMain.Parent);
        Assert.Equal(stateAddress.Address, savedOther.Parent);
        Assert.Equal((byte)3, Assert.IsType<Node>(repository.ReadState(savedMain)).Value);
        Assert.Equal((byte)4, Assert.IsType<Node>(repository.ReadState(savedOther)).Value);
        Assert.Equal(stateAddress, repository.ResolveTag("main"));
        Assert.Equal(stateAddress, repository.ResolveTag("other"));
    }

    [Fact]
    public void TagCallsRejectDisposedRepository() {
        Repository repository = Create();
        CheckpointAddress address;
        try {
            using BranchCheckout main = repository.CreateBranch("main", new Node(), NoRebase);
            address = main.Head;
            repository.CreateTag("saved", address);
        } finally { repository.Dispose(); }
        var before = SnapshotFiles();
        Assert.Throws<ObjectDisposedException>(() => repository.ResolveTag("saved"));
        Assert.Throws<ObjectDisposedException>(() => repository.CreateTag("blocked", address));
        AssertFiles(before);
    }

    [Fact]
    public void InvalidTagNamesRejectWithoutWritingWhileLengthBoundariesPersist() {
        string longest = new('a', 128);
        using (Repository seed = Create()) {
            using BranchCheckout main = seed.CreateBranch("main", new Node(), NoRebase);
            seed.CreateTag("a", main.Head);
            seed.CreateTag(longest, main.Head);
        }
        var before = SnapshotFiles();
        using (Repository repository = Repository.OpenExisting(_root, new StateModelRegistry())) {
            CheckpointAddress address = repository.GetHead("main");
            Assert.Equal(address, repository.ResolveTag("a"));
            Assert.Equal(address, repository.ResolveTag(longest));
            string?[] invalidNames = [null, "", "Uppercase", "trailing.lock", "trailing.", new string('a', 129)];
            foreach (string? name in invalidNames) {
                AssertOrdinaryFailure("TagNameInvalid", () => repository.CreateTag(name!, address));
                AssertOrdinaryFailure("TagNameInvalid", () => repository.ResolveTag(name!));
                Assert.False(repository.IsFaulted);
            }
        }
        AssertFiles(before);
    }

    private static void AssertOrdinaryFailure(string errorName, Action action) {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(action);
        Assert.Contains($"[EventJournal.{errorName}]", error.Message);
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

    private static NormalizedRevision Baseline(WorldWorkspace workspace) => (NormalizedRevision)typeof(WorldWorkspace)
        .GetField("_baseline", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(workspace)!;
    private Repository Create(StateModelRegistry? models = null) => Repository.CreateNew(_root, models ?? SharedReadModel.Models(),
        new() { NewStoreLayout = RbfSegmentStoreLayout.Flat });
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
