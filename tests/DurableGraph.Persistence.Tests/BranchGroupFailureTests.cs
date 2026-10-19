using System.Buffers;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.HeadStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class BranchGroupFailureTests : IDisposable {
    private const string Prefix = "dg-branch-group-failure-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly DurableSchema RootSchema = new("GroupFailure.Root", 1, new DurableFieldInfo(1, TypeTag.Int32));
    private static readonly DurableSchema ExtraSchema = new("GroupFailure.Extra", 1, new DurableFieldInfo(1, TypeTag.Int32));

    public static IEnumerable<object[]> PublicationWindows() {
        foreach (bool growth in new[] { false, true }) {
            yield return [growth, "BeforeAppend", RepositoryPublicationOutcome.NotPublished, false, false];
            yield return [growth, "BeforePublication", RepositoryPublicationOutcome.NotPublished, false, true];
            yield return [growth, "BeforeDataConfirm", RepositoryPublicationOutcome.NotPublished, false, true];
            yield return [growth, "Append1", RepositoryPublicationOutcome.Unknown, false, true];
            yield return [growth, "Append2", RepositoryPublicationOutcome.Unknown, !growth, true];
            if (growth) { yield return [growth, "Append3", RepositoryPublicationOutcome.Unknown, true, true]; }
            yield return [growth, "AfterConfirmed", RepositoryPublicationOutcome.Published, true, true];
            yield return [growth, "AfterPublication", RepositoryPublicationOutcome.Published, true, true];
            yield return [growth, "BeforeInstall", RepositoryPublicationOutcome.Published, true, true];
            yield return [growth, "SecondInstall", RepositoryPublicationOutcome.Published, true, true];
        }
    }

    [Theory]
    [MemberData(nameof(PublicationWindows))]
    public void Publication_failure_reports_all_members_and_cold_open_selects_the_whole_group(
        bool growth, string stage, RepositoryPublicationOutcome expectedOutcome, bool visible, bool faulted) {
        BranchId leftId, rightId;
        CommitId leftBefore, rightBefore;
        HeadEntryId directoryBefore, schemaBefore;
        Dictionary<FrameAddress, byte[]> completed;
        using (Repository repository = Repository.CreateStorageNew(_path, Models(new()))) {
            leftId = repository.CreateRootBranchFromState("left", new Root { Value = 1 }).Id;
            rightId = repository.CreateRootBranchFromState("right", new Root { Value = 2 }).Id;
            using WorkingCopy left = repository.OpenWorkingCopy(leftId);
            using WorkingCopy right = repository.OpenWorkingCopy(rightId);
            leftBefore = left.BaseCommitId;
            rightBefore = right.BaseCommitId;
            RepositoryResources resources = ResourcesOf(repository);
            GraphRecordStore store = repository.GraphStore;
            directoryBefore = resources.Heads.ReadHead(1).Unwrap().Id;
            schemaBefore = resources.Heads.ReadHead(2).Unwrap().Id;
            long appended = store.AppendedFrames;
            completed = Frames(resources.Data);
            int hookCalls = 0, memberInstalls = 0;
            IOException injected = new("group window " + stage);
            repository.SaveStageObserver = point => {
                if (point == RepositorySaveStage.BeforePublication) {
                    foreach (var pair in Frames(resources.Data)) { completed.TryAdd(pair.Key, pair.Value); }
                }
                bool stop = stage switch {
                    "BeforeAppend" => point == RepositorySaveStage.BeforeAppend,
                    "BeforePublication" => point == RepositorySaveStage.BeforePublication,
                    "AfterPublication" => point == RepositorySaveStage.AfterPublication,
                    "BeforeInstall" => point == RepositorySaveStage.BeforeInstall,
                    "SecondInstall" => point == RepositorySaveStage.BeforeMemberInstall && ++memberInstalls == 2,
                    _ => false,
                };
                if (stop) { hookCalls++; throw injected; }
            };
            if (stage is "BeforeDataConfirm" or "AfterConfirmed") {
                InstallPublicationFault(resources.Heads, stage, () => { hookCalls++; throw injected; });
            } else if (stage is "Append1" or "Append2" or "Append3") {
                int target = stage[^1] - '0';
                InstallPublicationFault(resources.Heads, "AfterAppend", () => {
                    if (++hookCalls == target) { throw injected; }
                });
            }
            IDurableObject rightRoot = growth ? new Extra { Value = 20 } : new Root { Value = 20 };
            CommitRequest[] requests = [left.CreateStateCommitRequest(new Root { Value = 10 }), right.CreateStateCommitRequest(rightRoot)];
            IReadOnlyDictionary<BranchId, CommitInfo>? delivered = null;
            RepositoryPublicationException error = Assert.Throws<RepositoryPublicationException>(() => delivered = repository.CommitBranches(requests));
            Assert.Null(delivered);
            Assert.Same(injected, error.InnerException);
            Assert.Equal(expectedOutcome, error.Outcome);
            Assert.Equal(faulted, repository.IsFaulted);
            Assert.True(hookCalls > 0);
            Assert.Equal(2, error.Members.Count);
            Assert.Null(error.TargetBranchId); // Never pretend a multi-member failure is one branch.
            var members = error.Members.ToDictionary(item => item.TargetBranchId!.Value);
            Assert.Equal(leftBefore, members[leftId].ExpectedBaseCommitId);
            Assert.Equal(rightBefore, members[rightId].ExpectedBaseCommitId);
            foreach (var member in error.Members) {
                if (stage == "BeforeAppend") { Assert.Null(member.CandidateRevisionAddress); }
                else { Assert.NotNull(member.CandidateRevisionAddress); }
                if (stage is "AfterPublication" or "BeforeInstall" or "SecondInstall") {
                    Assert.NotNull(member.CandidateCommitId);
                }
            }
            if (stage != "SecondInstall") {
                Assert.Equal(leftBefore, left.BaseCommitId);
                Assert.Equal(rightBefore, right.BaseCommitId);
            }
            if (stage == "BeforeAppend") {
                Assert.Equal(appended, store.AppendedFrames);
                Assert.False(resources.HasPendingSave);
                Assert.Equal(leftBefore, repository.ReadCurrentCommit(leftId).Commit.Id);
                Assert.Equal(rightBefore, repository.ReadCurrentCommit(rightId).Commit.Id);
            } else {
                Assert.True(store.AppendedFrames > appended);
                Assert.ThrowsAny<InvalidOperationException>(() => repository.ListBranches());
                Assert.ThrowsAny<InvalidOperationException>(() => repository.CommitBranches(requests));
            }
        }
        using Repository reopened = Repository.OpenStorageExisting(_path, Models(new()));
        RepositoryResources cold = ResourcesOf(reopened);
        CommitId leftAfter = reopened.ReadCurrentCommit(leftId).Commit.Id;
        CommitId rightAfter = reopened.ReadCurrentCommit(rightId).Commit.Id;
        Assert.Equal(visible, leftAfter != leftBefore);
        Assert.Equal(visible, rightAfter != rightBefore);
        Assert.Equal(visible ? 10 : 1, Assert.IsAssignableFrom<IValue>(reopened.LoadState(leftAfter)).Value);
        Assert.Equal(visible ? 20 : 2, Assert.IsAssignableFrom<IValue>(reopened.LoadState(rightAfter)).Value);
        Assert.Equal(directoryBefore, cold.Heads.ReadHead(1).Unwrap().Id);
        Assert.Equal(visible && growth, cold.Heads.ReadHead(2).Unwrap().Id != schemaBefore);
        foreach (var pair in completed) {
            using FrameRead frame = cold.Data.ReadFrame(pair.Key).Unwrap();
            Assert.Equal(pair.Value, frame.Payload.ToArray());
        }
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("body")]
    public void Second_member_preparation_failure_cleans_every_candidate_and_same_requests_can_be_recaptured(string phase) {
        Hooks hooks = new();
        using Repository repository = Repository.CreateStorageNew(_path, Models(hooks));
        BranchId leftId = repository.CreateRootBranchFromState("left", new Root { Value = 1 }).Id;
        BranchId rightId = repository.CreateRootBranchFromState("right", new Root { Value = 2 }).Id;
        using WorkingCopy left = repository.OpenWorkingCopy(leftId);
        using WorkingCopy right = repository.OpenWorkingCopy(rightId);
        Root first = new() { Value = 10 }, second = new() { Value = 20 };
        CommitRequest[] requests = [left.CreateStateCommitRequest(first), right.CreateStateCommitRequest(second)];
        RepositoryResources resources = ResourcesOf(repository);
        long frames = repository.GraphStore.AppendedFrames;
        var oldHeads = SnapshotHeads(resources, leftId, rightId);
        IOException injected = new("second " + phase);
        hooks.Callback = (at, value) => { if (at == phase && value == 20) { throw injected; } };
        Exception error = Assert.ThrowsAny<Exception>(() => repository.CommitBranches(requests));
        Assert.Contains(injected.Message, error.ToString());
        Assert.False(repository.IsFaulted);
        Assert.False(resources.HasPendingSave);
        Assert.Equal(frames, repository.GraphStore.AppendedFrames);
        Assert.Equal(oldHeads, SnapshotHeads(resources, leftId, rightId));
        Assert.Equal(left.BaseCommitId.Value, oldHeads[0]);
        Assert.Equal(right.BaseCommitId.Value, oldHeads[1]);
        hooks.Callback = null;
        first.Value = 11;
        second.Value = 21;
        hooks.Calls.Clear();
        var saved = repository.CommitBranches(requests);
        Assert.Equal(2, saved.Count);
        Assert.Contains(("capture", 11), hooks.Calls);
        Assert.Contains(("capture", 21), hooks.Calls);
        Assert.Equal(11, Assert.IsType<Root>(repository.LoadState(saved[leftId].Id)).Value);
        Assert.Equal(21, Assert.IsType<Root>(repository.LoadState(saved[rightId].Id)).Value);
    }

    [Theory]
    [InlineData("enumerate")]
    [InlineData("capture")]
    [InlineData("body")]
    public void Input_and_model_callbacks_cannot_reenter_repository_participants_or_borrowed_facade(string phase) {
        Hooks hooks = new();
        using Repository repository = Repository.CreateStorageNew(_path, Models(hooks));
        BranchId leftId = repository.CreateRootBranchFromState("left", new Root { Value = 1 }).Id;
        BranchId rightId = repository.CreateRootBranchFromState("right", new Root { Value = 2 }).Id;
        using WorkingCopy left = repository.OpenWorkingCopy(leftId);
        using WorkingCopy right = repository.OpenWorkingCopy(rightId);
        using RepositoryStorageFacade borrowed = new(ResourcesOf(repository));
        CommitRequest[] requests = [left.CreateStateCommitRequest(new Root { Value = 10 }), right.CreateStateCommitRequest(new Root { Value = 20 })];
        int checks = 0;
        void Check() {
            checks++;
            Assert.Throws<InvalidOperationException>(() => repository.CommitBranches(requests));
            Assert.Throws<InvalidOperationException>(() => repository.ListBranches());
            Assert.Throws<InvalidOperationException>(() => left.CommitState());
            Assert.Throws<InvalidOperationException>(() => right.CommitState());
            bool produced = false;
            Assert.Throws<InvalidOperationException>(() => borrowed.PrepareFrom(leftId.Value, left.BaseCommitId.Value, null,
                StoredGraphKind.State, default, () => { produced = true; throw new Exception("must not capture"); }));
            Assert.False(produced);
        }
        hooks.Callback = (at, _) => { if (at == phase) { Check(); } };
        IEnumerable<CommitRequest> Enumerate() {
            if (phase == "enumerate") { Check(); }
            yield return requests[0];
            if (phase == "enumerate") { Check(); }
            yield return requests[1];
        }
        var saved = repository.CommitBranches(Enumerate());
        Assert.True(checks >= 2);
        Assert.Equal(2, saved.Count);
        Assert.False(repository.IsFaulted);
        hooks.Callback = null;
        left.CommitState(); // Reentry rejection did not strand pending/capture state.
        right.CommitState();
    }

    [Theory]
    [InlineData("enumerate")]
    [InlineData("capture")]
    [InlineData("body")]
    public void Resource_loss_after_user_callback_stops_before_the_next_member_callback(string phase) {
        Hooks hooks = new();
        using Repository repository = Repository.CreateStorageNew(_path, Models(hooks));
        BranchId leftId = repository.CreateRootBranchFromState("left", new Root { Value = 1 }).Id;
        BranchId rightId = repository.CreateRootBranchFromState("right", new Root { Value = 2 }).Id;
        using WorkingCopy left = repository.OpenWorkingCopy(leftId);
        using WorkingCopy right = repository.OpenWorkingCopy(rightId);
        RepositoryResources resources = ResourcesOf(repository);
        GraphRecordStore store = repository.GraphStore;
        long frames = store.AppendedFrames;
        CommitRequest[] requests = [left.CreateStateCommitRequest(new Root { Value = 10 }), right.CreateStateCommitRequest(new Root { Value = 20 })];
        hooks.Calls.Clear();
        hooks.Callback = (at, value) => { if (at == phase && value == 10) { resources.Data.Dispose(); } };
        bool secondEnumerated = false;
        IEnumerable<CommitRequest> Enumerate() {
            if (phase == "enumerate") { resources.Data.Dispose(); }
            yield return requests[0];
            secondEnumerated = true;
            yield return requests[1];
        }
        Assert.ThrowsAny<Exception>(() => repository.CommitBranches(Enumerate()));
        Assert.True(repository.IsFaulted);
        Assert.Equal(frames, store.AppendedFrames);
        if (phase == "enumerate") {
            Assert.False(secondEnumerated);
            Assert.Empty(hooks.Calls);
        } else {
            Assert.Contains((phase, 10), hooks.Calls);
            Assert.DoesNotContain((phase, 20), hooks.Calls);
            if (phase == "capture") { Assert.DoesNotContain(hooks.Calls, item => item.Phase == "body"); }
        }
    }

    [Theory]
    [InlineData("GetEnumerator")]
    [InlineData("MoveNext")]
    [InlineData("Current")]
    [InlineData("Dispose")]
    public void Every_enumerator_callback_keeps_the_resource_guard_and_working_copy_occupancy(string stage) {
        using Repository repository = Repository.CreateStorageNew(_path, Models(new()));
        BranchId leftId = repository.CreateRootBranchFromState("left", new Root { Value = 1 }).Id;
        BranchId rightId = repository.CreateRootBranchFromState("right", new Root { Value = 2 }).Id;
        using WorkingCopy left = repository.OpenWorkingCopy(leftId);
        using WorkingCopy right = repository.OpenWorkingCopy(rightId);
        RepositoryResources resources = ResourcesOf(repository);
        using RepositoryStorageFacade borrowed = new(resources);
        CommitRequest[] requests = [left.CreateStateCommitRequest(new Root { Value = 10 }), right.CreateStateCommitRequest(new Root { Value = 20 })];
        int callbacks = 0;
        var sequence = new CallbackRequests(requests, at => {
            if (at != stage) { return; }
            callbacks++;
            bool produced = false;
            Assert.Throws<InvalidOperationException>(() => borrowed.PrepareFrom(leftId.Value, left.BaseCommitId.Value, null,
                StoredGraphKind.State, default, () => { produced = true; throw new Exception("must not capture"); }));
            Assert.False(produced);
            Assert.Throws<InvalidOperationException>(resources.Dispose);
            Assert.Throws<InvalidOperationException>(left.Dispose);
            Assert.Throws<InvalidOperationException>(right.Dispose);
        });
        var saved = repository.CommitBranches(sequence);
        Assert.True(callbacks > 0);
        Assert.Equal(2, saved.Count);
        Assert.Equal(saved[leftId].Id, left.BaseCommitId);
        Assert.Equal(saved[rightId].Id, right.BaseCommitId);
        Assert.False(repository.IsFaulted);
        left.CommitState();
        right.CommitState();
    }

    [Fact]
    public void Enumerator_disposal_that_closes_graph_store_stops_before_the_first_capture() {
        Hooks hooks = new();
        using Repository repository = Repository.CreateStorageNew(_path, Models(hooks));
        BranchId leftId = repository.CreateRootBranchFromState("left", new Root { Value = 1 }).Id;
        BranchId rightId = repository.CreateRootBranchFromState("right", new Root { Value = 2 }).Id;
        using WorkingCopy left = repository.OpenWorkingCopy(leftId);
        using WorkingCopy right = repository.OpenWorkingCopy(rightId);
        GraphRecordStore store = repository.GraphStore;
        long frames = store.AppendedFrames;
        CommitId leftBefore = left.BaseCommitId, rightBefore = right.BaseCommitId;
        CommitRequest[] requests = [left.CreateStateCommitRequest(new Root { Value = 10 }), right.CreateStateCommitRequest(new Root { Value = 20 })];
        hooks.Calls.Clear();
        int disposals = 0;
        var sequence = new CallbackRequests(requests, at => {
            if (at == "Dispose") { disposals++; store.Dispose(); }
        });
        Exception error = Assert.ThrowsAny<Exception>(() => repository.CommitBranches(sequence));
        Assert.Contains(nameof(ObjectDisposedException), error.ToString());
        Assert.Equal(1, disposals);
        Assert.Empty(hooks.Calls);
        Assert.Equal(frames, store.AppendedFrames);
        Assert.Equal(leftBefore, left.BaseCommitId);
        Assert.Equal(rightBefore, right.BaseCommitId);
    }

    private static HeadEntryId[] SnapshotHeads(RepositoryResources resources, BranchId left, BranchId right) =>
        [resources.Heads.ReadHead(left.Value).Unwrap().Id, resources.Heads.ReadHead(right.Value).Unwrap().Id,
            resources.Heads.ReadHead(1).Unwrap().Id, resources.Heads.ReadHead(2).Unwrap().Id];

    private static Dictionary<FrameAddress, byte[]> Frames(Atelia.FrameStore.FrameStore data) {
        List<FrameAddress> addresses = [];
        data.Inventory(item => addresses.Add(item.Address)).Unwrap();
        Dictionary<FrameAddress, byte[]> result = [];
        foreach (FrameAddress address in addresses) {
            using FrameRead frame = data.ReadFrame(address).Unwrap();
            result.Add(address, frame.Payload.ToArray());
        }
        return result;
    }

    private static StateModelRegistry Models(Hooks hooks) {
        StateModelRegistry models = new();
        models.Register(Model<Root>(RootSchema, hooks));
        models.Register(Model<Extra>(ExtraSchema, hooks));
        return models;
    }

    private static StateModelBinding<T, int> Model<T>(DurableSchema schema, Hooks hooks) where T : class, IValue, new() {
        PreparedBaseBody Prepare(in int value) {
            hooks.Call("body", value);
            return Encode(in value);
        }
        return new(new(schema, Prepare, (in int before, in int now) => {
            hooks.Call("body", now);
            return new(before != now, Encode(in now).Body);
        }), [new StateReaderBinding<int>(schema,
            static (ref BareValueReader reader) => reader.ReadVarInt32(),
            static (ref BareValueReader reader, in int _) => reader.ReadVarInt32(), Visit)],
            static row => row.GetState<int>(), static () => new T(),
            static (T target, in int value, ObjectReadTable _) => target.Value = value,
            (source, _) => { hooks.Call("capture", source.Value); return source.Value; }, Visit);
    }

    private static PreparedBaseBody Encode(in int value) {
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        writer.WriteVarInt32(value);
        return new(buffer.WrittenSpan);
    }
    private static void Visit(in int _, IStateReferenceVisitor visitor) { }
    private interface IValue : IDurableObject { int Value { get; set; } }
    private sealed class Root : IValue { public Root() { } public int Value { get; set; } }
    private sealed class Extra : IValue { public Extra() { } public int Value { get; set; } }
    private sealed class CallbackRequests(CommitRequest[] requests, Action<string> callback) : IEnumerable<CommitRequest> {
        public IEnumerator<CommitRequest> GetEnumerator() {
            callback("GetEnumerator");
            return new Enumerator(requests, callback);
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        private sealed class Enumerator(CommitRequest[] requests, Action<string> callback) : IEnumerator<CommitRequest> {
            private int _index = -1;
            private bool _disposed;
            public CommitRequest Current {
                get { callback("Current"); return requests[_index]; }
            }
            object System.Collections.IEnumerator.Current => Current;
            public bool MoveNext() {
                callback("MoveNext");
                return ++_index < requests.Length;
            }
            public void Reset() => throw new NotSupportedException();
            public void Dispose() {
                if (_disposed) { return; }
                _disposed = true;
                callback("Dispose");
            }
        }
    }
    private sealed class Hooks {
        internal Action<string, int>? Callback;
        internal List<(string Phase, int Value)> Calls = [];
        internal void Call(string phase, int value) { Calls.Add((phase, value)); Callback?.Invoke(phase, value); }
    }
    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
