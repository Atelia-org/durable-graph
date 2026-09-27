using Atelia.Data;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using BoxStates = Atelia.DurableGraph.Generated.Family_47656E65726963426F78;
using WorldStates = Atelia.DurableGraph.Generated.Family_47656E65726963576F726C64;
using PointStates = Atelia.DurableGraph.Generated.Family_47656E65726963506F696E74;
using WorldV1 = Atelia.DurableGraph.Generated.Family_47656E65726963576F726C64.V1<Atelia.DurableGraph.Generated.Family_47656E6572696350616972.V1<Atelia.DurableGraph.Generated.Family_47656E657269634C6567616379506F696E74.V1>>;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace GenericPackageConsumerProbe;

internal static class Program {
    private static readonly RbfSegmentStoreOptions Options = new() { NewStoreLayout = RbfSegmentStoreLayout.Flat };
    private static readonly ReadAmplificationBaseBudgetParameters Policy = new(int.MaxValue, 1);

    private static void Main(string[] args) {
        if (args.Length != 1) { throw new ArgumentException("Pass one repository directory."); }
        string directory = Path.GetFullPath(args[0]);
#if HISTORY_V1
        Seed(directory);
        Console.WriteLine("GenericSeed:True:OwnerDelta:True:ClosedSchemas:True:InlineNoObjectIds:True");
#elif OMIT_CLOSED_UPGRADE
        MissingClosedUpgrade(directory);
        Console.WriteLine("MissingClosedUpgrade:True:ExactDecodeAvailable:True:NoPointBusinessCallback:True");
#elif HISTORY_V2
        Migrate(directory);
        Console.WriteLine("GenericUpgrade:True:ClosedBusiness:True:ForcedBase:True:NoChangeThenDelta:True:PerObjectContext:True");
#else
        DeleteInlineDomain(directory);
        Console.WriteLine("GenericThirdVersion:True:DeletedInlineDomain:True:StoredExactHistory:True:AdjacentContexts:True:StableResave:True");
#endif
    }

    private static StateModelRegistry Models() {
        StateModelRegistry models = new();
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
        return models;
    }

    private static StateReaderRegistry Readers() {
        StateReaderRegistry readers = new();
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(readers);
        return readers;
    }

#if HISTORY_V1
    private static void Seed(string directory) {
        World world = new();
        FrameAddress initial;
        FrameAddress historical;
        ObjectId worldId;
        using (EventHistoryRepository repository = EventHistoryRepository.CreateNew(directory, Models(), Options)) {
            using EventHistorySession<World> session = repository.CreateBranch("main", world, Policy);
            initial = session.StateRevisionAddress;
            worldId = session.StateId;
            world.First.Set(12);
            session.CommitDomainEvent(session.State, Policy);
            historical = session.CommitDomainState(Policy).RevisionAddress;
            Require(ReferenceEquals(world, session.State) && world.First.TransientValue == 77,
                "Commit must keep the existing domain instances and transient state.");
        }
        Inspect(directory, (store, schemas) => {
            StateRevision first = store.Read(initial);
            Require(first.LocalObjects.Count == 5 && first.LocalObjects.All(row => row.Kind == ObjectVersionKind.Base),
                "Only World, three Box instances and string may have object rows.");
            var state = CheckHistoricalDto(store, schemas, historical, worldId);
            StateRevision delta = store.Read(historical);
            Require(delta.LocalObjects.Count == 1 && delta.LocalObjects[0].ObjectId == state.Segment0Field1.Value &&
                delta.LocalObjects[0].Kind == ObjectVersionKind.Delta, "One generic field edit must produce its object's Delta.");
            Require(store.ReadObjectVersionChain(historical, state.Segment0Field1.Value).Records.Count == 2,
                "The generic object lacks the persisted Base/Delta chain.");
        });
        WriteAddress(directory, "historical", historical, worldId);
        using EventHistoryRepository reopened = EventHistoryRepository.OpenExisting(directory, Models(), Options);
        using EventHistorySession<World> restored = reopened.Resume<World>("main");
        CheckCurrent(restored.State, 12, 31);
        Require(restored.State.Legacy.Left.Value == 41 && restored.State.Legacy.Right.Value == 51,
            "Generic readonly inline values were not restored.");
    }
#elif OMIT_CLOSED_UPGRADE
    private static void MissingClosedUpgrade(string directory) {
        var (historical, worldId) = ReadAddress(directory, "historical");
        Inspect(directory, (store, schemas) => {
            var source = CheckHistoricalDto(store, schemas, historical, worldId);
            bool rejected = false;
            try { _ = ReadHistorical(directory, historical); }
            catch (InvalidOperationException) { rejected = true; }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, "A missing Point-state conversion must reject editable Load.");
            Require(UpgradeTrace.Calls.All(call => call.ObjectId != source.Segment0Field3),
                "An invalid Point owner chain must fail before its first business callback.");
        });
    }
