using System.Buffers;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Xunit.Abstractions;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class CommitRequestReentryTests(ITestOutputHelper output) : IDisposable {
    private const string Prefix = "dg-commit-request-reentry-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly DurableSchema RootSchema = new("CommitRequestReentry.Root", 1, new DurableFieldInfo(1, TypeTag.Int32));

    [Theory]
    [InlineData("state-request", "event")]
    [InlineData("state-request", "work-dispose")]
    [InlineData("state-request", "repository-dispose")]
    [InlineData("event-request", "event")]
    [InlineData("event-request", "work-dispose")]
    [InlineData("event-request", "repository-dispose")]
    [InlineData("state-immediate", "event")]
    [InlineData("state-immediate", "work-dispose")]
    [InlineData("state-immediate", "repository-dispose")]
    [InlineData("event-immediate", "event")]
    [InlineData("event-immediate", "work-dispose")]
    [InlineData("event-immediate", "repository-dispose")]
    public void SummaryProviderCannotReenterOrReleaseTheOriginalWorkingCopy(string entry, string nested) {
        int captures = 0;
        using Repository repository = Repository.CreateNew(_path, Models());
        BranchId branch = repository.CreateRootBranchFromState("main", new Root { Value = 10 }).Id;
        using WorkingCopy work = repository.OpenWorkingCopy(branch);
        Root state = Assert.IsType<Root>(work.State);
        state.OnCapture = () => captures++;
        Root domainEvent = new() { Value = 20, OnCapture = () => captures++ };
        CommitId before = work.BaseCommitId;
        CommitId? stateBefore = work.StateSourceCommitId;
        long framesBefore = repository.GraphStore.AppendedFrames;
        using CallbackMemory summaryOwner = new([1, 2, 3]);
        ReadOnlyMemory<byte> summary = summaryOwner.Memory; // Memory itself can call GetSpan; arm afterward.
        bool callbackRan = false, repositoryDisposed = false;
        summaryOwner.Callback = () => {
            callbackRan = true;
            Exception? nestedFailure = Record.Exception(() => {
                switch (nested) {
                    case "event": work.CommitEvent(new Root { Value = 99, OnCapture = () => captures++ }); break;
                    case "work-dispose": work.Dispose(); break;
                    case "repository-dispose": repository.Dispose(); repositoryDisposed = true; break;
                    default: throw new ArgumentOutOfRangeException(nameof(nested));
                }
            });
            // These are local observations, so recording the old behavior does not itself
            // make a normally forbidden Repository query while the operation is busy.
            output.WriteLine($"{entry}/{nested}: nestedFailure={nestedFailure?.GetType().Name ?? "none"}, " +
                $"baseChanged={work.BaseCommitId != before}, live={work.IsLiveFor(repository)}, " +
                $"repositoryDisposed={repositoryDisposed}, captures={captures}");
            Assert.IsType<InvalidOperationException>(nestedFailure);
            // A rejected nested call must not clear the outer operation's guard.
            Assert.Throws<InvalidOperationException>(work.Dispose);
            Assert.Throws<InvalidOperationException>(() => repository.ListBranches());
            Assert.Equal(before, work.BaseCommitId);
            Assert.True(work.IsLiveFor(repository));
        };

        CommitRequest? request = null;
        CommitInfo? committed = Invoke(entry, work, domainEvent, summary, out request);
        Assert.True(callbackRan);
        Assert.Null(summaryOwner.Callback);
        summaryOwner.Bytes[0] = 99;
        bool deferred = entry.EndsWith("request", StringComparison.Ordinal);
        if (deferred) {
            Assert.NotNull(request);
            Assert.Null(committed);
            Assert.Equal(before, request.ExpectedBaseCommitId);
            Assert.Equal(before, work.BaseCommitId);
            Assert.Equal(framesBefore, repository.GraphStore.AppendedFrames);
            Assert.Equal(0, captures);
            Assert.Single(repository.ReadFrames(branch));
            committed = repository.CommitBranches([request])[branch];
        }
        Assert.NotNull(committed);
        Assert.Equal(new byte[] { 1, 2, 3 }, committed.Summary.ToArray());
        Assert.Equal(1, captures);
        Assert.Equal(2, repository.ReadFrames(branch).Count); // One business commit, not one physical frame.
        Assert.Equal(committed.Id, work.BaseCommitId);
        bool isEvent = entry.StartsWith("event", StringComparison.Ordinal);
        Assert.Equal(isEvent ? CommitKind.Event : CommitKind.State, committed.Kind);
        Assert.Equal(isEvent ? stateBefore : committed.Id, work.StateSourceCommitId);
        Assert.Same(state, work.State);
        Assert.False(repository.IsFaulted);
        Assert.Throws<InvalidOperationException>(() => repository.OpenWorkingCopy(branch));
        work.CommitEvent(new Root { Value = 30 }); // The guard and original occupancy remain usable.
        Assert.Equal(3, repository.ReadFrames(branch).Count);
    }

    [Theory]
    [InlineData("state-request")]
    [InlineData("event-request")]
    [InlineData("state-immediate")]
    [InlineData("event-immediate")]
    public void SummaryProviderFailurePropagatesAndReleasesTheGuardForRetry(string entry) {
        int captures = 0;
        using Repository repository = Repository.CreateNew(_path, Models());
        BranchId branch = repository.CreateRootBranchFromState("main", new Root { Value = 10 }).Id;
        using WorkingCopy work = repository.OpenWorkingCopy(branch);
        Assert.IsType<Root>(work.State).OnCapture = () => captures++;
        Root domainEvent = new() { Value = 20, OnCapture = () => captures++ };
        CommitId before = work.BaseCommitId;
        CommitId? stateBefore = work.StateSourceCommitId;
        long framesBefore = repository.GraphStore.AppendedFrames;
        IOException sentinel = new("summary span failure");
        using CallbackMemory owner = new([4, 5]);
        ReadOnlyMemory<byte> summary = owner.Memory;
        owner.Callback = () => throw sentinel;
        Assert.Same(sentinel, Assert.Throws<IOException>(() => Invoke(entry, work, domainEvent, summary, out _)));
        Assert.Equal(0, captures);
        Assert.Equal(before, work.BaseCommitId);
        Assert.Equal(stateBefore, work.StateSourceCommitId);
        Assert.Equal(framesBefore, repository.GraphStore.AppendedFrames);
        Assert.False(repository.IsFaulted);
        Assert.True(work.IsLiveFor(repository));
        Assert.Equal(before, repository.ReadCurrentCommit(branch).Commit.Id);
        Assert.Throws<InvalidOperationException>(() => repository.OpenWorkingCopy(branch));
        CommitInfo? retried = Invoke(entry, work, domainEvent, summary, out CommitRequest? request);
        if (request is not null) { retried = repository.CommitBranches([request])[branch]; }
        Assert.NotNull(retried);
        Assert.Equal(new byte[] { 4, 5 }, retried.Summary.ToArray());
        Assert.Equal(1, captures);
        Assert.Equal(2, repository.ReadFrames(branch).Count);
    }

    [Fact]
    public void NestedStateReplacementCannotPairTheOldRootWithANewBaseline() {
        using Repository repository = Repository.CreateNew(_path, Models());
        BranchId branch = repository.CreateRootBranchFromState("main", new Root { Value = 10 }).Id;
        using WorkingCopy work = repository.OpenWorkingCopy(branch);
        Root original = Assert.IsType<Root>(work.State);
        CommitId before = work.BaseCommitId;
        Root replacement = new() { Value = 99 };
        using CallbackMemory owner = new([7]);
        ReadOnlyMemory<byte> summary = owner.Memory;
        owner.Callback = () => {
            Exception? failure = Record.Exception(() => work.CommitState(replacement));
            output.WriteLine($"nested State replacement: failure={failure?.GetType().Name ?? "none"}, " +
                $"baseChanged={work.BaseCommitId != before}, originalRootRetained={ReferenceEquals(original, work.State)}");
            Assert.IsType<InvalidOperationException>(failure);
        };
        CommitRequest request = work.CreateStateCommitRequest(summary);
        Assert.Equal(before, request.ExpectedBaseCommitId);
        Assert.Same(original, work.State);
        CommitInfo committed = repository.CommitBranches([request])[branch];
        Assert.Equal(10, Assert.IsType<Root>(repository.LoadState(committed.Id)).Value);
        Assert.Same(original, work.State);
        Assert.Equal(2, repository.ReadFrames(branch).Count);
    }

    private static CommitInfo? Invoke(string entry, WorkingCopy work, Root domainEvent,
        ReadOnlyMemory<byte> summary, out CommitRequest? request) {
        request = null;
        switch (entry) {
            case "state-request": request = work.CreateStateCommitRequest(summary); return null;
            case "event-request": request = work.CreateEventCommitRequest(domainEvent, summary); return null;
            case "state-immediate": return work.CommitState(summary);
            case "event-immediate": return work.CommitEvent(domainEvent, summary);
            default: throw new ArgumentOutOfRangeException(nameof(entry));
        }
    }

    private sealed class CallbackMemory(byte[] bytes) : MemoryManager<byte> {
        internal byte[] Bytes { get; } = bytes;
        internal Action? Callback { get; set; }
        public override Span<byte> GetSpan() {
            Action? callback = Callback;
            Callback = null;
            callback?.Invoke();
            return Bytes;
        }
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }

    private static StateModelRegistry Models() {
        StateModelRegistry models = new();
        models.Register(new StateModelBinding<Root, int>(new(RootSchema, BaseInt,
            static (in int prior, in int current) => new(prior != current, BaseInt(in current).Body)),
            [new StateReaderBinding<int>(RootSchema, static (ref BareValueReader reader) => reader.ReadVarInt32(),
                static (ref BareValueReader reader, in int _) => reader.ReadVarInt32(), static (in int _, IStateReferenceVisitor _) => { })],
            static row => row.GetState<int>(), static () => new(),
            static (Root root, in int state, ObjectReadTable _) => root.Value = state,
            static (root, _) => { root.OnCapture?.Invoke(); return root.Value; }, static (in int _, IStateReferenceVisitor _) => { }));
        return models;
    }

    private static PreparedBaseBody BaseInt(in int value) {
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        writer.WriteVarInt32(value);
        return new(bytes.WrittenSpan);
    }
    private sealed class Root : IDurableObject { internal int Value; internal Action? OnCapture; }

    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
