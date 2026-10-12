using System.Security.Cryptography;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.EventJournal;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using FrameAddress = Atelia.DurableGraph.Storage.FrameAddress;
using JournalStore = Atelia.EventJournal.EventJournal;
using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;
using State = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.State;

namespace Atelia.DurableGraph.Persistence.Tests;

/// <summary>Healthy low-level witnesses; only the final fixed refs are published.</summary>
internal sealed class DB085ReadOnlyCostFixture : IDisposable {
    private const string Prefix = "durable-db085-cost-";
    internal const string StateTag = "target-state";
    internal const string EventTag = "target-event";
    internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    internal DB085CostCell Cell { get; }
    internal DB085FixtureFacts Facts { get; private set; } = null!;

    internal DB085ReadOnlyCostFixture(DB085CostCell cell) {
        Cell = cell;
        try { Seed(); }
        catch { Dispose(); throw; }
    }

    private void Seed() {
        using (Repository.CreateNew(Path, SharedReadModel.Models(), new() { NewStoreLayout = RbfSegmentStoreLayout.Flat })) { }
        EventAddress targetState = default, targetEvent = default;
        FrameAddress targetStateRevision = default;
        using (IRbfFile schemaFile = RbfFile.OpenExisting(System.IO.Path.Combine(Path, "schemas.rbf"))) {
            SchemaStore schemas = new(schemaFile);
            schemas.RegisterBatch([SharedReadModel.Schema]);
            RepresentationId representation = schemas.RegisterRepresentations([ObjectLayout.ForDurable(SharedReadModel.Schema)])[0];
            // These definitions are unreferenced by every State/Journal graph and need no CLR models.
            for (int index = 0; index < Cell.ExtraSchemas; index++) {
                schemas.Register(new DurableSchema($"DB085Unused{index:D4}", 1, new DurableFieldInfo(1, TypeTag.Int32)));
            }
            using SegmentStore segments = SegmentStore.OpenExisting(System.IO.Path.Combine(Path, "state"));
            using StateRevisionStore states = new(segments);
            using JournalStore journal = JournalStore.OpenExisting(System.IO.Path.Combine(Path, "journal"));

            void Orphans() {
                for (int index = 0; index < Cell.UnrelatedHistory; index++) {
                    State state = new(1u, 1u, 0u, 99);
                    FrameAddress revision = states.AppendDurably(StateRevision.CreateObjectHeadMapBase(null,
                        [ObjectVersionRecord.CreateBase(1, BaseObjectBodyCodec.Encode(representation, SharedReadModel.Base(state)).Body)], []));
                    journal.AppendEventFrame(null, GraphEnvelopeCodec.Encode(revision, new ObjectId(1)), (uint)GraphFrameKind.State).Unwrap();
                }
            }

            void Target() {
                FrameAddress? previousRevision = null;
                EventAddress? previousEvent = null;
                for (int step = 0; step <= Cell.Ancestors; step++) {
                    // Final target graph is identical for every depth; all three object version chains grow.
                    byte firstValue = step == Cell.Ancestors ? (byte)11 : (byte)(40 + step % 100);
                    ObjectVersionRecord[] rows = Rows(representation, firstValue, previousRevision);
                    StateRevision revision = previousRevision is { } prior
                        ? StateRevision.CreateObjectHeadMapDelta(prior, rows, [])
                        : StateRevision.CreateObjectHeadMapBase(null, rows, []);
                    targetStateRevision = states.AppendDurably(revision);
                    targetState = journal.AppendEventFrame(previousEvent,
                        GraphEnvelopeCodec.Encode(targetStateRevision, new ObjectId(1)), (uint)GraphFrameKind.State).Unwrap();
                    previousRevision = targetStateRevision;
                    previousEvent = targetState;
                }
                Assert.Equal(Cell.Ancestors + 1, states.ReadObjectVersionChain(targetStateRevision, 1).Records.Count);
                FrameAddress eventRevision = states.AppendDurably(StateRevision.CreateObjectHeadMapBase(targetStateRevision,
                    Rows(representation, 21, null), []));
                targetEvent = journal.AppendEventFrame(targetState,
                    GraphEnvelopeCodec.Encode(eventRevision, new ObjectId(1)), (uint)GraphFrameKind.Event).Unwrap();
            }

            if (Cell.TargetAfterHistory) { Orphans(); Target(); }
            else { Target(); Orphans(); }
            journal.CreateBranch("main", targetEvent).Unwrap();
            Assert.True(journal.CreateTag(StateTag, targetState).Unwrap());
            Assert.True(journal.CreateTag(EventTag, targetEvent).Unwrap());
            for (int index = 0; index < Cell.ExtraTags; index++) {
                Assert.True(journal.CreateTag($"unused-{index:D4}", targetState).Unwrap());
            }
            Assert.Single(journal.ListBranches());
            Assert.Equal(1 + Cell.ExtraSchemas, schemas.Count);
        }
        // Full Audit is an independent fixture check, never called by a measured Open.
        Dictionary<string, string> beforeAudit = Snapshot();
        RepositoryAuditReport audit = RepositoryMaintenance.Audit(Path);
        Assert.True(audit.IsHealthy, string.Join("; ", audit.Findings.Select(f => f.Code + ": " + f.Message)));
        Assert.Equal(RepositoryAuditCompletion.Completed, audit.Completion);
        int expectedFrames = Cell.UnrelatedHistory + Cell.Ancestors + 2;
        Assert.Equal((long)expectedFrames, audit.StateFrames);
        Assert.Equal((long)expectedFrames, audit.JournalEvents);
        AssertUnchanged(beforeAudit);
        var refs = Directory.GetFiles(System.IO.Path.Combine(Path, "journal", "refs"), "*.rbf", SearchOption.AllDirectories);
        Facts = new(expectedFrames, expectedFrames, Cell.Ancestors + 1, 3, 1 + Cell.ExtraSchemas,
            1, 2 + Cell.ExtraTags, FileHash(System.IO.Path.Combine(Path, "schemas.rbf")),
            refs.Length, refs.Sum(file => new FileInfo(file).Length),
            targetState.SegmentNumber, targetState.Ticket.Offset, targetStateRevision.FileNumber, targetStateRevision.FrameTicket.Offset);
    }

