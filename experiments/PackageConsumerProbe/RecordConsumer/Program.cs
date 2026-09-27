using Atelia.Data;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using PartStates = Atelia.DurableGraph.Generated.Family_50617274;
using KeyStates = Atelia.DurableGraph.Generated.Family_4B6579;
using ValueStates = Atelia.DurableGraph.Generated.Family_56616C7565;
using WorldStates = Atelia.DurableGraph.Generated.Family_576F726C64;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;
#if HISTORY_V1
using Part = RecordPackageConsumerProbe.LegacyPart;
using Key = RecordPackageConsumerProbe.LegacyKey<RecordPackageConsumerProbe.LegacyPart>;
using Value = RecordPackageConsumerProbe.LegacyValue;
#else
using Part = RecordPackageConsumerProbe.CurrentPart;
using Key = RecordPackageConsumerProbe.CurrentKey<RecordPackageConsumerProbe.CurrentPart>;
using Value = RecordPackageConsumerProbe.CurrentValue;
#endif

namespace RecordPackageConsumerProbe;

internal static class Program {
    private static readonly RbfSegmentStoreOptions Options = new() { NewStoreLayout = RbfSegmentStoreLayout.Flat };
    private static readonly ReadAmplificationBaseBudgetParameters Policy = new(int.MaxValue, 1);
    private static readonly List<Type> ResolverCalls = [];

    private static void Main(string[] args) {
        string directory = Path.GetFullPath(args.Single());
#if HISTORY_V1
        Seed(directory);
        Console.WriteLine("RecordSeed:True:ReadonlyPositionalGeneric:True:PersistentKeyReplacement:True:StableModes:True");
#else
        Upgrade(directory);
        Console.WriteLine("RecordUpgrade:True:DeletedGenericAndNestedTypes:True:ExplicitDoubleSlotUpgrade:True:ForcedBaseThenDelta:True:ColdReopen:True");
#endif
    }

