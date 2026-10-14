using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.VersionStore;
using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;
using PublicationStore = Atelia.VersionStore.VersionStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class RepositoryRootCodecTests : IDisposable {
    private const string Prefix = "durable-repository-root-";
    private readonly string _root = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Fact]
    public void RequiredBranchAndSharedRefsUseDistinctHeadersAndSelectedKeyGrammar() {
        using GraphResources resources = GraphResources.CreateNew(_root);
        Assert.Equal(2u, resources.Versions.ReservedRefIdUpperBound);
        var initialBranches = resources.Versions.ReadRef(1).Unwrap();
        var initialShared = resources.Versions.ReadRef(2).Unwrap();
        Assert.Equal("P0R1"u8.ToArray(), initialBranches.Payload[..4].ToArray());
        Assert.Equal("P0S1"u8.ToArray(), initialShared.Payload[..4].ToArray());
        Assert.Empty(RootMapCodec.Decode(initialBranches.Payload[4..]).Unwrap());
        Assert.Empty(RootMapCodec.Decode(initialShared.Payload[4..]).Unwrap());
        GraphResources.ValidateBranchName("main");
        GraphResources.ValidateBranchName("branch-name.1");
        FrameAddress head = Seed(resources);
        var extra = resources.PrepareBranchRoot("branch-name.1", head);
        resources.PublishBranchRoot(extra, out _);
        var branches = RootMapCodec.Decode(resources.Versions.ReadRef(1).Unwrap().Payload[4..]).Unwrap();
        Assert.Equal(new[] { "branch/branch-name.1", "branch/main" }, branches.Keys.Order());
        Assert.All(branches.Values, address => Assert.Equal(head, address));
        var shared = RootMapCodec.Decode(resources.Versions.ReadRef(2).Unwrap().Payload[4..]).Unwrap();
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
        using (GraphResources resources = GraphResources.CreateNew(_root)) { Seed(resources); }
        using (GraphResources resources = GraphResources.OpenExisting(_root)) {
            // VersionStore owns opaque bytes. These are complete valid VS publications
            // whose required DG root grammar is deliberately invalid; there is no pending tail.
            FrameAddress selected = refId == 1 ? resources.GetBranchHead("main")!.Value : resources.SharedSchemaRoot!.Value;
            string validKey = refId == 1 ? "branch/main" : "schema";
            string key = mutation is "magic" or "version" or "header-role" or "schema-extra" ? validKey : mutation;
            Dictionary<string, FrameAddress> roots = new(StringComparer.Ordinal) { [key] = selected };
            if (mutation == "schema-extra") { roots["extra"] = selected; }
            byte[] map = RootMapCodec.Encode(roots).Unwrap();
            byte[] magic = refId == 1 ? "P0R1"u8.ToArray() : "P0S1"u8.ToArray();
            if (mutation == "header-role") { magic = refId == 1 ? "P0S1"u8.ToArray() : "P0R1"u8.ToArray(); }
            byte[] payload = [.. magic, .. map];
            if (mutation == "magic") { payload[0] = 0; }
            if (mutation == "version") { payload[3] = (byte)'2'; }
            resources.Versions.PublishRef(resources.Versions.ReadRef(refId).Unwrap().Revision.RefId, payload, out _).Unwrap();
        }
        var before = Snapshot();
        Assert.Throws<InvalidDataException>(() => GraphResources.OpenReadOnlyExisting(_root));
        AssertSnapshot(before);
        Assert.Throws<InvalidDataException>(() => GraphResources.OpenExisting(_root));
        AssertSnapshot(before);
    }

    [Fact]
    public void RealColdRootCapacityRefusalPreservesPublishedRootAndHealthyOwner() {
        using (GraphResources resources = GraphResources.CreateNew(_root)) { Seed(resources); }
        RefRevision accepted;
        RefRevision sharedAccepted;
        byte[] acceptedPayload, sharedPayload;
        string extraName;
        using (GraphResources resources = GraphResources.OpenExisting(_root)) {
            var initial = RootMapCodec.Decode(resources.Versions.ReadRef(1).Unwrap().Payload[4..]).Unwrap();
            FrameAddress head = resources.GetBranchHead("main")!.Value;
            var shared = resources.Versions.ReadRef(2).Unwrap();
            sharedAccepted = shared.Revision;
            sharedPayload = shared.Payload.ToArray();
            int low = 0, high = 10_000;
            while (high - low > 1) {
                int count = low + (high - low) / 2;
                var body = RootMapCodec.Encode(Candidate(count));
                if (!body.IsFailure && body.Value!.Length + 4 <= PublicationStore.MaxRootPayloadLength) { low = count; }
                else { high = count; }
            }
            acceptedPayload = [.. "P0R1"u8, .. RootMapCodec.Encode(Candidate(low)).Unwrap()];
            extraName = Name(low);
            GraphResources.ValidateBranchName(extraName);
            Assert.True(acceptedPayload.Length <= PublicationStore.MaxRootPayloadLength);
            var refused = RootMapCodec.Encode(Candidate(low + 1));
            Assert.True(refused.IsFailure || refused.Value!.Length + 4 > PublicationStore.MaxRootPayloadLength);
            accepted = resources.Versions.PublishRef(resources.Versions.ReadRef(1).Unwrap().Revision.RefId, acceptedPayload, out _).Unwrap();

            Dictionary<string, FrameAddress> Candidate(int count) {
                var roots = new Dictionary<string, FrameAddress>(initial, StringComparer.Ordinal);
                for (int index = 0; index < count; index++) { roots["branch/" + Name(index)] = head; }
                return roots;
            }
        }
        var before = Snapshot();
        using (GraphResources resources = GraphResources.OpenExisting(_root)) {
            var branchBefore = resources.Versions.ReadRef(1).Unwrap();
            Assert.Equal(accepted, branchBefore.Revision);
            FrameAddress head = resources.GetBranchHead("main")!.Value;
            long appended = resources.GraphStore.AppendedFrames;
            Assert.Throws<InvalidOperationException>(() => resources.PrepareBranchRoot(extraName, head));
            Assert.False(resources.IsFaulted);
            resources.RequireAvailable();
            Assert.Equal(appended, resources.GraphStore.AppendedFrames);
            Assert.Null(resources.GetBranchHead(extraName));
            var current = resources.Versions.ReadRef(1).Unwrap();
            Assert.Equal(accepted, current.Revision);
            Assert.Equal(acceptedPayload, current.Payload.ToArray());
            var shared = resources.Versions.ReadRef(2).Unwrap();
            Assert.Equal(sharedAccepted, shared.Revision);
            Assert.Equal(sharedPayload, shared.Payload.ToArray());
        }
        AssertSnapshot(before);
    }

    private static string Name(int index) => new string('a', 120) + index.ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
    private static FrameAddress Seed(GraphResources resources) {
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
