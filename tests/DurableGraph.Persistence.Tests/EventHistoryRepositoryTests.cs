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
        using (Repository repository = CreateRepository()) {
            using BranchCheckout session = repository.CreateBranch("main", world, NoRebase);
            first = session.StateRevisionAddress!.Value;
            rootId = session.StateId!.Value;
            Assert.Same(world, ((Node)session.State!));
            Assert.False(File.Exists(Path.Combine(_root, "publication.rbf")));
            eventAddress = session.CommitEvent(new Node { Value = 8 }, NoRebase).RevisionAddress;
            Assert.Equal(first, session.StateRevisionAddress!.Value);
            Assert.IsType<Node>(repository.ReadEvent(session.Head));
            child.Value = 3;
            second = session.CommitState(NoRebase).RevisionAddress;
            Assert.Equal(GraphFrameKind.State, session.Head.Kind);
            Assert.Same(child, ((Node)session.State!).Right);
            session.CommitEvent(new Node { Value = 9 }, NoRebase);
            child.Value = 4;
            third = session.CommitState(NoRebase).RevisionAddress;
            Assert.Equal(third, repository.GetHead("main").RevisionAddress);
            Assert.Equal(5, repository.ReadFrames("main").Count());
            Assert.Equal(2, repository.EnumerateEvents(repository.GetHead("main"), HistoryOrder.OldestFirst).ToArray().Count());
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
        using Repository reopened = Repository.OpenExisting(_root, Models());
        using BranchCheckout loaded = reopened.Checkout("main");
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
            using BranchCheckout session = repository.CreateBranch("main", world, NoRebase);
            session.CommitEvent(new Node { Left = child, Right = child, Value = 7 }, NoRebase);
            Assert.Equal((byte)7, ((Node)repository.ReadEvent(session.Head)).Value);
        }
        using (Repository repository = Repository.OpenExisting(_root, Models())) {
            using BranchCheckout session = repository.Checkout("main");
            Node pending = ((Node)repository.ReadEvent(session.Head));
            Assert.Same(pending.Left, pending.Right);
            Assert.NotSame(pending.Left, ((Node)session.State!).Left);
            ((Node)session.State!).Left!.Value = 9;
            Assert.Equal((byte)2, pending.Left!.Value);
            session.CommitState(NoRebase);
            Assert.Throws<ArgumentException>(() => repository.ReadEvent(session.Head));
        }
        using Repository read = Repository.OpenReadOnlyExisting(_root, Models());
        var eventFrame = Assert.Single(read.EnumerateEvents(read.GetHead("main"), HistoryOrder.OldestFirst).ToArray());
        var stateFrame = read.GetHead("main");
        var pair = (((Node First, Node Second))read.ReadPair(eventFrame, stateFrame));
        Assert.Equal((byte)2, pair.First.Left!.Value);
        Assert.Equal((byte)9, pair.Second.Left!.Value);
        Assert.Same(pair.First.Left, pair.First.Right);
        Assert.Same(pair.Second.Left, pair.Second.Right);
        Assert.Equal((byte)2, ((Node)read.ReadState(Assert.IsType<CheckpointAddress>(read.GetPreviousState(eventFrame)))).Left!.Value);
    }

    [Fact]
    public void RootReplacementInstallsOriginalCandidateOnlyAfterPublication() {
        Node initial = new() { Value = 1 }, replacement = new() { Value = 2 };
        using Repository repository = CreateRepository();
        using BranchCheckout session = repository.CreateBranch("main", initial, NoRebase);
        ObjectId originalId = session.StateId!.Value;
        session.CommitEvent(new Node(), NoRebase);
        repository.Checkpoint = checkpoint => {
            if (checkpoint == CommitCheckpoint.BeforePublication) Assert.Same(initial, ((Node)session.State!));
            if (checkpoint == CommitCheckpoint.AfterPrepare) replacement.Value = 3;
        };
        var published = session.CommitState(replacement, NoRebase);
        repository.Checkpoint = null;
        Assert.Same(replacement, ((Node)session.State!));
        Assert.NotEqual(originalId, session.StateId!.Value);
        Assert.Equal((byte)2, ((Node)repository.ReadState(published)).Value);
        session.CommitEvent(new Node(), NoRebase);
        var next = session.CommitState(NoRebase);
        Assert.Equal((byte)3, ((Node)repository.ReadState(next)).Value);
    }

    [Fact]
    public void UnchangedStateWritesNoObjectsAndDetachedThenReattachedInstanceGetsFreshId() {
        Node child = new() { Value = 2 }; child.Left = child;
        Node world = new() { Left = child, Right = child };
        FrameAddress first, unchanged, removed, restored;
        ObjectId rootId;
        using (Repository repository = CreateRepository()) {
            using BranchCheckout session = repository.CreateBranch("main", world, NoRebase);
            first = session.StateRevisionAddress!.Value; rootId = session.StateId!.Value;
            unchanged = Save(session).RevisionAddress;
            world.Left = world.Right = null;
            removed = Save(session).RevisionAddress;
            world.Left = child;
            restored = Save(session).RevisionAddress;
            Assert.Same(child, ((Node)session.State!).Left);
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
    public void FreeRoleSequencesPreserveSessionExclusivityAndReadRoleChecks() {
        using Repository repository = CreateRepository();
        using (BranchCheckout session = repository.CreateBranch("main", new Node(), NoRebase)) {
            var first = session.Head;
            CheckpointAddress nextState = session.CommitState(NoRebase);
            Assert.Equal(GraphFrameKind.State, nextState.Kind);
            Assert.Throws<ArgumentException>(() => repository.ReadEvent(nextState));
            Assert.Throws<InvalidOperationException>(() => repository.Checkout("main"));
            Assert.Throws<InvalidOperationException>(() => repository.CreateBranch("other", first));
            Assert.Throws<InvalidOperationException>(() => repository.MoveBranch("main", first, first));
            session.CommitEvent(new Node(), NoRebase);
            CheckpointAddress nextEvent = session.CommitEvent(new Node(), NoRebase);
            Assert.Equal(GraphFrameKind.Event, nextEvent.Kind);
            Assert.Throws<ArgumentException>(() => repository.ReadState(nextEvent));
            session.CommitState(NoRebase);
        }
        using BranchCheckout resumed = repository.Checkout("main");
        Assert.Equal(GraphFrameKind.State, resumed.Head.Kind);
    }

    [Fact]
    public void HistoricalForkAndMoveSelectLogicalParentRatherThanLatestPhysicalState() {
        using Repository repository = CreateRepository();
        CheckpointAddress initial, e, completed;
        BranchCheckout closed;
        using (BranchCheckout session = repository.CreateBranch("main", new Node { Value = 1 }, NoRebase)) {
            closed = session; initial = session.Head;
            e = session.CommitEvent(new Node { Value = 8 }, NoRebase);
            ((Node)session.State!).Value = 2;
            completed = session.CommitState(NoRebase);
        }
        Assert.Throws<ObjectDisposedException>(() => closed.CommitEvent(new Node(), NoRebase));
        repository.CreateBranch("from-state", initial);
        repository.CreateBranch("from-event", e);
        using (BranchCheckout fork = repository.Checkout("from-event")) {
            Assert.Equal((byte)1, ((Node)fork.State!).Value);
            Assert.Equal((byte)8, ((Node)repository.ReadEvent(fork.Head)).Value);
            ((Node)fork.State!).Value = 3;
            fork.CommitState(NoRebase);
        }
        using (BranchCheckout fork = repository.Checkout("from-state")) {
            Assert.Equal(GraphFrameKind.State, fork.Head.Kind);
            Assert.Equal((byte)1, ((Node)fork.State!).Value);
            Save(fork);
        }
        Assert.Equal(completed.RevisionAddress, repository.GetHead("main").RevisionAddress);
        Assert.ThrowsAny<Exception>(() => repository.MoveBranch("main", initial, e));
        repository.MoveBranch("main", completed, e);
        using BranchCheckout moved = repository.Checkout("main");
        Assert.Equal((byte)1, ((Node)moved.State!).Value);
        Assert.Equal((byte)8, ((Node)repository.ReadEvent(moved.Head)).Value);
    }

    [Fact]
    public void ForeignAndStaleOpenHandlesAreRejectedAndReadonlyOperationsDoNotWrite() {
        CheckpointAddress oldHandle;
        using (Repository repository = CreateRepository()) {
            using BranchCheckout session = repository.CreateBranch("main", new Node(), NoRebase);
            oldHandle = session.Head;
            Save(session);
        }
        var before = SnapshotFiles();
        using (Repository reader = Repository.OpenReadOnlyExisting(_root, Models())) {
            var head = reader.GetHead("main");
            Assert.ThrowsAny<Exception>(() => ((Node)reader.ReadState(oldHandle)));
            Assert.ThrowsAny<Exception>(() => reader.ReadPair(head, oldHandle));
            Assert.ThrowsAny<Exception>(() => reader.Checkout("main"));
            Assert.ThrowsAny<Exception>(() => reader.CreateBranch("fork", head));
            _ = ((Node)reader.ReadState(head));
            var pair = reader.ReadPair(head, head);
            Assert.IsType<Node>(pair.First);
            Assert.IsType<Node>(pair.Second);
        }
        AssertFiles(before);
    }

    [Fact]
    public void CaptureFailureAndCaptureReentryPreserveInstalledStateAndCanRetry() {
        bool fail = false, reenter = false;
        BranchCheckout? active = null;
        StateModelRegistry models = Models(node => {
            if (fail && node.Value == 2) throw new InvalidDataException("capture failed on child");
            if (reenter) active!.CommitState(NoRebase);
        });
        using Repository repository = CreateRepository(models);
        using BranchCheckout session = repository.CreateBranch("main", new Node { Left = new Node { Value = 2 } }, NoRebase);
        active = session;
        FrameAddress first = session.StateRevisionAddress!.Value;
        session.CommitEvent(new Node(), NoRebase);
        fail = true;
        Assert.Throws<InvalidDataException>(() => session.CommitState(NoRebase));
        Assert.Equal(first, session.StateRevisionAddress!.Value);
        Assert.False(repository.IsFaulted);
        fail = false; reenter = true;
        Assert.Throws<InvalidOperationException>(() => session.CommitState(NoRebase));
        Assert.False(repository.IsFaulted);
        reenter = false;
        session.CommitState(NoRebase);
    }

    [Theory]
    [InlineData((int)CommitCheckpoint.AfterStateDurable)]
    [InlineData((int)CommitCheckpoint.BeforeJournalAppend)]
    [InlineData((int)CommitCheckpoint.AfterJournalDurable)]
    [InlineData((int)CommitCheckpoint.BeforePublication)]
    public void KnownPrepublicationFailureDoesNotInstallCandidateAndColdCheckoutKeepsEventHead(int point) {
        FrameAddress first;
        using (Repository repository = CreateRepository()) {
            using BranchCheckout session = repository.CreateBranch("main", new Node { Value = 1 }, NoRebase);
            first = session.StateRevisionAddress!.Value;
            session.CommitEvent(new Node { Value = 7 }, NoRebase);
            ((Node)session.State!).Value = 2;
            repository.Checkpoint = checkpoint => { if (checkpoint == (CommitCheckpoint)point) throw new IOException("interrupted"); };
            GraphCommitException error = Assert.Throws<GraphCommitException>(() => session.CommitState(NoRebase));
            Assert.Equal(GraphCommitOutcome.NotPublished, error.Outcome);
            Assert.Equal(first, session.StateRevisionAddress!.Value);
            Assert.Equal(GraphFrameKind.Event, session.Head.Kind);
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        using BranchCheckout recovered = reopened.Checkout("main");
        Assert.Equal(first, recovered.StateRevisionAddress!.Value);
        Assert.Equal((byte)1, ((Node)recovered.State!).Value);
        Assert.Equal((byte)7, ((Node)reopened.ReadEvent(recovered.Head)).Value);
    }

    [Theory]
    [InlineData((int)CommitCheckpoint.AfterPublication)]
    [InlineData((int)CommitCheckpoint.BeforeInstall)]
    public void PublishedStateFailureFaultsWriterAndReopenKeepsPublishedStateHead(int point) {
        FrameAddress published;
        using (Repository repository = CreateRepository()) {
            using BranchCheckout session = repository.CreateBranch("main", new Node { Value = 1 }, NoRebase);
            session.CommitEvent(new Node(), NoRebase);
            ((Node)session.State!).Value = 9;
            repository.Checkpoint = checkpoint => { if (checkpoint == (CommitCheckpoint)point) throw new IOException("lost completion"); };
            GraphCommitException error = Assert.Throws<GraphCommitException>(() => session.CommitState(NoRebase));
            Assert.Equal(GraphCommitOutcome.Published, error.Outcome);
            published = Assert.IsType<FrameAddress>(error.CandidateRevisionAddress);
            Assert.True(repository.IsFaulted);
            Assert.Throws<InvalidOperationException>(() => session.CommitState(NoRebase));
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        using BranchCheckout recovered = reopened.Checkout("main");
        Assert.Equal(published, recovered.StateRevisionAddress!.Value);
        Assert.Equal((byte)9, ((Node)recovered.State!).Value);
        Assert.Equal(GraphFrameKind.State, recovered.Head.Kind);
    }

    [Fact]
    public void InitialPublicationFailureDoesNotExposeEmptyBranchAndPublishedInitialStateCanResume() {
        using (Repository repository = CreateRepository()) {
            repository.Checkpoint = checkpoint => {
                if (checkpoint == CommitCheckpoint.BeforePublication) throw new IOException("not bound");
            };
            var error = Assert.Throws<GraphCommitException>(() => repository.CreateBranch("main", new Node(), NoRebase));
            Assert.Equal(GraphCommitOutcome.NotPublished, error.Outcome);
        }
        using (Repository repository = Repository.OpenExisting(_root, Models())) {
            Assert.ThrowsAny<Exception>(() => repository.GetHead("main"));
            repository.Checkpoint = checkpoint => {
                if (checkpoint == CommitCheckpoint.AfterPublication) throw new IOException("bound but not returned");
            };
            var error = Assert.Throws<GraphCommitException>(() => repository.CreateBranch("main", new Node { Value = 6 }, NoRebase));
            Assert.Equal(GraphCommitOutcome.Published, error.Outcome);
        }
        using Repository read = Repository.OpenExisting(_root, Models());
        using BranchCheckout resumed = read.Checkout("main");
        Assert.Equal((byte)6, ((Node)resumed.State!).Value);
    }

    [Fact]
    public void LoadedUpgradeRewriteIsNotClearedByEventSaveAndLaterStatesUseDelta() {
        FrameAddress original, upgraded, unchanged, changed;
        using (Repository repository = CreateRepository()) {
            using var session = repository.CreateBranch("main", new Node { Value = 1 }, NoRebase);
            original = session.StateRevisionAddress!.Value;
        }
        using (Repository repository = Repository.OpenExisting(_root, Models(upgrade: true))) {
            using var session = repository.Checkout("main");
            Assert.Equal((byte)11, ((Node)session.State!).Value);
            Node same = ((Node)session.State!);
            session.CommitEvent(new Node(), NoRebase);
            upgraded = session.CommitState(NoRebase).RevisionAddress;
            unchanged = Save(session).RevisionAddress;
            same.Value = 12;
            changed = Save(session).RevisionAddress;
            Assert.Same(same, ((Node)session.State!));
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
    [InlineData("state-wrong-revision-parent")]
    public void CompleteButInvalidJournalGraphRelationshipFailsStrictOpenWithoutRepair(string damage) {
        FrameAddress initial;
        ObjectId rootId;
        using (Repository repository = CreateRepository()) {
            using var session = repository.CreateBranch("main", new Node(), NoRebase);
            initial = session.StateRevisionAddress!.Value;
            rootId = session.StateId!.Value;
        }
        FrameAddress invalid;
        using (SegmentStore segments = OpenState()) {
            using StateRevisionStore states = new(segments);
            StateRevision revision = damage switch {
                "wrong-revision-parent" or "state-wrong-revision-parent" => StateRevision.CreateObjectHeadMapBase(null, states.Read(initial).LocalObjects, []),
                "missing-root" => StateRevision.CreateObjectHeadMapBase(initial, [], []),
                _ => StateRevision.CreateObjectHeadMapDelta(initial, [], []),
            };
            invalid = states.AppendDurably(revision);
        }
        using (HistoryJournal history = HistoryJournal.Open(_root, readOnly: false)) {
            history.ConfirmDurable();
            var branch = history.Journal.OpenBranch("main").Unwrap();
            var prior = history.Journal.GetHead(branch);
            var record = history.Append(damage == "state-wrong-revision-parent" ? GraphFrameKind.State : GraphFrameKind.Event, invalid, rootId, prior);
            history.Journal.AdvanceRef(branch, prior, record).Unwrap();
        }
        var before = SnapshotFiles();
        Assert.Throws<InvalidDataException>(() => Repository.OpenReadOnlyExisting(_root, Models()));
        AssertFiles(before);
        Assert.Throws<InvalidDataException>(() => Repository.OpenExisting(_root, Models()));
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
        using (Repository repository = CreateRepository(all)) {
            using var session = repository.CreateBranch("main", new WholeWorld { Alice = alice, Bob = bob }, NoRebase);
            session.CommitEvent(new Node { Left = alice, Right = alice, Value = 8 }, NoRebase);
        }
        var before = SnapshotFiles();
        // No registration for World exists in this fixed read environment.
        int bobReads = 0, bobHydrates = 0;
        StateModelRegistry eventOnly = Models(onReadValue: value => { if (value == 3) bobReads++; },
            onHydrate: node => { if (node.Value == 3) bobHydrates++; });
        using (Repository repository = Repository.OpenReadOnlyExisting(_root, eventOnly)) {
            var e = Assert.Single(repository.EnumerateEvents(repository.GetHead("main"), HistoryOrder.OldestFirst).ToArray());
            Node read = ((Node)repository.ReadEvent(e));
            Assert.Equal(0, bobReads);
            Assert.Equal(0, bobHydrates);
            Assert.Equal((byte)2, read.Left!.Value);
            Assert.Same(read.Left, read.Right);
            Assert.Equal((byte)8, ((IDurableObject)repository.ReadEvent(e)) is Node n ? n.Value : 0);
            bool delivered = false;
            Assert.ThrowsAny<Exception>(() => {
                _ = (((Node First, WholeWorld Second))repository.ReadPair(e, Assert.IsType<CheckpointAddress>(repository.GetPreviousState(e))));
                delivered = true;
            });
            Assert.False(delivered);
            Assert.Throws<ArgumentException>(() => ((Node)repository.ReadState(e)));
        }
        using (Repository repository = Repository.OpenReadOnlyExisting(_root, all)) {
            var e = Assert.Single(repository.EnumerateEvents(repository.GetHead("main"), HistoryOrder.OldestFirst).ToArray());
            Assert.Equal((byte)8, Assert.IsType<Node>(((IDurableObject)repository.ReadEvent(e))).Value);
        }
        AssertFiles(before);
    }
    private sealed class WholeWorld : IDurableObject { internal Node? Alice; internal Node? Bob; }

    private static CheckpointAddress Save(BranchCheckout session) {
        session.CommitEvent(new Node(), NoRebase);
        return session.CommitState(NoRebase);
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
    private Repository CreateRepository(StateModelRegistry? models = null) => Repository.CreateNew(_root, models ?? Models(), new() { NewStoreLayout = RbfSegmentStoreLayout.Flat });
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