    private static StateModelRegistry Models() {
        var models = new StateModelRegistry();
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
        // A typed choice can be a standard comparer even though the source uses a custom instance.
        models.UseDictionaryComparer<string, int>(StringComparer.OrdinalIgnoreCase);
        models.UseDictionaryComparerResolver(dictionaryType => {
            ResolverCalls.Add(dictionaryType);
            Type key = dictionaryType.GetGenericArguments()[0];
#if HISTORY_V1
            Type definition = typeof(LegacyKey<>);
#else
            Type definition = typeof(CurrentKey<>);
#endif
            return key.IsGenericType && key.GetGenericTypeDefinition() == definition
                ? Activator.CreateInstance(typeof(ForwardComparer<>).MakeGenericType(key)) : null;
        });
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
        FrameAddress first, patch, historical, transient;
        ObjectId worldId;
        ResolverCalls.Clear();
        using (EventHistoryRepository repository = EventHistoryRepository.CreateNew(directory, Models(), Options)) {
            using EventHistorySession<World> session = repository.CreateBranch("main", world, Policy);
            first = session.StateRevisionAddress;
            worldId = session.StateId;
            var lookup = MakeKey(100, 0, -1);
            Require(world.Rows.ContainsKey(lookup), "Default IEquatable must ignore the persistent Timestamp field.");
            Set(world.Rows, lookup, 201);
            session.CommitDomainEvent(session.State, Policy);
            patch = session.CommitDomainState(Policy).RevisionAddress;
            Key actual = world.Rows.Keys.Single(key => key.Part.Number == 100);
            Value value = world.Rows[actual];
            Require(world.Rows.Remove(actual), "Missing key before replacing its stored Timestamp.");
            actual = actual with { Timestamp = 90000 };
            world.Rows.Add(actual, value);
            session.CommitDomainEvent(session.State, Policy);
            historical = session.CommitDomainState(Policy).RevisionAddress;
            Require(world.Rows.Remove(actual), "Missing key before changing only Transient state.");
            actual = actual with { Scratch = 999 };
            world.Rows.Add(actual, value);
            session.CommitDomainEvent(session.State, Policy);
            transient = session.CommitDomainState(Policy).RevisionAddress;
            Require(ResolverCalls.Count == 1 && ResolverCalls[0] == typeof(Dictionary<Key, Value>),
                "Application resolver must be cached by closed Dictionary; typed registration and Default must not invoke it.");
            Require(ReferenceEquals(world, session.State) && ReferenceEquals(world.Rows, world.Alias), "Commit replaced domain instances.");
        }
        Inspect(directory, (store, schemas) => {
            var ids = CheckHistorical(store, schemas, historical, worldId, 201, 90000);
            RequireDelta(store.Read(patch), ids.Rows);
            RequireDelta(store.Read(historical), ids.Rows);
            Require(store.Read(transient).LocalObjects.Count == 0, "Transient-only key changes produced persistent Delta.");
            CheckHistorical(store, schemas, first, worldId, 200, 10000);
        });
        WriteAddress(directory, historical, worldId);
        FrameAddress restoredUnchanged;
        using (EventHistoryRepository repository = EventHistoryRepository.OpenExisting(directory, Models(), Options)) {
            using EventHistorySession<World> session = repository.Resume<World>("main");
            CheckGraph(session.State, 201, 400, 0);
            session.CommitDomainEvent(session.State, Policy);
            restoredUnchanged = session.CommitDomainState(Policy).RevisionAddress;
        }
        Inspect(directory, (store, _) => Require(store.Read(restoredUnchanged).LocalObjects.Count == 0,
            "Load and recapture changed CurrentDefault/Application modes or frozen key content."));
    }

#else
    private static void Upgrade(string directory) {
        Require(typeof(World).Assembly.GetType("RecordPackageConsumerProbe.LegacyKey`1") is null &&
            typeof(World).Assembly.GetType("RecordPackageConsumerProbe.LegacyPart") is null &&
            typeof(World).Assembly.GetType("RecordPackageConsumerProbe.LegacyValue") is null,
            "Old generic key, nested struct and value CLR declarations must all be absent.");
        var (historical, worldId) = ReadAddress(directory);
        (ObjectId Rows, ObjectId Application) ids = default;
        ResolverCalls.Clear();
        Inspect(directory, (store, schemas) => ids = CheckHistorical(store, schemas, historical, worldId, 201, 90000));
        Require(Upgrades.Calls.Count == 0 && ResolverCalls.Count == 0, "Stored-exact reading invoked current behavior.");
        FrameAddress rewritten, unchanged, changed;
        using (EventHistoryRepository repository = EventHistoryRepository.OpenExisting(directory, Models(), Options)) {
            using EventHistorySession<World> session = repository.Resume<World>("main");
            World world = session.State;
            CheckGraph(world, 1201, 1400, 1000);
            Require(Upgrades.Calls.Count == 192 && Upgrades.Calls.All(call => call.Count == 32) &&
                new[] { ids.Rows, ids.Application }.All(id => new[] { "key", "part", "value" }.All(side =>
                    Upgrades.Calls.Count(call => call.Id == id && call.Side == side) == 32)),
                "Generic nested key/value upgrades must run once per entry and shared Dictionary owner.");
            Require(ResolverCalls.Count == 1, "CurrentDefault, typed Application or shared incoming edges caused extra resolver calls.");
            session.CommitDomainEvent(session.State, Policy);
            rewritten = session.CommitDomainState(Policy).RevisionAddress;
            session.CommitDomainEvent(session.State, Policy);
            unchanged = session.CommitDomainState(Policy).RevisionAddress;
            Set(world.Rows, MakeKey(1100, 0, -123), 1202);
            Set(world.Application, MakeKey(1100, 0, -123), 1401);
            session.CommitDomainEvent(session.State, Policy);
            changed = session.CommitDomainState(Policy).RevisionAddress;
            Require(ReferenceEquals(world, session.State), "Commit replaced the working World.");
        }
        Inspect(directory, (store, schemas) => {
            var rows = store.Read(rewritten).LocalObjects;
            Require(rows.Count == 2 && rows.All(row => row.Kind == ObjectVersionKind.Base &&
                (row.ObjectId == ids.Rows.Value || row.ObjectId == ids.Application.Value)),
                "Only the two upgraded Dictionaries must rewrite Base.");
            Require(store.Read(unchanged).LocalObjects.Count == 0, "Upgraded baseline or restored mode failed NoChange.");
            var deltas = store.Read(changed).LocalObjects;
            Require(deltas.Count == 2 && deltas.All(row => row.Kind == ObjectVersionKind.Delta &&
                (row.ObjectId == ids.Rows.Value || row.ObjectId == ids.Application.Value)),
                "CurrentDefault and Application must both resume ordinary value Delta.");
            CheckHistorical(store, schemas, historical, worldId, 201, 90000);
        });
        using EventHistoryRepository finalRepository = EventHistoryRepository.OpenExisting(directory, Models(), Options);
        using EventHistorySession<World> finalSession = finalRepository.Resume<World>("main");
        CheckGraph(finalSession.State, 1202, 1401, 1000);
        Require(Upgrades.Calls.Count == 192 && ResolverCalls.Count == 2, "Cold reopen repeated Upgrade or resolved beyond one closed Application type.");
    }

#endif

