using Atelia.Data;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;
#if HISTORY_V3
using WorldStates = Atelia.DurableGraph.Generated.Family_7061636B6167652E696E6C696E652D776F726C64;
#endif

namespace InlineStructPackageConsumerProbe;

internal static class Program {
    private static readonly RbfSegmentStoreOptions Options = new() { NewStoreLayout = RbfSegmentStoreLayout.Flat };
    private static readonly ReadAmplificationBaseBudgetParameters Policy = new(int.MaxValue, 1);

    private static void Main(string[] args) {
        if (args.Length != 1) { throw new ArgumentException("Pass one repository directory."); }
        string directory = Path.GetFullPath(args[0]);
#if HISTORY_V1
        Seed(directory);
        Console.WriteLine("InlineSeed:True:NestedOwnerDelta:True:NoStructObjectIds:True:ReadonlySharedCycles:True");
#elif HISTORY_V2 && CHILD_V2
        Migrate(directory);
        Console.WriteLine("InlineUpgrade:True:ForcedOwnerBase:True:NoChangeAfterInstall:True:SameDomainInstances:True");
#elif HISTORY_V2
        UpgradeNominalChildOnly(directory);
        Console.WriteLine("NominalChildVersionIndependent:True:OnlyChildBase:True:OwnerSchemaUnchanged:True");
#else
        DeleteInlineDomainDeclarations(directory);
        Console.WriteLine("DeletedStructClr:True:HistoricalExactRead:True:RetainedNestedUpgradeChain:True:CurrentHeadReopen:True");
#endif
    }

    private static StateModelRegistry Models() {
        StateModelRegistry models = new();
#if HISTORY_V3
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
#else
        World.__DurableState.RegisterModel(models);
        Node.__DurableState.RegisterModel(models);
#endif
        return models;
    }

