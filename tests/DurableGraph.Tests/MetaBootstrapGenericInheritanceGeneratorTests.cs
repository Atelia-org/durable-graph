using System.Reflection;
using Atelia.DurableGraph.Persistence;

using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void MetaBootstrapOrderedGenericInheritanceColdReadsCompleteSchemasAndContinuesLeafDelta() {
        GeneratorTestRun run = RunGenerator(BootstrapGenericInheritanceSource, [], null, "true");
        AssertSchemaOnlyCompiles(run);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("BootstrapGenericInheritance.Host")!;
        var models = host.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>();
        var create = host.GetMethod("Create")!.CreateDelegate<Func<IDurableObject>>();
        var objects = host.GetMethod("Objects")!.CreateDelegate<Func<IDurableObject, IDurableObject[]>>();
        var change = host.GetMethod("Change")!.CreateDelegate<Action<IDurableObject>>();
        var check = host.GetMethod("Check")!.CreateDelegate<Action<IDurableObject, int>>();
        var constructors = host.GetMethod("Constructors")!.CreateDelegate<Func<int>>();
        DurableSchema[] expected = ExpectedBootstrapGenericInheritanceSchemas();

        using RawBaseDirectory files = new();
        FrameAddress s0, s1, s2, metaRoot;
        uint[] ids;
        IDurableObject original = create();
        Assert.Equal(5, constructors()); // Root plus two Base and two Derived constructor calls.
        using (RepositoryStorageFacade repository = RepositoryStorageFacade.CreateNew(files.Path)) {
            StateModelRegistry registered = models();
            ModelExecutionEnvironment snapshot = registered.CreateEnvironment();
            IDurableObject[] initial = objects(original);
            for (int i = 0; i < initial.Length; i++) {
                Assert.Equal(expected[i], snapshot.ResolveCurrentModel(initial[i].GetType()).CurrentSchema);
            }
            check(original, 0);
            BranchSaveSession session = new(repository, "main", registered);
            using PreparedBranchSave save = session.Prepare(original); // Complete base/inline closure comes only from actual Capture.
            AssertBootstrapGenericInheritanceCatalog(save.Catalog.View, expected);
            Assert.Equal(3, save.Business.Write.Records.Count);
            Assert.All(save.Business.Write.Records, row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
            RepositorySaveResult result = save.Commit();
            s0 = result.Business.Address;
            metaRoot = result.Meta!.Address;
            DecodedGraphRows rows = StoredGraphDecoder.Read(repository.Store, s0, snapshot, session.Catalog.Accepted);
            ids = expected.Take(3).Select(schema => Assert.Single(rows.Rows.Values, row => row.Schema!.Equals(schema)).Id.Value).ToArray();
            Assert.Equal(result.Business.RootId.Value, ids[0]);
            Assert.Equal(3, result.Business.ObjectHeads.Count); // Base and Pair declarations have no independent objects.
            AssertBootstrapGenericInheritanceDtos(rows, ids, 0);
        }

        Dictionary<string, string> before = BootstrapContainerUpgradeFileHashes(files.Path);
        using (RepositoryStorageFacade repository = RepositoryStorageFacade.OpenReadOnlyExisting(files.Path)) {
            // This cold metadata path takes no business CLR types or registered model input.
            SchemaCatalogView catalog = SchemaCatalogView.Load(repository.Store, metaRoot);
            AssertBootstrapGenericInheritanceCatalog(catalog, expected);
            Assert.NotSame(expected[1], catalog.Schemas[new(expected[1].Type, 1)]);
            StateModelRegistry registered = models();
            ModelExecutionEnvironment snapshot = registered.CreateEnvironment(schemaLookup: key => catalog.Schemas.GetValueOrDefault(key));
            DecodedGraphRows rows = StoredGraphDecoder.Read(repository.Store, s0, snapshot, catalog);
            Assert.Equal(3, rows.Rows.Count);
            AssertBootstrapGenericInheritanceDtos(rows, ids, 0);
            for (int i = 0; i < ids.Length; i++) {
                Assert.Equal(expected[i], rows.Rows[new(ids[i])].Schema);
                AssertBootstrapClosureDynamicDescriptor(repository, rows.Snapshot, ids[i], ObjectLayout.ForDurable(expected[i]));
                Assert.Equal(expected[i], snapshot.ResolveReader(expected[i]).Schema); // Positive control precedes malformed exact shapes.
            }
            DurableSchema wrongOrder = new(expected[2].Type, 1, expected[1].Fields.ToArray(), expected[1].BaseSchema);
            DurableSchema wrongBase = new(expected[1].Type, 1, expected[1].Fields.ToArray(), expected[4]);
            DurableSchema wrongPair = new(expected[5].Type, 1, SchemaKind.InlineValue,
                new DurableFieldInfo(1, TypeTag.Int64), new DurableFieldInfo(2, TypeTag.Int64));
            DurableSchema wrongInline = new(expected[1].Type, 1,
                [new(1, TypeTag.InlineValue, inlineSchema: wrongPair), expected[1].Fields[1]], expected[1].BaseSchema);
            foreach (DurableSchema malformed in new[] { wrongOrder, wrongBase, wrongInline }) {
                Assert.Throws<InvalidDataException>(() => catalog.DescriptorId(ObjectLayout.ForDurable(malformed)));
                Assert.Throws<InvalidDataException>(() => snapshot.ResolveReader(malformed));
            }
            IDurableObject restored = BusinessPersistenceAdapter.ReadRoot(repository.Store, s0, registered);
            Assert.NotSame(original, restored);
            IDurableObject[] restoredObjects = objects(restored);
            Assert.Equal(typeof(int), restoredObjects[1].GetType().GetGenericArguments()[0]);
            Assert.Equal(typeof(long), restoredObjects[1].GetType().GetGenericArguments()[1]);
            Assert.Equal(typeof(long), restoredObjects[2].GetType().GetGenericArguments()[0]);
            Assert.Equal(typeof(int), restoredObjects[2].GetType().GetGenericArguments()[1]);
            check(restored, 0);
            Assert.Equal(5, constructors()); // Allocate/Hydrate bypasses every domain constructor, including the base.
            Assert.Equal(s0, repository.GetHead("main"));
            Assert.Equal(metaRoot, repository.SharedSchemaRoot);
            Assert.Equal(0, repository.Store.AppendedFrames);
        }
        AssertBootstrapContainerUpgradeFilesUnchanged(before, files.Path);

        using (RepositoryStorageFacade repository = RepositoryStorageFacade.OpenExisting(files.Path)) {
            StateModelRegistry registered = models();
            BranchSaveSession session = new(repository, "main", registered);
            IDurableObject restored = session.State!;
            check(restored, 0);
            IDurableObject[] identities = objects(restored);
            StoredGraphRevision source = session.StateSource!;
            change(restored); // One complete leaf Delta must carry both inherited U and inline Pair<T> changes.
            check(restored, 1);
            using (PreparedBranchSave save = session.Prepare(restored)) { // Default (5,5); padding makes the actual sparse Delta observable.
                Assert.Null(save.Catalog.Write);
                Assert.Empty(save.Catalog.EncodedIds);
                Assert.Equal(0, save.Catalog.AddedDescriptors);
                Assert.Equal(s0, save.Business.Write.SourceGraph);
                Assert.Empty(save.Business.Write.RemovedIds);
                Assert.Equal<uint>(ids.Skip(1).Order(), save.Business.Write.Records.Select(row => row.ObjectId));
                Assert.All(save.Business.Write.Records, row => {
                    Assert.Equal(ObjectVersionKind.Delta, row.Kind);
                    Assert.Equal(source.ObjectHeads[row.ObjectId], row.PriorAddress);
                });
                RepositorySaveResult result = save.Commit();
                s1 = result.Business.Address;
                Assert.Equal(metaRoot, result.Meta!.Address);
                Assert.Equal(metaRoot, result.Business.SchemaRoot);
                Assert.Equal(source.ObjectHeads[ids[0]], result.Business.ObjectHeads[ids[0]]);
                Assert.Equal(s1, session.StateSource!.Address);
                IDurableObject[] after = objects(session.State!);
                for (int i = 0; i < after.Length; i++) { Assert.Same(identities[i], after[i]); }
                foreach (uint id in ids.Skip(1)) {
                    ObjectVersionChain chain = repository.Store.ReadObjectChain(s1, id);
                    Assert.Equal(2, chain.Entries.Count);
                    Assert.Equal(ObjectVersionKind.Base, chain.Entries[0].Record.Kind);
                    Assert.Equal(ObjectVersionKind.Delta, chain.Entries[1].Record.Kind);
                    Assert.Equal(source.ObjectHeads[id], chain.Entries[1].Record.PriorAddress);
                }
                SchemaCatalogView catalog = session.Catalog.Accepted!;
                AssertBootstrapGenericInheritanceDtos(StoredGraphDecoder.Read(repository.Store, s1,
                    registered.CreateEnvironment(schemaLookup: key => catalog.Schemas.GetValueOrDefault(key)), catalog), ids, 1);
            }

            StoredGraphRevision accepted = session.StateSource!;
            long frames = repository.Store.AppendedFrames;
            using (PreparedBranchSave save = session.Prepare(restored)) {
                Assert.Null(save.Catalog.Write);
                Assert.Empty(save.Catalog.EncodedIds);
                Assert.Empty(save.Business.Write.Records);
                Assert.Empty(save.Business.Write.RemovedIds);
                Assert.Equal(s1, save.Business.Write.SourceGraph);
                RepositorySaveResult result = save.Commit();
                s2 = result.Business.Address;
                Assert.Equal(metaRoot, result.Meta!.Address);
                Assert.Null(result.Business.OwnedBatch);
                Assert.Equal(accepted.DirectoryRoot, result.Business.DirectoryRoot);
                foreach (uint id in ids) { Assert.Equal(accepted.ObjectHeads[id], result.Business.ObjectHeads[id]); }
            }
            Assert.Equal(frames + 1, repository.Store.AppendedFrames);
            Assert.Equal(5, constructors());
        }

        before = BootstrapContainerUpgradeFileHashes(files.Path);
        using (RepositoryStorageFacade repository = RepositoryStorageFacade.OpenReadOnlyExisting(files.Path)) {
            SchemaCatalogView catalog = SchemaCatalogView.Load(repository.Store, metaRoot);
            AssertBootstrapGenericInheritanceCatalog(catalog, expected);
            Assert.Equal(s2, repository.GetHead("main"));
            Assert.Equal(s2, repository.GetStateHead("main"));
            Assert.Equal(metaRoot, repository.SharedSchemaRoot);
            StateModelRegistry registered = models();
            ModelExecutionEnvironment snapshot = registered.CreateEnvironment(schemaLookup: key => catalog.Schemas.GetValueOrDefault(key));
            foreach ((FrameAddress graph, int stage) in new[] { (s0, 0), (s1, 1), (s2, 1) }) {
                Assert.Equal(metaRoot, repository.Store.ReadGraph(graph).SchemaRoot);
                AssertBootstrapGenericInheritanceDtos(StoredGraphDecoder.Read(repository.Store, graph, snapshot, catalog), ids, stage);
                check(BusinessPersistenceAdapter.ReadRoot(repository.Store, graph, registered), stage);
            }
            Assert.Equal(5, constructors());
            Assert.Equal(0, repository.Store.AppendedFrames);
        }
        AssertBootstrapContainerUpgradeFilesUnchanged(before, files.Path);
    }

    private static void AssertBootstrapGenericInheritanceDtos(DecodedGraphRows decoded, uint[] ids, int stage) {
        for (int i = 1; i < ids.Length; i++) {
            ObjectStateRecord row = decoded.Rows[new(ids[i])];
            object inherited = StateModelField(row, "Segment0Field1")!;
            if (i == 1) { Assert.Equal(stage == 0 ? 90000000001L : -90000000007L, Assert.IsType<long>(inherited)); }
            else { Assert.Equal(stage == 0 ? -333 : int.MaxValue - 17, Assert.IsType<int>(inherited)); }
            Assert.Equal(new ObjectId(ids[0]), Assert.IsType<ObjectId>(StateModelField(row, "Segment0Field2")));
            for (int field = 3; field <= 6; field++) { Assert.Equal((double)(field - 2), Assert.IsType<double>(StateModelField(row, $"Segment0Field{field}"))); }
            object pair = StateModelField(row, "Segment1Field1")!;
            object left = pair.GetType().GetField("Segment0Field1", BindingFlags.Public | BindingFlags.Instance)!.GetValue(pair)!;
            object right = pair.GetType().GetField("Segment0Field2", BindingFlags.Public | BindingFlags.Instance)!.GetValue(pair)!;
            if (i == 1) {
                Assert.Equal(stage == 0 ? -17 : 31, Assert.IsType<int>(left));
                Assert.Equal(stage == 0 ? 23 : int.MinValue + 99, Assert.IsType<int>(right));
            } else {
                Assert.Equal(stage == 0 ? long.MinValue + 17 : long.MaxValue - 41, Assert.IsType<long>(left));
                Assert.Equal(stage == 0 ? long.MaxValue - 23 : long.MinValue + 43, Assert.IsType<long>(right));
            }
            Assert.Equal(new ObjectId(ids[i]), Assert.IsType<ObjectId>(StateModelField(row, "Segment1Field2")));
        }
    }

    private static void AssertBootstrapGenericInheritanceCatalog(SchemaCatalogView catalog, DurableSchema[] expected) {
        Assert.Equal(7, catalog.Schemas.Count);
        Assert.Equal(7, catalog.DescriptorIds.Count);
        foreach (DurableSchema schema in expected) {
            Assert.Equal(schema, catalog.Schemas[new(schema.Type, 1)]);
            Assert.Contains(catalog.SchemaNodeId(schema), catalog.DescriptorIds);
        }
        DurableSchema first = catalog.Schemas[new(expected[1].Type, 1)], second = catalog.Schemas[new(expected[2].Type, 1)];
        Assert.Equal(new[] { TypeExpr.Builtin(TypeTag.Int32), TypeExpr.Builtin(TypeTag.Int64) }, first.Type.Arguments);
        Assert.Equal(new[] { TypeExpr.Builtin(TypeTag.Int64), TypeExpr.Builtin(TypeTag.Int32) }, second.Type.Arguments);
        Assert.Equal(expected[3], first.BaseSchema);
        Assert.Equal(expected[4], second.BaseSchema);
        Assert.Equal(TypeTag.Int64, first.BaseSchema!.Fields[0].TypeTag);
        Assert.Equal(TypeTag.Int32, second.BaseSchema!.Fields[0].TypeTag);
        Assert.Equal(expected[5], first.Fields[0].InlineSchema);
        Assert.Equal(expected[6], second.Fields[0].InlineSchema);
        Assert.All(first.Fields[0].InlineSchema!.Fields, field => Assert.Equal(TypeTag.Int32, field.TypeTag));
        Assert.All(second.Fields[0].InlineSchema!.Fields, field => Assert.Equal(TypeTag.Int64, field.TypeTag));
        foreach ((int firstIndex, int secondIndex) in new[] { (1, 2), (3, 4), (5, 6) }) {
            Assert.Equal(expected[firstIndex].SchemaId, expected[secondIndex].SchemaId);
            Assert.NotEqual(catalog.SchemaNodeId(expected[firstIndex]), catalog.SchemaNodeId(expected[secondIndex]));
        }
        foreach (DurableSchema inline in expected.Skip(5)) {
            Assert.Throws<InvalidDataException>(() => catalog.ResolveLayout(catalog.SchemaNodeId(inline)));
        }
    }

    private static DurableSchema[] ExpectedBootstrapGenericInheritanceSchemas() {
        TypeExpr integer = TypeExpr.Builtin(TypeTag.Int32), wide = TypeExpr.Builtin(TypeTag.Int64), rootType = TypeExpr.Named("GI.Root");
        DurableSchema Pair(TypeExpr argument, TypeTag tag) => new(TypeExpr.Named("GI.Pair", argument), 1, SchemaKind.InlineValue,
            new DurableFieldInfo(1, tag), new DurableFieldInfo(2, tag));
        DurableSchema Base(TypeExpr argument, TypeTag tag) => new(TypeExpr.Named("GI.Base", argument), 1,
            new DurableFieldInfo(1, tag), DurableFieldInfo.Reference(2, rootType), new(3, TypeTag.Double), new(4, TypeTag.Double), new(5, TypeTag.Double), new(6, TypeTag.Double));
        DurableSchema pairInt = Pair(integer, TypeTag.Int32), pairLong = Pair(wide, TypeTag.Int64);
        DurableSchema baseLong = Base(wide, TypeTag.Int64), baseInt = Base(integer, TypeTag.Int32);
        TypeExpr first = TypeExpr.Named("GI.Derived", integer, wide), second = TypeExpr.Named("GI.Derived", wide, integer);
        DurableSchema derivedIntLong = new(first, 1, [new(1, TypeTag.InlineValue, inlineSchema: pairInt), DurableFieldInfo.Reference(2, first)], baseLong);
        DurableSchema derivedLongInt = new(second, 1, [new(1, TypeTag.InlineValue, inlineSchema: pairLong), DurableFieldInfo.Reference(2, second)], baseInt);
        DurableSchema root = new(rootType, 1, DurableFieldInfo.Reference(1, first), DurableFieldInfo.Reference(2, second),
            DurableFieldInfo.Reference(3, first), DurableFieldInfo.Reference(4, second), DurableFieldInfo.Reference(5, rootType));
        return [root, derivedIntLong, derivedLongInt, baseLong, baseInt, pairInt, pairLong];
    }

    private const string BootstrapGenericInheritanceSource = """
        using System;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Persistence;
        namespace BootstrapGenericInheritance;
        public static class Trace { public static int Constructors; }
        [DurableType("GI.Pair", 1)] public readonly partial struct Pair<T> where T : unmanaged {
            [DurableField(1)] private readonly T _left;
            [DurableField(2)] private readonly T _right;
            public Pair(T left, T right) { _left = left; _right = right; }
            public T Left => _left;
            public T Right => _right;
        }
        [DurableType("GI.Base", 1)] public partial class Base<U> : IDurableObject where U : unmanaged {
            [DurableField(1)] private U _value;
            [DurableField(2)] private readonly Root _owner;
            // Stable payload makes the complete leaf Base larger than its sparse same-layout Delta.
            [DurableField(3)] private readonly double _a = 1;
            [DurableField(4)] private readonly double _b = 2;
            [DurableField(5)] private readonly double _c = 3;
            [DurableField(6)] private readonly double _d = 4;
            protected Base(U value, Root owner) { Trace.Constructors++; _value = value; _owner = owner; }
            public U Value => _value;
            public Root Owner => _owner;
            protected void SetBase(U value) => _value = value;
            public bool CheckPadding() => _a == 1 && _b == 2 && _c == 3 && _d == 4;
        }
        [DurableType("GI.Derived", 1)] public sealed partial class Derived<T, U> : Base<U> where T : unmanaged where U : unmanaged {
            [DurableField(1)] private Pair<T> _pair;
            [DurableField(2)] public Derived<T, U> Self;
            public Derived(Pair<T> pair, U value, Root owner) : base(value, owner) { Trace.Constructors++; _pair = pair; Self = this; }
            public Pair<T> Pair => _pair;
            public void Set(Pair<T> pair, U value) { _pair = pair; SetBase(value); }
        }
        [DurableType("GI.Root", 1)] public sealed partial class Root : IDurableObject {
            [DurableField(1)] public readonly Derived<int, long> First;
            [DurableField(2)] public readonly Derived<long, int> Second;
            [DurableField(3)] public readonly Derived<int, long> FirstAlias;
            [DurableField(4)] public readonly Derived<long, int> SecondAlias;
            [DurableField(5)] public readonly Root Self;
            public Root() {
                Trace.Constructors++;
                First = new(new(-17, 23), 90000000001L, this);
                Second = new(new(long.MinValue + 17, long.MaxValue - 23), -333, this);
                FirstAlias = First; SecondAlias = Second; Self = this;
            }
        }
        public static class Host {
            public static StateModelRegistry Models() {
                StateModelRegistry models = new(); Atelia.DurableGraph.Generated.DurableDefinitions.Register(models); return models;
            }
            public static IDurableObject Create() => new Root();
            public static IDurableObject[] Objects(IDurableObject value) {
                Root root = (Root)value; return new IDurableObject[] { root, root.First, root.Second };
            }
            public static int Constructors() => Trace.Constructors;
            public static void Change(IDurableObject value) {
                Root root = (Root)value;
                root.First.Set(new(31, int.MinValue + 99), -90000000007L);
                root.Second.Set(new(long.MaxValue - 41, long.MinValue + 43), int.MaxValue - 17);
            }
            public static void Check(IDurableObject value, int stage) {
                Root root = (Root)value;
                if (!ReferenceEquals(root, root.Self) || !ReferenceEquals(root.First, root.FirstAlias) || !ReferenceEquals(root.Second, root.SecondAlias) ||
                    !ReferenceEquals(root, root.First.Owner) || !ReferenceEquals(root, root.Second.Owner) ||
                    !ReferenceEquals(root.First, root.First.Self) || !ReferenceEquals(root.Second, root.Second.Self) || ReferenceEquals(root.First, root.Second) ||
                    root.First.GetType() != typeof(Derived<int, long>) || root.Second.GetType() != typeof(Derived<long, int>) ||
                    !root.First.CheckPadding() || !root.Second.CheckPadding()) throw new InvalidOperationException("Incorrect generic identities, shared refs or base padding.");
                if (root.First.Value != (stage == 0 ? 90000000001L : -90000000007L) ||
                    root.First.Pair.Left != (stage == 0 ? -17 : 31) || root.First.Pair.Right != (stage == 0 ? 23 : int.MinValue + 99) ||
                    root.Second.Value != (stage == 0 ? -333 : int.MaxValue - 17) ||
                    root.Second.Pair.Left != (stage == 0 ? long.MinValue + 17 : long.MaxValue - 41) ||
                    root.Second.Pair.Right != (stage == 0 ? long.MaxValue - 23 : long.MinValue + 43)) throw new InvalidOperationException("Incorrect inherited U or inline Pair<T> content.");
            }
        }
        """;
}
