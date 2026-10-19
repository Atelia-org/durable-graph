using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

// DB-096 mechanism witness: test-only group orchestration over real capture,
// catalog builders, generated business bodies and cold FS reads. No Branch/CU
// publication, participant dirty installation or CommitRequest API is exercised.
public sealed class SharedCatalogGroupPreparationTests : IDisposable {
    private const string Prefix = "dg-shared-catalog-group-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Two_captured_graphs_and_unreachable_declarations_share_one_catalog_and_cold_read() {
        DurableSchema established = new("Group.Established", 1, new DurableFieldInfo(1, TypeTag.Byte));
        DurableSchema worldOnly = new("Group.WorldUnused", 1, new DurableFieldInfo(1, TypeTag.Int32));
        DurableSchema playerOnly = new("Group.PlayerUnused", 1, new DurableFieldInfo(1, TypeTag.Int64));
        DB087Widget world = new() { Value = 58, Numbers = [4, 8, 15] };
        world.Child = world;
        DB087EnvelopeRoot player = new() { Numbers = [16, 23, 42] };
        FrameAddress schemaAddress, worldAddress, playerAddress;
        ObjectId establishedDescriptor;

        using (var repository = RepositoryStorageFacade.CreateNew(_path)) {
            SchemaCatalogWorkspace initial = new(static () => { });
            initial.Set(established);
            PreparedSchemaCatalogWrite seed = initial.Prepare();
            StoredGraphRevision seedGraph = repository.Store.AppendMeta(null, seed.Write!);
            seed.PrepareInstall(seedGraph);
            seed.Install(seedGraph);
            establishedDescriptor = seed.View.DescriptorId(ObjectLayout.ForDurable(established));

            BusinessPersistenceAdapter worldAdapter = new(repository.Store, Models());
            BusinessPersistenceAdapter playerAdapter = new(repository.Store, Models());
            BusinessSaveCandidate worldCapture = worldAdapter.CaptureOwned(world, StoredGraphKind.State);
            BusinessSaveCandidate playerCapture = playerAdapter.CaptureOwned(player, StoredGraphKind.State);
            ObjectLayout[] capturedLayouts = worldCapture.Graph.Objects.Concat(playerCapture.Graph.Objects)
                .Select(static row => row.Layout).ToArray();
            PreparedSchemaCatalogWrite common = PrepareGroupCatalog(seed.View, [worldOnly, playerOnly], capturedLayouts);
            Assert.Equal(establishedDescriptor, common.View.DescriptorId(ObjectLayout.ForDurable(established)));
            Assert.NotEqual(common.View.DescriptorId(worldCapture.Graph.Objects.Single(row => row.Id == worldCapture.RootId).Layout),
                common.View.DescriptorId(playerCapture.Graph.Objects.Single(row => row.Id == playerCapture.RootId).Layout));

            using PreparedBusinessWrite worldWrite = worldAdapter.PrepareOwned(worldCapture, common.View);
            using PreparedBusinessWrite playerWrite = playerAdapter.PrepareOwned(playerCapture, common.View);
            // Both business writes are encoded only after the common descriptor domain exists.
            StoredGraphRevision meta = repository.Store.AppendMeta(seedGraph.Address, common.Write!);
            schemaAddress = meta.Address;
            worldAddress = repository.Store.AppendBusiness(StoredGraphKind.State, null, meta.Address, worldWrite.Write).Address;
            playerAddress = repository.Store.AppendBusiness(StoredGraphKind.State, null, meta.Address, playerWrite.Write).Address;
            repository.Data.ConfirmDurable();
        }

