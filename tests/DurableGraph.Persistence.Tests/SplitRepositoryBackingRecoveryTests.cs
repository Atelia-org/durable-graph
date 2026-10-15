using System.Buffers.Binary;

using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Atelia.Rbf;
using Atelia.VersionStore;
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
            Assert.Empty(resources.Versions.RecoveryReports);
            Assert.Equal(seed.Head, recovered.GetHead("main").RevisionAddress);
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
            using BranchCheckout checkout = writer.CheckoutShared("main");
            DB087Widget root = AssertRing(checkout.State, 201);
            Assert.Equal(seed.Head, checkout.StateRevisionAddress);
            Assert.Equal(seed.RootId, checkout.StateId);
            Assert.Equal(seed.Storage.Keys.OrderBy(id => id.Value), checkout.Baseline!.Objects.Keys.OrderBy(id => id.Value));
            foreach ((ObjectId id, ObjectStorageInfo storage) in seed.Storage) { Assert.Equal(storage, checkout.Baseline.Objects[id].Storage); }
            root.Numbers[8] = 202;
            CheckpointAddress saved = checkout.CommitState();
            continued = saved.RevisionAddress;
            StoredGraphRevision graph = writer.GraphStore.ReadGraph(continued);
            ObjectVersionRecord delta = Assert.Single(writer.GraphStore.ReadBatch(graph.OwnedBatch!.Value).Records);
            ObjectStorageInfo prior = seed.Storage[seed.ListId];
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
            Assert.Equal(seed.ListId.Value, delta.ObjectId);
            Assert.Equal(prior.Head, delta.PriorAddress);
            Assert.Equal(seed.Head, graph.SourceGraph);
            Assert.Equal(seed.Head, graph.Parent);
            Assert.Equal(prior.ReconstructionPayloadBytes + ObjectVersionPayloadSize.GetDeltaPayloadBytes(delta.Body.Length, prior.Head),
                checkout.Baseline!.Objects[seed.ListId].Storage!.Value.ReconstructionPayloadBytes);
            AssertCompletedFrames(ResourcesOf(writer).Data, seed.Frames, allowAppended: true);
        }
        using Repository fresh = Repository.OpenStorageReadOnlyExisting(_path, Models());
        Assert.Equal(continued, fresh.GetHead("main").RevisionAddress);
        AssertRing(fresh.ReadState(fresh.GetHead("main")), 201, 202);
        CheckpointAddress[] history = fresh.ReadFrames("main").ToArray();
        Assert.Equal(seed.History, history.Take(seed.History.Length).Select(frame => frame.RevisionAddress));
        AssertRing(fresh.ReadState(history[0]), 7);
        AssertRing(fresh.ReadEvent(history[1]), 200);
        AssertRing(fresh.ReadState(history[2]), 201);
        AssertCompletedFrames(ResourcesOf(fresh).Data, seed.Frames, allowAppended: true);
        Assert.Empty(ResourcesOf(fresh).Data.RecoveryReports);
    }

    [Fact]
    public void UnselectedOrdinaryRefTailStaysUntouchedUntilItsActualLazyFirstRead() {
        SeedResult seed = Seed();
        byte[] ordinaryPayload = [30, 40, 50];
        RefRevision ordinaryRevision;
        using (Repository owner = Repository.OpenStorageExisting(_path, Models())) {
            var versions = ResourcesOf(owner).Versions;
            // DG reserves only 1/2. The explicit-ID CreateRef overload cannot create
            // ordinary ref3; automatic H+1 is the actual public VersionStore route.
            RefRevision created = versions.CreateRef(new byte[] { 10, 20 }, out PublicationOutcome createdOutcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, createdOutcome);
            Assert.Equal(3u, created.RefId.LocalId);
            ordinaryRevision = versions.PublishRef(created.RefId, ordinaryPayload, out PublicationOutcome published).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, published);
        }
        string refPath = Path.Combine(_path, "versions", "refs", "00000003.rbf");
        uint dataFileId = FileIdOf(seed.Head);
        long dataLength = new FileInfo(Path.Combine(_path, "data", "active", dataFileId.ToString("x8") + ".rbf")).Length;
        byte[] completeRef = File.ReadAllBytes(refPath);
        AppendIncompleteNextHead(refPath);
        byte[] incompleteRef = File.ReadAllBytes(refPath);
        Dictionary<string, byte[]> damagedFiles = SnapshotFiles();
        Assert.Equal(completeRef.Length + 1, incompleteRef.Length);

        using (Repository readonlyOwner = Repository.OpenStorageReadOnlyExisting(_path, Models())) {
            var versions = ResourcesOf(readonlyOwner).Versions;
            Assert.Empty(versions.RecoveryReports);
            Assert.Equal(incompleteRef, File.ReadAllBytes(refPath));
            Assert.Equal(seed.Head, readonlyOwner.GetHead("main").RevisionAddress);
            AssertHistory(readonlyOwner, seed);
            Assert.Equal(incompleteRef, File.ReadAllBytes(refPath));
            Assert.Empty(versions.RecoveryReports);
        }
        AssertFilesEqual(damagedFiles, SnapshotFiles()); // DG's required-root readonly open did not audit/ref-repair the unrelated control file.

        using (Repository writer = Repository.OpenStorageExisting(_path, Models())) {
            RepositoryResources resources = ResourcesOf(writer);
            IReadOnlyList<VersionStoreRecoveryReport> reports = resources.Versions.RecoveryReports;
            Assert.Empty(reports);
            Assert.Equal(incompleteRef, File.ReadAllBytes(refPath));
            AssertHistory(writer, seed);
            Assert.Equal(incompleteRef, File.ReadAllBytes(refPath));
            Assert.Empty(reports);
            AssertHealthyActive(resources.Data, dataFileId, dataLength);
            RefBytesSnapshot actual = resources.Versions.ReadRef(3).Unwrap();
            Assert.Equal(ordinaryRevision, actual.Revision);
            Assert.Equal(ordinaryPayload, actual.Payload.ToArray());
            VersionStoreRecoveryReport report = Assert.Single(reports);
            Assert.Equal(VersionStoreRecoveryFileKind.Ref, report.FileKind);
            Assert.Equal(3u, report.LocalId);
            AssertTruncated(report.Recovery, completeRef.Length);
            Assert.Equal(ordinaryRevision, resources.Versions.ReadRef(3).Unwrap().Revision);
            Assert.Equal(report, Assert.Single(reports)); // A successful repeated read does not produce another recovery action.
            Assert.Equal(seed.Head, writer.GetHead("main").RevisionAddress);
            Assert.Equal(seed.SchemaRoot, writer.SharedSchemaRoot);
            AssertCompletedFrames(resources.Data, seed.Frames);
            Assert.Equal(0, writer.GraphStore.AppendedFrames);
        }
        Assert.Equal(completeRef, File.ReadAllBytes(refPath));
        Dictionary<string, byte[]> expected = new(damagedFiles) { [Path.GetRelativePath(_path, refPath)] = completeRef };
        AssertFilesEqual(expected, SnapshotFiles());
        using Repository fresh = Repository.OpenStorageReadOnlyExisting(_path, Models());
        RefBytesSnapshot coldRef = ResourcesOf(fresh).Versions.ReadRef(3).Unwrap();
        Assert.Equal(ordinaryRevision, coldRef.Revision);
        Assert.Equal(ordinaryPayload, coldRef.Payload.ToArray());
        Assert.Empty(ResourcesOf(fresh).Versions.RecoveryReports);
        AssertHistory(fresh, seed);
        AssertCompletedFrames(ResourcesOf(fresh).Data, seed.Frames);
    }

    private SeedResult Seed() {
        using Repository seed = Repository.CreateStorageNew(_path, Models());
        DB087Widget root = Ring();
        using BranchCheckout main = seed.CreateSharedBranch("main", root);
        main.Catalog.Set(Extra);
        root.Numbers[7] = 200;
        main.CommitEvent(root);
        root.Numbers[7] = 201;
        CheckpointAddress state = main.CommitState();
        ObjectId listId = Assert.Single(main.Baseline!.Objects, pair => pair.Value.Current.Kind == ObjectStateKind.List).Key;
        var storage = main.Baseline.Objects.ToDictionary(pair => pair.Key, pair => pair.Value.Storage!.Value);
        return new(state.RevisionAddress, state.RootId, listId, seed.SharedSchemaRoot!.Value,
            seed.ReadFrames("main").Select(frame => frame.RevisionAddress).ToArray(), storage, ReadFrames(ResourcesOf(seed).Data));
    }
    private static void AssertHistory(Repository repository, SeedResult seed) {
        Assert.Equal(seed.Head, repository.GetHead("main").RevisionAddress);
        Assert.Equal(seed.SchemaRoot, repository.SharedSchemaRoot);
        CheckpointAddress[] frames = repository.ReadFrames("main").ToArray();
        Assert.Equal(seed.History, frames.Select(frame => frame.RevisionAddress));
        Assert.Equal(new[] { CheckpointKind.State, CheckpointKind.Event, CheckpointKind.State }, frames.Select(frame => frame.Kind));
        AssertRing(repository.ReadState(frames[0]), 7);
        AssertRing(repository.ReadEvent(frames[1]), 200);
        AssertRing(repository.ReadState(frames[2]), 201);
        Assert.Equal(seed.RootId, frames[2].RootId);
        StoredGraphRevision graph = repository.GraphStore.ReadGraph(seed.Head);
        Assert.Equal(seed.Storage.Keys.Select(id => id.Value).Order(), graph.ObjectHeads.Keys.Order());
        foreach ((ObjectId id, ObjectStorageInfo storage) in seed.Storage) {
            Assert.Equal(storage.Head, graph.ObjectHeads[id.Value]);
            Assert.Equal(storage.ReconstructionPayloadBytes, repository.GraphStore.ReadObjectChain(seed.Head, id.Value).ReconstructionPayloadBytes);
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
    private sealed record SeedResult(FrameAddress Head, ObjectId RootId, ObjectId ListId, FrameAddress SchemaRoot,
        FrameAddress[] History, Dictionary<ObjectId, ObjectStorageInfo> Storage, FrameImage[] Frames);
    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
