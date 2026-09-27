using Atelia.Data;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using PointStates = Atelia.DurableGraph.Generated.Family_506F696E74;
using WorldStates = Atelia.DurableGraph.Generated.Family_576F726C64;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace ArrayPackageConsumerProbe;

internal static class Program {
    private static readonly RbfSegmentStoreOptions Options = new() { NewStoreLayout = RbfSegmentStoreLayout.Flat };
    private static readonly ReadAmplificationBaseBudgetParameters Policy = new(int.MaxValue, 1);

    private static void Main(string[] args) {
        string directory = Path.GetFullPath(args.Single());
#if HISTORY_V1
        Seed(directory);
        Console.WriteLine("ArraySeed:True:FourRanks:True:GenericJaggedCycles:True:FrozenDelta:True:RepresentationIds:True:ReorderedRegistration:True");
#else
        Upgrade(directory);
        Console.WriteLine("ArrayUpgrade:True:SharedOwnerOnce:True:ForcedBaseThenDelta:True:HistoricalExact:True:ColdReopen:True:IndependentRepresentationUpgrade:True:DeltaInheritsRepresentation:True");
#endif
    }

    private static StateModelRegistry Models() {
        StateModelRegistry models = new();
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
#if HISTORY_V2
        models.UseArrayElementUpgrades(typeof(ArrayRules));
#endif
        return models;
    }

