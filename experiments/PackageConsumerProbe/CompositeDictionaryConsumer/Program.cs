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
using CollisionKeyStates = Atelia.DurableGraph.Generated.Family_436F6C6C6973696F6E4B6579;
using CollisionWorldStates = Atelia.DurableGraph.Generated.Family_436F6C6C6973696F6E576F726C64;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;
#if HISTORY_V1
using Part = CompositeDictionaryPackageConsumerProbe.LegacyPart;
using Key = CompositeDictionaryPackageConsumerProbe.LegacyKey<CompositeDictionaryPackageConsumerProbe.LegacyPart>;
using Value = CompositeDictionaryPackageConsumerProbe.LegacyValue;
#else
using Part = CompositeDictionaryPackageConsumerProbe.CurrentPart;
using Key = CompositeDictionaryPackageConsumerProbe.CurrentKey<CompositeDictionaryPackageConsumerProbe.CurrentPart>;
using Value = CompositeDictionaryPackageConsumerProbe.CurrentValue;
#endif

namespace CompositeDictionaryPackageConsumerProbe;

internal static class Program {
    private static readonly RbfSegmentStoreOptions Options = new() { NewStoreLayout = RbfSegmentStoreLayout.Flat };
    private static readonly ReadAmplificationBaseBudgetParameters Policy = new(int.MaxValue, 1);
    private static readonly List<Type> ResolverCalls = [];