    private static StateReaderRegistry Readers() {
        StateReaderRegistry readers = new();
#if HISTORY_V3
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(readers);
#else
        World.__DurableState.RegisterReaders(readers);
        Node.__DurableState.RegisterReaders(readers);
#endif
        return readers;
    }

#if HISTORY_V1
    private static void Seed(string directory) {
        string shared = new("shared label".ToCharArray());
        string equal = new("shared label".ToCharArray());
        Require(shared == equal && !ReferenceEquals(shared, equal), "Seed labels must have equal value but distinct identities.");
        Node node = new(31, shared);
        World world = new(node, equal);
        FrameAddress initial;
        FrameAddress historical;
        ObjectId worldId;
        using (EventHistoryRepository repository = EventHistoryRepository.CreateNew(directory, Models(), Options)) {
            using EventHistorySession<World> session = repository.CreateBranch("main", world, Policy);
            initial = session.StateRevisionAddress;
            worldId = session.StateId;
            world.ChangeLeft(12);
            session.CommitDomainEvent(session.State, Policy);
            historical = session.CommitDomainState(Policy).RevisionAddress;
            Require(ReferenceEquals(session.State, world) && ReferenceEquals(world.Links.Left.Node, node) &&
                world.TransientValue == 77 && world.Links.Left.TransientValue == 99,
                "Consecutive Commit must retain domain instances and transient values.");
        }
        Inspect(directory, (store, schemas) => {
            StateRevision first = store.Read(initial);
            Require(first.LocalObjects.Count == 4 && first.LocalObjects.All(row => row.Kind == ObjectVersionKind.Base),
                "Only World, Node and two identity-distinct strings may have object rows.");
            StateRevision second = store.Read(historical);
            Require(second.LocalObjects.Count == 1 && second.LocalObjects[0].ObjectId == worldId.Value &&
                second.LocalObjects[0].Kind == ObjectVersionKind.Delta,
                "One nested leaf edit must produce exactly one owner Delta.");
            Require(store.ReadObjectVersionChain(historical, worldId.Value).Records.Count == 2, "Missing owner Base/Delta chain.");
            CheckHistoricalDto(store, schemas, historical, worldId);
        });
        WriteAddress(directory, "historical", historical, worldId);
        using EventHistoryRepository reopened = EventHistoryRepository.OpenExisting(directory, Models(), Options);
        using EventHistorySession<World> restored = reopened.Resume<World>("main");
        CheckLinks(restored.State, 12, 31);
    }
#elif HISTORY_V2 && CHILD_V2
    private static void Migrate(string directory) {
        var (historical, worldId) = ReadAddress(directory, "historical");
        FrameAddress upgraded;
        FrameAddress unchanged;
        using (EventHistoryRepository repository = EventHistoryRepository.OpenExisting(directory, Models(), Options)) {
            using EventHistorySession<World> session = repository.Resume<World>("main");
            World world = session.State;
            Node node = world.Links.Left.Node;
            Require(World.UpgradeCalls == 1 && Node.UpgradeCalls == 1, "Each stored object must be upgraded exactly once.");
            CheckLinks(world, 1012, 131);
            Require(world.Score == World.InitialScore + 100 && world.Links.Right.Value == 1021, "Owner upgrade did not rebuild nested V2 DTOs.");
            session.CommitDomainEvent(session.State, Policy);
            upgraded = session.CommitDomainState(Policy).RevisionAddress;
            session.CommitDomainEvent(session.State, Policy);
            unchanged = session.CommitDomainState(Policy).RevisionAddress;
            Require(ReferenceEquals(world, session.State) && ReferenceEquals(node, session.State.Links.Left.Node) &&
                World.UpgradeCalls == 1 && Node.UpgradeCalls == 1, "Install must retain domain instances and avoid re-upgrading.");
        }
        Inspect(directory, (store, schemas) => {
            StateRevision rewrite = store.Read(upgraded);
            Require(rewrite.LocalObjects.Count == 2 && rewrite.LocalObjects.All(row => row.Kind == ObjectVersionKind.Base) &&
                rewrite.LocalObjectIds.Contains(worldId.Value), "Schema upgrades must force current World and Node Base objects.");
            Require(store.Read(unchanged).LocalObjects.Count == 0, "The installed current DTO baseline must compare unchanged.");
            CheckHistoricalDto(store, schemas, historical, worldId);
            var dto = RevisionDecoder.Read(store, schemas, upgraded, Readers()).GetRequired(worldId).GetState<World.__DurableState.V2>();
            Require(dto.Segment0Field1.Segment0Field1.Segment0Field1 == 1012L, "Stored current owner has the wrong nested value.");
        });
        WriteAddress(directory, "migrated", upgraded, worldId);
    }
#elif HISTORY_V2
    private static void UpgradeNominalChildOnly(string directory) {
        var (_, worldId) = ReadAddress(directory, "historical");
        FrameAddress upgraded;
        FrameAddress unchanged;
        using (EventHistoryRepository repository = EventHistoryRepository.OpenExisting(directory, Models(), Options)) {
            using EventHistorySession<World> session = repository.Resume<World>("main");
            World world = session.State;
            CheckLinks(world, 1012, 231);
            Require(World.UpgradeCalls == 0 && Node.UpgradeCalls == 1,
                "A nominal child version change must not require owner or inline layout upgrades.");
            session.CommitDomainEvent(session.State, Policy);
            upgraded = session.CommitDomainState(Policy).RevisionAddress;
            session.CommitDomainEvent(session.State, Policy);
            unchanged = session.CommitDomainState(Policy).RevisionAddress;
            Require(ReferenceEquals(world, session.State), "Child migration replaced the World domain object.");
        }
        Inspect(directory, (store, schemas) => {
            StateRevision revision = store.Read(upgraded);
            Require(revision.LocalObjects.Count == 1 && revision.LocalObjects[0].ObjectId != worldId.Value &&
                revision.LocalObjects[0].Kind == ObjectVersionKind.Base, "Only the upgraded child must be rewritten.");
            Require(store.Read(unchanged).LocalObjects.Count == 0, "Child baseline did not settle after publish.");
            Require(RevisionDecoder.Read(store, schemas, upgraded, Readers()).GetRequired(worldId).Schema!.Version == 2,
                "The independent nominal upgrade changed the owner Schema.");
        });
    }
#else
    private static void DeleteInlineDomainDeclarations(string directory) {
        var (historical, worldId) = ReadAddress(directory, "historical");
        Require(typeof(World).Assembly.GetType("InlineStructPackageConsumerProbe.Point") is null &&
            typeof(World).Assembly.GetType("InlineStructPackageConsumerProbe.Links") is null,
            "No old inline domain CLR declaration may survive in the third consumer.");
        Inspect(directory, (store, schemas) => {
            CheckHistoricalDto(store, schemas, historical, worldId);
            Require(World.UpgradeCalls == 0, "Stored-exact reading must not call Upgrade.");
            World old = ReadHistorical(directory, historical);
            Require(old.Summary == 2033L && old.Score == World.InitialScore + 1100 && World.UpgradeCalls == 2 && Node.UpgradeCalls == 2,
                "Old owner must traverse V1 -> V2 -> V3 using retained historical nested DTO constructors.");
        });
        World.UpgradeCalls = Node.UpgradeCalls = 0;
        FrameAddress removed;
        using (EventHistoryRepository repository = EventHistoryRepository.OpenExisting(directory, Models(), Options)) {
            using EventHistorySession<World> session = repository.Resume<World>("main");
            World world = session.State;
            Require(world.Summary == 2033L && world.Score == World.InitialScore + 1100 && World.UpgradeCalls == 1 && Node.UpgradeCalls == 0,
                "The current V2 head must use just the remaining V2 -> V3 owner upgrade.");
            session.CommitDomainEvent(session.State, Policy);
            removed = session.CommitDomainState(Policy).RevisionAddress;
            Require(ReferenceEquals(world, session.State), "Removing inline state replaced the domain World.");
        }
        Inspect(directory, (store, schemas) => {
            StateRevision revision = store.Read(removed);
            Require(revision.LocalObjects.Count == 1 && revision.LocalObjects[0].ObjectId == worldId.Value &&
                revision.LocalObjects[0].Kind == ObjectVersionKind.Base &&
                store.ReadLiveObjectHeadMap(removed).Keys.SequenceEqual(new[] { worldId.Value }),
                "Removing nested reference slots must rewrite World and remove Node plus both strings.");
            Require(RevisionDecoder.Read(store, schemas, removed, Readers()).Objects.Count == 1, "New membership retains the old inline-referenced graph.");
            CheckHistoricalDto(store, schemas, historical, worldId);
        });
        using EventHistoryRepository reopened = EventHistoryRepository.OpenExisting(directory, Models(), Options);
        using EventHistorySession<World> current = reopened.Resume<World>("main");
        Require(current.State.Summary == 2033L && current.State.Score == World.InitialScore + 1100 &&
            reopened.GetHead("main").RevisionAddress == removed, "Published V3 head cannot be reopened after deleting inline CLR declarations.");
    }
#endif

#if !HISTORY_V3
    private static void CheckLinks(World world, long expectedLeft, long expectedNode) {
        Require(world.Links.Left.Value == expectedLeft && world.Links.Left.Node.Value == expectedNode,
            "Nested readonly numeric values were not restored.");
        Require(ReferenceEquals(world.Links.World, world) && ReferenceEquals(world.Links.Left.Node, world.Links.Right.Node),
            "Nested durable references lost the self-cycle or shared child.");
        Require(ReferenceEquals(world.Links.Left.Label, world.Links.Left.Node.Label) &&
            world.Links.Left.Label == world.Links.Right.Label && !ReferenceEquals(world.Links.Left.Label, world.Links.Right.Label),
            "Nested string fields lost sharing or distinct equal-string identity.");
        Require(world.TransientValue == 0 && world.Links.Left.TransientValue == 0 && world.Links.Right.TransientValue == 0,
            "Restore must skip constructors and initialize transient state to defaults.");
    }
#endif

