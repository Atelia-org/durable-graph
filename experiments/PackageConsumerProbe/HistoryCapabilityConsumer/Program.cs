using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace HistoryCapabilityPackageConsumerProbe;

internal static class Program {
    private static readonly RbfSegmentStoreOptions Options = new() { NewStoreLayout = RbfSegmentStoreLayout.Flat };
    private static readonly ReadAmplificationBaseBudgetParameters Policy = new(int.MaxValue, 1);

    private static void Main(string[] args) {
        if (args.Length != 1) { throw new ArgumentException("Pass one artifact directory."); }
        string directory = Path.GetFullPath(args[0]);
#if HISTORY_V1
        Seed(directory);
        Console.WriteLine("HistorySeed:True:LegacyBaseDelta:True:LegacyCycle:True");
#elif READERS_ONLY
        ReadWithoutUpgrade(directory);
        Console.WriteLine("ExactReadersWithoutUpgrade:True:EditableLoadRequiresUpgrade:True");
#elif HISTORY_V2
        Migrate(directory);
        Console.WriteLine("AllSourceUpgraded:True:AbstractShellNotAllocated:True:BadOrphanRejected:True:LegacyRemoved:True:HistoricalRevisionPreserved:True");
#else
        ReadAfterDeletingShell(directory);
        Console.WriteLine("DeletedShellNewRevision:True:DeletedShellOldRevisionRejected:True:HistoryFilesAreNotReaders:True");
#endif
    }

    private static StateModelRegistry Models() {
        StateModelRegistry models = new();
        World.__DurableState.RegisterModel(models);
#if HISTORY_V1 || HISTORY_V2
        Legacy.__DurableState.RegisterModel(models);
#endif
        return models;
    }

    private static StateReaderRegistry Readers() {
        StateReaderRegistry readers = new();
        World.__DurableState.RegisterReaders(readers);
#if HISTORY_V1 || HISTORY_V2
        Legacy.__DurableState.RegisterReaders(readers);
#endif
        return readers;
    }

#if HISTORY_V1
    private static void Seed(string directory) {
        FrameAddress historical;
        ObjectId worldId;
        using (var repository = EventHistoryRepository.CreateNew(directory, Models(), Options))
        using (var session = repository.CreateBranch("main", new World(7, new Legacy(11)), Policy)) {
            worldId = session.StateId;
            session.CommitDomainEvent(session.State.SnapshotEvent(), Policy);
            session.State.Legacy.Change(12);
            historical = session.CommitDomainState(Policy).RevisionAddress;
        }
        using (var repository = EventHistoryRepository.OpenReadOnlyExisting(directory, Models(), Options)) {
            Require(repository.ReadState<World>(repository.GetHead("main")).Legacy.HasSelfCycle,
                "V1 self-cycle was not restored.");
        }
        using SegmentStore segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory, "state"), Options);
        using StateRevisionStore store = new(segments);
        StateRevision changed = store.Read(historical);
        ObjectVersionRecord delta = changed.LocalObjects.Single();
        Require(delta.ObjectId != worldId.Value && delta.Kind == ObjectVersionKind.Delta &&
            store.ReadObjectVersionChain(historical, delta.ObjectId).Records.Count == 2,
            "Expected child-only Legacy Base/Delta chain.");
    }
#elif READERS_ONLY
    private static void ReadWithoutUpgrade(string directory) {
        using var repository = EventHistoryRepository.OpenReadOnlyExisting(directory, Models(), Options);
        GraphFrame historical = repository.GetHead("main");
        using var file = RbfFile.OpenReadOnlyExisting(Path.Combine(directory, "schemas.rbf"));
        using SegmentStore segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory, "state"), Options);
        SchemaStore schemas = new(file, readOnly: true);
        using StateRevisionStore store = new(segments);
        DecodedRevision decoded = RevisionDecoder.Read(store, schemas, historical.RevisionAddress, Readers());
        ObjectId legacyId = decoded.Objects.Single(row => row.Id != historical.RootId).Id;
        Require(decoded.Objects.Count == 2 &&
            decoded.GetRequired(legacyId).GetState<Legacy.__DurableState.V1>().Segment0Field1 == 12 &&
            decoded.GetRequired(legacyId).GetState<Legacy.__DurableState.V1>().Segment0Field3 == 638_625_600_000_000_000,
            "Exact readers must reconstruct historical Base/Delta without compiled Upgrade methods.");
        ExpectInvalidData(() => repository.ReadState<World>(historical), "Missing single-object upgrade");
    }
