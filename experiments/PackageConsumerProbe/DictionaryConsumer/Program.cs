using Atelia.Data;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using ModeStates = Atelia.DurableGraph.Generated.Family_4D6F6465;
using PointStates = Atelia.DurableGraph.Generated.Family_506F696E74;
using WorldStates = Atelia.DurableGraph.Generated.Family_576F726C64;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;
#if HISTORY_V1
using Mode = DictionaryPackageConsumerProbe.LegacyMode;
using Point = DictionaryPackageConsumerProbe.LegacyPoint;
#else
using Mode = DictionaryPackageConsumerProbe.CurrentMode;
using Point = DictionaryPackageConsumerProbe.CurrentPoint;
#endif

namespace DictionaryPackageConsumerProbe;

internal static class Program {
    private static readonly RbfSegmentStoreOptions Options = new() { NewStoreLayout = RbfSegmentStoreLayout.Flat };
    private static readonly ReadAmplificationBaseBudgetParameters Policy = new(int.MaxValue, 1);

    private static void Main(string[] args) {
        string directory = Path.GetFullPath(args.Single());
#if HISTORY_V1
        Seed(directory);
        Console.WriteLine("DictionarySeed:True:SharedComparersAndCycles:True:KeyAddressedDelta:True:UnorderedNoChange:True:FrozenPreparation:True");
#else
        Upgrade(directory);
        Console.WriteLine("DictionaryUpgrade:True:HistoricalExact:True:DeletedKeyAndValueTypes:True:IndependentKeyValueRules:True:SharedOwnerOnce:True:ForcedBaseThenDelta:True:ColdReopen:True");
#endif
    }

    private static StateModelRegistry Models() {
        var models = new StateModelRegistry();
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
#if HISTORY_V2
        models.UseDictionaryKeyUpgrades(typeof(KeyRules));
        models.UseDictionaryValueUpgrades(typeof(ValueRules));
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
        FrameAddress first, patched, churned, edited, reordered, historical, unchanged;
        ObjectId worldId;
        using (EventHistoryRepository repository = EventHistoryRepository.CreateNew(directory, Models(), Options)) {
            using EventHistorySession<World> session = repository.CreateBranch("main", world, Policy);
            first = session.StateRevisionAddress;
            worldId = session.StateId;
            Set(world.Points, world.FirstKey, 101);
            session.CommitDomainEvent(session.State, Policy);
            patched = session.CommitDomainState(Policy).RevisionAddress;
            Node node = world.Points[world.FirstKey].Link!;
            Require(world.Points.Remove("key-005"), "Seed key missing before removal.");
            world.Points.Add(new("new-key".ToCharArray()), new Point { Value = 999, Link = node });
            session.CommitDomainEvent(session.State, Policy);
            churned = session.CommitDomainState(Policy).RevisionAddress;
            Set(world.Points, "key-006", 606);
            session.CommitDomainEvent(session.State, Policy);
            edited = session.CommitDomainState(Policy).RevisionAddress;
            var reverse = world.Points.Reverse().ToArray();
            world.Points.Clear();
            foreach (var entry in reverse) world.Points.Add(entry.Key, entry.Value);
            world.Points.EnsureCapacity(1000);
            session.CommitDomainEvent(session.State, Policy);
            reordered = session.CommitDomainState(Policy).RevisionAddress;
            Set(world.Points, "key-007", 707);
            session.CommitDomainEvent(session.State, Policy);
            historical = session.CommitDomainState(Policy).RevisionAddress;
            session.CommitDomainEvent(session.State, Policy);
            unchanged = session.CommitDomainState(Policy).RevisionAddress;
            Require(ReferenceEquals(world, session.State) && ReferenceEquals(world.Points, world.Alias),
                "Commit replaced the working domain or shared Dictionary instance.");
        }
        Inspect(directory, (store, schemas) => {
            var ids = CheckHistorical(store, schemas, historical, worldId, 101, 606, 707, true);
            RequireDictionaryDelta(store.Read(patched), ids.Points, onlyObject: true);
            RequireDictionaryDelta(store.Read(churned), ids.Points, onlyObject: false);
            RequireDictionaryDelta(store.Read(edited), ids.Points, onlyObject: true);
            RequireDictionaryDelta(store.Read(historical), ids.Points, onlyObject: true);
            Require(store.Read(reordered).LocalObjects.Count == 0 && store.Read(unchanged).LocalObjects.Count == 0,
                "Capacity or enumeration order produced a false Dictionary change.");
            CheckHistorical(store, schemas, first, worldId, 100, 106, 107, false);
            CheckHistorical(store, schemas, patched, worldId, 101, 106, 107, false);
            CheckHistorical(store, schemas, churned, worldId, 101, 106, 107, true);
            CheckHistorical(store, schemas, edited, worldId, 101, 606, 107, true);
        });
        File.WriteAllText(Path.Combine(directory, "historical.txt"), $"{historical.FileNumber}:{historical.FrameTicket.Packed}:{worldId.Value}");
        using (EventHistoryRepository repository = EventHistoryRepository.OpenExisting(directory, Models(), Options)) {
            using EventHistorySession<World> session = repository.Resume<World>("main");
            CheckGraph(session.State, 101, 606, 707, true, 0);
        }
        CheckSavedContentIsolation(directory + "-frozen");
    }

