using System.Buffers;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class RepositoryTagStorageTests : IDisposable {
    private const string Prefix = "durable-repository-tag-storage-";
    private readonly string _root = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("short-payload")]
    [InlineData("long-payload")]
    [InlineData("invalid-address")]
    [InlineData("missing-frame")]
    [InlineData("metadata")]
    public void SelectedTagPayloadAndGraphMetadataAreCheckedWithoutStartupTagAudit(string damage) {
        using (Repository seed = Repository.CreateNew(_root, SharedReadModel.Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            using BranchCheckout main = seed.CreateBranch("main", new Node());
            seed.CreateTag("good", main.Head);
        }
        using (GraphResources resources = GraphResources.OpenExisting(_root)) {
            FrameAddress bad = damage == "metadata"
                ? resources.Data.Append(P0GraphWire.GraphTag, [255]).Unwrap()
                : TestAddress(uint.MaxValue);
            byte[] payload = damage switch {
                "short-payload" => [0],
                "long-payload" => new byte[FrameAddress.EncodedSize + 1],
                "invalid-address" => new byte[FrameAddress.EncodedSize],
                _ => new byte[FrameAddress.EncodedSize]
            };
            if (damage is "missing-frame" or "metadata") { Assert.True(bad.TryWrite(payload)); }
            resources.Versions.CreateTag("invalid", payload, out _).Unwrap();
        }
        var before = Snapshot();
        using (Repository reader = Repository.OpenReadOnlyExisting(_root, new StateModelRegistry())) {
            Assert.Equal(reader.GetHead("main"), reader.ResolveTag("good"));
            Assert.ThrowsAny<Exception>(() => reader.ResolveTag("invalid"));
            Assert.False(reader.IsFaulted);
        }
        AssertSnapshot(before);
    }

    [Fact]
    public void TagMetadataCanResolveBeforeSelectedGraphRejectsUnknownSchema() {
        using (Repository seed = Repository.CreateNew(_root, SharedReadModel.Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            using BranchCheckout main = seed.CreateBranch("main", new Node());
        }
        FrameAddress frame;
        using (GraphResources resources = GraphResources.OpenExisting(_root)) {
            ArrayBufferWriter<byte> bytes = new();
            BareValueWriter writer = new(bytes);
            writer.WriteByte(P0TypedBodyCodec.FormatVersion);
            writer.WriteByte(1);
            writer.WriteVarUInt32(123456); // A missing descriptor in the actual pinned S.
            writer.WriteByte(7);
            frame = resources.MetaBootstrapStore.Append(P0GraphKind.State, null, resources.SharedMetaBootstrapRoot,
                new(null, new(1), [ObjectVersionRecord.CreateBase(1, bytes.WrittenSpan)])).Address;
            byte[] payload = new byte[FrameAddress.EncodedSize];
            frame.TryWrite(payload);
            resources.Versions.CreateTag("unknown-schema", payload, out _).Unwrap();
        }
        using Repository reader = Repository.OpenReadOnlyExisting(_root, SharedReadModel.Models());
        CheckpointAddress checkpoint = reader.ResolveTag("unknown-schema");
        Assert.Equal(frame, checkpoint.RevisionAddress);
        Assert.Throws<InvalidDataException>(() => reader.ReadState(checkpoint));
        Assert.False(reader.IsFaulted);
    }

    [Fact]
    public void TagControlTailRecoveryRemainsLazyUntilNamedAccess() {
        FrameAddress saved;
        using (Repository seed = Repository.CreateNew(_root, SharedReadModel.Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            using BranchCheckout main = seed.CreateBranch("main", new Node());
            saved = main.Head.RevisionAddress;
            seed.CreateTag("bookmark", main.Head);
        }
        string bucket = Assert.Single(Directory.GetFiles(Path.Combine(_root, "versions", "tags"), "*.rbf", SearchOption.AllDirectories));
        byte[] complete = File.ReadAllBytes(bucket);
        File.WriteAllBytes(bucket, [.. complete, 1]);
        using (Repository writable = Repository.OpenExisting(_root, new StateModelRegistry(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            Assert.Equal(complete.Length + 1, new FileInfo(bucket).Length);
            Assert.DoesNotContain(ResourcesOf(writable).Versions.RecoveryReports,
                report => report.FileKind == Atelia.VersionStore.VersionStoreRecoveryFileKind.TagBucket);
            Assert.Equal(saved, writable.ResolveTag("bookmark").RevisionAddress);
            Assert.Contains(ResourcesOf(writable).Versions.RecoveryReports,
                report => report.FileKind == Atelia.VersionStore.VersionStoreRecoveryFileKind.TagBucket &&
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
