using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;
using System.Buffers;

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
        long budget = cached ? GraphRecordStore.DefaultReadCacheBudgetBytes : 0;
        ObjectId rootId, listId;
        ObjectStorageInfo storage;
        FrameAddress savedAddress, schemaRoot;
        using (Repository seed = Repository.CreateStorageNew(_path, Models(), readCacheBudgetBytes: budget)) {
            seed.PreparedStateReuseEnabled = false;
            DB087Widget root = Ring();
            using WorkingCopy main = seed.OpenWorkingCopy(seed.CreateRootBranchFromState("main", root).Id);
            root = Assert.IsType<DB087Widget>(main.State);
            rootId = main.StateId!.Value;
            listId = Assert.Single(main.Baseline!.Objects, pair => pair.Value.Current.Kind == ObjectStateKind.List).Key;
            NormalizedGraphState initialBaseline = main.Baseline!;
            ObjectStorageInfo initialList = initialBaseline.Objects[listId].Storage!.Value;
            ModelExecutionEnvironment executionModels = main.Models;
            DecodedRevision.SchemaViewBindingEnvironment? executionEnvironment = main.BindingEnvironment;
            seed.CreateTag("state0", main.BaseCommitId);
            root.Numbers[7] = 200;
            seed.CreateTag("event", seed.CommitBranches([main.CreateEventCommitRequest(root)], [Extra])[main.BranchId].Id);
            Assert.Same(initialBaseline, main.Baseline);
            Assert.Same(executionModels, main.Models);
            Assert.Same(executionEnvironment, main.BindingEnvironment);
            Assert.Same(root, AssertRing(main.State, 200));
            root.Numbers[7] = 201;
            CommitId s1 = main.CommitState().Id;
            seed.CreateTag("state1", s1);
            Assert.Equal(rootId, main.StateId);
            Assert.Equal(listId, Assert.Single(main.Baseline!.Objects, pair => pair.Value.Current.Kind == ObjectStateKind.List).Key);
            Assert.Same(executionModels, main.Models);
            Assert.Same(executionEnvironment, main.BindingEnvironment);
            storage = main.Baseline.Objects[listId].Storage!.Value;
            StoredGraphRevision stateGraph = Snapshot(seed, s1);
            Assert.Equal(initialBaseline.RevisionAddress, stateGraph.SourceGraph);
            Assert.NotEqual(initialBaseline.SelectedSchemaView!.Snapshot!.Address, stateGraph.SchemaRoot);
            Assert.Equal(stateGraph.SchemaRoot, main.Baseline.SelectedSchemaView!.Snapshot!.Address);
            Assert.Same(executionModels, main.Baseline.SourceModels);
            ObjectVersionRecord delta = Assert.Single(seed.GraphStore.ReadBatch(stateGraph.OwnedBatch!.Value).Records);
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
            Assert.Equal(listId.Value, delta.ObjectId);
            Assert.Equal(initialList.Head, delta.PriorAddress);
            Assert.Equal(initialList.ReconstructionPayloadBytes + ObjectVersionPayloadSize.GetDeltaPayloadBytes(delta.Body.Length, initialList.Head),
                storage.ReconstructionPayloadBytes);
            long frames = seed.GraphStore.AppendedFrames;
            CommitId unchanged = main.CommitState().Id;
            savedAddress = Describe(seed, unchanged).Address;
            schemaRoot = seed.SharedSchemaRoot!.Value;
            StoredGraphRevision noChange = Snapshot(seed, unchanged);
            Assert.Null(noChange.OwnedBatch);
            Assert.Equal(stateGraph.DirectoryRoot, noChange.DirectoryRoot);
            Assert.Equal(frames + 1, seed.GraphStore.AppendedFrames);
            Assert.Equal(storage, main.Baseline.Objects[listId].Storage);
            Assert.Null(seed.PreparedStateEntry); // Hot save is not preparation-slot admission.

            // A new read uses the saved S, while this checkout keeps its original execution environment.
            DB087Widget independent = AssertRing(seed.LoadState(unchanged), 201);
            Assert.Same(root, AssertRing(main.State, 201));
            Assert.NotSame(root, independent);
            Assert.NotSame(root.Child, independent.Child);
            Assert.NotSame(root.Numbers, independent.Numbers);
            independent.Numbers[7] = -1;
            independent.Child!.Value = -2;
            AssertRing(main.State, 201); // Independent mutable edits cannot change the active checkout or stored DTOs.
            NormalizedGraphState savedBaseline = main.Baseline!;
            main.Dispose();
            using WorkingCopy signedAgain = seed.OpenWorkingCopy(BranchNamed(seed, "main"));
            DB087Widget restored = AssertRing(signedAgain.State, 201);
            Assert.NotSame(root, restored);
            Assert.NotSame(root.Child, restored.Child);
            Assert.NotSame(root.Numbers, restored.Numbers);
            Assert.NotSame(independent, restored);
            Assert.NotSame(independent.Numbers, restored.Numbers);
            Assert.Equal(savedAddress, signedAgain.StateRevisionAddress);
            Assert.Equal(rootId, signedAgain.StateId);
            Assert.Equal(listId, Assert.Single(signedAgain.Baseline!.Objects, pair => pair.Value.Current.Kind == ObjectStateKind.List).Key);
            Assert.Equal(schemaRoot, signedAgain.BindingEnvironment!.SelectedSchemaView.Snapshot!.Address);
            foreach ((ObjectId id, NormalizedObject row) in savedBaseline.Objects) {
                Assert.Equal(row.Storage, signedAgain.Baseline.Objects[id].Storage);
            }
            Assert.Equal(frames + 1, seed.GraphStore.AppendedFrames); // Reading and reacquiring the branch do not append.
            Assert.Null(seed.PreparedStateEntry);
        }
        Dictionary<string, byte[]> files = SnapshotFiles();
        // All previous owners and caches are closed. Meta loads before any business registration.
        using (Repository metaOnly = Repository.OpenStorageReadOnlyExisting(_path, new(), readCacheBudgetBytes: budget)) {
            Assert.Equal(schemaRoot, metaOnly.SharedSchemaRoot);
            SchemaCatalogView catalog = SchemaCatalogView.Load(metaOnly.GraphStore, schemaRoot);
            Assert.Equal(Extra, catalog.Schemas[new(Extra.Type, Extra.Version)]);
        }
        ObjectVersionBatch? heldBatch = null;
        using (Repository reader = Repository.OpenStorageReadOnlyExisting(_path, Models(), readCacheBudgetBytes: budget)) {
            reader.PreparedStateReuseEnabled = false;
            CommitId current = reader.ReadCurrentCommit(BranchNamed(reader, "main")).Commit.Id, old = reader.ResolveTag("state0"), saved = reader.ResolveTag("state1");
            Assert.Equal(savedAddress, Describe(reader, current).Address);
            DB087Widget first = AssertRing(reader.LoadState(saved), 201);
            GraphPhysicalReadStatistics before = reader.GraphStore.ReadCacheStatistics;
            DB087Widget second = AssertRing(reader.LoadState(saved), 201);
            Assert.NotSame(first, second);
            Assert.NotSame(first.Numbers, second.Numbers); // Physical memoization never materializes an editable or typed graph cache.
            GraphPhysicalReadStatistics after = reader.GraphStore.ReadCacheStatistics;
            if (cached) { AssertPhysicalProductionEqual(before, after); Assert.True(after.BatchHits > before.BatchHits); }
            else {
                Assert.True(after.BatchDecodes > before.BatchDecodes);
                Assert.True(after.DirectoryMaterializations > before.DirectoryMaterializations);
                Assert.True(after.DirectoryReplayFrames > before.DirectoryReplayFrames);
                Assert.Equal(0, after.ResidentChargeBytes);
                Assert.Equal(0, after.EntryCount);
            }
            AssertRing(reader.LoadState(old), 7);
            AssertRing(reader.LoadEvent(reader.ResolveTag("event")), 200);
            AssertRing(reader.LoadState(saved), 201);
            Assert.Null(reader.PreparedStateEntry);
            StoredGraphRevision savedGraph = Snapshot(reader, saved);
            heldBatch = reader.GraphStore.ReadBatch(savedGraph.OwnedBatch!.Value);
            Assert.Equal(storage.ReconstructionPayloadBytes,
                reader.GraphStore.ReadObjectChain(Snapshot(reader, saved), listId.Value).ReconstructionPayloadBytes);
            // A low-level typed helper may read old layouts through an additive newer
            // catalog. Saving authority instead requires the reader-issued exact-S environment.
            StateModelRegistry.FrozenConfiguration configuration = Models().FreezeConfiguration();
            DecodedRevision decoded = DecodedRevision.ReadStoredGraph(reader.GraphStore, Snapshot(reader, saved), configuration);
            Assert.NotEqual(schemaRoot, reader.GraphStore.ReadMetadata(Describe(reader, old).Address).SchemaRoot);
            DecodedGraphRows compatibleLayout = StoredGraphDecoder.Read(reader.GraphStore, Snapshot(reader, old),
                decoded.BindingEnvironment!.Models, decoded.BindingEnvironment.SelectedSchemaView);
            Assert.Equal(3, compatibleLayout.Rows.Count);
            Assert.Equal(reader.GraphStore.ReadMetadata(Describe(reader, old).Address).SchemaRoot, compatibleLayout.Snapshot.SchemaRoot);
            GraphPhysicalReadStatistics beforeWrongEnvironment = reader.GraphStore.ReadCacheStatistics;
            Assert.Throws<InvalidDataException>(() => DecodedRevision.ReadStoredGraph(reader.GraphStore, Snapshot(reader, old),
                configuration, decoded.BindingEnvironment));
            DecodedRevision positiveReuse = DecodedRevision.ReadStoredGraph(reader.GraphStore, Snapshot(reader, saved),
                configuration, decoded.BindingEnvironment);
            Assert.Same(decoded.BindingEnvironment, positiveReuse.BindingEnvironment);
            Assert.Equal(Describe(reader, saved).RootId, positiveReuse.GetRequired(Describe(reader, saved).RootId).Id);
            if (cached) { AssertPhysicalProductionEqual(beforeWrongEnvironment, reader.GraphStore.ReadCacheStatistics); }
            int alternateAllocations = 0;
            StateModelRegistry alternate = AlternateModels(() => alternateAllocations++);
            ModelExecutionEnvironment positiveCode = alternate.FreezeConfiguration().CreateEnvironment();
            Assert.True(positiveCode.TryGetCurrentModel(typeof(AlternateWidget), out StateModelBinding? validAlternate));
            Assert.Equal(TypeTag.Int64, validAlternate!.CurrentSchema.Fields[0].TypeTag);
            Assert.Throws<InvalidDataException>(() => DecodedRevision.ReadStoredGraph(reader.GraphStore, Snapshot(reader, saved), alternate.FreezeConfiguration()));
            Assert.Equal(0, alternateAllocations); // The alternate complete same-key layout is rejected before materialization.
            AssertRing(reader.LoadState(saved), 201); // Neither ordinary typed failure clears or poisons correct physical values.
            Assert.False(reader.IsFaulted);
            Assert.Equal(0, reader.GraphStore.AppendedFrames);
        }
        AssertFilesEqual(files, SnapshotFiles());
        using Repository reopened = Repository.OpenStorageExisting(_path, Models(), readCacheBudgetBytes: budget);
        reopened.PreparedStateReuseEnabled = false;
        Assert.Null(reopened.PreparedStateEntry);
        using WorkingCopy cold = reopened.OpenWorkingCopy(BranchNamed(reopened, "main"));
        DB087Widget coldRoot = AssertRing(cold.State, 201);
        Assert.Equal(savedAddress, cold.StateRevisionAddress);
        Assert.Equal(rootId, cold.StateId);
        Assert.Equal(listId, Assert.Single(cold.Baseline!.Objects, pair => pair.Value.Current.Kind == ObjectStateKind.List).Key);
        Assert.Equal(storage, cold.Baseline.Objects[listId].Storage);
        StoredGraphRevision oldSaved = Snapshot(reopened, reopened.ResolveTag("state1"));
        Assert.NotSame(heldBatch, reopened.GraphStore.ReadBatch(oldSaved.OwnedBatch!.Value));
        coldRoot.Numbers[8] = 202;
        CommitId continued = cold.CommitState().Id;
        StoredGraphRevision graph = Snapshot(reopened, continued);
        ObjectVersionRecord actualDelta = Assert.Single(reopened.GraphStore.ReadBatch(graph.OwnedBatch!.Value).Records);
        Assert.Equal(ObjectVersionKind.Delta, actualDelta.Kind);
        Assert.Equal(listId.Value, actualDelta.ObjectId);
        Assert.Equal(storage.Head, actualDelta.PriorAddress);
        Assert.Equal(savedAddress, graph.SourceGraph);
        Assert.Equal(storage.ReconstructionPayloadBytes + ObjectVersionPayloadSize.GetDeltaPayloadBytes(actualDelta.Body.Length, storage.Head),
            cold.Baseline!.Objects[listId].Storage!.Value.ReconstructionPayloadBytes);
        AssertRing(reopened.LoadState(continued), 201, 202);
        AssertRing(reopened.LoadState(reopened.ResolveTag("state0")), 7);
        AssertRing(reopened.LoadState(reopened.ResolveTag("state1")), 201);
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
    private static void AssertPhysicalProductionEqual(GraphPhysicalReadStatistics expected, GraphPhysicalReadStatistics actual) {
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