    private static (ObjectId Rows, ObjectId Application) CheckHistorical(StateRevisionStore store, SchemaStore schemas,
        FrameAddress address, ObjectId worldId, int first, long timestamp) {
        DecodedRevision decoded = RevisionDecoder.Read(store, schemas, address, Readers());
        var world = decoded.GetRequired(worldId).GetState<WorldStates.V1>();
        var rows = decoded.GetRequired(world.Segment0Field1).GetDictionaryState<KeyStates.V1<PartStates.V1>, ValueStates.V1>();
        var application = decoded.GetRequired(world.Segment0Field3).GetDictionaryState<KeyStates.V1<PartStates.V1>, ValueStates.V1>();
        Require(rows.Count == 32 && application.Count == 32 && rows.ComparerKind == DictionaryComparerKind.CurrentDefault &&
            application.ComparerKind == DictionaryComparerKind.Application && world.Segment0Field1 == world.Segment0Field2,
            "Historical composite key counts, modes or shared identity changed.");
        bool found = false;
        foreach (var entry in rows.Entries) {
            if (entry.Key.Segment0Field1.Segment0Field1 != 100) continue;
            found = entry.Value.Segment0Field1 == first && entry.Key.Segment0Field3 == timestamp;
        }
        Require(found, "Full persistent Timestamp or nested historical key/value Delta was lost.");
        var typed = decoded.GetRequired(world.Segment0Field5).GetDictionaryState<ObjectId, int>();
        Require(typed.ComparerKind == DictionaryComparerKind.Application && typed.Count == 1,
            "Custom source comparer must remain Application even when typed restoration chooses a built-in comparer.");
        return (world.Segment0Field1, world.Segment0Field3);
    }

    private static void CheckGraph(World world, long first, long applicationFirst, long offset) {
        Key lookup = MakeKey(100 + offset, 0, -999);
        Require(world.Rows.Count == 32 && world.Application.Count == 32 && world.Rows[lookup].Number == first &&
            world.Application[lookup].Number == applicationFirst && ReferenceEquals(world.Rows, world.Alias) &&
            world.Nested.Count == 3 && ReferenceEquals(world.Nested[0], world.Application) &&
            ReferenceEquals(world.Nested[1], world.Rows) && ReferenceEquals(world.Nested[2], world.Application),
            "Default/Application behavior or shared nested Dictionary restoration failed.");
        Key actual = world.Rows.Keys.Single(key => key.Part.Number == 100 + offset);
        Require(actual.Timestamp == 90000 && actual.Scratch == 0, "Persistent Timestamp or Transient restoration was incorrect.");
        Require(world.Typed["mixed"] == 41 && !world.Typed.TryAdd("mIXed", 42),
            "Typed current comparer configuration did not restore its query behavior.");
    }

    private static Key MakeKey(long number, int scope, long timestamp) => new() {
        Part = new Part {
#if HISTORY_V1
            Number = checked((int)number)
#else
            Number = number
#endif
        },
        Scope = scope, Timestamp = timestamp
    };

    private static void Set(Dictionary<Key, Value> dictionary, Key key, long number) {
        Value value = dictionary[key];
#if HISTORY_V1
        value = value with { Number = checked((int)number) };
#else
        value = value with { Number = number };
#endif
        dictionary[key] = value;
    }

    private static void RequireDelta(StateRevision revision, ObjectId id) => Require(revision.LocalObjects.Count == 1 &&
        revision.LocalObjects[0].ObjectId == id.Value && revision.LocalObjects[0].Kind == ObjectVersionKind.Delta,
        "A bounded key/value edit must write one actual Dictionary Delta.");

    private static void WriteAddress(string directory, FrameAddress address, ObjectId id) =>
        File.WriteAllText(Path.Combine(directory, "historical.txt"), $"{address.FileNumber}:{address.FrameTicket.Packed}:{id.Value}");

    private static (FrameAddress Address, ObjectId Id) ReadAddress(string directory) {
        string[] parts = File.ReadAllText(Path.Combine(directory, "historical.txt")).Split(':');
        return (new(uint.Parse(parts[0]), SizedPtr.FromPacked(ulong.Parse(parts[1]))), new(uint.Parse(parts[2])));
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