    private static StateReaderRegistry Readers() {
        StateReaderRegistry readers = new();
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(readers);
        return readers;
    }

#if HISTORY_V1
    private static void Seed(string directory) {
        World world = World.Seed();
        FrameAddress first, historical;
        ObjectId worldId;
        Dictionary<RepresentationId, ObjectLayout> representations = [];
        using (EventHistoryRepository repository = EventHistoryRepository.CreateNew(directory, Models(), Options)) {
            using EventHistorySession<World> session = repository.CreateBranch("main", world, Policy);
            first = session.StateRevisionAddress;
            worldId = session.StateId;
            world.Points[0].Value = 101;
            session.CommitDomainEvent(session.State, Policy);
            historical = session.CommitDomainState(Policy).RevisionAddress;
            Require(ReferenceEquals(world, session.State), "Commit replaced World.");
        }
        Inspect(directory, (store, schemas) => {
            ObjectId pointsId = CheckHistorical(store, schemas, historical, worldId);
            StateRevision delta = store.Read(historical);
            Require(delta.LocalObjects.Count == 1 && delta.LocalObjects[0].ObjectId == pointsId.Value &&
                delta.LocalObjects[0].Kind == ObjectVersionKind.Delta, "One element edit should produce one array Delta.");
            var initial = RevisionDecoder.Read(store, schemas, first, Readers());
            Require(initial.GetRequired(pointsId).GetArrayState<PointStates.V1>()[0].Segment0Field1 == 100,
                "Later domain mutation altered the original array snapshot.");
            var firstIds = CheckRepresentations(store, schemas, first, representations);
            var historicalIds = CheckRepresentations(store, schemas, historical, representations);
            Require(firstIds.Count == historicalIds.Count && firstIds.All(pair => historicalIds[pair.Key] == pair.Value),
                "A same-layout Delta changed its inherited representation ID.");
        });
        CheckReorderedRegistration(directory, representations);
        File.WriteAllText(Path.Combine(directory, "historical.txt"), $"{historical.FileNumber}:{historical.FrameTicket.Packed}:{worldId.Value}");
        using EventHistoryRepository reopened = EventHistoryRepository.OpenExisting(directory, Models(), Options);
        using EventHistorySession<World> restored = reopened.Resume<World>("main");
        CheckGraph(restored.State, 101);
    }
#else
    private static void Upgrade(string directory) {
        string[] parts = File.ReadAllText(Path.Combine(directory, "historical.txt")).Split(':');
        FrameAddress historical = new(uint.Parse(parts[0]), SizedPtr.FromPacked(ulong.Parse(parts[1])));
        ObjectId worldId = new(uint.Parse(parts[2]));
        ObjectId pointsId = default;
        Dictionary<uint, RepresentationId> historicalIds = [];
        Dictionary<RepresentationId, ObjectLayout> representations = [];
        Inspect(directory, (store, schemas) => {
            pointsId = CheckHistorical(store, schemas, historical, worldId);
            historicalIds = CheckRepresentations(store, schemas, historical, representations);
        });
        Require(Upgrades.Calls.Count == 0, "Stored-exact decoding invoked business Upgrade.");
        FrameAddress upgraded, unchanged, changed;
        using (EventHistoryRepository repository = EventHistoryRepository.OpenExisting(directory, Models(), Options)) {
            using EventHistorySession<World> session = repository.Resume<World>("main");
            World world = session.State;
            Point[] points = world.Points;
            CheckGraph(world, 1101);
            Require(Upgrades.Calls.Count == 32 && Upgrades.Calls.All(id => id == pointsId),
                "The shared array must be upgraded once, independently of its two incoming edges.");
            session.CommitDomainEvent(session.State, Policy);
            upgraded = session.CommitDomainState(Policy).RevisionAddress;
            session.CommitDomainEvent(session.State, Policy);
            unchanged = session.CommitDomainState(Policy).RevisionAddress;
            world.Points[0].Value = 1102;
            session.CommitDomainEvent(session.State, Policy);
            changed = session.CommitDomainState(Policy).RevisionAddress;
            Require(ReferenceEquals(world, session.State) && ReferenceEquals(points, session.State.Points) && Upgrades.Calls.Count == 32,
                "Successful commits must retain all domain instances and clear rewrite obligations.");
        }
        Inspect(directory, (store, schemas) => {
            StateRevision rewrite = store.Read(upgraded);
            Require(rewrite.LocalObjects.Count == 1 && rewrite.LocalObjects[0].ObjectId == pointsId.Value &&
                rewrite.LocalObjects[0].Kind == ObjectVersionKind.Base, "Only the upgraded array must force Base.");
            Require(store.Read(unchanged).LocalObjects.Count == 0, "The installed upgraded baseline should compare unchanged.");
            StateRevision delta = store.Read(changed);
            Require(delta.LocalObjects.Count == 1 && delta.LocalObjects[0].ObjectId == pointsId.Value &&
                delta.LocalObjects[0].Kind == ObjectVersionKind.Delta, "A subsequent edit should use ordinary array Delta.");
            var upgradedIds = CheckRepresentations(store, schemas, upgraded, representations);
            var changedIds = CheckRepresentations(store, schemas, changed, representations);
            Require(upgradedIds[pointsId.Value] != historicalIds[pointsId.Value], "The upgraded inline array layout must receive a new representation ID.");
            Require(upgradedIds[worldId.Value] == historicalIds[worldId.Value] &&
                historicalIds.All(pair => pair.Key == pointsId.Value || upgradedIds[pair.Key] == pair.Value),
                "A reference target Upgrade must not change its referring objects' representation IDs.");
            Require(upgradedIds.Count == changedIds.Count && upgradedIds.All(pair => changedIds[pair.Key] == pair.Value),
                "An ordinary Delta must inherit the upgraded Base's representation ID.");
            CheckHistorical(store, schemas, historical, worldId);
        });
        CheckReorderedRegistration(directory, representations);
        using EventHistoryRepository reopened = EventHistoryRepository.OpenExisting(directory, Models(), Options);
        using EventHistorySession<World> restored = reopened.Resume<World>("main");
        CheckGraph(restored.State, 1102);
        Require(Upgrades.Calls.Count == 32 && reopened.GetHead("main").RevisionAddress == changed,
            "Current Base/Delta cold reopen should not re-run element Upgrade.");
    }
#endif

    private static ObjectId CheckHistorical(StateRevisionStore store, SchemaStore schemas, FrameAddress address, ObjectId worldId) {
        DecodedRevision decoded = RevisionDecoder.Read(store, schemas, address, Readers());
        WorldStates.V1 world = decoded.GetRequired(worldId).GetState<WorldStates.V1>();
        Require(world.Segment0Field1 == world.Segment0Field2, "Historical sharing lost the shared array ID.");
        ObjectStateRecord record = decoded.GetRequired(world.Segment0Field1);
        var points = record.GetArrayState<PointStates.V1>();
        Require(record.Layout.Array!.ElementSlot.InlineSchema!.Version == 1 && points.Shape.Count == 32 &&
            points[0].Segment0Field1 == 101 && points[31].Segment0Field1 == 131,
            "Historical array must decode its exact old element DTO and Base/Delta chain.");
        return world.Segment0Field1;
    }

