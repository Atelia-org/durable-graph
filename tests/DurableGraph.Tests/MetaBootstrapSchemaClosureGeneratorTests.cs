using Atelia.DurableGraph.Persistence.Tests;
using Atelia.Binary;
using Atelia.DurableGraph.Persistence;

using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void MetaBootstrapGeneratedBaseInlineNullableAndListClosureColdLoadsWithoutBusinessRegistration() {
        GeneratorTestRun run = RunGenerator(BootstrapSchemaClosureSource, [], null, "true");
        AssertSchemaOnlyCompiles(run);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("BootstrapSchemaClosure.Host")!;
        var models = host.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>();
        var create = host.GetMethod("Create")!.CreateDelegate<Func<IDurableObject>>();
        var change = host.GetMethod("Change")!.CreateDelegate<Action<IDurableObject>>();
        var check = host.GetMethod("Check")!.CreateDelegate<Action<IDurableObject, int>>();
        var samples = host.GetMethod("Samples")!.CreateDelegate<Func<IDurableObject, object>>();
        var expected = ExpectedBootstrapSchemaClosure(); // Pure immutable factories, no model registration.

        using RawBaseDirectory files = new();
        FrameAddress s0, s1, schemaRoot, listHead;
        uint rootId, listId;
        IDurableObject original = create();
        using (RepositoryStorageFacade repository = RepositoryStorageFacade.CreateNew(files.Path)) {
            StateModelRegistry registered = models();
            ModelExecutionEnvironment snapshot = registered.CreateEnvironment();
            Assert.Equal(expected.Root, snapshot.ResolveCurrentModel(original.GetType()).CurrentSchema);
            Assert.True(snapshot.TryGetCurrentObjectBinding(samples(original).GetType(), out ObjectBinding? list));
            Assert.Equal(expected.List, list!.CurrentLayout.List);

            BranchSaveSessionFixture session = new(repository, "main", registered);
            Assert.Null(session.Catalog.Accepted);
            Assert.Equal(0, session.Catalog.DirtyCount);
            using PreparedFixtureSave save = session.Prepare(original); // No manual Set of any dependency.
            AssertBootstrapSchemaClosure(save.Catalog.View, expected.Root, expected.Base, expected.Point);
            Assert.Equal(2, save.Business.Write.Records.Count);
            Assert.All(save.Business.Write.Records, row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
            RepositorySaveResult result = save.Commit();
            s0 = result.Business.Address;
            schemaRoot = result.Meta!.Address;
            rootId = result.Business.RootId.Value;
            listId = Assert.Single(result.Business.ObjectHeads.Keys, id => id != rootId);
            listHead = result.Business.ObjectHeads[listId];
            Assert.Equal(2, result.Business.ObjectHeads.Count); // Base, inline and Nullable have no object rows.
            Assert.Equal(schemaRoot, repository.SharedSchemaRoot);
            Assert.Equal(schemaRoot, result.Business.SchemaRoot);
            AssertBootstrapClosureDynamicDescriptor(repository, result.Business, rootId, ObjectLayout.ForDurable(expected.Root));
            AssertBootstrapClosureDynamicDescriptor(repository, result.Business, listId, ObjectLayout.ForList(expected.List));
        }

        // Complete FS/VS lifetime separation. Meta decoding receives no business model registry.
        using (RepositoryStorageFacade repository = RepositoryStorageFacade.OpenReadOnlyExisting(files.Path)) {
            SchemaCatalogView meta = SchemaCatalogView.Load(repository.Store, schemaRoot);
            AssertBootstrapSchemaClosure(meta, expected.Root, expected.Base, expected.Point);
            Assert.NotSame(expected.Root, meta.Schemas[new(expected.Root.Type, expected.Root.Version)]);
            Assert.Equal(0, repository.Store.AppendedFrames);

            StateModelRegistry registered = models();
            ModelExecutionEnvironment snapshot = registered.CreateEnvironment(schemaLookup: key => meta.Schemas.GetValueOrDefault(key));
            DecodedGraphRows decoded = StoredGraphDecoder.Read(repository.Store, MechanismFixture.Snapshot(repository, s0), snapshot, meta);
            Assert.Equal(2, decoded.Rows.Count);
            Assert.Equal(expected.Root, decoded.Rows[new(rootId)].Schema);
            Assert.Equal(ObjectLayout.ForList(expected.List), decoded.Rows[new(listId)].Layout);
            IDurableObject read = BusinessPersistenceAdapter.ReadRoot(repository.Store, MechanismFixture.Snapshot(repository, s0), registered);
            Assert.NotSame(original, read);
            check(read, 0);
            Assert.Equal(s0, MechanismFixture.Current(repository, "main").Address);
            Assert.Equal(schemaRoot, repository.SharedSchemaRoot);
            Assert.Equal(0, repository.Store.AppendedFrames);
        }

        using (RepositoryStorageFacade repository = RepositoryStorageFacade.OpenExisting(files.Path)) {
            StateModelRegistry registered = models();
            BranchSaveSessionFixture session = new(repository, "main", registered);
            IDurableObject restored = session.State!;
            Assert.NotSame(original, restored);
            check(restored, 0);
            object restoredList = samples(restored);
            change(restored);
            check(restored, 1);
            using PreparedFixtureSave save = session.Prepare(restored, parameters: new(1_000_000, 1));
            Assert.Null(save.Catalog.Write);
            Assert.Equal(0, save.Catalog.AddedDescriptors);
            Assert.Empty(save.Catalog.EncodedIds);
            Assert.Equal(s0, save.Business.Write.SourceGraph);
            Assert.Empty(save.Business.Write.RemovedIds);
            Assert.Equal(2, save.Business.Write.Records.Count);
            ObjectVersionRecord rootDelta = Assert.Single(save.Business.Write.Records, row => row.ObjectId == rootId);
            ObjectVersionRecord listDelta = Assert.Single(save.Business.Write.Records, row => row.ObjectId == listId);
            Assert.Equal(ObjectVersionKind.Delta, rootDelta.Kind);
            Assert.Equal(ObjectVersionKind.Delta, listDelta.Kind);
            Assert.Equal(session.StateSource!.ObjectHeads[rootId], rootDelta.PriorAddress);
            Assert.Equal(listHead, listDelta.PriorAddress);
            RepositorySaveResult result = save.Commit();
            s1 = result.Business.Address;
            Assert.Equal(schemaRoot, result.Meta!.Address); // Commit returns the selected meta, including reuse.
            Assert.Equal(schemaRoot, session.Catalog.Accepted!.Snapshot!.Address);
            Assert.Equal(schemaRoot, result.Business.SchemaRoot);
            Assert.Equal(schemaRoot, repository.SharedSchemaRoot);
            Assert.Same(restored, session.State);
            Assert.Same(restoredList, samples(session.State!));
            Assert.Equal(s1, session.StateSource.Address);
            Assert.Equal(ObjectVersionKind.Delta, repository.Store.ReadObjectChain(MechanismFixture.Snapshot(repository, s1), rootId).Entries[^1].Record.Kind);
            Assert.Equal(ObjectVersionKind.Delta, repository.Store.ReadObjectChain(MechanismFixture.Snapshot(repository, s1), listId).Entries[^1].Record.Kind);
            check(BusinessPersistenceAdapter.ReadRoot(repository.Store, MechanismFixture.Snapshot(repository, s0), registered), 0);
            check(BusinessPersistenceAdapter.ReadRoot(repository.Store, MechanismFixture.Snapshot(repository, s1), registered), 1);
        }

        using (RepositoryStorageFacade repository = RepositoryStorageFacade.OpenReadOnlyExisting(files.Path)) {
            SchemaCatalogView meta = SchemaCatalogView.Load(repository.Store, schemaRoot);
            AssertBootstrapSchemaClosure(meta, expected.Root, expected.Base, expected.Point);
            Assert.Equal(s1, MechanismFixture.State(repository, "main")?.Address);
            Assert.Equal(s0, MechanismFixture.Snapshot(repository, s1).SourceGraph);
            Assert.Equal(schemaRoot, MechanismFixture.Snapshot(repository, s0).SchemaRoot);
            StateModelRegistry registered = models();
            check(BusinessPersistenceAdapter.ReadRoot(repository.Store, MechanismFixture.Snapshot(repository, s0), registered), 0);
            check(BusinessPersistenceAdapter.ReadRoot(repository.Store, MechanismFixture.Snapshot(repository, s1), registered), 1);
            Assert.Equal(0, repository.Store.AppendedFrames);
        }
    }

    private static void AssertBootstrapSchemaClosure(SchemaCatalogView meta,
        DurableSchema root, DurableSchema @base, DurableSchema point) {
        Assert.Equal(3, meta.Schemas.Count);
        Assert.Equal(root, meta.Schemas[new(root.Type, root.Version)]);
        Assert.Equal(@base, meta.Schemas[new(@base.Type, @base.Version)]);
        Assert.Equal(point, meta.Schemas[new(point.Type, point.Version)]);
        Assert.Equal(SchemaKind.ReferenceObject, meta.Schemas[new(root.Type, root.Version)].Kind);
        Assert.Equal(SchemaKind.InlineValue, meta.Schemas[new(point.Type, point.Version)].Kind);
        Assert.Equal(4, meta.DescriptorIds.Count); // Root, Base, Point and the composite List.
        ObjectId rootId = meta.DescriptorId(ObjectLayout.ForDurable(root));
        ObjectId baseId = meta.DescriptorId(ObjectLayout.ForDurable(@base));
        ListLayout list = new(DurableFieldInfo.Nullable(1, new(1, TypeTag.InlineValue, inlineSchema: point)));
        ObjectId listId = meta.DescriptorId(ObjectLayout.ForList(list));
        ObjectId inlineId = Assert.Single(meta.DescriptorIds, id => id != rootId && id != baseId && id != listId);
        Assert.Throws<InvalidDataException>(() => meta.ResolveLayout(inlineId));
    }

    private static void AssertBootstrapClosureDynamicDescriptor(RepositoryStorageFacade repository, StoredGraphRevision graph,
        uint objectId, ObjectLayout expected) {
        ObjectVersionChain chain = repository.Store.ReadObjectChain(graph, objectId);
        ObjectVersionChainEntry first = chain.Entries[0];
        Assert.Equal(ObjectVersionKind.Base, first.Record.Kind);
        BareValueReader reader = new(first.Record.Body);
        Assert.Equal(TypedBaseBodyCodec.FormatVersion, reader.ReadByte());
        Assert.Equal((byte)1, reader.ReadByte()); // Composite List and Root both name actual meta ObjectIds.
        ObjectId descriptor = new(reader.ReadVarUInt32());
        SchemaCatalogView meta = SchemaCatalogView.Load(repository.Store, first.SchemaRoot!.Value);
        Assert.Contains(descriptor, meta.DescriptorIds);
        Assert.Equal(meta.DescriptorId(expected), descriptor);
        Assert.Equal(expected, meta.ResolveLayout(descriptor));
    }

    // The package consumer can use these factories before registering any business code.
    private static (DurableSchema Root, DurableSchema Base, DurableSchema Point, ListLayout List) ExpectedBootstrapSchemaClosure() {
        DurableSchema point = new("p1.closure.point", 1, SchemaKind.InlineValue,
            new DurableFieldInfo(1, TypeTag.Int32), DurableFieldInfo.Nullable(2, new(1, TypeTag.Int32)));
        DurableSchema @base = new("p1.closure.base", 1, [
            new(1, TypeTag.Int32), new(2, TypeTag.Double), new(3, TypeTag.Double),
            new(4, TypeTag.Double), new(5, TypeTag.Double),
        ]);
        DurableFieldInfo optionalPoint = DurableFieldInfo.Nullable(1, new(1, TypeTag.InlineValue, inlineSchema: point));
        ListLayout list = new(optionalPoint);
        DurableSchema root = new("p1.closure.root", 1, [
            new(1, TypeTag.InlineValue, inlineSchema: point),
            DurableFieldInfo.Nullable(2, new(1, TypeTag.InlineValue, inlineSchema: point)),
            DurableFieldInfo.Nullable(3, new(1, TypeTag.Int32)),
            DurableFieldInfo.Reference(4, list.Type),
        ], @base);
        return (root, @base, point, list);
    }

    private const string BootstrapSchemaClosureSource = """
        using System;
        using System.Collections.Generic;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Persistence;
        namespace BootstrapSchemaClosure;
        [DurableType("p1.closure.base", 1)]
        public abstract partial class Base : IDurableObject {
            [DurableField(1)] public int Seed;
            [DurableField(2)] public double A;
            [DurableField(3)] public double B;
            [DurableField(4)] public double C;
            [DurableField(5)] public double D;
        }
        [DurableType("p1.closure.point", 1)]
        public partial struct Point {
            [DurableField(1)] public int X;
            [DurableField(2)] public int? Rank;
        }
        [DurableType("p1.closure.root", 1)]
        public sealed partial class Root : Base {
            [DurableField(1)] public Point Position;
            [DurableField(2)] public Point? OptionalPoint;
            [DurableField(3)] public int? Count;
            [DurableField(4)] public List<Point?> Samples = new();
        }
        public static class Host {
            public static StateModelRegistry Models() {
                StateModelRegistry models = new();
                Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
                return models;
            }
            public static IDurableObject Create() {
                Root root = new() { Seed = 17, A = 1, B = 2, C = 3, D = 4,
                    Position = new() { X = 7, Rank = 11 }, OptionalPoint = new Point { X = 13, Rank = null } };
                for (int i = 0; i < 128; i++) {
                    root.Samples.Add(i % 7 == 0 ? null : new Point { X = 1000 + i, Rank = i % 3 == 0 ? null : i });
                }
                return root;
            }
            public static object Samples(IDurableObject value) => ((Root)value).Samples;
            public static void Change(IDurableObject value) {
                Root root = (Root)value;
                root.Seed = 18;
                root.Position = new() { X = 8, Rank = null };
                root.OptionalPoint = null;
                root.Count = 99;
                root.Samples[31] = new Point { X = 7031, Rank = null };
            }
            public static void Check(IDurableObject value, int stage) {
                Root root = (Root)value;
                if (root.Seed != (stage == 0 ? 17 : 18) || root.A != 1 || root.B != 2 || root.C != 3 || root.D != 4 ||
                    root.Position.X != (stage == 0 ? 7 : 8) || root.Position.Rank != (stage == 0 ? 11 : (int?)null) ||
                    root.Count != (stage == 0 ? (int?)null : 99)) throw new InvalidOperationException("Root/base/inline state mismatch.");
                if (stage == 0 ? root.OptionalPoint is not { X: 13, Rank: null } : root.OptionalPoint is not null) {
                    throw new InvalidOperationException("Nullable inline state mismatch.");
                }
                if (root.Samples.Count != 128) throw new InvalidOperationException("List count mismatch.");
                for (int i = 0; i < 128; i++) {
                    Point? sample = root.Samples[i];
                    if (i % 7 == 0) {
                        if (sample is not null) throw new InvalidOperationException("Null List element mismatch.");
                    } else {
                        int x = stage == 1 && i == 31 ? 7031 : 1000 + i;
                        int? rank = i % 3 == 0 || stage == 1 && i == 31 ? null : i;
                        if (sample is not { } present || present.X != x || present.Rank != rank) {
                            throw new InvalidOperationException("Nullable inline List element mismatch.");
                        }
                    }
                }
            }
        }
        """;
}
