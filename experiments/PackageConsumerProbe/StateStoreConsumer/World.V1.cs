using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace StateStorePackageConsumerProbe;

// This first build publishes real package-generated history and seeds V1 state for the V2 process.
[DurableType("package.restore-world", 1)]
public sealed partial class World : IDurableObject {
    [DurableField(1)] private int _score;
    [DurableField(2)] private readonly string _name;
    [DurableField(4)] private readonly ulong _createdAtTicks;

    public World(int score, string name) { _score = score; _name = name; _createdAtTicks = 638_625_600_000_000_000; }

    internal static void Seed(string directory) {
        RbfSegmentStoreOptions options = new() { NewStoreLayout = RbfSegmentStoreLayout.Flat };
        ReadAmplificationBaseBudgetParameters policy = new(int.MaxValue, 1);
        StateModelRegistry models = new();
        __DurableState.RegisterModel(models);
        FrameAddress revision;
        ObjectId worldId;
        using (var repository = EventHistoryRepository.CreateNew(directory, models, options))
        using (var session = repository.CreateBranch("main", new World(7, "A"), policy)) {
            worldId = session.StateId;
            session.CommitDomainEvent(new World(session.State._score, "A"), policy);
            session.State._score = 8;
            session.CommitDomainState(policy);
            session.CommitDomainEvent(new World(session.State._score, "A"), policy);
            session.State._score = 9;
            revision = session.CommitDomainState(policy).RevisionAddress;
        }
        using SegmentStore segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory, "state"), options);
        using StateRevisionStore store = new(segments);
        Require(store.ReadObjectVersionChain(revision, worldId.Value).Records.Count == 3,
            "The V1 process did not persist its Base plus two Delta records.");
    }

    private static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
