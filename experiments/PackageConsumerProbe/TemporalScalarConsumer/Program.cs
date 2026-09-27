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

namespace TemporalScalarPackageConsumerProbe;

internal static class Program {
    private static readonly RbfSegmentStoreOptions Options = new() { NewStoreLayout = RbfSegmentStoreLayout.Flat };
    private static readonly ReadAmplificationBaseBudgetParameters Policy = new(int.MaxValue, 1);

    private static void Main(string[] args) {
        string directory = Path.GetFullPath(args.Single());
#if HISTORY_V1
        Seed(directory);
        Console.WriteLine("TemporalScalarSeed:True:ThreeScalarCompositions:True:ExactOffset:True:OffsetKeyRemoveAdd:True:ColdReopen:True");
#else
        Upgrade(directory);
        Console.WriteLine("TemporalScalarUpgrade:True:DeletedInlineClr:True:ExplicitOwnerAndValueUpgrade:True:HistoricalExact:True:ForcedBaseThenDelta:True:ColdReopen:True");
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
        FrameAddress first, offsetChanged, historical, unchanged;
        ObjectId worldId;
        using (Repository repository = Repository.CreateNew(directory, Models(), Options)) {
            using BranchCheckout session = repository.CreateBranch("main", world, Policy);
            first = session.StateRevisionAddress!.Value;
            worldId = session.StateId!.Value;
            world.Timestamp = Moment(8);
            world.Point = world.Point with { Timestamp = Moment(8) };
            world.Points[0] = world.Points[0] with { Timestamp = Moment(8) };
            world.Timestamps[0] = Moment(8);
            world.OptionalTimestamps[1] = Moment(8);
            session.CommitEvent(((World)session.State!), Policy);
            offsetChanged = session.CommitState(Policy).RevisionAddress;
            Require(world.Keys.Remove(Moment(0)), "Missing timestamp key before actual stored-key replacement.");
            world.Keys.Add(Moment(8), World.SampleDate);
            session.CommitEvent(((World)session.State!), Policy);
            historical = session.CommitState(Policy).RevisionAddress;
            session.CommitEvent(((World)session.State!), Policy);
            unchanged = session.CommitState(Policy).RevisionAddress;
            Require(ReferenceEquals(((World)session.State!), world), "Commit replaced the working graph.");
        }
        Inspect(directory, (store, schemas) => {
            var ids = CheckHistorical(store, schemas, historical, worldId, 8, 8);
            CheckHistorical(store, schemas, first, worldId, 0, 0);
            Require(store.Read(offsetChanged).LocalObjects.Count == 4 &&
                store.Read(offsetChanged).LocalObjects.All(row => row.Kind == ObjectVersionKind.Delta),
                "Offset-only changes must survive in owner, inline, List and Nullable List bodies.");
            var row = store.Read(historical).LocalObjects.Single();
            Require(row.ObjectId == ids.Keys.Value && row.Kind == ObjectVersionKind.Delta,
                "Real timestamp key replacement must write one Dictionary Delta.");
            CheckKeyReplacement(row.Body);
            Require(store.Read(unchanged).LocalObjects.Count == 0, "Unchanged exact scalar representation wrote objects.");
        });
        File.WriteAllText(Path.Combine(directory, "historical.txt"), $"{historical.FileNumber}:{historical.FrameTicket.Packed}:{worldId.Value}");
        FrameAddress recaptured;
        using (Repository reopened = Repository.OpenExisting(directory, Models(), Options)) {
            using BranchCheckout restored = reopened.Checkout("main");
            CheckGraph(((World)restored.State!), 0, 8);
            restored.CommitEvent(((World)restored.State!), Policy);
            recaptured = restored.CommitState(Policy).RevisionAddress;
        }
        Inspect(directory, (store, _) => Require(store.Read(recaptured).LocalObjects.Count == 0,
            "ScalarDefault DateOnly/DateTimeOffset/TimeOnly modes changed after Load and recapture."));
    }
#else
    private static void Upgrade(string directory) {
        Require(typeof(World).Assembly.GetType("TemporalScalarPackageConsumerProbe.LegacyPoint") is null,
            "The historical inline CLR declaration must be absent.");
        string[] parts = File.ReadAllText(Path.Combine(directory, "historical.txt")).Split(':');
        FrameAddress historical = new(uint.Parse(parts[0]), SizedPtr.FromPacked(ulong.Parse(parts[1])));
        ObjectId worldId = new(uint.Parse(parts[2]));
        (ObjectId Points, ObjectId Keys) ids = default;
        Inspect(directory, (store, schemas) => ids = CheckHistorical(store, schemas, historical, worldId, 8, 8));
        Require(Upgrades.OwnerCalls == 0 && Upgrades.ValueCalls.Count == 0, "Exact reading ran business upgrades.");
        FrameAddress rewritten, unchanged, changed;
        using (Repository repository = Repository.OpenExisting(directory, Models(), Options)) {
            using BranchCheckout session = repository.Checkout("main");
            World world = ((World)session.State!);
            CheckGraph(world, 1000, 8);
            Require(Upgrades.OwnerCalls == 1 && Upgrades.ValueCalls.Count == 33 &&
                Upgrades.ValueCalls.Count(call => call.Id == worldId) == 1 &&
                Upgrades.ValueCalls.Count(call => call.Id == ids.Points) == 32,
                "Explicit owner dependency and List element conversion must each run exactly once per value.");
            session.CommitEvent(((World)session.State!), Policy);
            rewritten = session.CommitState(Policy).RevisionAddress;
            session.CommitEvent(((World)session.State!), Policy);
            unchanged = session.CommitState(Policy).RevisionAddress;
            world.Timestamp = Moment(-4);
            world.Points[0] = world.Points[0] with { Timestamp = Moment(-4) };
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
            CheckHistorical(store, schemas, historical, worldId, 8, 8);
        });
        using Repository reopened = Repository.OpenExisting(directory, Models(), Options);
        using BranchCheckout restored = reopened.Checkout("main");
        CheckGraph(((World)restored.State!), 1000, -4);
        Require(Upgrades.OwnerCalls == 1 && Upgrades.ValueCalls.Count == 33, "Cold reopen repeated business upgrade.");
    }
