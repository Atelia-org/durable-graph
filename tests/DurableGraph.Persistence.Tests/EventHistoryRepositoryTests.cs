using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;

using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.FrameStore;
using SegmentStore = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed partial class EventHistoryRepositoryTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"durable-graph-repository-{Guid.NewGuid():N}");
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1000000, 1);
    private const ulong InitialSequence = 0xFEDC_BA98_7654_3210UL;
    private static readonly DurableSchema Schema = new("RepositoryNode", 1,
        new DurableFieldInfo(1, TypeTag.ObjectReference, "RepositoryNode"),
        new DurableFieldInfo(2, TypeTag.ObjectReference, "RepositoryNode"),
        new DurableFieldInfo(3, TypeTag.String), new DurableFieldInfo(4, TypeTag.Byte),
        new DurableFieldInfo(5, TypeTag.UInt64));

    [Fact]
    public void IndependentEventKeepsStateBaselineAndColdResumeKeepsGraphAliases() {
        Node child = new() { Value = 2, Text = new string('s', 5) };
        Node world = new() { Left = child, Right = child, Value = 1, Text = child.Text };
        child.Left = world;
        FrameAddress first, eventAddress, second, third;
        ObjectId rootId;
        using (Repository repository = CreateRepository()) {
            using WorkingCopy session = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", world, parameters: NoRebase).Id);
            Assert.NotSame(world, session.State);
            world = (Node)session.State!;
            child = world.Left!;
            first = session.StateRevisionAddress!.Value;
            rootId = session.StateId!.Value;
            Assert.Same(world, ((Node)session.State!));
            Assert.False(File.Exists(Path.Combine(_root, "publication.rbf")));
            eventAddress = WorkTestAccess.Address(session, session.CommitEvent(new Node { Value = 8 }).Id);
            Assert.Equal(first, session.StateRevisionAddress!.Value);
            Assert.IsType<Node>(repository.LoadEvent(session.BaseCommitId));
            child.Value = 3;
            second = WorkTestAccess.Address(session, session.CommitState(parameters: NoRebase).Id);
            Assert.Equal(CommitKind.State, WorkTestAccess.Kind(session));
            Assert.Same(child, ((Node)session.State!).Right);
            session.CommitEvent(new Node { Value = 9 });
            child.Value = 4;
            third = WorkTestAccess.Address(session, session.CommitState(parameters: NoRebase).Id);
            Assert.Equal(third, Describe(repository, repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id).Address);
            Assert.Equal(5, repository.ReadFrames(BranchNamed(repository, "main")).Count());
            Assert.Equal(2, repository.EnumerateEvents(repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id, HistoryOrder.OldestFirst).ToArray().Count());
        }
        using (RepositoryResources resources = RepositoryResources.OpenReadOnlyExisting(_root)) {
            GraphRecordStore store = resources.GraphStore;
            Assert.Null(MechanismFixture.Snapshot(store, first).SourceGraph);
            Assert.Null(MechanismFixture.Snapshot(store, eventAddress).SourceGraph);
            Assert.Equal(first, MechanismFixture.Snapshot(store, second).SourceGraph);
            ObjectVersionRecord write = Assert.Single(StorageTestData.LocalRecords(store, second));
            Assert.Equal(ObjectVersionKind.Delta, write.Kind);
            Assert.NotEqual(rootId.Value, write.ObjectId);
            Assert.Equal(second, MechanismFixture.Snapshot(store, third).SourceGraph);
            uint childId = write.ObjectId;
            Assert.Equal(MechanismFixture.Snapshot(store, second).ObjectHeads[childId],
                Assert.Single(StorageTestData.LocalRecords(store, third)).PriorAddress);
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        using WorkingCopy loaded = reopened.OpenWorkingCopy(BranchNamed(reopened, "main"));
        Node loadedState = Assert.IsType<Node>(loaded.State);
        Assert.NotNull(loadedState.Left);
        Assert.Equal(rootId, loaded.StateId!.Value);
        Assert.Equal((byte)4, loadedState.Left!.Value);
        Assert.Equal(InitialSequence, loadedState.Sequence);
        Assert.Same(loadedState.Left, loadedState.Right);
        Assert.Same(loadedState, loadedState.Left.Left);
        Assert.Same(loadedState.Text, loadedState.Left.Text);
        Assert.NotSame(world, loadedState);
    }

    [Fact]
    public void ExplicitEventReadIsolatedFromCheckoutStateAndPairPreservesEachView() {
        Node child = new() { Value = 2 };
        Node world = new() { Left = child, Right = child };
        using (Repository repository = CreateRepository()) {
            using WorkingCopy session = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", world, parameters: NoRebase).Id);
            Assert.NotSame(world, session.State);
            world = (Node)session.State!;
            child = world.Left!;
            session.CommitEvent(new Node { Left = child, Right = child, Value = 7 });
            Assert.Equal((byte)7, ((Node)repository.LoadEvent(session.BaseCommitId)).Value);
        }
        using (Repository repository = Repository.OpenExisting(_root, Models())) {
            using WorkingCopy session = repository.OpenWorkingCopy(BranchNamed(repository, "main"));
            Node pending = ((Node)repository.LoadEvent(session.BaseCommitId));
            Assert.Same(pending.Left, pending.Right);
            Assert.NotSame(pending.Left, ((Node)session.State!).Left);
            ((Node)session.State!).Left!.Value = 9;
            Assert.Equal((byte)2, pending.Left!.Value);
            session.CommitState(parameters: NoRebase);
            Assert.Throws<ArgumentException>(() => repository.LoadEvent(session.BaseCommitId));
        }
        using Repository read = Repository.OpenReadOnlyExisting(_root, Models());
        var eventFrame = Assert.Single(read.EnumerateEvents(read.ReadCurrentCommit(BranchNamed(read, "main")).Commit.Id, HistoryOrder.OldestFirst).ToArray());
        var stateFrame = read.ReadCurrentCommit(BranchNamed(read, "main")).Commit.Id;
        var pair = (First: (Node)read.LoadEvent(eventFrame.Id), Second: (Node)read.LoadState(stateFrame));
        Assert.Equal((byte)2, pair.First.Left!.Value);
        Assert.Equal((byte)9, pair.Second.Left!.Value);
        Assert.Same(pair.First.Left, pair.First.Right);
        Assert.Same(pair.Second.Left, pair.Second.Right);
        Assert.Equal((byte)2, ((Node)read.LoadState(Assert.IsType<CommitId>(read.GetPreviousState(eventFrame.Id)))).Left!.Value);
    }

    [Fact]
    public void RootReplacementInstallsOriginalCandidateOnlyAfterPublication() {
        Node initial = new() { Value = 1 }, replacement = new() { Value = 2 };
        using Repository repository = CreateRepository();
        using WorkingCopy session = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", initial, parameters: NoRebase).Id);
        initial = (Node)session.State!;
        ObjectId originalId = session.StateId!.Value;
        session.CommitEvent(new Node());
        repository.OperationStageObserver = checkpoint => {
            if (checkpoint == RepositoryOperationStage.AfterPrepare) replacement.Value = 3;
        };
        repository.SaveStageObserver = checkpoint => {
            if (checkpoint == RepositorySaveStage.BeforePublication) { Assert.Same(initial, ((Node)session.State!)); }
        };
        var published = session.CommitState(replacement, parameters: NoRebase).Id;
        repository.OperationStageObserver = null;
        repository.SaveStageObserver = null;
        Assert.Same(replacement, ((Node)session.State!));
        Assert.NotEqual(originalId, session.StateId!.Value);
        Assert.Equal((byte)2, ((Node)repository.LoadState(published)).Value);
        session.CommitEvent(new Node());
        var next = session.CommitState(parameters: NoRebase).Id;
        Assert.Equal((byte)3, ((Node)repository.LoadState(next)).Value);
    }

    [Fact]
    public void UnchangedStateWritesNoObjectsAndDetachedThenReattachedInstanceGetsFreshId() {
        Node child = new() { Value = 2 }; child.Left = child;
        Node world = new() { Left = child, Right = child };
        FrameAddress first, unchanged, removed, restored;
        ObjectId rootId;
        using (Repository repository = CreateRepository()) {
            using WorkingCopy session = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", world, parameters: NoRebase).Id);
            Assert.NotSame(world, session.State);
            world = (Node)session.State!;
            child = world.Left!;
            first = session.StateRevisionAddress!.Value; rootId = session.StateId!.Value;
            unchanged = Describe(repository, Save(session)).Address;
            world.Left = world.Right = null;
            removed = Describe(repository, Save(session)).Address;
            world.Left = child;
            restored = Describe(repository, Save(session)).Address;
            Assert.Same(child, ((Node)session.State!).Left);
        }
        using RepositoryResources resources = RepositoryResources.OpenReadOnlyExisting(_root);
        GraphRecordStore store = resources.GraphStore;
        Assert.Empty(StorageTestData.LocalRecords(store, unchanged));
        uint oldId = Assert.Single(MechanismFixture.Snapshot(store, first).ObjectHeads.Keys, id => id != rootId.Value);
        Assert.Equal(new[] { rootId.Value }, MechanismFixture.Snapshot(store, removed).ObjectHeads.Keys);
        ObjectVersionRecord fresh = Assert.Single(StorageTestData.LocalRecords(store, restored), row => row.ObjectId != rootId.Value);
        Assert.True(fresh.ObjectId > oldId);
        Assert.Equal(ObjectVersionKind.Base, fresh.Kind);
        Assert.DoesNotContain(oldId, MechanismFixture.Snapshot(store, restored).ObjectHeads.Keys);
    }

    [Fact]
    public void FreeRoleSequencesPreserveBranchExclusivityAndReadRoleChecks() {
        using Repository repository = CreateRepository();
        using (WorkingCopy session = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", new Node(), parameters: NoRebase).Id)) {
            var first = session.BaseCommitId;
            CommitId nextState = session.CommitState(parameters: NoRebase).Id;
            Assert.Equal(CommitKind.State, repository.ReadCommitInfo(nextState).Kind);
            Assert.Throws<ArgumentException>(() => repository.LoadEvent(nextState));
            Assert.Throws<InvalidOperationException>(() => repository.OpenWorkingCopy(BranchNamed(repository, "main")));
            BranchInfo otherInfo = repository.ForkBranch(first, "other");
            Assert.Equal(first, repository.ReadCurrentCommit(otherInfo.Id).Commit.Id);
            using WorkingCopy other = repository.OpenWorkingCopy(BranchNamed(repository, "other"));
            Assert.Equal(first, other.BaseCommitId);
            Assert.Equal(nextState, repository.ReadCurrentCommit(session.BranchId).Commit.Id);
            session.CommitEvent(new Node());
            CommitId nextEvent = session.CommitEvent(new Node()).Id;
            Assert.Equal(CommitKind.Event, repository.ReadCommitInfo(nextEvent).Kind);
            Assert.Throws<ArgumentException>(() => repository.LoadState(nextEvent));
            session.CommitState(parameters: NoRebase);
        }
        using WorkingCopy resumed = repository.OpenWorkingCopy(BranchNamed(repository, "main"));
        Assert.Equal(CommitKind.State, WorkTestAccess.Kind(resumed));
    }

    [Fact]
    public void HistoricalForkSelectsLogicalSourceAndLeavesOriginalBranchAppendOnly() {
        using Repository repository = CreateRepository();
        CommitId initial, e, completed;
        WorkingCopy closed;
        using (WorkingCopy session = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", new Node { Value = 1 }, parameters: NoRebase).Id)) {
            closed = session; initial = session.BaseCommitId;
            e = session.CommitEvent(new Node { Value = 8 }).Id;
            ((Node)session.State!).Value = 2;
            completed = session.CommitState(parameters: NoRebase).Id;
        }
        Assert.Throws<ObjectDisposedException>(() => closed.CommitEvent(new Node()).Id);
        repository.ForkBranch(initial, "from-state");
        repository.ForkBranch(e, "from-event");
        using (WorkingCopy fork = repository.OpenWorkingCopy(BranchNamed(repository, "from-event"))) {
            Assert.Equal((byte)1, ((Node)fork.State!).Value);
            Assert.Equal((byte)8, ((Node)repository.LoadEvent(fork.BaseCommitId)).Value);
            ((Node)fork.State!).Value = 3;
            fork.CommitState(parameters: NoRebase);
        }
        using (WorkingCopy fork = repository.OpenWorkingCopy(BranchNamed(repository, "from-state"))) {
            Assert.Equal(CommitKind.State, WorkTestAccess.Kind(fork));
            Assert.Equal((byte)1, ((Node)fork.State!).Value);
            Save(fork);
        }
        Assert.Equal(Describe(repository, completed).Address, Describe(repository, repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id).Address);
        BranchInfo oldEventFork = repository.ForkBranch(e, "continue-old-event");
        using WorkingCopy moved = repository.OpenWorkingCopy(oldEventFork.Id);
        Assert.Equal((byte)1, ((Node)moved.State!).Value);
        Assert.Equal((byte)8, ((Node)repository.LoadEvent(moved.BaseCommitId)).Value);
    }

    [Fact]
    public void SameRepositoryHandlesSurviveReopenAndReadonlyOperationsDoNotWrite() {
        CommitId oldHandle;
        using (Repository repository = CreateRepository()) {
            using WorkingCopy session = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", new Node(), parameters: NoRebase).Id);
            oldHandle = session.BaseCommitId;
            Save(session);
        }
        var before = SnapshotFiles();
        using (Repository reader = Repository.OpenReadOnlyExisting(_root, Models())) {
            var head = reader.ReadCurrentCommit(BranchNamed(reader, "main")).Commit.Id;
            Assert.IsType<Node>(reader.LoadState(oldHandle));
            Assert.ThrowsAny<Exception>(() => reader.OpenWorkingCopy(BranchNamed(reader, "main")));
            Assert.ThrowsAny<Exception>(() => reader.ForkBranch(head, "fork"));
            _ = ((Node)reader.LoadState(head));
        }
        AssertFiles(before);
    }

    [Fact]
    public void CaptureFailureAndCaptureReentryPreserveInstalledStateAndCanRetry() {
        bool fail = false, reenter = false;
        WorkingCopy? active = null;
        StateModelRegistry models = Models(node => {
            if (fail && node.Value == 2) throw new InvalidDataException("capture failed on child");
            if (reenter) active!.CommitState(parameters: NoRebase);
        });
        using Repository repository = CreateRepository(models);
        using WorkingCopy session = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", new Node { Left = new Node { Value = 2 } }, parameters: NoRebase).Id);
        active = session;
        FrameAddress first = session.StateRevisionAddress!.Value;
        session.CommitEvent(new Node());
        fail = true;
        Assert.Throws<InvalidDataException>(() => session.CommitState(parameters: NoRebase).Id);
        Assert.Equal(first, session.StateRevisionAddress!.Value);
        Assert.False(repository.IsFaulted);
        fail = false; reenter = true;
        Assert.Throws<InvalidOperationException>(() => session.CommitState(parameters: NoRebase).Id);
        Assert.False(repository.IsFaulted);
        reenter = false;
        session.CommitState(parameters: NoRebase);
    }

    [Theory]
    [InlineData((int)RepositorySaveStage.AfterBusinessAppend)]
    [InlineData((int)RepositorySaveStage.BeforePublication)]
    public void KnownPrepublicationFailureDoesNotInstallCandidateAndColdCheckoutKeepsEventHead(int point) {
        FrameAddress first;
        using (Repository repository = CreateRepository()) {
            using WorkingCopy session = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", new Node { Value = 1 }, parameters: NoRebase).Id);
            first = session.StateRevisionAddress!.Value;
            CommitId eventHead = session.CommitEvent(new Node { Value = 7 }).Id;
            ((Node)session.State!).Value = 2;
            repository.SaveStageObserver = checkpoint => { if (checkpoint == (RepositorySaveStage)point) throw new IOException("interrupted"); };
            RepositoryPublicationException error = Assert.Throws<RepositoryPublicationException>(() => session.CommitState(parameters: NoRebase).Id);
            Assert.Equal(RepositoryPublicationOutcome.NotPublished, error.Outcome);
            Assert.Equal(first, session.StateRevisionAddress!.Value);
            Assert.Equal(eventHead, session.BaseCommitId);
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        using WorkingCopy recovered = reopened.OpenWorkingCopy(BranchNamed(reopened, "main"));
        Assert.Equal(first, recovered.StateRevisionAddress!.Value);
        Assert.Equal((byte)1, ((Node)recovered.State!).Value);
        Assert.Equal((byte)7, ((Node)reopened.LoadEvent(recovered.BaseCommitId)).Value);
    }

    [Theory]
    [InlineData((int)RepositorySaveStage.AfterPublication)]
    [InlineData((int)RepositorySaveStage.BeforeInstall)]
    public void PublishedStateFailureFaultsWriterAndReopenKeepsPublishedStateHead(int point) {
        FrameAddress published;
        using (Repository repository = CreateRepository()) {
            using WorkingCopy session = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", new Node { Value = 1 }, parameters: NoRebase).Id);
            session.CommitEvent(new Node());
            ((Node)session.State!).Value = 9;
            repository.SaveStageObserver = checkpoint => { if (checkpoint == (RepositorySaveStage)point) throw new IOException("lost completion"); };
            RepositoryPublicationException error = Assert.Throws<RepositoryPublicationException>(() => session.CommitState(parameters: NoRebase).Id);
            Assert.Equal(RepositoryPublicationOutcome.Published, error.Outcome);
            published = Assert.IsType<FrameAddress>(error.CandidateRevisionAddress);
            Assert.True(repository.IsFaulted);
            Assert.Throws<InvalidOperationException>(() => session.CommitState(parameters: NoRebase).Id);
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        using WorkingCopy recovered = reopened.OpenWorkingCopy(BranchNamed(reopened, "main"));
        Assert.Equal(published, recovered.StateRevisionAddress!.Value);
        Assert.Equal((byte)9, ((Node)recovered.State!).Value);
        Assert.Equal(CommitKind.State, WorkTestAccess.Kind(recovered));
    }

    [Fact]
    public void InitialPublicationFailureDoesNotExposeEmptyBranchAndPublishedInitialStateCanResume() {
        using (Repository repository = CreateRepository()) {
            repository.SaveStageObserver = checkpoint => {
                if (checkpoint == RepositorySaveStage.BeforePublication) throw new IOException("not bound");
            };
            var error = Assert.Throws<RepositoryPublicationException>(() => repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", new Node(), parameters: NoRebase).Id));
            Assert.Equal(RepositoryPublicationOutcome.NotPublished, error.Outcome);
        }
        using (Repository repository = Repository.OpenExisting(_root, Models())) {
            Assert.ThrowsAny<Exception>(() => repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id);
            repository.SaveStageObserver = checkpoint => {
                if (checkpoint == RepositorySaveStage.AfterPublication) throw new IOException("bound but not returned");
            };
            var error = Assert.Throws<RepositoryPublicationException>(() => repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", new Node { Value = 6 }, parameters: NoRebase).Id));
            Assert.Equal(RepositoryPublicationOutcome.Published, error.Outcome);
        }
        using Repository read = Repository.OpenExisting(_root, Models());
        using WorkingCopy resumed = read.OpenWorkingCopy(BranchNamed(read, "main"));
        Assert.Equal((byte)6, ((Node)resumed.State!).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadedUpgradeRewriteIsNotClearedByEventSaveAndLaterStatesUseDelta(bool namedFork) {
        FrameAddress original, upgraded, unchanged, changed;
        using (Repository repository = CreateRepository()) {
            using var session = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", new Node { Value = 1 }, parameters: NoRebase).Id);
            original = session.StateRevisionAddress!.Value;
        }
        using (Repository repository = Repository.OpenExisting(_root, Models(upgrade: true))) {
            using var session = namedFork ? repository.OpenWorkingCopy(repository.ForkBranch(repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id, "child").Id) : repository.OpenWorkingCopy(BranchNamed(repository, "main"));
            Assert.Equal((byte)11, ((Node)session.State!).Value);
            Node same = ((Node)session.State!);
            session.CommitEvent(new Node());
            upgraded = WorkTestAccess.Address(session, session.CommitState(parameters: NoRebase).Id);
            unchanged = Describe(repository, Save(session)).Address;
            same.Value = 12;
            changed = Describe(repository, Save(session)).Address;
            Assert.Same(same, ((Node)session.State!));
        }
        using RepositoryResources resources = RepositoryResources.OpenReadOnlyExisting(_root);
        GraphRecordStore store = resources.GraphStore;
        Assert.Equal(original, MechanismFixture.Snapshot(store, upgraded).SourceGraph);
        Assert.Equal(ObjectVersionKind.Base, Assert.Single(StorageTestData.LocalRecords(store, upgraded)).Kind);
        Assert.Empty(StorageTestData.LocalRecords(store, unchanged));
        Assert.Equal(unchanged, MechanismFixture.Snapshot(store, changed).SourceGraph);
        Assert.Equal(ObjectVersionKind.Delta, Assert.Single(StorageTestData.LocalRecords(store, changed)).Kind);
    }


    [Fact]
    public void ColdEventBrowseDoesNotBindWorldAndPairSecondFailureDeliversNothing() {
        Node alice = new() { Value = 2 }, bob = new() { Value = 3 };
        StateModelRegistry all = Models();
        DurableSchema outer = new("UnneededWorld", 1,
            new DurableFieldInfo(1, TypeTag.ObjectReference, Schema.SchemaId),
            new DurableFieldInfo(2, TypeTag.ObjectReference, Schema.SchemaId));
        all.Register(new StateModelBinding<WholeWorld, State>(new(outer,
            static (in State state) => {
                ArrayBufferWriter<byte> bytes = new();
                BareValueWriter writer = new(bytes);
                writer.WriteVarUInt32(state.Left.Value); writer.WriteVarUInt32(state.Right.Value);
                return new PreparedBaseBody(bytes.WrittenSpan);
            }, static (in State prior, in State next) => new PreparedDeltaBody(false, [])),
            [new StateReaderBinding<State>(outer,
                static (ref BareValueReader input) => new(input.ReadVarUInt32(), input.ReadVarUInt32(), 0, 0),
                static (ref BareValueReader input, in State prior) => prior,
                static (in State state, IStateReferenceVisitor visitor) => {
                    visitor.VisitDurable(state.Left, Schema.SchemaId); visitor.VisitDurable(state.Right, Schema.SchemaId);
                })],
            static row => throw new InvalidOperationException("World normalization must not run during Event browsing."),
            static () => throw new InvalidOperationException("World allocation must not run during Event browsing."),
            static (WholeWorld domain, in State state, ObjectReadTable objects) => throw new InvalidOperationException("World hydration must not run during Event browsing."),
            static (domain, context) => new(context.CaptureDurable(domain.Alice, Schema.SchemaId), context.CaptureDurable(domain.Bob, Schema.SchemaId), default(ObjectId), 0),
            static (in State state, IStateReferenceVisitor visitor) => {
                visitor.VisitDurable(state.Left, Schema.SchemaId); visitor.VisitDurable(state.Right, Schema.SchemaId);
            }));
        using (Repository repository = CreateRepository(all)) {
            // This model deliberately cannot restore World. Seed exact history through the real
            // writer mechanism without testing OpenWorkingCopy as part of an Event-read fixture.
            using RepositoryStorageFacade facade = new(ResourcesOf(repository));
            BranchSaveSessionFixture seed = new(facade, "main", all);
            using (PreparedFixtureSave state = seed.Prepare(new WholeWorld { Alice = alice, Bob = bob }, parameters: NoRebase)) { state.Commit(); }
            using (PreparedFixtureSave domainEvent = seed.Prepare(new Node { Left = alice, Right = alice, Value = 8 }, StoredGraphKind.Event, NoRebase)) { domainEvent.Commit(); }
        }
        var before = SnapshotFiles();
        // No registration for World exists in this fixed read environment.
        int bobReads = 0, bobHydrates = 0;
        StateModelRegistry eventOnly = Models(onReadValue: value => { if (value == 3) bobReads++; },
            onHydrate: node => { if (node.Value == 3) bobHydrates++; });
        using (Repository repository = Repository.OpenReadOnlyExisting(_root, eventOnly)) {
            var e = Assert.Single(repository.EnumerateEvents(repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id, HistoryOrder.OldestFirst).ToArray());
            Node read = ((Node)repository.LoadEvent(e.Id));
            Assert.Equal(0, bobReads);
            Assert.Equal(0, bobHydrates);
            Assert.Equal((byte)2, read.Left!.Value);
            Assert.Same(read.Left, read.Right);
            Assert.Equal((byte)8, ((IDurableObject)repository.LoadEvent(e.Id)) is Node n ? n.Value : 0);
            bool delivered = false;
            Assert.ThrowsAny<Exception>(() => {
                _ = repository.ReadCheckpoint(e.Id);
                delivered = true;
            });
            Assert.False(delivered);
            Assert.Throws<ArgumentException>(() => ((Node)repository.LoadState(e.Id)));
        }
        using (Repository repository = Repository.OpenReadOnlyExisting(_root, all)) {
            var e = Assert.Single(repository.EnumerateEvents(repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id, HistoryOrder.OldestFirst).ToArray());
            Assert.Equal((byte)8, Assert.IsType<Node>(((IDurableObject)repository.LoadEvent(e.Id))).Value);
        }
        AssertFiles(before);
    }
    private sealed class WholeWorld : IDurableObject { internal Node? Alice; internal Node? Bob; }

    private static CommitId Save(WorkingCopy session) {
        session.CommitEvent(new Node());
        return session.CommitState(parameters: NoRebase).Id;
    }
    private Dictionary<string, (byte[] Bytes, DateTime Modified)> SnapshotFiles() => Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => path, path => (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path)));
    private void AssertFiles(Dictionary<string, (byte[] Bytes, DateTime Modified)> before) {
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, saved) in before) {
            Assert.Equal(saved.Bytes, File.ReadAllBytes(path));
            Assert.Equal(saved.Modified, File.GetLastWriteTimeUtc(path));
        }
    }

    private sealed class Node : IDurableObject {
        internal Node? Left;
        internal Node? Right;
        internal string? Text;
        internal byte Value;
        internal ulong Sequence = InitialSequence;
    }
    private readonly record struct State(ObjectId Left, ObjectId Right, ObjectId Text, byte Value, ulong Sequence = InitialSequence) {
        internal State(uint left, uint right, uint text, byte value, ulong sequence = InitialSequence)
            : this(new ObjectId(left), new ObjectId(right), new ObjectId(text), value, sequence) { }
    }

    private static StateModelRegistry Models(Action<Node>? onCapture = null, bool upgrade = false,
        Action<byte>? onReadValue = null, Action<Node>? onHydrate = null, Func<Node>? allocate = null,
        Action? onNormalize = null) {
        DurableSchema current = upgrade ? new(Schema.SchemaId, 2, Schema.Fields.ToArray()) : Schema;
        StatePreparationBinding<State> preparation = new(current,
            static (in State state) => Base(state),
            static (in State prior, in State next) => Delta(prior, next));
        State Read(ref BareValueReader input) {
            State state = new(input.ReadVarUInt32(), input.ReadVarUInt32(), input.ReadVarUInt32(), input.ReadByte(), input.ReadVarUInt64());
            onReadValue?.Invoke(state.Value);
            return state;
        }
        StateReaderBinding Reader(DurableSchema schema) => new StateReaderBinding<State>(schema, Read,
            static (ref BareValueReader input, in State prior) => Apply(ref input, prior), Visit);
        StateReaderBinding[] readers = upgrade ? [Reader(Schema), Reader(current)] : [Reader(current)];
        StateModelBinding model = new StateModelBinding<Node, State>(preparation, readers,
            row => {
                onNormalize?.Invoke();
                return upgrade && row.Schema!.Version == 1
                    ? row.GetState<State>() with { Value = (byte)(row.GetState<State>().Value + 10) }
                    : row.GetState<State>();
            },
            () => allocate is null ? new Node() : allocate(),
            (Node domain, in State state, ObjectReadTable objects) => {
                domain.Left = objects.ResolveDurable<Node>(state.Left);
                domain.Right = objects.ResolveDurable<Node>(state.Right);
                domain.Text = objects.ResolveString(state.Text);
                domain.Value = state.Value;
                domain.Sequence = state.Sequence;
                onHydrate?.Invoke(domain);
            },
            (node, context) => {
                onCapture?.Invoke(node);
                return new(context.CaptureDurable(node.Left, Schema.SchemaId), context.CaptureDurable(node.Right, Schema.SchemaId),
                    context.CaptureString(node.Text), node.Value, node.Sequence);
            }, Visit);
        StateModelRegistry registry = new();
        registry.Register(model);
        return registry;
    }

    private static void Visit(in State state, IStateReferenceVisitor visitor) {
        visitor.VisitDurable(state.Left, Schema.SchemaId);
        visitor.VisitDurable(state.Right, Schema.SchemaId);
        visitor.VisitString(state.Text);
    }
    private static PreparedBaseBody Base(State state) {
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        writer.WriteVarUInt32(state.Left.Value);
        writer.WriteVarUInt32(state.Right.Value);
        writer.WriteVarUInt32(state.Text.Value);
        writer.WriteByte(state.Value);
        writer.WriteVarUInt64(state.Sequence);
        return new(bytes.WrittenSpan);
    }
    private static PreparedDeltaBody Delta(State prior, State next) {
        byte mask = (byte)((prior.Left != next.Left ? 1 : 0) | (prior.Right != next.Right ? 2 : 0) |
            (prior.Text != next.Text ? 4 : 0) | (prior.Value != next.Value ? 8 : 0) |
            (prior.Sequence != next.Sequence ? 16 : 0));
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        writer.WriteByte(mask);
        if ((mask & 1) != 0) writer.WriteVarUInt32(next.Left.Value);
        if ((mask & 2) != 0) writer.WriteVarUInt32(next.Right.Value);
        if ((mask & 4) != 0) writer.WriteVarUInt32(next.Text.Value);
        if ((mask & 8) != 0) writer.WriteByte(next.Value);
        if ((mask & 16) != 0) writer.WriteVarUInt64(next.Sequence);
        return new(mask != 0, bytes.WrittenSpan);
    }
    private static State Apply(ref BareValueReader input, State prior) {
        byte mask = input.ReadByte();
        if (mask == 0 || mask > 31) throw new InvalidDataException("Invalid test bitmap.");
        return new((mask & 1) != 0 ? new ObjectId(input.ReadVarUInt32()) : prior.Left,
            (mask & 2) != 0 ? new ObjectId(input.ReadVarUInt32()) : prior.Right,
            (mask & 4) != 0 ? new ObjectId(input.ReadVarUInt32()) : prior.Text,
            (mask & 8) != 0 ? input.ReadByte() : prior.Value,
            (mask & 16) != 0 ? input.ReadVarUInt64() : prior.Sequence);
    }
    private Repository CreateRepository(StateModelRegistry? models = null) => Repository.CreateNew(_root, models ?? Models());
    private SegmentStore OpenState() => SegmentStore.Open(Path.Combine(_root, "data"));

    public void Dispose() {
        string resolved = Path.GetFullPath(_root);
        if (!string.Equals(Path.GetDirectoryName(resolved), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("durable-graph-repository-", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Refusing to delete outside this test fixture.");
        }
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}