    private static void CheckSavedContentIsolation(string directory) {
        World world = World.Seed();
        using var repository = EventHistoryRepository.CreateNew(directory, Models(), Options);
        using var first = repository.CreateBranch("main", world, Policy);
        world.Points.Clear();
        world.Modes.Clear();
        world.IgnoreCase.Clear();
        world.Identity.Clear();
        first.Dispose();
        using EventHistorySession<World> loaded = repository.Resume<World>("main");
        CheckGraph(loaded.State, 100, 106, 107, false, 0);
        Set(loaded.State.Points, loaded.State.FirstKey, 555);
        loaded.CommitDomainEvent(loaded.State, Policy);
        GraphFrame changed = loaded.CommitDomainState(Policy);
        loaded.State.Points.Clear();
        World restored = repository.ReadState<World>(changed);
        CheckGraph(restored, 555, 106, 107, false, 0);
    }
#else
    private static void Upgrade(string directory) {
        Require(typeof(World).Assembly.GetType("DictionaryPackageConsumerProbe.LegacyMode") is null &&
            typeof(World).Assembly.GetType("DictionaryPackageConsumerProbe.LegacyPoint") is null,
            "Historical key and value CLR declarations must be absent from the current assembly.");
        string[] parts = File.ReadAllText(Path.Combine(directory, "historical.txt")).Split(':');
        FrameAddress historical = new(uint.Parse(parts[0]), SizedPtr.FromPacked(ulong.Parse(parts[1])));
        ObjectId worldId = new(uint.Parse(parts[2]));
        (ObjectId Points, ObjectId Modes) ids = default;
        Inspect(directory, (store, schemas) => ids = CheckHistorical(store, schemas, historical, worldId, 101, 606, 707, true));
        Require(Upgrades.Calls.Count == 0, "Stored-exact decoding invoked a key/value business conversion.");
        FrameAddress upgraded, unchanged, changed;
        using (EventHistoryRepository repository = EventHistoryRepository.OpenExisting(directory, Models(), Options)) {
            using EventHistorySession<World> session = repository.Resume<World>("main");
            World world = session.State;
            var points = world.Points;
            CheckGraph(world, 1101, 1606, 1707, true, 1000);
            Require(Upgrades.Calls.Count == 96 && Upgrades.Calls.All(call => call.Count == 32) &&
                Upgrades.Calls.Count(call => call.Id == ids.Points && call.Side == "value") == 32 &&
                Upgrades.Calls.Count(call => call.Id == ids.Modes && call.Side == "key") == 32 &&
                Upgrades.Calls.Count(call => call.Id == ids.Modes && call.Side == "value") == 32,
                "Separate key/value conversions must run once per entry and shared Dictionary owner.");
            session.CommitDomainEvent(session.State, Policy);
            upgraded = session.CommitDomainState(Policy).RevisionAddress;
            session.CommitDomainEvent(session.State, Policy);
            unchanged = session.CommitDomainState(Policy).RevisionAddress;
            Set(world.Points, world.FirstKey, 1102);
            session.CommitDomainEvent(session.State, Policy);
            changed = session.CommitDomainState(Policy).RevisionAddress;
            Require(ReferenceEquals(world, session.State) && ReferenceEquals(points, world.Points), "Commit replaced working instances.");
        }
        Inspect(directory, (store, schemas) => {
            StateRevision rewrite = store.Read(upgraded);
            HashSet<uint> expected = [ids.Points.Value, ids.Modes.Value];
            Require(rewrite.LocalObjects.Count == 2 && rewrite.LocalObjects.All(row =>
                row.Kind == ObjectVersionKind.Base && expected.Contains(row.ObjectId)),
                "Only the two upgraded Dictionaries must rewrite Base; nominal reference owners must remain unchanged.");
            Require(store.Read(unchanged).LocalObjects.Count == 0, "Installed upgraded baseline must compare unchanged.");
            RequireDictionaryDelta(store.Read(changed), ids.Points, onlyObject: true);
            CheckHistorical(store, schemas, historical, worldId, 101, 606, 707, true);
        });
        using EventHistoryRepository finalRepository = EventHistoryRepository.OpenExisting(directory, Models(), Options);
        using EventHistorySession<World> finalSession = finalRepository.Resume<World>("main");
        CheckGraph(finalSession.State, 1102, 1606, 1707, true, 1000);
        Require(Upgrades.Calls.Count == 96, "Current Base/Delta reopen repeated historical conversion.");
    }
#endif

