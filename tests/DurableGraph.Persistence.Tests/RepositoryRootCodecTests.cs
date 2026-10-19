using Atelia.HeadStore;
using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;
using PublicationStore = Atelia.HeadStore.HeadStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class RepositoryRootCodecTests : IDisposable {
    private const string Prefix = "durable-repository-root-";
    private readonly string _root = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Fact]
    public void RequiredControlsUseDistinctHeadersAndBranchLabelsAreSeparateFromIdentity() {
        using Repository repository = Repository.CreateNew(_root, SharedReadModel.Models());
        RepositoryResources resources = ResourcesOf(repository);
        Assert.Equal(3u, resources.Heads.ReservedHeadIdUpperBound);
        var initialBranches = resources.Heads.ReadHead(1).Unwrap();
        var initialShared = resources.Heads.ReadHead(2).Unwrap();
        var initialBookmarks = resources.Heads.ReadHead(3).Unwrap();
        Assert.Equal("P0R3"u8.ToArray(), initialBranches.Payload[..4].ToArray());
        Assert.Equal("P0S1"u8.ToArray(), initialShared.Payload[..4].ToArray());
        Assert.Equal("P0T2"u8.ToArray(), initialBookmarks.Payload[..4].ToArray());
        Assert.Null(ManagementDirectoryPointer.Decode(ManagementDirectoryKind.Branches, initialBranches.Payload));
        Assert.Null(ManagementDirectoryPointer.Decode(ManagementDirectoryKind.Bookmarks, initialBookmarks.Payload));
        Assert.Empty(NamedFrameMapCodec.Decode(initialShared.Payload[4..]).Unwrap());
        BranchInfo branch = repository.CreateRootBranchFromState("世界 / Main", new Node { Value = 7 });
        CommitId source = repository.ReadCurrentCommit(branch.Id).Commit.Id;
        BranchInfo fork = repository.ForkBranch(source, "世界 / Main");
        Assert.NotEqual(branch.Id, fork.Id);
        Assert.Equal(source, repository.ReadCurrentCommit(fork.Id).Commit.Id);
        Assert.Equal(2, resources.ListBranches().Count);
        Assert.All(resources.ListBranches().Values, label => Assert.Equal("世界 / Main", label));
        var directory = resources.Heads.ReadHead(1).Unwrap();
        Assert.Equal(17, directory.Payload.Length);
        FrameAddress graph = ManagementDirectoryPointer.Decode(ManagementDirectoryKind.Branches, directory.Payload)!.Value;
        Assert.Equal(StoredGraphKind.Meta, resources.GraphStore.ReadMetaGraph(graph).Kind);
        var shared = NamedFrameMapCodec.Decode(resources.Heads.ReadHead(2).Unwrap().Payload[4..]).Unwrap();
        Assert.Equal("schema", Assert.Single(shared).Key);
        Assert.Equal(resources.SharedSchemaRoot, shared["schema"]);
        Assert.Equal(StoredGraphKind.Meta, resources.GraphStore.ReadMetaGraph(shared["schema"]).Kind);
    }

    [Theory]
    [InlineData('1')]
    [InlineData('2')]
    public void LegacyThreeHeadDirectoryRejectsBothOpenModesWithoutMutation(char version) {
        byte[] payload;
        using (Repository repository = Repository.CreateNew(_root, SharedReadModel.Models())) {
            repository.CreateRootBranchFromState("main", new Node { Value = 7 });
            payload = ResourcesOf(repository).Heads.ReadHead(1).Unwrap().Payload.ToArray();
            Assert.True(payload.AsSpan().StartsWith("P0R3"u8));
        }
        using (var data = Atelia.FrameStore.FrameStore.Open(Path.Combine(_root, "data")))
        using (var heads = PublicationStore.Open(Path.Combine(_root, "versions"), data)) {
            payload[3] = (byte)version;
            var branch = heads.ReadHead(1).Unwrap();
            heads.PublishHead(branch.Id.HeadId, payload, out PublicationOutcome outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
        }
        var before = Snapshot();
        foreach (bool readOnly in new[] { true, false }) {
            InvalidDataException error = Assert.Throws<InvalidDataException>(() => {
                using Repository repository = readOnly
                    ? Repository.OpenReadOnlyExisting(_root, SharedReadModel.Models())
                    : Repository.OpenExisting(_root, SharedReadModel.Models());
            });
            Assert.Equal("Unknown management directory pointer format.", error.Message);
            AssertSnapshot(before);
        }
    }

    [Theory]
    [InlineData(1u, "magic")]
    [InlineData(1u, "version")]
    [InlineData(1u, "header-role")]
    [InlineData(1u, "presence")]
    [InlineData(1u, "truncated")]
    [InlineData(1u, "trailing")]
    [InlineData(3u, "magic")]
    [InlineData(3u, "header-role")]
    [InlineData(3u, "presence")]
    [InlineData(2u, "magic")]
    [InlineData(2u, "version")]
    [InlineData(2u, "header-role")]
    [InlineData(2u, "unknown")]
    [InlineData(2u, "branch/main")]
    [InlineData(2u, "schema-extra")]
    public void PersistedInvalidControlPayloadRejectsOpenWithoutMutation(uint headId, string mutation) {
        using (Repository repository = Repository.CreateNew(_root, SharedReadModel.Models())) {
            repository.CreateRootBranchFromState("main", new Node { Value = 7 });
        }
        // Publish complete malformed control data only after every DG owner/cache is closed.
        using (var data = Atelia.FrameStore.FrameStore.Open(Path.Combine(_root, "data")))
        using (var heads = PublicationStore.Open(Path.Combine(_root, "versions"), data)) {
            HeadEntry selected = heads.ReadHead(headId).Unwrap();
            byte[] payload = selected.Payload.ToArray();
            if (headId == 2 && mutation is "unknown" or "branch/main" or "schema-extra") {
                var roots = NamedFrameMapCodec.Decode(payload.AsSpan(4)).Unwrap();
                FrameAddress schema = roots["schema"];
                var bad = new Dictionary<string, FrameAddress>(StringComparer.Ordinal) { [mutation == "schema-extra" ? "schema" : mutation] = schema };
                if (mutation == "schema-extra") { bad["extra"] = schema; }
                payload = [.. "P0S1"u8, .. NamedFrameMapCodec.Encode(bad).Unwrap()];
            } else {
                switch (mutation) {
                    case "magic": payload[0] = 0; break;
                    case "version": payload[3] = (byte)'9'; break;
                    case "header-role": (headId == 2 ? "P0R3"u8 : "P0S1"u8).CopyTo(payload); break;
                    case "presence": payload[4] = 2; break;
                    case "truncated": payload = payload[..^1]; break;
                    case "trailing": payload = [.. payload, 0]; break;
                }
            }
            heads.PublishHead(selected.Id.HeadId, payload, out _).Unwrap();
        }
        var before = Snapshot();
        Assert.Throws<InvalidDataException>(() => RepositoryResources.OpenReadOnlyExisting(_root));
        AssertSnapshot(before);
        Assert.Throws<InvalidDataException>(() => RepositoryResources.OpenExisting(_root));
        AssertSnapshot(before);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("long")]
    [InlineData("unicode")]
    [InlineData("summary")]
    public void InvalidCreationInputRejectsBeforeAppendAndPreservesHealthyOwner(string invalid) {
        using (Repository repository = Repository.CreateNew(_root, SharedReadModel.Models())) {
            repository.CreateRootBranchFromState("main", new Node { Value = 7 });
        }
        // The old whole-table 1 MiB directory cap no longer exists. The applicable limits
        // are each label and Entry summary; the small D pointer stays constant-sized.
        var before = Snapshot();
        using (Repository repository = Repository.OpenExisting(_root, SharedReadModel.Models())) {
            RepositoryResources resources = ResourcesOf(repository);
            HeadEntry directory = resources.Heads.ReadHead(1).Unwrap(), shared = resources.Heads.ReadHead(2).Unwrap();
            long appended = resources.GraphStore.AppendedFrames;
            string label = invalid switch { "empty" => "", "long" => new string('a', 1025), "unicode" => new string((char)0xD800, 1), _ => "candidate" };
            byte[] summary = invalid == "summary" ? new byte[BusinessEntryPayloadCodec.MaxSummaryLength + 1] : [];
            Assert.ThrowsAny<ArgumentException>(() => repository.CreateRootBranchFromState(label, new Node { Value = 8 }, summary));
            Assert.False(repository.IsFaulted);
            Assert.Equal(appended, resources.GraphStore.AppendedFrames);
            Assert.Single(resources.ListBranches());
            Assert.Equal(directory.Id, resources.Heads.ReadHead(1).Unwrap().Id);
            Assert.Equal(shared.Id, resources.Heads.ReadHead(2).Unwrap().Id);
            Assert.Equal(17, resources.Heads.ReadHead(1).Unwrap().Payload.Length);
        }
        AssertSnapshot(before);
    }

    private Dictionary<string, byte[]> Snapshot() => Directory.GetFiles(_root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
    private void AssertSnapshot(Dictionary<string, byte[]> expected) {
        Assert.Equal(expected.Keys.Order(), Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, bytes) in expected) { Assert.Equal(bytes, File.ReadAllBytes(path)); }
    }
    public void Dispose() => SharedReadModel.DeleteFixture(_root, Prefix);
}
