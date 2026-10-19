using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;
using System.Buffers.Binary;

using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Atelia.Rbf;
using Atelia.HeadStore;
using DataStore = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

// Tests normal backing-store incomplete-tail recovery at the pinned storage stack.
// Offline injection only appends a partial next HeadLen after every owner closes;
// it never removes or changes an existing complete frame and is not data rescue.
public sealed class SplitRepositoryBackingRecoveryTests : IDisposable {
    private const string Prefix = "dg-split-backing-recovery-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly DurableSchema Extra = new("SplitRecoveryExtra", 1, new DurableFieldInfo(1, TypeTag.Int32));

    [Fact]
    public void IncompleteDataTailIsReadonlyRejectedAndNormallyRecoveredWithoutChangingCompleteHistory() {
        SeedResult seed = Seed();
        Dictionary<string, byte[]> completeFiles = SnapshotFiles();
        // The finite fixture stays in a single ordinary active file. Path spelling is
        // FrameStorePaths.GetActiveRelativePath in the exact storage source pin.
        string active = Assert.Single(Directory.GetFiles(Path.Combine(_path, "data", "active"), "*.rbf"));
        uint fileId = FileIdOf(seed.Head);
        Assert.Equal(fileId.ToString("x8") + ".rbf", Path.GetFileName(active));
        long completeLength = new FileInfo(active).Length;
        AppendIncompleteNextHead(active);
        Dictionary<string, byte[]> damagedFiles = SnapshotFiles();
        Assert.Equal(completeLength + 1, new FileInfo(active).Length);
        Assert.Throws<InvalidDataException>(() => Repository.OpenStorageReadOnlyExisting(_path, Models()));
        AssertFilesEqual(damagedFiles, SnapshotFiles()); // Failed read-only admission repairs nothing and releases its resources.

        using (Repository recovered = Repository.OpenStorageExisting(_path, Models())) {
            RepositoryResources resources = ResourcesOf(recovered);
            var report = Assert.Single(resources.Data.RecoveryReports);
            Assert.Equal(fileId, report.Key);
            AssertTruncated(report.Value, completeLength);
            Assert.Empty(resources.Heads.RecoveryReports);
            Assert.Equal(seed.Head, Describe(recovered, recovered.ReadCurrentCommit(seed.Branch).Commit.Id).Address);
            Assert.Equal(seed.SchemaRoot, recovered.SharedSchemaRoot);
            AssertHistory(recovered, seed);
            AssertCompletedFrames(resources.Data, seed.Frames);
            Assert.Equal(Extra, SchemaCatalogView.Load(recovered.GraphStore, seed.SchemaRoot).Schemas[new(Extra.Type, Extra.Version)]);
            Assert.Equal(0, recovered.GraphStore.AppendedFrames);
        }
        AssertFilesEqual(completeFiles, SnapshotFiles()); // Only the incomplete suffix was discarded; all completed bytes are exact.

        FrameAddress continued;
        using (Repository writer = Repository.OpenStorageExisting(_path, Models())) {
            AssertHealthyActive(ResourcesOf(writer).Data, fileId, completeLength);
            using WorkingCopy checkout = writer.OpenWorkingCopy(seed.Branch);
            DB087Widget root = AssertRing(checkout.State, 201);
            Assert.Equal(seed.Head, checkout.StateRevisionAddress);
            Assert.Equal(seed.RootId, checkout.StateId);
            Assert.Equal(seed.Storage.Keys.OrderBy(id => id.Value), checkout.Baseline!.Objects.Keys.OrderBy(id => id.Value));
            foreach ((ObjectId id, ObjectStorageInfo storage) in seed.Storage) { Assert.Equal(storage, checkout.Baseline.Objects[id].Storage); }
            root.Numbers[8] = 202;
            CommitInfo saved = checkout.CommitState();
            continued = Describe(writer, saved.Id).Address;
            StoredGraphRevision graph = Snapshot(writer, saved.Id);
            ObjectVersionRecord delta = Assert.Single(writer.GraphStore.ReadBatch(graph.OwnedBatch!.Value).Records);
            ObjectStorageInfo prior = seed.Storage[seed.ListId];
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
            Assert.Equal(seed.ListId.Value, delta.ObjectId);
            Assert.Equal(prior.Head, delta.PriorAddress);
            Assert.Equal(seed.Head, graph.SourceGraph);
            Assert.Equal(seed.History.Last(), Describe(writer, new CommitId(ResourcesOf(writer).ReadPrevious(saved.Id.Value)!.Id)).Address);
            Assert.Equal(prior.ReconstructionPayloadBytes + ObjectVersionPayloadSize.GetDeltaPayloadBytes(delta.Body.Length, prior.Head),
                checkout.Baseline!.Objects[seed.ListId].Storage!.Value.ReconstructionPayloadBytes);
            AssertCompletedFrames(ResourcesOf(writer).Data, seed.Frames, allowAppended: true);
        }
        using Repository fresh = Repository.OpenStorageReadOnlyExisting(_path, Models());
        Assert.Equal(continued, Describe(fresh, fresh.ReadCurrentCommit(seed.Branch).Commit.Id).Address);
        AssertRing(fresh.LoadState(fresh.ReadCurrentCommit(seed.Branch).Commit.Id), 201, 202);
        CommitInfo[] history = fresh.ReadFrames(seed.Branch).ToArray();
        Assert.Equal(seed.History, history.Take(seed.History.Length).Select(frame => Describe(fresh, frame.Id).Address));
        AssertRing(fresh.LoadState(history[0].Id), 7);
        AssertRing(fresh.LoadEvent(history[1].Id), 200);
        AssertRing(fresh.LoadState(history[2].Id), 201);
        AssertCompletedFrames(ResourcesOf(fresh).Data, seed.Frames, allowAppended: true);
        Assert.Empty(ResourcesOf(fresh).Data.RecoveryReports);
    }

