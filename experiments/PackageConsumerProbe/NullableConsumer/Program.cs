using Atelia.Data;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using PointStates = Atelia.DurableGraph.Generated.Family_506F696E74;
using WorldStates = Atelia.DurableGraph.Generated.Family_576F726C64;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace NullablePackageConsumerProbe;

internal static class Program {
    private static readonly RbfSegmentStoreOptions Options = new() { NewStoreLayout = RbfSegmentStoreLayout.Flat };
    private static readonly ReadAmplificationBaseBudgetParameters Policy = new(int.MaxValue, 1);

    private static void Main(string[] args) {
        string directory = Path.GetFullPath(args.Single());
#if HISTORY_V1
        Seed(directory);
        Console.WriteLine("NullableSeed:True:SharedCycles:True:VectorAndRank4:True:FrozenPreparation:True:HistoricalDelta:True");
#else
        Upgrade(directory);
        Console.WriteLine("NullableUpgrade:True:LiftedOwnerAndElements:True:ForcedBaseThenDelta:True:HistoricalExact:True:DeletedDomainStruct:True:ColdReopen:True:ClearRemovesIsland:True");
#endif
    }

    private static StateModelRegistry Models() {
        var models = new StateModelRegistry();
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
#if HISTORY_V2
        models.UseListElementUpgrades(typeof(NullableRules));
        models.UseArrayElementUpgrades(typeof(NullableRules));
#endif
        return models;
    }

    private static StateReaderRegistry Readers() {
        var readers = new StateReaderRegistry();
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(readers);
        return readers;
    }

#if HISTORY_V1
    private static void Seed(string directory) {
        World world = World.Seed();
        FrameAddress first, historical, unchanged;
        ObjectId worldId;
        using (Repository repository = Repository.CreateNew(directory, Models(), Options)) {
            using BranchCheckout session = repository.CreateBranch("main", world, Policy);
            first = session.StateRevisionAddress!.Value;
            worldId = session.StateId!.Value;
            var point = world.Points[0]!.Value;
            point.Value = 101;
            world.Points[0] = point;
            session.CommitEvent(((World)session.State!), Policy);
            historical = session.CommitState(Policy).RevisionAddress;
            session.CommitEvent(((World)session.State!), Policy);
            unchanged = session.CommitState(Policy).RevisionAddress;
            Require(ReferenceEquals(world, ((World)session.State!)), "Commit replaced the working domain instance.");
        }
        Inspect(directory, (store, schemas) => {
            var ids = CheckHistorical(store, schemas, historical, worldId);
            StateRevision delta = store.Read(historical);
            Require(delta.LocalObjects.Count == 1 && delta.LocalObjects[0].ObjectId == ids.List.Value &&
                delta.LocalObjects[0].Kind == ObjectVersionKind.Delta, "One nullable element patch must produce one List Delta.");
            Require(store.Read(unchanged).LocalObjects.Count == 0, "Nullable values produced a false change.");
            var initial = RevisionDecoder.Read(store, schemas, first, Readers());
            Require(initial.GetRequired(ids.List).GetListState<NullableState<PointStates.V1>>()[0].Value.Segment0Field1 == 100,
                "An edited domain value changed the earlier frozen state.");
        });
        File.WriteAllText(Path.Combine(directory, "historical.txt"), $"{historical.FileNumber}:{historical.FrameTicket.Packed}:{worldId.Value}");
        using (Repository repository = Repository.OpenExisting(directory, Models(), Options)) {
            using BranchCheckout session = repository.Checkout("main");
            CheckGraph(((World)session.State!), 101, 0);
        }
        CheckSavedContentIsolation(directory + "-frozen");
    }

