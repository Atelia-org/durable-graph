using System.Buffers;
using System.Diagnostics;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Xunit.Abstractions;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class EventHistoryAdmissionTests(ITestOutputHelper output) : IDisposable {
    private const string Prefix = "dg-event-history-admission-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly DurableSchema RootSchema = new("EventHistoryAdmission.Root", 1, new DurableFieldInfo(1, TypeTag.Int32));

    public static IEnumerable<object[]> CostCases() {
        foreach (bool startsWithState in new[] { false, true }) {
            foreach (int count in new[] { 32, 64, 128 }) {
                yield return [count, startsWithState, 0, GraphRecordStore.DefaultReadCacheBudgetBytes];
            }
            // At N=64 complete the cache/summary factorial without repeating every long run.
            yield return [64, startsWithState, 0, 0L];
            yield return [64, startsWithState, 1024, 0L];
            yield return [64, startsWithState, 1024, GraphRecordStore.DefaultReadCacheBudgetBytes];
        }
    }

    [Theory]
    [MemberData(nameof(CostCases))]
    public void LongEventTailAdmissionHasLinearHistoryVisitsSeparatelyFromPhysicalReads(
        int count, bool startsWithState, int summaryBytes, long budget) {
        byte[] summary = Enumerable.Repeat((byte)0x51, summaryBytes).ToArray();
        using Repository repository = Repository.CreateNew(_path, Models(), readCacheBudgetBytes: budget);
        BranchId branch = startsWithState
            ? repository.CreateRootBranchFromState("main", new Root { Value = 10 }, summary).Id
            : repository.CreateRootBranchFromEvent("main", new Root { Value = 10 }, summary).Id;
        using WorkingCopy work = repository.OpenWorkingCopy(branch);
        CommitId initial = work.BaseCommitId;
        CommitId? originalState = work.StateSourceCommitId;
        IDurableObject? stateInstance = work.State;
        RepositoryResources resources = DB096TestAccess.Resources(repository);
        GraphRecordStore store = repository.GraphStore;
        long visitsBefore = resources.HistoryScanVisitedEntries;
        GraphPhysicalReadStatistics physicalBefore = store.ReadCacheStatistics;
        Root domainEvent = new();
        long firstVisits = 0, finalVisits = 0;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        for (int index = 1; index <= count; index++) {
            domainEvent.Value = index;
            long previous = resources.HistoryScanVisitedEntries;
            work.CommitEvent(domainEvent, summary);
            long visits = resources.HistoryScanVisitedEntries - previous;
            if (index == 1) { firstVisits = visits; }
            if (index == count) { finalVisits = visits; }
        }
        long elapsed = Stopwatch.GetTimestamp() - started;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        long totalVisits = resources.HistoryScanVisitedEntries - visitsBefore;
        GraphPhysicalReadStatistics physicalAfter = store.ReadCacheStatistics;
        output.WriteLine($"N={count}, initial={(startsWithState ? "State" : "Event")}, summary={summaryBytes}, budget={budget}: " +
            $"historyVisits={totalVisits}, first={firstVisits}, final={finalVisits}, " +
            $"graphHits={physicalAfter.GraphHits - physicalBefore.GraphHits}, graphMisses={physicalAfter.GraphMisses - physicalBefore.GraphMisses}, " +
            $"graphDecodes={physicalAfter.GraphDecodes - physicalBefore.GraphDecodes}, " +
            $"allocated={allocated}, elapsedMs={elapsed * 1000.0 / Stopwatch.Frequency:F3}, " +
            $"unoptimizedThreeScanFormula={3L * count * (count + 1) / 2}");
        // Count callbacks, not time/I/O. Leave room for constant startup work and
        // several admission checks per save while rejecting a growing tail rescan.
        Assert.True(totalVisits <= 4L * count + 3, $"History visits grew with the Event tail: {totalVisits} for {count} commits.");
        if (budget == 0) { Assert.Equal(physicalBefore.GraphHits, physicalAfter.GraphHits); }

        // Keep navigation and graph restoration outside the measured interval.
        Assert.NotEqual(initial, work.BaseCommitId);
        Assert.Equal(originalState, work.StateSourceCommitId);
        Assert.Same(stateInstance, work.State);
        Assert.Equal(startsWithState ? initial : (CommitId?)null, work.StateSourceCommitId);
        Assert.Equal(count + 1, repository.ReadFrames(branch).Count);
        Assert.Equal(count, Assert.IsType<Root>(repository.LoadEvent(work.BaseCommitId)).Value);
        Assert.False(repository.IsFaulted);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(GraphRecordStore.DefaultReadCacheBudgetBytes)]
    public void StateOnlyControlHasNoGrowingEventTail(long budget) {
        const int count = 64;
        using Repository repository = Repository.CreateNew(_path, Models(), readCacheBudgetBytes: budget);
        BranchId branch = repository.CreateRootBranchFromState("main", new Root { Value = 10 }).Id;
        using WorkingCopy work = repository.OpenWorkingCopy(branch);
        Root state = Assert.IsType<Root>(work.State);
        RepositoryResources resources = DB096TestAccess.Resources(repository);
        long before = resources.HistoryScanVisitedEntries;
        for (int index = 1; index <= count; index++) {
            state.Value = index;
            work.CommitState();
        }
        long visits = resources.HistoryScanVisitedEntries - before;
        output.WriteLine($"State-only control N={count}, budget={budget}, historyVisits={visits}");
        Assert.True(visits <= 4L * count + 3, $"State-only admission did too much history work: {visits}.");
        Assert.Equal(work.BaseCommitId, work.StateSourceCommitId);
        Assert.Equal(count, Assert.IsType<Root>(repository.LoadState(work.BaseCommitId)).Value);
    }

    [Fact]
    public void InstallingStateResetsTheSourceBeforeAnotherLongEventTail() {
        const int count = 32;
        using Repository repository = Repository.CreateNew(_path, Models());
        BranchId branch = repository.CreateRootBranchFromState("main", new Root { Value = 10 }).Id;
        using WorkingCopy work = repository.OpenWorkingCopy(branch);
        CommitId firstState = work.BaseCommitId;
        RepositoryResources resources = DB096TestAccess.Resources(repository);
        long before = resources.HistoryScanVisitedEntries;
        for (int index = 0; index < count; index++) { work.CommitEvent(new Root { Value = 100 + index }); }
        long firstTailVisits = resources.HistoryScanVisitedEntries - before;
        Assert.Equal(firstState, work.StateSourceCommitId);
        before = resources.HistoryScanVisitedEntries;
        Root next = new() { Value = 300 };
        CommitInfo secondState = work.CommitState(next);
        long stateInstallVisits = resources.HistoryScanVisitedEntries - before;
        before = resources.HistoryScanVisitedEntries;
        for (int index = 0; index < count; index++) { work.CommitEvent(new Root { Value = 400 + index }); }
        long secondTailVisits = resources.HistoryScanVisitedEntries - before;
        output.WriteLine($"S0/{count}E/S1/{count}E: firstTail={firstTailVisits}, S1={stateInstallVisits}, " +
            $"secondTail={secondTailVisits}, oldTailFormula={3L * count * (count + 1) / 2}");
        Assert.Equal(secondState.Id, work.StateSourceCommitId);
        Assert.Same(next, work.State);
        Assert.Equal(300, Assert.IsType<Root>(repository.LoadLatestSavedState(work.BaseCommitId).State).Value);
        Assert.Equal(10, Assert.IsType<Root>(repository.LoadState(firstState)).Value);
        Assert.True(firstTailVisits <= 4L * count + 3, $"First tail visits: {firstTailVisits}.");
        Assert.True(stateInstallVisits <= 4, $"Installing State rescanned the previous tail: {stateInstallVisits}.");
        Assert.True(secondTailVisits <= 4L * count + 3, $"Second tail visits: {secondTailVisits}.");
    }

    [Fact]
    public void ReadingAnOldStateSourceDoesNotEvictTheCurrentTipsAdmissionCursor() {
        const int count = 32;
        using Repository repository = Repository.CreateNew(_path, Models());
        BranchId branch = repository.CreateRootBranchFromState("main", new Root { Value = 10 }).Id;
        using WorkingCopy work = repository.OpenWorkingCopy(branch);
        CommitId firstState = work.BaseCommitId;
        CommitId oldEvent = default;
        for (int index = 1; index <= count; index++) {
            CommitId saved = work.CommitEvent(new Root { Value = index }).Id;
            if (index == count / 2) { oldEvent = saved; }
        }
        // Bring admission to the exact current tip before the historical query.
        CommitRequest unused = work.CreateEventCommitRequest(new Root { Value = 90 });
        Assert.Equal(work.BaseCommitId, unused.ExpectedBaseCommitId);
        RepositoryResources resources = DB096TestAccess.Resources(repository);
        long before = resources.HistoryScanVisitedEntries;
        LoadedState old = repository.LoadLatestSavedState(oldEvent);
        long historicalVisits = resources.HistoryScanVisitedEntries - before;
        before = resources.HistoryScanVisitedEntries;
        CommitInfo hot = work.CommitEvent(new Root { Value = 99 });
        long hotVisits = resources.HistoryScanVisitedEntries - before;
        output.WriteLine($"Old E{count / 2} query after E{count}: historicalVisits={historicalVisits}, nextHotCommitVisits={hotVisits}");
        Assert.Equal(firstState, old.StateCommitId);
        Assert.Equal(10, Assert.IsType<Root>(old.State).Value);
        Assert.Equal(firstState, work.StateSourceCommitId);
        Assert.Equal(hot.Id, work.BaseCommitId);
        Assert.True(hotVisits <= 4, $"The old query caused the next hot commit to rescan history: {hotVisits} visits.");
        Assert.Equal(99, Assert.IsType<Root>(repository.LoadEvent(hot.Id)).Value);
        Assert.False(repository.IsFaulted);
    }

    [Fact]
    public void EventOnlyHistoryCanInstallItsFirstStateAfterTheTailWithoutReplayingEvents() {
        using Repository repository = Repository.CreateNew(_path, Models());
        BranchId branch = repository.CreateRootBranchFromEvent("main", new Root { Value = 1 }).Id;
        using WorkingCopy work = repository.OpenWorkingCopy(branch);
        for (int index = 0; index < 8; index++) { work.CommitEvent(new Root { Value = index + 2 }); }
        Assert.Null(work.State);
        Assert.Null(work.StateSourceCommitId);
        Root firstState = new() { Value = 30 };
        CommitInfo saved = work.CommitState(firstState);
        Assert.Same(firstState, work.State);
        Assert.Equal(saved.Id, work.StateSourceCommitId);
        CommitInfo laterEvent = work.CommitEvent(new Root { Value = 40 });
        Assert.Equal(laterEvent.Id, work.BaseCommitId);
        Assert.Equal(saved.Id, work.StateSourceCommitId);
        Assert.Equal(30, Assert.IsType<Root>(work.State).Value);
        LoadedState loaded = repository.LoadLatestSavedState(laterEvent.Id);
        Assert.Equal(saved.Id, loaded.StateCommitId);
        Assert.Equal(30, Assert.IsType<Root>(loaded.State).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForkFromAnEventKeepsTheSelectedPrefixWhenItsParentLaterInstallsState(bool startsWithState) {
        using Repository repository = Repository.CreateNew(_path, Models());
        BranchId parentId = startsWithState
            ? repository.CreateRootBranchFromState("parent", new Root { Value = 10 }).Id
            : repository.CreateRootBranchFromEvent("parent", new Root { Value = 10 }).Id;
        using WorkingCopy parent = repository.OpenWorkingCopy(parentId);
        CommitId? inheritedState = parent.StateSourceCommitId;
        CommitId selected = parent.CommitEvent(new Root { Value = 11 }).Id;
        parent.CommitEvent(new Root { Value = 12 });
        CommitInfo parentNewState = parent.CommitState(new Root { Value = 999 });
        BranchId fork = repository.ForkBranch(selected, "child").Id;
        using WorkingCopy child = repository.OpenWorkingCopy(fork);
        Assert.Equal(selected, child.BaseCommitId);
        Assert.Equal(inheritedState, child.StateSourceCommitId);
        if (startsWithState) { Assert.Equal(10, Assert.IsType<Root>(child.State).Value); }
        else { Assert.Null(child.State); }
        CommitInfo childEvent = child.CommitEvent(new Root { Value = 20 });
        Assert.Equal(inheritedState, child.StateSourceCommitId);
        Assert.Equal(parentNewState.Id, parent.BaseCommitId);
        Assert.DoesNotContain(repository.ReadFrames(fork), item => item.Id == parentNewState.Id);
        Root next = new() { Value = 30 };
        CommitInfo childState = child.CommitState(next);
        Assert.Equal(childState.Id, child.StateSourceCommitId);
        Assert.Same(next, child.State);
        Assert.Equal(parentNewState.Id, repository.ReadCurrentCommit(parentId).Commit.Id);
        Assert.Equal(20, Assert.IsType<Root>(repository.LoadEvent(childEvent.Id)).Value);
        Assert.False(repository.IsFaulted);
    }

    [Fact]
    public void AlternatingBranchesKeepIndependentStateAndNoStateSources() {
        using Repository repository = Repository.CreateNew(_path, Models());
        BranchId stateBranch = repository.CreateRootBranchFromState("state", new Root { Value = 10 }).Id;
        BranchId eventBranch = repository.CreateRootBranchFromEvent("events", new Root { Value = 20 }).Id;
        using WorkingCopy state = repository.OpenWorkingCopy(stateBranch);
        using WorkingCopy events = repository.OpenWorkingCopy(eventBranch);
        CommitId? originalState = state.StateSourceCommitId;
        for (int index = 0; index < 8; index++) {
            state.CommitEvent(new Root { Value = 100 + index });
            events.CommitEvent(new Root { Value = 200 + index });
            Assert.Equal(originalState, state.StateSourceCommitId);
            Assert.Null(events.StateSourceCommitId);
            Assert.Null(events.State);
        }
        CommitInfo firstEventState = events.CommitState(new Root { Value = 30 });
        state.CommitEvent(new Root { Value = 110 });
        Assert.Equal(originalState, state.StateSourceCommitId);
        Assert.Equal(firstEventState.Id, events.StateSourceCommitId);
        Assert.Equal(10, Assert.IsType<Root>(state.State).Value);
        Assert.Equal(30, Assert.IsType<Root>(events.State).Value);
        Assert.Equal(110, Assert.IsType<Root>(repository.LoadEvent(state.BaseCommitId)).Value);
        Assert.Equal(30, Assert.IsType<Root>(repository.LoadState(events.BaseCommitId)).Value);
    }

    private static StateModelRegistry Models() {
        StateModelRegistry models = new();
        models.Register(new StateModelBinding<Root, int>(new(RootSchema, BaseInt,
            static (in int prior, in int current) => new(prior != current, BaseInt(in current).Body)),
            [new StateReaderBinding<int>(RootSchema, static (ref BareValueReader reader) => reader.ReadVarInt32(),
                static (ref BareValueReader reader, in int _) => reader.ReadVarInt32(), static (in int _, IStateReferenceVisitor _) => { })],
            static row => row.GetState<int>(), static () => new(),
            static (Root root, in int value, ObjectReadTable _) => root.Value = value,
            static (root, _) => root.Value, static (in int _, IStateReferenceVisitor _) => { }));
        return models;
    }

    private static PreparedBaseBody BaseInt(in int value) {
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        writer.WriteVarInt32(value);
        return new(buffer.WrittenSpan);
    }
    private sealed class Root : IDurableObject { internal int Value; }

    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
