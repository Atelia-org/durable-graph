using System.Security.Cryptography;
using Atelia.DurableGraph.Build;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Atelia.VersionStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void MetaBootstrapAcceptedContainerHistoryUpgradesExactValuesAndPreservesRewriteUntilState() {
        using AncestryHistoryDirectory history = new();
        SchemaHistoryTool publisher = new();
        GeneratorTestRun v1 = RunGenerator(BootstrapContainerUpgradeSource(1), [], null, "true");
        AssertSchemaOnlyCompiles(v1);
        BootstrapContainerUpgradeHost oldHost = BootstrapContainerUpgradeHost.Load(v1);
        publisher.Publish(history.WriteManifest(v1), history.History);
        Dictionary<string, string> acceptedV1 = history.ReadContents();
        Assert.Equal(4, acceptedV1.Count);
        GeneratorTestRun v2 = RunGenerator(BootstrapContainerUpgradeSource(2), history.ReadAdditionalTexts(), null, "true");
        AssertSchemaOnlyCompiles(v2);
        BootstrapContainerUpgradeHost currentHost = BootstrapContainerUpgradeHost.Load(v2);
        publisher.Publish(history.WriteManifest(v2), history.History);
        publisher.Verify(history.WriteManifest(v2), history.History);
        Assert.Equal(7, history.ReadContents().Count); // Root stays V1: all changed dependencies are nominal references.
        foreach ((string name, string content) in acceptedV1) {
            Assert.Equal(content, File.ReadAllText(Path.Combine(history.History, name)));
        }
        Assert.NotSame(oldHost.ClrType, currentHost.ClrType);
        Assert.Equal(typeof(int), oldHost.ClrType.Assembly.GetType("BootstrapContainerUpgrade.Point")!.GetField("X")!.FieldType);
        Assert.Equal(typeof(long), currentHost.ClrType.Assembly.GetType("BootstrapContainerUpgrade.Point")!.GetField("X")!.FieldType);
        Assert.Equal(typeof(byte), Enum.GetUnderlyingType(oldHost.ClrType.Assembly.GetType("BootstrapContainerUpgrade.Grade")!));
        Assert.Equal(typeof(ushort), Enum.GetUnderlyingType(currentHost.ClrType.Assembly.GetType("BootstrapContainerUpgrade.Grade")!));
        BootstrapContainerUpgradeExpected old = ExpectedBootstrapContainerUpgrade(1);
        BootstrapContainerUpgradeExpected current = ExpectedBootstrapContainerUpgrade(2);
        Assert.Equal(old.Layouts[0], current.Layouts[0]);

        using RawBaseDirectory files = new();
        FrameAddress s0, meta0, e1, e2, s1, s2, s3, meta1;
        uint[] ids;
        using (RepositoryStorageFacade repository = RepositoryStorageFacade.CreateNew(files.Path)) {
            StateModelRegistry models = oldHost.Models();
            IDurableObject root = oldHost.Create();
            oldHost.Check(root, 0);
            BranchSaveSession session = new(repository, "main", models);
            using PreparedBranchSave save = session.Prepare(root); // Actual V1 capture supplies the full declaration closure.
            AssertBootstrapContainerUpgradeCatalog(save.Catalog.View, old);
            Assert.Equal(4, save.Business.Write.Records.Count);
            Assert.All(save.Business.Write.Records, row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
            RepositorySaveResult result = save.Commit();
            s0 = result.Business.Address;
            meta0 = result.Meta!.Address;
            DecodedGraphRows rows = ReadBootstrapContainerUpgradeRows(repository, s0, models, session.Catalog.Accepted!);
            ids = old.Layouts.Select(layout => Assert.Single(rows.Rows.Values, row => row.Layout.Equals(layout)).Id.Value).ToArray();
            Assert.Equal(result.Business.RootId.Value, ids[0]);
            oldHost.CheckStored(rows.Rows.Values.ToArray(), 1, 0);
            AssertBootstrapContainerUpgradeHeads(repository, result.Business, ids, old.Layouts, ObjectVersionKind.Base);
        }

        Dictionary<string, string> readOnlyFiles = BootstrapContainerUpgradeFileHashes(files.Path);
        using (RepositoryStorageFacade repository = RepositoryStorageFacade.OpenReadOnlyExisting(files.Path)) {
            // Load employee metadata before constructing any business model snapshot.
            SchemaCatalogView catalog = SchemaCatalogView.Load(repository.Store, meta0);
            AssertBootstrapContainerUpgradeCatalog(catalog, old);
            var branches = repository.Versions.ReadRef(1).Unwrap();
            var shared = repository.Versions.ReadRef(2).Unwrap();
            StateModelRegistry models = currentHost.Models();
            currentHost.ResetCalls();
            DecodedGraphRows stored = ReadBootstrapContainerUpgradeRows(repository, s0, models, catalog);
            // Current assembly supplies historical DTO/body readers; decoding does not Normalize.
            currentHost.CheckStored(stored.Rows.Values.ToArray(), 1, 0);
            Assert.Equal(0, currentHost.CallCount());
            IDurableObject restored = BusinessPersistenceAdapter.ReadRoot(repository.Store, s0, models);
            currentHost.Check(restored, 1);
            currentHost.CheckUpgradeFacts(ids);
            Assert.Equal(s0, repository.GetHead("main"));
            Assert.Equal(meta0, repository.SharedSchemaRoot);
            AssertBootstrapContainerUpgradeCatalog(SchemaCatalogView.Load(repository.Store, meta0), old);
            Assert.Equal(branches.Revision, repository.Versions.ReadRef(1).Unwrap().Revision);
            Assert.Equal(shared.Revision, repository.Versions.ReadRef(2).Unwrap().Revision);
            Assert.Equal(branches.Payload.ToArray(), repository.Versions.ReadRef(1).Unwrap().Payload.ToArray());
            Assert.Equal(shared.Payload.ToArray(), repository.Versions.ReadRef(2).Unwrap().Payload.ToArray());
            Assert.Equal(0, repository.Store.AppendedFrames);
        }
        AssertBootstrapContainerUpgradeFilesUnchanged(readOnlyFiles, files.Path);

        using (RepositoryStorageFacade repository = RepositoryStorageFacade.OpenExisting(files.Path)) {
            StateModelRegistry models = currentHost.Models();
            BranchSaveSession session = new(repository, "main", models);
            IDurableObject root = session.State!;
            currentHost.Check(root, 1);
            object[] identities = currentHost.Objects(root);
            StoredGraphRevision source = session.StateSource!;
            Assert.Equal(s0, source.Address);
            AssertBootstrapContainerUpgradeCatalog(session.Catalog.Accepted!, old);
            long frames = repository.Store.AppendedFrames;
            using (PreparedBranchSave discarded = session.Prepare(root, StoredGraphKind.Event)) {
                Assert.NotNull(discarded.Catalog.Write);
                AssertBootstrapContainerUpgradeCatalog(discarded.Catalog.View, old, current);
                AssertBootstrapContainerUpgradeWrites(discarded, ids, s0, ObjectVersionKind.Base);
            }
            Assert.Equal(frames, repository.Store.AppendedFrames);
            Assert.Equal(s0, repository.GetHead("main"));
            Assert.Equal(meta0, repository.SharedSchemaRoot);
            AssertBootstrapContainerUpgradeCatalog(session.Catalog.Accepted!, old);
            Assert.Equal(0, session.Catalog.DirtyCount);
            Assert.Same(root, session.State);
            Assert.Equal(s0, session.StateSource!.Address);

            using (PreparedBranchSave save = session.Prepare(root, StoredGraphKind.Event)) {
                Assert.NotNull(save.Catalog.Write);
                AssertBootstrapContainerUpgradeWrites(save, ids, s0, ObjectVersionKind.Base);
                RepositorySaveResult result = save.Commit();
                e1 = result.Business.Address;
                meta1 = result.Meta!.Address;
                Assert.NotEqual(meta0, meta1);
                Assert.Equal(meta1, result.Business.SchemaRoot);
                Assert.Equal(source.ObjectHeads[ids[0]], result.Business.ObjectHeads[ids[0]]);
                AssertBootstrapContainerUpgradeHeads(repository, result.Business, ids.Skip(1).ToArray(), current.Layouts.Skip(1).ToArray(), ObjectVersionKind.Base);
            }
            AssertBootstrapContainerUpgradeCatalog(session.Catalog.Accepted!, old, current);
            Assert.Equal(e1, repository.GetHead("main"));
            Assert.Equal(s0, repository.GetStateHead("main"));
            Assert.Equal(meta1, repository.SharedSchemaRoot);
            Assert.Equal(meta1, session.Catalog.Accepted!.Snapshot!.Address);
            AssertBootstrapContainerUpgradePublishedRefs(repository, e1, meta1);
            for (int i = 1; i < old.Layouts.Length; i++) {
                Assert.NotEqual(session.Catalog.Accepted!.DescriptorId(old.Layouts[i]), session.Catalog.Accepted.DescriptorId(current.Layouts[i]));
            }
            Assert.Equal(0, session.Catalog.DirtyCount);
            Assert.Equal(s0, session.StateSource.Address);
            Assert.Same(root, session.State);

            // The identical second Event must still write three target Bases relative to S0.
            using (PreparedBranchSave save = session.Prepare(root, StoredGraphKind.Event)) {
                Assert.Null(save.Catalog.Write);
                AssertBootstrapContainerUpgradeWrites(save, ids, s0, ObjectVersionKind.Base);
                RepositorySaveResult result = save.Commit();
                e2 = result.Business.Address;
                Assert.Equal(e1, result.Business.Parent);
                Assert.Equal(meta1, result.Meta!.Address);
                Assert.Equal(source.ObjectHeads[ids[0]], result.Business.ObjectHeads[ids[0]]);
                currentHost.Check(BusinessPersistenceAdapter.ReadRoot(repository.Store, e2, models), 1);
            }
            Assert.Equal(e2, repository.GetHead("main"));
            Assert.Equal(s0, repository.GetStateHead("main"));
            Assert.Equal(meta1, repository.SharedSchemaRoot);
            Assert.Equal(meta1, session.Catalog.Accepted!.Snapshot!.Address);
            AssertBootstrapContainerUpgradePublishedRefs(repository, e2, meta1);
            Assert.Equal(s0, session.StateSource.Address);

            using (PreparedBranchSave save = session.Prepare(root)) {
                Assert.Null(save.Catalog.Write);
                AssertBootstrapContainerUpgradeWrites(save, ids, s0, ObjectVersionKind.Base);
                RepositorySaveResult result = save.Commit();
                s1 = result.Business.Address;
                Assert.Equal(e2, result.Business.Parent);
                Assert.Equal(source.ObjectHeads[ids[0]], result.Business.ObjectHeads[ids[0]]);
                AssertBootstrapContainerUpgradeHeads(repository, result.Business, ids.Skip(1).ToArray(), current.Layouts.Skip(1).ToArray(), ObjectVersionKind.Base);
            }
            Assert.Equal(s1, session.StateSource.Address);
            currentHost.CheckStored(ReadBootstrapContainerUpgradeRows(repository, s1, models, session.Catalog.Accepted!).Rows.Values.ToArray(), 2, 1);

            StoredGraphRevision accepted = session.StateSource;
            currentHost.Change(root); // Each container gets absent->present, present->absent and a numeric patch.
            currentHost.Check(root, 2);
            using (PreparedBranchSave save = session.Prepare(root)) {
                Assert.Null(save.Catalog.Write);
                Assert.Empty(save.Catalog.EncodedIds);
                AssertBootstrapContainerUpgradeWrites(save, ids, s1, ObjectVersionKind.Delta);
                foreach (ObjectVersionRecord delta in save.Business.Write.Records) {
                    Assert.Equal(accepted.ObjectHeads[delta.ObjectId], delta.PriorAddress);
                    Assert.True(delta.Body.Length < 128); // Full nullable contents need at least 128 presence bytes.
                }
                RepositorySaveResult result = save.Commit();
                s2 = result.Business.Address;
                Assert.Equal(meta1, result.Meta!.Address);
                Assert.Equal(source.ObjectHeads[ids[0]], result.Business.ObjectHeads[ids[0]]);
                AssertBootstrapContainerUpgradeHeads(repository, result.Business, ids.Skip(1).ToArray(), current.Layouts.Skip(1).ToArray(), ObjectVersionKind.Delta);
                foreach (uint id in ids.Skip(1)) {
                    ObjectVersionChain chain = repository.Store.ReadObjectChain(s2, id);
                    Assert.Equal(2, chain.Entries.Count); // New target Base severs the old representation chain.
                    Assert.Equal(meta1, chain.Entries[0].SchemaRoot);
                }
            }
            object[] after = currentHost.Objects(session.State!);
            for (int i = 0; i < after.Length; i++) { Assert.Same(identities[i], after[i]); }
            currentHost.CheckStored(ReadBootstrapContainerUpgradeRows(repository, s2, models, session.Catalog.Accepted!).Rows.Values.ToArray(), 2, 2);
            accepted = session.StateSource!;
            frames = repository.Store.AppendedFrames;
            using (PreparedBranchSave save = session.Prepare(root)) {
                Assert.Null(save.Catalog.Write);
                Assert.Empty(save.Business.Write.Records);
                Assert.Empty(save.Business.Write.RemovedIds);
                Assert.Equal(s2, save.Business.Write.SourceGraph);
                RepositorySaveResult result = save.Commit();
                s3 = result.Business.Address;
                Assert.Null(result.Business.OwnedBatch);
                Assert.Equal(accepted.DirectoryRoot, result.Business.DirectoryRoot);
                foreach (uint id in ids) { Assert.Equal(accepted.ObjectHeads[id], result.Business.ObjectHeads[id]); }
            }
            Assert.Equal(frames + 1, repository.Store.AppendedFrames);
        }

        readOnlyFiles = BootstrapContainerUpgradeFileHashes(files.Path);
        using (RepositoryStorageFacade repository = RepositoryStorageFacade.OpenReadOnlyExisting(files.Path)) {
            SchemaCatalogView catalog = SchemaCatalogView.Load(repository.Store, meta1);
            AssertBootstrapContainerUpgradeCatalog(catalog, old, current);
            AssertBootstrapContainerUpgradeCatalog(SchemaCatalogView.Load(repository.Store, meta0), old);
            Assert.Equal(s3, repository.GetHead("main"));
            Assert.Equal(s3, repository.GetStateHead("main"));
            Assert.Equal(meta1, repository.SharedSchemaRoot);
            AssertBootstrapContainerUpgradePublishedRefs(repository, s3, meta1);
            Assert.Equal(meta0, repository.Store.ReadGraph(s0).SchemaRoot);
            StateModelRegistry models = currentHost.Models();
            currentHost.CheckStored(ReadBootstrapContainerUpgradeRows(repository, s0, models, SchemaCatalogView.Load(repository.Store, meta0)).Rows.Values.ToArray(), 1, 0);
            currentHost.CheckStored(ReadBootstrapContainerUpgradeRows(repository, s2, models, catalog).Rows.Values.ToArray(), 2, 2);
            oldHost.Check(BusinessPersistenceAdapter.ReadRoot(repository.Store, s0, oldHost.Models()), 0);
            foreach (FrameAddress graph in new[] { s0, e1, e2, s1 }) {
                currentHost.Check(BusinessPersistenceAdapter.ReadRoot(repository.Store, graph, models), 1);
            }
            currentHost.Check(BusinessPersistenceAdapter.ReadRoot(repository.Store, s2, models), 2);
            currentHost.Check(BusinessPersistenceAdapter.ReadRoot(repository.Store, s3, models), 2);
            AssertBootstrapContainerUpgradeHeads(repository, repository.Store.ReadGraph(s0), ids, old.Layouts, ObjectVersionKind.Base);
            Assert.Equal(0, repository.Store.AppendedFrames);
        }
        AssertBootstrapContainerUpgradeFilesUnchanged(readOnlyFiles, files.Path);
    }

    private static DecodedGraphRows ReadBootstrapContainerUpgradeRows(RepositoryStorageFacade repository, FrameAddress graph,
        StateModelRegistry models, SchemaCatalogView selected) => StoredGraphDecoder.Read(repository.Store, graph,
        models.CreateEnvironment(schemaLookup: key => selected.Schemas.GetValueOrDefault(key)), selected);

    private static void AssertBootstrapContainerUpgradePublishedRefs(RepositoryStorageFacade repository, FrameAddress graph, FrameAddress meta) {
        byte[] branches = repository.Versions.ReadRef(1).Unwrap().Payload.ToArray();
        byte[] shared = repository.Versions.ReadRef(2).Unwrap().Payload.ToArray();
        Assert.True(branches.AsSpan().StartsWith("P0R1"u8));
        Assert.True(shared.AsSpan().StartsWith("P0S1"u8));
        Assert.Equal(graph, RootMapCodec.Decode(branches.AsSpan(4)).Unwrap()["branch/main"]);
        Assert.Equal(meta, RootMapCodec.Decode(shared.AsSpan(4)).Unwrap()["schema"]);
    }

    private static void AssertBootstrapContainerUpgradeWrites(PreparedBranchSave save, uint[] ids, FrameAddress source, ObjectVersionKind kind) {
        Assert.Equal(source, save.Business.Write.SourceGraph);
        Assert.Empty(save.Business.Write.RemovedIds);
        Assert.Equal<uint>(ids.Skip(1).Order(), save.Business.Write.Records.Select(row => row.ObjectId));
        Assert.All(save.Business.Write.Records, row => Assert.Equal(kind, row.Kind));
        Assert.DoesNotContain(save.Business.Write.Records, row => row.ObjectId == ids[0]);
    }

    private static void AssertBootstrapContainerUpgradeHeads(RepositoryStorageFacade repository, StoredGraphRevision graph,
        uint[] ids, ObjectLayout[] layouts, ObjectVersionKind latestKind) {
        for (int i = 0; i < ids.Length; i++) {
            ObjectVersionChain chain = repository.Store.ReadObjectChain(graph.Address, ids[i]);
            Assert.Equal(latestKind, chain.Entries[^1].Record.Kind);
            SchemaCatalogView origin = SchemaCatalogView.Load(repository.Store, chain.Entries[0].SchemaRoot!.Value);
            Assert.Equal(layouts[i], TypedBaseBodyCodec.Decode(chain.Entries[0].Record.Body, origin).Layout);
            AssertBootstrapClosureDynamicDescriptor(repository, graph, ids[i], layouts[i]);
            Assert.Throws<InvalidDataException>(() => TypedBaseBodyCodec.Decode(chain.Entries[0].Record.Body, catalog: null));
        }
    }

    private static void AssertBootstrapContainerUpgradeCatalog(SchemaCatalogView catalog, params BootstrapContainerUpgradeExpected[] expected) {
        DurableSchema[] schemas = expected.SelectMany(item => item.Schemas).Distinct().ToArray();
        ObjectLayout[] layouts = expected.SelectMany(item => item.Layouts).Distinct().ToArray();
        Assert.Equal(schemas.Length, catalog.Schemas.Count);
        Assert.Equal(schemas.Length + layouts.Count(layout => layout.Kind != ObjectStateKind.Durable), catalog.DescriptorIds.Count);
        foreach (DurableSchema schema in schemas) {
            Assert.Equal(schema, catalog.Schemas[new(schema.Type, schema.Version)]);
            Assert.Contains(catalog.SchemaNodeId(schema), catalog.DescriptorIds);
            if (schema.Kind == SchemaKind.InlineValue) { Assert.Throws<InvalidDataException>(() => catalog.ResolveLayout(catalog.SchemaNodeId(schema))); }
        }
        foreach (ObjectLayout layout in layouts) { Assert.Equal(layout, catalog.ResolveLayout(catalog.DescriptorId(layout))); }
    }

    private static Dictionary<string, string> BootstrapContainerUpgradeFileHashes(string path) =>
        Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToDictionary(file => Path.GetRelativePath(path, file),
            file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))), StringComparer.Ordinal);

    private static void AssertBootstrapContainerUpgradeFilesUnchanged(Dictionary<string, string> before, string path) {
        Dictionary<string, string> after = BootstrapContainerUpgradeFileHashes(path);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach ((string file, string hash) in before) { Assert.Equal(hash, after[file]); }
    }

    private sealed record BootstrapContainerUpgradeExpected(DurableSchema[] Schemas, ObjectLayout[] Layouts);

    private static BootstrapContainerUpgradeExpected ExpectedBootstrapContainerUpgrade(int version) {
        DurableSchema grade = new("CU.Grade", version, SchemaKind.InlineValue, new DurableFieldInfo(1, version == 1 ? TypeTag.Byte : TypeTag.UInt16));
        DurableSchema point = new("CU.Point", version, SchemaKind.InlineValue,
            new DurableFieldInfo(1, version == 1 ? TypeTag.Int32 : TypeTag.Int64),
            new DurableFieldInfo(2, TypeTag.InlineValue, inlineSchema: grade), DurableFieldInfo.Nullable(3, new(1, TypeTag.Int32)));
        DurableSchema key = new("CU.Key", version, SchemaKind.InlineValue,
            new DurableFieldInfo(1, version == 1 ? TypeTag.Int32 : TypeTag.Int64), DurableFieldInfo.Nullable(2, new(1, TypeTag.Int32)));
        DurableFieldInfo optionalPoint = DurableFieldInfo.Nullable(1, new(1, TypeTag.InlineValue, inlineSchema: point));
        ArrayLayout array = new(TypeExprKind.VectorArray, optionalPoint);
        ListLayout list = new(optionalPoint);
        DictionaryLayout dictionary = new(new(1, TypeTag.InlineValue, inlineSchema: key), optionalPoint);
        DurableSchema root = new("CU.Root", 1, [
            DurableFieldInfo.Reference(1, array.Type), DurableFieldInfo.Reference(2, list.Type), DurableFieldInfo.Reference(3, dictionary.Type),
            DurableFieldInfo.Reference(4, array.Type), DurableFieldInfo.Reference(5, list.Type), DurableFieldInfo.Reference(6, dictionary.Type),
            DurableFieldInfo.Reference(7, TypeExpr.Named("CU.Root")),
        ]);
        return new([root, key, point, grade], [ObjectLayout.ForDurable(root), ObjectLayout.ForArray(array), ObjectLayout.ForList(list), ObjectLayout.ForDictionary(dictionary)]);
    }

    private sealed class BootstrapContainerUpgradeHost(Type host) {
        public Type ClrType => host.Assembly.GetType("BootstrapContainerUpgrade.Root")!;
        public Func<StateModelRegistry> Models { get; } = host.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>();
        public Func<IDurableObject> Create { get; } = host.GetMethod("Create")!.CreateDelegate<Func<IDurableObject>>();
        public Func<IDurableObject, object[]> Objects { get; } = host.GetMethod("Objects")!.CreateDelegate<Func<IDurableObject, object[]>>();
        public Action<IDurableObject> Change { get; } = host.GetMethod("Change")!.CreateDelegate<Action<IDurableObject>>();
        public Action<IDurableObject, int> Check { get; } = host.GetMethod("Check")!.CreateDelegate<Action<IDurableObject, int>>();
        public Action<ObjectStateRecord[], int, int> CheckStored { get; } = host.GetMethod("CheckStored")!.CreateDelegate<Action<ObjectStateRecord[], int, int>>();
        public Action ResetCalls { get; } = host.GetMethod("ResetCalls")!.CreateDelegate<Action>();
        public Func<int> CallCount { get; } = host.GetMethod("CallCount")!.CreateDelegate<Func<int>>();
        public Action<uint[]> CheckUpgradeFacts { get; } = host.GetMethod("CheckUpgradeFacts")!.CreateDelegate<Action<uint[]>>();
        public static BootstrapContainerUpgradeHost Load(GeneratorTestRun run) =>
            new(EmitAndLoad(run.OutputCompilation).GetType("BootstrapContainerUpgrade.Host")!);
    }

    private static string BootstrapContainerUpgradeSource(int version) {
        string upgrades = version == 2 ? """
            [ValueUpgradeRuleSet(AllowKeepExact = true, AllowNullableLifting = true)] public sealed class Rules;
            public static class Upgrades {
                public static readonly List<(string Side, UpgradeContext Context)> Calls = new();
                [DurableValueUpgrade(typeof(Rules), "CU.Grade", 1, 2)]
                public static void Grade(in G.V1 prior, out G.V2 next, UpgradeContext context) {
                    Calls.Add(("grade", context)); next = new((ushort)(prior.Segment0Field1 + 1000));
                }
                [DurableValueUpgrade(typeof(Rules), "CU.Point", 1, 2)]
                [UpgradeDependency("grade", typeof(Rules), "CU.Point", 2, "CU.Point", 2)]
                public static void Point(in P.V1<NullableState<int>> prior, out P.V2<NullableState<int>> next, UpgradeContext context) {
                    Calls.Add(("point", context));
                    G.V1 grade = prior.Segment0Field2;
                    var convert = context.GetValueUpgrade<G.V1, G.V2>("grade");
                    next = new(prior.Segment0Field1 + 1000L, convert(in grade), prior.Segment0Field3);
                }
                [DurableValueUpgrade(typeof(Rules), "CU.Key", 1, 2)]
                public static void Key(in K.V1<NullableState<int>> prior, out K.V2<NullableState<int>> next, UpgradeContext context) {
                    Calls.Add(("key", context)); next = new(prior.Segment0Field1 + 10000L, prior.Segment0Field2);
                }
            }
            """ : "";
        string currentStored = version == 2 ? """
                if (storedVersion == 2) {
                    CheckExact<P.V2<NullableState<int>>, K.V2<NullableState<int>>>(rows,
                        value => value.HasValue ? (value.Value.Segment0Field1, value.Value.Segment0Field2.Segment0Field1, ToOptional(value.Value.Segment0Field3)) : null,
                        key => (key.Segment0Field1, ToOptional(key.Segment0Field2)), phase);
                    return;
                }
            """ : "";
        string facts = version == 2 ? """
                if (Upgrades.Calls.Count(call => call.Side == "key") != 128 || Upgrades.Calls.Count(call => call.Side == "grade") != 306)
                    throw new InvalidOperationException("Incorrect direct endpoint callback counts.");
                foreach (ObjectStateKind kind in new[] { ObjectStateKind.Array, ObjectStateKind.List, ObjectStateKind.Dictionary }) {
                    if (Upgrades.Calls.Count(call => call.Side == "point" && call.Context.SourceObjectLayout.Kind == kind) != 102)
                        throw new InvalidOperationException("Absent nullable values must bypass child callbacks.");
                }
                foreach (var call in Upgrades.Calls) {
                    UpgradeContext context = call.Context;
                    int index = context.SourceObjectLayout.Kind switch { ObjectStateKind.Array => 1, ObjectStateKind.List => 2, ObjectStateKind.Dictionary => 3, _ => -1 };
                    if (index < 0 || context.ObjectId.Value != ids[index] || context.SourceObjectLayout.Equals(context.TargetObjectLayout))
                        throw new InvalidOperationException("Incorrect container owner identity or endpoints.");
                    if (index == 1 && (context.ArrayShape is null || !context.ArrayShape.Equals(new ArrayShape(128))) ||
                        index == 2 && context.ListCount != 128 || index == 3 && context.DictionaryCount != 128)
                        throw new InvalidOperationException("Container facts were lost in child scopes.");
                    DurableSchema point = context.SourceObjectLayout.Kind switch {
                        ObjectStateKind.Array => context.SourceObjectLayout.Array!.ElementSlot.ValueSchema!,
                        ObjectStateKind.List => context.SourceObjectLayout.List!.ElementSlot.ValueSchema!,
                        _ => context.SourceObjectLayout.Dictionary!.ValueSlot.ValueSchema!,
                    };
                    if (point.Version != 1) throw new InvalidOperationException("Source exact representation was lost.");
                }
            """ : "";
        return $$"""
            using System;
            using System.Collections.Generic;
            using System.Linq;
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using Atelia.DurableGraph.Persistence;
            using G = Atelia.DurableGraph.Generated.Family_43552E4772616465;
            using P = Atelia.DurableGraph.Generated.Family_43552E506F696E74;
            using K = Atelia.DurableGraph.Generated.Family_43552E4B6579;
            namespace BootstrapContainerUpgrade;
            [DurableType("CU.Grade", {{version}})] public enum Grade : {{(version == 1 ? "byte" : "ushort")}} { Named = 1 }
            [DurableType("CU.Point", {{version}})] public partial struct Point {
                [DurableField(1)] public {{(version == 1 ? "int" : "long")}} X;
                [DurableField(2)] public Grade Grade;
                [DurableField(3)] public int? Rank;
            }
            [DurableType("CU.Key", {{version}})] public partial struct Key : IEquatable<Key> {
                [DurableField(1)] public {{(version == 1 ? "int" : "long")}} X;
                [DurableField(2)] public int? Rank;
                public bool Equals(Key other) => X == other.X && Rank == other.Rank;
                public override bool Equals(object? other) => other is Key key && Equals(key);
                public override int GetHashCode() => HashCode.Combine(X, Rank);
            }
            [DurableType("CU.Root", 1)] public sealed partial class Root : IDurableObject {
                [DurableField(1)] public Point?[] Array = new Point?[128];
                [DurableField(2)] public List<Point?> List = new();
                [DurableField(3)] public Dictionary<Key, Point?> Dictionary = new();
                [DurableField(4)] public Point?[] ArrayAlias = null!;
                [DurableField(5)] public List<Point?> ListAlias = null!;
                [DurableField(6)] public Dictionary<Key, Point?> DictionaryAlias = null!;
                [DurableField(7)] public Root Self = null!;
            }
            {{upgrades}}
            public static class Host {
                public static StateModelRegistry Models() {
                    StateModelRegistry models = new();
                    Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
                    {{(version == 2 ? "models.UseArrayElementUpgrades(typeof(Rules)); models.UseListElementUpgrades(typeof(Rules)); models.UseDictionaryKeyUpgrades(typeof(Rules)); models.UseDictionaryValueUpgrades(typeof(Rules));" : "")}}
                    return models;
                }
                public static IDurableObject Create() {
                    Root root = new();
                    for (int i = 0; i < 128; i++) {
                        Point? value = DomainPoint(i, {{(version == 1 ? "0" : "1")}});
                        root.Array[i] = value; root.List.Add(value); root.Dictionary.Add(KeyFor(i), value);
                    }
                    root.ArrayAlias = root.Array; root.ListAlias = root.List; root.DictionaryAlias = root.Dictionary; root.Self = root;
                    return root;
                }
                public static object[] Objects(IDurableObject value) {
                    Root root = (Root)value; return new object[] { root, root.Array, root.List, root.Dictionary };
                }
                private static Key KeyFor(int i) => new() { X = i + {{(version == 1 ? "0" : "10000")}}, Rank = i % 3 == 0 ? null : 500 + i };
                private static (long X, ushort Grade, int? Rank)? Expected(int i, int phase) {
                    if (phase == 2 && i == 10) return (90000000001L, 60000, 17);
                    if (phase == 2 && i == 11) return null;
                    if (phase == 2 && i == 63) return (90000000063L, 65530, null);
                    return i % 5 == 0 ? null : (100 + i + (phase == 0 ? 0 : 1000), (ushort)(phase == 0 ? 233 : 1233), i % 3 == 0 ? null : i);
                }
                private static Point? DomainPoint(int i, int phase) {
                    var expected = Expected(i, phase);
                    return expected.HasValue ? new Point { X = ({{(version == 1 ? "int" : "long")}})expected.Value.X, Grade = (Grade)expected.Value.Grade, Rank = expected.Value.Rank } : null;
                }
                public static void Change(IDurableObject value) {
                    Root root = (Root)value;
                    foreach (int i in new[] { 10, 11, 63 }) {
                        Point? next = DomainPoint(i, 2); root.Array[i] = next; root.List[i] = next; root.Dictionary[KeyFor(i)] = next;
                    }
                }
                public static void Check(IDurableObject value, int phase) {
                    Root root = (Root)value;
                    if (!ReferenceEquals(root, root.Self) || !ReferenceEquals(root.Array, root.ArrayAlias) || !ReferenceEquals(root.List, root.ListAlias) ||
                        !ReferenceEquals(root.Dictionary, root.DictionaryAlias) || root.Array.Rank != 1 || root.Array.GetLowerBound(0) != 0 ||
                        root.Array.Length != 128 || root.List.Count != 128 || root.Dictionary.Count != 128) throw new InvalidOperationException("Container owner facts or identity changed.");
                    for (int i = 0; i < 128; i++) {
                        CheckPoint(root.Array[i], Expected(i, phase)); CheckPoint(root.List[i], Expected(i, phase));
                        Key key = KeyFor(i);
                        if (!root.Dictionary.TryGetValue(key, out Point? point)) throw new InvalidOperationException("Incorrect upgraded key.");
                        CheckPoint(point, Expected(i, phase));
                        Key equivalent = KeyFor(i);
                        if (!root.Dictionary.Comparer.Equals(key, equivalent) || root.Dictionary.Comparer.GetHashCode(key) != root.Dictionary.Comparer.GetHashCode(equivalent))
                            throw new InvalidOperationException("Current comparer equality/hash semantics changed.");
                        key.Rank = -777;
                        if (root.Dictionary.Comparer.Equals(key, equivalent) || root.Dictionary.ContainsKey(key)) throw new InvalidOperationException("Comparer ignored part of the exact key.");
                    }
                }
                private static void CheckPoint(Point? point, (long X, ushort Grade, int? Rank)? expected) {
                    var actual = point.HasValue ? ((long)point.Value.X, (ushort)point.Value.Grade, point.Value.Rank) : ((long, ushort, int?)?)null;
                    if (actual != expected) throw new InvalidOperationException("Incorrect normalized or continued point.");
                }
                private static int? ToOptional(NullableState<int> value) => value.HasValue ? value.Value : null;
                public static void CheckStored(ObjectStateRecord[] rows, int storedVersion, int phase) {
                    {{currentStored}}
                    if (storedVersion != 1) throw new InvalidOperationException("Unexpected stored version.");
                    CheckExact<P.V1<NullableState<int>>, K.V1<NullableState<int>>>(rows,
                        value => value.HasValue ? (value.Value.Segment0Field1, (ushort)value.Value.Segment0Field2.Segment0Field1, ToOptional(value.Value.Segment0Field3)) : null,
                        key => (key.Segment0Field1, ToOptional(key.Segment0Field2)), phase);
                }
                private static void CheckExact<TPoint, TKey>(ObjectStateRecord[] rows,
                    Func<NullableState<TPoint>, (long X, ushort Grade, int? Rank)?> point, Func<TKey, (long X, int? Rank)> key, int phase)
                    where TPoint : unmanaged where TKey : unmanaged {
                    if (rows.Length != 4 || typeof(TPoint).Assembly != typeof(Root).Assembly || typeof(TKey).Assembly != typeof(Root).Assembly)
                        throw new InvalidOperationException("Stored DTOs must come from the independent selected model assembly.");
                    var array = rows.Single(row => row.Kind == ObjectStateKind.Array).GetArrayState<NullableState<TPoint>>();
                    var list = rows.Single(row => row.Kind == ObjectStateKind.List).GetListState<NullableState<TPoint>>();
                    var dictionary = rows.Single(row => row.Kind == ObjectStateKind.Dictionary).GetDictionaryState<TKey, NullableState<TPoint>>();
                    if (!array.Shape.Equals(new ArrayShape(128)) || list.Count != 128 || dictionary.Count != 128 || dictionary.ComparerKind != DictionaryComparerKind.CurrentDefault)
                        throw new InvalidOperationException("Stored container facts changed.");
                    for (int i = 0; i < 128; i++) {
                        if (point(array[i]) != Expected(i, phase) || point(list[i]) != Expected(i, phase)) throw new InvalidOperationException("Incorrect exact stored point.");
                    }
                    HashSet<int> seen = new();
                    foreach (var entry in dictionary.Entries) {
                        var exactKey = key(entry.Key);
                        int i = checked((int)(exactKey.X - (phase == 0 ? 0 : 10000)));
                        if (i < 0 || i >= 128 || !seen.Add(i) || exactKey.Rank != (i % 3 == 0 ? null : 500 + i) || point(entry.Value) != Expected(i, phase))
                            throw new InvalidOperationException("Incorrect exact stored key/value DTO.");
                    }
                }
                public static void ResetCalls() { {{(version == 2 ? "Upgrades.Calls.Clear();" : "")}} }
                public static int CallCount() => {{(version == 2 ? "Upgrades.Calls.Count" : "0")}};
                public static void CheckUpgradeFacts(uint[] ids) { {{facts}} }
            }
            """;
    }
}