    private static void CheckSavedContentIsolation(string directory) {
        World world = World.Seed();
        using var repository = Repository.CreateNew(directory, Models(), Options);
        using var first = repository.CreateBranch("main", world, Policy);
        world.Optional = null;
        world.Points.Clear();
        world.Vector[0] = null;
        world.Grid[0, 0, 0, 1] = null;
        first.Dispose();
        using BranchCheckout loaded = repository.Checkout("main");
        CheckGraph(((World)loaded.State!), 100, 0);
        var point = ((World)loaded.State!).Points[0]!.Value;
        point.Value = 555;
        ((World)loaded.State!).Points[0] = point;
        loaded.CommitEvent(((World)loaded.State!), Policy);
        CheckpointAddress changed = loaded.CommitState(Policy);
        ((World)loaded.State!).Points.Clear();
        World restored = ((World)repository.ReadState(changed));
        CheckGraph(restored, 555, 0);
    }
#else
    private static void Upgrade(string directory) {
        Require(typeof(World).Assembly.GetType("NullablePackageConsumerProbe.LegacyPoint") is null,
            "The historical domain struct must be absent from the current assembly.");
        string[] parts = File.ReadAllText(Path.Combine(directory, "historical.txt")).Split(':');
        FrameAddress historical = new(uint.Parse(parts[0]), SizedPtr.FromPacked(ulong.Parse(parts[1])));
        ObjectId worldId = new(uint.Parse(parts[2]));
        (ObjectId List, ObjectId Vector, ObjectId Grid, ObjectId Node) ids = default;
        Inspect(directory, (store, schemas) => ids = CheckHistorical(store, schemas, historical, worldId));
        Require(Upgrades.Calls.Count == 0 && Upgrades.OwnerCalls == 0, "Exact decoding invoked business conversion.");
        FrameAddress upgraded, unchanged, changed, removed;
        using (Repository repository = Repository.OpenExisting(directory, Models(), Options)) {
            using BranchCheckout session = repository.Checkout("main");
            World world = ((World)session.State!);
            var points = world.Points;
            CheckGraph(world, 1101, 1000);
            Require(Upgrades.OwnerCalls == 1 && Upgrades.Calls.Count == 35 &&
                Upgrades.Calls.Count(call => call.Id == worldId) == 1 &&
                Upgrades.Calls.Count(call => call.Id == ids.List) == 31 &&
                Upgrades.Calls.Count(call => call.Id == ids.Vector) == 2 &&
                Upgrades.Calls.Count(call => call.Id == ids.Grid) == 1,
                "Lifted providers must run only for present values, once per owner, independent of incoming references.");
            session.CommitEvent(((World)session.State!), Policy);
            upgraded = session.CommitState(Policy).RevisionAddress;
            session.CommitEvent(((World)session.State!), Policy);
            unchanged = session.CommitState(Policy).RevisionAddress;
            var point = world.Points[0]!.Value;
            point.Value = 1102;
            world.Points[0] = point;
            session.CommitEvent(((World)session.State!), Policy);
            changed = session.CommitState(Policy).RevisionAddress;
            Require(ReferenceEquals(world, ((World)session.State!)) && ReferenceEquals(points, world.Points), "Commit replaced working instances.");
        }
        Inspect(directory, (store, schemas) => {
            StateRevision rewrite = store.Read(upgraded);
            HashSet<uint> expected = [worldId.Value, ids.List.Value, ids.Vector.Value, ids.Grid.Value];
            Require(rewrite.LocalObjects.Count == 4 && rewrite.LocalObjects.All(row =>
                row.Kind == ObjectVersionKind.Base && expected.Contains(row.ObjectId)), "Every upgraded owner must rewrite Base, with no child rewrite.");
            Require(store.Read(unchanged).LocalObjects.Count == 0, "Installed upgraded baseline must compare unchanged.");
            StateRevision delta = store.Read(changed);
            Require(delta.LocalObjects.Count == 1 && delta.LocalObjects[0].ObjectId == ids.List.Value &&
                delta.LocalObjects[0].Kind == ObjectVersionKind.Delta, "Same-layout nullable edit must resume ordinary Delta.");
            CheckHistorical(store, schemas, historical, worldId);
        });
        using (Repository repository = Repository.OpenExisting(directory, Models(), Options)) {
            using BranchCheckout session = repository.Checkout("main");
            CheckGraph(((World)session.State!), 1102, 1000);
            Require(Upgrades.Calls.Count == 35 && Upgrades.OwnerCalls == 1, "Current Base/Delta reopen re-ran Upgrade.");
            ((World)session.State!).Optional = null;
            for (int i = 0; i < ((World)session.State!).Points.Count; i++) ((World)session.State!).Points[i] = null;
            Array.Clear(((World)session.State!).Vector);
            Array.Clear(((World)session.State!).Grid);
            session.CommitEvent(((World)session.State!), Policy);
            removed = session.CommitState(Policy).RevisionAddress;
        }
        Inspect(directory, (store, schemas) => {
            Require(!store.ReadLiveObjectHeadMap(removed).ContainsKey(ids.Node.Value), "Absent values retained a now-unreachable cyclic node.");
            Require(store.ReadLiveObjectHeadMap(removed).Count == 4, "Clearing all nullable references must remove the node and its string, retaining four owners.");
            CheckHistorical(store, schemas, historical, worldId);
        });
        using Repository finalRepository = Repository.OpenExisting(directory, Models(), Options);
        using BranchCheckout finalSession = finalRepository.Checkout("main");
        Require(((World)finalSession.State!).Optional is null && ((World)finalSession.State!).Points.All(point => point is null) &&
            ReferenceEquals(((World)finalSession.State!).Points, ((World)finalSession.State!).Alias), "All-null state or shared identity failed cold restoration.");
    }
#endif

