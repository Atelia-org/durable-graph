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

namespace ListPackageConsumerProbe;

internal static class Program {
    private static readonly RbfSegmentStoreOptions Options = new() { NewStoreLayout = RbfSegmentStoreLayout.Flat };
    private static readonly ReadAmplificationBaseBudgetParameters Policy = new(int.MaxValue, 1);

    private static void Main(string[] args) {
        string directory = Path.GetFullPath(args.Single());
#if HISTORY_V1
        Seed(directory);
        Console.WriteLine("ListSeed:True:CompositionsAndCycles:True:ResizeAndCapacity:True:FrozenDelta:True:RepresentationIds:True:ReorderedRegistration:True");
#else
        Upgrade(directory);
        Console.WriteLine("ListUpgrade:True:SharedOwnerOnce:True:ForcedBaseThenDelta:True:HistoricalExact:True:DeletedDomainStruct:True:ColdReopen:True:IndependentRepresentationUpgrade:True:DeltaInheritsRepresentation:True");
#endif
    }

    private static StateModelRegistry Models() {
        StateModelRegistry models = new();
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
#if HISTORY_V2
        models.UseListElementUpgrades(typeof(ListRules));
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
        FrameAddress capacityOnly;
        List<(FrameAddress Address, int[] Values)> edits = [];
        ObjectId worldId;
        Dictionary<RepresentationId, ObjectLayout> representations = [];
        using (Repository repository = Repository.CreateNew(directory, Models(), Options)) {
            using BranchCheckout session = repository.CreateBranch("main", world, Policy);
            first = session.StateRevisionAddress!.Value;
            worldId = session.StateId!.Value;
            world.Points[0] = new() { Value = 101 };
            void SaveEdit() {
                session.CommitEvent(((World)session.State!), Policy);
                edits.Add((session.CommitState(Policy).RevisionAddress, world.Points.Select(point => point.Value).ToArray()));
            }
            SaveEdit();
            world.Points.Add(new() { Value = 999 });
            SaveEdit();
            world.Points.Insert(1, new() { Value = 222 });
            SaveEdit();
            world.Points.RemoveAt(1);
            SaveEdit();
            world.Points.RemoveAt(world.Points.Count - 1);
            SaveEdit();
            historical = edits[^1].Address;
            world.Points.Capacity += 64;
            session.CommitEvent(((World)session.State!), Policy);
            capacityOnly = session.CommitState(Policy).RevisionAddress;
            Require(ReferenceEquals(world, ((World)session.State!)) && ReferenceEquals(world.Points, ((World)session.State!).Alias), "Commit replaced World or List.");
        }
        Inspect(directory, (store, schemas) => {
            ObjectId pointsId = CheckHistorical(store, schemas, historical, worldId);
            StateRevision delta = store.Read(edits[0].Address);
            Require(delta.LocalObjects.Count == 1 && delta.LocalObjects[0].ObjectId == pointsId.Value &&
                delta.LocalObjects[0].Kind == ObjectVersionKind.Delta, "One element edit should produce one List Delta.");
            foreach ((FrameAddress address, int[] expected) in edits) {
                DecodedRevision edited = RevisionDecoder.Read(store, schemas, address, Readers());
                var values = edited.GetRequired(pointsId).GetListState<PointStates.V1>();
                Require(values.Count == expected.Length, "Resize did not retain the List ObjectId and count.");
                for (int i = 0; i < expected.Length; i++) {
                    Require(values[i].Segment0Field1 == expected[i], "A list insert, remove or append failed roundtrip.");
                }
                Require(store.Read(address).LocalObjects.All(row => row.ObjectId == pointsId.Value),
                    "An element edit or resize must not rewrite the referring World.");
            }
            Require(store.Read(capacityOnly).LocalObjects.Count == 0, "Capacity is not persistent state.");
            var initial = RevisionDecoder.Read(store, schemas, first, Readers());
            Require(initial.GetRequired(pointsId).GetListState<PointStates.V1>()[0].Segment0Field1 == 100,
                "Later domain mutation altered the original List snapshot.");
            var firstIds = CheckRepresentations(store, schemas, first, representations);
            var historicalIds = CheckRepresentations(store, schemas, historical, representations);
            Require(firstIds.Count == historicalIds.Count && firstIds.All(pair => historicalIds[pair.Key] == pair.Value),
                "A same-layout Delta changed its inherited representation ID.");
        });
        CheckReorderedRegistration(directory, representations);
        CheckSavedContentIsolation(directory + "-frozen");
        File.WriteAllText(Path.Combine(directory, "historical.txt"), $"{historical.FileNumber}:{historical.FrameTicket.Packed}:{worldId.Value}");
        using Repository reopened = Repository.OpenExisting(directory, Models(), Options);
        using BranchCheckout restored = reopened.Checkout("main");
        CheckGraph(((World)restored.State!), 101);
    }

