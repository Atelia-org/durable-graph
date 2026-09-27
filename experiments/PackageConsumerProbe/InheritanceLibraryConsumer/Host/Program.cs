using Atelia.Data;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using InheritanceLibrary.App;
using InheritanceLibrary.Base;
using InheritanceLibrary.Middle;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

internal static class Program {
    private static readonly RbfSegmentStoreOptions Options = new() { NewStoreLayout = RbfSegmentStoreLayout.Flat };
    private static readonly ReadAmplificationBaseBudgetParameters Policy = new(int.MaxValue, 1);
    private static void Main(string[] args) {
        if (args.Length != 2) throw new ArgumentException("Expected seed|upgrade and database directory.");
        if (args[0] == "seed") Seed(args[1]);
        else if (args[0] == "upgrade") Upgrade(args[1]);
        else throw new ArgumentException("Unknown mode.");
    }
    private static StateModelRegistry Models() {
        var models = new StateModelRegistry();
        // Explicit complete registration; reverse dependency order is intentional.
        AppCatalog.Register(models);
        MiddleCatalog.Register(models);
        BaseCatalog.Register(models);
        return models;
    }
    private static StateReaderRegistry Readers() {
        var readers = new StateReaderRegistry();
        BaseCatalog.RegisterReaders(readers);
        MiddleCatalog.RegisterReaders(readers);
        AppCatalog.RegisterReaders(readers);
        return readers;
    }
    private static void Seed(string directory) {
        Require(!BaseCatalog.LegacyClrAbsent, "V1 must contain its original base and hidden inline CLR types.");
        FrameAddress ancestorChange, leafChange, historical, noChange;
        ObjectId worldId;
        using (EventHistoryRepository repository = EventHistoryRepository.CreateNew(directory, Models(), Options)) {
            Leaf world = new(10, true), child = new(11, false);
            world.Link = child;
            child.Link = world;
            world.Alias = child;
            world.Peers = [world, child, child];
            using EventHistorySession<Leaf> session = repository.CreateBranch("main", world, Policy);
            worldId = session.StateId;
            world.Ancestor++;
            session.CommitDomainEvent(session.State, Policy);
            ancestorChange = session.CommitDomainState(Policy).RevisionAddress;
            world.LeafValue++;
            session.CommitDomainEvent(session.State, Policy);
            leafChange = session.CommitDomainState(Policy).RevisionAddress;
            child.Ancestor++;
            session.CommitDomainEvent(session.State, Policy);
            historical = session.CommitDomainState(Policy).RevisionAddress;
            session.CommitDomainEvent(session.State, Policy);
            noChange = session.CommitDomainState(Policy).RevisionAddress;
            Require(ReferenceEquals(session.State, world), "Commit replaced the domain graph.");
            CheckGraph(world, false, 41, 50);
        }
        Inspect(directory, (store, schemas) => {
            ObjectId childId = CheckHistorical(store, schemas, historical, worldId);
            CheckWrites(store, ancestorChange, ObjectVersionKind.Delta, worldId);
            CheckWrites(store, leafChange, ObjectVersionKind.Delta, worldId);
            CheckWrites(store, historical, ObjectVersionKind.Delta, childId);
            Require(store.Read(noChange).LocalObjects.Count == 0, "Unchanged graph wrote objects.");
        });
        File.WriteAllText(Path.Combine(directory, "historical.txt"), $"{historical.FileNumber}:{historical.FrameTicket.Packed}:{worldId.Value}");
        Require(BaseCatalog.ConstructorCalls == 2 && MiddleCatalog.ConstructorCalls == 2 && AppCatalog.ConstructorCalls == 2,
            "Expected only the two manually constructed leaf instances.");
        using (EventHistoryRepository repository = EventHistoryRepository.OpenExisting(directory, Models(), Options)) {
            using EventHistorySession<Leaf> session = repository.Resume<Leaf>("main");
            CheckGraph(session.State, false, 41, 50);
            session.CommitDomainEvent(session.State, Policy);
            noChange = session.CommitDomainState(Policy).RevisionAddress;
        }
        Require(BaseCatalog.ConstructorCalls == 2 && MiddleCatalog.ConstructorCalls == 2 && AppCatalog.ConstructorCalls == 2,
            "Hydrate ran domain constructors or initializers.");
        Inspect(directory, (store, _) => Require(store.Read(noChange).LocalObjects.Count == 0, "Cold recapture wrote objects."));
        Console.WriteLine("InheritanceLibrarySeed:True:TwoExternalBaseEdges:True:HiddenReadonlyGenericNullable:True:PolymorphicCycles:True:AncestorAndLeafDelta:True:ColdNoChange:True");
    }
    private static void Upgrade(string directory) {
        Require(BaseCatalog.LegacyClrAbsent, "V2 must delete the old base and hidden inline CLR declarations.");
        string[] parts = File.ReadAllText(Path.Combine(directory, "historical.txt")).Split(':');
        FrameAddress historical = new(uint.Parse(parts[0]), SizedPtr.FromPacked(ulong.Parse(parts[1])));
        ObjectId worldId = new(uint.Parse(parts[2]));
        ObjectId childId = default;
        Inspect(directory, (store, schemas) => childId = CheckHistorical(store, schemas, historical, worldId));
        Require(AppCatalog.UpgradeCalls == 0 && BaseCatalog.UpgradeCalls == 0 && MiddleCatalog.UpgradeCalls == 0,
            "Stored exact reading ran business upgrades.");
        FrameAddress rewritten, noChange, changed;
        using (EventHistoryRepository repository = EventHistoryRepository.OpenExisting(directory, Models(), Options)) {
            using EventHistorySession<Leaf> session = repository.Resume<Leaf>("main");
            CheckGraph(session.State, true, 41, 50);
            CheckUpgradeCounters();
            session.CommitDomainEvent(session.State, Policy);
            rewritten = session.CommitDomainState(Policy).RevisionAddress;
            session.CommitDomainEvent(session.State, Policy);
            noChange = session.CommitDomainState(Policy).RevisionAddress;
            session.State.Ancestor++;
            ((Leaf)session.State.Link!).LeafValue++;
            session.CommitDomainEvent(session.State, Policy);
            changed = session.CommitDomainState(Policy).RevisionAddress;
        }
        Inspect(directory, (store, schemas) => {
            CheckWrites(store, rewritten, ObjectVersionKind.Base, worldId, childId);
            Require(store.Read(noChange).LocalObjects.Count == 0, "Upgraded state failed NoChange.");
            CheckWrites(store, changed, ObjectVersionKind.Delta, worldId, childId);
            CheckHistorical(store, schemas, historical, worldId);
        });
        using (EventHistoryRepository repository = EventHistoryRepository.OpenExisting(directory, Models(), Options)) {
            using EventHistorySession<Leaf> session = repository.Resume<Leaf>("main");
            CheckGraph(session.State, true, 42, 51);
            CheckUpgradeCounters();
            session.CommitDomainEvent(session.State, Policy);
            noChange = session.CommitDomainState(Policy).RevisionAddress;
        }
        Require(BaseCatalog.ConstructorCalls == 0 && MiddleCatalog.ConstructorCalls == 0 && AppCatalog.ConstructorCalls == 0,
            "Loading ran a base or leaf domain constructor.");
        Inspect(directory, (store, _) => Require(store.Read(noChange).LocalObjects.Count == 0, "Current cold state failed NoChange."));
        Console.WriteLine("InheritanceLibraryUpgrade:True:DeletedBaseAndInlineClr:True:HistoricalExact:True:LeafOnlyUpgrade:True:RequiredBases:True:NoChangeThenDelta:True:ColdReopen:True");
    }
    private static void CheckUpgradeCounters() => Require(AppCatalog.UpgradeCalls == 2 && BaseCatalog.UpgradeCalls == 0 && MiddleCatalog.UpgradeCalls == 0,
        "Each leaf must execute its own edge once; base/middle owner edges must not run.");
    private static ObjectId CheckHistorical(StateRevisionStore store, SchemaStore schemas, FrameAddress address, ObjectId worldId) {
        DecodedRevision decoded = RevisionDecoder.Read(store, schemas, address, Readers());
        Require(decoded.Objects.Count == 3, "Expected exactly two leaf objects and one List; no base/inline object rows.");
        ObjectStateRecord root = decoded.GetRequired(worldId);
        ObjectId childId = AppCatalog.HistoricalLink(root);
        AppCatalog.CheckHistorical(root, 10, 41, 51, true);
        AppCatalog.CheckHistorical(decoded.GetRequired(childId), 11, 41, 50, false);
        return childId;
    }
    private static void CheckWrites(StateRevisionStore store, FrameAddress address, ObjectVersionKind kind, params ObjectId[] ids) {
        var rows = store.Read(address).LocalObjects;
        Require(rows.Count == ids.Length && rows.All(row => row.Kind == kind) &&
            rows.Select(row => row.ObjectId).Order().SequenceEqual(ids.Select(id => id.Value).Order()),
            $"Expected only specified leaf objects to write {kind}; base and inline state are declaration segments, not objects.");
    }
    private static void CheckGraph(Leaf world, bool upgraded, int rootAncestor, int childLeafValue) {
        Require(world.Link is Leaf, "Base-declared reference lost actual leaf type.");
        Leaf child = (Leaf)world.Link!;
        Require(ReferenceEquals(world.Alias, child) && ReferenceEquals(child.Link, world) &&
            world.Peers is { Count: 3 } && ReferenceEquals(world.Peers[0], world) &&
            ReferenceEquals(world.Peers[1], child) && ReferenceEquals(world.Peers[2], child),
            "Shared references, polymorphic List or cyclic base reference changed.");
        Require(world.PairValue == 10 && child.PairValue == 11 && world.Extra == 10 && child.Extra == 11 &&
            world.MiddleValue == 60 && child.MiddleValue == 60 && world.TokenValid && child.TokenValid &&
            world.Ancestor == rootAncestor && child.Ancestor == 41 && world.LeafValue == 51 && child.LeafValue == childLeafValue &&
            world.Stamp == (upgraded ? 1020 : 20) && child.Stamp == (upgraded ? 1020 : 20) &&
            world.Optional == (upgraded ? 1030 : 30) && child.Optional is null,
            "Inherited private readonly values, hidden generic/nullable state or leaf fields changed.");
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