    private static void Main(string[] args) {
        string directory = Path.GetFullPath(args.Single());
#if HISTORY_V1
        Seed(directory);
        SeedCollision(directory + "-collision");
        Console.WriteLine("CompositeDictionarySeed:True:DefaultIgnoresTimestamp:True:TypedAndGenericApplication:True:PersistentKeyReplacement:True:StableModes:True");
#else
        Upgrade(directory);
        CheckCollision(directory + "-collision");
        Console.WriteLine("CompositeDictionaryUpgrade:True:DeletedGenericAndNestedTypes:True:ExplicitDoubleSlotUpgrade:True:ForcedBaseThenDelta:True:ColdReopen:True:SameSchemaComparerCollision:True");
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
        using (Repository repository = Repository.CreateNew(directory, Models(), Options)) {
            using BranchCheckout session = repository.CreateBranch("main", world, Policy);
            first = session.StateRevisionAddress!.Value;
            worldId = session.StateId!.Value;
            var lookup = MakeKey(100, 0, -1);
            Require(world.Rows.ContainsKey(lookup), "Default IEquatable must ignore the persistent Timestamp field.");
            Set(world.Rows, lookup, 201);
            session.CommitEvent(((World)session.State!), Policy);
            patch = session.CommitState(Policy).RevisionAddress;
            Key actual = world.Rows.Keys.Single(key => key.Part.Number == 100);
            Value value = world.Rows[actual];
            Require(world.Rows.Remove(actual), "Missing key before replacing its stored Timestamp.");
            actual.Timestamp = 90000;
            world.Rows.Add(actual, value);
            session.CommitEvent(((World)session.State!), Policy);
            historical = session.CommitState(Policy).RevisionAddress;
            Require(world.Rows.Remove(actual), "Missing key before changing only Transient state.");
            actual.Scratch = 999;
            world.Rows.Add(actual, value);
            session.CommitEvent(((World)session.State!), Policy);
            transient = session.CommitState(Policy).RevisionAddress;
            Require(ResolverCalls.Count == 1 && ResolverCalls[0] == typeof(Dictionary<Key, Value>),
                "Application resolver must be cached by closed Dictionary; typed registration and Default must not invoke it.");
            Require(ReferenceEquals(world, ((World)session.State!)) && ReferenceEquals(world.Rows, world.Alias), "Commit replaced domain instances.");
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
        using (Repository repository = Repository.OpenExisting(directory, Models(), Options)) {
            using BranchCheckout session = repository.Checkout("main");
            CheckGraph(((World)session.State!), 201, 400, 0);
            session.CommitEvent(((World)session.State!), Policy);
            restoredUnchanged = session.CommitState(Policy).RevisionAddress;
        }
        Inspect(directory, (store, _) => Require(store.Read(restoredUnchanged).LocalObjects.Count == 0,
            "Load and recapture changed CurrentDefault/Application modes or frozen key content."));
    }

    private static void SeedCollision(string directory) {
        var world = new CollisionWorld();
        world.Rows.Add(new() { Tenant = 7, Number = 1, Timestamp = 100 }, 11);
        world.Rows.Add(new() { Tenant = 7, Number = 2, Timestamp = 200 }, 22);
        using Repository repository = Repository.CreateNew(directory, Models(), Options);
        using BranchCheckout session = repository.CreateBranch("main", world, Policy);
        FrameAddress address = session.StateRevisionAddress!.Value;
        WriteAddress(directory, address, session.StateId!.Value);
    }
#else
    private static void Upgrade(string directory) {
        Require(typeof(World).Assembly.GetType("CompositeDictionaryPackageConsumerProbe.LegacyKey`1") is null &&
            typeof(World).Assembly.GetType("CompositeDictionaryPackageConsumerProbe.LegacyPart") is null &&
            typeof(World).Assembly.GetType("CompositeDictionaryPackageConsumerProbe.LegacyValue") is null,
            "Old generic key, nested struct and value CLR declarations must all be absent.");
        var (historical, worldId) = ReadAddress(directory);
        (ObjectId Rows, ObjectId Application) ids = default;
        ResolverCalls.Clear();
        Inspect(directory, (store, schemas) => ids = CheckHistorical(store, schemas, historical, worldId, 201, 90000));
        Require(Upgrades.Calls.Count == 0 && ResolverCalls.Count == 0, "Stored-exact reading invoked current behavior.");
        FrameAddress rewritten, unchanged, changed;
        using (Repository repository = Repository.OpenExisting(directory, Models(), Options)) {
            using BranchCheckout session = repository.Checkout("main");
            World world = ((World)session.State!);
            CheckGraph(world, 1201, 1400, 1000);
            Require(Upgrades.Calls.Count == 192 && Upgrades.Calls.All(call => call.Count == 32) &&
                new[] { ids.Rows, ids.Application }.All(id => new[] { "key", "part", "value" }.All(side =>
                    Upgrades.Calls.Count(call => call.Id == id && call.Side == side) == 32)),
                "Generic nested key/value upgrades must run once per entry and shared Dictionary owner.");
            Require(ResolverCalls.Count == 1, "CurrentDefault, typed Application or shared incoming edges caused extra resolver calls.");
            session.CommitEvent(((World)session.State!), Policy);
            rewritten = session.CommitState(Policy).RevisionAddress;
            session.CommitEvent(((World)session.State!), Policy);
            unchanged = session.CommitState(Policy).RevisionAddress;
            Set(world.Rows, MakeKey(1100, 0, -123), 1202);
            Set(world.Application, MakeKey(1100, 0, -123), 1401);
            session.CommitEvent(((World)session.State!), Policy);
            changed = session.CommitState(Policy).RevisionAddress;
            Require(ReferenceEquals(world, ((World)session.State!)), "Commit replaced the working World.");
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
        using Repository finalRepository = Repository.OpenExisting(directory, Models(), Options);
        using BranchCheckout finalSession = finalRepository.Checkout("main");
        CheckGraph(((World)finalSession.State!), 1202, 1401, 1000);
        Require(Upgrades.Calls.Count == 192 && ResolverCalls.Count == 2, "Cold reopen repeated Upgrade or resolved beyond one closed Application type.");
    }

    private static void CheckCollision(string directory) {
        var (address, worldId) = ReadAddress(directory);
        CollisionKey.Comparisons = 0;
        int upgrades = Upgrades.Calls.Count;
        int resolutions = ResolverCalls.Count;
        Inspect(directory, (store, schemas) => {
            DecodedRevision decoded = RevisionDecoder.Read(store, schemas, address, Readers());
            var world = decoded.GetRequired(worldId).GetState<CollisionWorldStates.V1>();
            var state = decoded.GetRequired(world.Segment0Field1).GetDictionaryState<CollisionKeyStates.V1, int>();
            Require(state.Count == 2 && state.ComparerKind == DictionaryComparerKind.CurrentDefault &&
                state.Entries[0].Key.Segment0Field1 == 7 && state.Entries[1].Key.Segment0Field1 == 7 &&
                state.Entries[0].Key.Segment0Field2 != state.Entries[1].Key.Segment0Field2,
                "Same-Schema historical keys must remain independently readable.");
        });
        Require(CollisionKey.Comparisons == 0 && Upgrades.Calls.Count == upgrades && ResolverCalls.Count == resolutions,
            "Exact reading interpreted current comparison behavior.");
        bool rejected = false;
        using (Repository repository = Repository.OpenExisting(directory, Models(), Options)) {
            try {
                using BranchCheckout session = repository.Checkout("main");
            }
            catch (InvalidDataException error) when (error.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
                error.Message.Contains("collision", StringComparison.OrdinalIgnoreCase)) {
                rejected = true;
            }
        }
        Require(rejected && CollisionKey.Comparisons > 0 && Upgrades.Calls.Count == upgrades && ResolverCalls.Count == resolutions,
            "Changing only Equals must fail current TryAdd without Schema Upgrade, resolver or partial World delivery.");
        // The failed current load did not destroy the exact historical access path.
        CollisionKey.Comparisons = 0;
        Inspect(directory, (store, schemas) => { _ = RevisionDecoder.Read(store, schemas, address, Readers()); });
        Require(CollisionKey.Comparisons == 0, "Exact history after failed Load executed current key code.");
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
        value.Number = checked((int)number);
#else
        value.Number = number;
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
