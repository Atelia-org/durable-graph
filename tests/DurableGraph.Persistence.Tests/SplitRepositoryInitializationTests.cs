using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.VersionStore;
using DataStore = Atelia.FrameStore.FrameStore;
using PublicationStore = Atelia.VersionStore.VersionStore;

namespace Atelia.DurableGraph.Persistence.Tests;

// Lower CreateRef failure windows followed by real DG Open. These are ordinary
// exception/close/reopen witnesses, not fault injection into DG.CreateNew or a process crash.
public sealed class SplitRepositoryInitializationTests : IDisposable {
    private const string Prefix = "dg-split-initialization-";
    private const uint SentinelTag = 0x53494E49;
    private static readonly byte[] SentinelBody = [17, 29, 41, 53, 67];
    private static readonly byte[] SentinelMetadata = [79, 83];
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private string DataPath => Path.Combine(_path, "data");
    private string VersionsPath => Path.Combine(_path, "versions");
    private string PendingPath => Path.Combine(VersionsPath, "creating", "pending.rbf");

    [Fact]
    public void SingleFormalRootFreshReadOnlyAndWritableOpenRejectWithoutCompletingOrRemovingContent() {
        Directory.CreateDirectory(_path);
        byte[] branches = EmptyRootPayload("P0R1"u8);
        FrameAddress sentinel;
        RefRevision first;
        using (DataStore data = DataStore.Create(DataPath))
        using (PublicationStore versions = PublicationStore.Create(VersionsPath, data, reservedRefIdUpperBound: 2)) {
            sentinel = data.Append(SentinelTag, SentinelBody, SentinelMetadata).Unwrap();
            first = versions.CreateRef(1, branches, out PublicationOutcome outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            AssertSentinel(data, sentinel);
        }
        Dictionary<string, byte[]> complete = SnapshotTree(_path);
        Assert.False(File.Exists(PendingPath));
        foreach (bool readOnly in new[] { true, false }) {
            RejectMissingSharedRoot(readOnly);
            AssertFilesEqual(complete, SnapshotTree(_path));
            // A new exclusive FS + VS acquisition after each failed DG Open proves
            // that both borrowed and owning backing resources were released.
            AssertBackingRootsAndSentinel(first, sentinel, branches, shared: null);
            AssertFilesEqual(complete, SnapshotTree(_path));
        }
    }

    [Theory]
    [InlineData("BeforeNewRefRename", PublicationOutcome.NotAttempted, false)]
    [InlineData("AfterConfirmed", PublicationOutcome.Confirmed, true)]
    public void LowerSecondCreateRefFailureFreshDgOpenFollowsFormalRootsAndReportedOutcome(
        string stage, PublicationOutcome expectedOutcome, bool bothFormal) {
        Directory.CreateDirectory(_path);
        byte[] branches = EmptyRootPayload("P0R1"u8), shared = EmptyRootPayload("P0S1"u8);
        FrameAddress sentinel;
        RefRevision first;
        using (DataStore data = DataStore.Create(DataPath))
        using (PublicationStore versions = PublicationStore.Create(VersionsPath, data, reservedRefIdUpperBound: 2)) {
            sentinel = data.Append(SentinelTag, SentinelBody, SentinelMetadata).Unwrap();
            int hookCalls = 0;
            InstallPublicationFault(versions, stage, () => {
                if (++hookCalls == 2) { throw new IOException("Second lower CreateRef publication window."); }
            });
            first = versions.CreateRef(1, branches, out PublicationOutcome firstOutcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, firstOutcome);
            PublicationOutcome secondOutcome = PublicationOutcome.Unknown;
            Assert.Throws<IOException>(() => versions.CreateRef(2, shared, out secondOutcome).Unwrap());
            Assert.Equal(2, hookCalls);
            Assert.Equal(expectedOutcome, secondOutcome);
            Assert.Throws<InvalidOperationException>(versions.EnsureUsable);
            // VS faults but does not own the borrowed FS. The scope closes both
            // before any fresh DG owner, and the completed data frame remains readable.
            AssertSentinel(data, sentinel);
        }
        Dictionary<string, byte[]> completedData = SnapshotTree(DataPath);
        Dictionary<string, byte[]> formalRefs = SnapshotTree(Path.Combine(VersionsPath, "refs"));
        Assert.Equal(bothFormal ? new[] { "00000001.rbf", "00000002.rbf" } : new[] { "00000001.rbf" },
            formalRefs.Keys.Order());
        Assert.Equal(!bothFormal, File.Exists(PendingPath));

        if (!bothFormal) {
            RejectMissingSharedRoot(readOnly: true);
            Assert.True(File.Exists(PendingPath)); // RO Open never cancels private pending.
            AssertStableCompletedFiles();
            // The next exclusive DG Open itself encounters and cancels pending;
            // reaching RefNotFound also proves that the failed RO owner released its locks.
            RejectMissingSharedRoot(readOnly: false);
            Assert.False(File.Exists(PendingPath)); // Normal writable VS admission cancels it.
            AssertStableCompletedFiles();
            AssertBackingRootsAndSentinel(first, sentinel, branches, shared: null);
            AssertStableCompletedFiles();
            return;
        }

        // No second RefRevision was delivered, yet its out evidence and both formal
        // files agree. Fresh DG can open the two complete roots without initialization repair.
        Dictionary<string, byte[]> complete = SnapshotTree(_path);
        using (Repository reader = Repository.OpenMetaBootstrapReadOnlyExisting(_path, Models())) {
            Assert.Empty(reader.ListBranches());
            Assert.Null(reader.SharedSchemaRoot);
            AssertSentinel(ResourcesOf(reader).Data, sentinel);
        }
        AssertFilesEqual(complete, SnapshotTree(_path));
        AssertBackingRootsAndSentinel(first, sentinel, branches, shared);
        AssertStableCompletedFiles();
        SaveGeneratedGraphThenColdContinue(sentinel);

        void AssertStableCompletedFiles() {
            AssertFilesEqual(completedData, SnapshotTree(DataPath));
            AssertFilesEqual(formalRefs, SnapshotTree(Path.Combine(VersionsPath, "refs")));
        }
    }

    private void SaveGeneratedGraphThenColdContinue(FrameAddress sentinel) {
        FrameAddress state0, schemaRoot;
        ObjectId listId;
        Dictionary<ObjectId, ObjectStorageInfo> storage;
        using (Repository seed = Repository.OpenMetaBootstrapExisting(_path, Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            seed.PreparedStateReuseEnabled = false;
            using BranchCheckout main = seed.CreateBranch("main", Ring());
            state0 = main.Head.RevisionAddress;
            schemaRoot = seed.SharedSchemaRoot!.Value;
            seed.CreateTag("initial", main.Head);
            listId = Assert.Single(main.Baseline!.Objects, pair => pair.Value.Current.Kind == ObjectStateKind.List).Key;
            storage = main.Baseline.Objects.ToDictionary(pair => pair.Key, pair => pair.Value.Storage!.Value);
            Assert.Equal(3, storage.Count);
            P0GraphSnapshot graph = seed.MetaBootstrapStore.ReadGraph(state0);
            Assert.Equal(schemaRoot, graph.SchemaRoot);
            foreach ((ObjectId id, ObjectStorageInfo info) in storage) {
                Assert.Equal(info.Head, graph.Objects[id.Value]);
                Assert.Equal(info.ReconstructionPayloadBytes,
                    seed.MetaBootstrapStore.ReadObjectChain(state0, id.Value).ReconstructionPayloadBytes);
            }
            Assert.NotEqual(state0, storage[listId].Head);
            AssertSentinel(ResourcesOf(seed).Data, sentinel);
        }

        FrameAddress state1;
        ObjectStorageInfo continuedStorage;
        using (Repository reopened = Repository.OpenMetaBootstrapExisting(_path, Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            reopened.PreparedStateReuseEnabled = false;
            Assert.Equal(state0, reopened.GetHead("main").RevisionAddress);
            Assert.Equal(schemaRoot, reopened.SharedSchemaRoot);
            using BranchCheckout cold = reopened.Checkout("main");
            DB087Widget root = AssertRing(cold.State, 7);
            Assert.Equal(state0, cold.StateRevisionAddress);
            Assert.Equal(storage.Keys.Order(), cold.Baseline!.Objects.Keys.Order());
            foreach ((ObjectId id, ObjectStorageInfo info) in storage) {
                Assert.Equal(info, cold.Baseline.Objects[id].Storage);
            }
            root.Numbers[7] = 200;
            CheckpointAddress next = cold.CommitState();
            state1 = next.RevisionAddress;
            P0GraphSnapshot graph = reopened.MetaBootstrapStore.ReadGraph(state1);
            Assert.Equal(state0, graph.SourceGraph);
            Assert.Equal(schemaRoot, graph.SchemaRoot);
            Assert.NotNull(graph.OwnedBatch);
            P0Batch batch = reopened.MetaBootstrapStore.ReadBatch(graph.OwnedBatch!.Value);
            Assert.Equal(state0, batch.SourceGraph);
            Assert.Equal(schemaRoot, batch.SchemaRoot);
            ObjectVersionRecord delta = Assert.Single(batch.Records);
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
            Assert.Equal(listId.Value, delta.ObjectId);
            Assert.Equal(storage[listId].Head, delta.PriorAddress);
            continuedStorage = cold.Baseline!.Objects[listId].Storage!.Value;
            Assert.Equal(graph.OwnedBatch.Value, graph.Objects[listId.Value]);
            Assert.Equal(graph.OwnedBatch.Value, continuedStorage.Head);
            Assert.NotEqual(state1, continuedStorage.Head);
            Assert.NotNull(delta.EncodedPayloadBytes);
            Assert.Equal(storage[listId].ReconstructionPayloadBytes + delta.EncodedPayloadBytes!.Value,
                continuedStorage.ReconstructionPayloadBytes);
            Assert.Equal(continuedStorage.ReconstructionPayloadBytes,
                reopened.MetaBootstrapStore.ReadObjectChain(state1, listId.Value).ReconstructionPayloadBytes);
            foreach ((ObjectId id, ObjectStorageInfo info) in storage.Where(pair => pair.Key != listId)) {
                Assert.Equal(info, cold.Baseline.Objects[id].Storage);
                Assert.Equal(info.Head, graph.Objects[id.Value]);
            }
            AssertRing(reopened.ReadState(next), 200);
            AssertRing(reopened.ReadState(reopened.ResolveTag("initial")), 7);
            AssertSentinel(ResourcesOf(reopened).Data, sentinel);
        }
        using Repository final = Repository.OpenMetaBootstrapReadOnlyExisting(_path, Models());
        Assert.Equal(state1, final.GetHead("main").RevisionAddress);
        Assert.Equal(schemaRoot, final.SharedSchemaRoot);
        AssertRing(final.ReadState(final.GetHead("main")), 200);
        AssertRing(final.ReadState(final.ResolveTag("initial")), 7);
        P0ObjectChain chain = final.MetaBootstrapStore.ReadObjectChain(state1, listId.Value);
        Assert.Equal(continuedStorage.Head, chain.Head);
        Assert.Equal(continuedStorage.ReconstructionPayloadBytes, chain.ReconstructionPayloadBytes);
        AssertSentinel(ResourcesOf(final).Data, sentinel);
    }

    private void RejectMissingSharedRoot(bool readOnly) {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => {
            using Repository unexpected = readOnly
                ? Repository.OpenMetaBootstrapReadOnlyExisting(_path, Models())
                : Repository.OpenMetaBootstrapExisting(_path, Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared);
        });
        Assert.Contains("[VersionStore.RefNotFound]", error.Message, StringComparison.Ordinal);
    }

    private void AssertBackingRootsAndSentinel(RefRevision first, FrameAddress sentinel, byte[] branches, byte[]? shared) {
        using DataStore data = DataStore.Open(DataPath);
        using PublicationStore versions = PublicationStore.Open(VersionsPath, data);
        Assert.Equal(2u, versions.ReservedRefIdUpperBound);
        Assert.Equal(shared is null ? new uint[] { 1 } : new uint[] { 1, 2 },
            versions.ListRefs().Unwrap().Select(id => id.LocalId).Order());
        RefBytesSnapshot branch = versions.ReadRef(1).Unwrap();
        Assert.Equal(first, branch.Revision);
        Assert.Equal(branches, branch.Payload.ToArray());
        if (shared is null) {
            var missing = versions.ReadRef(2);
            Assert.True(missing.IsFailure);
            Assert.Equal("VersionStore.RefNotFound", missing.Error!.ErrorCode);
        } else { Assert.Equal(shared, versions.ReadRef(2).Unwrap().Payload.ToArray()); }
        AssertSentinel(data, sentinel);
    }

    private static void AssertSentinel(DataStore data, FrameAddress address) {
        using FrameRead frame = data.ReadFrame(address).Unwrap();
        Assert.Equal(SentinelTag, frame.Tag);
        Assert.Equal(SentinelBody, frame.Payload.ToArray());
        Assert.Equal(SentinelMetadata, frame.TailMeta.ToArray());
    }

    private static byte[] EmptyRootPayload(ReadOnlySpan<byte> magic) =>
        [.. magic, .. RootMapCodec.Encode(new Dictionary<string, FrameAddress>()).Unwrap()];
    private static Dictionary<string, byte[]> SnapshotTree(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(root, path), File.ReadAllBytes);
    private static void AssertFilesEqual(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual) {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach ((string name, byte[] bytes) in expected) { Assert.Equal(bytes, actual[name]); }
    }
    private static StateModelRegistry Models() {
        StateModelRegistry models = new();
        models.Register(global::Atelia.DurableGraph.Generated.Family_5030576964676574.Definition);
        return models;
    }
    private static DB087Widget Ring() {
        List<int> numbers = Enumerable.Range(0, 128).ToList();
        DB087Widget root = new() { Value = 1, Numbers = numbers };
        root.Child = new() { Value = 2, Numbers = numbers, Child = root };
        return root;
    }
    private static DB087Widget AssertRing(IDurableObject? value, int atSeven) {
        DB087Widget root = Assert.IsType<DB087Widget>(value);
        DB087Widget child = Assert.IsType<DB087Widget>(root.Child);
        Assert.Equal(1, root.Value);
        Assert.Equal(2, child.Value);
        Assert.Same(root, child.Child);
        Assert.Same(root.Numbers, child.Numbers);
        Assert.Equal(128, root.Numbers.Count);
        for (int i = 0; i < root.Numbers.Count; i++) { Assert.Equal(i == 7 ? atSeven : i, root.Numbers[i]); }
        return root;
    }
    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
