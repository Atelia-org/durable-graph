using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;

using Atelia.FrameStore;
using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class DB078EventQueryTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"durable-db078-query-{Guid.NewGuid():N}");
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1000000, 1);

    [Fact]
    public void MetadataQueryWithEmptyModelsCrossesStatesAndIgnoresPhysicalSiblingRecords() {
        using (Repository repository = Create()) {
            CommitId e0;
            using (WorkingCopy branch = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("main", new Node { Value = 1 }, parameters: NoRebase).Id)) {
                e0 = branch.BaseCommitId;
                branch.CommitState(new Node { Value = 2 }, parameters: NoRebase);
                branch.CommitEvent(new Node { Value = 3 }, parameters: NoRebase);
            }
            repository.ForkBranch(e0, "sibling");
            using (WorkingCopy sibling = repository.OpenWorkingCopy(BranchNamed(repository, "sibling"))) {
                sibling.CommitEvent(new Node { Value = 99 }, parameters: NoRebase);
            }
            using (WorkingCopy main = repository.OpenWorkingCopy(BranchNamed(repository, "main"))) {
                main.CommitState(parameters: NoRebase);
                main.CommitEvent(new Node { Value = 4 }, parameters: NoRebase);
                main.CommitState(parameters: NoRebase);
            }
        }
        using Repository empty = Repository.OpenReadOnlyExisting(_root, new StateModelRegistry());
        var frames = empty.ReadFrames(BranchNamed(empty, "main"));
        CommitId[] expected = frames.Where(frame => frame.Kind == CommitKind.Event).Select(frame => frame.Id).ToArray();
        Assert.Equal(3, expected.Length);
        Assert.Equal(expected.Reverse(), empty.EnumerateEvents(empty.ReadCurrentCommit(BranchNamed(empty, "main")).Commit.Id).Select(commit => commit.Id));
        Assert.Equal(expected, empty.EnumerateEvents(empty.ReadCurrentCommit(BranchNamed(empty, "main")).Commit.Id, HistoryOrder.OldestFirst).Select(commit => commit.Id));
        Assert.Equal(new[] { expected[2], expected[1] }, empty.EnumerateEvents(frames[^1].Id, afterExclusive: frames[1].Id).Select(commit => commit.Id));
        Assert.Equal(new[] { expected[1] }, empty.EnumerateEvents(expected[1], afterExclusive: expected[0]).Select(commit => commit.Id));
        Assert.Empty(empty.EnumerateEvents(frames[^1].Id, afterExclusive: frames[^1].Id).Select(commit => commit.Id));
        Assert.Empty(empty.EnumerateEvents(frames[1].Id, afterExclusive: expected[0]).Select(commit => commit.Id));
        Assert.Equal(expected[2], empty.EnumerateEvents(frames[^1].Id).Select(commit => commit.Id).First());
    }

    [Theory]
    [InlineData(HistoryOrder.NewestFirst)]
    [InlineData(HistoryOrder.OldestFirst)]
    public void InvalidBoundsFailBeforeAnyItemCanEscape(HistoryOrder order) {
        using Repository repository = Create();
        CommitId initial, end, sibling;
        using (WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("main", new Node(), parameters: NoRebase).Id)) {
            initial = main.BaseCommitId;
            end = main.CommitEvent(new Node(), parameters: NoRebase).Id;
        }
        repository.ForkBranch(initial, "sibling");
        using (WorkingCopy other = repository.OpenWorkingCopy(BranchNamed(repository, "sibling"))) {
            sibling = other.CommitEvent(new Node(), parameters: NoRebase).Id;
        }
        foreach ((CommitId selected, CommitId lower) in new[] { (end, sibling), (initial, end) }) {
            int delivered = 0;
            Assert.Throws<ArgumentException>(() => {
                foreach (CommitId _ in repository.EnumerateEvents(selected, order, lower).Select(commit => commit.Id)) { delivered++; }
            });
            Assert.Equal(0, delivered);
        }
        Assert.Equal(new[] { end, initial }, repository.EnumerateEvents(end).Select(commit => commit.Id));
    }

    [Fact]
    public void DefaultAndForeignIdsAreRejectedWhilePersistedIdsRemainQueryable() {
        int allocations = 0;
        CommitId stale;
        using (Repository repository = Create()) {
            using WorkingCopy branch = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("main", new Node(), parameters: NoRebase).Id);
            stale = branch.BaseCommitId;
        }
        using Repository reopened = Repository.OpenExisting(_root, SharedReadModel.Models(allocate: () => { allocations++; return new Node(); }));
        using Repository foreign = Repository.CreateNew(Path.Combine(_root, "foreign"), SharedReadModel.Models());
        using WorkingCopy other = foreign.OpenWorkingCopy(foreign.CreateRootBranchFromEvent("main", new Node(), parameters: NoRebase).Id);
        CommitId valid = reopened.ReadCurrentCommit(BranchNamed(reopened, "main")).Commit.Id;
        Assert.Equal(stale, valid);
        foreach (CommitId bad in new[] { default(CommitId), other.BaseCommitId }) {
            Assert.ThrowsAny<Exception>(() => reopened.ReadCheckpoint(bad));
            Assert.ThrowsAny<Exception>(() => reopened.EnumerateEvents(bad).Select(commit => commit.Id).ToArray());
            Assert.ThrowsAny<Exception>(() => reopened.EnumerateEvents(valid, afterExclusive: bad).Select(commit => commit.Id).ToArray());
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => reopened.EnumerateEvents(valid, (HistoryOrder)37).Select(commit => commit.Id).ToArray());
        Assert.Equal(0, allocations);
        Assert.False(reopened.IsFaulted);
        Assert.Single(reopened.EnumerateEvents(valid).Select(commit => commit.Id));
    }

    [Theory]
    [InlineData(HistoryOrder.NewestFirst)]
    [InlineData(HistoryOrder.OldestFirst)]
    public void IteratorKeepsCapturedEndAcrossCommitsAndForksAndAllowsReadsInLoop(HistoryOrder order) {
        using Repository repository = Create();
        WorkingCopy branch = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("main", new Node { Value = 1 }, parameters: NoRebase).Id);
        CommitId e0 = branch.BaseCommitId;
        branch.CommitState(new Node { Value = 8 }, parameters: NoRebase);
        CommitId e1 = branch.CommitEvent(new Node { Value = 2 }, parameters: NoRebase).Id;
        CommitId e2 = branch.CommitEvent(new Node { Value = 3 }, parameters: NoRebase).Id;
        IEnumerable<CommitId> query = repository.EnumerateEvents(e2, order).Select(commit => commit.Id);
        branch.CommitEvent(new Node { Value = 4 }, parameters: NoRebase);
        using IEnumerator<CommitId> iterator = query.GetEnumerator();
        List<CommitId> addresses = [];
        Assert.True(iterator.MoveNext());
        addresses.Add(iterator.Current);
        Assert.IsType<Node>(repository.LoadEvent(iterator.Current));
        CommitId latest = branch.CommitEvent(new Node { Value = 5 }, parameters: NoRebase).Id;
        branch.Dispose();
        BranchInfo fork = repository.ForkBranch(e0, "old-point");
        Assert.Equal(e0, repository.ReadCurrentCommit(fork.Id).Commit.Id);
        while (iterator.MoveNext()) {
            addresses.Add(iterator.Current);
            Assert.IsType<Node>(repository.LoadEvent(iterator.Current));
        }
        CommitId[] expected = order == HistoryOrder.NewestFirst ? [e2, e1, e0] : [e0, e1, e2];
        Assert.Equal(expected, addresses);
        // A second enumerator uses the same selected end even after creating a fork.
        Assert.Equal(expected, query);
        Assert.Equal(latest, repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id);
        foreach (CommitId address in query) { Assert.IsType<Node>(repository.LoadEvent(address)); }
    }

    [Theory]
    [InlineData(HistoryOrder.NewestFirst, false, 0)]
    [InlineData(HistoryOrder.NewestFirst, false, 1)]
    [InlineData(HistoryOrder.NewestFirst, false, 2)]
    [InlineData(HistoryOrder.NewestFirst, false, 3)]
    [InlineData(HistoryOrder.OldestFirst, false, 0)]
    [InlineData(HistoryOrder.OldestFirst, false, 1)]
    [InlineData(HistoryOrder.OldestFirst, false, 2)]
    [InlineData(HistoryOrder.OldestFirst, false, 3)]
    [InlineData(HistoryOrder.NewestFirst, true, 0)]
    [InlineData(HistoryOrder.NewestFirst, true, 1)]
    [InlineData(HistoryOrder.NewestFirst, true, 2)]
    [InlineData(HistoryOrder.NewestFirst, true, 3)]
    [InlineData(HistoryOrder.OldestFirst, true, 0)]
    [InlineData(HistoryOrder.OldestFirst, true, 1)]
    [InlineData(HistoryOrder.OldestFirst, true, 2)]
    [InlineData(HistoryOrder.OldestFirst, true, 3)]
    public void EveryMoveNextChecksRepositoryEvenBeforeStartOrAfterExhaustion(HistoryOrder order, bool fault, int iteratorState) {
        using Repository repository = Create();
        using WorkingCopy branch = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("main", new Node { Value = 1 }, parameters: NoRebase).Id);
        CommitId first = branch.BaseCommitId;
        CommitId end = branch.CommitEvent(new Node { Value = 2 }, parameters: NoRebase).Id;
        using IEnumerator<CommitInfo> iterator = repository.EnumerateEvents(end, order,
            afterExclusive: iteratorState == 3 ? end : null).GetEnumerator();
        if (iteratorState == 1) { Assert.True(iterator.MoveNext()); }
        if (iteratorState == 2) {
            Assert.True(iterator.MoveNext());
            Assert.True(iterator.MoveNext());
            Assert.False(iterator.MoveNext());
        }
        if (iteratorState == 3) { Assert.False(iterator.MoveNext()); }
        if (fault) {
            repository.SaveStageObserver = point => {
                if (point == RepositorySaveStage.AfterBusinessAppend) { throw new IOException("Injected publication failure."); }
            };
            Assert.Throws<RepositoryPublicationException>(() => branch.CommitEvent(new Node(), parameters: NoRebase).Id);
            Assert.True(repository.IsFaulted);
            Assert.Throws<InvalidOperationException>(() => iterator.MoveNext());
            Assert.Throws<InvalidOperationException>(() => iterator.MoveNext());
            Assert.Throws<InvalidOperationException>(() => repository.ReadCheckpoint(first));
        } else {
            repository.Dispose();
            Assert.Throws<ObjectDisposedException>(() => iterator.MoveNext());
            Assert.Throws<ObjectDisposedException>(() => iterator.MoveNext());
            Assert.Throws<ObjectDisposedException>(() => repository.ReadCheckpoint(first));
        }
    }

    private Repository Create() => Repository.CreateNew(_root, SharedReadModel.Models());
    public void Dispose() => SharedReadModel.DeleteFixture(_root, "durable-db078-query-");
}
