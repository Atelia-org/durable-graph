using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace StateStorePackageConsumerProbe;

// This first build publishes real package-generated history and seeds V1 state for the V2 process.
[DurableType("package.restore-world", 1)]
public sealed partial class World : IDurableObject {
    [DurableField(1)] private int _score;
    [DurableField(2)] private readonly string _name;
    [DurableField(4)] private readonly ulong _createdAtTicks;

    public World(int score, string name) { _score = score; _name = name; _createdAtTicks = 638_625_600_000_000_000; }

    internal static void Seed(string directory) {
        const long options = 1024 * 1024;
        ReadAmplificationBaseBudgetParameters policy = new(int.MaxValue, 1);
        StateModelRegistry models = new();
        __DurableState.RegisterModel(models);
        FrameAddress revision;
        ObjectId worldId;
        using (var repository = Repository.CreateNew(directory, models, options))
        using (var session = repository.CreateBranch("main", new World(7, "A"), policy)) {
            worldId = session.StateId!.Value;
            session.CommitEvent(new World(((World)session.State!)._score, "A"), policy);
            ((World)session.State!)._score = 8;
            session.CommitState(policy);
            session.CommitEvent(new World(((World)session.State!)._score, "A"), policy);
            ((World)session.State!)._score = 9;
            revision = session.CommitState(policy).RevisionAddress;
        }
        using GraphResources resources = GraphResources.OpenReadOnlyExisting(directory);
        StateRevisionStore store = resources.States;
        StateReaderRegistry readers = new();
        __DurableState.RegisterReaders(readers);
        var exact = RevisionDecoder.Read(store, resources.Schemas, revision, readers)
            .GetRequired(worldId).GetState<__DurableState.V1>();
        Require(exact.Segment0Field1 == 9 && exact.Segment0Field4 == 638_625_600_000_000_000 &&
            store.Read(revision).ParentRevisionAddress is not null,
            "The V1 process did not preserve its exact current values and State ancestry.");
    }

    private static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
