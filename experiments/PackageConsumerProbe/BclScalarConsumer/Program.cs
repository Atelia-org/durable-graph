using Atelia.Data;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using PointStates = Atelia.DurableGraph.Generated.Family_506F696E74;
using WorldStates = Atelia.DurableGraph.Generated.Family_576F726C64;
using BoxStates = Atelia.DurableGraph.Generated.Family_426F78;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace BclScalarPackageConsumerProbe;

internal static class Program {
    private static readonly RbfSegmentStoreOptions Options = new() { NewStoreLayout = RbfSegmentStoreLayout.Flat };
    private static readonly ReadAmplificationBaseBudgetParameters Policy = new(int.MaxValue, 1);

    private static void Main(string[] args) {
        string directory = Path.GetFullPath(args.Single());
#if HISTORY_V1
        Seed(directory);
        Console.WriteLine("BclScalarSeed:True:ThreeScalarCompositions:True:ExactDecimalScale:True:DecimalKeyRemoveAdd:True:ColdReopen:True");
#else
        Upgrade(directory);
        Console.WriteLine("BclScalarUpgrade:True:DeletedInlineClr:True:ExplicitOwnerAndValueUpgrade:True:HistoricalExact:True:ForcedBaseThenDelta:True:ColdReopen:True");
#endif
    }

