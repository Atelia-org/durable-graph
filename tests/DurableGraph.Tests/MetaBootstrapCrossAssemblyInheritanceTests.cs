using System.Reflection;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void MetaBootstrapThreeAssemblyHiddenInheritanceColdReadsAndContinuesWholeLeafDelta() {
        // Reuse the established declarations exactly; only the consumer Host changes backend.
        GeneratorTestRun bases = RunCrossAssemblyGenerator(InheritanceGraphBaseSource, forceDefinitions: "true");
        AssertSchemaOnlyCompiles(bases);
        var baseImage = EmitCrossAssemblyReference(bases);
        GeneratorTestRun middle = RunCrossAssemblyGenerator(InheritanceGraphMiddleSource, [baseImage.Reference]);
        AssertSchemaOnlyCompiles(middle);
        var middleImage = EmitCrossAssemblyReference(middle);
        int hostOffset = InheritanceGraphApplicationSource.IndexOf("public static class Host {", StringComparison.Ordinal);
        Assert.True(hostOffset > 0);
        string applicationSource = InheritanceGraphApplicationSource[..hostOffset] + BootstrapCrossAssemblyInheritanceHost;
        GeneratorTestRun app = RunCrossAssemblyGenerator(applicationSource, [baseImage.Reference, middleImage.Reference]);
        AssertSchemaOnlyCompiles(app);
        foreach (GeneratorTestRun consumer in new[] { middle, app }) {
            string generated = GeneratedSource(consumer, "DurableGenericStates.g.cs");
            Assert.DoesNotContain("typeof(global::InheritanceBases.Hidden", generated);
            Assert.DoesNotContain("global::InheritanceBases.HiddenPair", generated);
            Assert.DoesNotContain("global::InheritanceBases.HiddenPoint", generated);
            Assert.DoesNotContain(consumer.OutputCompilation.GetDiagnostics(), diagnostic => diagnostic.Id is "CS0433" or "CS0436");
        }
        using CrossAssemblyLoadScope scope = new(bases, middle, app);
        Assembly baseAssembly = scope.Load(bases), middleAssembly = scope.Load(middle), appAssembly = scope.Load(app);
        Assert.Equal(3, new[] { baseAssembly, middleAssembly, appAssembly }.Distinct().Count());
        Assert.False(baseAssembly.GetType("InheritanceBases.HiddenPair`1")!.IsPublic);
        Assert.False(baseAssembly.GetType("InheritanceBases.HiddenPoint")!.IsPublic);
        Type host = appAssembly.GetType("InheritanceApp.Host")!;
        var models = host.GetMethod("Models")!.CreateDelegate<Func<bool, StateModelRegistry>>();
        var create = host.GetMethod("Create")!.CreateDelegate<Func<IDurableObject>>();
        var objects = host.GetMethod("Objects")!.CreateDelegate<Func<IDurableObject, object[]>>();
        var change = host.GetMethod("Change")!.CreateDelegate<Action<IDurableObject>>();
        var check = host.GetMethod("Check")!.CreateDelegate<Action<IDurableObject, int, bool>>();
        var reset = host.GetMethod("Reset")!.CreateDelegate<Action>();
        var counters = host.GetMethod("Counters")!.CreateDelegate<Func<int[]>>();
        var expected = ExpectedBootstrapCrossAssemblyInheritance();
        ObjectLayout[] layouts = [ObjectLayout.ForDurable(expected.Schemas[0]), ObjectLayout.ForDurable(expected.Schemas[1]), ObjectLayout.ForList(expected.List)];

        using RawBaseDirectory files = new();
        FrameAddress s0, s1, s2, metaRoot;
        uint[] ids;
        IDurableObject original = create();
        Assert.Equal(new[] { 7, 3 }, counters());
        using (P0Repository repository = P0Repository.CreateNew(files.Path)) {
            StateModelRegistry registered = models(false);
            StateModelSnapshot snapshot = registered.Snapshot();
            object[] initial = objects(original);
            for (int i = 0; i < initial.Length; i++) {
                Assert.True(snapshot.TryGetCurrentObjectBinding(initial[i].GetType(), out ObjectBinding? binding));
                Assert.Equal(layouts[i], binding!.CurrentLayout);
            }
            check(original, 0, false);
            P0Session session = new(repository, "main", registered);
            using P0SessionSave save = session.Prepare(original); // No manual base, hidden inline or List registration.
            AssertBootstrapCrossAssemblyInheritanceCatalog(save.Catalog.View, expected.Schemas, expected.List);
            Assert.Equal(3, save.Business.Write.Records.Count);
            Assert.All(save.Business.Write.Records, row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
            P0SaveResult result = save.Commit();
            s0 = result.Business.Address;
            metaRoot = result.Meta!.Address;
            P0DecodedRows rows = P0TypedReader.Read(repository.Store, s0, snapshot, session.Catalog.Accepted);
            ids = layouts.Select(layout => Assert.Single(rows.Rows.Values, row => row.Layout.Equals(layout)).Id.Value).ToArray();
            Assert.Equal(result.Business.RootId.Value, ids[0]);
            Assert.Equal(3, result.Business.Objects.Count); // Only two actual leaves and their shared List.
            AssertBootstrapCrossAssemblyInheritanceDtos(rows, ids, 0);
        }
        reset(); // Cold Allocate/Hydrate must execute neither constructors nor transient initializers.

        Dictionary<string, string> before = BootstrapContainerUpgradeFileHashes(files.Path);
        using (P0Repository repository = P0Repository.OpenReadOnlyExisting(files.Path)) {
            P0CatalogView catalog = P0CatalogView.Load(repository.Store, metaRoot); // No business model registration input.
            AssertBootstrapCrossAssemblyInheritanceCatalog(catalog, expected.Schemas, expected.List);
            Assert.NotSame(expected.Schemas[0], catalog.Schemas[new(expected.Schemas[0].Type, 1)]);
            StateModelRegistry registered = models(true); // Reverse the three independent definition registrations.
            StateModelSnapshot snapshot = registered.Snapshot(schemaLookup: key => catalog.Schemas.GetValueOrDefault(key));
            P0DecodedRows rows = P0TypedReader.Read(repository.Store, s0, snapshot, catalog);
            Assert.Equal(3, rows.Rows.Count);
            AssertBootstrapCrossAssemblyInheritanceDtos(rows, ids, 0);
            for (int i = 0; i < ids.Length; i++) {
                Assert.Equal(layouts[i], rows.Rows[new(ids[i])].Layout);
                AssertBootstrapClosureDynamicDescriptor(repository, rows.Snapshot, ids[i], layouts[i]);
            }
            IDurableObject restored = P0BusinessWorkspace.ReadRoot(repository.Store, s0, registered);
            Assert.NotSame(original, restored);
            Assert.Equal(appAssembly, restored.GetType().Assembly);
            Assert.Equal(middleAssembly, objects(restored)[1].GetType().Assembly);
            check(restored, 0, true);
            Assert.Equal(new[] { 0, 0 }, counters());
            Assert.Equal(s0, repository.GetHead("main"));
            Assert.Equal(metaRoot, repository.SharedSchemaRoot);
            Assert.Equal(0, repository.Store.AppendedFrames);
        }
        AssertBootstrapContainerUpgradeFilesUnchanged(before, files.Path);

        using (P0Repository repository = P0Repository.OpenExisting(files.Path)) {
            StateModelRegistry registered = models(true);
            P0Session session = new(repository, "main", registered);
            IDurableObject restored = session.State!;
            check(restored, 0, true);
            object[] identities = objects(restored);
            P0GraphSnapshot source = session.StateSource!;
            change(restored);
            check(restored, 1, true);
            using (P0SessionSave save = session.Prepare(restored)) { // The established fixture already has stable Guid/int padding.
                Assert.Null(save.Catalog.Write);
                Assert.Empty(save.Catalog.EncodedIds);
                Assert.Equal(0, save.Catalog.AddedDescriptors);
                Assert.Equal(s0, save.Business.Write.SourceGraph);
                Assert.Empty(save.Business.Write.RemovedIds);
                ObjectVersionRecord delta = Assert.Single(save.Business.Write.Records);
                Assert.Equal(ids[0], delta.ObjectId);
                Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
                Assert.Equal(source.Objects[ids[0]], delta.PriorAddress);
                // Eleven flattened fields have one two-byte bitmap: base field 5 and leaf field 1.
                Assert.Equal(new byte[] { 0x10, 0x04, 22, 90 }, delta.Body.ToArray());
                P0SaveResult result = save.Commit();
                s1 = result.Business.Address;
                Assert.Equal(metaRoot, result.Meta!.Address);
                Assert.Equal(metaRoot, result.Business.SchemaRoot);
                Assert.Equal(s1, session.StateSource!.Address);
                Assert.NotEqual(source.Objects[ids[0]], result.Business.Objects[ids[0]]);
                foreach (uint id in ids.Skip(1)) { Assert.Equal(source.Objects[id], result.Business.Objects[id]); }
                object[] after = objects(session.State!);
                for (int i = 0; i < after.Length; i++) { Assert.Same(identities[i], after[i]); }
                P0ObjectChain chain = repository.Store.ReadObjectChain(s1, ids[0]);
                Assert.Equal(2, chain.Entries.Count);
                Assert.Equal(ObjectVersionKind.Base, chain.Entries[0].Record.Kind);
                Assert.Equal(ObjectVersionKind.Delta, chain.Entries[1].Record.Kind);
                Assert.Equal(source.Objects[ids[0]], chain.Entries[1].Record.PriorAddress);
                P0CatalogView catalog = session.Catalog.Accepted!;
                AssertBootstrapCrossAssemblyInheritanceDtos(P0TypedReader.Read(repository.Store, s1,
                    registered.Snapshot(schemaLookup: key => catalog.Schemas.GetValueOrDefault(key)), catalog), ids, 1);
            }
            P0GraphSnapshot accepted = session.StateSource!;
            long frames = repository.Store.AppendedFrames;
            using (P0SessionSave save = session.Prepare(restored)) {
                Assert.Null(save.Catalog.Write);
                Assert.Empty(save.Catalog.EncodedIds);
                Assert.Empty(save.Business.Write.Records);
                Assert.Empty(save.Business.Write.RemovedIds);
                Assert.Equal(s1, save.Business.Write.SourceGraph);
                P0SaveResult result = save.Commit();
                s2 = result.Business.Address;
                Assert.Equal(metaRoot, result.Meta!.Address);
                Assert.Null(result.Business.OwnedBatch);
                Assert.Equal(accepted.DirectoryRoot, result.Business.DirectoryRoot);
                foreach (uint id in ids) { Assert.Equal(accepted.Objects[id], result.Business.Objects[id]); }
            }
            Assert.Equal(frames + 1, repository.Store.AppendedFrames);
            Assert.Equal(new[] { 0, 0 }, counters());
        }

        before = BootstrapContainerUpgradeFileHashes(files.Path);
        using (P0Repository repository = P0Repository.OpenReadOnlyExisting(files.Path)) {
            P0CatalogView catalog = P0CatalogView.Load(repository.Store, metaRoot);
            AssertBootstrapCrossAssemblyInheritanceCatalog(catalog, expected.Schemas, expected.List);
            Assert.Equal(s2, repository.GetHead("main"));
            Assert.Equal(s2, repository.GetStateHead("main"));
            Assert.Equal(metaRoot, repository.SharedSchemaRoot);
            StateModelRegistry registered = models(false);
            StateModelSnapshot snapshot = registered.Snapshot(schemaLookup: key => catalog.Schemas.GetValueOrDefault(key));
            foreach ((FrameAddress graph, int stage) in new[] { (s0, 0), (s1, 1), (s2, 1) }) {
                Assert.Equal(metaRoot, repository.Store.ReadGraph(graph).SchemaRoot);
                AssertBootstrapCrossAssemblyInheritanceDtos(P0TypedReader.Read(repository.Store, graph, snapshot, catalog), ids, stage);
                check(P0BusinessWorkspace.ReadRoot(repository.Store, graph, registered), stage, true);
            }
            Assert.Equal(new[] { 0, 0 }, counters());
            Assert.Equal(0, repository.Store.AppendedFrames);
        }
        AssertBootstrapContainerUpgradeFilesUnchanged(before, files.Path);
    }

    private static void AssertBootstrapCrossAssemblyInheritanceDtos(P0DecodedRows decoded, uint[] ids, int stage) {
        Assert.Equal(3, decoded.Rows.Count);
        for (int i = 0; i < 2; i++) {
            ObjectStateRecord row = decoded.Rows[new(ids[i])];
            object pair = StateModelField(row, "Segment0Field1")!;
            Assert.Equal(i == 0 ? 12345 : 54321, Assert.IsType<int>(pair.GetType().GetField("Segment0Field1")!.GetValue(pair)));
            Assert.Equal(9876, Assert.IsType<int>(pair.GetType().GetField("Segment0Field2")!.GetValue(pair)));
            object optional = StateModelField(row, "Segment0Field2")!;
            Assert.Equal(i == 0, Assert.IsType<bool>(optional.GetType().GetProperty("HasValue")!.GetValue(optional)));
            if (i == 0) {
                object point = optional.GetType().GetProperty("Value")!.GetValue(optional)!;
                Assert.Equal(4567, Assert.IsType<int>(point.GetType().GetField("Segment0Field1")!.GetValue(point)));
            }
            Assert.Equal(new ObjectId(ids[0]), Assert.IsType<ObjectId>(StateModelField(row, "Segment0Field3")));
            Assert.Equal(new ObjectId(ids[2]), Assert.IsType<ObjectId>(StateModelField(row, "Segment0Field4")));
            Assert.Equal(i == 0 && stage == 1 ? 11 : 10, Assert.IsType<int>(StateModelField(row, "Segment0Field5")));
            Assert.Equal(10000, Assert.IsType<int>(StateModelField(row, "Segment0Field6")));
            Assert.Equal(20000, Assert.IsType<int>(StateModelField(row, "Segment0Field7")));
            Assert.Equal(new Guid("da4ba670-6682-453e-a117-a4e3a5a72f2d"), Assert.IsType<Guid>(StateModelField(row, "Segment0Field8")));
            Assert.Equal(33333, Assert.IsType<int>(StateModelField(row, "Segment1Field1")));
            if (i == 0) {
                Assert.Equal(44444, Assert.IsType<int>(StateModelField(row, "Segment2Field1")));
                Assert.Equal(stage == 0 ? 44 : 45, Assert.IsType<int>(StateModelField(row, "Segment3Field1")));
            } else { Assert.Equal(55, Assert.IsType<int>(StateModelField(row, "Segment2Field1"))); }
        }
        FrozenListState<ObjectId> list = decoded.Rows[new(ids[2])].GetListState<ObjectId>();
        Assert.Equal(new[] { new ObjectId(ids[0]), new ObjectId(ids[1]), new ObjectId(ids[0]) }, list.Elements.ToArray());
    }

    private static void AssertBootstrapCrossAssemblyInheritanceCatalog(P0CatalogView catalog, DurableSchema[] expected, ListLayout list) {
        Assert.Equal(7, catalog.Schemas.Count);
        Assert.Equal(8, catalog.DescriptorIds.Count);
        foreach (DurableSchema schema in expected) {
            Assert.Equal(schema, catalog.Schemas[new(schema.Type, 1)]);
            Assert.Contains(catalog.SchemaNodeId(schema), catalog.DescriptorIds);
        }
        DurableSchema leaf = catalog.Schemas[new(expected[0].Type, 1)];
        Assert.Equal(expected[2], leaf.BaseSchema);
        Assert.Equal(expected[3], leaf.BaseSchema!.BaseSchema);
        Assert.Equal(expected[4], leaf.BaseSchema.BaseSchema!.BaseSchema);
        DurableSchema @base = leaf.BaseSchema.BaseSchema.BaseSchema!;
        Assert.Equal(TypeExpr.Builtin(TypeTag.Int32), Assert.Single(@base.Type.Arguments));
        Assert.Equal(expected[5], @base.Fields[0].InlineSchema);
        Assert.Equal(TypeExpr.Builtin(TypeTag.Int32), Assert.Single(@base.Fields[0].InlineSchema!.Type.Arguments));
        Assert.Equal(expected[6], @base.Fields[1].ValueSchema);
        Assert.Equal(TypeTag.Nullable, @base.Fields[1].TypeTag);
        Assert.Equal(TypeExpr.Named("inherit.Base", TypeExpr.Builtin(TypeTag.Int32)), @base.Fields[2].TargetType);
        Assert.Equal(list.Type, @base.Fields[3].TargetType);
        foreach (DurableSchema inline in expected.Skip(5)) {
            Assert.Throws<InvalidDataException>(() => catalog.ResolveLayout(catalog.SchemaNodeId(inline)));
        }
        Assert.Equal(ObjectLayout.ForList(list), catalog.ResolveLayout(catalog.DescriptorId(ObjectLayout.ForList(list))));
    }

    private static (DurableSchema[] Schemas, ListLayout List) ExpectedBootstrapCrossAssemblyInheritance() {
        TypeExpr integer = TypeExpr.Builtin(TypeTag.Int32), baseType = TypeExpr.Named("inherit.Base", integer);
        ListLayout list = new(DurableFieldInfo.Reference(1, baseType));
        DurableSchema pair = new(TypeExpr.Named("inherit.HiddenPair", integer), 1, SchemaKind.InlineValue,
            new DurableFieldInfo(1, TypeTag.Int32), new DurableFieldInfo(2, TypeTag.Int32));
        DurableSchema point = new("inherit.HiddenPoint", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int32));
        DurableSchema @base = new(baseType, 1,
            new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: pair), DurableFieldInfo.Nullable(2, new(1, TypeTag.InlineValue, inlineSchema: point)),
            DurableFieldInfo.Reference(3, baseType), DurableFieldInfo.Reference(4, list.Type), new(5, TypeTag.Int32), new(6, TypeTag.Int32), new(7, TypeTag.Int32), new(8, TypeTag.Guid));
        DurableSchema middle = new(TypeExpr.Named("inherit.Middle", integer), 1, [new(1, TypeTag.Int32)], @base);
        DurableSchema local = new(TypeExpr.Named("inherit.Local", integer), 1, [new(1, TypeTag.Int32)], middle);
        DurableSchema leaf = new(TypeExpr.Named("inherit.Leaf", integer), 1, [new(1, TypeTag.Int32)], local);
        DurableSchema other = new(TypeExpr.Named("inherit.Other", integer), 1, [new(1, TypeTag.Int32)], middle);
        return ([leaf, other, local, middle, @base, pair, point], list);
    }

    private const string BootstrapCrossAssemblyInheritanceHost = """
        public static class Host {
            public static StateModelRegistry Models(bool reverse) {
                StateModelRegistry models = new();
                if (reverse) { Atelia.DurableGraph.Generated.DurableDefinitions.Register(models); InheritanceMiddle.Catalog.Register(models); InheritanceBases.Catalog.Register(models); }
                else { InheritanceBases.Catalog.Register(models); InheritanceMiddle.Catalog.Register(models); Atelia.DurableGraph.Generated.DurableDefinitions.Register(models); }
                return models;
            }
            public static IDurableObject Create() {
                Leaf<int> world = new(12345); Other<int> other = new(54321);
                world.Peer = world; other.Peer = world;
                world.Items.Add(world); world.Items.Add(other); world.Items.Add(world); other.Items = world.Items;
                return world;
            }
            public static object[] Objects(IDurableObject value) {
                Leaf<int> world = (Leaf<int>)value; return new object[] { world, world.Items[1], world.Items };
            }
            public static void Change(IDurableObject value) { Leaf<int> world = (Leaf<int>)value; world.Ancestor++; world.LeafValue++; }
            public static void Reset() { Trace.Constructors = Trace.Initializers = 0; }
            public static int[] Counters() => new[] { Trace.Constructors, Trace.Initializers };
            public static void Check(IDurableObject value, int stage, bool hydrated) {
                Leaf<int> world = (Leaf<int>)value; Other<int> other = (Other<int>)world.Items[1];
                if (world.GetType() != typeof(Leaf<int>) || other.GetType() != typeof(Other<int>) ||
                    world.Value != 12345 || !world.CheckBase(true, hydrated) || world.MiddleValue != 33333 || world.LocalValue != 44444 ||
                    hydrated && !world.LeafHydrated || other.Value != 54321 || !other.CheckBase(false, hydrated) || other.MiddleValue != 33333 ||
                    other.OtherValue != 55 || world.Ancestor != (stage == 0 ? 10 : 11) || world.LeafValue != (stage == 0 ? 44 : 45) || other.Ancestor != 10)
                    throw new InvalidOperationException("Incorrect hidden readonly base, middle, local or leaf state.");
                if (world.Items.Count != 3 || !ReferenceEquals(world, world.Peer) || !ReferenceEquals(world, other.Peer) ||
                    !ReferenceEquals(world, world.Items[0]) || !ReferenceEquals(world, world.Items[2]) || !ReferenceEquals(other.Items, world.Items))
                    throw new InvalidOperationException("Shared List polymorphism, aliases or ring changed.");
            }
        }
        """;
}
