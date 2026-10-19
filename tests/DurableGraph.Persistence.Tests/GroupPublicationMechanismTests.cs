using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Atelia.HeadStore;

namespace Atelia.DurableGraph.Persistence.Tests;

// DB-096 witness: real business candidates share one catalog and publish their
// two business Heads plus S in one HS transaction. D membership stays unchanged.
// This is not a Repository group coordinator. The owner closes after direct HS
// publication because its cached publication baseline is then stale.
public sealed class GroupPublicationMechanismTests : IDisposable {
    private const string Prefix = "dg-group-publication-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1000000, 1);

    [Fact]
    public void Mixed_candidates_share_catalog_publish_once_install_by_kind_and_cold_continue_delta() {
        DurableSchema declaration = new("Group.WorldDeclaration", 1, new DurableFieldInfo(1, TypeTag.Byte));
        FrameAddress worldSeed, playerSeed, groupedWorld, groupedEvent, commonSchema, continuedWorld, continuedPlayer;
        uint worldList, playerList;
        using (RepositoryStorageFacade repository = RepositoryStorageFacade.CreateNew(_path)) {
            BranchSaveSessionFixture worldSeedSession = new(repository, "world", Models());
            BranchSaveSessionFixture playerSeedSession = new(repository, "player", Models());
            using (PreparedFixtureSave save = worldSeedSession.Prepare(NewWorld(10), parameters: NoRebase)) {
                worldSeed = save.Commit().Business.Address;
            }
            using (PreparedFixtureSave save = playerSeedSession.Prepare(NewWorld(20), parameters: NoRebase)) {
                playerSeed = save.Commit().Business.Address;
            }
            BusinessPersistenceAdapter world = BusinessPersistenceAdapter.LoadState(repository.Store, MechanismFixture.Snapshot(repository, worldSeed), Models());
            BusinessPersistenceAdapter player = BusinessPersistenceAdapter.LoadState(repository.Store, MechanismFixture.Snapshot(repository, playerSeed), Models());
            DB087Widget worldRoot = Assert.IsType<DB087Widget>(world.State);
            IDurableObject playerState = player.State!;
            NormalizedGraphState playerBaseline = player.Baseline!;
            StoredGraphRevision playerSource = player.Source!;
            worldList = ListId(world.Baseline!);
            playerList = ListId(playerBaseline);
            FrameAddress worldListPrior = world.Source!.ObjectHeads[worldList];
            FrameAddress playerListPrior = playerSource.ObjectHeads[playerList];
            long playerH = playerBaseline.Objects[new(playerList)].Storage!.Value.ReconstructionPayloadBytes;
            worldRoot.Numbers[2] = 11;
            DB087EnvelopeRoot domainEvent = new() { Numbers = [31, 32] };
            SchemaCatalogView accepted = SchemaCatalogView.Load(repository.Store, repository.SharedSchemaRoot!.Value);
            Assert.DoesNotContain(new SchemaKey("P0Envelope", 1), accepted.Schemas.Keys);
            HeadEntry oldDirectory = repository.Heads.ReadHead(1).Unwrap();
            HeadEntry oldSchema = repository.Heads.ReadHead(2).Unwrap();
            HeadEntry oldWorld = repository.Heads.ReadHead(MechanismFixture.Branch(repository, "world")).Unwrap();
            HeadEntry oldPlayer = repository.Heads.ReadHead(MechanismFixture.Branch(repository, "player")).Unwrap();

            BusinessSaveCandidate worldCapture = world.CaptureOwned(worldRoot, StoredGraphKind.State);
            BusinessSaveCandidate? eventCapture = null;
            try {
                eventCapture = player.CaptureOwned(domainEvent, StoredGraphKind.Event);
                ObjectLayout[] layouts = worldCapture.Graph.Objects.Concat(eventCapture.Graph.Objects)
                    .Select(static row => row.Layout).ToArray();
                IReadOnlyDictionary<SchemaKey, DurableSchema> closure = SchemaCatalogSemantics.Collect(
                    accepted.Schemas.Values.Append(declaration), layouts);
                SchemaCatalogSemantics.ValidateDeclarations(closure.Values, accepted.ObjectLayouts.Concat(layouts));
                SchemaCatalogWorkspace catalogOwner = new(static () => { }, accepted);
                foreach (DurableSchema schema in closure.Values) { catalogOwner.Set(schema); }
                PreparedSchemaCatalogWrite common = catalogOwner.PrepareOwned(layouts);
                Assert.NotNull(common.Write);
                Assert.Contains(new SchemaKey("P0Envelope", 1), common.View.Schemas.Keys);
                Assert.Contains(new SchemaKey(declaration.Type, declaration.Version), common.View.Schemas.Keys);

                using PreparedBusinessWrite stateWrite = world.PrepareOwned(worldCapture, common.View, NoRebase);
                using PreparedBusinessWrite eventWrite = player.PrepareOwned(eventCapture, common.View, NoRebase);
                ObjectVersionRecord delta = Assert.Single(stateWrite.Write.Records);
                Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
                Assert.Equal(worldList, delta.ObjectId);
                Assert.Equal(worldListPrior, delta.PriorAddress);
                Assert.Equal(worldSeed, stateWrite.Write.SourceGraph);
                Assert.Equal(playerSeed, eventWrite.Write.SourceGraph);
                StoredGraphRevision meta = repository.Store.AppendMeta(accepted.Snapshot!.Address, common.Write!);
                StoredGraphRevision state = repository.Store.AppendBusiness(StoredGraphKind.State, worldSeed, meta.Address, stateWrite.Write);
                StoredGraphRevision eventGraph = repository.Store.AppendBusiness(StoredGraphKind.Event, playerSeed, meta.Address, eventWrite.Write);
                commonSchema = meta.Address;
                groupedWorld = state.Address;
                groupedEvent = eventGraph.Address;
                common.PrepareInstall(meta);
                stateWrite.PrepareInstall(state); // Events deliberately have no State installation.

                byte[] worldPayload = BusinessEntryPayloadCodec.Encode(groupedWorld, []);
                byte[] playerPayload = BusinessEntryPayloadCodec.Encode(groupedEvent, []);
                byte[] schemaPayload = RootPayload("P0S1"u8, new() { ["schema"] = commonSchema });
                IReadOnlyDictionary<HeadId, HeadEntryId> published = repository.Heads.PublishHeads(new Dictionary<HeadId, ReadOnlyMemory<byte>> {
                    [oldWorld.Id.HeadId] = worldPayload,
                    [oldPlayer.Id.HeadId] = playerPayload,
                    [oldSchema.Id.HeadId] = schemaPayload
                }, out PublicationOutcome outcome).Unwrap();
                Assert.Equal(PublicationOutcome.Confirmed, outcome);
                Assert.Equal(3, published.Count);
                Assert.NotEqual(oldWorld.Id, published[oldWorld.Id.HeadId]);
                Assert.NotEqual(oldPlayer.Id, published[oldPlayer.Id.HeadId]);
                Assert.NotEqual(oldSchema.Id, published[oldSchema.Id.HeadId]);
                Assert.Equal(oldDirectory.Id, repository.Heads.ReadHead(1).Unwrap().Id);
                Assert.Equal(oldDirectory.Payload.ToArray(), repository.Heads.ReadHead(1).Unwrap().Payload.ToArray());
                Assert.Equal(worldPayload, repository.Heads.ReadHead(oldWorld.Id.HeadId).Unwrap().Payload.ToArray());
                Assert.Equal(playerPayload, repository.Heads.ReadHead(oldPlayer.Id.HeadId).Unwrap().Payload.ToArray());
                Assert.Equal(schemaPayload, repository.Heads.ReadHead(2).Unwrap().Payload.ToArray());

                common.Install(meta);
                stateWrite.Install(state);
                eventCapture.AbortPreparation();
                Assert.Same(common.View, catalogOwner.Accepted);
                Assert.Same(worldRoot, world.State);
                Assert.Same(state, world.Source);
                Assert.Equal(groupedWorld, world.Baseline!.RevisionAddress);
                Assert.Equal(11, world.Baseline.Objects[new(worldList)].Current.GetListState<int>().Elements[2]);
                Assert.Same(playerState, player.State);
                Assert.Same(playerSource, player.Source);
                Assert.Same(playerBaseline, player.Baseline);
                Assert.Equal(playerListPrior, player.Source!.ObjectHeads[playerList]);
                Assert.Equal(playerH, player.Baseline!.Objects[new(playerList)].Storage!.Value.ReconstructionPayloadBytes);
            } finally {
                worldCapture.AbortPreparation();
                eventCapture?.AbortPreparation();
            }
            // Never save through this facade again: direct HS publication has not
            // installed RepositoryResources' directory/catalog baseline.
        }

        using (RepositoryStorageFacade repository = RepositoryStorageFacade.OpenExisting(_path)) {
            Assert.Equal(groupedWorld, MechanismFixture.FindCurrent(repository, "world")?.Address);
            Assert.Equal(groupedEvent, MechanismFixture.FindCurrent(repository, "player")?.Address);
            Assert.Equal(playerSeed, MechanismFixture.State(repository, "player")?.Address);
            Assert.Equal(commonSchema, repository.SharedSchemaRoot);
            SchemaCatalogView coldCatalog = SchemaCatalogView.Load(repository.Store, commonSchema);
            Assert.Equal(declaration, coldCatalog.Schemas[new(declaration.Type, declaration.Version)]);
            Assert.Contains(new SchemaKey("P0Envelope", 1), coldCatalog.Schemas.Keys);
            Assert.Equal(commonSchema, repository.Store.ReadMetadata(groupedWorld).SchemaRoot);
            Assert.Equal(commonSchema, repository.Store.ReadMetadata(groupedEvent).SchemaRoot);
            DB087EnvelopeRoot restoredEvent = Assert.IsType<DB087EnvelopeRoot>(
                BusinessPersistenceAdapter.ReadRoot(repository.Store, MechanismFixture.Snapshot(repository, groupedEvent), Models()));
            Assert.Equal(new[] { 31, 32 }, restoredEvent.Numbers);
            Assert.Null(restoredEvent.Link);
            BranchSaveSessionFixture world = new(repository, "world", Models());
            BranchSaveSessionFixture player = new(repository, "player", Models());
            Assert.Equal(groupedWorld, world.StateSource!.Address);
            Assert.Equal(playerSeed, player.StateSource!.Address); // Event did not become the baseline.
            Assert.Equal(worldList, ListId(world.Baseline!));
            Assert.Equal(playerList, ListId(player.Baseline!));
            DB087Widget worldRoot = Assert.IsType<DB087Widget>(world.State);
            DB087Widget playerRoot = Assert.IsType<DB087Widget>(player.State);
            Assert.Equal(11, worldRoot.Numbers[2]);
            Assert.Equal(0, playerRoot.Numbers[2]);
            worldRoot.Numbers[3] = 12;
            playerRoot.Numbers[3] = 22;
            continuedWorld = SaveDelta(world, worldRoot, worldList, groupedWorld);
            continuedPlayer = SaveDelta(player, playerRoot, playerList, playerSeed);
            Assert.Equal(groupedWorld, MechanismFixture.Resources(repository).ReadPrevious(MechanismFixture.Current(repository, "world").Id)?.Address);
            Assert.Equal(groupedEvent, MechanismFixture.Resources(repository).ReadPrevious(MechanismFixture.Current(repository, "player").Id)?.Address);
            Assert.Equal(commonSchema, repository.SharedSchemaRoot); // Neither continuation grows Schema.
        }
        using (RepositoryStorageFacade repository = RepositoryStorageFacade.OpenReadOnlyExisting(_path)) {
            Assert.Equal(continuedWorld, MechanismFixture.FindCurrent(repository, "world")?.Address);
            Assert.Equal(continuedPlayer, MechanismFixture.FindCurrent(repository, "player")?.Address);
            DB087Widget world = Assert.IsType<DB087Widget>(BusinessPersistenceAdapter.ReadRoot(repository.Store, MechanismFixture.Snapshot(repository, continuedWorld), Models()));
            DB087Widget player = Assert.IsType<DB087Widget>(BusinessPersistenceAdapter.ReadRoot(repository.Store, MechanismFixture.Snapshot(repository, continuedPlayer), Models()));
            Assert.Equal(11, world.Numbers[2]);
            Assert.Equal(12, world.Numbers[3]);
            Assert.Equal(0, player.Numbers[2]);
            Assert.Equal(22, player.Numbers[3]);
            Assert.Same(world, world.Child);
            Assert.Same(player, player.Child);
        }
    }