    private static void CheckGraph(World world, long first) {
        Require(world.Points[0].Value == first && world.Points.Length == 32 && ReferenceEquals(world.Points, world.Alias), "Point values or sharing lost.");
        Require(ReferenceEquals(world.Jagged[0], world.Jagged[1]) && ReferenceEquals(world.Jagged[0], world.Box.Value) &&
            ReferenceEquals(world.Jagged[0], world.Box.Items[0]) && world.Box.Value[2] == 7, "Open T[] and T-to-array projection lost identity.");
        Require(world.Grid[0, 0].First == 9 && world.Grid[0, 0].Second == "array graph" &&
            world.Cube[0, 1, 0] == 13 && world.Hyper[0, 0, 0, 1] == 19, "Rank or generic struct element layout lost.");
        Require(ReferenceEquals(world.Cycle[0], world), "Array/domain cycle restoration failed.");
    }

    private static void Inspect(string directory, Action<StateRevisionStore, SchemaStore> action) {
        using var file = RbfFile.OpenReadOnlyExisting(Path.Combine(directory, "schemas.rbf"));
        using SegmentStore segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory, "state"), Options);
        using StateRevisionStore states = new(segments);
        action(states, new SchemaStore(file, readOnly: true));
    }

    private static Dictionary<uint, RepresentationId> CheckRepresentations(StateRevisionStore store, SchemaStore schemas,
        FrameAddress address, Dictionary<RepresentationId, ObjectLayout> representations) {
        Dictionary<uint, RepresentationId> ids = [];
        DecodedRevision decoded = RevisionDecoder.Read(store, schemas, address, Readers());
        foreach (uint id in store.ReadLiveObjectHeadMap(address).Keys) {
            ObjectVersionChain chain = store.ReadObjectVersionChain(address, id);
            ReadOnlySpan<byte> bytes = chain.Records[0].Record.Body;
            BinaryPayloadReader header = new(bytes);
            Require(header.ReadByte() == 4, "New object Bases must use the representation-ID envelope.");
            RepresentationId representation = new(header.ReadUInt32());
            ObjectLayout layout = schemas.GetRepresentation(representation);
            ObjectStateRecord exact = decoded.GetRequired(new ObjectId(id));
            Require(layout.Equals(exact.Layout), "Representation lookup changed the complete persisted layout.");
            Require(layout.Kind != ObjectStateKind.String || representation == RepresentationId.String,
                "String must use the built-in representation ID.");
            if (layout.Kind == ObjectStateKind.String) {
                Require(header.ReadString() == exact.StringContent, "The representation ID must be followed immediately by raw string content.");
                header.EnsureFullyConsumed();
            }
            if (representations.TryGetValue(representation, out ObjectLayout? previous)) {
                Require(previous.Equals(layout), "A persisted representation ID changed meaning.");
            }
            representations[representation] = layout;
            ids.Add(id, representation);
        }
        return ids;
    }

    private static void CheckReorderedRegistration(string directory, Dictionary<RepresentationId, ObjectLayout> representations) {
        // The read handles and repository have closed. Reopen a fresh writable directory and
        // request the complete set in reverse order; no process-local cache can supply its IDs.
        using var file = RbfFile.OpenExisting(Path.Combine(directory, "schemas.rbf"));
        SchemaStore schemas = new(file);
        long before = file.TailOffset;
        var requested = representations.OrderByDescending(pair => pair.Key.Value).ToArray();
        RepresentationId[] actual = schemas.RegisterRepresentations(requested.Select(pair => pair.Value).ToArray());
        Require(actual.SequenceEqual(requested.Select(pair => pair.Key)), "Reopening or request order reassigned persisted IDs.");
        Require(file.TailOffset == before, "Registering existing representations must not append metadata.");
        // Schema count is intentionally not a proxy: primitive/reference arrays are complete
        // representations without a corresponding user Schema registration.
    }

    private static void Require(bool condition, string message) {
        if (!condition) throw new InvalidOperationException(message);
    }
}