    [Fact]
    public void UnselectedOrdinaryRefTailStaysUntouchedUntilItsActualLazyFirstRead() {
        SeedResult seed = Seed();
        byte[] ordinaryPayload = [30, 40, 50];
        HeadEntryId ordinaryRevision;
        using (Repository owner = Repository.OpenStorageExisting(_path, Models())) {
            var versions = ResourcesOf(owner).Heads;
            // DG reserves 1/2/3. The explicit-ID CreateHead overload cannot create
            // ordinary head5; automatic H+1 is the actual public HeadStore route.
            HeadEntryId created = versions.CreateHead(new byte[] { 10, 20 }, out PublicationOutcome createdOutcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, createdOutcome);
            Assert.Equal(5u, versions.GetLocalHeadId(created.HeadId).Unwrap());
            ordinaryRevision = versions.PublishHead(created.HeadId, ordinaryPayload, out PublicationOutcome published).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, published);
        }
        string refPath = Path.Combine(_path, "versions", "refs", "00000005.rbf");
        uint dataFileId = FileIdOf(seed.Head);
        long dataLength = new FileInfo(Path.Combine(_path, "data", "active", dataFileId.ToString("x8") + ".rbf")).Length;
        byte[] completeRef = File.ReadAllBytes(refPath);
        AppendIncompleteNextHead(refPath);
        byte[] incompleteRef = File.ReadAllBytes(refPath);
        Dictionary<string, byte[]> damagedFiles = SnapshotFiles();
        Assert.Equal(completeRef.Length + 1, incompleteRef.Length);

        using (Repository readonlyOwner = Repository.OpenStorageReadOnlyExisting(_path, Models())) {
            var versions = ResourcesOf(readonlyOwner).Heads;
            Assert.Empty(versions.RecoveryReports);
            Assert.Equal(incompleteRef, File.ReadAllBytes(refPath));
            Assert.Equal(seed.Head, Describe(readonlyOwner, readonlyOwner.ReadCurrentCommit(seed.Branch).Commit.Id).Address);
            AssertHistory(readonlyOwner, seed);
            Assert.Equal(incompleteRef, File.ReadAllBytes(refPath));
            Assert.Empty(versions.RecoveryReports);
        }
        AssertFilesEqual(damagedFiles, SnapshotFiles()); // DG's required-root readonly open did not audit/ref-repair the unrelated control file.

