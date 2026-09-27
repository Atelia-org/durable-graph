using Atelia.Data;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using BoxStates = Atelia.DurableGraph.Generated.Family_426F78;
using PointStates = Atelia.DurableGraph.Generated.Family_506F696E74;
using PairStates = Atelia.DurableGraph.Generated.Family_50616972;
using WorldV1 = Atelia.DurableGraph.Generated.Family_576F726C64.V1<Atelia.DurableGraph.Generated.Family_50616972.V1<Atelia.DurableGraph.Generated.Family_4C6567616379506F696E74.V1>>;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace ValueUpgradePackageConsumerProbe;

internal static class Program {
    private static readonly RbfSegmentStoreOptions Options = new() { NewStoreLayout = RbfSegmentStoreLayout.Flat };
    private static readonly ReadAmplificationBaseBudgetParameters Policy = new(int.MaxValue, 1);

    private static void Main(string[] args) {
        if (args.Length != 1) { throw new ArgumentException("Pass one repository directory."); }
        string directory = Path.GetFullPath(args[0]);
#if HISTORY_V1
        Seed(directory);
        Console.WriteLine("ValueSeed:True:OwnerDelta:True:NestedInline:True:ExactHistory:True");
#elif OMIT_POINT_RULE
        MissingPointRule(directory);
        Console.WriteLine("MissingValueRule:True:ExactDecodeAvailable:True:NoInvalidOwnerCallback:True");
#elif HISTORY_V2
        Migrate(directory);
        Console.WriteLine("ValueUpgrade:True:OpenOwnerAndPair:True:ReusedLeaf:True:ScopedContexts:True:ForcedBaseThenDelta:True");
#else
        DeleteInlineDomain(directory);
        Console.WriteLine("ValueHistory:True:DeletedInlineDomain:True:RetainedValueRule:True:AdjacentContexts:True:StableResave:True");
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
                "Commit must retain the domain instances and their transient state.");
        }
        Inspect(directory, (store, schemas) => {
            StateRevision first = store.Read(initial);
            Require(first.LocalObjects.Count == 6 && first.LocalObjects.All(row => row.Kind == ObjectVersionKind.Base),
                "Only World, four Box instances and string may have object rows; inline values have no IDs.");
            var state = CheckHistoricalDto(store, schemas, historical, worldId);
            StateRevision delta = store.Read(historical);
            Require(delta.LocalObjects.Count == 1 && delta.LocalObjects[0].ObjectId == state.Segment0Field1.Value &&
                delta.LocalObjects[0].Kind == ObjectVersionKind.Delta &&
                store.ReadObjectVersionChain(historical, state.Segment0Field1.Value).Records.Count == 2,
                "One generic field edit must produce its object's persisted Base/Delta chain.");
        });
        WriteAddress(directory, historical, worldId);
        using EventHistoryRepository reopened = EventHistoryRepository.OpenExisting(directory, Models(), Options);
        using EventHistorySession<World> restored = reopened.Resume<World>("main");
        CheckCurrent(restored.State, 12, offset: 0);
        Require(restored.State.Legacy.Left.Value == 41 && restored.State.Legacy.Right.Value == 51,
            "The initial nested readonly inline states were not restored.");
    }
#elif OMIT_POINT_RULE
    private static void MissingPointRule(string directory) {
        var (historical, worldId) = ReadAddress(directory);
        Inspect(directory, (store, schemas) => {
            var old = CheckHistoricalDto(store, schemas, historical, worldId);
            bool rejected = false;
            try { _ = ReadHistorical(directory, historical); }
            catch (InvalidOperationException) { rejected = true; }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, "A missing declared Point conversion must reject editable Load.");
            Require(UpgradeTrace.Calls.All(call => call.ObjectId != old.Segment0Field3 && call.ObjectId != old.Segment0Field4),
                "A missing direct or nested value rule must reject the affected owner's whole chain before its first callback.");
        });
    }
