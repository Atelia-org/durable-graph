using Atelia.VersionStore;
using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;
using PublicationStore = Atelia.VersionStore.VersionStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class RepositoryRootCodecTests : IDisposable {
    private const string Prefix = "durable-repository-root-";
    private readonly string _root = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Fact]
    public void RootCodewordFreezesMagicVersionAndSelectedKeyGrammar() {
        Assert.Equal(Convert.FromHexString("444752310100"), RepositoryRootCodec.Encode(new Dictionary<string, FrameAddress>()));
        Dictionary<string, FrameAddress> roots = new(StringComparer.Ordinal) {
            ["schema"] = TestAddress(), ["branch/main"] = TestAddress(2), ["branch/branch-name.1"] = TestAddress(3)
        };
        Assert.Equal(roots.OrderBy(pair => pair.Key), RepositoryRootCodec.Decode(RepositoryRootCodec.Encode(roots)).OrderBy(pair => pair.Key));
        Assert.Throws<InvalidDataException>(() => RepositoryRootCodec.Decode(Convert.FromHexString("004752310100")));
        Assert.Throws<InvalidDataException>(() => RepositoryRootCodec.Decode(Convert.FromHexString("444752310200")));
    }

    [Theory]
    [InlineData("magic")]
    [InlineData("version")]
    [InlineData("unknown")]
    [InlineData("branch/")]
    [InlineData("branch/Upper")]
    [InlineData("branch/main.lock")]
    [InlineData("branch/bad/name")]
    public void PersistedUnknownOrInvalidRootKeyRejectsOpenWithoutMutation(string key) {
        Seed();
        using (GraphResources resources = GraphResources.OpenLegacyExisting(_root)) {
            // VersionStore owns opaque root bytes; the DG required root decoder owns this grammar.
            byte[] map = RootMapCodec.Encode(new Dictionary<string, FrameAddress> { [key is "magic" or "version" ? "branch/main" : key] = resources.GetHead("main") }).Unwrap();
            byte[] payload = [.. "DGR1"u8, 1, .. map];
            if (key == "magic") { payload[0] = 0; }
            if (key == "version") { payload[4] = 2; }
            resources.Versions.PublishRef(resources.RootRevision.RefId, payload, out _).Unwrap();
        }
        var before = Snapshot();
        Assert.Throws<InvalidDataException>(() => GraphResources.OpenLegacyReadOnlyExisting(_root));
        AssertSnapshot(before);
        Assert.Throws<InvalidDataException>(() => GraphResources.OpenLegacyExisting(_root));
        AssertSnapshot(before);
    }

    [Fact]
    public void RealColdRootCapacityRefusalPreservesPublishedRootAndHealthyOwner() {
        Seed();
        RefRevision accepted;
        byte[] acceptedPayload;
        string extraName;
        using (GraphResources resources = GraphResources.OpenLegacyExisting(_root)) {
            var initial = RepositoryRootCodec.Decode(resources.Versions.ReadRef(1).Unwrap().Payload);
            FrameAddress head = resources.GetHead("main");
            int low = 0, high = 10_000;
            while (high - low > 1) {
                int count = low + (high - low) / 2;
                var body = RootMapCodec.Encode(Candidate(count));
                if (!body.IsFailure && body.Value!.Length + 5 <= PublicationStore.MaxRootPayloadLength) { low = count; }
                else { high = count; }
            }
            acceptedPayload = RepositoryRootCodec.Encode(Candidate(low));
            extraName = Name(low);
            Assert.True(acceptedPayload.Length <= PublicationStore.MaxRootPayloadLength);
            Assert.Throws<InvalidOperationException>(() => RepositoryRootCodec.Encode(Candidate(low + 1)));
            accepted = resources.Versions.PublishRef(resources.RootRevision.RefId, acceptedPayload, out _).Unwrap();

            Dictionary<string, FrameAddress> Candidate(int count) {
                var roots = new Dictionary<string, FrameAddress>(initial, StringComparer.Ordinal);
                for (int index = 0; index < count; index++) { roots[RepositoryRootCodec.BranchPrefix + Name(index)] = head; }
                return roots;
            }
        }
        var before = Snapshot();
        using (GraphResources resources = GraphResources.OpenLegacyExisting(_root)) {
            Assert.Equal(accepted, resources.RootRevision);
            FrameAddress head = resources.GetHead("main");
            Assert.Throws<InvalidOperationException>(() => resources.PrepareBranchRoot(extraName, head));
            Assert.False(resources.IsFaulted);
            resources.RequireAvailable();
            Assert.Equal(accepted, resources.RootRevision);
            Assert.False(resources.HasBranch(extraName));
            var current = resources.Versions.ReadRef(1).Unwrap();
            Assert.Equal(accepted, current.Revision);
            Assert.Equal(acceptedPayload, current.Payload.ToArray());
        }
        AssertSnapshot(before);
    }

    private static string Name(int index) => new string('a', 120) + index.ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
    private void Seed() {
        using GraphResources resources = GraphResources.CreateLegacyNew(_root);
        resources.PublishBranch("main", TestAddress(), out _);
    }
    private Dictionary<string, byte[]> Snapshot() => Directory.GetFiles(_root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
    private void AssertSnapshot(Dictionary<string, byte[]> expected) {
        Assert.Equal(expected.Keys.Order(), Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, bytes) in expected) { Assert.Equal(bytes, File.ReadAllBytes(path)); }
    }
    public void Dispose() => SharedReadModel.DeleteFixture(_root, Prefix);
}
