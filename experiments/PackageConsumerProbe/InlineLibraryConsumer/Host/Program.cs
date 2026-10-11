using Atelia.Data;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using InlineLibrary.App;
using InlineLibrary.A;
using InlineLibrary.B;


internal static class Program {
    private const long Options = 1024 * 1024;
    private static readonly ReadAmplificationBaseBudgetParameters Policy = new(int.MaxValue, 1);
    private static void Main(string[] args) {
        if (args.Length != 2) throw new ArgumentException("Expected seed|upgrade and database directory.");
        if (args[0] == "seed") Seed(args[1]);
        else if (args[0] == "upgrade") Upgrade(args[1]);
        else throw new ArgumentException("Unknown mode.");
    }
    private static StateModelRegistry Models() {
        var models = new StateModelRegistry();
        AppCatalog.Register(models);
        LibraryACatalog.Register(models);
        LibraryBCatalog.Register(models);
        return models;
    }
    private static StateReaderRegistry Readers() {
        var readers = new StateReaderRegistry();
        LibraryBCatalog.RegisterReaders(readers);
        LibraryACatalog.RegisterReaders(readers);
        AppCatalog.RegisterReaders(readers);
        return readers;
    }
    private static void Seed(string directory) {
        Require(!LibraryBCatalog.LegacyClrAbsent, "V1 must contain its original inline CLR type.");
        FrameAddress historical, noChange;
        ObjectId worldId;
        using (Repository repository = Repository.CreateNew(directory, Models(), Options)) {
            World world = new();
            using BranchCheckout session = repository.CreateBranch("main", world, Policy);
            worldId = session.StateId!.Value;
            world.Value++;
            session.CommitEvent(((World)session.State!), Policy);
            historical = session.CommitState(Policy).RevisionAddress;
            session.CommitEvent(((World)session.State!), Policy);
            noChange = session.CommitState(Policy).RevisionAddress;
            Require(ReferenceEquals(((World)session.State!), world), "Commit replaced the domain graph.");
        }
        Inspect(directory, (store, schemas) => {
            CheckHistorical(store, schemas, historical, worldId);
            CheckSingleWrite(store, historical, worldId, ObjectVersionKind.Delta);
            Require(store.Read(noChange).LocalObjects.Count == 0, "Unchanged graph wrote objects.");
        });
        File.WriteAllText(Path.Combine(directory, "historical.txt"), $"{ProbeArtifacts.Format(historical)}:{worldId.Value}");
        using (Repository repository = Repository.OpenExisting(directory, Models(), Options)) {
            using BranchCheckout session = repository.Checkout("main");
            CheckGraph(((World)session.State!), 11, 20);
            session.CommitEvent(((World)session.State!), Policy);
            noChange = session.CommitState(Policy).RevisionAddress;
        }
        Inspect(directory, (store, _) => Require(store.Read(noChange).LocalObjects.Count == 0, "Cold recapture wrote objects."));
        Console.WriteLine("InlineLibrarySeed:True:TransitivePrivateInline:True:NullableAndGeneric:True:OwnerDelta:True:ColdNoChange:True");
    }
    private static void Upgrade(string directory) {
        Require(LibraryBCatalog.LegacyClrAbsent, "V2 must delete the old inline CLR declaration.");
        string[] parts = File.ReadAllText(Path.Combine(directory, "historical.txt")).Split(':');
        FrameAddress historical = ProbeArtifacts.Parse(parts[0]);
        ObjectId worldId = new(uint.Parse(parts[1]));
        Inspect(directory, (store, schemas) => CheckHistorical(store, schemas, historical, worldId));
        Require(AppCatalog.UpgradeCalls == 0, "Exact reading ran business upgrades.");
        FrameAddress rewritten, noChange, changed;
        using (Repository repository = Repository.OpenExisting(directory, Models(), Options)) {
            using BranchCheckout session = repository.Checkout("main");
            CheckGraph(((World)session.State!), 1011, 1020);
            Require(AppCatalog.UpgradeCalls == 1, "Expected exactly one explicit World upgrade.");
            session.CommitEvent(((World)session.State!), Policy);
            rewritten = session.CommitState(Policy).RevisionAddress;
            session.CommitEvent(((World)session.State!), Policy);
            noChange = session.CommitState(Policy).RevisionAddress;
            ((World)session.State!).Value++;
            session.CommitEvent(((World)session.State!), Policy);
            changed = session.CommitState(Policy).RevisionAddress;
        }
        Inspect(directory, (store, schemas) => {
            CheckSingleWrite(store, rewritten, worldId, ObjectVersionKind.Base);
            Require(store.Read(noChange).LocalObjects.Count == 0, "Upgraded state failed NoChange.");
            CheckSingleWrite(store, changed, worldId, ObjectVersionKind.Delta);
            CheckHistorical(store, schemas, historical, worldId);
        });
        using (Repository repository = Repository.OpenExisting(directory, Models(), Options)) {
            using BranchCheckout session = repository.Checkout("main");
            CheckGraph(((World)session.State!), 1012, 1020);
            Require(AppCatalog.UpgradeCalls == 1, "Reopening current state repeated upgrade.");
        }
        Console.WriteLine("InlineLibraryUpgrade:True:DeletedInlineClr:True:HistoricalExact:True:OwnerBase:True:NoChangeThenDelta:True:ColdReopen:True");
    }
    private static void CheckHistorical(StateRevisionStore store, SchemaStore schemas, FrameAddress address, ObjectId worldId) {
        DecodedRevision decoded = RevisionDecoder.Read(store, schemas, address, Readers());
        Require(decoded.Objects.Count == 1, "Inline values must not have object rows.");
        AppCatalog.CheckHistorical(decoded.GetRequired(worldId), 11);
    }
    private static void CheckSingleWrite(StateRevisionStore store, FrameAddress address, ObjectId id, ObjectVersionKind kind) {
        var rows = store.Read(address).LocalObjects;
        Require(rows.Count == 1 && rows[0].ObjectId == id.Value && rows[0].Kind == kind,
            $"Expected only World {id.Value} to write {kind}; inline values must not acquire objects.");
    }
    private static void CheckGraph(World world, long value, long optional) => Require(world.IsValid(value, optional),
        "Current transitive private/readonly fields, Nullable or generic values changed.");
    private static void Inspect(string directory, Action<StateRevisionStore, SchemaStore> action) {
        using GraphResources resources = GraphResources.OpenReadOnlyExisting(directory);
        action(resources.States, resources.Schemas);
    }
    private static void Require(bool condition, string message) {
        if (!condition) throw new InvalidOperationException(message);
    }
}