#elif HISTORY_V2
    private static void Migrate(string directory) {
        var (historical, worldId) = ReadAddress(directory);
        FrameAddress upgraded;
        FrameAddress unchanged;
        FrameAddress changed;
        using (EventHistoryRepository repository = EventHistoryRepository.OpenExisting(directory, Models(), Options)) {
            using EventHistorySession<World> session = repository.Resume<World>("main");
            World world = session.State;
            Box<Pair<Point>> nested = world.Nested;
            CheckCurrent(world, 12, offset: 1000);
            Require(world.First.Stamp == 100 && world.Second.Stamp == 100 && world.PointBox.Stamp == 100 &&
                world.Nested.Stamp == 100 && world.Legacy.Left.Value == 1041 && world.Legacy.Right.Value == 1051,
                "One open owner must support KeepExact, leaf and nested Pair conversion.");
            Require(UpgradeTrace.Calls.Count == 12, "Expected five owner and seven explicitly invoked value callbacks.");
            session.CommitDomainEvent(session.State, Policy);
            upgraded = session.CommitDomainState(Policy).RevisionAddress;
            session.CommitDomainEvent(session.State, Policy);
            unchanged = session.CommitDomainState(Policy).RevisionAddress;
            world.First.Set(13);
            session.CommitDomainEvent(session.State, Policy);
            changed = session.CommitDomainState(Policy).RevisionAddress;
            Require(ReferenceEquals(world, session.State) && ReferenceEquals(nested, session.State.Nested) &&
                UpgradeTrace.Calls.Count == 12, "Commit must install the DTO baseline without replacing or re-upgrading instances.");
        }
        Inspect(directory, (store, schemas) => {
            var old = CheckHistoricalDto(store, schemas, historical, worldId);
            RequireForcedBaseAndStableDelta(store, upgraded, unchanged, changed, old.Segment0Field1);
            CheckContexts(schemas, expectedEdges: 1);
            CheckNestedCallbacks(old, worldId);
        });
    }
#else
    private static void DeleteInlineDomain(string directory) {
        var (historical, worldId) = ReadAddress(directory);
        Require(typeof(World).Assembly.GetType("ValueUpgradePackageConsumerProbe.LegacyPoint") is null,
            "The final consumer must physically exclude the historical inline domain declaration.");
        Inspect(directory, (store, schemas) => {
            var old = CheckHistoricalDto(store, schemas, historical, worldId);
            Require(UpgradeTrace.Calls.Count == 0, "Stored-exact DTO decoding must not invoke business Upgrade.");
            World oldest = ReadHistorical(directory, historical);
            CheckCurrent(oldest, 12, offset: 1000);
            Require(oldest.Summary == 2092 && oldest.First.LastStamp == 300 && UpgradeTrace.Calls.Count == 17,
                "Retained state-only value providers must run through two owner edges without LegacyPoint CLR.");
            CheckNestedCallbacks(old, worldId);
        });
        var oldestTrace = UpgradeTrace.Calls.ToArray();
        UpgradeTrace.Calls.Clear();
        FrameAddress upgraded;
        FrameAddress unchanged;
        FrameAddress changed;
        using (EventHistoryRepository repository = EventHistoryRepository.OpenExisting(directory, Models(), Options)) {
            using EventHistorySession<World> session = repository.Resume<World>("main");
            World world = session.State;
            CheckCurrent(world, 13, offset: 1000);
            Require(world.Summary == 2092 && world.First.LastStamp == 300 && UpgradeTrace.Calls.Count == 5,
                "The V2 head must execute only its remaining owner edges, with KeepExact for unchanged values.");
            session.CommitDomainEvent(session.State, Policy);
            upgraded = session.CommitDomainState(Policy).RevisionAddress;
            session.CommitDomainEvent(session.State, Policy);
            unchanged = session.CommitDomainState(Policy).RevisionAddress;
            world.First.Set(14);
            session.CommitDomainEvent(session.State, Policy);
            changed = session.CommitDomainState(Policy).RevisionAddress;
            Require(ReferenceEquals(world, session.State), "Commit replaced the domain World.");
        }
        Inspect(directory, (store, schemas) => {
            var old = CheckHistoricalDto(store, schemas, historical, worldId);
            RequireForcedBaseAndStableDelta(store, upgraded, unchanged, changed, old.Segment0Field1);
            CheckContexts(schemas, expectedEdges: 1);
            UpgradeTrace.Calls.Clear();
            UpgradeTrace.Calls.AddRange(oldestTrace);
            CheckContexts(schemas, expectedEdges: 2);
            Require(store.Read(changed).RemovedObjectIds.Count == 0,
                "Deleting inline values with no references must not invent object removals.");
        });
        UpgradeTrace.Calls.Clear();
        using EventHistoryRepository reopened = EventHistoryRepository.OpenExisting(directory, Models(), Options);
        using EventHistorySession<World> current = reopened.Resume<World>("main");
        CheckCurrent(current.State, 14, offset: 1000);
        Require(current.State.Summary == 2092 && UpgradeTrace.Calls.Count == 0 && reopened.GetHead("main").RevisionAddress == changed,
            "The published current head must reopen without Upgrade.");
    }
