using Atelia.RbfSegmentStore;
using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class DB078EventQueryTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"durable-db078-query-{Guid.NewGuid():N}");
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1000000, 1);

    [Fact]
    public void MetadataQueryWithEmptyModelsCrossesStatesAndIgnoresPhysicalSiblingRecords() {
        using (Repository repository = Create()) {
            CheckpointAddress e0;
            using (BranchCheckout branch = repository.CreateBranchFromEvent("main", new Node { Value = 1 }, NoRebase)) {
                e0 = branch.Head;
                branch.CommitState(new Node { Value = 2 }, NoRebase);
                branch.CommitEvent(new Node { Value = 3 }, NoRebase);
            }
            repository.CreateBranch("sibling", e0);
            using (BranchCheckout sibling = repository.Checkout("sibling")) {
                sibling.CommitEvent(new Node { Value = 99 }, NoRebase);
            }
            using (BranchCheckout main = repository.Checkout("main")) {
                main.CommitState(NoRebase);
                main.CommitEvent(new Node { Value = 4 }, NoRebase);
                main.CommitState(NoRebase);
            }
        }
        using Repository empty = Repository.OpenReadOnlyExisting(_root, new StateModelRegistry());
        var frames = empty.ReadFrames("main");
        CheckpointAddress[] expected = frames.Where(frame => frame.Kind == GraphFrameKind.Event).ToArray();
        Assert.Equal(3, expected.Length);
        Assert.Equal(expected.Reverse(), empty.EnumerateEvents(empty.GetHead("main")));
        Assert.Equal(expected, empty.EnumerateEvents(empty.GetHead("main"), HistoryOrder.OldestFirst));
        Assert.Equal(new[] { expected[2], expected[1] }, empty.EnumerateEvents(frames[^1], afterExclusive: frames[1]));
        Assert.Equal(new[] { expected[1] }, empty.EnumerateEvents(expected[1], afterExclusive: expected[0]));
        Assert.Empty(empty.EnumerateEvents(frames[^1], afterExclusive: frames[^1]));
        Assert.Empty(empty.EnumerateEvents(frames[1], afterExclusive: expected[0]));
        Assert.Equal(expected[2], empty.EnumerateEvents(frames[^1]).First());
    }

    [Theory]
    [InlineData(HistoryOrder.NewestFirst)]
    [InlineData(HistoryOrder.OldestFirst)]
    public void InvalidBoundsFailBeforeAnyItemCanEscape(HistoryOrder order) {
        using Repository repository = Create();
        CheckpointAddress initial, end, sibling;
        using (BranchCheckout main = repository.CreateBranchFromEvent("main", new Node(), NoRebase)) {
            initial = main.Head;
            end = main.CommitEvent(new Node(), NoRebase);
        }
        repository.CreateBranch("sibling", initial);
        using (BranchCheckout other = repository.Checkout("sibling")) {
            sibling = other.CommitEvent(new Node(), NoRebase);
        }
        foreach ((CheckpointAddress selected, CheckpointAddress lower) in new[] { (end, sibling), (initial, end) }) {
            int delivered = 0;
            Assert.Throws<ArgumentException>(() => {
                foreach (CheckpointAddress _ in repository.EnumerateEvents(selected, order, lower)) { delivered++; }
            });
            Assert.Equal(0, delivered);
        }
        Assert.Equal(new[] { end, initial }, repository.EnumerateEvents(end));
    }

    [Fact]
    public void InvalidNullForeignAndReopenedAddressesAreRejectedWithoutMaterialization() {
        int allocations = 0;
        CheckpointAddress stale;
        using (Repository repository = Create()) {
            using BranchCheckout branch = repository.CreateBranchFromEvent("main", new Node(), NoRebase);
            stale = branch.Head;
        }
        using Repository reopened = Repository.OpenExisting(_root, SharedReadModel.Models(allocate: () => { allocations++; return new Node(); }));
        using Repository foreign = Repository.CreateNew(Path.Combine(_root, "foreign"), SharedReadModel.Models(),
            new() { NewStoreLayout = RbfSegmentStoreLayout.Flat });
        using BranchCheckout other = foreign.CreateBranchFromEvent("main", new Node(), NoRebase);
        CheckpointAddress valid = reopened.GetHead("main");
        foreach (CheckpointAddress bad in new[] { stale, other.Head }) {
            Assert.Throws<ArgumentException>(() => reopened.ReadCheckpoint(bad));
            Assert.Throws<ArgumentException>(() => reopened.EnumerateEvents(bad).ToArray());
            Assert.Throws<ArgumentException>(() => reopened.EnumerateEvents(valid, afterExclusive: bad).ToArray());
        }
        Assert.Throws<ArgumentNullException>(() => reopened.ReadCheckpoint(null!));
        Assert.Throws<ArgumentNullException>(() => reopened.EnumerateEvents(null!).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => reopened.EnumerateEvents(valid, (HistoryOrder)37).ToArray());
        Assert.Equal(0, allocations);
        Assert.False(reopened.IsFaulted);
        Assert.Single(reopened.EnumerateEvents(valid));
    }

    [Theory]
    [InlineData(HistoryOrder.NewestFirst)]
    [InlineData(HistoryOrder.OldestFirst)]
    public void IteratorKeepsCapturedEndAcrossCommitsAndRefMovesAndAllowsReadsInLoop(HistoryOrder order) {
        using Repository repository = Create();
        BranchCheckout branch = repository.CreateBranchFromEvent("main", new Node { Value = 1 }, NoRebase);
        CheckpointAddress e0 = branch.Head;
        branch.CommitState(new Node { Value = 8 }, NoRebase);
        CheckpointAddress e1 = branch.CommitEvent(new Node { Value = 2 }, NoRebase);
        CheckpointAddress e2 = branch.CommitEvent(new Node { Value = 3 }, NoRebase);
        IEnumerable<CheckpointAddress> query = repository.EnumerateEvents(e2, order);
        branch.CommitEvent(new Node { Value = 4 }, NoRebase);
        using IEnumerator<CheckpointAddress> iterator = query.GetEnumerator();
        List<CheckpointAddress> addresses = [];
        Assert.True(iterator.MoveNext());
        addresses.Add(iterator.Current);
        Assert.IsType<Node>(repository.ReadEvent(iterator.Current));
        CheckpointAddress latest = branch.CommitEvent(new Node { Value = 5 }, NoRebase);
        branch.Dispose();
        repository.MoveBranch("main", latest, e0);
        while (iterator.MoveNext()) {
            addresses.Add(iterator.Current);
            Assert.IsType<Node>(repository.ReadEvent(iterator.Current));
        }
        CheckpointAddress[] expected = order == HistoryOrder.NewestFirst ? [e2, e1, e0] : [e0, e1, e2];
        Assert.Equal(expected, addresses);
        // A second enumerator uses the same selected end even after the branch moved.
        Assert.Equal(expected, query);
        Assert.Equal(e0, repository.GetHead("main"));
        foreach (CheckpointAddress address in query) { Assert.IsType<Node>(repository.ReadEvent(address)); }
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
        using BranchCheckout branch = repository.CreateBranchFromEvent("main", new Node { Value = 1 }, NoRebase);
        CheckpointAddress first = branch.Head;
        CheckpointAddress end = branch.CommitEvent(new Node { Value = 2 }, NoRebase);
        using IEnumerator<CheckpointAddress> iterator = repository.EnumerateEvents(end, order,
            afterExclusive: iteratorState == 3 ? end : null).GetEnumerator();
        if (iteratorState == 1) { Assert.True(iterator.MoveNext()); }
        if (iteratorState == 2) {
            Assert.True(iterator.MoveNext());
            Assert.True(iterator.MoveNext());
            Assert.False(iterator.MoveNext());
        }
        if (iteratorState == 3) { Assert.False(iterator.MoveNext()); }
        if (fault) {
            repository.Checkpoint = point => {
                if (point == CommitCheckpoint.AfterStateDurable) { throw new IOException("Injected publication failure."); }
            };
            Assert.Throws<GraphCommitException>(() => branch.CommitEvent(new Node(), NoRebase));
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

    private Repository Create() => Repository.CreateNew(_root, SharedReadModel.Models(),
        new() { NewStoreLayout = RbfSegmentStoreLayout.Flat });
    public void Dispose() => SharedReadModel.DeleteFixture(_root, "durable-db078-query-");
}
