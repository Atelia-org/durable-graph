
using Atelia.HeadStore;
using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;
using PublicationStore = Atelia.HeadStore.HeadStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class RepositoryRootCodecTests : IDisposable {
    private const string Prefix = "durable-repository-root-";
    private readonly string _root = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Fact]
    public void RequiredBranchAndSharedRefsUseDistinctHeadersAndSelectedKeyGrammar() {
        using RepositoryResources resources = RepositoryResources.CreateNew(_root);
        Assert.Equal(3u, resources.Heads.ReservedHeadIdUpperBound);
        var initialBranches = resources.Heads.ReadHead(1).Unwrap();
        var initialShared = resources.Heads.ReadHead(2).Unwrap();
        Assert.Equal("P0R1"u8.ToArray(), initialBranches.Payload[..4].ToArray());
        Assert.Equal("P0S1"u8.ToArray(), initialShared.Payload[..4].ToArray());
        Assert.Empty(NamedFrameMapCodec.Decode(initialBranches.Payload[4..]).Unwrap());
        Assert.Empty(NamedFrameMapCodec.Decode(initialShared.Payload[4..]).Unwrap());
        RepositoryResources.ValidateBranchName("main");
        RepositoryResources.ValidateBranchName("branch-name.1");
        FrameAddress head = Seed(resources);
        var extra = resources.PrepareBranchRoot("branch-name.1", head);
        resources.PublishBranchRoot(extra, out _);
        var branches = NamedFrameMapCodec.Decode(resources.Heads.ReadHead(1).Unwrap().Payload[4..]).Unwrap();
        Assert.Equal(new[] { "branch/branch-name.1", "branch/main" }, branches.Keys.Order());
        Assert.All(branches.Values, address => Assert.Equal(head, address));
        var shared = NamedFrameMapCodec.Decode(resources.Heads.ReadHead(2).Unwrap().Payload[4..]).Unwrap();
        Assert.Equal("schema", Assert.Single(shared).Key);
        Assert.Equal(resources.SharedSchemaRoot, shared["schema"]);
        Assert.Equal(StoredGraphKind.Meta, resources.GraphStore.ReadMetadata(shared["schema"]).Kind);
    }

    [Theory]
    [InlineData(1u, "magic")]
    [InlineData(1u, "version")]
    [InlineData(1u, "header-role")]
    [InlineData(1u, "unknown")]
    [InlineData(1u, "schema")]
    [InlineData(1u, "branch/")]
    [InlineData(1u, "branch/Upper")]
    [InlineData(1u, "branch/main.lock")]
    [InlineData(1u, "branch/bad/name")]
    [InlineData(2u, "magic")]
    [InlineData(2u, "version")]
    [InlineData(2u, "header-role")]
    [InlineData(2u, "unknown")]
    [InlineData(2u, "branch/main")]
    [InlineData(2u, "schema-extra")]
    public void PersistedUnknownOrInvalidRootKeyRejectsOpenWithoutMutation(uint refId, string mutation) {
        using (RepositoryResources resources = RepositoryResources.CreateNew(_root)) { Seed(resources); }
        using (RepositoryResources resources = RepositoryResources.OpenExisting(_root)) {
            // HeadStore owns opaque bytes. These are complete valid VS publications
            // whose required DG root grammar is deliberately invalid; there is no pending tail.
            FrameAddress selected = refId == 1 ? resources.GetBranchHead("main")!.Value : resources.SharedSchemaRoot!.Value;
            string validKey = refId == 1 ? "branch/main" : "schema";
            string key = mutation is "magic" or "version" or "header-role" or "schema-extra" ? validKey : mutation;
            Dictionary<string, FrameAddress> roots = new(StringComparer.Ordinal) { [key] = selected };
            if (mutation == "schema-extra") { roots["extra"] = selected; }
            byte[] map = NamedFrameMapCodec.Encode(roots).Unwrap();
            byte[] magic = refId == 1 ? "P0R1"u8.ToArray() : "P0S1"u8.ToArray();
            if (mutation == "header-role") { magic = refId == 1 ? "P0S1"u8.ToArray() : "P0R1"u8.ToArray(); }
            byte[] payload = [.. magic, .. map];
            if (mutation == "magic") { payload[0] = 0; }
            if (mutation == "version") { payload[3] = (byte)'2'; }
            resources.Heads.PublishHead(resources.Heads.ReadHead(refId).Unwrap().Id.HeadId, payload, out _).Unwrap();
        }
        var before = Snapshot();
        Assert.Throws<InvalidDataException>(() => RepositoryResources.OpenReadOnlyExisting(_root));
        AssertSnapshot(before);
        Assert.Throws<InvalidDataException>(() => RepositoryResources.OpenExisting(_root));
        AssertSnapshot(before);
    }

    [Fact]
    public void RealColdRootCapacityRefusalPreservesPublishedRootAndHealthyOwner() {
        using (RepositoryResources resources = RepositoryResources.CreateNew(_root)) { Seed(resources); }
        HeadEntryId accepted;
        HeadEntryId sharedAccepted;
        byte[] acceptedPayload, sharedPayload;
        string extraName;
        using (RepositoryResources resources = RepositoryResources.OpenExisting(_root)) {
            var initial = NamedFrameMapCodec.Decode(resources.Heads.ReadHead(1).Unwrap().Payload[4..]).Unwrap();
            FrameAddress head = resources.GetBranchHead("main")!.Value;
            var shared = resources.Heads.ReadHead(2).Unwrap();
            sharedAccepted = shared.Id;
            sharedPayload = shared.Payload.ToArray();
            int low = 0, high = 10_000;
            while (high - low > 1) {
                int count = low + (high - low) / 2;
                var body = NamedFrameMapCodec.Encode(Candidate(count));
                if (!body.IsFailure && body.Value!.Length + 4 <= PublicationStore.MaxHeadEntryPayloadLength) { low = count; }
                else { high = count; }
            }
            acceptedPayload = [.. "P0R1"u8, .. NamedFrameMapCodec.Encode(Candidate(low)).Unwrap()];
            extraName = Name(low);
            RepositoryResources.ValidateBranchName(extraName);
            Assert.True(acceptedPayload.Length <= PublicationStore.MaxHeadEntryPayloadLength);
            var refused = NamedFrameMapCodec.Encode(Candidate(low + 1));
            Assert.True(refused.IsFailure || refused.Value!.Length + 4 > PublicationStore.MaxHeadEntryPayloadLength);
            accepted = resources.Heads.PublishHead(resources.Heads.ReadHead(1).Unwrap().Id.HeadId, acceptedPayload, out _).Unwrap();

            Dictionary<string, FrameAddress> Candidate(int count) {
                var roots = new Dictionary<string, FrameAddress>(initial, StringComparer.Ordinal);
                for (int index = 0; index < count; index++) { roots["branch/" + Name(index)] = head; }
                return roots;
            }
        }
        var before = Snapshot();
        using (RepositoryResources resources = RepositoryResources.OpenExisting(_root)) {
            var branchBefore = resources.Heads.ReadHead(1).Unwrap();
            Assert.Equal(accepted, branchBefore.Id);
            FrameAddress head = resources.GetBranchHead("main")!.Value;
            long appended = resources.GraphStore.AppendedFrames;
            Assert.Throws<InvalidOperationException>(() => resources.PrepareBranchRoot(extraName, head));
            Assert.False(resources.IsFaulted);
            resources.RequireAvailable();
            Assert.Equal(appended, resources.GraphStore.AppendedFrames);
            Assert.Null(resources.GetBranchHead(extraName));
            var current = resources.Heads.ReadHead(1).Unwrap();
            Assert.Equal(accepted, current.Id);
            Assert.Equal(acceptedPayload, current.Payload.ToArray());
            var shared = resources.Heads.ReadHead(2).Unwrap();
            Assert.Equal(sharedAccepted, shared.Id);
            Assert.Equal(sharedPayload, shared.Payload.ToArray());
        }
        AssertSnapshot(before);
    }

    private static string Name(int index) => new string('a', 120) + index.ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
    private static FrameAddress Seed(RepositoryResources resources) {
        using RepositoryStorageFacade facade = new(resources);
        BranchSaveSession session = new(facade, "main", SharedReadModel.Models());
        using PreparedBranchSave save = session.Prepare(new Node { Value = 7 });
        return save.Commit().Business.Address;
    }
    private Dictionary<string, byte[]> Snapshot() => Directory.GetFiles(_root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
    private void AssertSnapshot(Dictionary<string, byte[]> expected) {
        Assert.Equal(expected.Keys.Order(), Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, bytes) in expected) { Assert.Equal(bytes, File.ReadAllBytes(path)); }
    }
    public void Dispose() => SharedReadModel.DeleteFixture(_root, Prefix);
}