#endif

    private static void CheckCurrent(World world, int first, long offset) {
        Require(world.First.Value == first && world.Second.Value == 21 && world.PointBox.Value.Value == 31 + offset &&
            world.Nested.Value.Left.Value == 61 + offset && world.Nested.Value.Right.Value == 71 + offset &&
            !ReferenceEquals(world.First, world.Second) && world.Label == "composable history",
            "Generic objects lost their values, identities or references.");
        Require(world.First.CreatedAtTicks == 638_625_600_000_000_000 && world.Second.CreatedAtTicks == 638_625_600_000_000_000 &&
            world.PointBox.CreatedAtTicks == 638_625_600_000_000_000 && world.Nested.CreatedAtTicks == 638_625_600_000_000_000,
            "Stable creation timestamps did not survive capture, Upgrade, or restore.");
        Require(world.First.TransientValue == 0 && world.Second.TransientValue == 0 &&
            world.PointBox.TransientValue == 0 && world.Nested.TransientValue == 0,
            "Restoration must bypass constructors and transient initializers.");
    }

    private static WorldV1 CheckHistoricalDto(StateRevisionStore store, SchemaStore schemas,
        FrameAddress historical, ObjectId worldId) {
        DecodedRevision decoded = RevisionDecoder.Read(store, schemas, historical, Readers());
        var world = decoded.GetRequired(worldId).GetState<WorldV1>();
        var first = decoded.GetRequired(world.Segment0Field1).GetState<BoxStates.V1<int>>();
        var second = decoded.GetRequired(world.Segment0Field2).GetState<BoxStates.V1<int>>();
        var point = decoded.GetRequired(world.Segment0Field4).GetState<BoxStates.V1<PointStates.V1>>();
        var nested = decoded.GetRequired(world.Segment0Field3).GetState<BoxStates.V1<PairStates.V1<PointStates.V1>>>();
        Require(decoded.Objects.Count == 6 && decoded.GetRequired(worldId).Schema!.Version == 1 &&
            first.Segment0Field1 == 12 && second.Segment0Field1 == 21 && point.Segment0Field1.Segment0Field1 == 31 &&
            first.Segment0Field4 == 638_625_600_000_000_000 && second.Segment0Field4 == 638_625_600_000_000_000 &&
            point.Segment0Field4 == 638_625_600_000_000_000 && nested.Segment0Field4 == 638_625_600_000_000_000 &&
            nested.Segment0Field1.Segment0Field1.Segment0Field1 == 61 &&
            nested.Segment0Field1.Segment0Field2.Segment0Field1 == 71 &&
            world.Segment0Field6.Segment0Field1.Segment0Field1 == 41 &&
            world.Segment0Field6.Segment0Field2.Segment0Field1 == 51,
            "Stored-exact DTOs and their persisted Base/Delta reconstruction changed across builds.");
        DurableSchema nestedSchema = decoded.GetRequired(world.Segment0Field3).Schema!;
        Require(nestedSchema.Type == TypeExpr.Named("Box", TypeExpr.Named("Pair", TypeExpr.Named("Point"))) &&
            nestedSchema.Fields[0].InlineSchema!.Version == 1 &&
            nestedSchema.Fields[0].InlineSchema!.Fields[0].InlineSchema!.Version == 1,
            "The nested constructed identity or its complete historical inline layout was lost.");
        return world;
    }

    private static void RequireForcedBaseAndStableDelta(StateRevisionStore store, FrameAddress upgraded,
        FrameAddress unchanged, FrameAddress changed, ObjectId changedId) {
        StateRevision rewrite = store.Read(upgraded);
        Require(rewrite.LocalObjects.Count == 5 && rewrite.LocalObjects.All(row => row.Kind == ObjectVersionKind.Base),
            "All five upgraded durable objects must force Base, including KeepExact Box<int>.");
        Require(store.Read(unchanged).LocalObjects.Count == 0, "The installed DTO baseline must compare unchanged.");
        StateRevision delta = store.Read(changed);
        Require(delta.LocalObjects.Count == 1 && delta.LocalObjects[0].ObjectId == changedId.Value &&
            delta.LocalObjects[0].Kind == ObjectVersionKind.Delta, "A subsequent same-Schema edit must produce ordinary Delta.");
    }

    private static void CheckContexts(SchemaStore schemas, int expectedEdges) {
        var groups = UpgradeTrace.Calls.GroupBy(call => call.ObjectId).ToArray();
        Require(groups.Length == 5 && groups.All(group => !group.Key.IsNull), "Context leaked an owner's object identity.");
        foreach (var group in groups) {
            var edges = group.GroupBy(call => call.Source.Version).ToArray();
            Require(edges.Length == expectedEdges, "Context retained the wrong owner edge count.");
            DurableSchema? previousTarget = null;
            foreach (var edge in edges) {
                var owner = edge.First();
                Require(IsOwner(owner.Provider) && edge.Count(call => IsOwner(call.Provider)) == 1,
                    "Each owner edge must start with exactly one owner callback.");
                foreach (var call in edge) {
                    Require(call.Source.Equals(owner.Source) && call.Target.Equals(owner.Target) &&
                        call.Source.Type == call.Target.Type && call.Target.Version == call.Source.Version + 1,
                        "A child Context must retain the complete containing owner edge, not the inline provider's versions.");
                    Require(call.Source.Equals(schemas.GetRequired(call.Source.Type, call.Source.Version)) &&
                        call.Target.Equals(schemas.GetRequired(call.Target.Type, call.Target.Version)),
                        "Context endpoints disagree with complete persisted exact schemas.");
                }
                if (previousTarget is not null) {
                    Require(previousTarget.Equals(owner.Source), "Adjacent contexts disagree about the full middle layout.");
                }
                previousTarget = owner.Target;
            }
        }
    }

    private static void CheckNestedCallbacks(WorldV1 world, ObjectId worldId) {
        Require(Providers(world.Segment0Field1).SequenceEqual(new[] { "box-value" }) &&
            Providers(world.Segment0Field2).SequenceEqual(new[] { "box-value" }) &&
            Providers(world.Segment0Field4).SequenceEqual(new[] { "box-value", "point-leaf" }) &&
            Providers(world.Segment0Field3).SequenceEqual(new[] { "box-value", "pair-value", "point-leaf", "point-leaf" }) &&
            Providers(worldId).SequenceEqual(new[] { "world-value", "pair-value", "legacy-leaf", "legacy-leaf" }),
            "Open owner/Pair composition lost local key scopes, leaf reuse or per-object invocation state.");
        IEnumerable<string> Providers(ObjectId id) => UpgradeTrace.Calls
            .Where(call => call.ObjectId == id && call.Source.Version == 1).Select(call => call.Provider);
    }

    private static bool IsOwner(string provider) => provider.StartsWith("box-", StringComparison.Ordinal) ||
        provider.StartsWith("world-", StringComparison.Ordinal);

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

    private static void WriteAddress(string directory, FrameAddress revision, ObjectId worldId) =>
        File.WriteAllText(Path.Combine(directory, "historical.txt"), $"{revision.FileNumber}:{revision.FrameTicket.Packed}:{worldId.Value}");

    private static (FrameAddress Revision, ObjectId WorldId) ReadAddress(string directory) {
        string[] parts = File.ReadAllText(Path.Combine(directory, "historical.txt")).Split(':');
        Require(parts.Length == 3, "Malformed probe address handoff.");
        return (new FrameAddress(uint.Parse(parts[0]), SizedPtr.FromPacked(ulong.Parse(parts[1]))), new ObjectId(uint.Parse(parts[2])));
    }

    private static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