    private static void CheckSavedContentIsolation(string directory) {
        World world = World.Seed();
        using var repository = Repository.CreateNew(directory, Models(), Options);
        using var first = repository.CreateBranch("main", world, Policy);
        world.Points.Clear();
        first.Dispose();
        using BranchCheckout loaded = repository.Checkout("main");
        Require(((World)loaded.State!).Points.Count == 32 && ((World)loaded.State!).Points[0].Value == 100, "Saved Base aliases mutable List state.");
        ((World)loaded.State!).Points[0] = new() { Value = 555 };
        ((World)loaded.State!).Points.Add(new() { Value = 777 });
        loaded.CommitEvent(((World)loaded.State!), Policy);
        CheckpointAddress changed = loaded.CommitState(Policy);
        ((World)loaded.State!).Points.Clear();
        World restored = ((World)repository.ReadState(changed));
        Require(restored.Points.Count == 33 && restored.Points[0].Value == 555 && restored.Points[^1].Value == 777,
            "Saved Delta aliases a later-cleared domain List.");
    }
#else
    private static void Upgrade(string directory) {
        Require(typeof(World).Assembly.GetType("ListPackageConsumerProbe.LegacyPoint") is null,
            "This build must delete the old inline domain CLR type while retaining its generated state history.");
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
        using (Repository repository = Repository.OpenExisting(directory, Models(), Options)) {
            using BranchCheckout session = repository.Checkout("main");
            World world = ((World)session.State!);
            var points = world.Points;
            CheckGraph(world, 1101);
            Require(Upgrades.Calls.Count == 32 && Upgrades.Calls.All(id => id == pointsId),
                "The shared list must be upgraded once, independently of its two incoming edges.");
            session.CommitEvent(((World)session.State!), Policy);
            upgraded = session.CommitState(Policy).RevisionAddress;
            session.CommitEvent(((World)session.State!), Policy);
            unchanged = session.CommitState(Policy).RevisionAddress;
            world.Points[0] = new() { Value = 1102 };
            session.CommitEvent(((World)session.State!), Policy);
            changed = session.CommitState(Policy).RevisionAddress;
            Require(ReferenceEquals(world, ((World)session.State!)) && ReferenceEquals(points, ((World)session.State!).Points) && Upgrades.Calls.Count == 32,
                "Successful commits must retain all domain instances and clear rewrite obligations.");
        }
        Inspect(directory, (store, schemas) => {
            StateRevision rewrite = store.Read(upgraded);
            Require(rewrite.LocalObjects.Count == 1 && rewrite.LocalObjects[0].ObjectId == pointsId.Value &&
                rewrite.LocalObjects[0].Kind == ObjectVersionKind.Base, "Only the upgraded list must force Base.");
            Require(store.Read(unchanged).LocalObjects.Count == 0, "The installed upgraded baseline should compare unchanged.");
            StateRevision delta = store.Read(changed);
            Require(delta.LocalObjects.Count == 1 && delta.LocalObjects[0].ObjectId == pointsId.Value &&
                delta.LocalObjects[0].Kind == ObjectVersionKind.Delta, "A subsequent edit should use ordinary list Delta.");
            var upgradedIds = CheckRepresentations(store, schemas, upgraded, representations);
            var changedIds = CheckRepresentations(store, schemas, changed, representations);
            Require(upgradedIds[pointsId.Value] != historicalIds[pointsId.Value], "The upgraded inline list layout must receive a new representation ID.");
            Require(upgradedIds[worldId.Value] == historicalIds[worldId.Value] &&
                historicalIds.All(pair => pair.Key == pointsId.Value || upgradedIds[pair.Key] == pair.Value),
                "A reference target Upgrade must not change its referring objects' representation IDs.");
            Require(upgradedIds.Count == changedIds.Count && upgradedIds.All(pair => changedIds[pair.Key] == pair.Value),
                "An ordinary Delta must inherit the upgraded Base's representation ID.");
            CheckHistorical(store, schemas, historical, worldId);
        });
        CheckReorderedRegistration(directory, representations);
        using Repository reopened = Repository.OpenExisting(directory, Models(), Options);
        using BranchCheckout restored = reopened.Checkout("main");
        CheckGraph(((World)restored.State!), 1102);
        Require(Upgrades.Calls.Count == 32 && reopened.GetHead("main").RevisionAddress == changed,
            "Current Base/Delta cold reopen should not re-run element Upgrade.");
    }
#endif

    private static ObjectId CheckHistorical(StateRevisionStore store, SchemaStore schemas, FrameAddress address, ObjectId worldId) {
        DecodedRevision decoded = RevisionDecoder.Read(store, schemas, address, Readers());
        WorldStates.V1 world = decoded.GetRequired(worldId).GetState<WorldStates.V1>();
        Require(world.Segment0Field1 == world.Segment0Field2, "Historical sharing lost the shared list ID.");
        ObjectStateRecord record = decoded.GetRequired(world.Segment0Field1);
        var points = record.GetListState<PointStates.V1>();
        Require(record.Layout.List!.ElementSlot.InlineSchema!.Version == 1 && points.Count == 32 &&
            points[0].Segment0Field1 == 101 && points[31].Segment0Field1 == 131,
            "Historical list must decode its exact old element DTO and Base/Delta chain.");
        return world.Segment0Field1;
    }

    private static void CheckGraph(World world, long first) {
        Require(world.Points[0].Value == first && world.Points.Count == 32 && ReferenceEquals(world.Points, world.Alias), "Point values or sharing lost.");
        Require(ReferenceEquals(world.Nested[0], world.Nested[1]) && ReferenceEquals(world.Nested[0], world.Box.Value) &&
            ReferenceEquals(world.Nested[0], world.Box.Items[0]) && ReferenceEquals(world.Nested[0], world.Vectors[0]) &&
            ReferenceEquals(world.Nested[0], world.Values[0].First) && world.Box.Value.SequenceEqual(new[] { 3, 5, 7 }),
            "Open T/List<T>, nested lists, arrays of lists or inline reference projection lost identity.");
        Require(world.Grids[0][0, 0].First == 9 && world.Grids[0][0, 0].Second == "list graph" &&
            ReferenceEquals(world.Grids[0][0, 0].Second, world.Values[0].Second), "List of rank-two generic struct arrays lost content.");
        Require(ReferenceEquals(world.Cycle[0], world), "List/domain cycle restoration failed.");
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
        // Schema count is intentionally not a proxy: primitive/reference lists are complete
        // representations without a corresponding user Schema registration.
    }

    private static void Require(bool condition, string message) {
        if (!condition) throw new InvalidOperationException(message);
    }
}
