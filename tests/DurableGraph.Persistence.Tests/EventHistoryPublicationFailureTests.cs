using System.Reflection;
using Atelia.Data;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Journal = Atelia.EventJournal.EventJournal;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed partial class EventHistoryRepositoryTests {
    [Theory]
    [InlineData("before-create", false)]
    [InlineData("after-create", false)]
    [InlineData("init-block", false)]
    [InlineData("before-bind", false)]
    [InlineData("after-bind", true)]
    public void InitialBranchCreateInitBindFailuresAreUnknownUntilStrictReopen(string phase, bool visible) {
        using (Repository repository = CreateRepository()) {
            Journal journal = JournalOf(repository);
            FieldInfo field = typeof(Journal).GetField("_refOpLog", BindingFlags.Instance | BindingFlags.NonPublic)!;
            IRbfFile original = (IRbfFile)field.GetValue(journal)!;
            string refObjects = (string)typeof(Journal).GetField("_refObjectsPath", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(journal)!;
            // Test-only I/O interception at the actual EventJournal RefOp log. The real
            // CreateBranch still performs Create, Ref Init, and BindName in its normal order.
            field.SetValue(journal, new RefOpFaultFile(original, phase, refObjects));
            GraphCommitException failure = Assert.Throws<GraphCommitException>(() =>
                repository.CreateBranch("main", new Node { Value = 7 }, NoRebase));
            Assert.Equal(GraphCommitOutcome.Unknown, failure.Outcome);
            Assert.True(repository.IsFaulted);
            Assert.NotNull(failure.CandidateRevisionAddress);
            Assert.Throws<InvalidOperationException>(() => repository.GetHead("main"));
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        Assert.Equal(visible, reopened.ListBranches().Contains("main"));
        if (visible) {
            using var session = reopened.Checkout("main");
            Assert.Equal((byte)7, ((Node)session.State!).Value);
            Assert.Equal(GraphFrameKind.State, session.Head.Kind);
        }
    }

    [Fact]
    public void RefCasMismatchAfterJournalAppendDoesNotPublishOrInstallStateCandidate() {
        FrameAddress original;
        using (Repository repository = CreateRepository()) {
            using var session = repository.CreateBranch("main", new Node { Value = 1 }, NoRebase);
            CheckpointAddress first = session.Head;
            using BranchCheckout other = repository.Fork("other", first);
            original = session.StateRevisionAddress!.Value;
            session.CommitEvent(new Node(), NoRebase);
            ((Node)session.State!).Value = 9;
            Journal journal = JournalOf(repository);
            repository.Checkpoint = point => {
                if (point != CommitCheckpoint.BeforePublication) return;
                var branch = journal.OpenBranch("main").Unwrap();
                journal.MoveRef(branch, journal.GetHead(branch), first.Address).Unwrap();
            };
            GraphCommitException failure = Assert.Throws<GraphCommitException>(() => session.CommitState(NoRebase));
            Assert.Equal(GraphCommitOutcome.NotPublished, failure.Outcome);
            Assert.Equal(original, session.StateRevisionAddress!.Value);
            Assert.True(repository.IsFaulted);
            Assert.True(other.IsFaulted);
            Assert.Throws<InvalidOperationException>(() => other.CommitState(NoRebase));
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        using var resumed = reopened.Checkout("main");
        Assert.Equal(original, resumed.StateRevisionAddress!.Value);
        Assert.Equal((byte)1, ((Node)resumed.State!).Value);
        Assert.Equal(GraphFrameKind.State, resumed.Head.Kind);
    }

    [Theory]
    [InlineData("Upper")]
    [InlineData("bad/name")]
    [InlineData("main.lock")]
    [InlineData("trailing.")]
    public void InvalidBranchNameIsRejectedBeforeCaptureOrWrites(string name) {
        using (Repository empty = CreateRepository()) { }
        var before = SnapshotFiles();
        int captures = 0;
        using (Repository repository = Repository.OpenExisting(_root, Models(_ => captures++))) {
            Assert.ThrowsAny<Exception>(() => repository.CreateBranch(name, new Node(), NoRebase));
            Assert.Equal(0, captures);
            Assert.False(repository.IsFaulted);
            Assert.Empty(repository.ListBranches());
        }
        AssertFiles(before);
        using Repository retry = Repository.OpenExisting(_root, Models());
        using var valid = retry.CreateBranch("main", new Node(), NoRebase);
    }

    [Theory]
    [InlineData((int)CommitCheckpoint.AfterPublication)]
    [InlineData((int)CommitCheckpoint.BeforeInstall)]
    public void PublishedEventFailureReopensAsPendingWithoutAdvancingStateBaseline(int point) {
        FrameAddress state;
        using (Repository repository = CreateRepository()) {
            using var session = repository.CreateBranch("main", new Node { Value = 1 }, NoRebase);
            state = session.StateRevisionAddress!.Value;
            repository.Checkpoint = checkpoint => {
                if (checkpoint == (CommitCheckpoint)point) throw new IOException("Event published but completion interrupted");
            };
            var failure = Assert.Throws<GraphCommitException>(() => session.CommitEvent(new Node { Value = 8 }, NoRebase));
            Assert.Equal(GraphCommitOutcome.Published, failure.Outcome);
            Assert.Equal(state, session.StateRevisionAddress!.Value);
            Assert.True(repository.IsFaulted);
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        using var resumed = reopened.Checkout("main");
        Assert.Equal(state, resumed.StateRevisionAddress!.Value);
        Assert.Equal((byte)1, ((Node)resumed.State!).Value);
        Assert.Equal((byte)8, ((Node)reopened.ReadEvent(resumed.Head)).Value);
        resumed.CommitState(NoRebase);
        Assert.Equal(GraphFrameKind.State, resumed.Head.Kind);
    }

    private static Journal JournalOf(Repository repository) =>
        ((HistoryJournal)typeof(Repository).GetField("_history", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(repository)!).Journal;

    private sealed class RefOpFaultFile(IRbfFile inner, string phase, string refObjects) : IRbfFile {
        private int _appends;
        private SizedPtr _ticket;
        public long TailOffset => inner.TailOffset;
        public AteliaResult<SizedPtr> Append(uint tag, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> tailMeta = default) {
            _appends++;
            if (phase == "before-create" && _appends == 1 || phase == "before-bind" && _appends == 2) throw new IOException(phase);
            var result = inner.Append(tag, payload, tailMeta);
            _ticket = result.Unwrap();
            return result;
        }
        public void DurableFlush() {
            inner.DurableFlush();
            if (phase == "after-create" && _appends == 1 || phase == "after-bind" && _appends == 2) throw new IOException(phase);
            if (phase == "init-block" && _appends == 1) {
                string objectPath = Path.Combine(refObjects, new Atelia.EventJournal.RefId(_ticket.Packed).ToHexString());
                File.WriteAllText(objectPath, "Deliberately prevent Ref Init from creating its directory.");
            }
        }
        public RbfFrameBuilder BeginAppend() => inner.BeginAppend();
        public AteliaResult<RbfPooledFrame> ReadPooledFrame(SizedPtr ptr) => inner.ReadPooledFrame(ptr);
        public AteliaResult<RbfFrame> ReadFrame(SizedPtr ptr, Span<byte> buffer) => inner.ReadFrame(ptr, buffer);
        public RbfReverseSequence ScanReverse(bool showTombstone = false) => inner.ScanReverse(showTombstone);
        public RbfForwardSequence ScanForward(bool showTombstone = false) => inner.ScanForward(showTombstone);
        public long GetPhysicalOffsetImmediatelyAfter(SizedPtr ticket) => inner.GetPhysicalOffsetImmediatelyAfter(ticket);
        public AteliaResult<OptionalRbfFrameInfo> ReadFrameInfoImmediatelyAfter(SizedPtr ticket) => inner.ReadFrameInfoImmediatelyAfter(ticket);
        public AteliaResult<RbfFrameInfo> ReadFrameInfo(SizedPtr ticket) => inner.ReadFrameInfo(ticket);
        public AteliaResult<RbfTailMeta> ReadTailMeta(SizedPtr ticket, Span<byte> buffer) => inner.ReadTailMeta(ticket, buffer);
        public AteliaResult<RbfPooledTailMeta> ReadPooledTailMeta(SizedPtr ticket) => inner.ReadPooledTailMeta(ticket);
        public void Truncate(long newLengthBytes) => inner.Truncate(newLengthBytes);
        public void SetupReadLog(string? logPath) => inner.SetupReadLog(logPath);
        public void Dispose() => inner.Dispose();
    }
}