    private static (ObjectId List, ObjectId Vector, ObjectId Grid, ObjectId Node) CheckHistorical(
        StateRevisionStore store, SchemaStore schemas, FrameAddress address, ObjectId worldId) {
        DecodedRevision decoded = RevisionDecoder.Read(store, schemas, address, Readers());
        var world = decoded.GetRequired(worldId).GetState<WorldStates.V1<NullableState<PointStates.V1>, NullableState<int>>>();
        Require(world.Segment0Field1.HasValue && world.Segment0Field1.Value.Segment0Field1 == 41 &&
            world.Segment0Field2 == world.Segment0Field3, "Historical owner nullable state or shared list ID changed.");
        ObjectStateRecord listRecord = decoded.GetRequired(world.Segment0Field2);
        var list = listRecord.GetListState<NullableState<PointStates.V1>>();
        Require(listRecord.Layout.List!.ElementSlot.ValueSchema!.Version == 1 && list.Count == 32 &&
            list[0].Value.Segment0Field1 == 101 && !list[7].HasValue && list[31].Value.Segment0Field1 == 131,
            "Historical nullable list did not retain its exact child version, absent value and Delta.");
        var vector = decoded.GetRequired(world.Segment0Field4).GetArrayState<NullableState<PointStates.V1>>();
        var grid = decoded.GetRequired(world.Segment0Field5).GetArrayState<NullableState<PointStates.V1>>();
        Require(vector.Elements.Length == 3 && !vector.Elements[1].HasValue && grid.Elements.Length == 2 && !grid.Elements[0].HasValue,
            "Historical array element nullable states failed decoding.");
        return (world.Segment0Field2, world.Segment0Field4, world.Segment0Field5, world.Segment0Field1.Value.Segment0Field2);
    }

    private static void CheckGraph(World world, long first, long offset) {
        Require(world.Optional!.Value.Value == 41 + offset && world.Number == -17 && world.Points.Count == 32 &&
            world.Points[0]!.Value.Value == first && world.Points[7] is null && ReferenceEquals(world.Points, world.Alias),
            "Nullable values or shared list identity were not restored.");
        Node node = world.Optional.Value.Link!;
        Require(ReferenceEquals(node.Owner, world) && ReferenceEquals(node.Self, node) &&
            world.Points.Where(point => point.HasValue).All(point => ReferenceEquals(point!.Value.Link, node)),
            "Nullable struct references did not preserve sharing and cycles.");
        Require(world.Vector.Length == 3 && world.Vector[1] is null && world.Vector[0]!.Value.Value == 61 + offset &&
            world.Grid.Rank == 4 && world.Grid[0, 0, 0, 0] is null && world.Grid[0, 0, 0, 1]!.Value.Value == 81 + offset &&
            ReferenceEquals(world.Vector[2]!.Value.Link, node) && ReferenceEquals(world.Grid[0, 0, 0, 1]!.Value.Link, node),
            "Nullable vector/rank-four element composition failed.");
    }

    private static void Inspect(string directory, Action<StateRevisionStore, SchemaStore> action) {
        using var file = RbfFile.OpenReadOnlyExisting(Path.Combine(directory, "schemas.rbf"));
        using SegmentStore segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory, "state"), Options);
        using StateRevisionStore states = new(segments);
        action(states, new SchemaStore(file, readOnly: true));
    }

    private static void Require(bool condition, string message) {
        if (!condition) throw new InvalidOperationException(message);
    }
}