    private static void CheckHistoricalDto(StateRevisionStore store, SchemaStore schemas, FrameAddress historical, ObjectId worldId) {
        DecodedRevision decoded = RevisionDecoder.Read(store, schemas, historical, Readers());
#if HISTORY_V3
        var state = decoded.GetRequired(worldId).GetState<WorldStates.V1>();
#else
        var state = decoded.GetRequired(worldId).GetState<World.__DurableState.V1>();
#endif
        Require(decoded.Objects.Count == 4 && decoded.GetRequired(worldId).Schema!.Version == 1 &&
            state.Segment0Field1.Segment0Field1.Segment0Field1 == 12 &&
            state.Segment0Field1.Segment0Field2.Segment0Field1 == 21 && state.Segment1Field1 == World.InitialScore,
            "Historical exact nested DTO/Base+Delta reconstruction changed across consumer builds.");
    }

    private static World ReadHistorical(string directory, FrameAddress address) {
        using var repository = EventHistoryRepository.OpenReadOnlyExisting(directory, Models(), Options);
        GraphFrame frame = repository.ReadFrames("main").Single(frame => frame.RevisionAddress == address);
        return repository.ReadState<World>(frame);
    }

    private static void Inspect(string directory, Action<StateRevisionStore, SchemaStore> action) {
        using var file = RbfFile.OpenReadOnlyExisting(Path.Combine(directory, "schemas.rbf"));
        using SegmentStore segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory, "state"), Options);
        using StateRevisionStore states = new(segments);
        action(states, new SchemaStore(file, readOnly: true));
    }

    private static void WriteAddress(string directory, string name, FrameAddress revision, ObjectId worldId) =>
        File.WriteAllText(Path.Combine(directory, name + ".txt"), $"{revision.FileNumber}:{revision.FrameTicket.Packed}:{worldId.Value}");

    private static (FrameAddress Revision, ObjectId WorldId) ReadAddress(string directory, string name) {
        string[] parts = File.ReadAllText(Path.Combine(directory, name + ".txt")).Split(':');
        Require(parts.Length == 3, "Malformed probe address handoff.");
        return (new FrameAddress(uint.Parse(parts[0]), SizedPtr.FromPacked(ulong.Parse(parts[1]))), new ObjectId(uint.Parse(parts[2])));
    }

    private static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
