using Atelia.Data;
using Atelia.DurableGraph.Storage;
using Atelia.RbfSegmentStore;
using JournalStore = Atelia.EventJournal.EventJournal;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;
using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class DB085ReadOnlyBoundaryTests : IDisposable {
    private const string Prefix = "durable-db085-boundary-";
    private readonly string _root = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("state")]
    [InlineData("schemas.rbf")]
    [InlineData("journal/events")]
    public void CandidateRetainsStrictActiveTailAndSchemaChecks(string component) {
        using (var writer = Repository.CreateNew(_root, SharedReadModel.Models(),
            new() { NewStoreLayout = RbfSegmentStoreLayout.Flat })) {
            using var branch = writer.CreateBranch("main", new Node { Value = 1 });
        }
        string file = component == "schemas.rbf" ? Path.Combine(_root, component) :
            Directory.GetFiles(Path.Combine(_root, component), "*.rbf", SearchOption.AllDirectories).Single();
        using (var stream = new FileStream(file, FileMode.Append, FileAccess.Write)) { stream.Write([1, 2, 3]); }
        byte[] before = File.ReadAllBytes(file);
        Assert.ThrowsAny<Exception>(() => Repository.OpenReadOnlyCandidate(_root, SharedReadModel.Models()));
        Assert.Equal(before, File.ReadAllBytes(file));
        // Failed candidate acquisition must release the repository lock as well as its readers.
        var audit = RepositoryMaintenance.Audit(_root);
        Assert.False(audit.IsHealthy);
        Assert.DoesNotContain(audit.Findings, finding => finding.RelativePath == "repository.lock");
    }

    [Fact]
    public void CandidateCannotOverlapWriterAndDoesNotCreateMissingLock() {
        using (var writer = Repository.CreateNew(_root, SharedReadModel.Models())) {
            Assert.ThrowsAny<IOException>(() => Repository.OpenReadOnlyCandidate(_root, SharedReadModel.Models()));
        }
        string lockFile = Path.Combine(_root, "repository.lock");
        File.Delete(lockFile);
        Assert.Throws<FileNotFoundException>(() => Repository.OpenReadOnlyCandidate(_root, SharedReadModel.Models()));
        Assert.False(File.Exists(lockFile));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonLastCrcFailureIsDeferredAndCarriesItsDependencyAddress(bool journalFrame) {
        CheckpointAddress first;
        using (var writer = Repository.CreateNew(_root, SharedReadModel.Models())) {
            using var branch = writer.CreateBranch("main", new Node { Value = 1 });
            first = branch.Head;
            branch.CommitEvent(new Node { Value = 2 });
            writer.CreateTag("last", branch.CommitState(new Node { Value = 3 }));
        }
        string directory = Path.Combine(_root, journalFrame ? "journal/events" : "state");
        string file = Directory.GetFiles(directory, "*.rbf", SearchOption.AllDirectories).Single();
        long offset = (journalFrame ? first.Address.Ticket.Offset : first.RevisionAddress.FrameTicket.Offset) + 4;
        // Deliberate corruption on a closed fixture. A healthy later physical frame remains the tail.
        using (var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            stream.Position = offset;
            int value = stream.ReadByte();
            Assert.True(value >= 0);
            stream.Position = offset;
            stream.WriteByte((byte)(value ^ 1));
            stream.Flush(flushToDisk: true);
        }
        byte[] before = File.ReadAllBytes(file);
        int callbacks = 0;
        using (var candidate = Repository.OpenReadOnlyCandidate(_root,
            SharedReadModel.Models(allocate: () => { callbacks++; return new(); }))) {
            // Resolve the healthy final tag; branch Init may itself inspect the corrupt first Event.
            var error = Assert.Throws<InvalidOperationException>(() => candidate.ResolveTag("last"));
            Assert.Contains("CRC", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(first.Address.ToString(), error.Data["DurableGraph.JournalAddress"]);
            if (!journalFrame) {
                Assert.Equal(first.RevisionAddress.ToString(), error.Data["DurableGraph.RevisionAddress"]);
            }
            Assert.Equal(0, callbacks);
        }
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.False(RepositoryMaintenance.Audit(_root).IsHealthy);
    }

    [Fact]
    public void RandomReadableNestedRevisionRequiresAuditForPhysicalMembership() {
        CheckpointAddress original;
        using (var repository = Repository.CreateNew(_root, SharedReadModel.Models(),
            new() { NewStoreLayout = RbfSegmentStoreLayout.Flat })) {
            using var branch = repository.CreateBranch("main", new Node { Value = 42 });
            original = branch.Head;
        }
        string stateFile = Directory.GetFiles(Path.Combine(_root, "state"), "*.rbf", SearchOption.AllDirectories).Single();
        // Copy a real complete root Revision, including its trailing Fence. Its opaque object
        // body is valid for the registered model, not just for the structural Audit decoder.
        byte[] bytes = File.ReadAllBytes(stateFile);
        var ticket = original.RevisionAddress.FrameTicket;
        byte[] nestedBytes = bytes.AsSpan(checked((int)ticket.Offset), checked(ticket.Length + 4)).ToArray();
        using (var segments = SegmentStore.OpenExisting(Path.Combine(_root, "state"))) {
            using var states = new StateRevisionStore(segments);
            for (int padding = 0; padding < 4; padding++) {
                // The containing fact is an orphan string Base. Audit intentionally checks its
                // representation header, not application body semantics; arbitrary body bytes fit.
                byte[] body = new byte[2 + padding + nestedBytes.Length];
                body[0] = 4;
                body[1] = 1;
                nestedBytes.CopyTo(body, 2 + padding);
                states.AppendDurably(StateRevision.CreateObjectHeadMapBase(null,
                    [ObjectVersionRecord.CreateBase(1, body)], []));
            }
        }
        Assert.True(RepositoryMaintenance.Audit(_root).IsHealthy);
        bytes = File.ReadAllBytes(stateFile);
        int offset = -1;
        for (int index = checked((int)(ticket.Offset + ticket.Length + 4));
            index <= bytes.Length - nestedBytes.Length; index += 4) {
            if (bytes.AsSpan(index, nestedBytes.Length).SequenceEqual(nestedBytes)) { offset = index; break; }
        }
        Assert.True(offset >= 0);
        var nested = new FrameAddress(1, SizedPtr.Create(offset, ticket.Length));
        using (var journal = JournalStore.OpenExisting(Path.Combine(_root, "journal"))) {
            // Deliberate protocol violation: this reference was not produced by the DG writer.
            var forged = journal.AppendEventFrame(null,
                GraphEnvelopeCodec.Encode(nested, original.RootId), (uint)GraphFrameKind.State).Unwrap();
            journal.CreateBranch("forged", forged).Unwrap();
        }
        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        using (var candidate = Repository.OpenReadOnlyCandidate(_root, SharedReadModel.Models())) {
            var selected = candidate.GetHead("forged");
            Assert.Equal(42, Assert.IsType<Node>(candidate.ReadState(selected)).Value);
        }
        var audit = RepositoryMaintenance.Audit(_root);
        Assert.False(audit.IsHealthy);
        Assert.Contains(audit.Findings, finding => finding.Code == "JournalGraphInvalid" &&
            finding.Message.Contains("absent from complete physical State frames"));
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order());
        foreach (var pair in before) { Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key)); }
    }

    public void Dispose() => SharedReadModel.DeleteFixture(_root, Prefix);
}
