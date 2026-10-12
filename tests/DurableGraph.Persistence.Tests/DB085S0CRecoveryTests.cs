using Atelia.Data;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.Rbf.Internal;
using Atelia.EventJournal;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;
using JournalStore = Atelia.EventJournal.EventJournal;
using FrameAddress = Atelia.DurableGraph.Storage.FrameAddress;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class DB085S0CRecoveryTests {
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void ActualPartialPayloadOrMissingFenceTraversesRecoveryAction(int presentFenceBytes) {
        using var fixture = new DB085S0CFixture();
        var completeBefore = fixture.Snapshot();
        // Negative selects a complete HeadLen plus four payload bytes. Otherwise the
        // real frame write stops at its final Fence, retaining exactly 0, 1 or 3 bytes.
        fixture.PartialWrite("state", requested => presentFenceBytes < 0 ? 8 : requested - 4 + presentFenceBytes);
        var report = RepositoryRecovery.Recover(fixture.Root, SharedReadModel.Models());
        using var deliveredWriter = report.Repository;
        Assert.Equal("Succeeded", report.Status.ToString());
        Assert.Equal("Qualified", report.Qualification!.Status.ToString());
        Assert.True(report.ActionAttempted);
        Assert.True(report.LengthChanged);
        Assert.True(report.BarrierCompleted);
        var repository = Assert.IsType<Repository>(report.Repository);
        fixture.AssertLockHeld();
        repository.Dispose();
        fixture.AssertUnchanged(completeBefore);
        using var continued = Repository.OpenExisting(fixture.Root, SharedReadModel.Models());
        fixture.ContinueAndColdOpen(continued);
        fixture.AssertLockReleased();
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("state")]
    [InlineData("event")]
    [InlineData("ref")]
    public void ActualPartialWriteIsRepairedWithoutChangingAnyCompleteFact(string role) {
        using var fixture = new DB085S0CFixture();
        var before = fixture.Snapshot();
        fixture.PartialWrite(role);
        Assert.ThrowsAny<Exception>(() => Repository.OpenExisting(fixture.Root, SharedReadModel.Models()).Dispose());
        var report = RepositoryRecovery.Recover(fixture.Root, SharedReadModel.Models());
        using var deliveredWriter = report.Repository;
        Assert.Equal("Succeeded", report.Status.ToString());
        Assert.Equal("Qualified", report.Qualification!.Status.ToString());
        Assert.True(report.ActionAttempted);
        Assert.True(report.LengthChanged);
        Assert.True(report.BarrierCompleted);
        Assert.True(report.SourceMayHaveChanged);
        var repository = Assert.IsType<Repository>(report.Repository);
        fixture.AssertLockHeld();
        Assert.Equal(fixture.Head.RevisionAddress, repository.GetHead("main").RevisionAddress);
        repository.Dispose();
        fixture.AssertUnchanged(before);
        using var continued = Repository.OpenExisting(fixture.Root, SharedReadModel.Models());
        fixture.ContinueAndColdOpen(continued);
        fixture.AssertLockReleased();
    }

    [Fact]
    public void CompleteOrphansArePreservedAndNoActionPathStillConfirmsAllFiles() {
        using var fixture = new DB085S0CFixture();
        FrameAddress orphan;
        using (var segments = SegmentStore.OpenExisting(Path.Combine(fixture.Root, "state"))) {
            using var states = new StateRevisionStore(segments);
            orphan = states.AppendDurably(StateRevision.CreateObjectHeadMapBase(fixture.Head.RevisionAddress, [], states.ReadLiveObjectHeadMap(fixture.Head.RevisionAddress)));
        }
        EventAddress orphanEvent;
        using (var journal = JournalStore.OpenExisting(Path.Combine(fixture.Root, "journal"))) {
            orphanEvent = journal.AppendEventFrame(fixture.Head.Address, GraphEnvelopeCodec.Encode(orphan, fixture.Head.RootId), (uint)GraphFrameKind.Event).Unwrap();
        }
        var before = fixture.Snapshot();
        var stages = new List<string>();
        RepositoryRecovery.Checkpoint = stages.Add;
        try {
            var report = RepositoryRecovery.Recover(fixture.Root, SharedReadModel.Models());
            using var deliveredWriter = report.Repository;
            Assert.Equal("Succeeded", report.Status.ToString());
            Assert.False(report.ActionAttempted);
            Assert.False(report.LengthChanged);
            var confirmed = stages.Where(s => s.StartsWith("AfterConfirmFile:", StringComparison.Ordinal)).ToArray();
            Assert.Contains(confirmed, s => s.EndsWith("schemas.rbf", StringComparison.Ordinal));
            Assert.Contains(confirmed, s => s.Contains(":state/", StringComparison.Ordinal));
            Assert.Contains(confirmed, s => s.Contains(":journal/events/", StringComparison.Ordinal));
            Assert.Contains(confirmed, s => s.Contains(":journal/refs/objects/", StringComparison.Ordinal));
            Assert.True(Array.FindIndex(confirmed, s => s.EndsWith("schemas.rbf", StringComparison.Ordinal)) < Array.FindIndex(confirmed, s => s.Contains(":state/", StringComparison.Ordinal)));
            Assert.True(Array.FindIndex(confirmed, s => s.Contains(":state/", StringComparison.Ordinal)) < Array.FindIndex(confirmed, s => s.Contains(":journal/events/", StringComparison.Ordinal)));
            Assert.True(Array.FindIndex(confirmed, s => s.Contains(":journal/events/", StringComparison.Ordinal)) < Array.FindIndex(confirmed, s => s.Contains(":journal/refs/objects/", StringComparison.Ordinal)));
            Assert.Equal("AfterConfirmFile:journal/refs/ref-op-log.rbf", confirmed[^1]);
            Assert.IsType<Repository>(report.Repository).Dispose();
            fixture.AssertUnchanged(before);
            using var continued = Repository.OpenExisting(fixture.Root, SharedReadModel.Models());
            fixture.ContinueAndColdOpen(continued);
        }
        finally { RepositoryRecovery.Checkpoint = null; }
        using (var segments = SegmentStore.OpenExisting(Path.Combine(fixture.Root, "state"))) {
            using var states = new StateRevisionStore(segments);
            Assert.Equal(fixture.Head.RevisionAddress, states.Read(orphan).ParentRevisionAddress);
        }
        using var freshJournal = JournalStore.OpenExisting(Path.Combine(fixture.Root, "journal"));
        using var preservedEvent = freshJournal.ReadEvent(orphanEvent).Unwrap();
        Assert.Equal(orphanEvent, preservedEvent.Address);
    }

    [Fact]
    public void LockIsContinuousAndCandidateHandlesCloseBeforeAnyMutation() {
        using var fixture = new DB085S0CFixture();
        fixture.PartialWrite("state");
        var recoveryStages = new List<string>();
        RepositoryRecoveryQualification.Checkpoint = stage => {
            fixture.AssertLockHeld();
            Assert.ThrowsAny<IOException>(() => new FileStream(fixture.FileFor("state"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
        };
        RepositoryRecovery.Checkpoint = stage => {
            recoveryStages.Add(stage);
            fixture.AssertLockHeld();
            if (stage is "CandidateResourcesDisposed" or "BeforeAction") {
                using var writable = new FileStream(fixture.FileFor("state"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
        };
        try {
            var report = RepositoryRecovery.Recover(fixture.Root, SharedReadModel.Models());
            using var deliveredWriter = report.Repository;
            Assert.Equal("Succeeded", report.Status.ToString());
            Assert.Contains("CandidateResourcesDisposed", recoveryStages);
            Assert.Contains("BeforeSetLength", recoveryStages);
            Assert.Contains("BeforeDelivery", recoveryStages);
            fixture.AssertLockHeld();
            report.Repository!.Dispose();
        }
        finally { RepositoryRecovery.Checkpoint = null; RepositoryRecoveryQualification.Checkpoint = null; }
        fixture.AssertLockReleased();
    }

    [Theory]
    [InlineData("BeforeAction", "PrecheckRefused", false, false)]
    [InlineData("BeforeSetLength", "PrecheckRefused", false, false)]
    [InlineData("AfterSetLengthBeforeFlush", "ActionFailed", true, false)]
    [InlineData("AfterAction", "ActionFailed", true, true)]
    [InlineData("BeforeStrictOpen", "StrictOpenFailed", true, true)]
    [InlineData("AfterHistoryOpen", "StrictOpenFailed", true, true)]
    [InlineData("AfterGraphResourcesOpen", "StrictOpenFailed", true, true)]
    [InlineData("BeforeJournalConfirmation", "StrictOpenFailed", true, true)]
    [InlineData("BeforeDelivery", "DeliveryFailed", true, true)]
    public void InterruptionNeverDeliversWriterAndRetryUsesDiskState(string stage, string status, bool changed, bool barrier) {
        using var fixture = new DB085S0CFixture();
        fixture.PartialWrite("state");
        var before = fixture.Snapshot();
        int reached = 0;
        RepositoryRecovery.Checkpoint = current => { if (current == stage) { reached++; throw new IOException("Injected recovery interruption at " + stage); } };
        try {
            var report = RepositoryRecovery.Recover(fixture.Root, SharedReadModel.Models());
            using var deliveredWriter = report.Repository;
            Assert.Equal(1, reached);
            Assert.Equal(status, report.Status.ToString());
            Assert.Null(report.Repository);
            Assert.Equal(changed, report.LengthChanged);
            Assert.Equal(barrier, report.BarrierCompleted);
            if (!changed) { fixture.AssertUnchanged(before); }
            else {
                foreach (var pair in before) {
                    byte[] after = File.ReadAllBytes(pair.Key);
                    Assert.Equal(pair.Value.AsSpan(0, after.Length).ToArray(), after);
                    Assert.Equal(pair.Key == fixture.FileFor("state") ? pair.Value.Length - 2 : pair.Value.Length, after.Length);
                }
            }
        }
        finally { RepositoryRecovery.Checkpoint = null; }
        fixture.AssertLockReleased();
        var retry = RepositoryRecovery.Recover(fixture.Root, SharedReadModel.Models());
        using var retryWriter = retry.Repository;
        Assert.Equal("Succeeded", retry.Status.ToString());
        Assert.Equal(!changed, retry.LengthChanged);
        fixture.ContinueAndColdOpen(Assert.IsType<Repository>(retry.Repository));
    }

    [Theory]
    [InlineData("BeforeAction", false)]
    [InlineData("AfterSetLengthBeforeFlush", true)]
    [InlineData("BeforeDelivery", true)]
    public void CancellationKeepsAccurateMutationOutcomeAndClosesResources(string stage, bool changed) {
        using var fixture = new DB085S0CFixture();
        fixture.PartialWrite("ref");
        using var cancellation = new CancellationTokenSource();
        int reached = 0;
        RepositoryRecovery.Checkpoint = current => { if (current == stage) { reached++; cancellation.Cancel(); } };
        try {
            var report = RepositoryRecovery.Recover(fixture.Root, SharedReadModel.Models(), cancellationToken: cancellation.Token);
            using var deliveredWriter = report.Repository;
            Assert.Equal(1, reached);
            Assert.NotEqual("Succeeded", report.Status.ToString());
            Assert.Contains("cancel", report.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.True(report.Cancelled);
            Assert.Equal(changed, report.CancellationAfterMutation);
            Assert.Equal(changed, report.SourceMayHaveChanged);
            Assert.Null(report.Repository);
            Assert.Equal(changed, report.LengthChanged);
            if (changed) { Assert.True(report.SourceMayHaveChanged); }
        }
        finally { RepositoryRecovery.Checkpoint = null; }
        fixture.AssertLockReleased();
        var retry = RepositoryRecovery.Recover(fixture.Root, SharedReadModel.Models());
        using var retryWriter = retry.Repository;
        Assert.Equal("Succeeded", retry.Status.ToString());
        fixture.ContinueAndColdOpen(Assert.IsType<Repository>(retry.Repository));
    }

    [Theory]
    [InlineData("budget", "BudgetExceeded")]
    [InlineData("cancel", "Cancelled")]
    [InlineData("io", "IoFailure")]
    public void QualificationFailuresCannotMutateOrRetainWriter(string failure, string expected) {
        using var fixture = new DB085S0CFixture();
        fixture.PartialWrite("schema");
        var before = fixture.Snapshot();
        using var cancellation = new CancellationTokenSource();
        if (failure == "cancel") { cancellation.Cancel(); }
        if (failure == "io") { RepositoryRecoveryQualification.Checkpoint = _ => throw new IOException("Injected qualification I/O."); }
        try {
            var report = RepositoryRecovery.Recover(fixture.Root, SharedReadModel.Models(), failure == "budget" ? 0 : long.MaxValue, cancellation.Token);
            using var deliveredWriter = report.Repository;
            Assert.NotEqual("Succeeded", report.Status.ToString());
            if (report.Qualification is not null) { Assert.Equal(expected, report.Qualification.Status.ToString()); }
            Assert.Null(report.Repository);
            Assert.False(report.ActionAttempted);
            Assert.False(report.LengthChanged);
            Assert.False(report.SourceMayHaveChanged);
            fixture.AssertUnchanged(before);
        }
        finally { RepositoryRecoveryQualification.Checkpoint = null; }
        fixture.AssertLockReleased();
    }

    [Theory]
    [InlineData("crc", "Invalid")]
    [InlineData("semantic", "Unsupported")]
    [InlineData("multiple", "Unsupported")]
    [InlineData("unknown", "Unsupported")]
    [InlineData("tag", "Unsupported")]
    [InlineData("fork", "Unsupported")]
    [InlineData("trailer", "Unresolved")]
    [InlineData("orphan-parent", "Invalid")]
    public void RejectedFactsNeverAuthorizeTailRemoval(string damage, string qualification) {
        using var fixture = new DB085S0CFixture();
        string path = fixture.FileFor("state");
        if (damage == "crc") {
            byte[] bytes = File.ReadAllBytes(path); bytes[checked((int)fixture.Head.RevisionAddress.FrameTicket.Offset + 4)] ^= 1;
            File.WriteAllBytes(path, bytes);
        }
        if (damage == "semantic") {
            using var writer = RbfFile.OpenExisting(fixture.FileFor("schema"));
            writer.Append(SchemaCatalogWireCodec.RbfTag, [255]).Unwrap(); writer.DurableFlush();
        }
        if (damage == "multiple") { fixture.PartialWrite("event"); }
        if (damage == "unknown") { File.WriteAllText(Path.Combine(fixture.Root, "unknown"), "outside scope"); }
        if (damage is "tag" or "fork") {
            using var repository = Repository.OpenExisting(fixture.Root, SharedReadModel.Models());
            var head = repository.GetHead("main");
            if (damage == "tag") { repository.CreateTag("tag", head); }
            else { repository.CreateBranch("other", head); }
        }
        if (damage == "orphan-parent") {
            using var segments = SegmentStore.OpenExisting(Path.Combine(fixture.Root, "state"));
            using var states = new StateRevisionStore(segments);
            states.AppendDurably(StateRevision.CreateObjectHeadMapBase(new FrameAddress(1, SizedPtr.Create(4, 24)), [], []));
        }
        if (damage == "trailer") {
            SizedPtr ticket;
            using (var writer = RbfFile.OpenExisting(path)) { ticket = writer.Append(0xF200, new byte[64]).Unwrap(); writer.DurableFlush(); }
            byte[] bytes = File.ReadAllBytes(path);
            File.WriteAllBytes(path, bytes[..checked((int)(ticket.Offset + ticket.Length - 16 + 1))]);
        }
        else { fixture.PartialWrite("state"); }
        var before = fixture.Snapshot();
        var report = RepositoryRecovery.Recover(fixture.Root, SharedReadModel.Models());
        using var deliveredWriter = report.Repository;
        Assert.Equal("QualificationRefused", report.Status.ToString());
        Assert.Equal(qualification, report.Qualification!.Status.ToString());
        Assert.Null(report.Repository);
        Assert.False(report.ActionAttempted);
        Assert.False(report.LengthChanged);
        Assert.False(report.BarrierCompleted);
        fixture.AssertUnchanged(before);
        fixture.AssertLockReleased();
    }

    [Theory]
    [InlineData((int)CommitCheckpoint.AfterStateDurable, false)]
    [InlineData((int)CommitCheckpoint.AfterJournalDurable, false)]
    [InlineData((int)CommitCheckpoint.AfterPublication, true)]
    [InlineData((int)CommitCheckpoint.BeforeInstall, true)]
    public void CompleteFactsAfterInterruptedCommitSurviveRecoveryWithoutTail(int checkpoint, bool published) {
        using var fixture = new DB085S0CFixture();
        FrameAddress candidate;
        using (var writer = Repository.OpenExisting(fixture.Root, SharedReadModel.Models())) {
            using var branch = writer.Checkout("main");
            Assert.IsType<SharedReadModel.Node>(branch.State).Value = 7;
            writer.Checkpoint = stage => { if ((int)stage == checkpoint) { throw new IOException("Commit interrupted after complete durable fact."); } };
            var failure = Assert.Throws<GraphCommitException>(() => branch.CommitState(DB085S0CFixture.NoRebase));
            candidate = failure.CandidateRevisionAddress!.Value;
            Assert.True(writer.IsFaulted);
        }
        // Captured after the failing call: includes complete unacknowledged facts/orphans.
        var afterInterruption = fixture.Snapshot();
        var report = RepositoryRecovery.Recover(fixture.Root, SharedReadModel.Models());
        using var deliveredWriter = report.Repository;
        Assert.Equal("Succeeded", report.Status.ToString());
        Assert.True(report.NoAction);
        Assert.False(report.ActionAttempted);
        var repository = Assert.IsType<Repository>(report.Repository);
        Assert.Equal(published ? candidate : fixture.Head.RevisionAddress, repository.GetHead("main").RevisionAddress);
        repository.Dispose();
        fixture.AssertUnchanged(afterInterruption);
        using var continued = Repository.OpenExisting(fixture.Root, SharedReadModel.Models());
        fixture.ContinueAndColdOpen(continued, published ? (byte)7 : (byte)1);
        using var segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(fixture.Root, "state"));
        using var states = new StateRevisionStore(segments);
        Assert.Equal(fixture.Head.RevisionAddress, states.Read(candidate).ParentRevisionAddress);
        foreach (var pair in afterInterruption) {
            byte[] current = File.ReadAllBytes(pair.Key);
            Assert.Equal(pair.Value, current.AsSpan(0, pair.Value.Length).ToArray());
        }
    }

    [Theory]
    [InlineData("length")]
    [InlineData("inventory")]
    [InlineData("crc")]
    public void ChangedSourceAfterCandidateDisposalFailsPrecheckWithoutTruncation(string change) {
        using var fixture = new DB085S0CFixture();
        fixture.PartialWrite("state");
        Dictionary<string, byte[]>? afterChange = null;
        RepositoryRecovery.Checkpoint = stage => {
            if (stage != "CandidateResourcesDisposed") { return; }
            string path = fixture.FileFor("state");
            if (change == "length") {
                using var append = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
                append.WriteByte(0);
            }
            if (change == "inventory") { File.WriteAllText(Path.Combine(fixture.Root, "added"), "changed"); }
            if (change == "crc") {
                byte[] bytes = File.ReadAllBytes(path); bytes[checked((int)fixture.Head.RevisionAddress.FrameTicket.Offset + 4)] ^= 1;
                File.WriteAllBytes(path, bytes);
            }
            afterChange = fixture.Snapshot();
        };
        try {
            var report = RepositoryRecovery.Recover(fixture.Root, SharedReadModel.Models());
            using var deliveredWriter = report.Repository;
            Assert.NotNull(afterChange);
            Assert.Equal("PrecheckRefused", report.Status.ToString());
            Assert.Null(report.Repository);
            Assert.False(report.ActionAttempted);
            Assert.False(report.LengthChanged);
            fixture.AssertUnchanged(afterChange!);
        }
        finally { RepositoryRecovery.Checkpoint = null; }
        fixture.AssertLockReleased();
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("state")]
    [InlineData("event")]
    [InlineData("ref")]
    public void ActualConfirmationFlushFailureClosesFreshWriterAndRetryConfirmsFromBeginning(string role) {
        using var fixture = new DB085S0CFixture();
        fixture.PartialWrite("state");
        string target = Path.GetFullPath(fixture.FileFor(role));
        int injected = 0;
        RbfWriteInstrumentation.Current = new() {
            BeforeFlush = request => {
                if (string.Equals(request.Path, target, StringComparison.OrdinalIgnoreCase)) { injected++; throw new IOException("Injected actual confirmation barrier failure."); }
            },
        };
        try {
            var report = RepositoryRecovery.Recover(fixture.Root, SharedReadModel.Models());
            using var deliveredWriter = report.Repository;
            Assert.Equal(1, injected);
            Assert.Equal("StrictOpenFailed", report.Status.ToString());
            Assert.Null(report.Repository);
            Assert.True(report.LengthChanged);
            Assert.True(report.BarrierCompleted); // The truncate barrier precedes the failing dependency barrier.
        }
        finally { RbfWriteInstrumentation.Current = null; }
        fixture.AssertLockReleased();
        var stages = new List<string>();
        RepositoryRecovery.Checkpoint = stages.Add;
        try {
            var retry = RepositoryRecovery.Recover(fixture.Root, SharedReadModel.Models());
            using var retryWriter = retry.Repository;
            Assert.Equal("Succeeded", retry.Status.ToString());
            Assert.True(retry.NoAction);
            Assert.Contains("AfterConfirmFile:schemas.rbf", stages);
            Assert.Contains("AfterConfirmFile:" + Path.GetRelativePath(fixture.Root, target).Replace('\\', '/'), stages);
            fixture.ContinueAndColdOpen(Assert.IsType<Repository>(retry.Repository));
        }
        finally { RepositoryRecovery.Checkpoint = null; }
    }
}