#elif HISTORY_V2
    private static void Migrate(string directory) {
        var (historical, worldId) = ReadAddress(directory, "historical");
        FrameAddress upgraded;
        FrameAddress unchanged;
        FrameAddress changed;
        ObjectId changedId = default;
        using (EventHistoryRepository repository = EventHistoryRepository.OpenExisting(directory, Models(), Options)) {
            using EventHistorySession<World> session = repository.Resume<World>("main");
            World world = session.State;
            Box<int> first = world.First;
            CheckCurrent(world, 12, 1031);
            Require(world.First.Stamp == 100 && world.Second.Stamp == 100 && world.PointBox.Stamp == 200 &&
                world.Legacy.Left.Value == 1041 && world.Legacy.Right.Value == 1051,
                "Generic pass-through, closed business, or explicit nested owner conversion did not run.");
            Require(UpgradeTrace.Calls.Count == 4, "Each stored durable object must take one adjacent Upgrade.");
            session.CommitDomainEvent(session.State, Policy);
            upgraded = session.CommitDomainState(Policy).RevisionAddress;
            session.CommitDomainEvent(session.State, Policy);
            unchanged = session.CommitDomainState(Policy).RevisionAddress;
            world.First.Set(13);
            session.CommitDomainEvent(session.State, Policy);
            changed = session.CommitDomainState(Policy).RevisionAddress;
            Require(ReferenceEquals(world, session.State) && ReferenceEquals(first, session.State.First) &&
                UpgradeTrace.Calls.Count == 4, "Commit must install its DTO baseline without replacing or re-upgrading instances.");
        }
        Inspect(directory, (store, schemas) => {
            var old = CheckHistoricalDto(store, schemas, historical, worldId);
            changedId = old.Segment0Field1;
            RequireForcedBaseAndStableDelta(store, upgraded, unchanged, changed, changedId);
            CheckContexts(schemas, expectedObjectCount: 4, expectedEdgesPerObject: 1);
            Require(UpgradeTrace.Calls.Single(call => call.ObjectId == old.Segment0Field3).Provider == "point-closed",
                "The explicit closed provider did not take priority over generic pass-through.");
        });
        WriteAddress(directory, "migrated", changed, worldId);
    }
#else
    private static void DeleteInlineDomain(string directory) {
        var (historical, worldId) = ReadAddress(directory, "historical");
        Require(typeof(World).Assembly.GetType("GenericPackageConsumerProbe.LegacyPoint") is null,
            "The final consumer must exclude the old inline domain declaration.");
        Require(typeof(Box<Point>).IsConstructedGenericType,
            "Independent Box<Point> source rows deliberately retain their current CLR closure.");
        Inspect(directory, (store, schemas) => {
            CheckHistoricalDto(store, schemas, historical, worldId);
            Require(UpgradeTrace.Calls.Count == 0, "Stored-exact decoding must not invoke business Upgrade.");
            World oldest = ReadHistorical(directory, historical);
            CheckCurrent(oldest, 12, 1031);
            Require(oldest.Summary == 2092 && oldest.First.LastStamp == 300 && UpgradeTrace.Calls.Count == 8,
                "The retained state-only history must execute both owner edges without LegacyPoint CLR.");
        });
        var oldestTrace = UpgradeTrace.Calls.ToArray();
        UpgradeTrace.Calls.Clear();
        FrameAddress upgraded;
        FrameAddress unchanged;
        FrameAddress changed;
        using (EventHistoryRepository repository = EventHistoryRepository.OpenExisting(directory, Models(), Options)) {
            using EventHistorySession<World> session = repository.Resume<World>("main");
            World world = session.State;
            CheckCurrent(world, 13, 1031);
            Require(world.Summary == 2092 && world.First.LastStamp == 300 && UpgradeTrace.Calls.Count == 4,
                "The current V2 head must execute only its remaining adjacent edge.");
            session.CommitDomainEvent(session.State, Policy);
            upgraded = session.CommitDomainState(Policy).RevisionAddress;
            session.CommitDomainEvent(session.State, Policy);
            unchanged = session.CommitDomainState(Policy).RevisionAddress;
            world.First.Set(14);
            session.CommitDomainEvent(session.State, Policy);
            changed = session.CommitDomainState(Policy).RevisionAddress;
            Require(ReferenceEquals(world, session.State), "A third-version commit replaced the domain World.");
        }
        Inspect(directory, (store, schemas) => {
            var old = CheckHistoricalDto(store, schemas, historical, worldId);
            RequireForcedBaseAndStableDelta(store, upgraded, unchanged, changed, old.Segment0Field1);
            CheckContexts(schemas, expectedObjectCount: 4, expectedEdgesPerObject: 1);
            UpgradeTrace.Calls.Clear();
            UpgradeTrace.Calls.AddRange(oldestTrace);
            CheckContexts(schemas, expectedObjectCount: 4, expectedEdgesPerObject: 2);
            Require(store.Read(changed).RemovedObjectIds.Count == 0,
                "Deleting an inline value with no reference slots must not invent object removals.");
        });
        UpgradeTrace.Calls.Clear();
        using EventHistoryRepository reopened = EventHistoryRepository.OpenExisting(directory, Models(), Options);
        using EventHistorySession<World> current = reopened.Resume<World>("main");
        CheckCurrent(current.State, 14, 1031);
        Require(current.State.Summary == 2092 && UpgradeTrace.Calls.Count == 0 && reopened.GetHead("main").RevisionAddress == changed,
            "The published current head must reopen without invoking Upgrade.");
    }