    private static FrameAddress SaveDelta(BranchSaveSessionFixture session, DB087Widget root, uint listId, FrameAddress source) {
        FrameAddress prior = session.StateSource!.ObjectHeads[listId];
        long priorH = session.Baseline!.Objects[new(listId)].Storage!.Value.ReconstructionPayloadBytes;
        using PreparedFixtureSave save = session.Prepare(root, parameters: NoRebase);
        ObjectVersionRecord delta = Assert.Single(save.Business.Write.Records);
        Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
        Assert.Equal(listId, delta.ObjectId);
        Assert.Equal(prior, delta.PriorAddress);
        Assert.Equal(source, save.Business.Write.SourceGraph);
        Assert.Null(save.Catalog.Write);
        StoredGraphRevision graph = save.Commit().Business;
        Assert.Equal(source, graph.SourceGraph);
        Assert.Equal(priorH + ObjectVersionPayloadSize.GetDeltaPayloadBytes(delta.Body.Length, prior),
            session.Baseline!.Objects[new(listId)].Storage!.Value.ReconstructionPayloadBytes);
        Assert.Same(root, session.State);
        return graph.Address;
    }

    private static uint ListId(NormalizedGraphState baseline) => Assert.Single(
        baseline.Objects.Values, static row => row.Current.Kind == ObjectStateKind.List).Current.Id.Value;

    private static DB087Widget NewWorld(int value) {
        DB087Widget root = new() { Value = value, Numbers = Enumerable.Repeat(0, 128).ToList() };
        root.Child = root;
        return root;
    }

    private static byte[] RootPayload(ReadOnlySpan<byte> magic, Dictionary<string, FrameAddress> roots) {
        byte[] body = NamedFrameMapCodec.Encode(roots).Unwrap();
        byte[] payload = new byte[magic.Length + body.Length];
        magic.CopyTo(payload);
        body.CopyTo(payload, magic.Length);
        return payload;
    }

    private static StateModelRegistry Models() {
        StateModelRegistry models = new();
        models.Register(global::Atelia.DurableGraph.Generated.Family_5030576964676574.Definition);
        models.Register(global::Atelia.DurableGraph.Generated.Family_5030456E76656C6F7065.Definition);
        return models;
    }

    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
