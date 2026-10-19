using Atelia.DurableGraph.Persistence.Tests;
using Atelia.DurableGraph.Persistence;

using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void MetaBootstrapBuiltinNullableAndNestedListsColdReadWithoutListDescriptorsAndContinueDelta() {
        GeneratorTestRun run = RunGenerator(BootstrapBuiltinListSource, [], null, "true");
        AssertSchemaOnlyCompiles(run);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("BootstrapBuiltinLists.Host")!;
        var models = host.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>();
        var create = host.GetMethod("Create")!.CreateDelegate<Func<IDurableObject>>();
        var objects = host.GetMethod("Objects")!.CreateDelegate<Func<IDurableObject, object[]>>();
        var change = host.GetMethod("Change")!.CreateDelegate<Action<IDurableObject>>();
        var check = host.GetMethod("Check")!.CreateDelegate<Action<IDurableObject, int>>();
        ObjectLayout[] expected = ExpectedBootstrapBuiltinListLayouts();

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
                Assert.Equal(expected[i], binding!.CurrentLayout);
            }
            BranchSaveSessionFixture session = new(repository, "main", registered);
            using PreparedFixtureSave save = session.Prepare(original); // No manual Schema or container Set.
            AssertBootstrapBuiltinListCatalog(save.Catalog.View, expected);
            Assert.Equal(4, save.Business.Write.Records.Count);
            Assert.All(save.Business.Write.Records, row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
            RepositorySaveResult result = save.Commit();
            s0 = result.Business.Address;
            schemaRoot = result.Meta!.Address;
            rootId = result.Business.RootId.Value;
            Assert.Equal(4, result.Business.ObjectHeads.Count); // Root and three Lists; shared inner is one object.
            Assert.Equal(0, session.Catalog.DirtyCount);
        }

        using (RepositoryStorageFacade repository = RepositoryStorageFacade.OpenReadOnlyExisting(files.Path)) {
            // Fresh resources, complete meta read before supplying any business model registry.
            SchemaCatalogView meta = SchemaCatalogView.Load(repository.Store, schemaRoot);
            AssertBootstrapBuiltinListCatalog(meta, expected);
            Assert.Equal(0, repository.Store.AppendedFrames);
            StateModelRegistry registered = models();
            ModelExecutionEnvironment snapshot = registered.CreateEnvironment(schemaLookup: key => meta.Schemas.GetValueOrDefault(key));
            DecodedGraphRows decoded = StoredGraphDecoder.Read(repository.Store, MechanismFixture.Snapshot(repository, s0), snapshot, meta);
            Assert.Equal(4, decoded.Rows.Count);
            ids = expected.Select(layout => Assert.Single(decoded.Rows.Values, row => row.Layout.Equals(layout)).Id.Value).ToArray();
            Assert.Equal(rootId, ids[0]);
            StoredGraphRevision graph = MechanismFixture.Snapshot(repository, s0);
            for (int i = 1; i < expected.Length; i++) {
                AssertBootstrapBuiltinListBaseWithoutCatalog(repository, graph, ids[i], expected[i]);
            }
            IDurableObject read = BusinessPersistenceAdapter.ReadRoot(repository.Store, MechanismFixture.Snapshot(repository, s0), registered);
            Assert.NotSame(original, read);
            check(read, 0); // Exact List types, wide Int64 values, Nullable markers and shared inner references.
            Assert.Equal(s0, MechanismFixture.Current(repository, "main").Address);
            Assert.Equal(schemaRoot, repository.SharedSchemaRoot);
            Assert.Equal(0, repository.Store.AppendedFrames);
        }

        using (RepositoryStorageFacade repository = RepositoryStorageFacade.OpenExisting(files.Path)) {
            StateModelRegistry registered = models();
            BranchSaveSessionFixture session = new(repository, "main", registered);
            IDurableObject restored = session.State!;
            check(restored, 0);
            object[] restoredObjects = objects(restored);
            StoredGraphRevision prior = session.StateSource!;
            change(restored);
            check(restored, 1);
            using (PreparedFixtureSave save = session.Prepare(restored, parameters: new(1_000_000, 1))) {
                Assert.Null(save.Catalog.Write);
                Assert.Empty(save.Catalog.EncodedIds);
                Assert.Equal(0, save.Catalog.AddedDescriptors);
                Assert.Equal(s0, save.Business.Write.SourceGraph);
                Assert.Empty(save.Business.Write.RemovedIds);
                Assert.Equal(3, save.Business.Write.Records.Count);
                Assert.Equal<uint>(ids.Skip(1).Order(), save.Business.Write.Records.Select(row => row.ObjectId));
                Assert.DoesNotContain(save.Business.Write.Records, row => row.ObjectId == rootId);
                foreach (ObjectVersionRecord delta in save.Business.Write.Records) {
                    Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
                    Assert.Equal(prior.ObjectHeads[delta.ObjectId], delta.PriorAddress);
                }
                RepositorySaveResult result = save.Commit();
                s1 = result.Business.Address;
                Assert.Equal(schemaRoot, result.Meta!.Address);
                Assert.Equal(schemaRoot, result.Business.SchemaRoot);
                Assert.Equal(schemaRoot, repository.SharedSchemaRoot);
                Assert.Equal(prior.ObjectHeads[rootId], result.Business.ObjectHeads[rootId]); // Owner's ID slots did not change.
                Assert.Equal(s0, result.Business.SourceGraph);
                Assert.Equal(s1, session.StateSource!.Address);
                object[] after = objects(session.State!);
                for (int i = 0; i < after.Length; i++) { Assert.Same(restoredObjects[i], after[i]); }
                foreach (uint id in ids.Skip(1)) {
                    Assert.NotEqual(prior.ObjectHeads[id], result.Business.ObjectHeads[id]);
                    ObjectVersionChain chain = repository.Store.ReadObjectChain(MechanismFixture.Snapshot(repository, s1), id);
                    Assert.Equal(2, chain.Entries.Count);
                    Assert.Equal(ObjectVersionKind.Delta, chain.Entries[^1].Record.Kind);
                }
            }

            StoredGraphRevision accepted = session.StateSource!;
            long frames = repository.Store.AppendedFrames;
            using (PreparedFixtureSave unchanged = session.Prepare(restored)) {
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
                Assert.Equal(schemaRoot, result.Business.SchemaRoot);
                foreach (uint id in ids) { Assert.Equal(accepted.ObjectHeads[id], result.Business.ObjectHeads[id]); }
            }
            Assert.Equal(frames + 1, repository.Store.AppendedFrames);
            check(BusinessPersistenceAdapter.ReadRoot(repository.Store, MechanismFixture.Snapshot(repository, s0), registered), 0);
            check(BusinessPersistenceAdapter.ReadRoot(repository.Store, MechanismFixture.Snapshot(repository, s1), registered), 1);
        }

        using (RepositoryStorageFacade repository = RepositoryStorageFacade.OpenReadOnlyExisting(files.Path)) {
            SchemaCatalogView meta = SchemaCatalogView.Load(repository.Store, schemaRoot);
            AssertBootstrapBuiltinListCatalog(meta, expected);
            Assert.Equal(s2, MechanismFixture.State(repository, "main")?.Address);
            Assert.Equal(schemaRoot, MechanismFixture.Snapshot(repository, s0).SchemaRoot);
            Assert.Equal(schemaRoot, MechanismFixture.Snapshot(repository, s1).SchemaRoot);
            StateModelRegistry registered = models();
            check(BusinessPersistenceAdapter.ReadRoot(repository.Store, MechanismFixture.Snapshot(repository, s0), registered), 0);
            check(BusinessPersistenceAdapter.ReadRoot(repository.Store, MechanismFixture.Snapshot(repository, s1), registered), 1);
            check(BusinessPersistenceAdapter.ReadRoot(repository.Store, MechanismFixture.Snapshot(repository, s2), registered), 1);
            Assert.Equal(0, repository.Store.AppendedFrames);
        }
    }

    private static void AssertBootstrapBuiltinListCatalog(SchemaCatalogView meta, ObjectLayout[] expected) {
        DurableSchema root = expected[0].Schema!;
        Assert.Equal(root, Assert.Single(meta.Schemas).Value);
        Assert.Equal(meta.DescriptorId(expected[0]), Assert.Single(meta.DescriptorIds));
        foreach (ObjectLayout list in expected.Skip(1)) {
            Assert.Throws<InvalidDataException>(() => meta.DescriptorId(list));
        }
    }

    private static void AssertBootstrapBuiltinListBaseWithoutCatalog(RepositoryStorageFacade repository, StoredGraphRevision graph,
        uint id, ObjectLayout expected) {
        ObjectVersionChain chain = repository.Store.ReadObjectChain(graph, id);
        ObjectVersionChainEntry first = Assert.Single(chain.Entries);
        Assert.Equal((byte)5, first.Record.Body[0]); // ABI5 retains structural Lists; this is a fresh current-format witness.
        var decoded = TypedBaseBodyCodec.Decode(first.Record.Body, catalog: null);
        Assert.Equal(expected, decoded.Layout); // Resolve the structural layout without hardcoding its route or construction bytes.
        ModelExecutionEnvironment empty = new StateModelRegistry().CreateEnvironment();
        ObjectReaderBinding reader = empty.ResolveObjectReader(decoded.Layout);
        ObjectStateRecord row = reader.Read(new(id), new StateModelBodySource(decoded.Body));
        Assert.Equal(new ObjectId(id), row.Id);
        Assert.Equal(expected, row.Layout); // Real Base body decoding, with no user definition or SchemaStore.
    }

    private static ObjectLayout[] ExpectedBootstrapBuiltinListLayouts() {
        TypeExpr intType = TypeExpr.Builtin(TypeTag.Int32);
        ListLayout inner = new(new(1, TypeTag.Int32));
        ListLayout optional = new(DurableFieldInfo.Nullable(1, new(1, TypeTag.Int64)));
        ListLayout nested = new(DurableFieldInfo.Reference(1, TypeExpr.List(intType)));
        DurableSchema root = new("p1.builtin.lists", 1, [
            DurableFieldInfo.Reference(1, optional.Type), DurableFieldInfo.Reference(2, nested.Type),
        ]);
        return [ObjectLayout.ForDurable(root), ObjectLayout.ForList(optional), ObjectLayout.ForList(nested), ObjectLayout.ForList(inner)];
    }

    private const string BootstrapBuiltinListSource = """
        using System;
        using System.Collections.Generic;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Persistence;
        namespace BootstrapBuiltinLists;
        [DurableType("p1.builtin.lists", 1)] public sealed partial class Root : IDurableObject {
            [DurableField(1)] public List<long?> Optional = new();
            [DurableField(2)] public List<List<int>> Nested = new();
        }
        public static class Host {
            public static StateModelRegistry Models() {
                StateModelRegistry models = new();
                Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
                return models;
            }
            public static IDurableObject Create() {
                Root root = new();
                for (int i = 0; i < 256; i++) { root.Optional.Add(i % 5 == 0 ? null : (long)int.MaxValue + 1000 + i); }
                List<int> inner = new();
                for (int i = 0; i < 128; i++) { inner.Add(1000 + i); }
                for (int i = 0; i < 64; i++) { root.Nested.Add(inner); }
                return root;
            }
            public static object[] Objects(IDurableObject value) {
                Root root = (Root)value;
                return [root, root.Optional, root.Nested, root.Nested[0]];
            }
            public static void Change(IDurableObject value) {
                Root root = (Root)value;
                root.Optional[30] = long.MaxValue - 23; // Absent to present, beyond the Int32 range.
                root.Optional[31] = null;
                List<int> inner = root.Nested[0];
                inner[17] = 7777;
                root.Nested.Add(inner); // Outer resize reuses the same inner object identity.
            }
            public static void Check(IDurableObject value, int stage) {
                Root root = (Root)value;
                if (root.Optional.Count != 256 || root.Nested.Count != (stage == 0 ? 64 : 65)) {
                    throw new InvalidOperationException("List count mismatch.");
                }
                for (int i = 0; i < 256; i++) {
                    long? expected = stage == 1 && i == 30 ? long.MaxValue - 23
                        : i % 5 == 0 || stage == 1 && i == 31 ? null : (long)int.MaxValue + 1000 + i;
                    if (root.Optional[i] != expected) throw new InvalidOperationException("Nullable Int64 value mismatch.");
                }
                List<int> inner = root.Nested[0];
                if (inner.Count != 128) throw new InvalidOperationException("Inner count mismatch.");
                foreach (List<int> candidate in root.Nested) {
                    if (!ReferenceEquals(candidate, inner)) throw new InvalidOperationException("Shared inner List identity mismatch.");
                }
                for (int i = 0; i < 128; i++) {
                    if (inner[i] != (stage == 1 && i == 17 ? 7777 : 1000 + i)) {
                        throw new InvalidOperationException("Inner Int32 value mismatch.");
                    }
                }
            }
        }
        """;
}