        using var reopened = RepositoryStorageFacade.OpenReadOnlyExisting(_path);
        SchemaCatalogView coldCatalog = SchemaCatalogView.Load(reopened.Store, schemaAddress);
        Assert.Equal(establishedDescriptor, coldCatalog.DescriptorId(ObjectLayout.ForDurable(established)));
        Assert.Equal(worldOnly, coldCatalog.Schemas[new(worldOnly.Type, worldOnly.Version)]);
        Assert.Equal(playerOnly, coldCatalog.Schemas[new(playerOnly.Type, playerOnly.Version)]);
        Assert.NotSame(worldOnly, coldCatalog.Schemas[new(worldOnly.Type, worldOnly.Version)]);
        Assert.Equal(schemaAddress, reopened.Store.ReadMetadata(worldAddress).SchemaRoot);
        Assert.Equal(schemaAddress, reopened.Store.ReadMetadata(playerAddress).SchemaRoot);
        DB087Widget restoredWorld = Assert.IsType<DB087Widget>(BusinessPersistenceAdapter.ReadRoot(reopened.Store, reopened.Store.ReadBusinessGraph(worldAddress, null), Models()));
        DB087EnvelopeRoot restoredPlayer = Assert.IsType<DB087EnvelopeRoot>(BusinessPersistenceAdapter.ReadRoot(reopened.Store, reopened.Store.ReadBusinessGraph(playerAddress, null), Models()));
        Assert.Equal(58, restoredWorld.Value);
        Assert.Equal(new[] { 4, 8, 15 }, restoredWorld.Numbers);
        Assert.Same(restoredWorld, restoredWorld.Child);
        Assert.Equal(new[] { 16, 23, 42 }, restoredPlayer.Numbers);
        Assert.Null(restoredPlayer.Link);
        Assert.NotSame(world, restoredWorld);
        Assert.NotSame(player, restoredPlayer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Conflicting_group_inputs_are_rejected_before_append_including_container_dependencies(bool viaContainer) {
        using var repository = RepositoryStorageFacade.CreateNew(_path);
        SchemaCatalogWorkspace initial = new(static () => { });
        PreparedSchemaCatalogWrite seed = initial.Prepare();
        StoredGraphRevision meta = repository.Store.AppendMeta(null, seed.Write!);
        seed.PrepareInstall(meta);
        seed.Install(meta);
        long framesBefore = repository.Store.AppendedFrames;
        SchemaKind kind = viaContainer ? SchemaKind.InlineValue : SchemaKind.ReferenceObject;
        DurableSchema first = new("Group.Conflicting", 1, kind, new DurableFieldInfo(1, TypeTag.Int32));
        DurableSchema second = new("Group.Conflicting", 1, kind, new DurableFieldInfo(1, TypeTag.Int64));
        DurableSchema[] explicitRoots = viaContainer ? [first] : [first, second];
        ObjectLayout[] layouts = viaContainer
            ? [ObjectLayout.ForList(new(new(1, TypeTag.InlineValue, inlineSchema: second)))] : [];

        Assert.Throws<SchemaConflictException>(() => PrepareGroupCatalog(seed.View, explicitRoots, layouts));
        Assert.Equal(framesBefore, repository.Store.AppendedFrames);
        Assert.Same(seed.View, initial.Accepted);
        Assert.Empty(initial.Accepted!.Schemas);
        Assert.Equal(0, initial.DirtyCount);

        if (!viaContainer) {
            // A naive sequence of Set calls loses the first unaccepted declaration.
            // This deliberate counterexample is why the group must Collect first.
            SchemaCatalogWorkspace overwritten = new(static () => { }, seed.View);
            overwritten.Set(first);
            overwritten.Set(second);
            Assert.Equal(second, Assert.Single(overwritten.Prepare().View.Schemas).Value);
        }
    }

    [Fact]
    public void Independently_prepared_catalogs_cannot_repin_each_others_encoded_bodies() {
        DurableSchema left = new("Group.Left", 1, new DurableFieldInfo(1, TypeTag.Byte));
        DurableSchema right = new("Group.Right", 1, new DurableFieldInfo(1, TypeTag.Byte));
        SchemaCatalogView leftView = PrepareGroupCatalog(null, [left], []).View;
        SchemaCatalogView rightView = PrepareGroupCatalog(null, [right], []).View;
        ObjectLayout leftLayout = ObjectLayout.ForDurable(left), rightLayout = ObjectLayout.ForDurable(right);
        Assert.Equal(leftView.DescriptorId(leftLayout), rightView.DescriptorId(rightLayout));
        byte[] encoded = TypedBaseBodyCodec.Encode(leftLayout, new PreparedBaseBody([7]), leftView);
        Assert.Equal(leftLayout, TypedBaseBodyCodec.Decode(encoded, leftView).Layout);
        // At the codec boundary the same number names a different definition;
        // choosing the last candidate as shared root is not a valid group merge.
        Assert.Equal(rightLayout, TypedBaseBodyCodec.Decode(encoded, rightView).Layout);
        Assert.False(rightView.Schemas.ContainsKey(new(left.Type, left.Version)));
    }

    private static PreparedSchemaCatalogWrite PrepareGroupCatalog(SchemaCatalogView? accepted,
        IEnumerable<DurableSchema> explicitRoots, IEnumerable<ObjectLayout> capturedLayouts) {
        ObjectLayout[] layouts = capturedLayouts.ToArray();
        // Arrays stand for each participant's explicit requests; extracting and
        // consuming actual workspaces' private dirty state remains product work.
        IReadOnlyDictionary<SchemaKey, DurableSchema> closure = SchemaCatalogSemantics.Collect(
            (accepted?.Schemas.Values ?? []).Concat(explicitRoots), layouts);
        SchemaCatalogSemantics.ValidateDeclarations(closure.Values, (accepted?.ObjectLayouts ?? []).Concat(layouts));
        SchemaCatalogWorkspace common = new(static () => { }, accepted);
        foreach (DurableSchema schema in closure.Values) { common.Set(schema); }
        return common.PrepareOwned(layouts);
    }

    private static StateModelRegistry Models() {
        StateModelRegistry models = new();
        models.Register(global::Atelia.DurableGraph.Generated.Family_5030576964676574.Definition);
        models.Register(global::Atelia.DurableGraph.Generated.Family_5030456E76656C6F7065.Definition);
        return models;
    }

    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
