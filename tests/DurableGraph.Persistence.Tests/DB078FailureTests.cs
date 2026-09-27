using System.Reflection;
using Atelia.Data;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;
using Journal = Atelia.EventJournal.EventJournal;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed partial class EventHistoryRepositoryTests {
    [Theory]
    [InlineData((int)CommitCheckpoint.AfterStateDurable, false)]
    [InlineData((int)CommitCheckpoint.BeforeJournalAppend, false)]
    [InlineData((int)CommitCheckpoint.AfterJournalDurable, false)]
    [InlineData((int)CommitCheckpoint.BeforePublication, false)]
    [InlineData((int)CommitCheckpoint.AfterPublication, true)]
    [InlineData((int)CommitCheckpoint.BeforeInstall, true)]
    public void InitialEventPublicationFailureReopensWithEitherNoBranchOrEventOnlyBranch(int point, bool published) {
        using (Repository repository = CreateRepository()) {
            repository.Checkpoint = checkpoint => {
                if (checkpoint == (CommitCheckpoint)point) throw new IOException("Interrupted initial Event.");
            };
            var failure = Assert.Throws<GraphCommitException>(() =>
                repository.CreateBranchFromEvent("main", new Node { Value = 7 }, NoRebase));
            Assert.Equal(published ? GraphCommitOutcome.Published : GraphCommitOutcome.NotPublished, failure.Outcome);
            Assert.True(repository.IsFaulted);
            Assert.NotNull(failure.CandidateRevisionAddress);
            Assert.Throws<InvalidOperationException>(() => repository.GetHead("main"));
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        Assert.Equal(published, reopened.ListBranches().Contains("main"));
        if (published) {
            using BranchCheckout checkout = reopened.Checkout("main");
            Assert.Null(checkout.State);
            Assert.Equal((byte)7, Assert.IsType<Node>(reopened.ReadEvent(checkout.Head)).Value);
            checkout.CommitState(new Node { Value = 9 }, NoRebase);
            Assert.Equal((byte)9, Assert.IsType<Node>(checkout.State).Value);
        }
    }

    [Theory]
    [InlineData("before-create", false)]
    [InlineData("after-create", false)]
    [InlineData("before-bind", false)]
    [InlineData("after-bind", true)]
    public void InitialEventUnknownPublicationRequiresReopenToDiscoverVisibility(string phase, bool visible) {
        using (Repository repository = CreateRepository()) {
            Journal journal = JournalOf(repository);
            FieldInfo field = typeof(Journal).GetField("_refOpLog", BindingFlags.Instance | BindingFlags.NonPublic)!;
            IRbfFile original = (IRbfFile)field.GetValue(journal)!;
            string refObjects = (string)typeof(Journal).GetField("_refObjectsPath", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(journal)!;
            field.SetValue(journal, new RefOpFaultFile(original, phase, refObjects));
            var failure = Assert.Throws<GraphCommitException>(() =>
                repository.CreateBranchFromEvent("main", new Node { Value = 7 }, NoRebase));
            Assert.Equal(GraphCommitOutcome.Unknown, failure.Outcome);
            Assert.True(repository.IsFaulted);
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        Assert.Equal(visible, reopened.ListBranches().Contains("main"));
        if (visible) {
            using BranchCheckout checkout = reopened.Checkout("main");
            Assert.Null(checkout.State);
            Assert.Equal((byte)7, Assert.IsType<Node>(reopened.ReadEvent(checkout.Head)).Value);
        }
    }

    [Theory]
    [InlineData((int)CommitCheckpoint.AfterStateDurable, false)]
    [InlineData((int)CommitCheckpoint.BeforeJournalAppend, false)]
    [InlineData((int)CommitCheckpoint.AfterJournalDurable, false)]
    [InlineData((int)CommitCheckpoint.BeforePublication, false)]
    [InlineData((int)CommitCheckpoint.AfterPublication, true)]
    [InlineData((int)CommitCheckpoint.BeforeInstall, true)]
    public void FirstStateFailureNeverInstallsCandidateAndColdCheckoutUsesActualPublishedHead(int point, bool published) {
        using (Repository repository = CreateRepository()) {
            using BranchCheckout checkout = repository.CreateBranchFromEvent("main", new Node { Value = 7 }, NoRebase);
            var eventHead = checkout.Head;
            repository.Checkpoint = checkpoint => {
                Assert.Null(checkout.State);
                Assert.Equal(eventHead, checkout.Head);
                if (checkpoint == (CommitCheckpoint)point) throw new IOException("Interrupted first State.");
            };
            var failure = Assert.Throws<GraphCommitException>(() => checkout.CommitState(new Node { Value = 9 }, NoRebase));
            Assert.Equal(published ? GraphCommitOutcome.Published : GraphCommitOutcome.NotPublished, failure.Outcome);
            Assert.Null(checkout.State);
            Assert.Equal(eventHead, checkout.Head);
            Assert.True(checkout.IsFaulted);
            Assert.Throws<InvalidOperationException>(() => checkout.CommitState(new Node(), NoRebase));
            Assert.Throws<InvalidOperationException>(() => checkout.CommitEvent(new Node(), NoRebase));
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        using BranchCheckout recovered = reopened.Checkout("main");
        if (published) {
            Assert.Equal((byte)9, Assert.IsType<Node>(recovered.State).Value);
            Assert.Equal(GraphFrameKind.State, recovered.Head.Kind);
        } else {
            Assert.Null(recovered.State);
            Assert.Equal((byte)7, Assert.IsType<Node>(reopened.ReadEvent(recovered.Head)).Value);
            recovered.CommitState(new Node { Value = 11 }, NoRebase);
            Assert.Equal((byte)11, Assert.IsType<Node>(recovered.State).Value);
        }
    }

    [Fact]
    public void FirstStateUnknownRefWriteFailureLeavesNoInstalledCandidateAndReopensEventOnly() {
        using (Repository repository = CreateRepository()) {
            using BranchCheckout checkout = repository.CreateBranchFromEvent("main", new Node { Value = 7 }, NoRebase);
            var eventHead = checkout.Head;
            string refFile = Assert.Single(Directory.GetFiles(Path.Combine(_root, "journal", "refs", "objects"), "*.rbf", SearchOption.AllDirectories));
            FileStream? blocked = null;
            try {
                repository.Checkpoint = checkpoint => {
                    if (checkpoint == CommitCheckpoint.BeforePublication) {
                        // AdvanceRef opens its backing ref object only inside publication. Prevent
                        // that real I/O without changing any persisted frame or product test seam.
                        blocked = new FileStream(refFile, FileMode.Open, FileAccess.Read, FileShare.None);
                    }
                };
                var failure = Assert.Throws<GraphCommitException>(() => checkout.CommitState(new Node { Value = 9 }, NoRebase));
                Assert.Equal(GraphCommitOutcome.Unknown, failure.Outcome);
                Assert.True(repository.IsFaulted);
                Assert.Null(checkout.State);
                Assert.Equal(eventHead, checkout.Head);
            } finally {
                blocked?.Dispose();
            }
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        using BranchCheckout recovered = reopened.Checkout("main");
        Assert.Null(recovered.State);
        Assert.Equal((byte)7, Assert.IsType<Node>(reopened.ReadEvent(recovered.Head)).Value);
        recovered.CommitState(new Node { Value = 11 }, NoRebase);
        Assert.Equal((byte)11, Assert.IsType<Node>(recovered.State).Value);
    }

    [Fact]
    public void EventOnlyCaptureReentryAndDisposeRejectionKeepCheckoutUsable() {
        Action? duringCapture = null;
        using Repository repository = CreateRepository(Models(_ => duringCapture?.Invoke()));
        using BranchCheckout checkout = repository.CreateBranchFromEvent("main", new Node(), NoRebase);
        var initial = checkout.Head;
        duringCapture = () => checkout.CommitEvent(new Node(), NoRebase);
        Assert.Throws<InvalidOperationException>(() => checkout.CommitState(new Node(), NoRebase));
        Assert.False(repository.IsFaulted);
        Assert.Null(checkout.State);
        Assert.Equal(initial, checkout.Head);
        duringCapture = () => {
            Assert.Throws<InvalidOperationException>(() => checkout.Dispose());
            Assert.Throws<InvalidOperationException>(() => repository.Dispose());
            Assert.Throws<InvalidOperationException>(() => repository.Checkout("main"));
        };
        Node state = new() { Value = 3 };
        checkout.CommitState(state, NoRebase);
        Assert.Same(state, checkout.State);
        duringCapture = null;
        checkout.CommitState(NoRebase);
        checkout.Dispose();
        checkout.Dispose();
        Assert.Throws<ObjectDisposedException>(() => checkout.CommitEvent(new Node(), NoRebase));
        Assert.Throws<ObjectDisposedException>(() => checkout.CommitState(new Node(), NoRebase));
        using BranchCheckout replacement = repository.Checkout("main");
        Assert.Equal((byte)3, Assert.IsType<Node>(replacement.State).Value);
    }

    [Fact]
    public void InitialEventCaptureReentryAndReadonlyCreationRejectWithoutPublishingOrWriting() {
        Repository? current = null;
        bool reenter = true;
        using (Repository repository = CreateRepository(Models(_ => {
            if (reenter) current!.CreateBranchFromEvent("nested", new Node(), NoRebase);
        }))) {
            current = repository;
            Assert.Throws<InvalidOperationException>(() => repository.CreateBranchFromEvent("main", new Node(), NoRebase));
            Assert.False(repository.IsFaulted);
            Assert.Empty(repository.ListBranches());
            reenter = false;
            using BranchCheckout checkout = repository.CreateBranchFromEvent("main", new Node { Value = 4 }, NoRebase);
            Assert.Null(checkout.State);
        }
        var before = SnapshotFiles();
        using (Repository reader = Repository.OpenReadOnlyExisting(_root, Models())) {
            Assert.Throws<InvalidOperationException>(() => reader.CreateBranchFromEvent("forbidden", new Node(), NoRebase));
            Assert.Throws<InvalidOperationException>(() => reader.Checkout("main"));
            Assert.Equal((byte)4, Assert.IsType<Node>(reader.ReadEvent(reader.GetHead("main"))).Value);
        }
        AssertFiles(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EventOnlyOrphanWithEventRevisionAsGraphParentFailsStrictOpenWithoutRepair(bool orphan) {
        FrameAddress eventRevision;
        ObjectId rootId;
        using (Repository repository = CreateRepository()) {
            using BranchCheckout checkout = repository.CreateBranchFromEvent("main", new Node(), NoRebase);
            eventRevision = checkout.Head.RevisionAddress;
            rootId = checkout.Head.RootId;
        }
        FrameAddress invalid;
        using (SegmentStore segments = OpenState()) {
            using StateRevisionStore states = new(segments);
            // Journal ancestry has no State. Even a valid Event graph cannot become
            // the graph baseline of its next Event or first State.
            invalid = states.AppendDurably(StateRevision.CreateObjectHeadMapDelta(eventRevision, [], []));
        }
        using (HistoryJournal history = HistoryJournal.Open(_root, readOnly: false)) {
            history.ConfirmDurable();
            var branch = history.Journal.OpenBranch("main").Unwrap();
            var prior = history.Journal.GetHead(branch);
            var appended = history.Append(GraphFrameKind.State, invalid, rootId, prior);
            if (!orphan) history.Journal.AdvanceRef(branch, prior, appended).Unwrap();
        }
        var before = SnapshotFiles();
        Assert.Throws<InvalidDataException>(() => Repository.OpenReadOnlyExisting(_root, Models()));
        AssertFiles(before);
        Assert.Throws<InvalidDataException>(() => Repository.OpenExisting(_root, Models()));
        AssertFiles(before);
    }

    [Fact]
    public void EventOnlyPhysicalOrphanWithMissingLogicalAncestorFailsStrictOpenWithoutRepair() {
        CheckpointAddress first;
        using (Repository repository = CreateRepository()) {
            using BranchCheckout checkout = repository.CreateBranchFromEvent("main", new Node(), NoRebase);
            first = checkout.Head;
        }
        // Append a complete, checksum-valid physical orphan directly: its parent coordinate
        // points inside the earlier Event rather than at a committed Journal record.
        var missingParent = first.Address with {
            Ticket = SizedPtr.Create(first.Address.Ticket.Offset + 4, first.Address.Ticket.Length),
        };
        byte[] payload = GraphEnvelopeCodec.Encode(first.RevisionAddress, first.RootId);
        var header = new Atelia.EventJournal.EventFrameHeader(
            Atelia.EventJournal.EventPayloadCodecId.Identity, 2, 0, (uint)GraphFrameKind.Event,
            default, (uint)payload.Length, missingParent);
        Span<byte> tailMeta = stackalloc byte[Atelia.EventJournal.EventFrameHeaderCodec.FixedLength];
        Atelia.EventJournal.EventFrameHeaderCodec.Encode(in header, tailMeta);
        string path = Assert.Single(Directory.GetFiles(Path.Combine(_root, "journal", "events"), "*.rbf", SearchOption.AllDirectories));
        using (IRbfFile events = RbfFile.OpenExisting(path)) {
            events.Append(Journal.EventFrameTag, payload, tailMeta).Unwrap();
            events.DurableFlush();
        }
        var before = SnapshotFiles();
        Assert.ThrowsAny<Exception>(() => Repository.OpenReadOnlyExisting(_root, Models()));
        AssertFiles(before);
        Assert.ThrowsAny<Exception>(() => Repository.OpenExisting(_root, Models()));
        AssertFiles(before);
    }
}