#endif

    private static (ObjectId Points, ObjectId Keys) CheckHistorical(StateRevisionStore store, SchemaStore schemas,
        FrameAddress address, ObjectId worldId, int offsetHours, int keyOffsetHours) {
        DecodedRevision decoded = RevisionDecoder.Read(store, schemas, address, Readers());
        var world = decoded.GetRequired(worldId).GetState<WorldStates.V1>();
        Require(world.Segment0Field1 == World.SampleDate && world.Segment0Field3 == TimeOnly.MinValue &&
            SameExact(world.Segment0Field2, Moment(offsetHours)), "Stored-exact direct scalar values changed.");
        var point = world.Segment0Field4;
        Require(point.Segment0Field1 == World.SampleDate && SameExact(point.Segment0Field2, Moment(offsetHours)) &&
            point.Segment0Field3 == TimeOnly.MinValue && point.Segment0Field4 == 10,
            "Deleted inline historical DTO lost scalar content.");
        var points = decoded.GetRequired(world.Segment0Field5).GetListState<PointStates.V1>();
        Require(points.Count == 32 && points[0].Segment0Field4 == 100 && SameExact(points[0].Segment0Field2, Moment(offsetHours)) &&
            world.Segment0Field5 == world.Segment0Field6, "Historical List values or shared identity changed.");
        var keys = decoded.GetRequired(world.Segment0Field7).GetDictionaryState<DateTimeOffset, DateOnly>();
        bool foundKey = false;
        foreach (var entry in keys.Entries) {
            if (entry.Key == Moment(0)) foundKey = SameExact(entry.Key, Moment(keyOffsetHours)) && entry.Value == World.SampleDate;
        }
        Require(keys.Count == 32 && keys.ComparerKind == DictionaryComparerKind.ScalarDefault &&
            foundKey,
            "Stored-exact DateTimeOffset key retained the wrong offset or comparer.");
        Require(decoded.GetRequired(world.Segment0Field12).GetDictionaryState<DateOnly, TimeOnly>().ComparerKind == DictionaryComparerKind.ScalarDefault &&
            decoded.GetRequired(world.Segment0Field13).GetDictionaryState<TimeOnly, DateTimeOffset>().ComparerKind == DictionaryComparerKind.ScalarDefault,
            "New builtin Dictionary keys require ScalarDefault.");
        Require(decoded.GetRequired(world.Segment0Field14).GetState<BoxStates.V1<DateOnly>>().Segment0Field1 == World.SampleDate &&
            SameExact(decoded.GetRequired(world.Segment0Field15).GetState<BoxStates.V1<DateTimeOffset>>().Segment0Field1, DateTimeOffset.MaxValue) &&
            decoded.GetRequired(world.Segment0Field16).GetState<BoxStates.V1<TimeOnly>>().Segment0Field1.Ticks == TimeOnly.MaxValue.Ticks - 1,
            "Historical generic scalar closures failed.");
        return (world.Segment0Field5, world.Segment0Field7);
    }

    private static void CheckGraph(World world, long offset, int offsetHours) {
        Require(world.Date == World.SampleDate && SameExact(world.Timestamp, Moment(offsetHours)) && world.Time == TimeOnly.MinValue &&
            world.Point.Date == World.SampleDate && SameExact(world.Point.Timestamp, Moment(8)) &&
            world.Point.Time == TimeOnly.MinValue && world.Point.Count == 10 + offset,
            "Direct or nested scalar representation failed restoration.");
        Require(ReferenceEquals(world.Points, world.Alias) && world.Points.Count == 32 &&
            SameExact(world.Points[0].Timestamp, Moment(offsetHours)) && world.Points[0].Count == 100 + offset,
            "Shared upgraded List state failed restoration.");
        Require(world.Keys[Moment(0)] == World.SampleDate && SameExact(world.Keys.Keys.Single(key => key == Moment(0)), Moment(8)) &&
            SameExact(world.Timestamps[0], Moment(8)) && SameExact(world.OptionalTimestamps[1]!.Value, Moment(8)) &&
            SameExact(world.OptionalTimestamps[2]!.Value, DateTimeOffset.MinValue) && world.OptionalTimestamps[0] is null,
            "Timestamp offsets or nullable containers were normalized accidentally.");
        Require(world.Dates[0] is null && world.Dates[1] == DateOnly.MinValue && world.Dates[2] == World.SampleDate &&
            world.Times[0, 0] is null && world.Times[0, 1] == TimeOnly.MaxValue && world.Times[0, 2]!.Value.Ticks == TimeOnly.MaxValue.Ticks - 1 &&
            world.ByDate[World.SampleDate] == TimeOnly.MaxValue && SameExact(world.ByTime[TimeOnly.MinValue], DateTimeOffset.MinValue) &&
            world.DateOnlyBox.Value == World.SampleDate && SameExact(world.TimestampBox.Value, DateTimeOffset.MaxValue) && world.TimeBox.Value.Ticks == TimeOnly.MaxValue.Ticks - 1,
            "Nullable arrays, builtin key/value or generic compositions failed restoration.");
    }

    private static void CheckKeyReplacement(ReadOnlySpan<byte> body) {
        BinaryPayloadReader reader = new(body);
        Require(reader.ReadUInt32() == 1 && SameExact(reader.ReadDateTimeOffset(), Moment(0)), "Expected one old-offset key Remove.");
        Require(reader.ReadUInt32() == 0, "Key representation replacement must not use PatchValue.");
        Require(reader.ReadUInt32() == 1 && SameExact(reader.ReadDateTimeOffset(), Moment(8)) && reader.ReadDateOnly() == World.SampleDate,
            "Expected one new-offset key Add with its unchanged value.");
        reader.EnsureFullyConsumed();
    }

    private static DateTimeOffset Moment(int offsetHours) => World.Moment(offsetHours);
    private static bool SameExact(DateTimeOffset left, DateTimeOffset right) => left.EqualsExact(right);
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
