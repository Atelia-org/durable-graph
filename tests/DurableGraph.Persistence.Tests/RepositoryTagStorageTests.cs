using System.Buffers.Binary;
using System.Text;
using Atelia.Data;
using Atelia.Data.Hashing;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.EventJournal;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using JournalStore = Atelia.EventJournal.EventJournal;
using StateAddress = Atelia.DurableGraph.Storage.FrameAddress;

namespace Atelia.DurableGraph.Persistence.Tests;

/// <summary>Strict DurableGraph consumption of the pinned EventJournal immutable-tag format.</summary>
public sealed class RepositoryTagStorageTests : IDisposable {
    private const string Prefix = "durable-repository-tag-storage-";
    private readonly string _root = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private string JournalPath => Path.Combine(_root, "journal");
    private string RefLogPath => Path.Combine(JournalPath, "refs", "ref-op-log.rbf");

    [Theory]
    [InlineData("duplicate")]
    [InlineData("version")]
    [InlineData("missing-target")]
    [InlineData("unknown-frame")]
    [InlineData("tombstone")]
    [InlineData("crc")]
    [InlineData("truncated-tail")]
    public void MalformedPersistedTagRejectsBothRepositoryOpenModesWithoutRepair(string damage) {
        EventAddress target = CreateTaggedRepository();
        byte[] payload = EncodeTag(damage == "duplicate" ? "anchor" : "raw", target);
        if (damage == "version") { BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), 2); }
        if (damage == "missing-target") { BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16), uint.MaxValue); }
        uint frameTag = damage == "unknown-frame" ? 0x1234_5678u : JournalStore.TagBindingFrameTag;
        SizedPtr ticket;
        using (IRbfFile file = RbfFile.OpenExisting(RefLogPath)) {
            ticket = file.Append(frameTag, payload).Unwrap();
            file.DurableFlush();
            // Format/semantic cases have an intact RBF envelope and valid CRC;
            // they cannot pass merely because a physical checksum rejects first.
            using RbfPooledFrame frame = file.ReadPooledFrame(ticket).Unwrap();
            Assert.False(frame.IsTombstone);
        }
        if (damage == "tombstone") { MarkTombstone(ticket); }
        if (damage == "crc") {
            byte[] bytes = File.ReadAllBytes(RefLogPath);
            bytes[checked((int)ticket.Offset + 4)] ^= 0x80;
            File.WriteAllBytes(RefLogPath, bytes);
        }
        if (damage == "truncated-tail") {
            // Deliberate offline fixture damage, never a normal open or recovery action.
            byte[] bytes = File.ReadAllBytes(RefLogPath);
            File.WriteAllBytes(RefLogPath, bytes[..^1]);
        }
        AssertBothOpensRejectWithoutChangingFiles(expectInvalidData: true, crcMismatch: damage == "crc");
    }

    [Theory]
    [InlineData("envelope")]
    [InlineData("state")]
    [InlineData("schema")]
    public void CheckedReadableTagTargetStillRequiresValidDurableGraphDependencies(string missing) {
        CreateTaggedRepository();
        StateAddress revision = new(uint.MaxValue, SizedPtr.Create(4, 24));
        if (missing == "schema") {
            using GraphResources resources = GraphResources.OpenExisting(_root);
            // A complete State frame with an unregistered representation is valid RBF
            // and valid State framing, but cannot establish its required Schema layout.
            EncodedBaseObjectBody body = BaseObjectBodyCodec.Encode(new RepresentationId(123456), new PreparedBaseBody([7]));
            revision = resources.States.AppendDurably(StateRevision.CreateObjectHeadMapBase(null,
                [ObjectVersionRecord.CreateBase(1, body.Body)], []));
        }
        EventAddress target;
        using (JournalStore raw = JournalStore.OpenExisting(JournalPath, StrictOptions())) {
            byte[] payload = missing == "envelope" ? [0x00] : GraphEnvelopeCodec.Encode(revision, new(1));
            target = raw.AppendEventFrame(null, payload, (uint)GraphFrameKind.State).Unwrap();
            Assert.True(raw.CreateTag("invalid-checkpoint", target).Unwrap());
            Assert.Equal(target, raw.ResolveTag("invalid-checkpoint").Unwrap());
        }
        using (JournalStore upstream = JournalStore.OpenReadOnlyExisting(JournalPath, StrictOptions())) {
            // This is specifically DG validation, not an upstream invalid-target test.
            Assert.Equal(target, upstream.ResolveTag("invalid-checkpoint").Unwrap());
        }
        AssertBothOpensRejectWithoutChangingFiles(expectInvalidData: missing != "state");
    }

    [Fact]
    public void TagPublicationLogIsConfirmedAfterEventsAndRefObjects() {
        EventAddress target = CreateTaggedRepository();
        string eventFile = Component("events"), refObject = Component("ref-object");
        List<string> confirmed = [];
        using HistoryJournal history = HistoryJournal.Open(_root, readOnly: false);
        Assert.Equal(target, history.Journal.ResolveTag("anchor").Unwrap());
        history.AfterConfirmFile = confirmed.Add;
        history.ConfirmDurable();
        Assert.Equal(new[] { eventFile, refObject, RefLogPath }, confirmed);
        Assert.Equal(target, history.Journal.ResolveTag("anchor").Unwrap());
        history.ConfirmDurable();
        Assert.Equal(3, confirmed.Count);
    }

    [Fact]
    public void FailedTargetConfirmationCannotConfirmTagLogOrEnableJournalWrites() {
        EventAddress target = CreateTaggedRepository();
        Dictionary<string, byte[]> before = Snapshot();
        List<string> confirmed = [];
        using (HistoryJournal history = HistoryJournal.Open(_root, readOnly: false)) {
            history.AfterConfirmFile = path => {
                confirmed.Add(path);
                throw new IOException("Interrupted after target Event file confirmation.");
            };
            Assert.Throws<IOException>(history.ConfirmDurable);
            Assert.Equal(new[] { Component("events") }, confirmed);
            Assert.DoesNotContain(RefLogPath, confirmed);
            Assert.Throws<InvalidOperationException>(() => history.Append(GraphFrameKind.State,
                new StateAddress(1, SizedPtr.Create(4, 24)), new(1), null));
            Assert.Throws<ObjectDisposedException>(() => history.Journal.ResolveTag("anchor"));
        }
        AssertUnchanged(before);
        using HistoryJournal reopened = HistoryJournal.Open(_root, readOnly: false);
        reopened.ConfirmDurable();
        Assert.Equal(target, reopened.Journal.ResolveTag("anchor").Unwrap());
    }

    private EventAddress CreateTaggedRepository() {
        EventAddress target;
        using (Repository repository = Repository.CreateNew(_root, SharedReadModel.Models(),
            new() { NewStoreLayout = RbfSegmentStoreLayout.Flat })) {
            using BranchCheckout main = repository.CreateBranch("main", new SharedReadModel.Node { Value = 7 });
            target = main.Head.Address;
        }
        using JournalStore journal = JournalStore.OpenExisting(JournalPath, StrictOptions());
        Assert.True(journal.CreateTag("anchor", target).Unwrap());
        return target;
    }

    // Exact codec witness from atelia-storage 883f995847f9bc9b92801f4f5f1f0997f54ff7d7.
    // Keep production encoding inside EventJournal; raw fixtures deliberately bypass it.
    private static byte[] EncodeTag(string name, EventAddress target) {
        byte[] bytes = new byte[32 + name.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x4754_4A45); // EJTG
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), 32);
        EventAddressCodec.Encode(target, bytes.AsSpan(8, 16));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(24), checked((ushort)name.Length));
        Encoding.ASCII.GetBytes(name, bytes.AsSpan(32));
        return bytes;
    }

    private void MarkTombstone(SizedPtr ticket) {
        // Only this closed fixture is mutated. Re-seal the descriptor CRC so a
        // valid tombstone reaches DG's explicit tombstone rejection.
        byte[] bytes = File.ReadAllBytes(RefLogPath);
        Span<byte> trailer = bytes.AsSpan(checked((int)ticket.EndOffsetExclusive - 16), 16);
        uint descriptor = BinaryPrimitives.ReadUInt32LittleEndian(trailer[4..]) | 0x8000_0000u;
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[4..], descriptor);
        RollingCrc.SealCodewordBackward(trailer);
        File.WriteAllBytes(RefLogPath, bytes);
        using IRbfFile file = RbfFile.OpenReadOnlyExisting(RefLogPath);
        using RbfPooledFrame frame = file.ReadPooledFrame(ticket).Unwrap();
        Assert.True(frame.IsTombstone);
    }

    private void AssertBothOpensRejectWithoutChangingFiles(bool expectInvalidData, bool crcMismatch = false) {
        Dictionary<string, byte[]> before = Snapshot();
        foreach (bool readOnly in new[] { true, false }) {
            Action open = () => {
                using Repository rejected = readOnly ? Repository.OpenReadOnlyExisting(_root, new StateModelRegistry()) :
                    Repository.OpenExisting(_root, new StateModelRegistry());
            };
            if (crcMismatch) {
                InvalidOperationException error = Assert.Throws<InvalidOperationException>(open);
                Assert.Contains("[Rbf.CrcMismatch]", error.Message);
            }
            else if (expectInvalidData) { Assert.Throws<InvalidDataException>(open); }
            else { Assert.ThrowsAny<Exception>(open); }
            AssertUnchanged(before);
        }
    }
    private string Component(string component) => component switch {
        "events" => Assert.Single(Directory.GetFiles(Path.Combine(JournalPath, "events"), "*.rbf", SearchOption.AllDirectories)),
        "ref-object" => Assert.Single(Directory.GetFiles(Path.Combine(JournalPath, "refs", "objects"), "*.rbf", SearchOption.AllDirectories)),
        _ => throw new ArgumentOutOfRangeException(nameof(component)),
    };
    private static EventJournalOptions StrictOptions() => new() {
        EventSegmentStoreOptions = new() { RecoverActiveTailOnOpen = false },
        RefSegmentStoreOptions = new() { RecoverActiveTailOnOpen = false },
        RefOpLogOptions = new() { RecoverActiveTailOnOpen = false },
    };
    private Dictionary<string, byte[]> Snapshot() => Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => path, File.ReadAllBytes);
    private void AssertUnchanged(Dictionary<string, byte[]> before) {
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order());
        foreach ((string path, byte[] bytes) in before) { Assert.Equal(bytes, File.ReadAllBytes(path)); }
    }
    public void Dispose() => SharedReadModel.DeleteFixture(_root, Prefix);
}