    private static ObjectVersionRecord[] Rows(RepresentationId representation, byte firstValue, FrameAddress? prior) {
        ObjectVersionRecord[] rows = new ObjectVersionRecord[3];
        for (uint id = 1; id <= 3; id++) {
            uint next = id == 3 ? 1 : id + 1;
            State state = new(next, next, 0u, (byte)(firstValue + id - 1));
            PreparedBaseBody body = SharedReadModel.Base(state);
            rows[id - 1] = prior is { } address
                ? ObjectVersionRecord.CreateDelta(id, address, body.Body)
                : ObjectVersionRecord.CreateBase(id, BaseObjectBodyCodec.Encode(representation, body).Body);
        }
        return rows;
    }

    internal static ulong AssertGraph(object value, byte firstValue) {
        Node root = Assert.IsType<Node>(value);
        Node cursor = root;
        ulong checksum = 14695981039346656037UL;
        HashSet<Node> seen = new(ReferenceEqualityComparer.Instance);
        for (int index = 0; index < 3; index++) {
            Assert.True(seen.Add(cursor));
            Assert.Equal((byte)(firstValue + index), cursor.Value);
            Assert.Null(cursor.Text);
            Assert.Null(cursor.Links);
            Assert.Same(cursor.Next, cursor.Alias);
            checksum = unchecked((checksum ^ cursor.Value) * 1099511628211UL);
            cursor = Assert.IsType<Node>(cursor.Next);
        }
        Assert.Same(root, cursor);
        return checksum;
    }

    internal Dictionary<string, string> Snapshot() => Directory.GetFiles(Path, "*", SearchOption.AllDirectories)
        .ToDictionary(file => System.IO.Path.GetRelativePath(Path, file), FileHash, StringComparer.Ordinal);

    internal void AssertUnchanged(Dictionary<string, string> before) {
        Dictionary<string, string> after = Snapshot();
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var entry in before) { Assert.Equal(entry.Value, after[entry.Key]); }
    }

    private static string FileHash(string path) {
        using FileStream file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    public void Dispose() => SharedReadModel.DeleteFixture(Path, Prefix);
}

internal sealed record DB085CostCell(string Axis, int Scale, bool TargetAfterHistory = false) {
    internal int UnrelatedHistory => Axis == "history" ? Scale : 0;
    internal int Ancestors => Axis == "ancestors" ? Scale : 0;
    internal int ExtraSchemas => Axis == "schema" ? Scale : 0;
    internal int ExtraTags => Axis == "catalog" ? Scale : 0;
}

internal sealed record DB085FixtureFacts(long StateFrames, long JournalEvents, int StateObjectVersionChainLength,
    int GraphNodes, int SchemaCount, int BranchCount, int TagCount, string SchemaSha256,
    int RefRbfFiles, long RefRbfBytes, uint TargetStateJournalSegment, long TargetStateJournalOffset,
    uint TargetStateRevisionSegment, long TargetStateRevisionOffset);