#endif

    private static void CheckCurrent(World world, int first, long point) {
        Require(world.First.Value == first && world.Second.Value == 21 && world.PointBox.Value.Value == point &&
            !ReferenceEquals(world.First, world.Second) && world.Label == "generic history",
            "Closed generic objects lost their values, identities, or references.");
        Require(world.First.CreatedAtTicks == 638_625_600_000_000_000 && world.Second.CreatedAtTicks == 638_625_600_000_000_000 &&
            world.PointBox.CreatedAtTicks == 638_625_600_000_000_000, "Stable creation timestamps did not survive capture, Upgrade, or restore.");
        Require(world.First.TransientValue == 0 && world.Second.TransientValue == 0 && world.PointBox.TransientValue == 0,
            "Restoration must bypass constructors and transient initializers.");
    }

    private static WorldV1 CheckHistoricalDto(StateRevisionStore store, SchemaStore schemas,
        FrameAddress historical, ObjectId worldId) {
        DecodedRevision decoded = RevisionDecoder.Read(store, schemas, historical, Readers());
        var world = decoded.GetRequired(worldId).GetState<WorldV1>();
        var first = decoded.GetRequired(world.Segment0Field1).GetState<BoxStates.V1<int>>();
        var second = decoded.GetRequired(world.Segment0Field2).GetState<BoxStates.V1<int>>();
        var point = decoded.GetRequired(world.Segment0Field3).GetState<BoxStates.V1<PointStates.V1>>();
        Require(decoded.Objects.Count == 5 && decoded.GetRequired(worldId).Schema!.Version == 1 &&
            first.Segment0Field1 == 12 && second.Segment0Field1 == 21 && point.Segment0Field1.Segment0Field1 == 31 &&
            first.Segment0Field4 == 638_625_600_000_000_000 && second.Segment0Field4 == 638_625_600_000_000_000 &&
            point.Segment0Field4 == 638_625_600_000_000_000 &&
            world.Segment0Field5.Segment0Field1.Segment0Field1 == 41 &&
            world.Segment0Field5.Segment0Field2.Segment0Field1 == 51,
            "Stored-exact generic DTOs/Base+Delta reconstruction changed across package builds.");
        DurableSchema intSchema = decoded.GetRequired(world.Segment0Field1).Schema!;
        DurableSchema pointSchema = decoded.GetRequired(world.Segment0Field3).Schema!;
        Require(intSchema.Type == TypeExpr.Named("GenericBox", TypeExpr.Builtin(TypeTag.Int32)) &&
            pointSchema.Type == TypeExpr.Named("GenericBox", TypeExpr.Named("GenericPoint")) &&
            intSchema.Type != pointSchema.Type && pointSchema.Fields[0].InlineSchema!.Version == 1,
            "Persisted constructed type identities or historical inline exact layout were lost.");
        return world;
    }

    private static void RequireForcedBaseAndStableDelta(StateRevisionStore store, FrameAddress upgraded,
        FrameAddress unchanged, FrameAddress changed, ObjectId changedId) {
        StateRevision rewrite = store.Read(upgraded);
        Require(rewrite.LocalObjects.Count == 4 && rewrite.LocalObjects.All(row => row.Kind == ObjectVersionKind.Base),
            "All four upgraded durable objects, including layout-equivalent Box<int>, must force Base.");
        Require(store.Read(unchanged).LocalObjects.Count == 0, "The installed DTO baseline must compare unchanged.");
        StateRevision delta = store.Read(changed);
        Require(delta.LocalObjects.Count == 1 && delta.LocalObjects[0].ObjectId == changedId.Value &&
            delta.LocalObjects[0].Kind == ObjectVersionKind.Delta, "The next same-Schema edit must use ordinary Delta.");
    }

    private static void CheckContexts(SchemaStore schemas, int expectedObjectCount, int expectedEdgesPerObject) {
        var groups = UpgradeTrace.Calls.GroupBy(call => call.ObjectId).ToArray();
        Require(groups.Length == expectedObjectCount && groups.All(group => !group.Key.IsNull && group.Count() == expectedEdgesPerObject),
            "UpgradeContext has stale object identity or the wrong edge count.");
        foreach (var group in groups) {
            DurableSchema? previousTarget = null;
            foreach (var call in group) {
                Require(call.Source.Type == call.Target.Type && call.Target.Version == call.Source.Version + 1,
                    "UpgradeContext must describe its current adjacent owner edge.");
                Require(call.Source.Equals(schemas.GetRequired(call.Source.Type, call.Source.Version)) &&
                    call.Target.Equals(schemas.GetRequired(call.Target.Type, call.Target.Version)),
                    "UpgradeContext endpoints disagree with complete persisted exact schemas.");
                if (previousTarget is not null) {
                    Require(previousTarget.Equals(call.Source), "Adjacent contexts do not share the same full middle layout.");
                }
                previousTarget = call.Target;
            }
        }
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
