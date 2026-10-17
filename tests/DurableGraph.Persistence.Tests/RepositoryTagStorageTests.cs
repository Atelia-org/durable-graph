using System.Buffers;

using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class RepositoryTagStorageTests : IDisposable {
    private const string Prefix = "durable-repository-tag-storage-";
    private readonly string _root = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Fact]
    public void FullBookmarkDirectoryRejectsBeforePublicationAndPreservesExistingBindings() {
        using (Repository seed = Repository.CreateNew(_root, SharedReadModel.Models())) {
            using BranchCheckout main = seed.CreateSharedBranch("main", new Node());
        }
        string first = "0000" + new string('x', 1020);
        using (RepositoryResources resources = RepositoryResources.OpenExisting(_root)) {
            FrameAddress address = resources.GetBranchHead("main")!.Value;
            var map = new Dictionary<string, FrameAddress>(StringComparer.Ordinal);
            byte[] accepted = [.. "P0T1"u8, 0];
            for (int i = 0; ; i++) {
                map.Add(i.ToString("D4") + new string('x', 1020), address);
                var encoded = Atelia.HeadStore.NamedFrameMapCodec.Encode(map);
                if (encoded.IsFailure || encoded.Unwrap().Length + 4 > Atelia.HeadStore.HeadStore.MaxHeadEntryPayloadLength) { break; }
                accepted = [.. "P0T1"u8, .. encoded.Unwrap()];
            }
            resources.Heads.PublishHead(resources.Heads.ReadHead(3).Unwrap().Id.HeadId, accepted, out _).Unwrap();
        }
        var before = Snapshot();
        using (Repository repository = Repository.OpenExisting(_root, new StateModelRegistry())) {
            var address = repository.GetHead("main");
            Assert.Throws<InvalidOperationException>(() => repository.CreateTag(new string('z', 1024), address));
            Assert.False(repository.IsFaulted);
            Assert.Equal(address, repository.ResolveTag(first));
        }
        AssertSnapshot(before);
    }

    [Theory]
    [InlineData("missing-frame")]
    [InlineData("metadata")]
    public void SelectedGraphMetadataIsCheckedWithoutStartupTargetAudit(string damage) {
        using (Repository seed = Repository.CreateNew(_root, SharedReadModel.Models())) {
            using BranchCheckout main = seed.CreateSharedBranch("main", new Node());
            seed.CreateTag("good", main.Head);
        }
        using (RepositoryResources resources = RepositoryResources.OpenExisting(_root)) {
            FrameAddress bad = damage == "metadata"
                ? resources.Data.Append(GraphFrameCodec.GraphTag, [255]).Unwrap()
                : TestAddress(uint.MaxValue);
            var head = resources.Heads.ReadHead(3).Unwrap();
            var map = new Dictionary<string, FrameAddress>(Atelia.HeadStore.NamedFrameMapCodec.Decode(head.Payload[4..]).Unwrap()) { ["invalid"] = bad };
            byte[] payload = [.. "P0T1"u8, .. Atelia.HeadStore.NamedFrameMapCodec.Encode(map).Unwrap()];
            resources.Heads.PublishHead(head.Id.HeadId, payload, out _).Unwrap();
        }
        var before = Snapshot();
        using (Repository reader = Repository.OpenReadOnlyExisting(_root, new StateModelRegistry())) {
            Assert.Equal(reader.GetHead("main"), reader.ResolveTag("good"));
            Assert.ThrowsAny<Exception>(() => reader.ResolveTag("invalid"));
            Assert.False(reader.IsFaulted);
        }
        AssertSnapshot(before);
    }

    [Theory]
    [InlineData("magic")]
    [InlineData("short")]
    [InlineData("trailing")]
    [InlineData("name")]
    public void MalformedBookmarkDirectoryRejectsOpenWithoutRepair(string damage) {
        using (Repository seed = Repository.CreateNew(_root, SharedReadModel.Models())) {
            using BranchCheckout main = seed.CreateSharedBranch("main", new Node());
        }
        using (RepositoryResources resources = RepositoryResources.OpenExisting(_root)) {
            var head = resources.Heads.ReadHead(3).Unwrap();
            byte[] payload = damage switch {
                "magic" => [.. "BAD1"u8, 0],
                "short" => [.. "P0T1"u8],
                "trailing" => [.. "P0T1"u8, 0, 0],
                _ => [.. "P0T1"u8, .. Atelia.HeadStore.NamedFrameMapCodec.Encode(
                    new Dictionary<string, FrameAddress> { [""] = resources.GetBranchHead("main")!.Value }).Unwrap()]
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
            using BranchCheckout main = seed.CreateSharedBranch("main", new Node());
        }
        FrameAddress frame;
        using (RepositoryResources resources = RepositoryResources.OpenExisting(_root)) {
            ArrayBufferWriter<byte> bytes = new();
            BareValueWriter writer = new(bytes);
            writer.WriteByte(TypedBaseBodyCodec.FormatVersion);
            writer.WriteByte(1);
            writer.WriteVarUInt32(123456); // A missing descriptor in the actual pinned S.
            writer.WriteByte(7);
            frame = resources.GraphStore.Append(StoredGraphKind.State, null, resources.SharedSchemaRoot,
                new(null, new(1), [ObjectVersionRecord.CreateBase(1, bytes.WrittenSpan)])).Address;
            byte[] payload = new byte[FrameAddress.EncodedSize];
            frame.TryWrite(payload);
            resources.CreateStoredTag("unknown-schema", frame, out _);
        }
        using Repository reader = Repository.OpenReadOnlyExisting(_root, SharedReadModel.Models());
        CheckpointAddress checkpoint = reader.ResolveTag("unknown-schema");
        Assert.Equal(frame, checkpoint.RevisionAddress);
        Assert.Throws<InvalidDataException>(() => reader.ReadState(checkpoint));
        Assert.False(reader.IsFaulted);
    }

    [Fact]
    public void RequiredBookmarkHeadTailRecoversBeforeDirectoryLoad() {
        FrameAddress saved;
        using (Repository seed = Repository.CreateNew(_root, SharedReadModel.Models())) {
            using BranchCheckout main = seed.CreateSharedBranch("main", new Node());
            saved = main.Head.RevisionAddress;
            seed.CreateTag("bookmark", main.Head);
        }
        string bucket = Path.Combine(_root, "versions", "refs", "00000003.rbf");
        byte[] complete = File.ReadAllBytes(bucket);
        File.WriteAllBytes(bucket, [.. complete, 1]);
        using (Repository writable = Repository.OpenExisting(_root, new StateModelRegistry())) {
            Assert.Equal(complete.Length, new FileInfo(bucket).Length);
            Assert.Equal(saved, writable.ResolveTag("bookmark").RevisionAddress);
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