    private static (ObjectId Points, ObjectId Modes) CheckHistorical(StateRevisionStore store, SchemaStore schemas,
        FrameAddress address, ObjectId worldId, int first, int sixth, int seventh, bool churned) {
        DecodedRevision decoded = RevisionDecoder.Read(store, schemas, address, Readers());
        var world = decoded.GetRequired(worldId).GetState<WorldStates.V1>();
        Require(world.Segment0Field1 == world.Segment0Field2, "Historical shared Dictionary IDs changed.");
        var pointState = decoded.GetRequired(world.Segment0Field1).GetDictionaryState<ObjectId, PointStates.V1>();
        var points = new Dictionary<string, PointStates.V1>(StringComparer.Ordinal);
        bool firstKeyShared = false;
        foreach (var entry in pointState.Entries) {
            string key = decoded.GetRequired(entry.Key).StringContent;
            points.Add(key, entry.Value);
            if (key == "key-000") firstKeyShared = entry.Key == world.Segment0Field6;
        }
        Require(points.Count == 32 && points["key-000"].Segment0Field1 == first &&
            points["key-006"].Segment0Field1 == sixth && points["key-007"].Segment0Field1 == seventh && firstKeyShared,
            "Historical key-addressed Delta or actual key reference identity failed.");
        Require(churned ? !points.ContainsKey("key-005") && points["new-key"].Segment0Field1 == 999 :
            points.ContainsKey("key-005") && !points.ContainsKey("new-key"), "Remove/Add historical state is wrong.");
        var modeState = decoded.GetRequired(world.Segment0Field3).GetDictionaryState<ModeStates.V1, PointStates.V1>();
        var modes = new Dictionary<int, int>();
        foreach (var entry in modeState.Entries) modes.Add(entry.Key.Segment0Field1, entry.Value.Segment0Field1);
        Require(modes.Count == 32 && modes[-100] == 200 && modes[-69] == 231,
            "Historical enum key / inline value pair did not retain the old exact layout.");
        var nested = decoded.GetRequired(world.Segment0Field9).GetListState<ObjectId>();
        var vector = decoded.GetRequired(world.Segment0Field10).GetArrayState<ObjectId>();
        Require(nested.Count == 2 && nested[0] == world.Segment0Field1 && nested[1] == world.Segment0Field1 &&
            vector.Elements.Length == 2 && vector.Elements[0] == world.Segment0Field3 && vector.Elements[1] == world.Segment0Field3,
            "Dictionary composition through List and array lost shared identities.");
        return (world.Segment0Field1, world.Segment0Field3);
    }