        using (Repository writer = Repository.OpenStorageExisting(_path, Models())) {
            RepositoryResources resources = ResourcesOf(writer);
            IReadOnlyList<HeadStoreRecoveryReport> reports = resources.Heads.RecoveryReports;
            Assert.Empty(reports);
            Assert.Equal(incompleteRef, File.ReadAllBytes(refPath));
            AssertHistory(writer, seed);
            Assert.Equal(incompleteRef, File.ReadAllBytes(refPath));
            Assert.Empty(reports);
            AssertHealthyActive(resources.Data, dataFileId, dataLength);
            HeadEntry actual = resources.Heads.ReadHead(5).Unwrap();
            Assert.Equal(ordinaryRevision, actual.Id);
            Assert.Equal(ordinaryPayload, actual.Payload.ToArray());
            HeadStoreRecoveryReport report = Assert.Single(reports);
            Assert.Equal(HeadStoreRecoveryFileKind.Head, report.FileKind);
            Assert.Equal(5u, report.LocalId);
            AssertTruncated(report.Recovery, completeRef.Length);
            Assert.Equal(ordinaryRevision, resources.Heads.ReadHead(5).Unwrap().Id);
            Assert.Equal(report, Assert.Single(reports)); // A successful repeated read does not produce another recovery action.
            Assert.Equal(seed.Head, Describe(writer, writer.ReadCurrentCommit(seed.Branch).Commit.Id).Address);
            Assert.Equal(seed.SchemaRoot, writer.SharedSchemaRoot);
            AssertCompletedFrames(resources.Data, seed.Frames);
            Assert.Equal(0, writer.GraphStore.AppendedFrames);
        }
        Assert.Equal(completeRef, File.ReadAllBytes(refPath));
        Dictionary<string, byte[]> expected = new(damagedFiles) { [Path.GetRelativePath(_path, refPath)] = completeRef };
        AssertFilesEqual(expected, SnapshotFiles());
        using Repository fresh = Repository.OpenStorageReadOnlyExisting(_path, Models());
        HeadEntry coldRef = ResourcesOf(fresh).Heads.ReadHead(5).Unwrap();
        Assert.Equal(ordinaryRevision, coldRef.Id);
        Assert.Equal(ordinaryPayload, coldRef.Payload.ToArray());
        Assert.Empty(ResourcesOf(fresh).Heads.RecoveryReports);
        AssertHistory(fresh, seed);
        AssertCompletedFrames(ResourcesOf(fresh).Data, seed.Frames);
    }

    private SeedResult Seed() {
        using Repository seed = Repository.CreateStorageNew(_path, Models());
        DB087Widget root = Ring();
        BranchInfo branch = seed.CreateRootBranchFromState("main", root);
        using WorkingCopy main = seed.OpenWorkingCopy(branch.Id);
        root = Assert.IsType<DB087Widget>(main.State);
        root.Numbers[7] = 200;
        seed.CommitBranches([main.CreateEventCommitRequest(root)], [Extra]);
        root.Numbers[7] = 201;
        CommitInfo state = main.CommitState();
        ObjectId listId = Assert.Single(main.Baseline!.Objects, pair => pair.Value.Current.Kind == ObjectStateKind.List).Key;
        var storage = main.Baseline.Objects.ToDictionary(pair => pair.Key, pair => pair.Value.Storage!.Value);
        return new(branch.Id, Describe(seed, state.Id).Address, main.StateId!.Value, listId, seed.SharedSchemaRoot!.Value,
            seed.ReadFrames(branch.Id).Select(frame => Describe(seed, frame.Id).Address).ToArray(), storage, ReadFrames(ResourcesOf(seed).Data));
    }
    private static void AssertHistory(Repository repository, SeedResult seed) {
        Assert.Equal(seed.Head, Describe(repository, repository.ReadCurrentCommit(seed.Branch).Commit.Id).Address);
        Assert.Equal(seed.SchemaRoot, repository.SharedSchemaRoot);
        CommitInfo[] frames = repository.ReadFrames(seed.Branch).ToArray();
        Assert.Equal(seed.History, frames.Select(frame => Describe(repository, frame.Id).Address));
        Assert.Equal(new[] { CommitKind.State, CommitKind.Event, CommitKind.State }, frames.Select(frame => frame.Kind));
        AssertRing(repository.LoadState(frames[0].Id), 7);
        AssertRing(repository.LoadEvent(frames[1].Id), 200);
        AssertRing(repository.LoadState(frames[2].Id), 201);
        Assert.Equal(seed.RootId, Describe(repository, frames[2].Id).RootId);
        StoredGraphRevision graph = Snapshot(repository, frames[2].Id);
        Assert.Equal(seed.Storage.Keys.Select(id => id.Value).Order(), graph.ObjectHeads.Keys.Order());
        foreach ((ObjectId id, ObjectStorageInfo storage) in seed.Storage) {
            Assert.Equal(storage.Head, graph.ObjectHeads[id.Value]);
            Assert.Equal(storage.ReconstructionPayloadBytes, repository.GraphStore.ReadObjectChain(graph, id.Value).ReconstructionPayloadBytes);
        }
    }
    private static FrameImage[] ReadFrames(DataStore data) {
        List<FrameInfo> inventory = [];
        data.Inventory(inventory.Add).Unwrap();
        return inventory.Select(info => {
            using FrameRead frame = data.ReadFrame(info.Address).Unwrap();
            return new FrameImage(info.Address, frame.Tag, frame.TailMetaLength, frame.IsTombstone, frame.PayloadAndMeta.ToArray());
        }).ToArray();
    }
    private static void AssertCompletedFrames(DataStore data, FrameImage[] expected, bool allowAppended = false) {
        FrameImage[] actual = ReadFrames(data);
        if (!allowAppended) { Assert.Equal(expected.Length, actual.Length); }
        foreach (FrameImage old in expected) {
            FrameImage retained = Assert.Single(actual, image => image.Address == old.Address);
            Assert.Equal(old.Tag, retained.Tag);
            Assert.Equal(old.TailMetaLength, retained.TailMetaLength);
            Assert.Equal(old.IsTombstone, retained.IsTombstone);
            Assert.Equal(old.PayloadAndMeta, retained.PayloadAndMeta);
        }
    }
    private void AppendIncompleteNextHead(string path) {
        string relative = Path.GetRelativePath(Path.GetFullPath(_path), Path.GetFullPath(path));
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) {
            throw new InvalidOperationException("The tail fixture must stay in its own closed repository.");
        }
        using FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.None);
        stream.WriteByte(1); // A partial next HeadLen, retaining every previously complete byte.
        stream.Flush(flushToDisk: true);
    }
    private static void AssertTruncated(RbfTailRecoveryReport report, long completeLength) {
        Assert.Equal(RbfTailRecoveryAction.Truncated, report.Action);
        Assert.Equal(completeLength + 1, report.OriginalLength);
        Assert.Equal(completeLength, report.FinalLength);
        Assert.Equal(completeLength, report.AffectedFrameOffset);
        Assert.Null(report.FrameTicket);
    }
    private static void AssertHealthyActive(DataStore data, uint fileId, long completeLength) {
        var report = Assert.Single(data.RecoveryReports);
        Assert.Equal(fileId, report.Key);
        Assert.Equal(RbfTailRecoveryAction.None, report.Value.Action);
        Assert.Equal(completeLength, report.Value.OriginalLength);
        Assert.Equal(completeLength, report.Value.FinalLength);
        Assert.Null(report.Value.AffectedFrameOffset);
        Assert.Null(report.Value.FrameTicket);
    }
    private static uint FileIdOf(FrameAddress address) {
        Span<byte> bytes = stackalloc byte[FrameAddress.EncodedSize];
        Assert.True(address.TryWrite(bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }
    private static StateModelRegistry Models() {
        StateModelRegistry registry = new();
        registry.Register(global::Atelia.DurableGraph.Generated.Family_5030576964676574.Definition);
        return registry;
    }
    private static DB087Widget Ring() {
        List<int> numbers = Enumerable.Range(0, 128).ToList();
        DB087Widget root = new() { Value = 1, Numbers = numbers };
        root.Child = new() { Value = 2, Numbers = numbers, Child = root };
        return root;
    }
    private static DB087Widget AssertRing(IDurableObject? value, int atSeven, int atEight = 8) {
        DB087Widget root = Assert.IsType<DB087Widget>(value);
        DB087Widget child = Assert.IsType<DB087Widget>(root.Child);
        Assert.Equal(1, root.Value);
        Assert.Equal(2, child.Value);
        Assert.Same(root, child.Child);
        Assert.Same(root.Numbers, child.Numbers);
        Assert.Equal(128, root.Numbers.Count);
        for (int index = 0; index < 128; index++) { Assert.Equal(index == 7 ? atSeven : index == 8 ? atEight : index, root.Numbers[index]); }
        return root;
    }
    private Dictionary<string, byte[]> SnapshotFiles() => Directory.GetFiles(_path, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(_path, path), File.ReadAllBytes);
    private static void AssertFilesEqual(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual) {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach ((string name, byte[] bytes) in expected) { Assert.Equal(bytes, actual[name]); }
    }
    private sealed record FrameImage(FrameAddress Address, uint Tag, int TailMetaLength, bool IsTombstone, byte[] PayloadAndMeta);
    private sealed record SeedResult(BranchId Branch, FrameAddress Head, ObjectId RootId, ObjectId ListId, FrameAddress SchemaRoot,
        FrameAddress[] History, Dictionary<ObjectId, ObjectStorageInfo> Storage, FrameImage[] Frames);
    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
