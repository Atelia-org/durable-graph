using Atelia.Data;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using ModeStates = Atelia.DurableGraph.Generated.Family_4D6F6465;
using CellStates = Atelia.DurableGraph.Generated.Family_43656C6C;
using BoxStates = Atelia.DurableGraph.Generated.Family_426F78;
using WorldStates = Atelia.DurableGraph.Generated.Family_576F726C64;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;
#if HISTORY_V1
using Mode = EnumPackageConsumerProbe.LegacyMode;
#else
using Mode = EnumPackageConsumerProbe.CurrentMode;
#endif

namespace EnumPackageConsumerProbe;

internal static class Program {
    private static readonly RbfSegmentStoreOptions Options = new() { NewStoreLayout = RbfSegmentStoreLayout.Flat };
    private static readonly ReadAmplificationBaseBudgetParameters Policy = new(int.MaxValue, 1);

    private static void Main(string[] args) {
        string directory = Path.GetFullPath(args.Single());
#if HISTORY_V1
        Seed(directory);
        Console.WriteLine("EnumSeed:True:UnknownBits:True:GenericNullableAndArrays:True:SharedListDelta:True:FrozenPreparation:True");
#else
        Upgrade(directory);
        Console.WriteLine("EnumUpgrade:True:HistoricalExact:True:DeletedDomainEnum:True:ExplicitLiftAndOwners:True:ForcedBaseThenDelta:True:ColdReopen:True");
#endif
    }

    private static StateModelRegistry Models() {
        var models = new StateModelRegistry();
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
#if HISTORY_V2
        models.UseListElementUpgrades(typeof(EnumRules));
        models.UseArrayElementUpgrades(typeof(EnumRules));
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
        using (EventHistoryRepository repository = EventHistoryRepository.CreateNew(directory, Models(), Options)) {
            using EventHistorySession<World> session = repository.CreateBranch("main", world, Policy);
            first = session.StateRevisionAddress;
            worldId = session.StateId;
            world.Modes[0] = (Mode)101;
            session.CommitDomainEvent(session.State, Policy);
            historical = session.CommitDomainState(Policy).RevisionAddress;
            session.CommitDomainEvent(session.State, Policy);
            unchanged = session.CommitDomainState(Policy).RevisionAddress;
            Require(ReferenceEquals(world, session.State), "Commit replaced the working domain instance.");
        }
        Inspect(directory, (store, schemas) => {
            var ids = CheckHistorical(store, schemas, historical, worldId);
            RequireListDelta(store.Read(historical), ids.List);
            Require(store.Read(unchanged).LocalObjects.Count == 0, "Enum values produced a false change.");
            var initial = RevisionDecoder.Read(store, schemas, first, Readers());
            Require(initial.GetRequired(ids.List).GetListState<ModeStates.V1>()[0].Segment0Field1 == 100,
                "An edited domain value changed the earlier frozen state.");
        });
        File.WriteAllText(Path.Combine(directory, "historical.txt"), $"{historical.FileNumber}:{historical.FrameTicket.Packed}:{worldId.Value}");
        using (EventHistoryRepository repository = EventHistoryRepository.OpenExisting(directory, Models(), Options)) {
            using EventHistorySession<World> session = repository.Resume<World>("main");
            CheckGraph(session.State, 101, 0);
        }
        CheckSavedContentIsolation(directory + "-frozen");
    }

    private static void CheckSavedContentIsolation(string directory) {
        World world = World.Seed();
        using var repository = EventHistoryRepository.CreateNew(directory, Models(), Options);
        using var first = repository.CreateBranch("main", world, Policy);
        world.Mode = default;
        world.Optional = null;
        world.Modes.Clear();
        Array.Clear(world.Vector);
        Array.Clear(world.Grid);
        world.Box.Value = null;
        world.Cell = default;
        first.Dispose();
        using EventHistorySession<World> loaded = repository.Resume<World>("main");
        CheckGraph(loaded.State, 100, 0);
        loaded.State.Modes[0] = (Mode)555;
        loaded.CommitDomainEvent(loaded.State, Policy);
        GraphFrame changed = loaded.CommitDomainState(Policy);
        loaded.State.Modes.Clear();
        World restored = repository.ReadState<World>(changed);
        CheckGraph(restored, 555, 0);
    }
#else
    private static void Upgrade(string directory) {
        Require(typeof(World).Assembly.GetType("EnumPackageConsumerProbe.LegacyMode") is null,
            "The historical domain enum must be absent from the current assembly.");
        string[] parts = File.ReadAllText(Path.Combine(directory, "historical.txt")).Split(':');
        FrameAddress historical = new(uint.Parse(parts[0]), SizedPtr.FromPacked(ulong.Parse(parts[1])));
        ObjectId worldId = new(uint.Parse(parts[2]));
        (ObjectId List, ObjectId Vector, ObjectId Grid, ObjectId Box) ids = default;
        Inspect(directory, (store, schemas) => ids = CheckHistorical(store, schemas, historical, worldId));
        Require(Upgrades.Calls.Count == 0 && Upgrades.OwnerCalls == 0, "Exact decoding invoked business conversion.");
        FrameAddress upgraded, unchanged, changed;
        using (EventHistoryRepository repository = EventHistoryRepository.OpenExisting(directory, Models(), Options)) {
            using EventHistorySession<World> session = repository.Resume<World>("main");
            World world = session.State;
            var modes = world.Modes;
            CheckGraph(world, 1101, 1000);
            Require(Upgrades.OwnerCalls == 2 && Upgrades.Calls.Count == 39 &&
                Upgrades.Calls.Count(id => id == worldId) == 3 &&
                Upgrades.Calls.Count(id => id == ids.List) == 32 &&
                Upgrades.Calls.Count(id => id == ids.Vector) == 2 &&
                Upgrades.Calls.Count(id => id == ids.Grid) == 1 &&
                Upgrades.Calls.Count(id => id == ids.Box) == 1,
                "Enum conversion must run once per present value and shared owner; absent values do not call business code.");
            session.CommitDomainEvent(session.State, Policy);
            upgraded = session.CommitDomainState(Policy).RevisionAddress;
            session.CommitDomainEvent(session.State, Policy);
            unchanged = session.CommitDomainState(Policy).RevisionAddress;
            world.Modes[0] = (Mode)1102;
            session.CommitDomainEvent(session.State, Policy);
            changed = session.CommitDomainState(Policy).RevisionAddress;
            Require(ReferenceEquals(world, session.State) && ReferenceEquals(modes, world.Modes), "Commit replaced working instances.");
        }
        Inspect(directory, (store, schemas) => {
            StateRevision rewrite = store.Read(upgraded);
            HashSet<uint> expected = [worldId.Value, ids.List.Value, ids.Vector.Value, ids.Grid.Value, ids.Box.Value];
            Require(rewrite.LocalObjects.Count == 5 && rewrite.LocalObjects.All(row =>
                row.Kind == ObjectVersionKind.Base && expected.Contains(row.ObjectId)), "Every upgraded owner must rewrite Base; inline enums must not become objects.");
            Require(store.Read(unchanged).LocalObjects.Count == 0, "Installed upgraded baseline must compare unchanged.");
            RequireListDelta(store.Read(changed), ids.List);
            CheckHistorical(store, schemas, historical, worldId);
        });
        using EventHistoryRepository finalRepository = EventHistoryRepository.OpenExisting(directory, Models(), Options);
        using EventHistorySession<World> finalSession = finalRepository.Resume<World>("main");
        CheckGraph(finalSession.State, 1102, 1000);
        Require(Upgrades.Calls.Count == 39 && Upgrades.OwnerCalls == 2, "Current Base/Delta reopen re-ran Upgrade.");
    }
#endif

