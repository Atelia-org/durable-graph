using Atelia.DurableGraph.Persistence;

using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void MetaBootstrapBuiltinVectorAndInlineRectangularArraysColdReadAndContinueSparseDelta() {
        GeneratorTestRun run = RunGenerator(BootstrapArraySource, [], null, "true");
        AssertSchemaOnlyCompiles(run);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("BootstrapArrays.Host")!;
        var models = host.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>();
        var create = host.GetMethod("Create")!.CreateDelegate<Func<IDurableObject>>();
        var objects = host.GetMethod("Objects")!.CreateDelegate<Func<IDurableObject, object[]>>();
        var change = host.GetMethod("Change")!.CreateDelegate<Action<IDurableObject>>();
        var check = host.GetMethod("Check")!.CreateDelegate<Action<IDurableObject, int>>();
        var expected = ExpectedBootstrapArrayLayouts();
        ObjectLayout[] layouts = [ObjectLayout.ForDurable(expected.Root), ObjectLayout.ForArray(expected.Vector), ObjectLayout.ForArray(expected.Points)];

        using RawBaseDirectory files = new();
        FrameAddress s0, s1, s2, schemaRoot;
        uint rootId;
        uint[] ids;
        IDurableObject original = create();
        using (RepositoryStorageFacade repository = RepositoryStorageFacade.CreateNew(files.Path)) {
            StateModelRegistry registered = models();
            ModelExecutionEnvironment snapshot = registered.CreateEnvironment();
            object[] initial = objects(original);
            for (int i = 0; i < initial.Length; i++) {
                Assert.True(snapshot.TryGetCurrentObjectBinding(initial[i].GetType(), out ObjectBinding? binding));
                Assert.Equal(layouts[i], binding!.CurrentLayout);
            }
            BranchSaveSession session = new(repository, "main", registered);
            using PreparedBranchSave save = session.Prepare(original); // No hand-registered inline or array dependency.
            AssertBootstrapArrayCatalog(save.Catalog.View, expected.Root, expected.Point, expected.Vector, expected.Points);
            Assert.Equal(3, save.Business.Write.Records.Count);
            Assert.All(save.Business.Write.Records, row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
            RepositorySaveResult result = save.Commit();
            s0 = result.Business.Address;
            schemaRoot = result.Meta!.Address;
            rootId = result.Business.RootId.Value;
            Assert.Equal(3, result.Business.ObjectHeads.Count);
            Assert.Equal(0, session.Catalog.DirtyCount);
        }

        using (RepositoryStorageFacade repository = RepositoryStorageFacade.OpenReadOnlyExisting(files.Path)) {
            // Every old resource has closed. Exact metadata is read without business model registration.
            SchemaCatalogView meta = SchemaCatalogView.Load(repository.Store, schemaRoot);
            AssertBootstrapArrayCatalog(meta, expected.Root, expected.Point, expected.Vector, expected.Points);
            Assert.Equal(0, repository.Store.AppendedFrames);
            StateModelRegistry registered = models();
            ModelExecutionEnvironment snapshot = registered.CreateEnvironment(schemaLookup: key => meta.Schemas.GetValueOrDefault(key));
            DecodedGraphRows decoded = StoredGraphDecoder.Read(repository.Store, s0, snapshot, meta);
            Assert.Equal(3, decoded.Rows.Count);
            ids = layouts.Select(layout => Assert.Single(decoded.Rows.Values, row => row.Layout.Equals(layout)).Id.Value).ToArray();
            Assert.Equal(rootId, ids[0]);
            StoredGraphRevision graph = repository.Store.ReadGraph(s0);
            AssertBootstrapArrayVectorWithoutCatalog(repository, graph, ids[1], expected.Vector);
            AssertBootstrapClosureDynamicDescriptor(repository, graph, rootId, layouts[0]);
            AssertBootstrapClosureDynamicDescriptor(repository, graph, ids[2], layouts[2]);
            ObjectVersionChainEntry userBase = Assert.Single(repository.Store.ReadObjectChain(s0, ids[2]).Entries);
            Assert.Throws<InvalidDataException>(() => TypedBaseBodyCodec.Decode(userBase.Record.Body, catalog: null));
            IDurableObject read = BusinessPersistenceAdapter.ReadRoot(repository.Store, s0, registered);
            Assert.NotSame(original, read);
            check(read, 0);
            Assert.Equal(s0, repository.GetHead("main"));
            Assert.Equal(schemaRoot, repository.SharedSchemaRoot);
            Assert.Equal(0, repository.Store.AppendedFrames);
        }

        using (RepositoryStorageFacade repository = RepositoryStorageFacade.OpenExisting(files.Path)) {
            StateModelRegistry registered = models();
            BranchSaveSession session = new(repository, "main", registered);
            IDurableObject restored = session.State!;
            check(restored, 0);
            object[] restoredObjects = objects(restored);
            StoredGraphRevision prior = session.StateSource!;
            change(restored);
            check(restored, 1);
            using (PreparedBranchSave save = session.Prepare(restored, parameters: new(1_000_000, 1))) {
                Assert.Null(save.Catalog.Write);
                Assert.Empty(save.Catalog.EncodedIds);
                Assert.Equal(0, save.Catalog.AddedDescriptors);
                Assert.Equal(s0, save.Business.Write.SourceGraph);
                Assert.Empty(save.Business.Write.RemovedIds);
                Assert.Equal(2, save.Business.Write.Records.Count);
                Assert.Equal<uint>(ids.Skip(1).Order(), save.Business.Write.Records.Select(row => row.ObjectId));
                Assert.DoesNotContain(save.Business.Write.Records, row => row.ObjectId == rootId);
                foreach (ObjectVersionRecord delta in save.Business.Write.Records) {
                    Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
                    Assert.Equal(prior.ObjectHeads[delta.ObjectId], delta.PriorAddress);
                    // Each Base needs at least 128 Nullable presence bytes; these are sparse patches.
                    Assert.True(delta.Body.Length < 128);
                }
                RepositorySaveResult result = save.Commit();
                s1 = result.Business.Address;
                Assert.Equal(schemaRoot, result.Meta!.Address);
                Assert.Equal(schemaRoot, result.Business.SchemaRoot);
                Assert.Equal(schemaRoot, repository.SharedSchemaRoot);
                Assert.Equal(prior.ObjectHeads[rootId], result.Business.ObjectHeads[rootId]);
                Assert.Equal(s0, result.Business.SourceGraph);
                Assert.Equal(s1, session.StateSource!.Address);
                object[] after = objects(session.State!);
                for (int i = 0; i < after.Length; i++) { Assert.Same(restoredObjects[i], after[i]); }
                foreach (uint id in ids.Skip(1)) {
                    Assert.NotEqual(prior.ObjectHeads[id], result.Business.ObjectHeads[id]);
                    ObjectVersionChain chain = repository.Store.ReadObjectChain(s1, id);
                    Assert.Equal(2, chain.Entries.Count);
                    Assert.Equal(ObjectVersionKind.Delta, chain.Entries[^1].Record.Kind);
                }
            }

            StoredGraphRevision accepted = session.StateSource!;
            long frames = repository.Store.AppendedFrames;
            using (PreparedBranchSave unchanged = session.Prepare(restored)) {
                Assert.Null(unchanged.Catalog.Write);
                Assert.Empty(unchanged.Catalog.EncodedIds);
                Assert.Empty(unchanged.Business.Write.Records);
                Assert.Empty(unchanged.Business.Write.RemovedIds);
                Assert.Equal(s1, unchanged.Business.Write.SourceGraph);
                RepositorySaveResult result = unchanged.Commit();
                s2 = result.Business.Address;
                Assert.Null(result.Business.OwnedBatch);
                Assert.Equal(accepted.DirectoryRoot, result.Business.DirectoryRoot);
                Assert.Equal(s1, result.Business.SourceGraph);
                foreach (uint id in ids) { Assert.Equal(accepted.ObjectHeads[id], result.Business.ObjectHeads[id]); }
            }
            Assert.Equal(frames + 1, repository.Store.AppendedFrames);
            check(BusinessPersistenceAdapter.ReadRoot(repository.Store, s0, registered), 0);
            check(BusinessPersistenceAdapter.ReadRoot(repository.Store, s1, registered), 1);
        }

        using (RepositoryStorageFacade repository = RepositoryStorageFacade.OpenReadOnlyExisting(files.Path)) {
            SchemaCatalogView meta = SchemaCatalogView.Load(repository.Store, schemaRoot);
            AssertBootstrapArrayCatalog(meta, expected.Root, expected.Point, expected.Vector, expected.Points);
            Assert.Equal(s2, repository.GetStateHead("main"));
            Assert.Equal(schemaRoot, repository.Store.ReadGraph(s0).SchemaRoot);
            Assert.Equal(schemaRoot, repository.Store.ReadGraph(s1).SchemaRoot);
            StateModelRegistry registered = models();
            check(BusinessPersistenceAdapter.ReadRoot(repository.Store, s0, registered), 0);
            check(BusinessPersistenceAdapter.ReadRoot(repository.Store, s1, registered), 1);
            check(BusinessPersistenceAdapter.ReadRoot(repository.Store, s2, registered), 1);
            Assert.Equal(0, repository.Store.AppendedFrames);
        }
    }

    private static void AssertBootstrapArrayCatalog(SchemaCatalogView meta, DurableSchema root, DurableSchema point,
        ArrayLayout vector, ArrayLayout points) {
        Assert.Equal(2, meta.Schemas.Count);
        Assert.Equal(root, meta.Schemas[new(root.Type, root.Version)]);
        Assert.Equal(point, meta.Schemas[new(point.Type, point.Version)]);
        Assert.Equal(3, meta.DescriptorIds.Count); // Root, inline Point and the exact user array; no builtin array descriptor.
        Assert.Contains(meta.DescriptorId(ObjectLayout.ForDurable(root)), meta.DescriptorIds);
        ObjectId descriptor = meta.DescriptorId(ObjectLayout.ForArray(points));
        Assert.Equal(ObjectLayout.ForArray(points), meta.ResolveLayout(descriptor));
        Assert.Throws<InvalidDataException>(() => meta.DescriptorId(ObjectLayout.ForArray(vector)));
        Assert.Throws<InvalidDataException>(() => meta.ResolveLayout(meta.SchemaNodeId(point)));
    }

    private static void AssertBootstrapArrayVectorWithoutCatalog(RepositoryStorageFacade repository, StoredGraphRevision graph,
        uint id, ArrayLayout expected) {
        ObjectVersionChainEntry first = Assert.Single(repository.Store.ReadObjectChain(graph.Address, id).Entries);
        Assert.Equal((byte)5, first.Record.Body[0]); // ABI5 retains Array codec 1; this is a fresh current-format witness.
        var decoded = TypedBaseBodyCodec.Decode(first.Record.Body, catalog: null);
        Assert.Equal(ObjectLayout.ForArray(expected), decoded.Layout);
        ModelExecutionEnvironment empty = new StateModelRegistry().CreateEnvironment();
        ObjectStateRecord row = empty.ResolveObjectReader(decoded.Layout).Read(new(id), new StateModelBodySource(decoded.Body));
        FrozenArrayState<NullableState<int>> state = row.GetArrayState<NullableState<int>>();
        Assert.Equal(new ArrayShape(128), state.Shape);
        for (int i = 0; i < 128; i++) {
            Assert.Equal(i % 5 != 0, state[i].HasValue);
            if (state[i].HasValue) { Assert.Equal(1000 + i, state[i].Value); }
        }
    }

    private static (DurableSchema Root, DurableSchema Point, ArrayLayout Vector, ArrayLayout Points) ExpectedBootstrapArrayLayouts() {
        DurableSchema point = new("p1.array.point", 1, SchemaKind.InlineValue,
            new DurableFieldInfo(1, TypeTag.Int32), DurableFieldInfo.Nullable(2, new(1, TypeTag.Int32)));
        ArrayLayout vector = new(TypeExprKind.VectorArray, DurableFieldInfo.Nullable(1, new(1, TypeTag.Int32)));
        ArrayLayout points = new(TypeExprKind.Rank2Array, DurableFieldInfo.Nullable(1, new(1, TypeTag.InlineValue, inlineSchema: point)));
        DurableSchema root = new("p1.array.root", 1, [
            DurableFieldInfo.Reference(1, vector.Type), DurableFieldInfo.Reference(2, points.Type),
        ]);
        return (root, point, vector, points);
    }

    private const string BootstrapArraySource = """
        using System;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Persistence;
        namespace BootstrapArrays;
        [DurableType("p1.array.point", 1)] public partial struct Point {
            [DurableField(1)] public int X;
            [DurableField(2)] public int? Rank;
        }
        [DurableType("p1.array.root", 1)] public sealed partial class Root : IDurableObject {
            [DurableField(1)] public int?[] Optional = new int?[128];
            [DurableField(2)] public Point?[,] Points = new Point?[16, 8];
        }
        public static class Host {
            public static StateModelRegistry Models() {
                StateModelRegistry models = new();
                Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
                return models;
            }
            public static IDurableObject Create() {
                Root root = new();
                for (int i = 0; i < 128; i++) { root.Optional[i] = i % 5 == 0 ? null : 1000 + i; }
                for (int row = 0; row < 16; row++) for (int column = 0; column < 8; column++) {
                    int index = row * 8 + column;
                    root.Points[row, column] = index % 9 == 0 ? null : new Point { X = 1000 + index, Rank = index % 4 == 0 ? null : index };
                }
                return root;
            }
            public static object[] Objects(IDurableObject value) {
                Root root = (Root)value;
                return [root, root.Optional, root.Points];
            }
            public static void Change(IDurableObject value) {
                Root root = (Root)value;
                root.Optional[20] = int.MinValue + 17;
                root.Optional[21] = null;
                root.Optional[63] = -12345;
                root.Points[0, 0] = new Point { X = 9000, Rank = 100 };
                root.Points[7, 3] = new Point { X = 7003, Rank = null };
                root.Points[7, 2] = null;
            }
            public static void Check(IDurableObject value, int stage) {
                Root root = (Root)value;
                if (!root.Optional.GetType().IsSZArray || root.Optional.Rank != 1 || root.Optional.Length != 128 || root.Optional.GetLowerBound(0) != 0 ||
                    root.Points.Rank != 2 || root.Points.GetLength(0) != 16 || root.Points.GetLength(1) != 8 ||
                    root.Points.GetLowerBound(0) != 0 || root.Points.GetLowerBound(1) != 0) {
                    throw new InvalidOperationException("SZ/rank-2 zero-based array shape mismatch.");
                }
                for (int i = 0; i < 128; i++) {
                    int? expected = stage == 1 && i == 20 ? int.MinValue + 17 : stage == 1 && i == 63 ? -12345
                        : i % 5 == 0 || stage == 1 && i == 21 ? null : 1000 + i;
                    if (root.Optional[i] != expected) throw new InvalidOperationException("Nullable vector value mismatch.");
                }
                for (int row = 0; row < 16; row++) for (int column = 0; column < 8; column++) {
                    int index = row * 8 + column;
                    Point? point = root.Points[row, column];
                    bool inserted = stage == 1 && index == 0;
                    if (index % 9 == 0 && !inserted || stage == 1 && index == 58) {
                        if (point is not null) throw new InvalidOperationException("Absent rectangular element mismatch.");
                    } else {
                        int x = inserted ? 9000 : stage == 1 && index == 59 ? 7003 : 1000 + index;
                        int? rank = inserted ? 100 : index % 4 == 0 || stage == 1 && index == 59 ? null : index;
                        if (point is not { } present || present.X != x || present.Rank != rank) {
                            throw new InvalidOperationException("Nullable inline rectangular value mismatch.");
                        }
                    }
                }
            }
        }
        """;
}
