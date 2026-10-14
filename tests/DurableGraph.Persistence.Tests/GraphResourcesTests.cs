using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class GraphResourcesTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"durable-graph-resources-{Guid.NewGuid():N}");
    private static readonly DurableSchema Schema = new("ResourcesNode", 1, new DurableFieldInfo(1, TypeTag.Int32));

    [Fact]
    public void SharedOwnerPersistsSchemaAndDataAndReadOnlyCannotWrite() {
        FrameAddress address;
        using (GraphResources resources = GraphResources.CreateNew(_root)) { address = SeedTyped(resources).Address; }
        Assert.False(File.Exists(Path.Combine(_root, "schemas.rbf")));
        Assert.False(File.Exists(Path.Combine(_root, "repository.lock")));
        var before = Snapshot();
        using (GraphResources reader = GraphResources.OpenReadOnlyExisting(_root)) {
            using RepositoryStorageFacade facade = new(reader);
            Assert.True(reader.IsReadOnly);
            reader.RequireAvailable();
            AssertSchema(reader);
            Assert.Equal(address, facade.GetHead("main"));
            Assert.Equal((byte)7, Assert.IsType<SharedReadModel.Node>(BusinessPersistenceAdapter.ReadRoot(facade.Store, address, SharedReadModel.Models())).Value);
            Assert.Throws<InvalidOperationException>(reader.RequireWritable);
            Assert.Throws<InvalidOperationException>(facade.EnsureEditingAllowed);
            BranchSaveSession readonlySession = new(facade, "main", SharedReadModel.Models());
            Assert.Throws<InvalidOperationException>(() => readonlySession.Catalog.Set(Schema));
            Assert.Throws<InvalidOperationException>(() => facade.Prepare("other", StoredGraphKind.State,
                new(null, new(1), [ObjectVersionRecord.CreateBase(1, [10])])));
            using GraphResources second = GraphResources.OpenReadOnlyExisting(_root);
            Assert.Equal(address, second.GetBranchHead("main"));
            AssertSchema(second);
        }
        AssertUnchanged(before);
        using GraphResources reopened = GraphResources.OpenExisting(_root);
        Assert.Equal(address, reopened.GetBranchHead("main"));
        AssertSchema(reopened);
    }

    [Fact]
    public void MissingAndOldLayoutReadOnlyOpenNeverCreatesResources() {
        Assert.Throws<DirectoryNotFoundException>(() => GraphResources.OpenReadOnlyExisting(_root));
        Assert.False(Directory.Exists(_root));
        Directory.CreateDirectory(_root);
        // The required data owner rejects a missing data directory before acquiring resources.
        Assert.Throws<DirectoryNotFoundException>(() => GraphResources.OpenReadOnlyExisting(_root));
        Assert.Empty(Directory.GetFileSystemEntries(_root));
        File.WriteAllBytes(Path.Combine(_root, "schemas.rbf"), []);
        var before = Snapshot();
        Assert.Throws<DirectoryNotFoundException>(() => GraphResources.OpenExisting(_root));
        AssertUnchanged(before);
        Assert.Throws<DirectoryNotFoundException>(() => GraphResources.OpenReadOnlyExisting(_root));
        AssertUnchanged(before);
    }

    [Fact]
    public void BackingLocksCoverWritersAndAllowOnlySharedReaders() {
        using (GraphResources writer = GraphResources.CreateNew(_root)) {
            Assert.ThrowsAny<Exception>(() => GraphResources.OpenExisting(_root));
            Assert.ThrowsAny<Exception>(() => GraphResources.OpenReadOnlyExisting(_root));
        }
        using GraphResources reader = GraphResources.OpenReadOnlyExisting(_root);
        using GraphResources second = GraphResources.OpenReadOnlyExisting(_root);
        Assert.ThrowsAny<Exception>(() => GraphResources.OpenExisting(_root));
    }

    [Fact]
    public void UnselectedCompleteInvalidGraphIsNotStartupAuthority() {
        FrameAddress orphan;
        using (GraphResources resources = GraphResources.CreateNew(_root)) {
            SeedTyped(resources);
            orphan = resources.Data.Append(GraphFrameCodec.GraphTag, [255], [255]).Unwrap();
            resources.Data.ConfirmDurable();
        }
        using GraphResources reopened = GraphResources.OpenExisting(_root);
        AssertSchema(reopened);
        Assert.Throws<InvalidDataException>(() => reopened.GraphStore.ReadMetadata(orphan));
        Assert.Throws<InvalidDataException>(() => reopened.ReadHistory(orphan));
    }

    [Fact]
    public void OwnerFaultAndDisposeRejectWarmedFacadesButOwnedResultsRemainReadable() {
        using GraphResources resources = GraphResources.CreateNew(_root);
        GraphRecordStore retained = resources.GraphStore;
        StoredGraphRevision graph = SeedRaw(resources);
        FrameAddress address = graph.Address;
        ObjectVersionBatch batch = retained.ReadBatch(graph.OwnedBatch!.Value);
        IReadOnlyDictionary<uint, FrameAddress> heads = retained.ReadDirectory(graph.DirectoryRoot);
        Assert.Same(batch, retained.ReadBatch(graph.OwnedBatch.Value));
        Assert.Same(heads, retained.ReadDirectory(graph.DirectoryRoot));
        resources.MarkFaulted();
        Assert.True(resources.IsFaulted);
        Assert.Throws<InvalidOperationException>(resources.RequireAvailable);
        Assert.ThrowsAny<InvalidOperationException>(() => retained.ReadMetadata(address));
        Assert.ThrowsAny<InvalidOperationException>(() => retained.ReadBatch(graph.OwnedBatch.Value));
        resources.Dispose();
        resources.Dispose();
        Assert.Throws<ObjectDisposedException>(resources.RequireAvailable);
        Assert.Throws<ObjectDisposedException>(() => retained.ReadMetadata(address));
        Assert.Throws<ObjectDisposedException>(() => retained.ReadDirectory(graph.DirectoryRoot));
        Assert.Throws<ObjectDisposedException>(() => retained.Append(StoredGraphKind.State, address, null,
            new(address, new(7), [ObjectVersionRecord.CreateBase(7, [30])])));
        Assert.Equal(graph.OwnedBatch, heads[7]);
        Assert.Equal(new byte[] { 10, 20 }, Assert.Single(batch.Records).Body.ToArray());
        using GraphResources reopened = GraphResources.OpenReadOnlyExisting(_root);
        Assert.NotSame(batch, reopened.GraphStore.ReadBatch(graph.OwnedBatch.Value));
        Assert.Equal(address, reopened.GetBranchHead("main"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualBackingClosureRejectsWarmCache(bool closeVersionStore) {
        using GraphResources resources = GraphResources.CreateNew(_root);
        GraphRecordStore retained = resources.GraphStore;
        StoredGraphRevision graph = SeedRaw(resources);
        ObjectVersionBatch owned = retained.ReadBatch(graph.OwnedBatch!.Value);
        retained.ReadDirectory(graph.DirectoryRoot);
        Assert.True(retained.ReadCacheStatistics.ResidentChargeBytes > 0);
        if (closeVersionStore) { resources.Versions.Dispose(); }
        else { resources.Data.Dispose(); }
        Assert.Throws<ObjectDisposedException>(resources.RequireAvailable);
        Assert.Throws<ObjectDisposedException>(() => retained.ReadMetadata(graph.Address));
        Assert.Throws<ObjectDisposedException>(() => retained.ReadBatch(graph.OwnedBatch.Value));
        Assert.Throws<ObjectDisposedException>(() => retained.ReadDirectory(graph.DirectoryRoot));
        Assert.Equal(0, retained.ReadCacheStatistics.EntryCount);
        Assert.Equal(0, retained.ReadCacheStatistics.ResidentChargeBytes);
        Assert.Equal(new byte[] { 10, 20 }, Assert.Single(owned.Records).Body.ToArray());
    }

    [Fact]
    public void NormalWritableTailRecoveryPreservesCompletedFramesAndAddresses() {
        FrameAddress address;
        using (GraphResources resources = GraphResources.CreateNew(_root)) { address = SeedTyped(resources).Address; }
        string active = Assert.Single(Directory.GetFiles(Path.Combine(_root, "data", "active"), "*.rbf"));
        byte[] complete = File.ReadAllBytes(active);
        File.WriteAllBytes(active, [.. complete, 1]); // Offline fixture: partial next HeadLen, no live owner.
        var damaged = Snapshot();
        Assert.ThrowsAny<Exception>(() => GraphResources.OpenReadOnlyExisting(_root));
        AssertUnchanged(damaged);
        using (GraphResources reopened = GraphResources.OpenExisting(_root)) {
            Assert.Contains(reopened.Data.RecoveryReports.Values, report => report.Action == Atelia.Rbf.RbfTailRecoveryAction.Truncated);
            AssertSchema(reopened);
            Assert.Equal(address, reopened.GetBranchHead("main"));
            Assert.Equal((byte)7, Assert.IsType<SharedReadModel.Node>(BusinessPersistenceAdapter.ReadRoot(reopened.GraphStore, address, SharedReadModel.Models())).Value);
        }
        Assert.Equal(complete, File.ReadAllBytes(active));
    }

    private static StoredGraphRevision SeedTyped(GraphResources resources) {
        using RepositoryStorageFacade facade = new(resources);
        BranchSaveSession session = new(facade, "main", SharedReadModel.Models());
        session.Catalog.Set(Schema);
        using PreparedBranchSave save = session.Prepare(new SharedReadModel.Node { Value = 7 });
        return save.Commit().Business;
    }
    private static StoredGraphRevision SeedRaw(GraphResources resources) {
        using RepositoryStorageFacade facade = new(resources);
        using PendingRepositorySave pending = facade.Prepare("main", StoredGraphKind.State,
            new(null, new(7), [ObjectVersionRecord.CreateBase(7, [10, 20])]));
        return facade.Commit(pending).Business;
    }
    private static void AssertSchema(GraphResources resources) {
        SchemaCatalogView catalog = SchemaCatalogView.Load(resources.GraphStore, resources.SharedSchemaRoot!.Value);
        Assert.Equal(Schema, catalog.Schemas[new(Schema.Type, Schema.Version)]);
    }
    private Dictionary<string, byte[]> Snapshot() => Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => path, File.ReadAllBytes);
    private void AssertUnchanged(Dictionary<string, byte[]> before) {
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, bytes) in before) { Assert.Equal(bytes, File.ReadAllBytes(path)); }
    }
    public void Dispose() => SharedReadModel.DeleteFixture(_root, "durable-graph-resources-");
}
