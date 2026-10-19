using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;
using System.Buffers.Binary;

using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Atelia.HeadStore;
using Frames = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class DefaultMetaBootstrapRepositoryTests : IDisposable {
    private const string Prefix = "dg-default-meta-bootstrap-";
    private const long SmallRotationThreshold = 128;
    private readonly List<string> _paths = [];

    [Fact]
    public void PublicDefaultPublishesJointMetaAndRotatesBeforeColdDeltaContinuation() {
        string path = NewPath();
        FrameAddress initialState, eventAddress, savedState, initialSchema, acceptedSchema, continuedState;
        ObjectId listId;
        ObjectStorageInfo savedList;
        FrameImage[] completed;
        using (Repository repository = Repository.CreateNew(path, Models(), SmallRotationThreshold, readCacheBudgetBytes: 0)) {
            DB087Widget root = Ring();
            BranchInfo branch = repository.CreateRootBranchFromState("main", root);
            using WorkingCopy main = repository.OpenWorkingCopy(branch.Id);
            root = Assert.IsType<DB087Widget>(main.State);
            initialState = Describe(repository, main.BaseCommitId).Address;
            initialSchema = repository.SharedSchemaRoot!.Value;
            StoredGraphRevision first = repository.GraphStore.ReadBusinessGraph(initialState, null);
            Assert.Equal(StoredGraphKind.State, first.Kind);
            Assert.Equal(initialSchema, first.SchemaRoot);
            Assert.Equal(3, first.ObjectHeads.Count);
            Assert.Single(SchemaCatalogView.Load(repository.GraphStore, initialSchema).Schemas);
            AssertPublished(repository, initialState, initialSchema);
            repository.CreateTag("initial", main.BaseCommitId);
            NormalizedGraphState baseline = main.Baseline!;
            listId = Assert.Single(baseline.Objects, row => row.Value.Current.Kind == ObjectStateKind.List).Key;

            // Capture discovers the second Schema through a real Event root; no catalog edit seam is used.
            root.Numbers[7] = 200;
            DB087EnvelopeRoot domainEvent = new() { Link = root, Numbers = root.Numbers };
            CommitInfo publishedEvent = main.CommitEvent(domainEvent);
            eventAddress = Describe(repository, publishedEvent.Id).Address;
            acceptedSchema = repository.SharedSchemaRoot!.Value;
            Assert.NotEqual(initialSchema, acceptedSchema);
            Assert.Equal(2, SchemaCatalogView.Load(repository.GraphStore, acceptedSchema).Schemas.Count);
            Assert.Equal(acceptedSchema, main.AcceptedCatalog!.Snapshot!.Address);
            Assert.Same(baseline, main.Baseline);
            Assert.Equal(initialState, main.StateRevisionAddress);
            Assert.Same(root, main.State);
            Assert.Equal(initialState, ResourcesOf(repository).FindNearestState(main.BaseCommitId.Value, inclusive: true)?.Address);
            StoredGraphRevision actualEvent = Snapshot(repository, publishedEvent.Id);
            Assert.Equal(StoredGraphKind.Event, actualEvent.Kind);
            Assert.Equal(initialState, ResourcesOf(repository).ReadPrevious(publishedEvent.Id.Value)!.Address);
            Assert.Equal(initialState, actualEvent.SourceGraph);
            Assert.Equal(acceptedSchema, actualEvent.SchemaRoot);
            Assert.Equal(4, actualEvent.ObjectHeads.Count);
            AssertPublished(repository, eventAddress, acceptedSchema);
            repository.CreateTag("event", publishedEvent.Id);

            HeadEntry sharedBefore = ResourcesOf(repository).Heads.ReadHead(2).Unwrap();
            root.Numbers[7] = 201;
            savedState = Describe(repository, main.CommitState().Id).Address;
            StoredGraphRevision saved = repository.GraphStore.ReadBusinessGraph(savedState, initialState);
            Assert.Equal(eventAddress, ResourcesOf(repository).ReadPrevious(main.BaseCommitId.Value)!.Address);
            Assert.Equal(initialState, saved.SourceGraph);
            Assert.Equal(acceptedSchema, saved.SchemaRoot);
            Assert.Equal(3, saved.ObjectHeads.Count);
            ObjectVersionRecord delta = Assert.Single(repository.GraphStore.ReadBatch(saved.OwnedBatch!.Value).Records);
            Assert.Equal(listId.Value, delta.ObjectId);
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
            Assert.Equal(baseline.Objects[listId].Storage!.Value.Head, delta.PriorAddress);
            savedList = main.Baseline!.Objects[listId].Storage!.Value;
            HeadEntry sharedAfter = ResourcesOf(repository).Heads.ReadHead(2).Unwrap();
            Assert.Equal(sharedBefore.Id, sharedAfter.Id);
            Assert.Equal(sharedBefore.Payload.ToArray(), sharedAfter.Payload.ToArray());
            AssertPublished(repository, savedState, acceptedSchema);
            completed = ReadFrameImages(ResourcesOf(repository).Data);
            Assert.True(completed.Select(frame => FileId(frame.Address)).Distinct().Count() > 1);
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(path, "data", "archive"), "*.rbf", SearchOption.AllDirectories));
            Assert.Equal(0, repository.GraphStore.ReadCacheStatistics.ResidentChargeBytes);
        }

        Dictionary<string, byte[]> archived = SnapshotArchives(path);
        Dictionary<string, byte[]> readOnlyImages = SnapshotFiles(path);
        // All previous resources/caches are closed; meta can load before business model registration.
        using (Repository metaOnly = Repository.OpenReadOnlyExisting(path, new(), readCacheBudgetBytes: 0)) {
            Assert.Equal(acceptedSchema, metaOnly.SharedSchemaRoot);
            Assert.Equal(2, SchemaCatalogView.Load(metaOnly.GraphStore, acceptedSchema).Schemas.Count);
        }
        using (Repository reader = Repository.OpenReadOnlyExisting(path, Models(), readCacheBudgetBytes: 0)) {
            AssertPublished(reader, savedState, acceptedSchema);
            AssertRing(reader.LoadState(reader.ResolveTag("initial")), 7);
            DB087EnvelopeRoot loadedEvent = Assert.IsType<DB087EnvelopeRoot>(reader.LoadEvent(reader.ResolveTag("event")));
            DB087Widget eventRoot = AssertRing(loadedEvent.Link, 200);
            Assert.Same(eventRoot.Numbers, loadedEvent.Numbers);
            AssertRing(reader.LoadState(reader.ReadCurrentCommit(BranchNamed(reader, "main")).Commit.Id), 201);
            AssertFrameImages(reader, completed);
        }
        AssertFilesUnchanged(readOnlyImages, SnapshotFiles(path));

        using (Repository reopened = Repository.OpenExisting(path, Models(), SmallRotationThreshold, readCacheBudgetBytes: 0)) {
            using WorkingCopy cold = reopened.OpenWorkingCopy(BranchNamed(reopened, "main"));
            DB087Widget root = AssertRing(cold.State, 201);
            Assert.Equal(savedState, cold.StateRevisionAddress);
            Assert.Equal(savedList, cold.Baseline!.Objects[listId].Storage);
            HeadEntry sharedBefore = ResourcesOf(reopened).Heads.ReadHead(2).Unwrap();
            root.Numbers[8] = 202;
            continuedState = Describe(reopened, cold.CommitState().Id).Address;
            StoredGraphRevision continued = reopened.GraphStore.ReadBusinessGraph(continuedState, savedState);
            Assert.Equal(savedState, ResourcesOf(reopened).ReadPrevious(cold.BaseCommitId.Value)!.Address);
            Assert.Equal(savedState, continued.SourceGraph);
            Assert.Equal(acceptedSchema, continued.SchemaRoot);
            ObjectVersionRecord delta = Assert.Single(reopened.GraphStore.ReadBatch(continued.OwnedBatch!.Value).Records);
            Assert.Equal(listId.Value, delta.ObjectId);
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
            Assert.Equal(savedList.Head, delta.PriorAddress);
            Assert.Equal(savedList.ReconstructionPayloadBytes + delta.EncodedPayloadBytes!.Value,
                cold.Baseline!.Objects[listId].Storage!.Value.ReconstructionPayloadBytes);
            // This commit itself crosses files, proving OpenExisting also forwards the rotation value.
            Assert.True(new[] { continued.Address, continued.DirectoryRoot, continued.OwnedBatch.Value }
                .Select(FileId).Distinct().Count() > 1);
            HeadEntry sharedAfter = ResourcesOf(reopened).Heads.ReadHead(2).Unwrap();
            Assert.Equal(sharedBefore.Id, sharedAfter.Id);
            Assert.Equal(sharedBefore.Payload.ToArray(), sharedAfter.Payload.ToArray());
            AssertPublished(reopened, continuedState, acceptedSchema);
            AssertFrameImages(reopened, completed);
        }
        foreach ((string file, byte[] bytes) in archived) { Assert.Equal(bytes, File.ReadAllBytes(file)); }
        readOnlyImages = SnapshotFiles(path);
        using (Repository fresh = Repository.OpenReadOnlyExisting(path, Models())) {
            AssertPublished(fresh, continuedState, acceptedSchema);
            AssertRing(fresh.LoadState(fresh.ReadCurrentCommit(BranchNamed(fresh, "main")).Commit.Id), 201, 202);
            AssertRing(fresh.LoadState(fresh.ResolveTag("initial")), 7);
            AssertFrameImages(fresh, completed);
        }
        AssertFilesUnchanged(readOnlyImages, SnapshotFiles(path));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1_048_576L)]
    public void PublicCacheBudgetControlsActualColdReadProductionAndOwnedLifetime(long budget) {
        string path = NewPath();
        FrameAddress saved;
        using (Repository seed = Repository.CreateNew(path, Models(), readCacheBudgetBytes: budget)) {
            BranchInfo branch = seed.CreateRootBranchFromState("main", Ring());
            saved = Describe(seed, seed.ReadCurrentCommit(branch.Id).Commit.Id).Address;
        }
        Dictionary<string, byte[]> images = SnapshotFiles(path);
        GraphRecordStore retained;
        ObjectVersionBatch owned;
        using (Repository reader = Repository.OpenReadOnlyExisting(path, Models(), readCacheBudgetBytes: budget)) {
            retained = reader.GraphStore;
            DB087Widget first = AssertRing(reader.LoadState(reader.ReadCurrentCommit(BranchNamed(reader, "main")).Commit.Id), 7);
            GraphPhysicalReadStatistics before = retained.ReadCacheStatistics;
            DB087Widget second = AssertRing(reader.LoadState(reader.ReadCurrentCommit(BranchNamed(reader, "main")).Commit.Id), 7);
            GraphPhysicalReadStatistics after = retained.ReadCacheStatistics;
            Assert.NotSame(first, second);
            Assert.NotSame(first.Numbers, second.Numbers);
            if (budget == 0) {
                Assert.True(after.BatchDecodes > before.BatchDecodes);
                Assert.True(after.DirectoryMaterializations > before.DirectoryMaterializations);
                Assert.True(after.DirectoryReplayFrames > before.DirectoryReplayFrames);
                Assert.Equal(0, after.ResidentChargeBytes);
                Assert.Equal(0, after.EntryCount);
            } else {
                Assert.Equal(before.GraphDecodes, after.GraphDecodes);
                Assert.Equal(before.BatchDecodes, after.BatchDecodes);
                Assert.Equal(before.DirectoryVersionDecodes, after.DirectoryVersionDecodes);
                Assert.Equal(before.DirectoryMaterializations, after.DirectoryMaterializations);
                Assert.Equal(before.DirectoryReplayFrames, after.DirectoryReplayFrames);
                Assert.True(after.BatchHits > before.BatchHits);
                Assert.InRange(after.ResidentChargeBytes, 1, budget);
                Assert.InRange(after.PeakResidentChargeBytes, 1, budget);
            }
            StoredGraphRevision graph = retained.ReadBusinessGraph(saved, null);
            owned = retained.ReadBatch(graph.OwnedBatch!.Value);
        }
        AssertFilesUnchanged(images, SnapshotFiles(path));
        Assert.Throws<ObjectDisposedException>(() => retained.ReadBusinessGraph(saved, null));
        Assert.Equal(0, retained.ReadCacheStatistics.ResidentChargeBytes);
        Assert.Equal(0, retained.ReadCacheStatistics.EntryCount);
        Assert.Equal(3, owned.Records.Count); // Already delivered owned values outlive the closed cache owner.
        Assert.All(owned.Records, record => Assert.NotEmpty(record.Body.ToArray()));
    }

    [Fact]
    public void PublicFactoryNegativeBudgetsRejectBeforeCreatingOrOpeningBackingResources() {
        string path = NewPath();
        Assert.Throws<ArgumentOutOfRangeException>(() => Repository.CreateNew(path, Models(), readCacheBudgetBytes: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => RepositoryResources.CreateNew(path, readCacheBudgetBytes: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Repository.OpenExisting(path, Models(), readCacheBudgetBytes: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => RepositoryResources.OpenExisting(path, readCacheBudgetBytes: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Repository.OpenReadOnlyExisting(path, Models(), readCacheBudgetBytes: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => RepositoryResources.OpenReadOnlyExisting(path, readCacheBudgetBytes: -1));
        Assert.False(Directory.Exists(path));
    }

    private static StateModelRegistry Models() {
        StateModelRegistry models = new();
        models.Register(global::Atelia.DurableGraph.Generated.Family_5030576964676574.Definition);
        models.Register(global::Atelia.DurableGraph.Generated.Family_5030456E76656C6F7065.Definition);
        return models;
    }
    private static DB087Widget Ring() {
        List<int> numbers = Enumerable.Range(0, 128).ToList();
        DB087Widget root = new() { Value = 1, Numbers = numbers };
        root.Child = new() { Value = 2, Numbers = numbers, Child = root };
        return root;
    }
    private static DB087Widget AssertRing(IDurableObject? value, int atSeven, int atEight = 8) {
        DB087Widget root = Assert.IsType<DB087Widget>(value), child = Assert.IsType<DB087Widget>(root.Child);
        Assert.Equal(1, root.Value);
        Assert.Equal(2, child.Value);
        Assert.Same(root, child.Child);
        Assert.Same(root.Numbers, child.Numbers);
        Assert.Equal(128, root.Numbers.Count);
        for (int i = 0; i < 128; i++) { Assert.Equal(i == 7 ? atSeven : i == 8 ? atEight : i, root.Numbers[i]); }
        return root;
    }
    private static void AssertPublished(Repository repository, FrameAddress head, FrameAddress schema) {
        RepositoryResources resources = ResourcesOf(repository);
        HeadEntry directory = resources.Heads.ReadHead(1).Unwrap(), shared = resources.Heads.ReadHead(2).Unwrap();
        Assert.True(directory.Payload.StartsWith("P0R3"u8));
        Assert.Equal(17, directory.Payload.Length);
        Assert.NotNull(ManagementDirectoryPointer.Decode(ManagementDirectoryKind.Branches, directory.Payload));
        var member = Assert.Single(resources.ListBranches());
        Assert.Equal("main", member.Value);
        HeadEntry business = resources.Heads.ReadHead(member.Key).Unwrap();
        Assert.Equal(head, BusinessEntryPayloadCodec.Decode(business.Payload).Graph);
        Assert.True(shared.Payload.StartsWith("P0S1"u8));
        Assert.Equal(schema, Assert.Single(NamedFrameMapCodec.Decode(shared.Payload[4..]).Unwrap()).Value);
        Assert.Equal("schema", Assert.Single(NamedFrameMapCodec.Decode(shared.Payload[4..]).Unwrap()).Key);
        Assert.Equal(head, resources.ReadCurrent(member.Key).Address);
        Assert.Equal(schema, repository.SharedSchemaRoot);
    }
    private sealed record FrameImage(FrameAddress Address, uint Tag, int TailMetaLength, bool IsTombstone, byte[] PayloadAndMeta);
    private static FrameImage[] ReadFrameImages(Frames data) {
        List<FrameAddress> addresses = [];
        data.Inventory(info => addresses.Add(info.Address)).Unwrap();
        return addresses.Select(address => {
            using FrameRead frame = data.ReadFrame(address).Unwrap();
            return new FrameImage(address, frame.Tag, frame.TailMetaLength, frame.IsTombstone, frame.PayloadAndMeta.ToArray());
        }).ToArray();
    }
    private static void AssertFrameImages(Repository repository, IEnumerable<FrameImage> expected) {
        foreach (FrameImage image in expected) {
            using FrameRead frame = ResourcesOf(repository).Data.ReadFrame(image.Address).Unwrap();
            Assert.Equal(image.Tag, frame.Tag);
            Assert.Equal(image.TailMetaLength, frame.TailMetaLength);
            Assert.Equal(image.IsTombstone, frame.IsTombstone);
            Assert.Equal(image.PayloadAndMeta, frame.PayloadAndMeta.ToArray());
        }
    }
    private static uint FileId(FrameAddress address) {
        Span<byte> bytes = stackalloc byte[FrameAddress.EncodedSize];
        Assert.True(address.TryWrite(bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }
    private static Dictionary<string, byte[]> SnapshotFiles(string path) => Directory.GetFiles(path, "*", SearchOption.AllDirectories)
        .ToDictionary(file => file, File.ReadAllBytes);
    private static Dictionary<string, byte[]> SnapshotArchives(string path) => Directory.GetFiles(Path.Combine(path, "data", "archive"), "*.rbf", SearchOption.AllDirectories)
        .ToDictionary(file => file, File.ReadAllBytes);
    private static void AssertFilesUnchanged(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual) {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach ((string file, byte[] bytes) in expected) { Assert.Equal(bytes, actual[file]); }
    }
    private string NewPath() {
        string path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
        _paths.Add(path);
        return path;
    }
    public void Dispose() {
        foreach (string path in _paths) { SharedReadModel.DeleteFixture(path, Prefix); }
    }
}
