using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace StateStorePackageConsumerProbe;

[DurableType("package.restore-world", 2)]
public sealed partial class World : IDurableObject {
    [DurableField(1)] private int _score;
    [DurableField(2)] private readonly string _name;
    [DurableField(3)] private readonly int _generation;
    [DurableField(4)] private readonly ulong _createdAtTicks;
    [Transient] private int _cache = 37;
    private static int _constructorCalls;
    private static int _upgradeCalls;

    public World(int score, string name) {
        _constructorCalls++;
        _score = score; _name = name; _generation = -1; _createdAtTicks = 638_625_600_000_000_000;
    }

    private static void UpgradeStateV1ToV2(in __DurableState.V1 old, out __DurableState.V2 next) {
        _upgradeCalls++;
        next = new(old.Segment0Field1 + 100, old.Segment0Field2, 73, old.Segment0Field4);
    }

    internal static void Exercise(string directory) {
        RbfSegmentStoreOptions options = new() { NewStoreLayout = RbfSegmentStoreLayout.Flat };
        FrameAddress oldRevision, upgradedRevision, unchangedRevision, finalRevision;
        ObjectId worldId;
        StateModelRegistry models = new();
        __DurableState.RegisterModel(models);
        __DurableState.RegisterModel(models);
        ReadAmplificationBaseBudgetParameters policy = new(int.MaxValue, 1);
        using (var repository = EventHistoryRepository.OpenExisting(directory, models, options))
        using (var session = repository.Resume<World>("main")) {
            oldRevision = session.StateRevisionAddress;
            worldId = session.StateId;
            Require(_upgradeCalls == 1 && session.State._score == 109 && session.State._name == "A" &&
                session.State._generation == 73 && session.State._createdAtTicks == 638_625_600_000_000_000 &&
                session.State._cache == 0 && _constructorCalls == 0, "Upgrade/readonly/constructor-free restoration failed.");
            World original = session.State;
            World marker = new(0, "Event");
            session.CommitDomainEvent(marker, policy);
            upgradedRevision = session.CommitDomainState(policy).RevisionAddress;
            Require(ReferenceEquals(session.State, original) && _upgradeCalls == 1, "Saving rebuilt or re-upgraded State.");
            session.CommitDomainEvent(marker, policy);
            unchangedRevision = session.CommitDomainState(policy).RevisionAddress;
            session.CommitDomainEvent(marker, policy);
            original._score = 110;
            finalRevision = session.CommitDomainState(policy).RevisionAddress;
        }
        using (var repository = EventHistoryRepository.OpenReadOnlyExisting(directory, models, options)) {
            World final = repository.ReadState<World>(repository.GetHead("main"));
            Require(final._score == 110 && final._generation == 73 && final._name == "A" &&
                final._createdAtTicks == 638_625_600_000_000_000 && final._cache == 0 &&
                _constructorCalls == 1 && _upgradeCalls == 1, "Cold reopening failed to restore current Base plus Delta.");
            var historical = repository.ReadFrames("main").Single(frame => frame.RevisionAddress == oldRevision);
            Require(repository.ReadState<World>(historical)._score == 109 && _upgradeCalls == 2,
                "Original historical revision was not preserved.");
        }
        using SegmentStore segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory, "state"), options);
        using StateRevisionStore store = new(segments);
        Require(store.ReadObjectVersionChain(oldRevision, worldId.Value).Records.Count == 3, "Old Delta chain missing.");
        StateRevision upgraded = store.Read(upgradedRevision);
        Require(upgraded.ParentRevisionAddress == oldRevision && upgraded.LocalObjects.Count == 1 &&
            upgraded.LocalObjects[0].Kind == ObjectVersionKind.Base, "Upgrade must force a current Base even after Event save.");
        Require(store.Read(unchangedRevision).LocalObjects.Count == 0, "Unchanged State requires no object write.");
        Require(store.Read(finalRevision).LocalObjects.Single().Kind == ObjectVersionKind.Delta &&
            store.ReadObjectVersionChain(finalRevision, worldId.Value).Records.Count == 2, "Expected ordinary Delta after forced Base.");
    }

    private static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
