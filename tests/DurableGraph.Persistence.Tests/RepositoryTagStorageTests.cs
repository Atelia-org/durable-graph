using Atelia.HeadStore;
using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;
using System.Buffers;

using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class RepositoryTagStorageTests : IDisposable {
    private const string Prefix = "durable-repository-tag-storage-";
    private readonly string _root = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Fact]
    public void BookmarkDirectoryCanExceedHeadPayloadLimitWhileItsPublishedPointerRemainsSmall() {
        CommitId target;
        using (Repository repository = Repository.CreateNew(_root, SharedReadModel.Models())) {
            BranchInfo main = repository.CreateRootBranchFromState("main", new Node());
            target = repository.ReadCurrentCommit(main.Id).Commit.Id;
            // The retired whole-table format could not hold these UTF-8 names in one 1 MiB payload.
            for (int i = 0; i < 350; i++) { repository.CreateTag(Name(i), target); }
            var pointer = Resources(repository).Heads.ReadHead(3).Unwrap();
            Assert.Equal(5 + FrameAddress.EncodedSize, pointer.Payload.Length);
        }
        using Repository reopened = Repository.OpenReadOnlyExisting(_root, new StateModelRegistry());
        Assert.Equal(target, reopened.ResolveTag(Name(0)));
        Assert.Equal(target, reopened.ResolveTag(Name(349)));
        static string Name(int index) => index.ToString("D4") + new string('界', 1020);
    }

    [Theory]
    [InlineData("missing-frame")]
    [InlineData("metadata")]
    public void SelectedGraphMetadataIsCheckedWithoutStartupTargetAudit(string damage) {
        using (Repository seed = Repository.CreateNew(_root, SharedReadModel.Models())) {
            using WorkingCopy main = seed.OpenWorkingCopy(seed.CreateRootBranchFromState("main", new Node()).Id);
            seed.CreateTag("good", main.BaseCommitId);
        }
        using (RepositoryResources resources = RepositoryResources.OpenExisting(_root)) {
            FrameAddress bad = damage == "metadata"
                ? resources.Data.Append(GraphFrameCodec.GraphTag, [255]).Unwrap()
                : TestAddress(uint.MaxValue);
            HeadEntryId badEntry = resources.Heads.PublishHead(Assert.Single(resources.ListBranches()).Key,
                BusinessEntryPayloadCodec.Encode(bad, []), out _).Unwrap();
            var head = resources.Heads.ReadHead(3).Unwrap();
            FrameAddress accepted = ManagementDirectoryPointer.Decode(ManagementDirectoryKind.Bookmarks, head.Payload)!.Value;
            DecodedGraphRows rows = ManagementDirectoryReader.ReadBookmarks(resources.GraphStore, accepted);
            PreparedDirectoryWrite candidate = ManagementDirectoryWriter.PrepareBookmarkAddition(resources.GraphStore, rows, "invalid", badEntry);
            StoredGraphRevision graph = resources.GraphStore.AppendMeta(accepted, candidate.Write);
            resources.Heads.PublishHead(head.Id.HeadId,
                ManagementDirectoryPointer.Encode(ManagementDirectoryKind.Bookmarks, graph.Address), out _).Unwrap();
        }
        var before = Snapshot();
        using (Repository reader = Repository.OpenReadOnlyExisting(_root, new StateModelRegistry())) {
            Assert.Equal(CommitKind.State, reader.ReadCommitInfo(reader.ResolveTag("good")).Kind);
            Assert.ThrowsAny<Exception>(() => reader.ResolveTag("invalid"));
            Assert.False(reader.IsFaulted);
        }
        AssertSnapshot(before);
    }

    [Theory]
    [InlineData("magic")]
    [InlineData("short")]
    [InlineData("trailing")]
    [InlineData("wrong-role")]
    public void MalformedBookmarkDirectoryRejectsOpenWithoutRepair(string damage) {
        using (Repository seed = Repository.CreateNew(_root, SharedReadModel.Models())) {
            using WorkingCopy main = seed.OpenWorkingCopy(seed.CreateRootBranchFromState("main", new Node()).Id);
        }
        using (RepositoryResources resources = RepositoryResources.OpenExisting(_root)) {
            var head = resources.Heads.ReadHead(3).Unwrap();
            byte[] payload = damage switch {
                "magic" => [.. "BAD1"u8, 0],
                "short" => [.. "P0T2"u8],
                "trailing" => [.. "P0T2"u8, 0, 0],
                _ => ManagementDirectoryPointer.Encode(ManagementDirectoryKind.Bookmarks, resources.SharedSchemaRoot)
            };
            resources.Heads.PublishHead(head.Id.HeadId, payload, out _).Unwrap();
        }
        var before = Snapshot();
        Assert.ThrowsAny<Exception>(() => Repository.OpenReadOnlyExisting(_root, new StateModelRegistry()));
        Assert.ThrowsAny<Exception>(() => Repository.OpenExisting(_root, new StateModelRegistry()));
        AssertSnapshot(before);
    }

    [Fact]
    public void TagMetadataCanResolveBeforeSelectedGraphRejectsUnknownSchema() {
        using (Repository seed = Repository.CreateNew(_root, SharedReadModel.Models())) {
            using WorkingCopy main = seed.OpenWorkingCopy(seed.CreateRootBranchFromState("main", new Node()).Id);
        }
        FrameAddress frame;
        using (RepositoryResources resources = RepositoryResources.OpenExisting(_root)) {
            ArrayBufferWriter<byte> bytes = new();
            BareValueWriter writer = new(bytes);
            writer.WriteByte(TypedBaseBodyCodec.FormatVersion);
            writer.WriteByte(1);
            writer.WriteVarUInt32(123456); // A missing descriptor in the actual pinned S.
            writer.WriteByte(7);
            HeadId branch = Assert.Single(resources.ListBranches()).Key;
            FrameAddress source = resources.ReadCurrent(branch).Address;
            frame = resources.GraphStore.AppendBusiness(StoredGraphKind.State, source, resources.SharedSchemaRoot,
                new(source, new(1), [ObjectVersionRecord.CreateBase(1, bytes.WrittenSpan)])).Address;
            HeadEntryId entry = resources.Heads.PublishHead(branch, BusinessEntryPayloadCodec.Encode(frame, []), out _).Unwrap();
            resources.CreateStoredTag("unknown-schema", entry, out _);
        }
        using Repository reader = Repository.OpenReadOnlyExisting(_root, SharedReadModel.Models());
        CommitId checkpoint = reader.ResolveTag("unknown-schema");
        Assert.Equal(frame, Describe(reader, checkpoint).Address);
        Assert.Throws<InvalidDataException>(() => reader.LoadState(checkpoint));
        Assert.False(reader.IsFaulted);
    }

    [Fact]
    public void RequiredBookmarkHeadTailRecoversBeforeDirectoryLoad() {
        CommitId saved;
        using (Repository seed = Repository.CreateNew(_root, SharedReadModel.Models())) {
            using WorkingCopy main = seed.OpenWorkingCopy(seed.CreateRootBranchFromState("main", new Node()).Id);
            saved = main.BaseCommitId;
            seed.CreateTag("bookmark", main.BaseCommitId);
        }
        string bucket = Path.Combine(_root, "versions", "refs", "00000003.rbf");
        byte[] complete = File.ReadAllBytes(bucket);
        File.WriteAllBytes(bucket, [.. complete, 1]);
        using (Repository writable = Repository.OpenExisting(_root, new StateModelRegistry())) {
            Assert.Equal(complete.Length, new FileInfo(bucket).Length);
            Assert.Equal(saved, writable.ResolveTag("bookmark"));
            Assert.Contains(ResourcesOf(writable).Heads.RecoveryReports,
                report => report.FileKind == Atelia.HeadStore.HeadStoreRecoveryFileKind.Head &&
                    report.Recovery.Action == Atelia.Rbf.RbfTailRecoveryAction.Truncated);
        }
        Assert.Equal(complete, File.ReadAllBytes(bucket));
    }

    private Dictionary<string, byte[]> Snapshot() => Directory.GetFiles(_root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
    private void AssertSnapshot(Dictionary<string, byte[]> expected) {
        Assert.Equal(expected.Keys.Order(), Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, bytes) in expected) { Assert.Equal(bytes, File.ReadAllBytes(path)); }
    }
    public void Dispose() => SharedReadModel.DeleteFixture(_root, Prefix);
}
