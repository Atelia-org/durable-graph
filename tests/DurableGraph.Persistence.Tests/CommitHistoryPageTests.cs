using System.Buffers;
using System.Runtime.InteropServices;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class CommitHistoryPageTests : IDisposable {
    private const string Prefix = "dg-commit-history-page-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly DurableSchema ProbeSchema = new("HistoryPageRoot", 1, new DurableFieldInfo(1, TypeTag.Int32));
    private static HistoryPageLimits Limits(int items) => new(items, long.MaxValue);

    [Fact]
    public void Fixed_anchor_and_continuations_do_not_drift_when_new_commits_are_appended() {
        using Repository repository = Repository.CreateStorageNew(_path, Models());
        BranchId branch = repository.CreateRootBranchFromState("main", new DB087Widget { Value = 1 }).Id;
        CommitId seed = repository.ReadCurrentCommit(branch).Commit.Id;
        using WorkingCopy work = repository.OpenWorkingCopy(branch);
        CommitId domainEvent = work.CommitEvent(new DB087Widget { Value = 2 }).Id;
        CommitId sameValueFirst = work.CommitState().Id;
        CommitId anchor = work.CommitState().Id;
        Assert.NotEqual(sameValueFirst, anchor); // Equal content still denotes distinct saves.
        CommitHistoryPage first = repository.ReadHistoryPage(HistoryStart.AtOrBefore(anchor), Limits(2));
        Assert.Equal(new[] { anchor, sameValueFirst }, first.Items.Select(item => item.Id));
        Assert.Equal(HistoryPageStopReason.PageFull, first.StopReason);
        AssertContinuation(first, sameValueFirst);
        ((DB087Widget)work.State!).Value = 9;
        CommitId newer = work.CommitState().Id;
        work.CommitEvent(new DB087Widget { Value = 10 });
        repository.CreateRootBranchFromState("other", new DB087Widget());
        CommitHistoryPage second = repository.ReadHistoryPage(first.Continuation!.Value, Limits(3));
        Assert.Equal(new[] { domainEvent, seed }, second.Items.Select(item => item.Id));
        Assert.Equal(HistoryPageStopReason.ReachedRoot, second.StopReason);
        Assert.Null(second.Continuation);
        Assert.Equal(new[] { anchor, sameValueFirst }, first.Items.Select(item => item.Id));
        CommitInfo[] repeated = Collect(repository, HistoryStart.AtOrBefore(anchor), 2);
        Assert.Equal(new[] { anchor, sameValueFirst, domainEvent, seed }, repeated.Select(item => item.Id));
        Assert.Equal(new[] { CommitKind.State, CommitKind.State, CommitKind.Event, CommitKind.State }, repeated.Select(item => item.Kind));
        Assert.DoesNotContain(repeated, item => item.Id == newer);
    }

    [Fact]
    public void Cold_pages_cross_actual_fork_sources_without_clone_commits_or_later_parent_history() {
        BranchId a, b, c, empty;
        CommitId a0, a1, b1, b2, c1;
        using (Repository repository = Repository.CreateStorageNew(_path, Models())) {
            a = repository.CreateRootBranchFromState("A", new DB087Widget { Value = 1 }).Id;
            a0 = repository.ReadCurrentCommit(a).Commit.Id;
            using (WorkingCopy work = repository.OpenWorkingCopy(a)) {
                ((DB087Widget)work.State!).Value = 11;
                a1 = work.CommitState().Id;
            }
            b = repository.ForkBranch(a0, "B from old A").Id;
            Assert.Equal(a0, repository.ReadCurrentCommit(b).Commit.Id);
            using (WorkingCopy work = repository.OpenWorkingCopy(b)) { b1 = work.CommitEvent(new DB087Widget { Value = 2 }).Id; }
            c = repository.ForkBranch(b1, "C").Id;
            Assert.Equal(b1, repository.ReadCurrentCommit(c).Commit.Id);
            Assert.Equal(new[] { b1, a0 }, Collect(repository, HistoryStart.AtOrBefore(b1), 1).Select(item => item.Id));
            using (WorkingCopy work = repository.OpenWorkingCopy(c)) {
                ((DB087Widget)work.State!).Value = 3;
                c1 = work.CommitState().Id;
            }
            empty = repository.ForkBranch(c1, "empty D").Id;
            using (WorkingCopy work = repository.OpenWorkingCopy(b)) { b2 = work.CommitEvent(new DB087Widget { Value = 22 }).Id; }
        }
        Dictionary<string, byte[]> before = SnapshotFiles();
        using (Repository reopened = Repository.OpenStorageReadOnlyExisting(_path, new StateModelRegistry())) {
            CommitId selected = reopened.ReadCurrentCommit(empty).Commit.Id;
            Assert.Equal(c1, selected);
            CommitInfo[] history = Collect(reopened, HistoryStart.AtOrBefore(selected), 1);
            Assert.Equal(new[] { c1, b1, a0 }, history.Select(item => item.Id));
            Assert.Equal(new[] { c, b, a }, history.Select(item => item.CreatedOnBranchId));
            Assert.Equal(new[] { CommitKind.State, CommitKind.Event, CommitKind.State }, history.Select(item => item.Kind));
            Assert.DoesNotContain(history, item => item.Id == a1 || item.Id == b2 || item.CreatedOnBranchId == empty);
            Assert.Equal(new[] { a0 }, Collect(reopened, HistoryStart.Before(b1), 2).Select(item => item.Id));
            Assert.Equal(0, reopened.GraphStore.AppendedFrames);
        }
        AssertFilesEqual(before, SnapshotFiles());
    }

    [Fact]
    public void Before_excludes_anchor_and_full_page_at_root_is_followed_by_an_empty_terminal_page() {
        using Repository repository = Repository.CreateStorageNew(_path, Models());
        BranchId branch = repository.CreateRootBranchFromState("root", new DB087Widget()).Id;
        CommitId root = repository.ReadCurrentCommit(branch).Commit.Id;
        CommitHistoryPage inclusive = repository.ReadHistoryPage(HistoryStart.AtOrBefore(root), Limits(2));
        Assert.Equal(root, Assert.Single(inclusive.Items).Id);
        Assert.Equal(HistoryPageStopReason.ReachedRoot, inclusive.StopReason);
        Assert.Null(inclusive.Continuation);
        CommitHistoryPage full = repository.ReadHistoryPage(HistoryStart.AtOrBefore(root), Limits(1));
        Assert.Equal(root, Assert.Single(full.Items).Id);
        Assert.Equal(HistoryPageStopReason.PageFull, full.StopReason);
        AssertContinuation(full, root);
        CommitHistoryPage empty = repository.ReadHistoryPage(full.Continuation!.Value, Limits(1));
        Assert.Empty(empty.Items);
        Assert.Equal(HistoryPageStopReason.ReachedRoot, empty.StopReason);
        Assert.Null(empty.Continuation);
        CommitHistoryPage explicitBefore = repository.ReadHistoryPage(HistoryStart.Before(root), Limits(10));
        Assert.Empty(explicitBefore.Items);
        Assert.Equal(HistoryPageStopReason.ReachedRoot, explicitBefore.StopReason);
        Assert.Null(explicitBefore.Continuation);
        HistoryPageLimits largest = new(int.MaxValue, long.MaxValue);
        CommitHistoryPage small = repository.ReadHistoryPage(HistoryStart.AtOrBefore(root), largest);
        Assert.Equal(root, Assert.Single(small.Items).Id);
        Assert.Equal(HistoryPageStopReason.ReachedRoot, small.StopReason);
        Assert.Null(small.Continuation);
        CommitHistoryPage stillEmpty = repository.ReadHistoryPage(HistoryStart.Before(root), largest);
        Assert.Empty(stillEmpty.Items);
        Assert.Equal(HistoryPageStopReason.ReachedRoot, stillEmpty.StopReason);
        Assert.Null(stillEmpty.Continuation);
    }

    [Fact]
    public void Application_state_filter_can_display_nothing_while_event_page_continuation_still_advances() {
        using Repository repository = Repository.CreateStorageNew(_path, Models());
        BranchId branch = repository.CreateRootBranchFromState("main", new DB087Widget()).Id;
        CommitId state = repository.ReadCurrentCommit(branch).Commit.Id;
        using WorkingCopy work = repository.OpenWorkingCopy(branch);
        CommitId e1 = work.CommitEvent(new DB087Widget { Value = 1 }).Id;
        CommitId e2 = work.CommitEvent(new DB087Widget { Value = 2 }).Id;
        CommitId e3 = work.CommitEvent(new DB087Widget { Value = 3 }).Id;
        CommitHistoryPage first = repository.ReadHistoryPage(HistoryStart.AtOrBefore(e3), Limits(2));
        Assert.Equal(new[] { e3, e2 }, first.Items.Select(item => item.Id));
        CommitInfo[] displayedStates = first.Items.Where(item => item.Kind == CommitKind.State).ToArray();
        Assert.Empty(displayedStates);
        AssertContinuation(first, e2);
        CommitHistoryPage second = repository.ReadHistoryPage(first.Continuation!.Value, Limits(3));
        Assert.Equal(new[] { e1, state }, second.Items.Select(item => item.Id));
        Assert.Equal(state, Assert.Single(second.Items, item => item.Kind == CommitKind.State).Id);
        Assert.Equal(HistoryPageStopReason.ReachedRoot, second.StopReason);
        Assert.Null(second.Continuation);
    }

    [Fact]
    public void Joint_publication_keeps_each_branches_history_local_to_its_own_commit_chain() {
        BranchId first, second;
        CommitId firstSeed, secondSeed, firstCommit, secondCommit;
        using (Repository repository = Repository.CreateStorageNew(_path, Models())) {
            first = repository.CreateRootBranchFromState("first", new DB087Widget { Value = 1 }).Id;
            second = repository.CreateRootBranchFromState("second", new DB087Widget { Value = 2 }).Id;
            using WorkingCopy left = repository.OpenWorkingCopy(first);
            using WorkingCopy right = repository.OpenWorkingCopy(second);
            firstSeed = left.BaseCommitId;
            secondSeed = right.BaseCommitId;
            var commits = repository.CommitBranches([
                left.CreateStateCommitRequest(), right.CreateEventCommitRequest(new DB087Widget { Value = 3 })
            ]);
            firstCommit = commits[first].Id;
            secondCommit = commits[second].Id;
        }
        using Repository reopened = Repository.OpenStorageReadOnlyExisting(_path, new StateModelRegistry());
        CommitInfo[] firstHistory = Collect(reopened, HistoryStart.AtOrBefore(firstCommit), 1);
        CommitInfo[] secondHistory = Collect(reopened, HistoryStart.AtOrBefore(secondCommit), 1);
        Assert.Equal(new[] { firstCommit, firstSeed }, firstHistory.Select(item => item.Id));
        Assert.Equal(new[] { secondCommit, secondSeed }, secondHistory.Select(item => item.Id));
        Assert.All(firstHistory, item => Assert.Equal(first, item.CreatedOnBranchId));
        Assert.All(secondHistory, item => Assert.Equal(second, item.CreatedOnBranchId));
        Assert.Equal(CommitKind.Event, secondHistory[0].Kind);
    }

    [Fact]
    public void Summaries_are_owned_per_item_and_page_and_remain_readable_after_repository_close() {
        CommitId root, tip;
        using (Repository repository = Repository.CreateStorageNew(_path, Models())) {
            byte[] summary = [4, 5];
            BranchId branch = repository.CreateRootBranchFromState("main", new DB087Widget(), summary).Id;
            root = repository.ReadCurrentCommit(branch).Commit.Id;
            using WorkingCopy work = repository.OpenWorkingCopy(branch);
            tip = work.CommitEvent(new DB087Widget(), summary).Id;
            summary[0] = 99;
        }
        CommitHistoryPage retained;
        using (Repository reopened = Repository.OpenStorageReadOnlyExisting(_path, new StateModelRegistry())) {
            retained = reopened.ReadHistoryPage(HistoryStart.AtOrBefore(tip), Limits(8));
            CommitHistoryPage other = reopened.ReadHistoryPage(HistoryStart.AtOrBefore(tip), Limits(8));
            Assert.Equal(new[] { tip, root }, retained.Items.Select(item => item.Id));
            Assert.All(retained.Items, item => Assert.Equal(new byte[] { 4, 5 }, item.Summary.ToArray()));
            Assert.True(MemoryMarshal.TryGetArray(retained.Items[0].Summary, out ArraySegment<byte> memory));
            memory.Array![memory.Offset] = 88;
            Assert.Equal(new byte[] { 4, 5 }, retained.Items[1].Summary.ToArray());
            Assert.All(other.Items, item => Assert.Equal(new byte[] { 4, 5 }, item.Summary.ToArray()));
            Assert.Equal(new byte[] { 4, 5 }, reopened.ReadCommitInfo(tip).Summary.ToArray());
        }
        Assert.Equal(tip, retained.Items[0].Id);
        Assert.Equal(new byte[] { 88, 5 }, retained.Items[0].Summary.ToArray());
        Assert.Equal(new byte[] { 4, 5 }, retained.Items[1].Summary.ToArray());
    }

    [Fact]
    public void Paging_never_captures_allocates_or_hydrates_business_models() {
        Counts counts = new();
        CommitId root, tip;
        using (Repository repository = Repository.CreateStorageNew(_path, Models(counts))) {
            BranchId branch = repository.CreateRootBranchFromState("main", new ProbeRoot { Value = 1 }).Id;
            root = repository.ReadCurrentCommit(branch).Commit.Id;
            using WorkingCopy work = repository.OpenWorkingCopy(branch);
            tip = work.CommitEvent(new ProbeRoot { Value = 2 }).Id;
        }
        counts.Captures = counts.Allocations = counts.Hydrations = 0;
        counts.Forbidden = true;
        using Repository reopened = Repository.OpenStorageReadOnlyExisting(_path, Models(counts));
        Assert.Equal(new[] { tip, root }, Collect(reopened, HistoryStart.AtOrBefore(tip), 1).Select(item => item.Id));
        Assert.Equal(0, counts.Captures);
        Assert.Equal(0, counts.Allocations);
        Assert.Equal(0, counts.Hydrations);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Default_commit_boundaries_fail_at_the_read_call(int variant) {
        using Repository repository = Repository.CreateStorageNew(_path, Models());
        HistoryStart start = variant switch {
            0 => default,
            1 => HistoryStart.AtOrBefore(default),
            _ => HistoryStart.Before(default)
        };
        Assert.Throws<ArgumentException>(() => repository.ReadHistoryPage(start, Limits(1)));
        Assert.False(repository.IsFaulted);
    }

    [Theory]
    [InlineData(0, 100L)]
    [InlineData(-1, 100L)]
    [InlineData(1, 0L)]
    [InlineData(1, -1L)]
    public void Nonpositive_limits_fail_before_scanning_and_leave_the_owner_usable(int maxItems, long maxWork) {
        using Repository repository = Repository.CreateStorageNew(_path, Models());
        BranchId branch = repository.CreateRootBranchFromState("main", new DB087Widget()).Id;
        CommitId root = repository.ReadCurrentCommit(branch).Commit.Id;
        HistoryPageLimits limits = new(maxItems, maxWork);
        Assert.Throws<ArgumentOutOfRangeException>(() => repository.ReadHistoryPage(HistoryStart.AtOrBefore(root), limits));
        Assert.False(repository.IsFaulted);
        Assert.Equal(root, Assert.Single(repository.ReadHistoryPage(HistoryStart.AtOrBefore(root), Limits(2)).Items).Id);
    }

    private static CommitInfo[] Collect(Repository repository, HistoryStart start, int pageSize) {
        List<CommitInfo> result = [];
        for (int pageNumber = 0; pageNumber < 32; pageNumber++) {
            CommitHistoryPage page = repository.ReadHistoryPage(start, Limits(pageSize));
            Assert.InRange(page.Items.Count, 0, pageSize);
            result.AddRange(page.Items);
            if (page.StopReason == HistoryPageStopReason.ReachedRoot) {
                Assert.Null(page.Continuation);
                Assert.Equal(result.Count, result.Select(item => item.Id).Distinct().Count());
                return result.ToArray();
            }
            Assert.Equal(HistoryPageStopReason.PageFull, page.StopReason);
            Assert.Equal(pageSize, page.Items.Count);
            AssertContinuation(page, page.Items[^1].Id);
            start = page.Continuation!.Value;
        }
        throw new InvalidOperationException("Finite fixture pagination failed to reach its root.");
    }

    private static void AssertContinuation(CommitHistoryPage page, CommitId expected) {
        Assert.True(page.Continuation.HasValue);
        Assert.Equal(expected, page.Continuation!.Value.CommitId);
        Assert.False(page.Continuation!.Value.IsInclusive);
    }

    private static StateModelRegistry Models(Counts? counts = null) {
        StateModelRegistry models = new();
        models.Register(global::Atelia.DurableGraph.Generated.Family_5030576964676574.Definition);
        if (counts is not null) {
            models.Register(new StateModelBinding<ProbeRoot, int>(new(ProbeSchema, BaseInt,
                static (in int prior, in int current) => new(prior != current, BaseInt(in current).Body)),
                [new StateReaderBinding<int>(ProbeSchema, static (ref BareValueReader reader) => reader.ReadVarInt32(),
                    static (ref BareValueReader reader, in int _) => reader.ReadVarInt32(), static (in int _, IStateReferenceVisitor _) => { })],
                static row => row.GetState<int>(), () => { counts.Allocations++; counts.Check(); return new(); },
                (ProbeRoot root, in int state, ObjectReadTable _) => { counts.Hydrations++; counts.Check(); root.Value = state; },
                (root, _) => { counts.Captures++; counts.Check(); return root.Value; }, static (in int _, IStateReferenceVisitor _) => { }));
        }
        return models;
    }
    private static PreparedBaseBody BaseInt(in int value) {
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        writer.WriteVarInt32(value);
        return new(bytes.WrittenSpan);
    }
    private sealed class ProbeRoot : IDurableObject { internal int Value; }
    private sealed class Counts {
        internal int Captures, Allocations, Hydrations;
        internal bool Forbidden;
        internal void Check() { if (Forbidden) { throw new InvalidOperationException("History descriptions must not materialize business objects."); } }
    }
    private Dictionary<string, byte[]> SnapshotFiles() => Directory.GetFiles(_path, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(_path, path), File.ReadAllBytes);
    private static void AssertFilesEqual(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual) {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach ((string path, byte[] bytes) in expected) { Assert.Equal(bytes, actual[path]); }
    }
    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
