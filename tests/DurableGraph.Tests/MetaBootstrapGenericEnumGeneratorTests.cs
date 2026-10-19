using Atelia.DurableGraph.Persistence.Tests;
using Atelia.DurableGraph.Persistence;

using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void MetaBootstrapClosedGenericEnumAndNullableSchemasColdReadAndContinueWithDistinctDescriptors() {
        GeneratorTestRun run = RunGenerator(BootstrapGenericEnumSource, [], null, "true");
        AssertSchemaOnlyCompiles(run);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("BootstrapGenericEnum.Host")!;
        var models = host.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>();
        var create = host.GetMethod("Create")!.CreateDelegate<Func<IDurableObject>>();
        var objects = host.GetMethod("Objects")!.CreateDelegate<Func<IDurableObject, IDurableObject[]>>();
        var change = host.GetMethod("Change")!.CreateDelegate<Action<IDurableObject>>();
        var check = host.GetMethod("Check")!.CreateDelegate<Action<IDurableObject, int>>();
        DurableSchema[] expected = ExpectedBootstrapGenericEnumSchemas(); // No business registration.

        using RawBaseDirectory files = new();
        FrameAddress s0, s1, s2, schemaRoot;
        uint rootId, intBoxId, enumBoxId;
        IDurableObject original = create();
        using (RepositoryStorageFacade repository = RepositoryStorageFacade.CreateNew(files.Path)) {
            StateModelRegistry registered = models();
            ModelExecutionEnvironment snapshot = registered.CreateEnvironment();
            IDurableObject[] initial = objects(original);
            for (int i = 0; i < initial.Length; i++) {
                Assert.Equal(expected[i], snapshot.ResolveCurrentModel(initial[i].GetType()).CurrentSchema);
            }
            BranchSaveSessionFixture session = new(repository, "main", registered);
            using PreparedFixtureSave save = session.Prepare(original); // All exact dependencies come from Capture.
            AssertBootstrapGenericEnumCatalog(save.Catalog.View, expected);
            Assert.Equal(3, save.Business.Write.Records.Count);
            Assert.All(save.Business.Write.Records, row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
            RepositorySaveResult result = save.Commit();
            s0 = result.Business.Address;
            schemaRoot = result.Meta!.Address;
            rootId = result.Business.RootId.Value;
            Assert.Equal(3, result.Business.ObjectHeads.Count); // Inline, enum and Nullable never acquire object IDs.
            Assert.Equal(0, session.Catalog.DirtyCount);
        }

        // Close every FS/VS/DG resource before decoding meta, with no business registry input.
        using (RepositoryStorageFacade repository = RepositoryStorageFacade.OpenReadOnlyExisting(files.Path)) {
            SchemaCatalogView meta = SchemaCatalogView.Load(repository.Store, schemaRoot);
            AssertBootstrapGenericEnumCatalog(meta, expected);
            Assert.NotSame(expected[0], meta.Schemas[new(expected[0].Type, 1)]);
            Assert.Equal(0, repository.Store.AppendedFrames);

            StateModelRegistry registered = models();
            ModelExecutionEnvironment snapshot = registered.CreateEnvironment(schemaLookup: key => meta.Schemas.GetValueOrDefault(key));
            DecodedGraphRows decoded = StoredGraphDecoder.Read(repository.Store, MechanismFixture.Snapshot(repository, s0), snapshot, meta);
            Assert.Equal(3, decoded.Rows.Count);
            Assert.Equal(expected[0], decoded.Rows[new(rootId)].Schema);
            intBoxId = Assert.Single(decoded.Rows.Values, row => row.Schema?.Type.Equals(expected[1].Type) == true).Id.Value;
            enumBoxId = Assert.Single(decoded.Rows.Values, row => row.Schema?.Type.Equals(expected[2].Type) == true).Id.Value;
            Assert.NotEqual(intBoxId, enumBoxId);
            Assert.Equal(expected[1], decoded.Rows[new(intBoxId)].Schema);
            Assert.Equal(expected[2], decoded.Rows[new(enumBoxId)].Schema);
            StoredGraphRevision graph = MechanismFixture.Snapshot(repository, s0);
            AssertBootstrapClosureDynamicDescriptor(repository, graph, rootId, ObjectLayout.ForDurable(expected[0]));
            AssertBootstrapClosureDynamicDescriptor(repository, graph, intBoxId, ObjectLayout.ForDurable(expected[1]));
            AssertBootstrapClosureDynamicDescriptor(repository, graph, enumBoxId, ObjectLayout.ForDurable(expected[2]));
            IDurableObject read = BusinessPersistenceAdapter.ReadRoot(repository.Store, MechanismFixture.Snapshot(repository, s0), registered);
            Assert.NotSame(original, read);
            check(read, 0); // Two different closed CLR types, aliasing and both self/owner rings.
            Assert.Equal(s0, MechanismFixture.Current(repository, "main").Address);
            Assert.Equal(schemaRoot, repository.SharedSchemaRoot);
            Assert.Equal(0, repository.Store.AppendedFrames);
        }

        using (RepositoryStorageFacade repository = RepositoryStorageFacade.OpenExisting(files.Path)) {
            StateModelRegistry registered = models();
            BranchSaveSessionFixture session = new(repository, "main", registered);
            IDurableObject restored = session.State!;
            check(restored, 0);
            IDurableObject[] restoredObjects = objects(restored);
            change(restored);
            check(restored, 1);
            using (PreparedFixtureSave save = session.Prepare(restored, parameters: new(1_000_000, 1))) {
                Assert.Null(save.Catalog.Write);
                Assert.Empty(save.Catalog.EncodedIds);
                Assert.Equal(0, save.Catalog.AddedDescriptors);
                Assert.Equal(s0, save.Business.Write.SourceGraph);
                Assert.Empty(save.Business.Write.RemovedIds);
                Assert.Equal(3, save.Business.Write.Records.Count);
                Assert.Equal(new[] { rootId, intBoxId, enumBoxId }.Order(), save.Business.Write.Records.Select(row => row.ObjectId));
                foreach (ObjectVersionRecord delta in save.Business.Write.Records) {
                    Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
                    Assert.Equal(session.StateSource!.ObjectHeads[delta.ObjectId], delta.PriorAddress);
                }
                RepositorySaveResult result = save.Commit();
                s1 = result.Business.Address;
                Assert.Equal(schemaRoot, result.Meta!.Address); // Selected meta is returned even when reused.
                Assert.Equal(schemaRoot, result.Business.SchemaRoot);
                Assert.Equal(schemaRoot, repository.SharedSchemaRoot);
                Assert.Equal(s0, result.Business.SourceGraph);
                Assert.Equal(s1, session.StateSource!.Address);
                IDurableObject[] after = objects(session.State!);
                for (int i = 0; i < after.Length; i++) { Assert.Same(restoredObjects[i], after[i]); }
                foreach (uint id in new[] { rootId, intBoxId, enumBoxId }) {
                    ObjectVersionChain chain = repository.Store.ReadObjectChain(MechanismFixture.Snapshot(repository, s1), id);
                    Assert.Equal(2, chain.Entries.Count);
                    Assert.Equal(ObjectVersionKind.Delta, chain.Entries[^1].Record.Kind);
                }
            }

            // Absent Nullable children must not cause phantom changes in generic state comparison.
            StoredGraphRevision accepted = session.StateSource!;
            long frames = repository.Store.AppendedFrames;
            using (PreparedFixtureSave unchanged = session.Prepare(restored)) {
                Assert.Null(unchanged.Catalog.Write);
                Assert.Empty(unchanged.Catalog.EncodedIds);
                Assert.Empty(unchanged.Business.Write.Records);
                Assert.Empty(unchanged.Business.Write.RemovedIds);
                RepositorySaveResult result = unchanged.Commit();
                s2 = result.Business.Address;
                Assert.Null(result.Business.OwnedBatch);
                Assert.Equal(accepted.DirectoryRoot, result.Business.DirectoryRoot);
                Assert.Equal(s1, result.Business.SourceGraph);
                Assert.Equal(schemaRoot, result.Business.SchemaRoot);
            }
            Assert.Equal(frames + 1, repository.Store.AppendedFrames);
            check(BusinessPersistenceAdapter.ReadRoot(repository.Store, MechanismFixture.Snapshot(repository, s0), registered), 0);
            check(BusinessPersistenceAdapter.ReadRoot(repository.Store, MechanismFixture.Snapshot(repository, s2), registered), 1);
        }

        using (RepositoryStorageFacade repository = RepositoryStorageFacade.OpenReadOnlyExisting(files.Path)) {
            SchemaCatalogView meta = SchemaCatalogView.Load(repository.Store, schemaRoot);
            AssertBootstrapGenericEnumCatalog(meta, expected);
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

    private static void AssertBootstrapGenericEnumCatalog(SchemaCatalogView meta, DurableSchema[] expected) {
        Assert.Equal(6, meta.Schemas.Count);
        Assert.Equal(6, meta.DescriptorIds.Count);
        foreach (DurableSchema schema in expected) {
            Assert.Equal(schema, meta.Schemas[new(schema.Type, schema.Version)]);
            Assert.Contains(meta.SchemaNodeId(schema), meta.DescriptorIds);
        }
        Assert.Equal(expected[1].SchemaId, expected[2].SchemaId);
        Assert.NotEqual(expected[1].Type, expected[2].Type);
        Assert.NotEqual(meta.DescriptorId(ObjectLayout.ForDurable(expected[1])), meta.DescriptorId(ObjectLayout.ForDurable(expected[2])));
        Assert.Equal(expected[3].SchemaId, expected[4].SchemaId);
        Assert.NotEqual(expected[3].Type, expected[4].Type);
        Assert.NotEqual(meta.SchemaNodeId(expected[3]), meta.SchemaNodeId(expected[4]));
        foreach (DurableSchema inline in expected.Skip(3)) {
            Assert.Equal(SchemaKind.InlineValue, inline.Kind);
            Assert.Throws<InvalidDataException>(() => meta.ResolveLayout(meta.SchemaNodeId(inline)));
        }
    }

    // Pure exact-schema factories also make closed argument identity visible without any CLR binding.
    private static DurableSchema[] ExpectedBootstrapGenericEnumSchemas() {
        TypeExpr rootType = TypeExpr.Named("p1.generic.root"), intType = TypeExpr.Builtin(TypeTag.Int32);
        DurableSchema grade = new("p1.generic.grade", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Byte));
        TypeExpr nullableGrade = TypeExpr.Nullable(grade.Type);
        DurableFieldInfo intValue = new(1, TypeTag.Int32);
        DurableFieldInfo gradeValue = DurableFieldInfo.Nullable(1, new(1, TypeTag.InlineValue, inlineSchema: grade));
        DurableSchema Cell(TypeExpr argument, DurableFieldInfo value) => new(TypeExpr.Named("p1.generic.cell", argument), 1, SchemaKind.InlineValue, [
            value, new(2, TypeTag.Double), new(3, TypeTag.Double), new(4, TypeTag.Double), new(5, TypeTag.Double),
        ]);
        DurableSchema Box(TypeExpr argument, DurableFieldInfo value) {
            TypeExpr type = TypeExpr.Named("p1.generic.box", argument);
            return new(type, 1, [value, DurableFieldInfo.Reference(2, type), DurableFieldInfo.Reference(3, rootType),
                new(4, TypeTag.Double), new(5, TypeTag.Double), new(6, TypeTag.Double), new(7, TypeTag.Double)]);
        }
        DurableSchema intBox = Box(intType, intValue), enumBox = Box(nullableGrade, gradeValue);
        DurableSchema intCell = Cell(intType, intValue), enumCell = Cell(nullableGrade, gradeValue);
        DurableSchema root = new(rootType, 1, [
            DurableFieldInfo.Reference(1, intBox.Type), DurableFieldInfo.Reference(2, enumBox.Type),
            DurableFieldInfo.Reference(3, intBox.Type), new(4, TypeTag.InlineValue, inlineSchema: intCell),
            new(5, TypeTag.InlineValue, inlineSchema: enumCell),
            DurableFieldInfo.Nullable(6, new(1, TypeTag.InlineValue, inlineSchema: enumCell)),
        ]);
        return [root, intBox, enumBox, intCell, enumCell, grade];
    }

    private const string BootstrapGenericEnumSource = """
        using System;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Persistence;
        namespace BootstrapGenericEnum;
        [DurableType("p1.generic.grade", 1)] public enum Grade : byte { Good = 7, Other = 9 }
        [DurableType("p1.generic.cell", 1)] public partial struct Cell<T> {
            [DurableField(1)] public T Value;
            [DurableField(2)] public double A;
            [DurableField(3)] public double B;
            [DurableField(4)] public double C;
            [DurableField(5)] public double D;
        }
        [DurableType("p1.generic.box", 1)] public sealed partial class Box<T> : IDurableObject {
            [DurableField(1)] public T Value = default!;
            [DurableField(2)] public Box<T>? Next;
            [DurableField(3)] public Root? Owner;
            [DurableField(4)] public double A;
            [DurableField(5)] public double B;
            [DurableField(6)] public double C;
            [DurableField(7)] public double D;
        }
        [DurableType("p1.generic.root", 1)] public sealed partial class Root : IDurableObject {
            [DurableField(1)] public Box<int> Numbers = null!;
            [DurableField(2)] public Box<Grade?> Choice = null!;
            [DurableField(3)] public Box<int> Alias = null!;
            [DurableField(4)] public Cell<int> IntCell;
            [DurableField(5)] public Cell<Grade?> GradeCell;
            [DurableField(6)] public Cell<Grade?>? OptionalCell;
        }
        public static class Host {
            public static StateModelRegistry Models() {
                StateModelRegistry models = new();
                Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
                return models;
            }
            private static Cell<T> Cell<T>(T value) => new() { Value = value, A = 1, B = 2, C = 3, D = 4 };
            private static Box<T> Box<T>(T value, Root owner) {
                Box<T> box = new() { Value = value, Owner = owner, A = 1, B = 2, C = 3, D = 4 };
                box.Next = box;
                return box;
            }
            public static IDurableObject Create() {
                Root root = new() { IntCell = Cell(29), GradeCell = Cell<Grade?>(Grade.Good), OptionalCell = Cell<Grade?>(Grade.Other) };
                root.Numbers = Box(17, root);
                root.Alias = root.Numbers;
                root.Choice = Box<Grade?>(null, root);
                return root;
            }
            public static IDurableObject[] Objects(IDurableObject value) {
                Root root = (Root)value;
                return [root, root.Numbers, root.Choice];
            }
            public static void Change(IDurableObject value) {
                Root root = (Root)value;
                root.Numbers.Value = 18;
                root.Choice.Value = (Grade)233; // A legal unnamed enum bit pattern, not a declared constant.
                root.IntCell.Value = 30;
                root.GradeCell.Value = null;
                root.OptionalCell = null;
            }
            private static void Pads(double a, double b, double c, double d) {
                if (a != 1 || b != 2 || c != 3 || d != 4) throw new InvalidOperationException("Stable padding state mismatch.");
            }
            public static void Check(IDurableObject value, int stage) {
                Root root = (Root)value;
                if (!ReferenceEquals(root.Numbers, root.Alias) || ReferenceEquals(root.Numbers, root.Choice) ||
                    !ReferenceEquals(root.Numbers.Next, root.Numbers) || !ReferenceEquals(root.Choice.Next, root.Choice) ||
                    !ReferenceEquals(root.Numbers.Owner, root) || !ReferenceEquals(root.Choice.Owner, root)) {
                    throw new InvalidOperationException("Closed generic identity or nominal ring mismatch.");
                }
                if (root.Numbers.Value != (stage == 0 ? 17 : 18) || root.IntCell.Value != (stage == 0 ? 29 : 30)) {
                    throw new InvalidOperationException("Closed int value mismatch.");
                }
                if (stage == 0 ? root.Choice.Value is not null : root.Choice.Value != (Grade)233) {
                    throw new InvalidOperationException("Nullable enum value/bits mismatch.");
                }
                if (stage == 0 ? root.GradeCell.Value != Grade.Good : root.GradeCell.Value is not null) {
                    throw new InvalidOperationException("Generic inline Nullable enum mismatch.");
                }
                if (stage == 0 ? root.OptionalCell is not { Value: Grade.Other } : root.OptionalCell is not null) {
                    throw new InvalidOperationException("Whole nullable generic inline mismatch.");
                }
                Pads(root.Numbers.A, root.Numbers.B, root.Numbers.C, root.Numbers.D);
                Pads(root.Choice.A, root.Choice.B, root.Choice.C, root.Choice.D);
                Pads(root.IntCell.A, root.IntCell.B, root.IntCell.C, root.IntCell.D);
                Pads(root.GradeCell.A, root.GradeCell.B, root.GradeCell.C, root.GradeCell.D);
                if (root.OptionalCell is { } optional) { Pads(optional.A, optional.B, optional.C, optional.D); }
            }
        }
        """;
}
