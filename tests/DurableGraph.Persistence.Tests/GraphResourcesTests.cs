using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class GraphResourcesTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"durable-graph-resources-{Guid.NewGuid():N}");
    private static readonly DurableSchema Schema = new("ResourcesNode", 1, new DurableFieldInfo(1, TypeTag.Int32));

    [Fact]
    public void ResourcesOwnDataWithoutPublicationAndReadOnlyOpenCannotWrite() {
        FrameAddress address;
        using (GraphResources resources = GraphResources.CreateNew(_root)) {
            resources.RequireWritable();
            resources.Schemas.Register(Schema);
            address = resources.States.AppendDurably(EmptyRevision());
        }
        Assert.False(File.Exists(Path.Combine(_root, "publication.rbf")));
        var before = Snapshot();
        using (GraphResources reader = GraphResources.OpenReadOnlyExisting(_root)) {
            Assert.True(reader.IsReadOnly);
            Assert.False(reader.IsFaulted);
            reader.RequireAvailable();
            Assert.Equal(Schema, reader.Schemas.GetRequired("ResourcesNode", 1));
            Assert.Empty(reader.States.Read(address).LocalObjects);
            Assert.Throws<InvalidOperationException>(reader.RequireWritable);
            Assert.Throws<InvalidOperationException>(() => reader.Schemas.Register(Schema));
            Assert.Throws<InvalidOperationException>(() => reader.States.AppendDurably(EmptyRevision()));
        }
        AssertUnchanged(before);
        using GraphResources reopened = GraphResources.OpenExisting(_root);
        Assert.False(reopened.IsReadOnly);
        reopened.RequireWritable();
        Assert.Empty(reopened.States.Read(address).LocalObjects);
    }

    [Fact]
    public void MissingResourcesAreNeverCreatedByReadOnlyOpen() {
        Assert.Throws<DirectoryNotFoundException>(() => GraphResources.OpenReadOnlyExisting(_root));
        Assert.False(Directory.Exists(_root));
        Directory.CreateDirectory(_root);
        Assert.Throws<FileNotFoundException>(() => GraphResources.OpenReadOnlyExisting(_root));
        Assert.Empty(Directory.GetFileSystemEntries(_root));
        using (IRbfFile schemaFile = RbfFile.CreateNew(Path.Combine(_root, "schemas.rbf"))) {
            schemaFile.DurableFlush();
        }
        var before = Snapshot();
        Assert.Throws<DirectoryNotFoundException>(() => GraphResources.OpenReadOnlyExisting(_root));
        Assert.False(Directory.Exists(Path.Combine(_root, "state")));
        AssertUnchanged(before);
    }

    [Theory]
    [InlineData("schema", "truncate-aligned", false)]
    [InlineData("schema", "truncate-unaligned", false)]
    [InlineData("schema", "trailer-crc", false)]
    [InlineData("state", "truncate-aligned", false)]
    [InlineData("state", "truncate-unaligned", false)]
    [InlineData("state", "trailer-crc", false)]
    [InlineData("schema", "truncate-aligned", true)]
    [InlineData("schema", "truncate-unaligned", true)]
    [InlineData("schema", "trailer-crc", true)]
    [InlineData("state", "truncate-aligned", true)]
    [InlineData("state", "truncate-unaligned", true)]
    [InlineData("state", "trailer-crc", true)]
    public void EveryOpenRejectsBadTailsWithoutRepair(string component, string damage, bool readOnly) {
        using (GraphResources resources = GraphResources.CreateNew(_root)) {
            resources.Schemas.Register(Schema);
            resources.States.AppendDurably(EmptyRevision());
        }
        string path = component == "schema" ? Path.Combine(_root, "schemas.rbf") :
            Assert.Single(Directory.GetFiles(Path.Combine(_root, "state"), "*.rbf", SearchOption.AllDirectories));
        byte[] bytes = File.ReadAllBytes(path);
        switch (damage) {
            case "truncate-aligned": bytes = bytes[..^4]; break;
            case "truncate-unaligned": bytes = bytes[..^1]; break;
            case "trailer-crc": bytes[^8] ^= 0x20; break;
            default: throw new InvalidOperationException(damage);
        }
        File.WriteAllBytes(path, bytes);
        var before = Snapshot();
        Assert.ThrowsAny<Exception>(() => {
            RbfSegmentStoreOptions options = new();
            using GraphResources rejected = readOnly ? GraphResources.OpenReadOnlyExisting(_root, options) : GraphResources.OpenExisting(_root, options);
        });
        AssertUnchanged(before);
    }

    [Fact]
    public void ReadOnlyOpenAlsoValidatesHistoricalSegmentTails() {
        FrameAddress first;
        FrameAddress second;
        using (GraphResources resources = GraphResources.CreateNew(_root,
            new() { NewStoreLayout = RbfSegmentStoreLayout.Flat, SegmentSizeThresholdBytes = 64 })) {
            first = resources.States.AppendDurably(StateRevision.CreateObjectHeadMapBase(null,
                [ObjectVersionRecord.CreateBase(1, new byte[128])], []));
            second = resources.States.AppendDurably(EmptyRevision());
        }
        Assert.NotEqual(first.FileNumber, second.FileNumber);
        string historical = Directory.GetFiles(Path.Combine(_root, "state"), "*.rbf", SearchOption.AllDirectories).Order().First();
        byte[] bytes = File.ReadAllBytes(historical);
        File.WriteAllBytes(historical, bytes[..^4]);
        var before = Snapshot();
        Assert.ThrowsAny<Exception>(() => GraphResources.OpenReadOnlyExisting(_root));
        AssertUnchanged(before);
    }

    [Fact]
    public void FaultAndDisposePreventResourceReuseAndReleaseHandles() {
        GraphResources resources = GraphResources.CreateNew(_root);
        resources.MarkFaulted();
        Assert.True(resources.IsFaulted);
        Assert.Throws<InvalidOperationException>(resources.RequireAvailable);
        Assert.Throws<InvalidOperationException>(resources.RequireWritable);
        resources.Dispose();
        resources.Dispose();
        Assert.Throws<ObjectDisposedException>(resources.RequireAvailable);
        using GraphResources reopened = GraphResources.OpenExisting(_root);
        Assert.False(reopened.IsFaulted);
        reopened.RequireWritable();
    }

    [Fact]
    public void DisposeEndsRetainedWarmedStateStoreWhileOwnedResultsRemainReadable() {
        using GraphResources resources = GraphResources.CreateNew(_root);
        StateRevisionStore retained = resources.States;
        FrameAddress address = retained.AppendDurably(StateRevision.CreateObjectHeadMapBase(null,
            [ObjectVersionRecord.CreateBase(7, [10, 20])], []));
        StateRevision revision = retained.Read(address);
        IReadOnlyDictionary<uint, FrameAddress> heads = retained.ReadLiveObjectHeadMap(address);
        Assert.Same(revision, retained.Read(address));
        Assert.Same(heads, retained.ReadLiveObjectHeadMap(address));

        resources.Dispose();
        resources.Dispose();

        Assert.Throws<ObjectDisposedException>(() => retained.Read(address));
        Assert.Throws<ObjectDisposedException>(() => retained.ReadLiveObjectHeadMap(address));
        Assert.Throws<ObjectDisposedException>(() => retained.AppendDurably(EmptyRevision()));
        Assert.Equal(address, heads[7]);
        Assert.Equal(new byte[] { 10, 20 }, Assert.Single(revision.LocalObjects).Body.ToArray());

        using GraphResources reopened = GraphResources.OpenReadOnlyExisting(_root);
        Assert.NotSame(revision, reopened.States.Read(address));
        Assert.Equal(address, reopened.States.ReadLiveObjectHeadMap(address)[7]);
    }

    private static StateRevision EmptyRevision() => StateRevision.CreateObjectHeadMapBase(null, [], []);

    private Dictionary<string, (byte[] Bytes, DateTime WriteTime)> Snapshot() =>
        Directory.GetFiles(_root, "*", SearchOption.AllDirectories).ToDictionary(
            static path => path, static path => (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path)));

    private void AssertUnchanged(Dictionary<string, (byte[] Bytes, DateTime WriteTime)> before) {
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, prior) in before) {
            Assert.Equal(prior.Bytes, File.ReadAllBytes(path));
            Assert.Equal(prior.WriteTime, File.GetLastWriteTimeUtc(path));
        }
    }

    public void Dispose() {
        string resolved = Path.GetFullPath(_root);
        if (!string.Equals(Path.GetDirectoryName(resolved), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("durable-graph-resources-", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Refusing to delete outside this test fixture.");
        }
        if (Directory.Exists(resolved)) { Directory.Delete(resolved, recursive: true); }
    }
}
