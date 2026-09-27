using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace EventHistoryPackageConsumerProbe;

internal static class Program {
    private static readonly RbfSegmentStoreOptions Options = new() { NewStoreLayout = RbfSegmentStoreLayout.Flat };
    private static readonly ReadAmplificationBaseBudgetParameters Policy = new(int.MaxValue, 1);

    private static StateModelRegistry Models(bool eventOnly = false) {
        StateModelRegistry models = new();
        models.Register(Atelia.DurableGraph.Generated.Family_416C696365.Definition);
        models.Register(Atelia.DurableGraph.Generated.Family_4F62736572766564.Definition);
        if (!eventOnly) {
            models.Register(Atelia.DurableGraph.Generated.Family_576F726C64.Definition);
            models.Register(Atelia.DurableGraph.Generated.Family_426F62.Definition);
        }
        return models;
    }

    private static void Main(string[] args) {
        string directory = Path.GetFullPath(args.Single());
#if HISTORY_V1
        Alice alice = new() { Score = 1, Labels = ["shared", "shared"] };
        alice.Self = alice;
        World world = new() { Alice = alice, Bob = new() { Score = 99 } };
        using (var repository = EventHistoryRepository.CreateNew(directory, Models(), Options)) {
            using var session = repository.CreateBranch("main", world, Policy);
            Require(ReferenceEquals(session.State, world), "S0 replaced the live State.");
            alice.Score = 2;
            session.CommitDomainEvent(new Observed(alice), Policy);
            alice.Score = 3;
            session.CommitDomainState(Policy);
            alice.Score = 4;
            session.CommitDomainEvent(new Observed(alice), Policy);
            Require(ReferenceEquals(session.State, world) && world.Cache == 42, "Hot commit replaced domain instances.");
        }
        using (var repository = EventHistoryRepository.OpenReadOnlyExisting(directory, Models(eventOnly: true), Options)) {
            GraphFrame[] frames = repository.ReadFrames("main").ToArray();
            Require(frames.Length == 4, "Expected S0 E1 S1 E2.");
            Observed e = repository.ReadEvent<Observed>(frames[1]);
            Require(e.Target.Score == 2 && ReferenceEquals(e.Target, e.Alias), "E1 lost its recorded value or sharing.");
        }
        using (var repository = EventHistoryRepository.OpenReadOnlyExisting(directory, Models(), Options)) {
            Require(repository.ReadState<World>(repository.ReadFrames("main")[2]).Alice.Score == 3, "S1 lost its recorded value.");
        }
        Require(!File.Exists(Path.Combine(directory, "publication.rbf")), "Legacy publisher was created.");
        SharedReadProbe.Seed(directory + "-shared-read");
        Console.WriteLine("EventHistorySeed:True:Siblings:True:PendingEvent:True:IndependentEvent:True:SharedReadSeed:True");
#else
        var before = SnapshotFiles(directory);
        using (var repository = EventHistoryRepository.OpenReadOnlyExisting(directory, Models(eventOnly: true), Options)) {
            GraphFrame pending = repository.GetHead("main");
            Observed e = repository.ReadEvent<Observed>(pending);
            Require(World.UpgradeCalls == 0 && e.Target.Score == 1004, "Event-only read needed World capabilities.");
            CheckEvent(e);
        }
        using (var repository = EventHistoryRepository.OpenReadOnlyExisting(directory, Models(), Options)) {
            GraphFrame pending = repository.GetHead("main");
            var pair = repository.ReadPair<Observed, World>(pending, repository.GetPreviousState(pending));
            Require(pair.First.Target.Score == 1004 && pair.Second.Alice.Score == 1003 && pair.Second.Generation == 2,
                "Pair did not retain each selected Revision's values.");
            CheckEvent(pair.First);
        }
        Require(before.SequenceEqual(SnapshotFiles(directory)), "Readonly browsing changed repository files.");
        Alice.UpgradeCalls = World.UpgradeCalls = 0;
        FrameAddress rewritten, unchanged, delta, replacement;
        ObjectId originalRoot;
        using (var repository = EventHistoryRepository.OpenExisting(directory, Models(), Options)) {
            using var session = repository.Resume<World>("main");
            Observed pending = (Observed)session.PendingEvent!;
            Require(session.State.Alice.Score == 1003 && pending.Target.Score == 1004 && World.UpgradeCalls == 1 && Alice.UpgradeCalls == 2,
                "Resume(E) must load the preceding State and pending Event independently.");
            Require(!ReferenceEquals(session.State.Alice, pending.Target), "Writable Resume introduced a mutable cross-view alias.");
            originalRoot = session.StateId;
            World state = session.State;
            Require(state.Alice.CreatedAtTicks == 638_931_456_000_000_000, "State Upgrade changed stable creation timestamp.");
            state.Alice.Score = 1005;
            rewritten = session.CommitDomainState(Policy).RevisionAddress;
            Require(session.PendingEvent is null && ReferenceEquals(session.State, state), "State publication did not install original candidate.");
            session.CommitDomainEvent(new Observed(state.Alice), Policy);
            unchanged = session.CommitDomainState(Policy).RevisionAddress;
            state.Alice.Score = 1006;
            session.CommitDomainEvent(new Observed(state.Alice), Policy);
            delta = session.CommitDomainState(Policy).RevisionAddress;
            session.CommitDomainEvent(new Observed(state.Alice), Policy);
            World next = new() { Alice = state.Alice, Bob = state.Bob, Generation = 2 };
            replacement = session.CommitDomainState(next, Policy).RevisionAddress;
            Require(ReferenceEquals(session.State, next) && session.StateId != originalRoot, "Replacement root was not installed.");
        }
        using (var file = RbfFile.OpenReadOnlyExisting(Path.Combine(directory, "schemas.rbf")))
        using (SegmentStore segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory, "state"), Options)) {
            using StateRevisionStore store = new(segments);
            StateRevision rewrite = store.Read(rewritten);
            Require(rewrite.LocalObjects.Count == 2 && rewrite.LocalObjects.All(row => row.Kind == ObjectVersionKind.Base),
                "Upgraded World and Alice require Base even after pending Event restoration.");
            Require(store.Read(unchanged).LocalObjects.Count == 0, "Installed upgraded State was not NoChange.");
            StateRevision edited = store.Read(delta);
            Require(edited.LocalObjects.Count == 1 && edited.LocalObjects[0].Kind == ObjectVersionKind.Delta, "Ordinary subsequent edit must use Delta.");
            Require(store.Read(replacement).RemovedObjectIds.Contains(originalRoot.Value), "Root replacement did not remove the old World.");
        }
        using (var repository = EventHistoryRepository.OpenReadOnlyExisting(directory, Models(), Options)) {
            GraphFrame[] frames = repository.ReadFrames("main").ToArray();
            var pair = repository.ReadPair<Observed, World>(frames[3], repository.GetHead("main"));
            Require(pair.First.Target.Score == 1004 && pair.Second.Alice.Score == 1006, "Historical pending Event changed after later State commits.");
        }
        // Record observable closure size and physical bytes, not a claim about physical read I/O.
        long bytes = Directory.EnumerateFiles(directory, "*.rbf", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
        File.WriteAllText(Path.Combine(directory, "metrics.txt"), $"TotalRbfBytes={bytes}\nForcedBaseObjectWrites=2\nNoChangeObjectWrites=0\nDeltaObjectWrites=1\n");
        SharedReadProbe.Verify(directory + "-shared-read");
        Console.WriteLine("EventHistoryUpgrade:True:EventOnlyCatalog:True:ReadPair:True:PendingResume:True:ForcedBaseThenDelta:True:RootReplacement:True:Readonly:True:SharedRead:True");
#endif
    }

    private static void CheckEvent(Observed e) {
        Require(ReferenceEquals(e.Target, e.Alias) && ReferenceEquals(e.Target, e.Target.Self) &&
            ReferenceEquals(e.Target.Labels[0], e.Target.Labels[1]) && e.Target.CreatedAtTicks == 638_931_456_000_000_000,
            "Event sharing, cycles or stable creation timestamp were lost.");
    }

    private static string[] SnapshotFiles(string directory) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => path + ":" + File.GetLastWriteTimeUtc(path).Ticks + ":" +
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

    private static void Require(bool condition, string message) {
        if (!condition) throw new InvalidOperationException(message);
    }
}
