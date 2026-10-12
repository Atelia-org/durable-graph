using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class GraphResourcesSplitOwnerTests : IDisposable {
    private const string Prefix = "dg-split-resources-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ResourcesOwnedTypedStateAndSchemaEventColdContinueFromStateContentWithTheLatestCatalog() {
        DurableSchema extra = new("Unknown.Resource.Event", 1, new DurableFieldInfo(1, TypeTag.Int32));
        DurableSchema widget = new("P0Widget", 1, [
            new(1, TypeTag.Int32),
            DurableFieldInfo.Reference(2, TypeExpr.List(TypeExpr.Builtin(TypeTag.Int32))),
            DurableFieldInfo.Reference(3, TypeExpr.Named("P0Widget")),
        ]);
        FrameAddress state0, event1, schema0, schema1, state1, state2;
        ObjectId rootId, childId, listId;
        FrameAddress listHead0, rootHead0, childHead0;
        long h0;
        using (GraphResources resources = GraphResources.CreateMetaBootstrapNew(_path)) {
            using P0Repository facade = new(resources);
            Assert.Same(resources.Data, facade.Data);
            Assert.Same(resources.Versions, facade.Versions);
            StateModelRegistry models = Models();
            P0Session session = new(facade, "main", models);
            List<int> numbers = Enumerable.Repeat(0, 128).ToList();
            DB087Widget root = new() { Value = 1, Numbers = numbers };
            root.Child = new() { Value = 2, Numbers = numbers, Child = root };
            using (P0SessionSave save = session.Prepare(root)) {
                Assert.Same(resources, save.Pending.Owner);
                P0SaveResult saved = save.Commit();
                state0 = saved.Business.Address;
                schema0 = saved.SharedSchemaRoot!.Value;
                rootId = saved.Business.RootId;
                Assert.Equal(3, saved.Business.Objects.Count);
                Assert.Equal(state0, resources.GetMetaBootstrapHead("main"));
                Assert.Equal(state0, resources.GetMetaBootstrapStateHead("main"));
                Assert.Equal(schema0, resources.SharedMetaBootstrapRoot);
                Assert.Same(resources.MetaBootstrapStore, facade.Store);
            }
            P0GraphSnapshot s0 = facade.Store.ReadGraph(state0);
            P0DecodedRows rows = P0TypedReader.Read(facade.Store, state0, models.Snapshot(), session.Catalog.Accepted!);
            listId = Assert.Single(rows.Rows.Values, row => row.Kind == ObjectStateKind.List).Id;
            childId = Assert.Single(rows.Rows.Keys, id => id != rootId && id != listId);
            listHead0 = s0.Objects[listId.Value];
            rootHead0 = s0.Objects[rootId.Value];
            childHead0 = s0.Objects[childId.Value];
            h0 = facade.Store.ReadObjectChain(state0, listId.Value).ReconstructionPayloadBytes;

            session.Catalog.Set(extra); // The new descriptor has no business CLR registration.
            root.Numbers[7] = 1;
            using (P0SessionSave save = session.Prepare(root, P0GraphKind.Event)) {
                Assert.Same(resources, save.Pending.Owner);
                Assert.NotNull(save.Catalog.Write);
                ObjectVersionRecord delta = Assert.Single(save.Business.Write.Records);
                Assert.Equal(listId.Value, delta.ObjectId);
                Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
                Assert.Equal(listHead0, delta.PriorAddress);
                P0SaveResult saved = save.Commit();
                event1 = saved.Business.Address;
                schema1 = saved.SharedSchemaRoot!.Value;
                Assert.Equal(state0, saved.Business.SourceGraph);
                Assert.Equal(schema1, saved.Business.SchemaRoot);
            }
            Assert.NotEqual(schema0, schema1);
            Assert.Equal(state0, session.StateSource!.Address);
            Assert.Equal(schema0, session.StateSource.SchemaRoot);
            Assert.Same(root, session.State);
            Assert.Equal(0, session.Catalog.DirtyCount);
            Assert.Equal(schema1, session.Catalog.Accepted!.Snapshot!.Address);
            Assert.Equal(event1, facade.GetHead("main"));
            Assert.Equal(state0, facade.GetStateHead("main"));
            Assert.Equal(schema1, facade.SharedSchemaRoot);
            Assert.Equal(event1, resources.GetMetaBootstrapHead("main"));
            Assert.Equal(state0, resources.GetMetaBootstrapStateHead("main"));
            Assert.Equal(schema1, resources.SharedMetaBootstrapRoot);
            AssertRing(P0BusinessWorkspace.ReadRoot(facade.Store, state0, models), 0);
            AssertRing(P0BusinessWorkspace.ReadRoot(facade.Store, event1, models), 1);
        }

        Dictionary<string, byte[]> beforeReadOnly = SnapshotFiles();
        using (GraphResources resources = GraphResources.OpenMetaBootstrapReadOnlyExisting(_path)) {
            using P0Repository facade = new(resources);
            Assert.True(resources.IsReadOnly);
            // No business model has been created or registered in this open before meta projection.
            P0CatalogView catalog = P0CatalogView.Load(facade.Store, facade.SharedSchemaRoot!.Value);
            Assert.Equal(extra, catalog.Schemas[new(extra.Type, extra.Version)]);
            Assert.Equal(widget, catalog.Schemas[new(widget.Type, widget.Version)]);
            Assert.Equal(event1, facade.GetHead("main"));
            Assert.Equal(state0, facade.GetStateHead("main"));
            Assert.Equal(schema0, facade.Store.ReadGraph(state0).SchemaRoot);
            Assert.Equal(schema1, facade.Store.ReadGraph(event1).SchemaRoot);
            Assert.Throws<InvalidOperationException>(facade.EnsureEditingAllowed);
            AssertRing(P0BusinessWorkspace.ReadRoot(facade.Store, state0, Models()), 0);
            AssertRing(P0BusinessWorkspace.ReadRoot(facade.Store, event1, Models()), 1);
        }
        AssertFilesEqual(beforeReadOnly);

        using (GraphResources resources = GraphResources.OpenMetaBootstrapExisting(_path)) {
            using P0Repository facade = new(resources);
            P0Session session = new(facade, "main", Models());
            DB087Widget root = AssertRing(session.State, 0); // E1's content is not editable State.
            Assert.Equal(state0, session.StateSource!.Address);
            Assert.Equal(schema0, session.StateSource.SchemaRoot);
            Assert.Equal(schema1, session.Catalog.Accepted!.Snapshot!.Address);
            Assert.Equal(h0, facade.Store.ReadObjectChain(state0, listId.Value).ReconstructionPayloadBytes);
            root.Numbers[7] = 2;
            long d;
            using (P0SessionSave save = session.Prepare(root)) {
                Assert.Null(save.Catalog.Write);
                Assert.Equal(state0, save.Business.Write.SourceGraph);
                ObjectVersionRecord delta = Assert.Single(save.Business.Write.Records);
                Assert.Equal(listId.Value, delta.ObjectId);
                Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
                Assert.Equal(listHead0, delta.PriorAddress);
                d = ObjectVersionPayloadSize.GetDeltaPayloadBytes(delta.Body.Length, listHead0);
                P0SaveResult saved = save.Commit();
                state1 = saved.Business.Address;
                Assert.Equal(event1, saved.Business.Parent);
                Assert.Equal(state0, saved.Business.SourceGraph);
                Assert.Equal(schema1, saved.Business.SchemaRoot);
                Assert.Equal(rootHead0, saved.Business.Objects[rootId.Value]);
                Assert.Equal(childHead0, saved.Business.Objects[childId.Value]);
            }
            Assert.Equal(h0 + d, facade.Store.ReadObjectChain(state1, listId.Value).ReconstructionPayloadBytes);
            Assert.Equal(state1, facade.GetStateHead("main"));
            Assert.Equal(state1, facade.GetHead("main"));
            Assert.Equal(schema1, facade.SharedSchemaRoot);
            P0GraphSnapshot s1 = session.StateSource!;
            long before = facade.Store.AppendedFrames;
            using (P0SessionSave save = session.Prepare(root)) {
                Assert.Null(save.Catalog.Write);
                Assert.Empty(save.Business.Write.Records);
                Assert.Empty(save.Business.Write.RemovedIds);
                P0GraphSnapshot graph = save.Commit().Business;
                state2 = graph.Address;
                Assert.Equal(s1.DirectoryRoot, graph.DirectoryRoot);
                Assert.Null(graph.OwnedBatch);
            }
            Assert.Equal(before + 1, facade.Store.AppendedFrames);
        }
        using (GraphResources resources = GraphResources.OpenMetaBootstrapReadOnlyExisting(_path)) {
            using P0Repository facade = new(resources);
            Assert.Equal(state2, facade.GetHead("main"));
            Assert.Equal(state2, facade.GetStateHead("main"));
            Assert.Equal(schema1, facade.SharedSchemaRoot);
            AssertRing(P0BusinessWorkspace.ReadRoot(facade.Store, state0, Models()), 0);
            AssertRing(P0BusinessWorkspace.ReadRoot(facade.Store, event1, Models()), 1);
            AssertRing(P0BusinessWorkspace.ReadRoot(facade.Store, state1, Models()), 2);
            AssertRing(P0BusinessWorkspace.ReadRoot(facade.Store, state2, Models()), 2);
        }
    }

    [Theory]
    [InlineData("Fault")]
    [InlineData("Dispose")]
    [InlineData("DataClose")]
    [InlineData("VersionsClose")]
    public void SplitResourcesClosureRejectsRetainedFacadesStoresAndPendingButPreservesOwnedResults(string stop) {
        using GraphResources resources = GraphResources.CreateMetaBootstrapNew(_path);
        using P0Repository facade = new(resources);
        using (P0PendingSave seed = PrepareRaw(facade, 1)) { facade.Commit(seed); }
        FrameAddress address = facade.GetHead("main")!.Value;
        FrameAddress schema = facade.SharedSchemaRoot!.Value;
        P0GraphStore retained = facade.Store;
        P0GraphSnapshot graph = retained.ReadGraph(address);
        P0ObjectChain chain = retained.ReadObjectChain(address, 1);
        using P0PendingSave pending = PrepareRaw(facade, 2);
        Assert.Same(resources, pending.Owner);
        switch (stop) {
            case "Fault": resources.MarkFaulted(); break;
            case "Dispose": resources.Dispose(); break;
            case "DataClose": resources.Data.Dispose(); break;
            case "VersionsClose": resources.Versions.Dispose(); break;
            default: throw new InvalidOperationException(stop);
        }
        Assert.ThrowsAny<InvalidOperationException>(resources.RequireAvailable);
        Assert.ThrowsAny<InvalidOperationException>(() => resources.GetMetaBootstrapHead("main"));
        Assert.ThrowsAny<InvalidOperationException>(() => facade.GetHead("main"));
        Assert.ThrowsAny<InvalidOperationException>(facade.EnsureEditingAllowed);
        Assert.ThrowsAny<InvalidOperationException>(() => facade.Commit(pending));
        Assert.ThrowsAny<InvalidOperationException>(() => retained.ReadGraph(address));
        Assert.ThrowsAny<InvalidOperationException>(() => retained.ReadObjectChain(address, 1));
        Assert.ThrowsAny<InvalidOperationException>(() => retained.Append(P0GraphKind.State, address,
            schema, new(address, new(1), [ObjectVersionRecord.CreateBase(1, [3])])));
        if (stop == "Fault") {
            // Fault blocks Store/publication operations, but the inactive owned pending can still be released.
            facade.Discard(pending);
            Assert.False(pending.IsPrepared);
            Assert.ThrowsAny<InvalidOperationException>(() => facade.Commit(pending));
        }
        Assert.Equal(address, graph.Address);
        Assert.Equal(graph.Objects[1], chain.Head);
        Assert.Equal(new byte[] { 1 }, Assert.Single(chain.Entries).Record.Body.ToArray());
    }

    [Fact]
    public void OneResourcesOwnerSharesPendingAcrossBorrowedFacadesAndRejectsForeignAndLegacyEntryPoints() {
        using GraphResources resources = GraphResources.CreateMetaBootstrapNew(_path);
        using P0Repository first = new(resources);
        using P0Repository second = new(resources);
        Assert.Same(resources.Data, first.Data);
        Assert.Same(first.Data, second.Data);
        Assert.Same(resources.Versions, first.Versions);
        Assert.Same(first.Store, second.Store);
        using P0PendingSave pending = PrepareRaw(first, 1);
        Assert.Same(resources, pending.Owner);
        Assert.True(second.HasPending);
        Assert.Throws<InvalidOperationException>(() => PrepareRaw(second, 2));
        using (GraphResources foreignResources = GraphResources.CreateMetaBootstrapNew(Path.Combine(_path, "other"))) {
            using P0Repository foreign = new(foreignResources);
            Assert.Throws<InvalidOperationException>(() => foreignResources.CommitMetaBootstrap(pending));
            Assert.False(foreignResources.IsFaulted);
            Assert.False(foreign.HasPending);
        }
        // Closing a borrowed facade neither closes FS/VS nor resolves its resource owner's pending save.
        first.Dispose();
        resources.RequireAvailable();
        Assert.True(second.HasPending);
        Assert.Throws<ObjectDisposedException>(() => first.GetHead("main"));
        P0SaveResult saved = second.Commit(pending);
        Assert.False(second.HasPending);
        Assert.False(resources.IsFaulted);
        Assert.Equal(saved.Business.Address, second.GetHead("main"));
        Assert.Equal((byte)1, Assert.Single(second.Store.ReadObjectChain(saved.Business.Address, 1).Entries).Record.Body[0]);

        long frames = second.Store.AppendedFrames;
        Assert.Throws<InvalidOperationException>(() => resources.Schemas);
        Assert.Throws<InvalidOperationException>(() => resources.States);
        Assert.Throws<InvalidOperationException>(() => resources.PrepareBranchRoot("main", saved.Business.Address));
        Assert.Throws<InvalidOperationException>(() => resources.PrepareSchemaRoot(saved.SharedSchemaRoot!.Value));
        Assert.Throws<InvalidOperationException>(() => resources.ReadHistory(saved.Business.Address));
        using (GraphResources legacy = GraphResources.CreateNew(Path.Combine(_path, "legacy"))) {
            Assert.Throws<InvalidOperationException>(() => new P0Repository(legacy));
            Assert.Throws<InvalidOperationException>(() => legacy.PrepareMetaBootstrap("main", P0GraphKind.State,
                new(null, new(1), [ObjectVersionRecord.CreateBase(1, [1])])));
            FrameAddress legacyState = legacy.States.Append(StateRevision.CreateObjectHeadMapBase(null, [], []));
            GraphResources.RootUpdate actualLegacyUpdate = legacy.PrepareBranchRoot("main", legacyState);
            Assert.Throws<InvalidOperationException>(() => resources.PublishRoot(actualLegacyUpdate, out _));
            Assert.False(legacy.IsFaulted);
            Assert.False(resources.IsFaulted);
            // The material rejected by the wrong mode is valid at its actual issuing owner.
            legacy.PublishRoot(actualLegacyUpdate, out _);
            Assert.Equal(legacyState, legacy.GetHead("main"));
        }
        Assert.Equal(frames, second.Store.AppendedFrames);
        Assert.Equal(saved.Business.Address, second.GetHead("main"));
        // Rejections did not consume or fault the shared owner; a valid next save still publishes.
        using P0PendingSave next = resources.PrepareMetaBootstrap("main", P0GraphKind.State,
            new(resources.GetMetaBootstrapStateHead("main"), new(1), [ObjectVersionRecord.CreateBase(1, [2])]),
            new(resources.SharedMetaBootstrapRoot, new(1), [ObjectVersionRecord.CreateBase(1, [2])]));
        P0SaveResult continued = resources.CommitMetaBootstrap(next);
        Assert.Equal(continued.Business.Address, second.GetStateHead("main"));
        Assert.False(resources.IsFaulted);
    }

    private static P0PendingSave PrepareRaw(P0Repository facade, byte value) => facade.Prepare("main", P0GraphKind.State,
        new(facade.GetStateHead("main"), new(1), [ObjectVersionRecord.CreateBase(1, [value])]),
        new(facade.SharedSchemaRoot, new(1), [ObjectVersionRecord.CreateBase(1, [value])]));

    private static DB087Widget AssertRing(IDurableObject? value, int number) {
        DB087Widget root = Assert.IsType<DB087Widget>(value);
        Assert.Equal(1, root.Value);
        DB087Widget child = Assert.IsType<DB087Widget>(root.Child);
        Assert.Equal(2, child.Value);
        Assert.Same(root, child.Child);
        Assert.Same(root.Numbers, child.Numbers);
        Assert.Equal(128, root.Numbers.Count);
        for (int i = 0; i < root.Numbers.Count; i++) { Assert.Equal(i == 7 ? number : 0, root.Numbers[i]); }
        return root;
    }

    private static StateModelRegistry Models() {
        StateModelRegistry registry = new();
        registry.Register(global::Atelia.DurableGraph.Generated.Family_5030576964676574.Definition);
        return registry;
    }

    private Dictionary<string, byte[]> SnapshotFiles() => Directory.GetFiles(_path, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(_path, path), File.ReadAllBytes);

    private void AssertFilesEqual(Dictionary<string, byte[]> expected) {
        Dictionary<string, byte[]> actual = SnapshotFiles();
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach ((string name, byte[] bytes) in expected) { Assert.Equal(bytes, actual[name]); }
    }

    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