#elif HISTORY_V2
    private static void Migrate(string directory) {
        FrameAddress historical, migrated;
        ObjectId worldId, legacyId;
        using (var repository = EventHistoryRepository.OpenReadOnlyExisting(directory, Models(), Options)) {
            GraphFrame head = repository.GetHead("main");
            historical = head.RevisionAddress;
            worldId = head.RootId;
            using var file = RbfFile.OpenReadOnlyExisting(Path.Combine(directory, "schemas.rbf"));
            using SegmentStore segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory, "state"), Options);
            DecodedRevision decoded = RevisionDecoder.Read(new(segments), new(file, readOnly: true), historical, Readers());
            legacyId = decoded.Objects.Single(row => row.Id != worldId).Id;
            Require(decoded.Objects.Count == 2 && World.UpgradeCalls == 0 && Legacy.UpgradeCalls == 0,
                "Exact reading must not normalize either object.");
            Legacy.ProduceInvalidReference = true;
            ExpectInvalidData(() => repository.ReadState<World>(head));
            Require(World.UpgradeCalls == 1 && Legacy.UpgradeCalls == 1,
                "Even a newly orphaned shell must upgrade and reject its invalid current reference.");
        }
        Legacy.ProduceInvalidReference = false;
        World.UpgradeCalls = Legacy.UpgradeCalls = 0;
        using (var repository = EventHistoryRepository.OpenExisting(directory, Models(), Options))
        using (var session = repository.Resume<World>("main")) {
            Require(session.State.Score == 107 && World.UpgradeCalls == 1 && Legacy.UpgradeCalls == 1 &&
                Legacy.LastHistoricalValue == 12, "Full source directory was not upgraded from its exact stored values.");
            Require(typeof(Legacy).IsAbstract, "Migration shell must be abstract.");
            bool allocationRejected = false;
            try { Legacy.__DurableState.Allocate(); }
            catch (InvalidOperationException) { allocationRejected = true; }
            Require(allocationRejected, "Successful Resume must not allocate the abstract orphan shell.");
            session.CommitDomainEvent(session.State.SnapshotEvent(), Policy);
            migrated = session.CommitDomainState(Policy).RevisionAddress;
        }
        using var fileAfter = RbfFile.OpenReadOnlyExisting(Path.Combine(directory, "schemas.rbf"));
        using SegmentStore segmentsAfter = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory, "state"), Options);
        SchemaStore schemas = new(fileAfter, readOnly: true);
        using StateRevisionStore store = new(segmentsAfter);
        StateRevision changed = store.Read(migrated);
        Require(!store.ReadLiveObjectHeadMap(migrated).ContainsKey(legacyId.Value) &&
            changed.LocalObjects.Count == 1 && changed.LocalObjects[0].ObjectId == worldId.Value &&
            changed.LocalObjects[0].Kind == ObjectVersionKind.Base,
            "Migration must force World Base and remove Legacy despite the intervening Event save.");
        Require(store.ReadLiveObjectHeadMap(migrated).Keys.SequenceEqual(new[] { worldId.Value }) &&
            RevisionDecoder.Read(store, schemas, historical, Readers()).Objects.Count == 2,
            "Migration must preserve historical membership while removing current Legacy.");
    }
#else
    private static void ReadAfterDeletingShell(string directory) {
        using var repository = EventHistoryRepository.OpenReadOnlyExisting(directory, Models(), Options);
        GraphFrame[] frames = repository.ReadFrames("main").ToArray();
        GraphFrame historical = frames[2];
        GraphFrame migrated = frames[^1];
        Require(historical.RootId == migrated.RootId, "Migration changed World identity.");
        Require(typeof(World).Assembly.GetType("HistoryCapabilityPackageConsumerProbe.Legacy") is null,
            "Third consumer must contain no Legacy CLR shell.");
        using var file = RbfFile.OpenReadOnlyExisting(Path.Combine(directory, "schemas.rbf"));
        using SegmentStore segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory, "state"), Options);
        SchemaStore schemas = new(file, readOnly: true);
        using StateRevisionStore store = new(segments);
        Require(schemas.GetRequired("package.history-legacy", 1).Version == 1,
            "Legacy metadata must exist independently of its absent executable reader.");
        World loaded = repository.ReadState<World>(migrated);
        Require(loaded.Score == 107 && World.UpgradeCalls == 0 &&
            RevisionDecoder.Read(store, schemas, migrated.RevisionAddress, Readers()).Objects.Count == 1,
            "Migrated Revision should load with only the surviving World model.");
        ExpectInvalidData(() => RevisionDecoder.Read(store, schemas, historical.RevisionAddress, Readers()),
            "No declaration factory is registered for package.history-legacy");
        ExpectInvalidData(() => repository.ReadState<World>(historical),
            "No declaration factory is registered for package.history-legacy");
    }
#endif

    private static void ExpectInvalidData(Action action, string? message = null) {
        try { action(); }
        catch (InvalidDataException error) when (message is null || error.Message.Contains(message, StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("Expected a specific InvalidDataException.");
    }

    internal static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
