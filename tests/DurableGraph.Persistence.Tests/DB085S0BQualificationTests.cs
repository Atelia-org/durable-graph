using System.Buffers;
using Atelia.Data;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using Atelia.EventJournal;
using JournalStore = Atelia.EventJournal.EventJournal;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;
using FrameAddress = Atelia.DurableGraph.Storage.FrameAddress;
using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class DB085S0BQualificationTests : IDisposable {
    private const string Prefix = "durable-db085-s0b-";
    private readonly string _root = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1000000, 1);
    private CheckpointAddress Seed(bool commits = false) {
        using var repository = Repository.CreateNew(_root, SharedReadModel.Models(), new() { NewStoreLayout = RbfSegmentStoreLayout.Flat });
        using var branch = repository.CreateBranch("main", new Node { Value = 1 }, NoRebase);
        if (commits) { branch.CommitEvent(new Node { Value = 2 }, NoRebase); branch.CommitState(new Node { Value = 3 }, NoRebase); }
        return branch.Head;
    }
    private string FileFor(string role) => role == "schema" ? Path.Combine(_root, "schemas.rbf") :
        Directory.GetFiles(Path.Combine(_root, role == "event" ? "journal/events" : role == "ref" ? "journal/refs/objects" : "state"), "*.rbf", SearchOption.AllDirectories).Single();
    private RecoveryQualificationReport QualifyUnchanged(long budget = long.MaxValue, CancellationToken token = default) {
        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        var report = RepositoryRecoveryQualification.Qualify(_root, budget, token);
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order());
        foreach (var pair in before) { Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key)); }
        // Every success and failure releases its candidate handles and the exclusive lock.
        using var held = new FileStream(Path.Combine(_root, "repository.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        return report;
    }
    [Fact]
    public void CompleteRepositoryAndValidOrphansQualifyWithoutModelsOrWrites() {
        var head = Seed(commits: true);
        FrameAddress orphan;
        using (var segments = SegmentStore.OpenExisting(Path.Combine(_root, "state"))) {
            using var states = new StateRevisionStore(segments);
            orphan = states.AppendDurably(StateRevision.CreateObjectHeadMapBase(head.RevisionAddress, [], states.ReadLiveObjectHeadMap(head.RevisionAddress)));
        }
        using (var journal = JournalStore.OpenExisting(Path.Combine(_root, "journal"))) {
            journal.AppendEventFrame(head.Address, GraphEnvelopeCodec.Encode(orphan, head.RootId), (uint)GraphFrameKind.Event).Unwrap();
        }
        var report = QualifyUnchanged();
        Assert.Equal(RecoveryQualificationStatus.Qualified, report.Status);
        Assert.Equal(4, report.StateFrames);
        Assert.Equal(4, report.JournalEvents);
        Assert.True(report.Physical.Reads.RawRequestedBytes > 0);
        Assert.True(report.Candidate.Reads.RawRequestedBytes > 0);
        Assert.True(report.Dependencies.Reads.RawRequestedBytes > 0);
        Assert.Equal(report.Physical.Reads.RawRequestedBytes + report.Candidate.Reads.RawRequestedBytes + report.Dependencies.Reads.RawRequestedBytes, report.Total.Reads.RawRequestedBytes);
    }
    [Theory]
    [InlineData("schema")]
    [InlineData("state")]
    [InlineData("event")]
    [InlineData("ref")]
    public void RealAppendShortPrefixKeepsEveryCompleteFact(string role) {
        Seed(commits: true);
        string path = FileFor(role);
        byte[] original = File.ReadAllBytes(path);
        SizedPtr ticket;
        using (var writer = RbfFile.OpenExisting(path)) { ticket = writer.Append(0xF200, new byte[64]).Unwrap(); writer.DurableFlush(); }
        byte[] final = File.ReadAllBytes(path);
        // Closed fixture materializes the actual Append image's partial HeadLen.
        File.WriteAllBytes(path, final[..checked((int)ticket.Offset + 2)]);
        var report = QualifyUnchanged();
        Assert.Equal(RecoveryQualificationStatus.Qualified, report.Status);
        var file = Assert.Single(report.Files, f => f.RelativePath == Path.GetRelativePath(_root, path).Replace('\\', '/'));
        Assert.Equal(original.LongLength, file.VisibleEof);
        Assert.Equal(original.LongLength + 2, file.OriginalLength);
    }
    [Theory]
    [InlineData("parent")]
    [InlineData("external")]
    [InlineData("representation")]
    [InlineData("wire-version")]
    public void CompleteOrphanStateCannotHideInvalidDependenciesBeforeShortTail(string damage) {
        var head = Seed();
        using (var segments = SegmentStore.OpenExisting(Path.Combine(_root, "state"))) {
            using var states = new StateRevisionStore(segments);
            var absent = new FrameAddress(1, SizedPtr.Create(4, 24));
            if (damage == "wire-version") {
                using var writer = segments.OpenActiveWriter(); writer.File.Append(StateRevisionStore.RbfTag, [255]).Unwrap(); writer.File.DurableFlush();
            }
            else {
                states.AppendDurably(StateRevision.CreateObjectHeadMapBase(damage == "parent" ? absent : head.RevisionAddress,
                    damage == "representation" ? [ObjectVersionRecord.CreateBase(1, [4, 127])] : [],
                    damage == "external" ? [new KeyValuePair<uint, FrameAddress>(1, absent)] : []));
            }
        }
        AddShortTail(FileFor("state"));
        var report = QualifyUnchanged();
        Assert.Equal(damage == "wire-version" ? RecoveryQualificationStatus.Unsupported : RecoveryQualificationStatus.Invalid, report.Status);
    }
    [Theory]
    [InlineData("revision")]
    [InlineData("root")]
    [InlineData("parent")]
    public void CompleteOrphanJournalCannotHideInvalidGraph(string damage) {
        var head = Seed();
        using (var journal = JournalStore.OpenExisting(Path.Combine(_root, "journal"))) {
            journal.AppendEventFrame(damage == "parent" ? head.Address : null,
                GraphEnvelopeCodec.Encode(damage == "revision" ? new FrameAddress(1, SizedPtr.Create(4, 24)) : head.RevisionAddress,
                    damage == "root" ? new ObjectId(uint.MaxValue) : head.RootId), (uint)GraphFrameKind.Event).Unwrap();
        }
        Assert.Equal(RecoveryQualificationStatus.Invalid, QualifyUnchanged().Status);
    }
    [Fact]
    public void RandomReadableNestedStateIsExcludedFromPhysicalMembership() {
        var head = Seed();
        string statePath = FileFor("state");
        var ticket = head.RevisionAddress.FrameTicket;
        byte[] bytes = File.ReadAllBytes(statePath);
        byte[] embedded = bytes.AsSpan(checked((int)ticket.Offset), checked(ticket.Length + 4)).ToArray();
        using (var segments = SegmentStore.OpenExisting(Path.Combine(_root, "state"))) {
            using var states = new StateRevisionStore(segments);
            for (int padding = 0; padding < 4; padding++) {
                byte[] body = new byte[2 + padding + embedded.Length]; body[0] = 4; body[1] = 1;
                embedded.CopyTo(body, 2 + padding);
                states.AppendDurably(StateRevision.CreateObjectHeadMapBase(null, [ObjectVersionRecord.CreateBase(1, body)], []));
            }
        }
        bytes = File.ReadAllBytes(statePath);
        int nestedOffset = -1;
        for (int index = checked((int)(ticket.Offset + ticket.Length + 4)); index <= bytes.Length - embedded.Length; index += 4) {
            if (bytes.AsSpan(index, embedded.Length).SequenceEqual(embedded)) { nestedOffset = index; break; }
        }
        Assert.True(nestedOffset >= 0);
        var nested = new FrameAddress(1, SizedPtr.Create(nestedOffset, ticket.Length));
        using (var file = RbfFile.OpenReadOnlyExisting(statePath)) { using var readable = file.ReadPooledFrame(nested.FrameTicket).Unwrap(); }
        using (var journal = JournalStore.OpenExisting(Path.Combine(_root, "journal"))) {
            journal.AppendEventFrame(null, GraphEnvelopeCodec.Encode(nested, head.RootId), (uint)GraphFrameKind.State).Unwrap();
        }
        var report = QualifyUnchanged();
        Assert.Equal(RecoveryQualificationStatus.Invalid, report.Status);
        Assert.Contains("absent from complete physical State", report.Detail);
    }
    [Fact]
    public void CompleteSchemaSemanticErrorCannotBeHiddenByShortSuffix() {
        Seed();
        using (var writer = RbfFile.OpenExisting(FileFor("schema"))) { writer.Append(SchemaCatalogWireCodec.RbfTag, [255]).Unwrap(); writer.DurableFlush(); }
        AddShortTail(FileFor("schema"));
        Assert.Equal(RecoveryQualificationStatus.Unsupported, QualifyUnchanged().Status);
    }
    [Theory]
    [InlineData("tag")]
    [InlineData("fork")]
    [InlineData("unknown")]
    [InlineData("log-tail")]
    public void OutOfScopeFactsAreExplicitlyRejected(string role) {
        var head = Seed();
        if (role is "tag" or "fork") {
            using var repository = Repository.OpenExisting(_root, SharedReadModel.Models());
            var current = repository.GetHead("main");
            if (role == "tag") { repository.CreateTag("bookmark", current); }
            else { repository.CreateBranch("other", current); }
        }
        if (role == "unknown") { File.WriteAllText(Path.Combine(_root, "unrecognized"), "unknown"); }
        if (role == "log-tail") { AddShortTail(Path.Combine(_root, "journal", "refs", "ref-op-log.rbf")); }
        Assert.Equal(RecoveryQualificationStatus.Unsupported, QualifyUnchanged().Status);
    }
    [Fact]
    public void SharedBudgetChargesPhysicalCandidateAndDependencyReads() {
        Seed(commits: true);
        var full = QualifyUnchanged();
        Assert.Equal(RecoveryQualificationStatus.Qualified, full.Status);
        Assert.Equal(RecoveryQualificationStatus.BudgetExceeded, QualifyUnchanged(0).Status);
        var afterPhysical = QualifyUnchanged(full.Physical.Reads.RawRequestedBytes);
        Assert.Equal(RecoveryQualificationStatus.BudgetExceeded, afterPhysical.Status);
        Assert.Equal(0, afterPhysical.Dependencies.Reads.RawRequestedBytes);
        var inDependencies = QualifyUnchanged(full.Physical.Reads.RawRequestedBytes + full.Candidate.Reads.RawRequestedBytes);
        Assert.Equal(RecoveryQualificationStatus.BudgetExceeded, inDependencies.Status);
        Assert.Equal(RecoveryQualificationStatus.Qualified, QualifyUnchanged(full.Total.Reads.RawRequestedBytes).Status);
    }
    [Fact]
    public void LockAndSourceGuardsRemainHeldAcrossAllPhasesAndCancellation() {
        Seed();
        using var cancellation = new CancellationTokenSource();
        int checkpoints = 0;
        RepositoryRecoveryQualification.Checkpoint = stage => {
            checkpoints++;
            Assert.ThrowsAny<IOException>(() => new FileStream(Path.Combine(_root, "repository.lock"), FileMode.Open, FileAccess.Read, FileShare.Read).Dispose());
            Assert.ThrowsAny<IOException>(() => new FileStream(FileFor("state"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
            if (stage == "BeforeDependencies") { cancellation.Cancel(); }
        };
        try { Assert.Equal(RecoveryQualificationStatus.Cancelled, QualifyUnchanged(token: cancellation.Token).Status); }
        finally { RepositoryRecoveryQualification.Checkpoint = null; }
        Assert.True(checkpoints >= 3);
        Assert.Equal(RecoveryQualificationStatus.Qualified, QualifyUnchanged().Status);
    }
    [Fact]
    public void CompleteCrcContradictionRemainsInvalidBeforeShortTail() {
        var head = Seed();
        string path = FileFor("state");
        byte[] bytes = File.ReadAllBytes(path); bytes[checked((int)head.RevisionAddress.FrameTicket.Offset + 4)] ^= 1;
        File.WriteAllBytes(path, bytes);
        // The later partial HeadLen cannot authorize dropping an earlier complete bad frame.
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write)) { stream.Write([64, 0]); }
        Assert.Equal(RecoveryQualificationStatus.Invalid, QualifyUnchanged().Status);
    }
    [Fact]
    public void CompleteAdvanceCannotPointOutsideCandidateEventEof() {
        CheckpointAddress removed;
        using (var repository = Repository.CreateNew(_root, SharedReadModel.Models(), new() { NewStoreLayout = RbfSegmentStoreLayout.Flat })) {
            using var branch = repository.CreateBranch("main", new Node { Value = 1 }, NoRebase);
            removed = branch.CommitEvent(new Node { Value = 2 }, NoRebase);
        }
        string eventPath = FileFor("event");
        byte[] bytes = File.ReadAllBytes(eventPath);
        // Real Event's partial HeadLen, but its complete published Advance remains retained.
        File.WriteAllBytes(eventPath, bytes[..checked((int)removed.Address.Ticket.Offset + 2)]);
        var report = QualifyUnchanged();
        Assert.Equal(RecoveryQualificationStatus.Invalid, report.Status);
        Assert.Contains("Journal.", report.Detail);
    }
    [Fact]
    public void HistoricalRefExpectedTargetCannotBeHiddenByHealthyCurrentHead() {
        Seed(commits: true);
        string refPath = FileFor("ref");
        var moves = new List<(uint Tag, RefMoveFrame Move)>();
        using (var file = RbfFile.OpenReadOnlyExisting(refPath)) {
            var scan = file.ScanForward().GetEnumerator();
            while (scan.MoveNext()) {
                using var frame = file.ReadPooledFrame(scan.Current.Ticket).Unwrap();
                moves.Add((frame.Tag, RefMoveFrameCodec.Decode(frame.PayloadAndMeta).Unwrap()));
            }
        }
        var middle = moves[1];
        var missing = new EventAddress(SizedPtr.Create(4, 24), 1, middle.Move.NewTarget!.Value.Hint);
        moves[1] = (middle.Tag, middle.Move with { ExpectedOldTarget = missing });
        // Closed adversarial fixture rebuilds CRC-valid move records using the real writer.
        File.Delete(refPath);
        using (var writer = RbfFile.CreateNew(refPath)) {
            foreach (var move in moves) {
                byte[] payload = new byte[RefMoveFrameCodec.FixedLength];
                RefMoveFrameCodec.Encode(move.Move, payload);
                writer.Append(move.Tag, payload).Unwrap();
            }
            writer.DurableFlush();
        }
        Assert.Equal(RecoveryQualificationStatus.Invalid, QualifyUnchanged().Status);
    }
    [Fact]
    public void MultipleShortTailsAreOutsideSingleWriterScope() {
        Seed(); AddShortTail(FileFor("state")); AddShortTail(FileFor("event"));
        Assert.Equal(RecoveryQualificationStatus.Unsupported, QualifyUnchanged().Status);
    }
    [Fact]
    public void PartialTrailerRemainsUnresolved() {
        Seed(); string path = FileFor("state"); SizedPtr ticket;
        using (var writer = RbfFile.OpenExisting(path)) { ticket = writer.Append(0xF200, new byte[64]).Unwrap(); writer.DurableFlush(); }
        byte[] bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..checked((int)(ticket.Offset + ticket.Length - 16 + 1))]);
        Assert.Equal(RecoveryQualificationStatus.Unresolved, QualifyUnchanged().Status);
    }

    [Fact]
    public void IoFailureAndPreCancellationCannotBecomeInvalidOrQualified() {
        Seed();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Equal(RecoveryQualificationStatus.Cancelled, QualifyUnchanged(token: cancellation.Token).Status);
        RepositoryRecoveryQualification.Checkpoint = stage => {
            if (stage == "CandidateConstructed") { throw new IOException("Injected stage-boundary I/O failure."); }
        };
        try { Assert.Equal(RecoveryQualificationStatus.IoFailure, QualifyUnchanged().Status); }
        finally { RepositoryRecoveryQualification.Checkpoint = null; }
        using (var writer = Repository.OpenExisting(_root, SharedReadModel.Models())) {
            Assert.Equal(RecoveryQualificationStatus.IoFailure, RepositoryRecoveryQualification.Qualify(_root).Status);
        }
        Assert.Equal(RecoveryQualificationStatus.Qualified, QualifyUnchanged().Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrphanDeltaRequiresPhysicalPriorAndExactParentHead(bool priorExists) {
        var head = Seed();
        using (var segments = SegmentStore.OpenExisting(Path.Combine(_root, "state"))) {
            using var states = new StateRevisionStore(segments);
            FrameAddress prior = priorExists
                ? states.AppendDurably(StateRevision.CreateObjectHeadMapBase(null,
                    [ObjectVersionRecord.CreateBase(head.RootId.Value, [4, 1])], []))
                : new FrameAddress(1, SizedPtr.Create(4, 24));
            // Adversarial, CRC-correct wire fixture bypasses the writer's prior guard.
            using var lease = segments.OpenActiveWriter();
            var buffer = new ArrayBufferWriter<byte>();
            var writer = new BinaryPayloadWriter(buffer);
            writer.WriteByte(StateRevisionStore.WireVersion);
            writer.WriteByte((byte)ObjectHeadMapKind.Base);
            writer.WriteByte(1);
            writer.WriteUInt32(0);
            writer.WriteUInt64(head.RevisionAddress.FrameTicket.Serialize());
            writer.WriteUInt32(1);
            writer.WriteUInt32(head.RootId.Value);
            writer.WriteByte((byte)ObjectVersionKind.Delta);
            writer.WriteUInt32(0);
            writer.WriteUInt64(prior.FrameTicket.Serialize());
            writer.WriteUInt32(1);
            writer.WriteByte(0);
            writer.WriteUInt32(0);
            lease.File.Append(StateRevisionStore.RbfTag, buffer.WrittenSpan).Unwrap();
            lease.File.DurableFlush();
        }
        AddShortTail(FileFor("state"));
        Assert.Equal(RecoveryQualificationStatus.Invalid, QualifyUnchanged().Status);
    }
    private static void AddShortTail(string path) {
        SizedPtr next;
        using (var writer = RbfFile.OpenExisting(path)) { next = writer.Append(0xF200, new byte[64]).Unwrap(); writer.DurableFlush(); }
        byte[] bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..checked((int)next.Offset + 2)]);
    }
    public void Dispose() => SharedReadModel.DeleteFixture(_root, Prefix);
}