    private static (ObjectId List, ObjectId Vector, ObjectId Grid, ObjectId Box) CheckHistorical(
        StateRevisionStore store, SchemaStore schemas, FrameAddress address, ObjectId worldId) {
        DecodedRevision decoded = RevisionDecoder.Read(store, schemas, address, Readers());
        var world = decoded.GetRequired(worldId).GetState<WorldStates.V1<NullableState<ModeStates.V1>, CellStates.V1<ModeStates.V1>>>();
        Require(world.Segment0Field1.Segment0Field1 == -17 && world.Segment0Field2.HasValue &&
            world.Segment0Field2.Value.Segment0Field1 == 41 && world.Segment0Field3 == world.Segment0Field4,
            "Historical enum values or shared list IDs changed.");
        ObjectStateRecord listRecord = decoded.GetRequired(world.Segment0Field3);
        var list = listRecord.GetListState<ModeStates.V1>();
        Require(listRecord.Layout.List!.ElementSlot.ValueSchema!.Version == 1 && list.Count == 32 &&
            list[0].Segment0Field1 == 101 && list[31].Segment0Field1 == 131,
            "Historical enum List did not retain its exact child version and Delta.");
        var vector = decoded.GetRequired(world.Segment0Field5).GetArrayState<ModeStates.V1>();
        var grid = decoded.GetRequired(world.Segment0Field6).GetArrayState<NullableState<ModeStates.V1>>();
        Require(vector.Elements.Length == 2 && vector.Elements[0].Segment0Field1 == int.MinValue &&
            vector.Elements[1].Segment0Field1 == int.MaxValue && grid.Elements.Length == 2 &&
            !grid.Elements[0].HasValue && grid.Elements[1].Value.Segment0Field1 == 81,
            "Historical unknown enum bits or nullable rank-four elements failed decoding.");
        Require(world.Segment0Field8.Segment0Field1.Segment0Field1 == 61,
            "Historical generic inline enum did not retain its exact state.");
        var box = decoded.GetRequired(world.Segment0Field7).GetState<BoxStates.V1<NullableState<ModeStates.V1>>>();
        Require(box.Segment0Field1.HasValue && box.Segment0Field1.Value.Segment0Field1 == 51,
            "Historical generic T? enum did not retain its exact state.");
        Require(store.ReadLiveObjectHeadMap(address).Count == 5, "Inline enum/Cell unexpectedly acquired an object row.");
        return (world.Segment0Field3, world.Segment0Field5, world.Segment0Field6, world.Segment0Field7);
    }

    private static void CheckGraph(World world, long first, long offset) {
        Require((long)world.Mode == -17 + offset && (long)world.Optional!.Value == 41 + offset &&
            world.Modes.Count == 32 && (long)world.Modes[0] == first && ReferenceEquals(world.Modes, world.Alias),
            "Enum values or shared list identity were not restored.");
        Require(world.Vector.Length == 2 && (long)world.Vector[0] == int.MinValue + offset &&
            (long)world.Vector[1] == int.MaxValue + offset && world.Grid.Rank == 4 &&
            world.Grid[0, 0, 0, 0] is null && (long)world.Grid[0, 0, 0, 1]!.Value == 81 + offset,
            "Unknown enum values and nullable array composition failed.");
        Require((long)world.Box.Value!.Value == 51 + offset && (long)world.Cell.Value == 61 + offset,
            "Generic T? and inline generic enum states failed restoration.");
    }

    private static void RequireListDelta(StateRevision revision, ObjectId id) =>
        Require(revision.LocalObjects.Count == 1 && revision.LocalObjects[0].ObjectId == id.Value &&
            revision.LocalObjects[0].Kind == ObjectVersionKind.Delta, "One enum edit must produce one List Delta.");

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
