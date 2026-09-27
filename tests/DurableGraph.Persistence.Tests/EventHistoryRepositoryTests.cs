using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

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
    public void EventAndStateHaveSiblingRevisionParentsAndColdResumeKeepsGraphAliases() {
        Node child = new() { Value = 2, Text = new string('s', 5) };
        Node world = new() { Left = child, Right = child, Value = 1, Text = child.Text };
        child.Left = world;
        FrameAddress first, eventAddress, second, third;
        ObjectId rootId;
        using (EventHistoryRepository repository = CreateRepository()) {
            using EventHistorySession<Node> session = repository.CreateBranch("main", world, Models(), NoRebase);
            first = session.StateRevisionAddress;
            rootId = session.StateId;
            Assert.Same(world, session.State);
            Assert.False(File.Exists(Path.Combine(_root, "publication.rbf")));
            eventAddress = session.CommitDomainEvent(new Node { Value = 8 }, NoRebase).RevisionAddress;
            Assert.Equal(first, session.StateRevisionAddress);
            Assert.IsType<Node>(session.PendingEvent);
            child.Value = 3;
            second = session.CommitDomainState(NoRebase).RevisionAddress;
            Assert.Null(session.PendingEvent);
            Assert.Same(child, session.State.Right);
            session.CommitDomainEvent(new Node { Value = 9 }, NoRebase);
            child.Value = 4;
            third = session.CommitDomainState(NoRebase).RevisionAddress;
            Assert.Equal(third, repository.GetHead("main").RevisionAddress);
            Assert.Equal(5, repository.ReadFrames("main").Count());
            Assert.Equal(2, repository.ReadEvents("main").Count());
        }
        using (SegmentStore segments = OpenState()) {
            using StateRevisionStore store = new(segments);
            Assert.Null(store.Read(first).ParentRevisionAddress);
            Assert.Equal(first, store.Read(eventAddress).ParentRevisionAddress);
            Assert.Equal(first, store.Read(second).ParentRevisionAddress);
            ObjectVersionRecord write = Assert.Single(store.Read(second).LocalObjects);
            Assert.Equal(ObjectVersionKind.Delta, write.Kind);
            Assert.NotEqual(rootId.Value, write.ObjectId);
            Assert.Equal(second, store.Read(third).ParentRevisionAddress);
            Assert.Equal(second, Assert.Single(store.Read(third).LocalObjects).PriorAddress);
        }
        using EventHistoryRepository reopened = EventHistoryRepository.OpenExisting(_root);
        using EventHistorySession<Node> loaded = reopened.Resume<Node>("main", Models());
        Assert.Equal(rootId, loaded.StateId);
        Assert.Equal((byte)4, loaded.State.Left!.Value);
        Assert.Equal(InitialSequence, loaded.State.Sequence);
        Assert.Same(loaded.State.Left, loaded.State.Right);
        Assert.Same(loaded.State, loaded.State.Left.Left);
        Assert.Same(loaded.State.Text, loaded.State.Left.Text);
        Assert.NotSame(world, loaded.State);
    }

    [Fact]
    public void PendingEventColdResumeIsolatedFromMutableStateAndPairPreservesEachView() {
        Node child = new() { Value = 2 };
        Node world = new() { Left = child, Right = child };
        using (EventHistoryRepository repository = CreateRepository()) {
            using EventHistorySession<Node> session = repository.CreateBranch("main", world, Models(), NoRebase);
            session.CommitDomainEvent(new Node { Left = child, Right = child, Value = 7 }, NoRebase);
            Assert.Equal((byte)7, session.GetPendingEvent<Node>().Value);
        }
        using (EventHistoryRepository repository = EventHistoryRepository.OpenExisting(_root)) {
            using EventHistorySession<Node> session = repository.Resume<Node>("main", Models());
            Node pending = session.GetPendingEvent<Node>();
            Assert.Same(pending.Left, pending.Right);
            Assert.NotSame(pending.Left, session.State.Left);
            session.State.Left!.Value = 9;
            Assert.Equal((byte)2, pending.Left!.Value);
            session.CommitDomainState(NoRebase);
            Assert.Throws<InvalidOperationException>(() => session.GetPendingEvent<Node>());
        }
        using EventHistoryRepository read = EventHistoryRepository.OpenReadOnlyExisting(_root);
        var eventFrame = Assert.Single(read.ReadEvents("main"));
        var stateFrame = read.GetHead("main");
        var pair = read.ReadPair<Node, Node>(eventFrame, stateFrame, Models());
        Assert.Equal((byte)2, pair.First.Left!.Value);
        Assert.Equal((byte)9, pair.Second.Left!.Value);
        Assert.Same(pair.First.Left, pair.First.Right);
        Assert.Same(pair.Second.Left, pair.Second.Right);
        Assert.Equal((byte)2, read.ReadState<Node>(read.GetPreviousState(eventFrame), Models()).Left!.Value);
    }

    [Fact]
    public void RootReplacementInstallsOriginalCandidateOnlyAfterPublication() {
        Node initial = new() { Value = 1 }, replacement = new() { Value = 2 };
        using EventHistoryRepository repository = CreateRepository();
        using EventHistorySession<Node> session = repository.CreateBranch("main", initial, Models(), NoRebase);
        ObjectId originalId = session.StateId;
        session.CommitDomainEvent(new Node(), NoRebase);
        repository.Checkpoint = checkpoint => {
            if (checkpoint == CommitCheckpoint.BeforePublication) Assert.Same(initial, session.State);
            if (checkpoint == CommitCheckpoint.AfterPrepare) replacement.Value = 3;
        };
        var published = session.CommitDomainState(replacement, NoRebase);
        repository.Checkpoint = null;
        Assert.Same(replacement, session.State);
        Assert.NotEqual(originalId, session.StateId);
        Assert.Equal((byte)2, repository.ReadState<Node>(published, Models()).Value);
        session.CommitDomainEvent(new Node(), NoRebase);
        var next = session.CommitDomainState(NoRebase);
        Assert.Equal((byte)3, repository.ReadState<Node>(next, Models()).Value);
    }

    [Fact]
    public void UnchangedStateWritesNoObjectsAndDetachedThenReattachedInstanceGetsFreshId() {
        Node child = new() { Value = 2 }; child.Left = child;
        Node world = new() { Left = child, Right = child };
        FrameAddress first, unchanged, removed, restored;
        ObjectId rootId;
        using (EventHistoryRepository repository = CreateRepository()) {
            using EventHistorySession<Node> session = repository.CreateBranch("main", world, Models(), NoRebase);
            first = session.StateRevisionAddress; rootId = session.StateId;
            unchanged = Save(session).RevisionAddress;
            world.Left = world.Right = null;
            removed = Save(session).RevisionAddress;
            world.Left = child;
            restored = Save(session).RevisionAddress;
            Assert.Same(child, session.State.Left);
        }
        using SegmentStore segments = OpenState();
        using StateRevisionStore store = new(segments);
        Assert.Empty(store.Read(unchanged).LocalObjects);
        Assert.Empty(store.Read(unchanged).RemovedObjectIds);
        uint oldId = Assert.Single(store.ReadLiveObjectHeadMap(first).Keys, id => id != rootId.Value);
        Assert.Equal(new[] { rootId.Value }, store.ReadLiveObjectHeadMap(removed).Keys);
        ObjectVersionRecord fresh = Assert.Single(store.Read(restored).LocalObjects, row => row.ObjectId != rootId.Value);
        Assert.True(fresh.ObjectId > oldId);
        Assert.Equal(ObjectVersionKind.Base, fresh.Kind);
        Assert.DoesNotContain(oldId, store.ReadLiveObjectHeadMap(restored).Keys);
    }

    [Fact]
    public void RoleChecksAndSessionExclusivityRejectMisuseBeforeWrites() {
        using EventHistoryRepository repository = CreateRepository();
        using (EventHistorySession<Node> session = repository.CreateBranch("main", new Node(), Models(), NoRebase)) {
            var first = session.Head;
            Assert.Throws<InvalidOperationException>(() => session.CommitDomainState(NoRebase));
            Assert.Throws<InvalidOperationException>(() => repository.Resume<Node>("main", Models()));
            Assert.Throws<InvalidOperationException>(() => repository.CreateBranch("other", first));
            Assert.Throws<InvalidOperationException>(() => repository.MoveBranch("main", first, first));
            session.CommitDomainEvent(new Node(), NoRebase);
            Assert.Throws<InvalidOperationException>(() => session.CommitDomainEvent(new Node(), NoRebase));
            session.CommitDomainState(NoRebase);
        }
        using EventHistorySession<Node> resumed = repository.Resume<Node>("main", Models());
        Assert.Null(resumed.PendingEvent);
    }

    [Fact]
    public void HistoricalForkAndMoveSelectLogicalParentRatherThanLatestPhysicalState() {
        using EventHistoryRepository repository = CreateRepository();
        GraphFrame initial, e, completed;
        EventHistorySession<Node> closed;
        using (EventHistorySession<Node> session = repository.CreateBranch("main", new Node { Value = 1 }, Models(), NoRebase)) {
            closed = session; initial = session.Head;
            e = session.CommitDomainEvent(new Node { Value = 8 }, NoRebase);
            session.State.Value = 2;
            completed = session.CommitDomainState(NoRebase);
        }
        Assert.Throws<ObjectDisposedException>(() => closed.CommitDomainEvent(new Node(), NoRebase));
        repository.CreateBranch("from-state", initial);
        repository.CreateBranch("from-event", e);
        using (EventHistorySession<Node> fork = repository.Resume<Node>("from-event", Models())) {
            Assert.Equal((byte)1, fork.State.Value);
            Assert.Equal((byte)8, fork.GetPendingEvent<Node>().Value);
            fork.State.Value = 3;
            fork.CommitDomainState(NoRebase);
        }
        using (EventHistorySession<Node> fork = repository.Resume<Node>("from-state", Models())) {
            Assert.Null(fork.PendingEvent);
            Assert.Equal((byte)1, fork.State.Value);
            Save(fork);
        }
        Assert.Equal(completed.RevisionAddress, repository.GetHead("main").RevisionAddress);
        Assert.ThrowsAny<Exception>(() => repository.MoveBranch("main", initial, e));
        repository.MoveBranch("main", completed, e);
        using EventHistorySession<Node> moved = repository.Resume<Node>("main", Models());
        Assert.Equal((byte)1, moved.State.Value);
        Assert.Equal((byte)8, moved.GetPendingEvent<Node>().Value);
    }

    [Fact]
    public void ForeignAndStaleOpenHandlesAreRejectedAndReadonlyOperationsDoNotWrite() {
        GraphFrame oldHandle;
        using (EventHistoryRepository repository = CreateRepository()) {
            using EventHistorySession<Node> session = repository.CreateBranch("main", new Node(), Models(), NoRebase);
            oldHandle = session.Head;
            Save(session);
        }
        var before = SnapshotFiles();
        using (EventHistoryRepository reader = EventHistoryRepository.OpenReadOnlyExisting(_root)) {
            var head = reader.GetHead("main");
            Assert.ThrowsAny<Exception>(() => reader.ReadState<Node>(oldHandle, Models()));
            Assert.ThrowsAny<Exception>(() => reader.ReadPair(head, oldHandle, Models()));
            Assert.ThrowsAny<Exception>(() => reader.Resume<Node>("main", Models()));
            Assert.ThrowsAny<Exception>(() => reader.CreateBranch("fork", head));
            reader.ReadState<Node>(head, Models());
            var pair = reader.ReadPair(head, head, Models());
            Assert.IsType<Node>(pair.First);
            Assert.IsType<Node>(pair.Second);
        }
        AssertFiles(before);
    }

    [Fact]
    public void CaptureFailureAndCaptureReentryPreserveInstalledStateAndCanRetry() {
        bool fail = false, reenter = false;
        EventHistorySession<Node>? active = null;
        StateModelRegistry models = Models(node => {
            if (fail && node.Value == 2) throw new InvalidDataException("capture failed on child");
            if (reenter) active!.CommitDomainState(NoRebase);
        });
        using EventHistoryRepository repository = CreateRepository();
        using EventHistorySession<Node> session = repository.CreateBranch("main", new Node { Left = new Node { Value = 2 } }, models, NoRebase);
        active = session;
        FrameAddress first = session.StateRevisionAddress;
        session.CommitDomainEvent(new Node(), NoRebase);
        fail = true;
        Assert.Throws<InvalidDataException>(() => session.CommitDomainState(NoRebase));
        Assert.Equal(first, session.StateRevisionAddress);
        Assert.False(repository.IsFaulted);
        fail = false; reenter = true;
        Assert.Throws<InvalidOperationException>(() => session.CommitDomainState(NoRebase));
        Assert.False(repository.IsFaulted);
        reenter = false;
        session.CommitDomainState(NoRebase);
    }

    [Theory]
    [InlineData((int)CommitCheckpoint.AfterStateDurable)]
    [InlineData((int)CommitCheckpoint.BeforeJournalAppend)]
    [InlineData((int)CommitCheckpoint.AfterJournalDurable)]
    [InlineData((int)CommitCheckpoint.BeforePublication)]
    public void KnownPrepublicationFailureDoesNotInstallCandidateAndColdResumeSeesPendingEvent(int point) {
        FrameAddress first;
        using (EventHistoryRepository repository = CreateRepository()) {
            using EventHistorySession<Node> session = repository.CreateBranch("main", new Node { Value = 1 }, Models(), NoRebase);
            first = session.StateRevisionAddress;
            session.CommitDomainEvent(new Node { Value = 7 }, NoRebase);
            session.State.Value = 2;
            repository.Checkpoint = checkpoint => { if (checkpoint == (CommitCheckpoint)point) throw new IOException("interrupted"); };
            GraphCommitException error = Assert.Throws<GraphCommitException>(() => session.CommitDomainState(NoRebase));
            Assert.Equal(GraphCommitOutcome.NotPublished, error.Outcome);
            Assert.Equal(first, session.StateRevisionAddress);
            Assert.NotNull(session.PendingEvent);
        }
        using EventHistoryRepository reopened = EventHistoryRepository.OpenExisting(_root);
        using EventHistorySession<Node> recovered = reopened.Resume<Node>("main", Models());
        Assert.Equal(first, recovered.StateRevisionAddress);
        Assert.Equal((byte)1, recovered.State.Value);
        Assert.Equal((byte)7, recovered.GetPendingEvent<Node>().Value);
    }

    [Theory]
    [InlineData((int)CommitCheckpoint.AfterPublication)]
    [InlineData((int)CommitCheckpoint.BeforeInstall)]
    public void PublishedStateFailureFaultsWriterAndReopenMustNotReplayPendingEvent(int point) {
        FrameAddress published;
        using (EventHistoryRepository repository = CreateRepository()) {
            using EventHistorySession<Node> session = repository.CreateBranch("main", new Node { Value = 1 }, Models(), NoRebase);
            session.CommitDomainEvent(new Node(), NoRebase);
            session.State.Value = 9;
            repository.Checkpoint = checkpoint => { if (checkpoint == (CommitCheckpoint)point) throw new IOException("lost completion"); };
            GraphCommitException error = Assert.Throws<GraphCommitException>(() => session.CommitDomainState(NoRebase));
            Assert.Equal(GraphCommitOutcome.Published, error.Outcome);
            published = Assert.IsType<FrameAddress>(error.CandidateRevisionAddress);
            Assert.True(repository.IsFaulted);
            Assert.Throws<InvalidOperationException>(() => session.CommitDomainState(NoRebase));
        }
        using EventHistoryRepository reopened = EventHistoryRepository.OpenExisting(_root);
        using EventHistorySession<Node> recovered = reopened.Resume<Node>("main", Models());
        Assert.Equal(published, recovered.StateRevisionAddress);
        Assert.Equal((byte)9, recovered.State.Value);
        Assert.Null(recovered.PendingEvent);
    }

    [Fact]
    public void InitialPublicationFailureDoesNotExposeEmptyBranchAndPublishedInitialStateCanResume() {
        using (EventHistoryRepository repository = CreateRepository()) {
            repository.Checkpoint = checkpoint => {
                if (checkpoint == CommitCheckpoint.BeforePublication) throw new IOException("not bound");
            };
            var error = Assert.Throws<GraphCommitException>(() => repository.CreateBranch("main", new Node(), Models(), NoRebase));
            Assert.Equal(GraphCommitOutcome.NotPublished, error.Outcome);
        }
        using (EventHistoryRepository repository = EventHistoryRepository.OpenExisting(_root)) {
            Assert.ThrowsAny<Exception>(() => repository.GetHead("main"));
            repository.Checkpoint = checkpoint => {
                if (checkpoint == CommitCheckpoint.AfterPublication) throw new IOException("bound but not returned");
            };
            var error = Assert.Throws<GraphCommitException>(() => repository.CreateBranch("main", new Node { Value = 6 }, Models(), NoRebase));
            Assert.Equal(GraphCommitOutcome.Published, error.Outcome);
        }
        using EventHistoryRepository read = EventHistoryRepository.OpenExisting(_root);
        using EventHistorySession<Node> resumed = read.Resume<Node>("main", Models());
        Assert.Equal((byte)6, resumed.State.Value);
    }

    [Fact]
    public void LoadedUpgradeRewriteIsNotClearedByEventSaveAndLaterStatesUseDelta() {
        FrameAddress original, upgraded, unchanged, changed;
        using (EventHistoryRepository repository = CreateRepository()) {
            using var session = repository.CreateBranch("main", new Node { Value = 1 }, Models(), NoRebase);
            original = session.StateRevisionAddress;
        }
        using (EventHistoryRepository repository = EventHistoryRepository.OpenExisting(_root)) {
            using var session = repository.Resume<Node>("main", Models(upgrade: true));
            Assert.Equal((byte)11, session.State.Value);
            Node same = session.State;
            session.CommitDomainEvent(new Node(), NoRebase);
            upgraded = session.CommitDomainState(NoRebase).RevisionAddress;
            unchanged = Save(session).RevisionAddress;
            same.Value = 12;
            changed = Save(session).RevisionAddress;
            Assert.Same(same, session.State);
        }
        using SegmentStore segments = OpenState();
        using StateRevisionStore store = new(segments);
        Assert.Equal(original, store.Read(upgraded).ParentRevisionAddress);
        Assert.Equal(ObjectVersionKind.Base, Assert.Single(store.Read(upgraded).LocalObjects).Kind);
        Assert.Empty(store.Read(unchanged).LocalObjects);
        Assert.Equal(unchanged, store.Read(changed).ParentRevisionAddress);
        Assert.Equal(ObjectVersionKind.Delta, Assert.Single(store.Read(changed).LocalObjects).Kind);
    }

    [Theory]
    [InlineData("wrong-revision-parent")]
    [InlineData("missing-root")]
    [InlineData("same-role")]
    public void CompleteButInvalidJournalGraphRelationshipFailsStrictOpenWithoutRepair(string damage) {
        FrameAddress initial;
        ObjectId rootId;
        using (EventHistoryRepository repository = CreateRepository()) {
            using var session = repository.CreateBranch("main", new Node(), Models(), NoRebase);
            initial = session.StateRevisionAddress;
            rootId = session.StateId;
        }
        FrameAddress invalid;
        using (SegmentStore segments = OpenState()) {
            using StateRevisionStore states = new(segments);
            StateRevision revision = damage switch {
                "wrong-revision-parent" => StateRevision.CreateObjectHeadMapBase(null, states.Read(initial).LocalObjects, []),
                "missing-root" => StateRevision.CreateObjectHeadMapBase(initial, [], []),
                _ => StateRevision.CreateObjectHeadMapDelta(initial, [], []),
            };
            invalid = states.AppendDurably(revision);
        }
        using (HistoryJournal history = HistoryJournal.Open(_root, readOnly: false)) {
            history.ConfirmDurable();
            var branch = history.Journal.OpenBranch("main").Unwrap();
            var prior = history.Journal.GetHead(branch);
            var record = history.Append(damage == "same-role" ? GraphFrameKind.State : GraphFrameKind.Event, invalid, rootId, prior);
            history.Journal.AdvanceRef(branch, prior, record).Unwrap();
        }
        var before = SnapshotFiles();
        Assert.Throws<InvalidDataException>(() => EventHistoryRepository.OpenReadOnlyExisting(_root));
        AssertFiles(before);
        Assert.Throws<InvalidDataException>(() => EventHistoryRepository.OpenExisting(_root));
        AssertFiles(before);
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
                BinaryPayloadWriter writer = new(bytes);
                writer.WriteUInt32(state.Left.Value); writer.WriteUInt32(state.Right.Value);
                return new PreparedBaseBody(bytes.WrittenSpan);
            }, static (in State prior, in State next) => new PreparedDeltaBody(false, [])),
            [new StateReaderBinding<State>(outer,
                static (ref BinaryPayloadReader input) => new(input.ReadUInt32(), input.ReadUInt32(), 0, 0),
                static (ref BinaryPayloadReader input, in State prior) => prior,
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
        using (EventHistoryRepository repository = CreateRepository()) {
            using var session = repository.CreateBranch("main", new WholeWorld { Alice = alice, Bob = bob }, all, NoRebase);
            session.CommitDomainEvent(new Node { Left = alice, Right = alice, Value = 8 }, NoRebase);
        }
        var before = SnapshotFiles();
        using (EventHistoryRepository repository = EventHistoryRepository.OpenReadOnlyExisting(_root)) {
            var e = Assert.Single(repository.ReadEvents("main"));
            // No registration for World exists in this read catalog at all.
            int bobReads = 0, bobHydrates = 0;
            StateModelRegistry eventOnly = Models(onReadValue: value => { if (value == 3) bobReads++; },
                onHydrate: node => { if (node.Value == 3) bobHydrates++; });
            Node read = repository.ReadEvent<Node>(e, eventOnly);
            Assert.Equal(0, bobReads);
            Assert.Equal(0, bobHydrates);
            Assert.Equal((byte)2, read.Left!.Value);
            Assert.Same(read.Left, read.Right);
            Assert.Equal((byte)8, repository.ReadEvent<IDurableObject>(e, all) is Node n ? n.Value : 0);
            bool delivered = false;
            Assert.ThrowsAny<Exception>(() => {
                _ = repository.ReadPair<Node, WholeWorld>(e, repository.GetPreviousState(e), Models());
                delivered = true;
            });
            Assert.False(delivered);
            Assert.Throws<ArgumentException>(() => repository.ReadState<Node>(e, Models()));
        }
        AssertFiles(before);
    }
    private sealed class WholeWorld : IDurableObject { internal Node? Alice; internal Node? Bob; }

    private static GraphFrame Save(EventHistorySession<Node> session) {
        session.CommitDomainEvent(new Node(), NoRebase);
        return session.CommitDomainState(NoRebase);
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
        Action<byte>? onReadValue = null, Action<Node>? onHydrate = null) {
        DurableSchema current = upgrade ? new(Schema.SchemaId, 2, Schema.Fields.ToArray()) : Schema;
        CapturedStatePreparation<State> preparation = new(current,
            static (in State state) => Base(state),
            static (in State prior, in State next) => Delta(prior, next));
        State Read(ref BinaryPayloadReader input) {
            State state = new(input.ReadUInt32(), input.ReadUInt32(), input.ReadUInt32(), input.ReadByte(), input.ReadUInt64());
            onReadValue?.Invoke(state.Value);
            return state;
        }
        StateReaderBinding Reader(DurableSchema schema) => new StateReaderBinding<State>(schema, Read,
            static (ref BinaryPayloadReader input, in State prior) => Apply(ref input, prior), Visit);
        StateReaderBinding[] readers = upgrade ? [Reader(Schema), Reader(current)] : [Reader(current)];
        StateModelBinding model = new StateModelBinding<Node, State>(preparation, readers,
            row => upgrade && row.Schema!.Version == 1 ? row.GetState<State>() with { Value = (byte)(row.GetState<State>().Value + 10) } : row.GetState<State>(),
            static () => new Node(),
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
        BinaryPayloadWriter writer = new(bytes);
        writer.WriteUInt32(state.Left.Value);
        writer.WriteUInt32(state.Right.Value);
        writer.WriteUInt32(state.Text.Value);
        writer.WriteByte(state.Value);
        writer.WriteUInt64(state.Sequence);
        return new(bytes.WrittenSpan);
    }
    private static PreparedDeltaBody Delta(State prior, State next) {
        byte mask = (byte)((prior.Left != next.Left ? 1 : 0) | (prior.Right != next.Right ? 2 : 0) |
            (prior.Text != next.Text ? 4 : 0) | (prior.Value != next.Value ? 8 : 0) |
            (prior.Sequence != next.Sequence ? 16 : 0));
        ArrayBufferWriter<byte> bytes = new();
        BinaryPayloadWriter writer = new(bytes);
        writer.WriteByte(mask);
        if ((mask & 1) != 0) writer.WriteUInt32(next.Left.Value);
        if ((mask & 2) != 0) writer.WriteUInt32(next.Right.Value);
        if ((mask & 4) != 0) writer.WriteUInt32(next.Text.Value);
        if ((mask & 8) != 0) writer.WriteByte(next.Value);
        if ((mask & 16) != 0) writer.WriteUInt64(next.Sequence);
        return new(mask != 0, bytes.WrittenSpan);
    }
    private static State Apply(ref BinaryPayloadReader input, State prior) {
        byte mask = input.ReadByte();
        if (mask == 0 || mask > 31) throw new InvalidDataException("Invalid test bitmap.");
        return new((mask & 1) != 0 ? new ObjectId(input.ReadUInt32()) : prior.Left,
            (mask & 2) != 0 ? new ObjectId(input.ReadUInt32()) : prior.Right,
            (mask & 4) != 0 ? new ObjectId(input.ReadUInt32()) : prior.Text,
            (mask & 8) != 0 ? input.ReadByte() : prior.Value,
            (mask & 16) != 0 ? input.ReadUInt64() : prior.Sequence);
    }
    private EventHistoryRepository CreateRepository() => EventHistoryRepository.CreateNew(_root,
        new() { NewStoreLayout = RbfSegmentStoreLayout.Flat });
    private SegmentStore OpenState() => SegmentStore.OpenExisting(Path.Combine(_root, "state"));

    public void Dispose() {
        string resolved = Path.GetFullPath(_root);
        if (!string.Equals(Path.GetDirectoryName(resolved), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("durable-graph-repository-", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Refusing to delete outside this test fixture.");
        }
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}