    private static void CheckGraph(World world, long first, long sixth, long seventh, bool churned, long offset) {
        Require(world.Points.Count == 32 && world.Points["key-000"].Value == first &&
            world.Points["key-006"].Value == sixth && world.Points["key-007"].Value == seventh &&
            ReferenceEquals(world.Points, world.Alias) && ReferenceEquals(world.Points, world.Box.Value) &&
            world.Nested.Count == 2 && world.Nested.All(dictionary => ReferenceEquals(dictionary, world.Points)) &&
            world.Vector.Length == 2 && world.Vector.All(dictionary => ReferenceEquals(dictionary, world.Modes)),
            "Dictionary contents or array/List/generic sharing failed restoration.");
        Require(churned ? !world.Points.ContainsKey("key-005") && world.Points["new-key"].Value == 999 + offset :
            world.Points.ContainsKey("key-005") && !world.Points.ContainsKey("new-key"), "Domain Remove/Add contents failed restoration.");
        Require(ReferenceEquals(world.Points.Keys.Single(key => key == "key-000"), world.FirstKey),
            "Ordinal string Dictionary must preserve the actual key object reference.");
        Require(world.Modes.Count == 32 && world.Modes[(Mode)(-100 + offset)].Value == 200 + offset &&
            world.Modes[(Mode)(-69 + offset)].Value == 231 + offset, "Enum key/value conversion failed.");
        Require(world.IgnoreCase["mixed"] == 41 && world.IgnoreCase["second"] == 51 &&
            !world.IgnoreCase.TryAdd("mIxEd", 100), "OrdinalIgnoreCase semantics were not restored.");
        Require(world.Identity.Count == 2 && !ReferenceEquals(world.IdentityFirst, world.IdentitySecond) &&
            world.IdentityFirst == world.IdentitySecond && world.Identity.ContainsKey(world.IdentityFirst) &&
            world.Identity.ContainsKey(world.IdentitySecond) && !world.Identity.ContainsKey(new("same content".ToCharArray())),
            "ReferenceIdentity comparer merged distinct equal string keys or lost key references.");
        Node node = world.Points[world.FirstKey].Link!;
        Require(node.Score == 73 && ReferenceEquals(node.Owner, world) && ReferenceEquals(node.Self, node) &&
            world.Points.Values.All(value => ReferenceEquals(value.Link, node)) &&
            world.Modes.Values.All(value => ReferenceEquals(value.Link, node)) &&
            world.Identity.Values.All(value => ReferenceEquals(value, node)), "Shared values and reference cycles failed restoration.");
    }

    private static void Set(Dictionary<string, Point> dictionary, string key, long value) {
        Point point = dictionary[key];
#if HISTORY_V1
        point.Value = checked((int)value);
#else
        point.Value = value;
#endif
        dictionary[key] = point;
    }

    private static void RequireDictionaryDelta(StateRevision revision, ObjectId id, bool onlyObject) {
        Require((!onlyObject || revision.LocalObjects.Count == 1) && revision.LocalObjects.Count(row =>
            row.ObjectId == id.Value && row.Kind == ObjectVersionKind.Delta) == 1,
            "A bounded Dictionary edit must preserve its ObjectId and write an actual Delta.");
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
