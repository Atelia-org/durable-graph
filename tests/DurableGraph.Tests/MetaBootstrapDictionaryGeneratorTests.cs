using System.Buffers;
using System.Reflection;
using Atelia.Binary;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void MetaBootstrapBuiltinAndInlineDictionariesColdReadCanonicalOperationsAndContinueWithBothComparerKinds() {
        GeneratorTestRun run = RunGenerator(BootstrapDictionarySource, [], null, "true");
        AssertSchemaOnlyCompiles(run);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("BootstrapDictionaries.Host")!;
        var models = host.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>();
        var create = host.GetMethod("Create")!.CreateDelegate<Func<IDurableObject>>();
        var objects = host.GetMethod("Objects")!.CreateDelegate<Func<IDurableObject, object[]>>();
        var change = host.GetMethod("Change")!.CreateDelegate<Action<IDurableObject>>();
        var reverse = host.GetMethod("Reverse")!.CreateDelegate<Action<IDurableObject>>();
        var check = host.GetMethod("Check")!.CreateDelegate<Action<IDurableObject, int>>();
        var expected = ExpectedBootstrapDictionaryLayouts();
        ObjectLayout[] layouts = [ObjectLayout.ForDurable(expected.Root), ObjectLayout.ForDictionary(expected.Scalars), ObjectLayout.ForDictionary(expected.Entries)];

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
            using PreparedBranchSave save = session.Prepare(original);
            AssertBootstrapDictionaryCatalog(save.Catalog.View, expected.Root, expected.Key, expected.Point, expected.Scalars, expected.Entries);
            Assert.Equal(3, save.Business.Write.Records.Count);
            Assert.All(save.Business.Write.Records, row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
            RepositorySaveResult result = save.Commit();
            s0 = result.Business.Address;
            schemaRoot = result.Meta!.Address;
            rootId = result.Business.RootId.Value;
            Assert.Equal(3, result.Business.ObjectHeads.Count);
        }

        using (RepositoryStorageFacade repository = RepositoryStorageFacade.OpenReadOnlyExisting(files.Path)) {
            SchemaCatalogView meta = SchemaCatalogView.Load(repository.Store, schemaRoot); // No business model input.
            AssertBootstrapDictionaryCatalog(meta, expected.Root, expected.Key, expected.Point, expected.Scalars, expected.Entries);
            Assert.Equal(0, repository.Store.AppendedFrames);
            StateModelRegistry registered = models();
            ModelExecutionEnvironment snapshot = registered.CreateEnvironment(schemaLookup: key => meta.Schemas.GetValueOrDefault(key));
            DecodedGraphRows decoded = StoredGraphDecoder.Read(repository.Store, s0, snapshot, meta);
            Assert.Equal(3, decoded.Rows.Count);
            ids = layouts.Select(layout => Assert.Single(decoded.Rows.Values, row => row.Layout.Equals(layout)).Id.Value).ToArray();
            Assert.Equal(rootId, ids[0]);
            StoredGraphRevision graph = repository.Store.ReadGraph(s0);
            AssertBootstrapScalarDictionaryWithoutCatalog(repository, graph, ids[1], expected.Scalars);
            AssertBootstrapClosureDynamicDescriptor(repository, graph, rootId, layouts[0]);
            AssertBootstrapClosureDynamicDescriptor(repository, graph, ids[2], layouts[2]);
            ObjectVersionChainEntry userBase = Assert.Single(repository.Store.ReadObjectChain(s0, ids[2]).Entries);
            var user = TypedBaseBodyCodec.Decode(userBase.Record.Body, meta);
            Assert.Equal((byte)DictionaryComparerKind.CurrentDefault, user.Body[0]);
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
            SchemaCatalogView meta = session.Catalog.Accepted!;
            ModelExecutionEnvironment snapshot = registered.CreateEnvironment(schemaLookup: key => meta.Schemas.GetValueOrDefault(key));
            DecodedGraphRows oldRows = StoredGraphDecoder.Read(repository.Store, s0, snapshot, meta);
            change(restored); // Remove, Add, three value patches, then reverse insertion order.
            check(restored, 1);
            using (PreparedBranchSave save = session.Prepare(restored, parameters: new(1_000_000, 1))) {
                Assert.Null(save.Catalog.Write);
                Assert.Empty(save.Catalog.EncodedIds);
                Assert.Equal(0, save.Catalog.AddedDescriptors);
                Assert.Equal(s0, save.Business.Write.SourceGraph);
                Assert.Empty(save.Business.Write.RemovedIds);
                Assert.Equal(2, save.Business.Write.Records.Count);
                Assert.Equal<uint>(ids.Skip(1).Order(), save.Business.Write.Records.Select(row => row.ObjectId));
                foreach (ObjectVersionRecord delta in save.Business.Write.Records) {
                    Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
                    Assert.Equal(prior.ObjectHeads[delta.ObjectId], delta.PriorAddress);
                    ObjectStateRecord old = oldRows.Rows[new(delta.ObjectId)];
                    InspectBootstrapDictionaryOperations(snapshot, old, delta.Body.ToArray(), old.Layout.Dictionary!);
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
            reverse(restored); // Enumeration changes alone must preserve both stored comparer modes and content.
            check(restored, 1);
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
            AssertBootstrapDictionaryCatalog(meta, expected.Root, expected.Key, expected.Point, expected.Scalars, expected.Entries);
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

    private static void InspectBootstrapDictionaryOperations(ModelExecutionEnvironment snapshot, ObjectStateRecord prior,
        byte[] body, DictionaryLayout layout) {
        StateValueBinding key = snapshot.ResolveStoredValue(layout.KeySlot), value = snapshot.ResolveStoredValue(layout.ValueSlot);
        typeof(DurableSchemaGeneratorTests).GetMethod(nameof(InspectBootstrapDictionaryOperationsTyped), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(key.StateType, value.StateType, key.StateOpsType, value.StateOpsType)
            .Invoke(null, [prior, body, layout]);
    }

    private static void InspectBootstrapDictionaryOperationsTyped<K, V, KOps, VOps>(ObjectStateRecord row, byte[] body,
        DictionaryLayout layout) where K : unmanaged where V : unmanaged where KOps : IStateOps<K> where VOps : IStateOps<V> {
        FrozenDictionaryState<K, V> prior = row.GetDictionaryState<K, V>();
        Assert.Equal(128, prior.Count);
        Assert.Equal(typeof(K) == typeof(int) ? DictionaryComparerKind.ScalarDefault : DictionaryComparerKind.CurrentDefault, prior.ComparerKind);
        string Canonical(K key) {
            ArrayBufferWriter<byte> buffer = new();
            BareValueWriter writer = new(buffer);
            KOps.WriteBase(ref writer, in key, layout.KeySlot);
            return Convert.ToHexString(buffer.WrittenSpan);
        }
        K ReadKey(ref BareValueReader input, out string canonical) {
            int start = input.ConsumedCount;
            K key = KOps.ReadBase(ref input, layout.KeySlot);
            canonical = Canonical(key);
            Assert.Equal(canonical, Convert.ToHexString(body.AsSpan(start, input.ConsumedCount - start)));
            return key;
        }
        Dictionary<string, V> old = prior.Entries.ToArray().ToDictionary(entry => Canonical(entry.Key), entry => entry.Value);
        BareValueReader reader = new(body);
        Assert.Equal(1u, reader.ReadVarUInt32());
        _ = ReadKey(ref reader, out string removed);
        Assert.Equal(Canonical(prior[5].Key), removed); // Seed insertion order is known; wire addresses use complete key bytes.
        Assert.Equal(3u, reader.ReadVarUInt32());
        HashSet<string> patches = [Canonical(prior[6].Key), Canonical(prior[7].Key), Canonical(prior[10].Key)];
        for (int i = 0; i < 3; i++) {
            _ = ReadKey(ref reader, out string address);
            Assert.True(patches.Remove(address));
            V previous = old[address];
            V next = VOps.ApplyDelta(ref reader, in previous, layout.ValueSlot);
            Assert.False(VOps.StateEquals(in previous, in next, layout.ValueSlot));
        }
        Assert.Empty(patches);
        Assert.Equal(1u, reader.ReadVarUInt32());
        K added = ReadKey(ref reader, out string addition);
        Assert.False(old.ContainsKey(addition));
        if (typeof(K) == typeof(int)) { Assert.Equal(1000, (int)(object)added); }
        else {
            Assert.Equal(1000, typeof(K).GetField("Segment0Field1")!.GetValue(added));
            NullableState<int> rank = (NullableState<int>)typeof(K).GetField("Segment0Field2")!.GetValue(added)!;
            Assert.True(rank.HasValue);
            Assert.Equal(11000, rank.Value);
        }
        _ = VOps.ReadBase(ref reader, layout.ValueSlot);
        reader.EnsureFullyConsumed();
    }

    private static void AssertBootstrapDictionaryCatalog(SchemaCatalogView meta, DurableSchema root, DurableSchema key,
        DurableSchema point, DictionaryLayout scalars, DictionaryLayout entries) {
        Assert.Equal(3, meta.Schemas.Count);
        Assert.Equal(4, meta.DescriptorIds.Count);
        foreach (DurableSchema schema in new[] { root, key, point }) {
            Assert.Equal(schema, meta.Schemas[new(schema.Type, schema.Version)]);
        }
        Assert.Equal(ObjectLayout.ForDictionary(entries), meta.ResolveLayout(meta.DescriptorId(ObjectLayout.ForDictionary(entries))));
        Assert.Throws<InvalidDataException>(() => meta.DescriptorId(ObjectLayout.ForDictionary(scalars)));
        Assert.Throws<InvalidDataException>(() => meta.ResolveLayout(meta.SchemaNodeId(key)));
        Assert.Throws<InvalidDataException>(() => meta.ResolveLayout(meta.SchemaNodeId(point)));
    }

    private static void AssertBootstrapScalarDictionaryWithoutCatalog(RepositoryStorageFacade repository, StoredGraphRevision graph,
        uint id, DictionaryLayout expected) {
        ObjectVersionChainEntry first = Assert.Single(repository.Store.ReadObjectChain(graph.Address, id).Entries);
        var decoded = TypedBaseBodyCodec.Decode(first.Record.Body, catalog: null);
        Assert.Equal(ObjectLayout.ForDictionary(expected), decoded.Layout);
        Assert.Equal((byte)DictionaryComparerKind.ScalarDefault, decoded.Body[0]);
        ModelExecutionEnvironment empty = new StateModelRegistry().CreateEnvironment();
        ObjectReaderBinding reader = empty.ResolveObjectReader(decoded.Layout);
        ObjectStateRecord row = reader.Read(new(id), new StateModelBodySource(decoded.Body));
        ((DictionaryStateReader)reader).ValidateLookupKeys(row, _ => throw new InvalidOperationException("Scalar keys require no reference lookup."));
        FrozenDictionaryState<int, NullableState<long>> state = row.GetDictionaryState<int, NullableState<long>>();
        Assert.Equal(DictionaryComparerKind.ScalarDefault, state.ComparerKind);
        Assert.Equal(128, state.Count);
        foreach (var entry in state.Entries) {
            Assert.Equal(entry.Key % 5 != 0, entry.Value.HasValue);
            if (entry.Value.HasValue) { Assert.Equal((long)int.MaxValue + 1000 + entry.Key, entry.Value.Value); }
        }
    }

    private static (DurableSchema Root, DurableSchema Key, DurableSchema Point, DictionaryLayout Scalars, DictionaryLayout Entries) ExpectedBootstrapDictionaryLayouts() {
        DurableSchema key = new("p1.dict.key", 1, SchemaKind.InlineValue,
            new DurableFieldInfo(1, TypeTag.Int32), DurableFieldInfo.Nullable(2, new(1, TypeTag.Int32)));
        DurableSchema point = new("p1.dict.point", 1, SchemaKind.InlineValue,
            new DurableFieldInfo(1, TypeTag.Int32), DurableFieldInfo.Nullable(2, new(1, TypeTag.Int32)));
        DictionaryLayout scalars = new(new(1, TypeTag.Int32), DurableFieldInfo.Nullable(2, new(1, TypeTag.Int64)));
        DictionaryLayout entries = new(new(1, TypeTag.InlineValue, inlineSchema: key), DurableFieldInfo.Nullable(2, new(1, TypeTag.InlineValue, inlineSchema: point)));
        DurableSchema root = new("p1.dict.root", 1, [DurableFieldInfo.Reference(1, scalars.Type), DurableFieldInfo.Reference(2, entries.Type)]);
        return (root, key, point, scalars, entries);
    }

    private const string BootstrapDictionarySource = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Persistence;
        namespace BootstrapDictionaries;
        [DurableType("p1.dict.key", 1)] public partial struct Key : IEquatable<Key> {
            [DurableField(1)] public int X;
            [DurableField(2)] public int? Rank;
            public bool Equals(Key other) => X == other.X && Rank == other.Rank;
            public override bool Equals(object? value) => value is Key other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(X, Rank);
        }
        [DurableType("p1.dict.point", 1)] public partial struct Point {
            [DurableField(1)] public int X;
            [DurableField(2)] public int? Rank;
        }
        [DurableType("p1.dict.root", 1)] public sealed partial class Root : IDurableObject {
            [DurableField(1)] public Dictionary<int, long?> Scalars = new();
            [DurableField(2)] public Dictionary<Key, Point?> Entries = new();
        }
        public static class Host {
            public static StateModelRegistry Models() {
                StateModelRegistry models = new();
                Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
                return models;
            }
            private static Key KeyFor(int index) => new() { X = index, Rank = index % 3 == 0 ? null : 10000 + index };
            public static IDurableObject Create() {
                Root root = new();
                for (int i = 0; i < 128; i++) {
                    root.Scalars.Add(i, i % 5 == 0 ? null : (long)int.MaxValue + 1000 + i);
                    root.Entries.Add(KeyFor(i), i % 5 == 0 ? null : new Point { X = 1000 + i, Rank = i % 4 == 0 ? null : i });
                }
                return root;
            }
            public static object[] Objects(IDurableObject value) {
                Root root = (Root)value;
                return [root, root.Scalars, root.Entries];
            }
            public static void Change(IDurableObject value) {
                Root root = (Root)value;
                root.Scalars.Remove(5);
                root.Scalars[6] = null;
                root.Scalars[7] = long.MaxValue - 23;
                root.Scalars[10] = long.MinValue + 17;
                root.Scalars.Add(1000, 77700000007L);
                root.Entries.Remove(KeyFor(5));
                root.Entries[KeyFor(6)] = null;
                root.Entries[KeyFor(7)] = new Point { X = 7007, Rank = null };
                root.Entries[KeyFor(10)] = new Point { X = 9000, Rank = 100 };
                root.Entries.Add(KeyFor(1000), new Point { X = 8000, Rank = 1000 });
                Reverse(root);
            }
            public static void Reverse(IDurableObject value) {
                Root root = (Root)value;
                var scalars = root.Scalars.ToArray();
                root.Scalars.Clear();
                foreach (var pair in scalars.Reverse()) { root.Scalars.Add(pair.Key, pair.Value); }
                var entries = root.Entries.ToArray();
                root.Entries.Clear();
                foreach (var pair in entries.Reverse()) { root.Entries.Add(pair.Key, pair.Value); }
            }
            public static void Check(IDurableObject value, int stage) {
                Root root = (Root)value;
                if (root.Scalars.Count != 128 || root.Entries.Count != 128) throw new InvalidOperationException("Dictionary count mismatch.");
                for (int i = 0; i < 128; i++) {
                    Key key = KeyFor(i);
                    if (stage == 1 && i == 5) {
                        if (root.Scalars.ContainsKey(i) || root.Entries.ContainsKey(key)) throw new InvalidOperationException("Removed keys remained live.");
                        continue;
                    }
                    long? expectedScalar = stage == 1 && i == 7 ? long.MaxValue - 23 : stage == 1 && i == 10 ? long.MinValue + 17
                        : i % 5 == 0 || stage == 1 && i == 6 ? null : (long)int.MaxValue + 1000 + i;
                    if (!root.Scalars.TryGetValue(i, out long? scalar) || scalar != expectedScalar) throw new InvalidOperationException("Nullable scalar lookup mismatch.");
                    if (!root.Entries.TryGetValue(key, out Point? point)) throw new InvalidOperationException("Full inline key lookup failed.");
                    Key different = new() { X = key.X, Rank = key.Rank.HasValue ? key.Rank + 1 : -1 };
                    if (root.Entries.ContainsKey(different)) throw new InvalidOperationException("CurrentDefault key comparer ignored nullable Rank.");
                    bool absent = i % 5 == 0 && !(stage == 1 && i == 10) || stage == 1 && i == 6;
                    if (absent) {
                        if (point is not null) throw new InvalidOperationException("Absent Point value mismatch.");
                    } else {
                        int x = stage == 1 && i == 7 ? 7007 : stage == 1 && i == 10 ? 9000 : 1000 + i;
                        int? rank = stage == 1 && i == 10 ? 100 : i % 4 == 0 || stage == 1 && i == 7 ? null : i;
                        if (point is not { } present || present.X != x || present.Rank != rank) throw new InvalidOperationException("Point value mismatch.");
                    }
                }
                if (stage == 0) {
                    if (root.Scalars.ContainsKey(1000) || root.Entries.ContainsKey(KeyFor(1000))) throw new InvalidOperationException("Added key leaked into old history.");
                } else if (!root.Scalars.TryGetValue(1000, out long? added) || added != 77700000007L ||
                    !root.Entries.TryGetValue(KeyFor(1000), out Point? addedPoint) || addedPoint is not { X: 8000, Rank: 1000 }) {
                    throw new InvalidOperationException("Added key/value mismatch.");
                }
            }
        }
        """;
}