    private static StateModelRegistry Models() {
        var models = new StateModelRegistry();
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
#if HISTORY_V2
        models.UseListElementUpgrades(typeof(PointRules));
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
        FrameAddress first, scale, historical, unchanged;
        ObjectId worldId;
        using (Repository repository = Repository.CreateNew(directory, Models(), Options)) {
            using BranchCheckout session = repository.CreateBranch("main", world, Policy);
            first = session.StateRevisionAddress!.Value;
            worldId = session.StateId!.Value;
            world.Amount = 1.00m;
            world.Point = world.Point with { Amount = 1.00m };
            world.Points[0] = world.Points[0] with { Amount = 1.00m };
            world.Amounts[0] = 1.00m;
            world.OptionalAmounts[1] = 1.00m;
            session.CommitEvent(((World)session.State!), Policy);
            scale = session.CommitState(Policy).RevisionAddress;
            Require(world.Keys.Remove(1.0m), "Missing decimal key before actual stored-key replacement.");
            world.Keys.Add(1.00m, World.SampleId);
            session.CommitEvent(((World)session.State!), Policy);
            historical = session.CommitState(Policy).RevisionAddress;
            session.CommitEvent(((World)session.State!), Policy);
            unchanged = session.CommitState(Policy).RevisionAddress;
            Require(ReferenceEquals(((World)session.State!), world), "Commit replaced the working graph.");
        }
        Inspect(directory, (store, schemas) => {
            var ids = CheckHistorical(store, schemas, historical, worldId, 2, 2);
            CheckHistorical(store, schemas, first, worldId, 1, 1);
            Require(store.Read(scale).LocalObjects.Count == 4 &&
                store.Read(scale).LocalObjects.All(row => row.Kind == ObjectVersionKind.Delta),
                "Scale-only changes must survive in owner, inline, List and Nullable List bodies.");
            var row = store.Read(historical).LocalObjects.Single();
            Require(row.ObjectId == ids.Keys.Value && row.Kind == ObjectVersionKind.Delta,
                "Real decimal key replacement must write one Dictionary Delta.");
            CheckKeyReplacement(row.Body);
            Require(store.Read(unchanged).LocalObjects.Count == 0, "Unchanged exact scalar representation wrote objects.");
        });
        File.WriteAllText(Path.Combine(directory, "historical.txt"), $"{historical.FileNumber}:{historical.FrameTicket.Packed}:{worldId.Value}");
        FrameAddress recaptured;
        using (Repository reopened = Repository.OpenExisting(directory, Models(), Options)) {
            using BranchCheckout restored = reopened.Checkout("main");
            CheckGraph(((World)restored.State!), 0, 2);
            restored.CommitEvent(((World)restored.State!), Policy);
            recaptured = restored.CommitState(Policy).RevisionAddress;
        }
        Inspect(directory, (store, _) => Require(store.Read(recaptured).LocalObjects.Count == 0,
            "ScalarDefault Guid/decimal/TimeSpan modes changed after Load and recapture."));
    }
#else
    private static void Upgrade(string directory) {
        Require(typeof(World).Assembly.GetType("BclScalarPackageConsumerProbe.LegacyPoint") is null,
            "The historical inline CLR declaration must be absent.");
        string[] parts = File.ReadAllText(Path.Combine(directory, "historical.txt")).Split(':');
        FrameAddress historical = new(uint.Parse(parts[0]), SizedPtr.FromPacked(ulong.Parse(parts[1])));
        ObjectId worldId = new(uint.Parse(parts[2]));
        (ObjectId Points, ObjectId Keys) ids = default;
        Inspect(directory, (store, schemas) => ids = CheckHistorical(store, schemas, historical, worldId, 2, 2));
        Require(Upgrades.OwnerCalls == 0 && Upgrades.ValueCalls.Count == 0, "Exact reading ran business upgrades.");
        FrameAddress rewritten, unchanged, changed;
        using (Repository repository = Repository.OpenExisting(directory, Models(), Options)) {
            using BranchCheckout session = repository.Checkout("main");
            World world = ((World)session.State!);
            CheckGraph(world, 1000, 2);
            Require(Upgrades.OwnerCalls == 1 && Upgrades.ValueCalls.Count == 33 &&
                Upgrades.ValueCalls.Count(call => call.Id == worldId) == 1 &&
                Upgrades.ValueCalls.Count(call => call.Id == ids.Points) == 32,
                "Explicit owner dependency and List element conversion must each run exactly once per value.");
            session.CommitEvent(((World)session.State!), Policy);
            rewritten = session.CommitState(Policy).RevisionAddress;
            session.CommitEvent(((World)session.State!), Policy);
            unchanged = session.CommitState(Policy).RevisionAddress;
            world.Amount = 1.000m;
            world.Points[0] = world.Points[0] with { Amount = 1.000m };
            session.CommitEvent(((World)session.State!), Policy);
            changed = session.CommitState(Policy).RevisionAddress;
            Require(ReferenceEquals(((World)session.State!), world), "Upgrade resave replaced the working graph.");
        }
        Inspect(directory, (store, schemas) => {
            var rows = store.Read(rewritten).LocalObjects;
            Require(rows.Count == 2 && rows.All(row => row.Kind == ObjectVersionKind.Base &&
                (row.ObjectId == worldId.Value || row.ObjectId == ids.Points.Value)),
                "Only the explicitly upgraded owner and List must rewrite Base.");
            Require(store.Read(unchanged).LocalObjects.Count == 0, "Upgraded state failed stable NoChange.");
            var deltas = store.Read(changed).LocalObjects;
            Require(deltas.Count == 2 && deltas.All(row => row.Kind == ObjectVersionKind.Delta),
                "Scalar edits must resume ordinary owner and List Delta after upgraded Base.");
            CheckHistorical(store, schemas, historical, worldId, 2, 2);
        });
        using Repository reopened = Repository.OpenExisting(directory, Models(), Options);
        using BranchCheckout restored = reopened.Checkout("main");
        CheckGraph(((World)restored.State!), 1000, 3);
        Require(Upgrades.OwnerCalls == 1 && Upgrades.ValueCalls.Count == 33, "Cold reopen repeated business upgrade.");
    }
#endif

