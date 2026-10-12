using Atelia.DurableGraph.Storage;
using Atelia.Data;
using Atelia.EventJournal;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;
using JournalStore = Atelia.EventJournal.EventJournal;
using FrameAddress = Atelia.DurableGraph.Storage.FrameAddress;
using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class DB085ReadOnlyCandidateTests : IDisposable {
    private const string Prefix = "durable-db085-candidate-";
    private readonly string _root = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1000000, 1);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HealthyHistoriesQueriesAndReadsPreserveFiles(bool eventFirst) {
        using (var writer = Create()) {
            using var branch = eventFirst
                ? writer.CreateBranchFromEvent("main", new Node { Value = 1 }, NoRebase)
                : writer.CreateBranch("main", new Node { Value = 1 }, NoRebase);
            writer.CreateTag("first", branch.Head);
            branch.CommitEvent(new Node { Value = 2 }, NoRebase);
            branch.CommitState(new Node { Value = 3 }, NoRebase);
            branch.CommitEvent(new Node { Value = 4 }, NoRebase);
            using var unrelated = writer.CreateBranch("unrelated", new Node { Value = 9 }, NoRebase);
        }
        var before = Snapshot();
        using (var reader = Candidate()) {
            Assert.True(reader.IsReadOnly);
            Assert.Equal(2, reader.ListBranches().Count);
            var frames = reader.ReadFrames("main");
            Assert.Throws<ArgumentException>(() => reader.EnumerateEvents(frames[3], afterExclusive: reader.GetHead("unrelated")).ToArray());
            Assert.Equal(4, frames.Count);
            Assert.Equal(frames[0], reader.ResolveTag("first"));
            Assert.Equal(frames[3], reader.GetHead("main"));
            Assert.Equal(frames[2], reader.GetPreviousState(frames[3]));
            Assert.Equal((byte)3, Assert.IsType<Node>(reader.ReadState(frames[2])).Value);
            Assert.Equal((byte)4, Assert.IsType<Node>(reader.ReadEvent(frames[3])).Value);
            var checkpoint = Assert.IsType<EventCheckpoint>(reader.ReadCheckpoint(frames[3]));
            Assert.Equal((byte)3, Assert.IsType<Node>(checkpoint.PreviousState).Value);
            var first = reader.ReadCheckpoint(frames[0]);
            if (eventFirst) { Assert.Null(Assert.IsType<EventCheckpoint>(first).PreviousState); }
            else { Assert.Null(Assert.IsType<StateCheckpoint>(first).PreviousEvent); }
            var pair = reader.ReadPair(frames[2], frames[3]);
            Assert.Equal((byte)3, Assert.IsType<Node>(pair.First).Value);
            Assert.Equal((byte)4, Assert.IsType<Node>(pair.Second).Value);
            var same = reader.ReadPair(frames[3], frames[3]);
            Assert.Equal((byte)4, Assert.IsType<Node>(same.Second).Value);
            foreach (HistoryOrder order in new[] { HistoryOrder.NewestFirst, HistoryOrder.OldestFirst }) {
                var events = reader.EnumerateEvents(frames[3], order).ToArray();
                Assert.Equal(eventFirst ? 3 : 2, events.Length);
                foreach (var item in events) { Assert.IsType<Node>(reader.ReadEvent(item)); }
                Assert.Equal(new[] { frames[3] }, reader.EnumerateEvents(frames[3], order, frames[1]));
                Assert.Empty(reader.EnumerateEvents(frames[3], order, frames[3]));
            }
            Assert.Throws<ArgumentException>(() => reader.ReadState(frames[3]));
            Assert.Throws<ArgumentException>(() => reader.GetPreviousState(frames[2]));
            Assert.Throws<InvalidOperationException>(() => reader.Checkout("main"));
            Assert.Throws<InvalidOperationException>(() => reader.Fork("fork", frames[3]));
            Assert.Throws<InvalidOperationException>(() => reader.CreateBranch("new", frames[3]));
            Assert.Throws<InvalidOperationException>(() => reader.CreateBranch("new", new Node()));
            Assert.Throws<InvalidOperationException>(() => reader.CreateBranchFromEvent("new", new Node()));
            Assert.Throws<InvalidOperationException>(() => reader.CreateTag("new", frames[3]));
            Assert.Throws<InvalidOperationException>(() => reader.MoveBranch("main", frames[3], frames[0]));
            Assert.False(reader.IsFaulted);
        }
        AssertUnchanged(before);
    }

    [Fact]
    public void MissingModelsCrossOpenAndEnumeratorLifetimeKeepExistingGuards() {
        var old = Seed();
        using var reader = Candidate(new StateModelRegistry());
        var head = reader.GetHead("main");
        Assert.ThrowsAny<Exception>(() => reader.ReadState(head));
        Assert.Throws<ArgumentException>(() => reader.ReadState(old));
        Assert.Throws<ArgumentException>(() => reader.ReadPair(head, old));
        Assert.Throws<ArgumentException>(() => reader.ReadCheckpoint(old));
        Assert.Throws<ArgumentException>(() => reader.EnumerateEvents(old));
        using var exhausted = reader.EnumerateEvents(head).GetEnumerator();
        Assert.False(exhausted.MoveNext());
        using var pending = reader.EnumerateEvents(head).GetEnumerator();
        reader.Dispose();
        Assert.Throws<ObjectDisposedException>(() => exhausted.MoveNext());
        Assert.Throws<ObjectDisposedException>(() => pending.MoveNext());
        Assert.Throws<ObjectDisposedException>(() => reader.ListBranches());
    }

    [Theory]
    [InlineData(HistoryOrder.NewestFirst)]
    [InlineData(HistoryOrder.OldestFirst)]
    public void LiveEnumerationAllowsLoopReadsAndChecksDisposeOnNextAdvance(HistoryOrder order) {
        using (var writer = Create()) {
            using var branch = writer.CreateBranchFromEvent("main", new Node { Value = 1 }, NoRebase);
            branch.CommitEvent(new Node { Value = 2 }, NoRebase);
        }
        using var reader = Candidate();
        using var events = reader.EnumerateEvents(reader.GetHead("main"), order).GetEnumerator();
        Assert.True(events.MoveNext());
        Assert.Equal(order == HistoryOrder.NewestFirst ? (byte)2 : (byte)1, Assert.IsType<Node>(reader.ReadEvent(events.Current)).Value);
        Assert.Null(reader.GetPreviousState(events.Current));
        reader.Dispose();
        Assert.Throws<ObjectDisposedException>(() => events.MoveNext());
    }

    [Fact]
    public void ApplicationCallbacksCannotReenterOrDisposeCandidate() {
        Seed();
        Repository? current = null;
        int callbacks = 0;
        using var reader = Candidate(SharedReadModel.Models(allocate: () => {
            callbacks++;
            Assert.Throws<InvalidOperationException>(() => current!.GetHead("main"));
            Assert.Throws<InvalidOperationException>(() => current!.Dispose());
            return new();
        }));
        current = reader;
        Assert.IsType<Node>(reader.ReadState(reader.GetHead("main")));
        Assert.Equal(1, callbacks);
        Assert.False(reader.IsFaulted);
        Assert.Single(reader.ListBranches());
    }

    [Theory]
    [InlineData("parent")]
    [InlineData("revision")]
    [InlineData("root")]
    [InlineData("version")]
    [InlineData("representation")]
    public void PublishedCrcValidBadGraphIsRejectedAtHeadTagAndBeforeCallbacks(string damage) {
        var initial = Seed();
        FrameAddress revision = initial.RevisionAddress;
        if (damage == "revision") { revision = new(1, SizedPtr.Create(4, 24)); }
        if (damage is "representation" or "root" or "version") {
            using var segments = SegmentStore.OpenExisting(Path.Combine(_root, "state"));
            using var states = new StateRevisionStore(segments);
            revision = states.AppendDurably(StateRevision.CreateObjectHeadMapBase(initial.RevisionAddress,
                damage == "representation" ? [ObjectVersionRecord.CreateBase(initial.RootId.Value, [4, 127])] : [],
                damage == "representation" ? [] : states.ReadLiveObjectHeadMap(initial.RevisionAddress)));
        }
        var root = damage == "root" ? new ObjectId(uint.MaxValue) : initial.RootId;
        byte[] payload = GraphEnvelopeCodec.Encode(revision, root);
        if (damage == "version") { payload[4] = 255; }
        EventAddress bad;
        using (var journal = JournalStore.OpenExisting(Path.Combine(_root, "journal"))) {
            bad = journal.AppendEventFrame(initial.Address, payload, (uint)GraphFrameKind.Event).Unwrap();
            journal.CreateBranch("bad", bad).Unwrap();
            journal.CreateTag("bad", bad).Unwrap();
        }
        int callbacks = 0;
        var before = Snapshot();
        using (var reader = Candidate(SharedReadModel.Models(allocate: () => { callbacks++; return new(); }, hydrate: _ => callbacks++))) {
            var good = reader.GetHead("main");
            Assert.Contains("bad", reader.ListBranches());
            Assert.ThrowsAny<Exception>(() => reader.GetHead("bad"));
            Assert.ThrowsAny<Exception>(() => reader.ResolveTag("bad"));
            Assert.ThrowsAny<Exception>(() => reader.ReadFrames("bad"));
            // Only tests can construct this: exercise actual-read guards past the normal Issue rejection.
            var forged = new CheckpointAddress(good.Owner, new HistoryGraphRecord(bad, initial.Address, GraphFrameKind.Event, revision, root));
            Assert.ThrowsAny<Exception>(() => reader.ReadEvent(forged));
            Assert.ThrowsAny<Exception>(() => reader.ReadCheckpoint(forged));
            Assert.ThrowsAny<Exception>(() => reader.ReadPair(good, forged));
            Assert.ThrowsAny<Exception>(() => reader.GetPreviousState(forged));
            Assert.ThrowsAny<Exception>(() => reader.EnumerateEvents(forged).ToArray());
            Assert.Equal(0, callbacks);
            Assert.False(reader.IsFaulted);
        }
        AssertUnchanged(before);
        Assert.ThrowsAny<Exception>(() => Repository.OpenReadOnlyExisting(_root, SharedReadModel.Models()));
        Assert.False(RepositoryMaintenance.Audit(_root).IsHealthy);
    }

    [Fact]
    public void BadUnrelatedOrphanDefersToAuditAndEmptyHeadDefersUntilAccess() {
        var initial = Seed();
        using (var journal = JournalStore.OpenExisting(Path.Combine(_root, "journal"))) {
            journal.AppendEventFrame(initial.Address, GraphEnvelopeCodec.Encode(initial.RevisionAddress, initial.RootId), (uint)GraphFrameKind.Event).Unwrap();
            journal.CreateBranch("empty", null).Unwrap();
        }
        var before = Snapshot();
        using (var reader = Candidate()) {
            Assert.Equal((byte)1, Assert.IsType<Node>(reader.ReadState(reader.GetHead("main"))).Value);
            Assert.Contains("empty", reader.ListBranches());
            Assert.Throws<InvalidDataException>(() => reader.GetHead("empty"));
        }
        Assert.ThrowsAny<Exception>(() => Repository.OpenReadOnlyExisting(_root, SharedReadModel.Models()));
        Assert.False(RepositoryMaintenance.Audit(_root).IsHealthy);
        AssertUnchanged(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TargetCannotHideBadAncestorOrUseAnOlderStateBaseline(bool targetUsesOlderState) {
        CheckpointAddress first;
        CheckpointAddress latest;
        using (var writer = Create()) {
            using var branch = writer.CreateBranch("main", new Node { Value = 1 }, NoRebase);
            first = branch.Head;
            branch.CommitEvent(new Node { Value = 2 }, NoRebase);
            latest = branch.CommitState(new Node { Value = 3 }, NoRebase);
        }
        FrameAddress revision;
        using (var segments = SegmentStore.OpenExisting(Path.Combine(_root, "state"))) {
            using var states = new StateRevisionStore(segments);
            var baseline = targetUsesOlderState ? first.RevisionAddress : latest.RevisionAddress;
            revision = states.AppendDurably(StateRevision.CreateObjectHeadMapBase(baseline, [], states.ReadLiveObjectHeadMap(baseline)));
        }
        EventAddress target;
        EventAddress parent;
        using (var journal = JournalStore.OpenExisting(Path.Combine(_root, "journal"))) {
            // This ancestor reuses a root revision after a later State and thus disagrees.
            parent = targetUsesOlderState ? latest.Address : journal.AppendEventFrame(latest.Address,
                GraphEnvelopeCodec.Encode(first.RevisionAddress, first.RootId), (uint)GraphFrameKind.Event).Unwrap();
            // In the bad-ancestor case the target's own Revision Parent is the correct latest State.
            target = journal.AppendEventFrame(parent, GraphEnvelopeCodec.Encode(revision, first.RootId), (uint)GraphFrameKind.Event).Unwrap();
            journal.CreateBranch("bad", target).Unwrap();
            journal.CreateTag("bad", target).Unwrap();
        }
        int callbacks = 0;
        using var reader = Candidate(SharedReadModel.Models(allocate: () => { callbacks++; return new(); }));
        var good = reader.GetHead("main");
        Assert.Throws<InvalidDataException>(() => reader.GetHead("bad"));
        Assert.Throws<InvalidDataException>(() => reader.ResolveTag("bad"));
        var selected = new CheckpointAddress(good.Owner, new HistoryGraphRecord(target, parent, GraphFrameKind.Event, revision, first.RootId));
        Assert.Throws<InvalidDataException>(() => reader.ReadEvent(selected));
        Assert.Throws<InvalidDataException>(() => reader.ReadCheckpoint(selected));
        Assert.Throws<InvalidDataException>(() => reader.ReadPair(good, selected));
        Assert.Throws<InvalidDataException>(() => reader.GetPreviousState(selected));
        foreach (var order in new[] { HistoryOrder.NewestFirst, HistoryOrder.OldestFirst }) {
            Assert.Throws<InvalidDataException>(() => reader.EnumerateEvents(selected, order).ToArray());
        }
        Assert.Equal(0, callbacks);
    }

    [Theory]
    [InlineData("missing-parent")]
    [InlineData("non-earlier-parent")]
    [InlineData("external-dependency")]
    [InlineData("orphan-crc")]
    public void ExternalBaseHeadCannotHideItsContainingRevisionDependencies(string damage) {
        var initial = Seed();
        FrameAddress orphan;
        FrameAddress revision;
        FrameAddress dependency;
        using (var segments = SegmentStore.OpenExisting(Path.Combine(_root, "state"))) {
            using var states = new StateRevisionStore(segments);
            var missing = new FrameAddress(initial.RevisionAddress.FileNumber, SizedPtr.Create(4, 24));
            dependency = damage == "non-earlier-parent"
                ? new FrameAddress(initial.RevisionAddress.FileNumber, SizedPtr.Create(1024 * 1024, 24)) : missing;
            byte[] rootBase = states.ReadObjectBaseBody(initial.RevisionAddress, initial.RootId.Value);
            // Append checks direct Delta priors; this local Base retains correct framing and CRC.
            // O has no Journal record, but R uses O's local Base as its live root head.
            orphan = states.AppendDurably(StateRevision.CreateObjectHeadMapBase(
                damage is "external-dependency" or "orphan-crc" ? initial.RevisionAddress : dependency,
                [ObjectVersionRecord.CreateBase(initial.RootId.Value, rootBase)],
                damage == "external-dependency"
                    ? [new KeyValuePair<uint, FrameAddress>(uint.MaxValue, dependency)] : []));
            revision = states.AppendDurably(StateRevision.CreateObjectHeadMapBase(initial.RevisionAddress, [],
                [new KeyValuePair<uint, FrameAddress>(initial.RootId.Value, orphan)]));
            // The local Base completes the old object-chain traversal without inspecting O.Parent
            // or O's other declared external head. This confirms the intended narrow fixture.
            var chain = states.ReadObjectVersionChain(revision, initial.RootId.Value);
            Assert.Single(chain.Records);
            Assert.Equal(orphan, chain.Records[0].ContainingRevisionAddress);
            Assert.Equal(rootBase, chain.Records[0].Record.Body.ToArray());
        }
        EventAddress target;
        using (var journal = JournalStore.OpenExisting(Path.Combine(_root, "journal"))) {
            target = journal.AppendEventFrame(initial.Address, GraphEnvelopeCodec.Encode(revision, initial.RootId), (uint)GraphFrameKind.Event).Unwrap();
            journal.CreateBranch("bad", target).Unwrap();
            journal.CreateTag("bad", target).Unwrap();
        }
        if (damage == "orphan-crc") {
            // All Stores and caches are closed. O is followed by healthy R, preserving
            // the complete last frame while only O's payload/CRC agreement is damaged.
            Assert.True(orphan.FrameTicket.Offset < revision.FrameTicket.Offset);
            string file = Directory.GetFiles(Path.Combine(_root, "state"), "*.rbf", SearchOption.AllDirectories).Single();
            using var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            long offset = checked(orphan.FrameTicket.Offset + 4);
            stream.Position = offset;
            int value = stream.ReadByte();
            Assert.True(value >= 0);
            stream.Position = offset;
            stream.WriteByte((byte)(value ^ 1));
            stream.Flush(flushToDisk: true);
        }
        var before = Snapshot();
        int callbacks = 0;
        using (var reader = Candidate(SharedReadModel.Models(allocate: () => { callbacks++; return new(); }, hydrate: _ => callbacks++))) {
            var good = reader.GetHead("main");
            var selected = new CheckpointAddress(good.Owner,
                new HistoryGraphRecord(target, initial.Address, GraphFrameKind.Event, revision, initial.RootId));
            void Reject(Action action) {
                Exception error = damage == "non-earlier-parent"
                    ? Assert.Throws<InvalidDataException>(action)
                    : Assert.Throws<InvalidOperationException>(action);
                Assert.Equal(target.ToString(), error.Data["DurableGraph.JournalAddress"]);
                // Require dependency provenance, not an unrelated failure or model callback error.
                Assert.Contains(error.Data["DurableGraph.RevisionAddress"],
                    new object[] { orphan.ToString(), dependency.ToString() });
                if (damage == "non-earlier-parent") {
                    Assert.Equal("State dependency must precede its containing Revision.", error.Message);
                    Assert.Equal(orphan.ToString(), error.Data["DurableGraph.RevisionAddress"]);
                } else {
                    Assert.StartsWith("Cannot get value from a failed result. Error:", error.Message);
                    if (damage == "orphan-crc") {
                        Assert.Contains("CRC", error.Message, StringComparison.OrdinalIgnoreCase);
                        Assert.Equal(orphan.ToString(), error.Data["DurableGraph.RevisionAddress"]);
                    }
                }
            }
            Reject(() => reader.GetHead("bad"));
            Reject(() => reader.ResolveTag("bad"));
            Reject(() => reader.ReadEvent(selected));
            Reject(() => reader.ReadCheckpoint(selected));
            Reject(() => reader.ReadPair(good, selected));
            Assert.Equal(0, callbacks);
            Assert.False(reader.IsFaulted);
        }
        var audit = RepositoryMaintenance.Audit(_root);
        Assert.False(audit.IsHealthy);
        Assert.Contains(audit.Findings, finding => finding.Code == (damage == "orphan-crc" ? "StateFrameInvalid" : "StateDependencyInvalid") &&
            finding.Kind == RepositoryAuditFindingKind.InvalidData && finding.Offset == orphan.FrameTicket.Offset &&
            finding.RelativePath.StartsWith("state/") &&
            (damage == "orphan-crc" ? finding.Message.Contains("CRC", StringComparison.OrdinalIgnoreCase)
                : finding.Message.Contains("preceding complete physical frames")));
        AssertUnchanged(before);
    }

    private Repository Create() => Repository.CreateNew(_root, SharedReadModel.Models(), new() { NewStoreLayout = RbfSegmentStoreLayout.Flat });
    private Repository Candidate(StateModelRegistry? models = null) => Repository.OpenReadOnlyCandidate(_root, models ?? SharedReadModel.Models());
    private CheckpointAddress Seed() {
        using var writer = Create();
        using var branch = writer.CreateBranch("main", new Node { Value = 1 }, NoRebase);
        return branch.Head;
    }
    private Dictionary<string, byte[]> Snapshot() => Directory.GetFiles(_root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
    private void AssertUnchanged(Dictionary<string, byte[]> before) {
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order());
        foreach (var pair in before) { Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key)); }
    }
    public void Dispose() => SharedReadModel.DeleteFixture(_root, Prefix);
}
