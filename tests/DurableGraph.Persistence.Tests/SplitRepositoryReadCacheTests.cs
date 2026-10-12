using System.Buffers;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class SplitRepositoryReadCacheTests : IDisposable {
    private const string Prefix = "dg-split-repository-cache-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly DurableSchema Extra = new("SplitCacheExtra", 1, new DurableFieldInfo(1, TypeTag.Int32));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PhysicalCacheModePreservesColdGeneratedHistoryTypedQualificationAndDeltaContinuation(bool cached) {
        long budget = cached ? StateRevisionStore.DefaultReadCacheBudgetBytes : 0;
        ObjectId listId;
        ObjectStorageInfo storage;
        FrameAddress savedAddress, schemaRoot;
        using (Repository seed = Repository.CreateMetaBootstrapNew(_path, Models(), readCacheBudgetBytes: budget)) {
            seed.PreparedStateReuseEnabled = false;
            DB087Widget root = Ring();
            using BranchCheckout main = seed.CreateBranch("main", root);
            seed.CreateTag("state0", main.Head);
            main.Catalog.Set(Extra);
            root.Numbers[7] = 200;
            seed.CreateTag("event", main.CommitEvent(root));
            root.Numbers[7] = 201;
            CheckpointAddress s1 = main.CommitState();
            seed.CreateTag("state1", s1);
            listId = Assert.Single(main.Baseline!.Objects, pair => pair.Value.Current.Kind == ObjectStateKind.List).Key;
            storage = main.Baseline.Objects[listId].Storage!.Value;
            P0GraphSnapshot stateGraph = seed.MetaBootstrapStore.ReadGraph(s1.RevisionAddress);
            ObjectVersionRecord delta = Assert.Single(seed.MetaBootstrapStore.ReadBatch(stateGraph.OwnedBatch!.Value).Records);
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
            Assert.Equal(listId.Value, delta.ObjectId);
            long frames = seed.MetaBootstrapStore.AppendedFrames;
            CheckpointAddress unchanged = main.CommitState();
            savedAddress = unchanged.RevisionAddress;
            schemaRoot = seed.SharedSchemaRoot!.Value;
            P0GraphSnapshot noChange = seed.MetaBootstrapStore.ReadGraph(savedAddress);
            Assert.Null(noChange.OwnedBatch);
            Assert.Equal(stateGraph.DirectoryRoot, noChange.DirectoryRoot);
            Assert.Equal(frames + 1, seed.MetaBootstrapStore.AppendedFrames);
            Assert.Equal(storage, main.Baseline.Objects[listId].Storage);
            Assert.Null(seed.PreparedStateEntry); // Hot save is not preparation-slot admission.
        }
        Dictionary<string, byte[]> files = SnapshotFiles();
        // All previous owners and caches are closed. Meta loads before any business registration.
        using (Repository metaOnly = Repository.OpenMetaBootstrapReadOnlyExisting(_path, new(), readCacheBudgetBytes: budget)) {
            Assert.Equal(schemaRoot, metaOnly.SharedSchemaRoot);
            P0CatalogView catalog = P0CatalogView.Load(metaOnly.MetaBootstrapStore, schemaRoot);
            Assert.Equal(Extra, catalog.Schemas[new(Extra.Type, Extra.Version)]);
        }
        P0Batch? heldBatch = null;
        using (Repository reader = Repository.OpenMetaBootstrapReadOnlyExisting(_path, Models(), readCacheBudgetBytes: budget)) {
            reader.PreparedStateReuseEnabled = false;
            CheckpointAddress current = reader.GetHead("main"), old = reader.ResolveTag("state0"), saved = reader.ResolveTag("state1");
            Assert.Equal(savedAddress, current.RevisionAddress);
            DB087Widget first = AssertRing(reader.ReadState(saved), 201);
            P0GraphReadCacheStatistics before = reader.MetaBootstrapStore.ReadCacheStatistics;
            DB087Widget second = AssertRing(reader.ReadState(saved), 201);
            Assert.NotSame(first, second);
            Assert.NotSame(first.Numbers, second.Numbers); // Physical memoization never materializes an editable or typed graph cache.
            P0GraphReadCacheStatistics after = reader.MetaBootstrapStore.ReadCacheStatistics;
            if (cached) { AssertPhysicalProductionEqual(before, after); Assert.True(after.BatchHits > before.BatchHits); }
            else {
                Assert.True(after.BatchDecodes > before.BatchDecodes);
                Assert.True(after.DirectoryMaterializations > before.DirectoryMaterializations);
                Assert.True(after.DirectoryReplayFrames > before.DirectoryReplayFrames);
                Assert.Equal(0, after.ResidentChargeBytes);
                Assert.Equal(0, after.EntryCount);
            }
            AssertRing(reader.ReadState(old), 7);
            AssertRing(reader.ReadEvent(reader.ResolveTag("event")), 200);
            var pair = reader.ReadPair(old, saved);
            AssertRing(pair.First, 7);
            AssertRing(pair.Second, 201);
            Assert.Null(reader.PreparedStateEntry);
            P0GraphSnapshot savedGraph = reader.MetaBootstrapStore.ReadGraph(saved.RevisionAddress);
            heldBatch = reader.MetaBootstrapStore.ReadBatch(savedGraph.OwnedBatch!.Value);
            Assert.Equal(storage.ReconstructionPayloadBytes,
                reader.MetaBootstrapStore.ReadObjectChain(saved.RevisionAddress, listId.Value).ReconstructionPayloadBytes);
            // A low-level typed helper may read old layouts through an additive newer
            // catalog. Saving authority instead requires the reader-issued exact-S environment.
            StateModelRegistry.FrozenConfiguration configuration = Models().FreezeConfiguration();
            DecodedRevision decoded = DecodedRevision.ReadSplit(reader.MetaBootstrapStore, saved.RevisionAddress, configuration);
            Assert.NotEqual(schemaRoot, reader.MetaBootstrapStore.ReadMetadata(old.RevisionAddress).SchemaRoot);
            P0DecodedRows compatibleLayout = P0TypedReader.Read(reader.MetaBootstrapStore, old.RevisionAddress,
                decoded.BindingEnvironment!.Models, decoded.BindingEnvironment.SelectedSchemaView);
            Assert.Equal(3, compatibleLayout.Rows.Count);
            Assert.Equal(reader.MetaBootstrapStore.ReadMetadata(old.RevisionAddress).SchemaRoot, compatibleLayout.Snapshot.SchemaRoot);
            P0GraphReadCacheStatistics beforeWrongEnvironment = reader.MetaBootstrapStore.ReadCacheStatistics;
            Assert.Throws<InvalidDataException>(() => DecodedRevision.ReadSplit(reader.MetaBootstrapStore, old.RevisionAddress,
                configuration, decoded.BindingEnvironment));
            DecodedRevision positiveReuse = DecodedRevision.ReadSplit(reader.MetaBootstrapStore, saved.RevisionAddress,
                configuration, decoded.BindingEnvironment);
            Assert.Same(decoded.BindingEnvironment, positiveReuse.BindingEnvironment);
            Assert.Equal(saved.RootId, positiveReuse.GetRequired(saved.RootId).Id);
            if (cached) { AssertPhysicalProductionEqual(beforeWrongEnvironment, reader.MetaBootstrapStore.ReadCacheStatistics); }
            int alternateAllocations = 0;
            StateModelRegistry alternate = AlternateModels(() => alternateAllocations++);
            StateModelSnapshot positiveCode = alternate.FreezeConfiguration().CreateSnapshot();
            Assert.True(positiveCode.TryGetCurrentModel(typeof(AlternateWidget), out StateModelBinding? validAlternate));
            Assert.Equal(TypeTag.Int64, validAlternate!.CurrentSchema.Fields[0].TypeTag);
            Assert.Throws<InvalidDataException>(() => DecodedRevision.ReadSplit(reader.MetaBootstrapStore, saved.RevisionAddress, alternate));
            Assert.Equal(0, alternateAllocations); // The alternate complete same-key layout is rejected before materialization.
            AssertRing(reader.ReadState(saved), 201); // Neither ordinary typed failure clears or poisons correct physical values.
            Assert.False(reader.IsFaulted);
            Assert.Equal(0, reader.MetaBootstrapStore.AppendedFrames);
        }
        AssertFilesEqual(files, SnapshotFiles());
        using Repository reopened = Repository.OpenMetaBootstrapExisting(_path, Models(), readCacheBudgetBytes: budget);
        reopened.PreparedStateReuseEnabled = false;
        Assert.Null(reopened.PreparedStateEntry);
        using BranchCheckout cold = reopened.Checkout("main");
        DB087Widget coldRoot = AssertRing(cold.State, 201);
        Assert.Equal(savedAddress, cold.StateRevisionAddress);
        Assert.Equal(listId, Assert.Single(cold.Baseline!.Objects, pair => pair.Value.Current.Kind == ObjectStateKind.List).Key);
        Assert.Equal(storage, cold.Baseline.Objects[listId].Storage);
        P0GraphSnapshot oldSaved = reopened.MetaBootstrapStore.ReadGraph(reopened.ResolveTag("state1").RevisionAddress);
        Assert.NotSame(heldBatch, reopened.MetaBootstrapStore.ReadBatch(oldSaved.OwnedBatch!.Value));
        coldRoot.Numbers[8] = 202;
        CheckpointAddress continued = cold.CommitState();
        P0GraphSnapshot graph = reopened.MetaBootstrapStore.ReadGraph(continued.RevisionAddress);
        ObjectVersionRecord actualDelta = Assert.Single(reopened.MetaBootstrapStore.ReadBatch(graph.OwnedBatch!.Value).Records);
        Assert.Equal(ObjectVersionKind.Delta, actualDelta.Kind);
        Assert.Equal(listId.Value, actualDelta.ObjectId);
        Assert.Equal(storage.Head, actualDelta.PriorAddress);
        Assert.Equal(savedAddress, graph.SourceGraph);
        Assert.Equal(storage.ReconstructionPayloadBytes + ObjectVersionPayloadSize.GetDeltaPayloadBytes(actualDelta.Body.Length, storage.Head),
            cold.Baseline!.Objects[listId].Storage!.Value.ReconstructionPayloadBytes);
        AssertRing(reopened.ReadState(continued), 201, 202);
        AssertRing(reopened.ReadState(reopened.ResolveTag("state0")), 7);
        AssertRing(reopened.ReadState(reopened.ResolveTag("state1")), 201);
        Assert.Null(reopened.PreparedStateEntry);
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
    private static DB087Widget AssertRing(IDurableObject? value, int atSeven, int atEight = 8) {
        DB087Widget root = Assert.IsType<DB087Widget>(value);
        DB087Widget child = Assert.IsType<DB087Widget>(root.Child);
        Assert.Equal(1, root.Value);
        Assert.Equal(2, child.Value);
        Assert.Same(root, child.Child);
        Assert.Same(root.Numbers, child.Numbers);
        Assert.Equal(128, root.Numbers.Count);
        for (int i = 0; i < 128; i++) { Assert.Equal(i == 7 ? atSeven : i == 8 ? atEight : i, root.Numbers[i]); }
        return root;
    }

    // A valid independent application's complete same-key current shape: Value is Int64,
    // not this repository's Int32. Its own code closes successfully without this stored S.
    private static StateModelRegistry AlternateModels(Action allocate) {
        TypeExpr list = TypeExpr.List(TypeExpr.Builtin(TypeTag.Int32));
        DurableSchema schema = new("P0Widget", 1, new DurableFieldInfo(1, TypeTag.Int64),
            DurableFieldInfo.Reference(2, list), DurableFieldInfo.Reference(3, TypeExpr.Named("P0Widget")));
        AlternateState Read(ref BareValueReader reader) => new(reader.ReadVarInt64(), new(reader.ReadVarUInt32()), new(reader.ReadVarUInt32()));
        void Visit(in AlternateState state, IStateReferenceVisitor visitor) {
            visitor.VisitObject(state.Numbers, list);
            visitor.VisitDurable(state.Child, schema.Type);
        }
        PreparedBaseBody Base(in AlternateState state) {
            ArrayBufferWriter<byte> bytes = new();
            BareValueWriter writer = new(bytes);
            writer.WriteVarInt64(state.Value);
            writer.WriteVarUInt32(state.Numbers.Value);
            writer.WriteVarUInt32(state.Child.Value);
            return new(bytes.WrittenSpan);
        }
        StateReaderBinding reader = new StateReaderBinding<AlternateState>(schema, Read,
            (ref BareValueReader input, in AlternateState _) => Read(ref input), Visit);
        StateModelRegistry models = new();
        models.Register(new StateModelBinding<AlternateWidget, AlternateState>(new(schema, Base,
            (in AlternateState prior, in AlternateState next) => new(prior != next, Base(in next).Body)), [reader],
            static row => row.GetState<AlternateState>(), () => { allocate(); return new(); },
            static (AlternateWidget root, in AlternateState state, ObjectReadTable objects) => {
                root.Value = state.Value;
                root.Numbers = objects.ResolveObject<List<int>>(state.Numbers);
                root.Child = objects.ResolveDurable<AlternateWidget>(state.Child);
            }, (root, context) => new(root.Value, context.CaptureObject(root.Numbers, list), context.CaptureDurable(root.Child, schema.Type)), Visit));
        return models;
    }
    private sealed class AlternateWidget : IDurableObject {
        internal long Value;
        internal List<int>? Numbers;
        internal AlternateWidget? Child;
    }
    private readonly record struct AlternateState(long Value, ObjectId Numbers, ObjectId Child);
    private static void AssertPhysicalProductionEqual(P0GraphReadCacheStatistics expected, P0GraphReadCacheStatistics actual) {
        Assert.Equal(expected.GraphDecodes, actual.GraphDecodes);
        Assert.Equal(expected.BatchDecodes, actual.BatchDecodes);
        Assert.Equal(expected.DirectoryVersionDecodes, actual.DirectoryVersionDecodes);
        Assert.Equal(expected.DirectoryMaterializations, actual.DirectoryMaterializations);
        Assert.Equal(expected.DirectoryReplayFrames, actual.DirectoryReplayFrames);
    }
    private Dictionary<string, byte[]> SnapshotFiles() => Directory.GetFiles(_path, "*", SearchOption.AllDirectories)
        .ToDictionary(name => Path.GetRelativePath(_path, name), File.ReadAllBytes);
    private static void AssertFilesEqual(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual) {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach ((string name, byte[] bytes) in expected) { Assert.Equal(bytes, actual[name]); }
    }
    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