    private static (ObjectId Points, ObjectId Keys) CheckHistorical(StateRevisionStore store, SchemaStore schemas,
        FrameAddress address, ObjectId worldId, int scale, int keyScale) {
        DecodedRevision decoded = RevisionDecoder.Read(store, schemas, address, Readers());
        var world = decoded.GetRequired(worldId).GetState<WorldStates.V1>();
        Require(world.Segment0Field1 == World.SampleId && world.Segment0Field3 == TimeSpan.MinValue &&
            SameBits(world.Segment0Field2, One(scale)), "Stored-exact direct scalar values changed.");
        var point = world.Segment0Field4;
        Require(point.Segment0Field1 == World.SampleId && SameBits(point.Segment0Field2, One(scale)) &&
            point.Segment0Field3 == TimeSpan.MinValue && point.Segment0Field4 == 10,
            "Deleted inline historical DTO lost scalar content.");
        var points = decoded.GetRequired(world.Segment0Field5).GetListState<PointStates.V1>();
        Require(points.Count == 32 && points[0].Segment0Field4 == 100 && SameBits(points[0].Segment0Field2, One(scale)) &&
            world.Segment0Field5 == world.Segment0Field6, "Historical List values or shared identity changed.");
        var keys = decoded.GetRequired(world.Segment0Field7).GetDictionaryState<decimal, Guid>();
        bool foundKey = false;
        foreach (var entry in keys.Entries) {
            if (entry.Key == 1m) foundKey = SameBits(entry.Key, One(keyScale)) && entry.Value == World.SampleId;
        }
        Require(keys.Count == 32 && keys.ComparerKind == DictionaryComparerKind.ScalarDefault &&
            foundKey,
            "Stored-exact decimal key retained the wrong scale or comparer.");
        Require(decoded.GetRequired(world.Segment0Field12).GetDictionaryState<Guid, TimeSpan>().ComparerKind == DictionaryComparerKind.ScalarDefault &&
            decoded.GetRequired(world.Segment0Field13).GetDictionaryState<TimeSpan, decimal>().ComparerKind == DictionaryComparerKind.ScalarDefault,
            "New builtin Dictionary keys require ScalarDefault.");
        Require(decoded.GetRequired(world.Segment0Field14).GetState<BoxStates.V1<Guid>>().Segment0Field1 == World.SampleId &&
            SameBits(decoded.GetRequired(world.Segment0Field15).GetState<BoxStates.V1<decimal>>().Segment0Field1, decimal.MaxValue) &&
            decoded.GetRequired(world.Segment0Field16).GetState<BoxStates.V1<TimeSpan>>().Segment0Field1.Ticks == -1,
            "Historical generic scalar closures failed.");
        return (world.Segment0Field5, world.Segment0Field7);
    }

    private static void CheckGraph(World world, long offset, int scale) {
        Require(world.Id == World.SampleId && SameBits(world.Amount, One(scale)) && world.Duration == TimeSpan.MinValue &&
            world.Point.Id == World.SampleId && SameBits(world.Point.Amount, 1.00m) &&
            world.Point.Duration == TimeSpan.MinValue && world.Point.Count == 10 + offset,
            "Direct or nested scalar representation failed restoration.");
        Require(ReferenceEquals(world.Points, world.Alias) && world.Points.Count == 32 &&
            SameBits(world.Points[0].Amount, One(scale)) && world.Points[0].Count == 100 + offset,
            "Shared upgraded List state failed restoration.");
        Require(world.Keys[1m] == World.SampleId && SameBits(world.Keys.Keys.Single(key => key == 1m), 1.00m) &&
            SameBits(world.Amounts[0], 1.00m) && SameBits(world.OptionalAmounts[1]!.Value, 1.00m) &&
            SameBits(world.OptionalAmounts[2]!.Value, new decimal(0, 0, 0, true, 28)) && world.OptionalAmounts[0] is null,
            "Decimal scale, signed zero or nullable containers were normalized accidentally.");
        Require(world.Ids[0] is null && world.Ids[1] == Guid.Empty && world.Ids[2] == World.SampleId &&
            world.Durations[0, 0] is null && world.Durations[0, 1] == TimeSpan.MaxValue && world.Durations[0, 2]!.Value.Ticks == -1 &&
            world.ById[World.SampleId] == TimeSpan.MaxValue && SameBits(world.ByDuration[TimeSpan.MinValue], decimal.MinValue) &&
            world.GuidBox.Value == World.SampleId && SameBits(world.DecimalBox.Value, decimal.MaxValue) && world.DurationBox.Value.Ticks == -1,
            "Nullable arrays, builtin key/value or generic compositions failed restoration.");
    }

    private static void CheckKeyReplacement(ReadOnlySpan<byte> body) {
        BinaryPayloadReader reader = new(body);
        Require(reader.ReadUInt32() == 1 && SameBits(reader.ReadDecimal(), 1.0m), "Expected one old-scale key Remove.");
        Require(reader.ReadUInt32() == 0, "Key representation replacement must not use PatchValue.");
        Require(reader.ReadUInt32() == 1 && SameBits(reader.ReadDecimal(), 1.00m) && reader.ReadGuid() == World.SampleId,
            "Expected one new-scale key Add with its unchanged value.");
        reader.EnsureFullyConsumed();
    }

    private static decimal One(int scale) => scale switch { 1 => 1.0m, 2 => 1.00m, 3 => 1.000m, _ => throw new ArgumentOutOfRangeException(nameof(scale)) };
    private static bool SameBits(decimal left, decimal right) => decimal.GetBits(left).AsSpan().SequenceEqual(decimal.GetBits(right));
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
