using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class GraphResourcesTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"durable-graph-resources-{Guid.NewGuid():N}");
    private static readonly DurableSchema Schema = new("ResourcesNode", 1, new DurableFieldInfo(1, TypeTag.Int32));

    [Fact]
    public void SharedOwnerPersistsSchemaAndDataAndReadOnlyCannotWrite() {
        FrameAddress address;
        using (GraphResources resources = GraphResources.CreateLegacyNew(_root)) {
            resources.Schemas.Register(Schema);
            address = resources.States.Append(EmptyRevision());
            resources.Data.ConfirmDurable();
        }
        Assert.False(File.Exists(Path.Combine(_root, "schemas.rbf")));
        Assert.False(File.Exists(Path.Combine(_root, "repository.lock")));
        var before = Snapshot();
        using (GraphResources reader = GraphResources.OpenLegacyReadOnlyExisting(_root)) {
            Assert.True(reader.IsReadOnly);
            reader.RequireAvailable();
            Assert.Equal(Schema, reader.Schemas.GetRequired("ResourcesNode", 1));
            Assert.Empty(reader.States.Read(address).LocalObjects);
            Assert.Throws<InvalidOperationException>(reader.RequireWritable);
            Assert.Throws<InvalidOperationException>(() => reader.Schemas.Register(Schema));
            Assert.Throws<InvalidOperationException>(() => reader.States.Append(EmptyRevision()));
            using GraphResources second = GraphResources.OpenLegacyReadOnlyExisting(_root);
            Assert.Empty(second.States.Read(address).LocalObjects);
        }
        AssertUnchanged(before);
        using GraphResources reopened = GraphResources.OpenLegacyExisting(_root);
        Assert.Empty(reopened.States.Read(address).LocalObjects);
    }

    [Fact]
    public void MissingAndOldLayoutReadOnlyOpenNeverCreatesResources() {
        Assert.Throws<DirectoryNotFoundException>(() => GraphResources.OpenLegacyReadOnlyExisting(_root));
        Assert.False(Directory.Exists(_root));
        Directory.CreateDirectory(_root);
        Assert.Throws<InvalidDataException>(() => GraphResources.OpenLegacyReadOnlyExisting(_root));
        Assert.Empty(Directory.GetFileSystemEntries(_root));
        File.WriteAllBytes(Path.Combine(_root, "schemas.rbf"), []);
        var before = Snapshot();
        Assert.Throws<InvalidDataException>(() => GraphResources.OpenLegacyExisting(_root));
        AssertUnchanged(before);
    }

    [Fact]
    public void BackingLocksCoverWritersAndAllowOnlySharedReaders() {
        using (GraphResources writer = GraphResources.CreateLegacyNew(_root)) {
            Assert.ThrowsAny<Exception>(() => GraphResources.OpenLegacyExisting(_root));
            Assert.ThrowsAny<Exception>(() => GraphResources.OpenLegacyReadOnlyExisting(_root));
        }
        using GraphResources reader = GraphResources.OpenLegacyReadOnlyExisting(_root);
        using GraphResources second = GraphResources.OpenLegacyReadOnlyExisting(_root);
        Assert.ThrowsAny<Exception>(() => GraphResources.OpenLegacyExisting(_root));
    }

    [Fact]
    public void UnselectedCompleteInvalidGraphIsNotStartupAuthority() {
        FrameAddress orphan;
        using (GraphResources resources = GraphResources.CreateLegacyNew(_root)) {
            resources.Schemas.Register(Schema);
            orphan = resources.Data.Append(StateRevisionStore.FrameTag, [255], [255]).Unwrap();
            resources.Data.ConfirmDurable();
        }
        using GraphResources reopened = GraphResources.OpenLegacyExisting(_root);
        Assert.Equal(Schema, reopened.Schemas.GetRequired("ResourcesNode", 1));
        Assert.Throws<InvalidDataException>(() => reopened.States.Read(orphan));
        Assert.ThrowsAny<Exception>(() => reopened.ReadHistory(orphan));
    }

    [Fact]
    public void OwnerFaultAndDisposeRejectWarmedFacadesButOwnedResultsRemainReadable() {
        using GraphResources resources = GraphResources.CreateLegacyNew(_root);
        StateRevisionStore retained = resources.States;
        FrameAddress address = retained.Append(StateRevision.CreateObjectHeadMapBase(null,
            [ObjectVersionRecord.CreateBase(7, [10, 20])], []));
        resources.Data.ConfirmDurable();
        StateRevision revision = retained.Read(address);
        IReadOnlyDictionary<uint, FrameAddress> heads = retained.ReadLiveObjectHeadMap(address);
        Assert.Same(revision, retained.Read(address));
        Assert.Same(heads, retained.ReadLiveObjectHeadMap(address));
        resources.MarkFaulted();
        Assert.True(resources.IsFaulted);
        Assert.Throws<InvalidOperationException>(resources.RequireAvailable);
        resources.Dispose();
        resources.Dispose();
        Assert.Throws<ObjectDisposedException>(resources.RequireAvailable);
        Assert.Throws<ObjectDisposedException>(() => retained.Read(address));
        Assert.Throws<ObjectDisposedException>(() => retained.ReadLiveObjectHeadMap(address));
        Assert.Throws<ObjectDisposedException>(() => retained.Append(EmptyRevision()));
        Assert.Equal(address, heads[7]);
        Assert.Equal(new byte[] { 10, 20 }, Assert.Single(revision.LocalObjects).Body.ToArray());
        using GraphResources reopened = GraphResources.OpenLegacyReadOnlyExisting(_root);
        Assert.NotSame(revision, reopened.States.Read(address));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualBackingClosureRejectsWarmCache(bool closeVersionStore) {
        using GraphResources resources = GraphResources.CreateLegacyNew(_root);
        FrameAddress address = resources.States.Append(EmptyRevision());
        resources.States.Read(address);
        if (closeVersionStore) { resources.Versions.Dispose(); }
        else { resources.Data.Dispose(); }
        Assert.Throws<ObjectDisposedException>(resources.RequireAvailable);
        if (!closeVersionStore) { Assert.Throws<ObjectDisposedException>(() => resources.States.Read(address)); }
    }

    [Fact]
    public void NormalWritableTailRecoveryPreservesCompletedFramesAndAddresses() {
        FrameAddress address;
        using (GraphResources resources = GraphResources.CreateLegacyNew(_root)) {
            resources.Schemas.Register(Schema);
            address = resources.States.Append(EmptyRevision());
            resources.Data.ConfirmDurable();
        }
        string active = Assert.Single(Directory.GetFiles(Path.Combine(_root, "data", "active"), "*.rbf"));
        byte[] complete = File.ReadAllBytes(active);
        File.WriteAllBytes(active, [.. complete, 1]); // Offline fixture: partial next HeadLen, no live owner.
        var damaged = Snapshot();
        Assert.ThrowsAny<Exception>(() => GraphResources.OpenLegacyReadOnlyExisting(_root));
        AssertUnchanged(damaged);
        using (GraphResources reopened = GraphResources.OpenLegacyExisting(_root)) {
            Assert.Contains(reopened.Data.RecoveryReports.Values, report => report.Action == Atelia.Rbf.RbfTailRecoveryAction.Truncated);
            Assert.Equal(Schema, reopened.Schemas.GetRequired("ResourcesNode", 1));
            Assert.Empty(reopened.States.Read(address).LocalObjects);
        }
        Assert.Equal(complete, File.ReadAllBytes(active));
    }

    private static StateRevision EmptyRevision() => StateRevision.CreateObjectHeadMapBase(null, [], []);
    private Dictionary<string, byte[]> Snapshot() => Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => path, File.ReadAllBytes);
    private void AssertUnchanged(Dictionary<string, byte[]> before) {
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, bytes) in before) { Assert.Equal(bytes, File.ReadAllBytes(path)); }
    }
    public void Dispose() => SharedReadModel.DeleteFixture(_root, "durable-graph-resources-");
}
