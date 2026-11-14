using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Storage;
using Atelia.DurableGraph.Testing;
using Atelia.FrameStore;
using Atelia.HeadStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class SourceFreeStateTests : IDisposable {
    private const string Prefix = "dg-source-free-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Fact]
    public void NoOpSnapshotIgnoresLogPredecessorAndColdCheckoutContinuesFromItsOwnAddress() {
        BranchId branch;
        FrameAddress original, noOp;
        CommitId continued;
        using (Repository repository = Repository.CreateNew(_path, Models())) {
            DB087Widget root = Widget(10);
            DB087Widget child = Widget(20);
            root.Child = child;
            child.Child = root;
            child.Numbers = root.Numbers;
            BranchInfo a = repository.CreateRootBranchFromState("A", root);
            BranchInfo b = repository.CreateRootBranchFromState("B", Widget(90));
            branch = b.Id;
            StoredGraphRevision first = DB096TestAccess.Snapshot(repository, repository.ReadCurrentCommit(a.Id).Commit.Id);
            original = first.Address;
            StoredGraphRevision snapshot = repository.GraphStore.AppendBusiness(StoredGraphKind.State, first.SchemaRoot,
                new(first.Address, first.RootId, []));
            noOp = snapshot.Address;
            Assert.Null(snapshot.OwnedBatch);
            Assert.Equal(first.DirectoryRoot, snapshot.DirectoryRoot);
            // A raw but valid snapshot publication supplies the history counterexample:
            // B0 is the predecessor, while this snapshot reuses A0's complete directory.
            RepositoryResources resources = DB096TestAccess.Resources(repository);
            resources.Heads.PublishHeads(new Dictionary<HeadId, ReadOnlyMemory<byte>> {
                [b.Id.Value] = BusinessEntryPayloadCodec.Encode(snapshot.Address, []),
            }, out _).Unwrap();
        }
        using (Repository repository = Repository.OpenExisting(_path, Models())) {
            using WorkingCopy work = repository.OpenWorkingCopy(branch);
            DB087Widget root = Assert.IsType<DB087Widget>(work.State);
            Assert.Equal(10, root.Value);
            Assert.Equal(20, root.Child!.Value);
            Assert.Same(root, root.Child.Child);
            Assert.Same(root.Numbers, root.Child.Numbers);
            root.Numbers[7] = 777;
            continued = work.CommitState(parameters: TestSavePolicies.Baseline).Id;
            StoredGraphRevision saved = DB096TestAccess.Snapshot(repository, continued);
            ObjectVersionBatch batch = repository.GraphStore.ReadBatch(saved.OwnedBatch!.Value);
            Assert.Equal(noOp, batch.SourceGraph);
            Assert.Contains(batch.Records, record => record.Kind == ObjectVersionKind.Delta);
            Assert.Equal(7, Assert.IsType<DB087Widget>(BusinessPersistenceAdapter.ReadRoot(repository.GraphStore,
                repository.GraphStore.ReadBusinessGraph(original), Models())).Numbers[7]);
        }
        using (Repository repository = Repository.OpenReadOnlyExisting(_path, Models())) {
            DB087Widget root = Assert.IsType<DB087Widget>(repository.LoadState(continued));
            Assert.Equal(777, root.Numbers[7]);
            Assert.Same(root, root.Child!.Child);
            Assert.Same(root.Numbers, root.Child.Numbers);
        }
    }

    [Fact]
    public void ColdReaderRejectsActualDeltaAndOwnedBatchViolationsWithoutAParentParameter() {
        FrameAddress[] malformed;
        using (Repository repository = Repository.CreateNew(_path, Models())) {
            BranchInfo left = repository.CreateRootBranchFromState("left", Widget(10));
            BranchInfo right = repository.CreateRootBranchFromState("right", Widget(20));
            StoredGraphRevision a = DB096TestAccess.Snapshot(repository, repository.ReadCurrentCommit(left.Id).Commit.Id);
            StoredGraphRevision b = DB096TestAccess.Snapshot(repository, repository.ReadCurrentCommit(right.Id).Commit.Id);
            RepositoryResources resources = DB096TestAccess.Resources(repository);
            uint id = a.RootId.Value;
            FrameAddress Append(uint tag, byte[] payload) => resources.Data.Append(tag, payload).Unwrap();
            FrameAddress Graph(FrameAddress directory, FrameAddress? batch) => Append(GraphFrameCodec.GraphTag,
                GraphFrameCodec.EncodeGraph(StoredGraphKind.State, null, a.RootId, directory, a.SchemaRoot, batch));
            FrameAddress Directory(FrameAddress head) => Append(GraphFrameCodec.DirectoryTag,
                GraphFrameCodec.EncodeDirectory(null, ObjectVersionKind.Base, null,
                    ObjectHeadDirectoryCodec.PrepareBase(new Dictionary<uint, FrameAddress> { [id] = head }).Body));
            ObjectVersionRecord wrong = ObjectVersionRecord.CreateDelta(id, b.ObjectHeads[id], [0]);
            FrameAddress wrongBatch = Append(GraphFrameCodec.BatchTag, GraphFrameCodec.EncodeBatch(a.Address, a.SchemaRoot, [wrong]));
            FrameAddress badPrior = Graph(Directory(wrongBatch), wrongBatch);
            FrameAddress badOwned = Graph(a.DirectoryRoot, wrongBatch);
            FrameAddress badSchemaBatch = Append(GraphFrameCodec.BatchTag, GraphFrameCodec.EncodeBatch(null, null,
                [ObjectVersionRecord.CreateBase(id, [0])]));
            FrameAddress badSchema = Graph(Directory(badSchemaBatch), badSchemaBatch);
            FrameAddress badRoleBatch = Append(GraphFrameCodec.BatchTag, GraphFrameCodec.EncodeBatch(a.SchemaRoot, a.SchemaRoot,
                [ObjectVersionRecord.CreateDelta(id, a.ObjectHeads[id], [0])]));
            FrameAddress badRole = Graph(Directory(badRoleBatch), badRoleBatch);
            var changes = ObjectHeadDirectoryCodec.PrepareDelta(a.ObjectHeads, [], new Dictionary<uint, FrameAddress> { [id] = b.ObjectHeads[id] });
            FrameAddress badDirectory = Append(GraphFrameCodec.DirectoryTag,
                GraphFrameCodec.EncodeDirectory(a.Address, ObjectVersionKind.Delta, b.DirectoryRoot, changes.Body));
            malformed = [badPrior, badOwned, badSchema, badRole, Graph(badDirectory, null)];
            resources.Data.ConfirmDurable();
        }
        using RepositoryStorageFacade reader = RepositoryStorageFacade.OpenReadOnlyExisting(_path);
        foreach (FrameAddress address in malformed) {
            Assert.Throws<InvalidDataException>(() => reader.Store.ReadBusinessGraph(address));
        }
    }

    [Fact]
    public void NoOpInstallationRejectsDifferentValidSnapshotWithMatchingIdsAndEmptyWrites() {
        using Repository repository = Repository.CreateNew(_path, Models());
        BranchInfo left = repository.CreateRootBranchFromState("left", Widget(10));
        BranchInfo right = repository.CreateRootBranchFromState("right", Widget(20));
        StoredGraphRevision a = DB096TestAccess.Snapshot(repository, repository.ReadCurrentCommit(left.Id).Commit.Id);
        StoredGraphRevision b = DB096TestAccess.Snapshot(repository, repository.ReadCurrentCommit(right.Id).Commit.Id);
        StoredGraphRevision bNoOp = repository.GraphStore.AppendBusiness(StoredGraphKind.State, b.SchemaRoot,
            new(b.Address, b.RootId, []));
        using RepositoryStorageFacade facade = new(DB096TestAccess.Resources(repository));
        BusinessPersistenceAdapter adapter = BusinessPersistenceAdapter.LoadState(repository.GraphStore, a, Models());
        BusinessSaveCandidate? capture = null;
        PreparedBusinessWrite? prepared = null;
        using PendingRepositorySave pending = facade.PrepareFrom(left.Id.Value, repository.ReadCurrentCommit(left.Id).Commit.Id.Value, null,
            StoredGraphKind.State, default, () => {
                capture = adapter.CaptureOwned(adapter.State!, StoredGraphKind.State);
                PreparedSchemaCatalogWrite catalog = facade.CatalogForPreparation.PrepareOwned(capture.Graph.Objects.Select(row => row.Layout), []);
                prepared = adapter.PrepareOwned(capture, catalog.View, TestSavePolicies.Baseline);
                Assert.Empty(prepared.Write.Records);
                return (prepared.Write, catalog.Write);
            });
        try {
            Assert.Throws<InvalidDataException>(() => prepared!.PrepareInstall(bNoOp));
            Assert.Equal(a.Address, adapter.Source!.Address);
        } finally { capture!.AbortPreparation(); }
    }

    private static DB087Widget Widget(int value) => new() { Value = value, Numbers = Enumerable.Range(0, 64).ToList() };
    private static StateModelRegistry Models() {
        StateModelRegistry models = new();
        models.Register(global::Atelia.DurableGraph.Generated.Family_5030576964676574.Definition);
        return models;
    }

    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
