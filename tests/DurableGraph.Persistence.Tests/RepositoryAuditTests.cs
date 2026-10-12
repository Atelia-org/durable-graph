using System.Buffers;
using Atelia.Data;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.EventJournal;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using JournalStore = Atelia.EventJournal.EventJournal;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;
using FrameAddress = Atelia.DurableGraph.Storage.FrameAddress;
using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class RepositoryAuditTests : IDisposable {
    private const string Prefix = "durable-repository-audit-";
    private readonly string _root = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1000000, 1);

    [Fact]
    public void HealthyBranchTagEventAndCompleteOrphansAuditWithoutModelsOrWrites() {
        CheckpointAddress head;
        using (var repository = Create()) {
            using var main = repository.CreateBranch("main", new Node { Value = 1 }, NoRebase);
            repository.CreateTag("first", main.Head);
            main.CommitEvent(new Node { Value = 2 }, NoRebase);
            head = main.CommitState(new Node { Value = 3 }, NoRebase);
            repository.CreateBranch("fork", head);
        }
        // A valid State orphan and valid Journal orphan remain facts even with no ref pointing at them.
        FrameAddress orphan;
        using (var segments = SegmentStore.OpenExisting(Path.Combine(_root, "state"))) {
            using var states = new StateRevisionStore(segments);
            orphan = states.AppendDurably(StateRevision.CreateObjectHeadMapBase(head.RevisionAddress,
                [], states.ReadLiveObjectHeadMap(head.RevisionAddress)));
        }
        using (var journal = JournalStore.OpenExisting(Path.Combine(_root, "journal"))) {
            journal.AppendEventFrame(head.Address, GraphEnvelopeCodec.Encode(orphan, head.RootId), (uint)GraphFrameKind.Event).Unwrap();
        }
        var report = AuditUnchanged();
        Assert.True(report.IsHealthy, string.Join("; ", report.Findings.Select(f => f.Code + ": " + f.Message)));
        Assert.Equal(RepositoryAuditCompletion.Completed, report.Completion);
        Assert.Equal(4, report.StateFrames);
        Assert.Equal(4, report.JournalEvents);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("schema")]
    [InlineData("journal")]
    public void BadTailRejectsNormalOpenButAuditRetainsReportAndBytes(string store) {
        Seed();
        string file = store switch {
            "state" => Directory.GetFiles(Path.Combine(_root, "state"), "*.rbf", SearchOption.AllDirectories).Single(),
            "schema" => Path.Combine(_root, "schemas.rbf"),
            _ => Path.Combine(_root, "journal", "refs", "ref-op-log.rbf"),
        };
        using (var stream = new FileStream(file, FileMode.Append, FileAccess.Write)) { stream.Write([1, 2, 3]); }
        Assert.ThrowsAny<Exception>(() => Repository.OpenReadOnlyExisting(_root, SharedReadModel.Models()));
        var report = AuditUnchanged();
        Assert.False(report.IsHealthy);
        Assert.Equal(RepositoryAuditCompletion.Incomplete, report.Completion);
        Assert.Contains(report.Findings, f => f.Kind == RepositoryAuditFindingKind.InvalidData);
    }

    [Theory]
    [InlineData("parent")]
    [InlineData("external")]
    [InlineData("representation")]
    [InlineData("base-version")]
    [InlineData("wire-version")]
    public void UnreferencedStateStillValidatesAllDependenciesAndFormats(string damage) {
        var head = Seed();
        using (var segments = SegmentStore.OpenExisting(Path.Combine(_root, "state"))) {
            using var states = new StateRevisionStore(segments);
            var missing = new FrameAddress(1, SizedPtr.Create(4, 24));
            if (damage == "wire-version") {
                using var writer = segments.OpenActiveWriter();
                writer.File.Append(StateRevisionStore.RbfTag, [255]).Unwrap();
                writer.File.DurableFlush();
            }
            else {
                ObjectVersionRecord[] locals = damage is "representation" or "base-version"
                    ? [ObjectVersionRecord.CreateBase(1, damage == "representation" ? new byte[] { 4, 127 } : new byte[] { 255, 1 })] : [];
                var external = damage == "external" ? new[] { new KeyValuePair<uint, FrameAddress>(1, missing) } : [];
                states.AppendDurably(StateRevision.CreateObjectHeadMapBase(damage == "parent" ? missing : head.RevisionAddress, locals, external));
            }
        }
        var report = AuditUnchanged();
        Assert.False(report.IsHealthy);
        Assert.Contains(report.Findings, f => f.RelativePath.StartsWith("state/") && f.Offset.HasValue &&
            f.Kind == (damage.EndsWith("version") ? RepositoryAuditFindingKind.UnsupportedFormat : RepositoryAuditFindingKind.InvalidData));
        if (damage.EndsWith("version")) { Assert.Equal(RepositoryAuditCompletion.Incomplete, report.Completion); }
    }

    [Theory]
    [InlineData("parent")]
    [InlineData("revision")]
    [InlineData("root")]
    [InlineData("version")]
    public void CrcValidJournalOrphanCannotHideInvalidGraph(string damage) {
        var head = Seed();
        byte[] envelope = GraphEnvelopeCodec.Encode(damage == "revision" ? new FrameAddress(1, SizedPtr.Create(4, 24)) : head.RevisionAddress,
            damage == "root" ? new ObjectId(uint.MaxValue) : head.RootId);
        if (damage == "version") { envelope[4] = 255; }
        using (var journal = JournalStore.OpenExisting(Path.Combine(_root, "journal"))) {
            // A root Revision has Parent=null, so a child Event using it disagrees with nearest State.
            journal.AppendEventFrame(damage == "parent" ? head.Address : null, envelope, (uint)GraphFrameKind.Event).Unwrap();
        }
        var report = AuditUnchanged();
        Assert.False(report.IsHealthy);
        Assert.Contains(report.Findings, f => f.RelativePath.StartsWith("journal/events/") && f.Offset.HasValue &&
            f.Kind == (damage == "version" ? RepositoryAuditFindingKind.UnsupportedFormat : RepositoryAuditFindingKind.InvalidData));
    }

    [Fact]
    public void UnsupportedSchemaIsIncompleteAndLocated() {
        Seed();
        using (var file = RbfFile.OpenExisting(Path.Combine(_root, "schemas.rbf"))) {
            file.Append(SchemaCatalogWireCodec.RbfTag, [255]).Unwrap();
            file.DurableFlush();
        }
        var report = AuditUnchanged();
        Assert.Equal(RepositoryAuditCompletion.Incomplete, report.Completion);
        Assert.Contains(report.Findings, f => f.Kind == RepositoryAuditFindingKind.UnsupportedFormat && f.RelativePath == "schemas.rbf" && f.Offset.HasValue);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("schema")]
    [InlineData("graph")]
    public void CompleteCrcValidFrameWithTruncatedTypedPayloadIsInvalidData(string store) {
        Seed();
        if (store == "state") {
            using var segments = SegmentStore.OpenExisting(Path.Combine(_root, "state"));
            using var writer = segments.OpenActiveWriter();
            writer.File.Append(StateRevisionStore.RbfTag, [StateRevisionStore.WireVersion]).Unwrap();
            writer.File.DurableFlush();
        }
        else if (store == "schema") {
            using var file = RbfFile.OpenExisting(Path.Combine(_root, "schemas.rbf"));
            file.Append(SchemaCatalogWireCodec.RbfTag, [SchemaCatalogWireCodec.Version]).Unwrap();
            file.DurableFlush();
        }
        else {
            using var journal = JournalStore.OpenExisting(Path.Combine(_root, "journal"));
            journal.AppendEventFrame(null, [68, 71, 72, 49, 1], (uint)GraphFrameKind.Event).Unwrap();
        }
        var report = AuditUnchanged();
        Assert.False(report.IsHealthy);
        Assert.Contains(report.Findings, f => f.Kind == RepositoryAuditFindingKind.InvalidData && f.Offset.HasValue);
        Assert.DoesNotContain(report.Findings, f => f.Kind == RepositoryAuditFindingKind.IoFailure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrphanLocalDeltaRequiresPhysicalPriorAndExactParentHead(bool physicallyPresentPrior) {
        var head = Seed();
        using (var segments = SegmentStore.OpenExisting(Path.Combine(_root, "state"))) {
            using var states = new StateRevisionStore(segments);
            FrameAddress prior = physicallyPresentPrior
                ? states.AppendDurably(StateRevision.CreateObjectHeadMapBase(null,
                    [ObjectVersionRecord.CreateBase(head.RootId.Value, [4, 1])], []))
                : new FrameAddress(1, SizedPtr.Create(4, 24));
            // Independent wire fixture bypasses Append's direct-prior guard while retaining a correct RBF CRC.
            using var lease = segments.OpenActiveWriter();
            var buffer = new ArrayBufferWriter<byte>();
            var writer = new BinaryPayloadWriter(buffer);
            writer.WriteByte(StateRevisionStore.WireVersion);
            writer.WriteByte((byte)ObjectHeadMapKind.Base);
            writer.WriteByte(1);
            writer.WriteUInt32(lease.SegmentNumber - head.RevisionAddress.FileNumber);
            writer.WriteUInt64(head.RevisionAddress.FrameTicket.Serialize());
            writer.WriteUInt32(1);
            writer.WriteUInt32(head.RootId.Value);
            writer.WriteByte((byte)ObjectVersionKind.Delta);
            writer.WriteUInt32(lease.SegmentNumber - prior.FileNumber);
            writer.WriteUInt64(prior.FrameTicket.Serialize());
            writer.WriteUInt32(1);
            writer.WriteByte(0);
            writer.WriteUInt32(0);
            lease.File.Append(StateRevisionStore.RbfTag, buffer.WrittenSpan).Unwrap();
            lease.File.DurableFlush();
        }
        var report = AuditUnchanged();
        Assert.Contains(report.Findings, f => f.Code == "StateDependencyInvalid" && f.Offset.HasValue);
        Assert.False(report.IsHealthy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RandomReadableEmbeddedStateFrameIsNotAPhysicalDependency(bool externalHead) {
        Seed();
        string temporary = _root + "-embedded.rbf";
        SizedPtr nestedTicket;
        byte[] nestedBytes;
        try {
            using (var file = RbfFile.CreateNew(temporary)) {
                // Valid State wire with one local string Base header. No application body decoding is in Audit's contract.
                nestedTicket = file.Append(StateRevisionStore.RbfTag,
                    [StateRevisionStore.WireVersion, (byte)ObjectHeadMapKind.Base, 0, 1, 1, (byte)ObjectVersionKind.Base, 2, 4, 1, 0]).Unwrap();
                file.DurableFlush();
            }
            nestedBytes = File.ReadAllBytes(temporary).AsSpan(checked((int)nestedTicket.Offset)).ToArray();
        }
        finally { File.Delete(temporary); }
        // Four valid outer facts vary only opaque body padding; at least one nested frame starts aligned.
        using (var segments = SegmentStore.OpenExisting(Path.Combine(_root, "state"))) {
            using var states = new StateRevisionStore(segments);
            for (int padding = 0; padding < 4; padding++) {
                byte[] body = new byte[2 + padding + nestedBytes.Length];
                body[0] = 4; body[1] = 1;
                nestedBytes.CopyTo(body, 2 + padding);
                states.AppendDurably(StateRevision.CreateObjectHeadMapBase(null, [ObjectVersionRecord.CreateBase(1, body)], []));
            }
        }
        string segmentPath = Directory.GetFiles(Path.Combine(_root, "state"), "*.rbf", SearchOption.AllDirectories).Single();
        byte[] bytes = File.ReadAllBytes(segmentPath);
        int nestedOffset = -1;
        for (int offset = 0; offset <= bytes.Length - nestedBytes.Length; offset += 4) {
            if (bytes.AsSpan(offset, nestedBytes.Length).SequenceEqual(nestedBytes)) { nestedOffset = offset; break; }
        }
        Assert.True(nestedOffset >= 0, "A valid nested frame must be aligned inside one opaque body.");
        var nested = new FrameAddress(1, SizedPtr.Create(nestedOffset, nestedTicket.Length));
        Assert.True(AuditUnchanged().IsHealthy); // Every real outer fact is independently valid.
        using (var segments = SegmentStore.OpenExisting(Path.Combine(_root, "state"))) {
            using var states = new StateRevisionStore(segments);
            Assert.Single(states.Read(nested).LocalObjects); // Random checked read accepts the embedded frame.
            Assert.Single(states.ReadLiveObjectHeadMap(nested));
            states.AppendDurably(StateRevision.CreateObjectHeadMapBase(externalHead ? null : nested, [],
                externalHead ? new[] { new KeyValuePair<uint, FrameAddress>(1, nested) } : []));
        }
        var report = AuditUnchanged();
        Assert.False(report.IsHealthy);
        Assert.Contains(report.Findings, f => f.Code == "StateDependencyInvalid" && f.Message.Contains("preceding complete physical frames"));
    }

    [Theory]
    [InlineData("schemas.rbf")]
    [InlineData("state")]
    [InlineData("journal")]
    [InlineData("repository.lock")]
    public void MissingDependencyCannotBeHealthy(string missing) {
        Seed();
        string path = Path.Combine(_root, missing);
        if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); } else { File.Delete(path); }
        var report = AuditUnchanged();
        Assert.False(report.IsHealthy);
        Assert.Equal(RepositoryAuditCompletion.Incomplete, report.Completion);
    }

    [Fact]
    public void ExistingLockConflictStopsBeforeScanningAndCancelAndIoNeverHealthy() {
        Seed();
        using (var writer = new FileStream(Path.Combine(_root, "repository.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            var blocked = RepositoryMaintenance.Audit(_root);
            Assert.False(blocked.IsHealthy);
            Assert.Equal(RepositoryAuditCompletion.Incomplete, blocked.Completion);
            Assert.Equal("RepositoryLockUnavailable", Assert.Single(blocked.Findings).Code);
            Assert.Equal(0, blocked.StateFrames);
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var report = RepositoryMaintenance.Audit(_root, cancelled.Token);
        Assert.False(report.IsHealthy);
        Assert.Equal(RepositoryAuditCompletion.Cancelled, report.Completion);
        Assert.False(RepositoryMaintenance.Audit(_root + "-missing").IsHealthy);
        using (var held = new FileStream(Path.Combine(_root, "schemas.rbf"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            var io = RepositoryMaintenance.Audit(_root);
            Assert.Equal(RepositoryAuditCompletion.Incomplete, io.Completion);
            Assert.Contains(io.Findings, f => f.Kind == RepositoryAuditFindingKind.IoFailure && f.RelativePath == "schemas.rbf");
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void InterruptedSecondJournalInventoryCannotSignHealthyAndRetainsEarlierFindings(bool cancel, bool earlierInvalid) {
        Seed();
        if (earlierInvalid) {
            using var segments = SegmentStore.OpenExisting(Path.Combine(_root, "state"));
            using var states = new StateRevisionStore(segments);
            states.AppendDurably(StateRevision.CreateObjectHeadMapBase(null, [ObjectVersionRecord.CreateBase(1, [4, 127])], []));
        }
        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        using var cancellation = new CancellationTokenSource();
        FileStream? held = null;
        bool reached = false;
        RepositoryAuditReport report;
        try {
            RepositoryMaintenance.AuditCheckpoint = stage => {
                Assert.Equal("BeforeGraphInventory", stage);
                reached = true;
                if (cancel) { cancellation.Cancel(); }
                else { held = new FileStream(Path.Combine(_root, "journal", "events", "active.segment"), FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            };
            report = RepositoryMaintenance.Audit(_root, cancellation.Token);
        }
        finally {
            RepositoryMaintenance.AuditCheckpoint = null;
            held?.Dispose();
        }
        Assert.True(reached);
        Assert.False(report.IsHealthy);
        Assert.Equal(cancel ? RepositoryAuditCompletion.Cancelled : RepositoryAuditCompletion.Incomplete, report.Completion);
        if (earlierInvalid) { Assert.Contains(report.Findings, f => f.Code == "StateDependencyInvalid"); }
        if (!cancel) { Assert.Contains(report.Findings, f => f.Kind == RepositoryAuditFindingKind.IoFailure && f.RelativePath == "journal/events/active.segment"); }
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order());
        foreach (var pair in before) { Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key)); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StorageLegalEmptyBranchHeadStillViolatesDurableGraphContract(bool moveExisting) {
        CheckpointAddress? head = null;
        if (moveExisting) { head = Seed(); } else { using var empty = Create(); }
        using (var journal = JournalStore.OpenExisting(Path.Combine(_root, "journal"))) {
            if (moveExisting) { journal.MoveRef(journal.OpenBranch("main").Unwrap(), head!.Address, null).Unwrap(); }
            else { journal.CreateBranch("empty", null).Unwrap(); }
        }
        var normal = Assert.Throws<InvalidDataException>(() => Repository.OpenReadOnlyExisting(_root, SharedReadModel.Models()));
        Assert.Equal("EventHistory branches cannot have empty heads.", normal.Message);
        var report = AuditUnchanged();
        Assert.False(report.IsHealthy);
        Assert.Contains(report.Findings, f => f.Code == "JournalBranchInvalid" && f.Kind == RepositoryAuditFindingKind.InvalidData &&
            f.Message == "EventHistory branches cannot have empty heads.");
    }

    private Repository Create() => Repository.CreateNew(_root, SharedReadModel.Models(), new() { NewStoreLayout = RbfSegmentStoreLayout.Flat });
    private CheckpointAddress Seed() {
        using var repository = Create();
        using var branch = repository.CreateBranch("main", new Node { Value = 1 }, NoRebase);
        return branch.Head;
    }
    private RepositoryAuditReport AuditUnchanged() {
        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        var report = RepositoryMaintenance.Audit(_root);
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order());
        foreach (var pair in before) { Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key)); }
        return report;
    }
    public void Dispose() => SharedReadModel.DeleteFixture(_root, Prefix);
}
