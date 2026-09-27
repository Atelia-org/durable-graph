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
        using (var repository = Repository.CreateNew(directory, Models(), Options)) {
            using var session = repository.CreateBranch("main", world, Policy);
            Require(ReferenceEquals(((World)session.State!), world), "S0 replaced the live State.");
            alice.Score = 2;
            session.CommitEvent(new Observed(alice), Policy);
            alice.Score = 3;
            session.CommitState(Policy);
            alice.Score = 4;
            session.CommitEvent(new Observed(alice), Policy);
            Require(ReferenceEquals(((World)session.State!), world) && world.Cache == 42, "Hot commit replaced domain instances.");
        }
        using (var repository = Repository.OpenReadOnlyExisting(directory, Models(eventOnly: true), Options)) {
            CheckpointAddress[] frames = repository.ReadFrames("main").ToArray();
            Require(frames.Length == 4, "Expected S0 E1 S1 E2.");
            Observed e = ((Observed)repository.ReadEvent(frames[1]));
            Require(e.Target.Score == 2 && ReferenceEquals(e.Target, e.Alias), "E1 lost its recorded value or sharing.");
        }
        using (var repository = Repository.OpenReadOnlyExisting(directory, Models(), Options)) {
            Require(((World)repository.ReadState(repository.ReadFrames("main")[2])).Alice.Score == 3, "S1 lost its recorded value.");
        }
        Require(!File.Exists(Path.Combine(directory, "publication.rbf")), "Legacy publisher was created.");
        SharedReadProbe.Seed(directory + "-shared-read");
        Console.WriteLine("EventHistorySeed:True:Siblings:True:EventHead:True:IndependentEvent:True:SharedReadSeed:True");
#else
        var before = SnapshotFiles(directory);
        using (var repository = Repository.OpenReadOnlyExisting(directory, Models(eventOnly: true), Options)) {
            CheckpointAddress pending = repository.GetHead("main");
            Observed e = ((Observed)repository.ReadEvent(pending));
            Require(World.UpgradeCalls == 0 && e.Target.Score == 1004, "Event-only read needed World capabilities.");
            CheckEvent(e);
        }
        using (var repository = Repository.OpenReadOnlyExisting(directory, Models(), Options)) {
            CheckpointAddress pending = repository.GetHead("main");
            var pair = repository.ReadPair(pending, repository.GetPreviousState(pending)!);
            Require(((Observed)pair.First).Target.Score == 1004 && ((World)pair.Second).Alice.Score == 1003 && ((World)pair.Second).Generation == 2,
                "Pair did not retain each selected Revision's values.");
            CheckEvent(((Observed)pair.First));
        }
        Require(before.SequenceEqual(SnapshotFiles(directory)), "Readonly browsing changed repository files.");
        Alice.UpgradeCalls = World.UpgradeCalls = 0;
        FrameAddress rewritten, unchanged, delta, replacement;
        ObjectId originalRoot;
        using (var repository = Repository.OpenExisting(directory, Models(), Options)) {
            using var session = repository.Checkout("main");
            Observed pending = (Observed)repository.ReadEvent(session.Head)!;
            Require(((World)session.State!).Alice.Score == 1003 && pending.Target.Score == 1004 && World.UpgradeCalls == 1 && Alice.UpgradeCalls == 2,
                "Checkout and explicit ReadEvent must restore independently.");
            Require(!ReferenceEquals(((World)session.State!).Alice, pending.Target), "Checkout plus explicit ReadEvent introduced a mutable cross-view alias.");
            originalRoot = session.StateId!.Value;
            World state = ((World)session.State!);
            Require(state.Alice.CreatedAtTicks == 638_931_456_000_000_000, "State Upgrade changed stable creation timestamp.");
            state.Alice.Score = 1005;
            rewritten = session.CommitState(Policy).RevisionAddress;
            Require(session.Head.Kind == GraphFrameKind.State && ReferenceEquals(((World)session.State!), state), "State publication did not install original candidate.");
            session.CommitEvent(new Observed(state.Alice), Policy);
            unchanged = session.CommitState(Policy).RevisionAddress;
            state.Alice.Score = 1006;
            session.CommitEvent(new Observed(state.Alice), Policy);
            delta = session.CommitState(Policy).RevisionAddress;
            session.CommitEvent(new Observed(state.Alice), Policy);
            World next = new() { Alice = state.Alice, Bob = state.Bob, Generation = 2 };
            replacement = session.CommitState(next, Policy).RevisionAddress;
            Require(ReferenceEquals(((World)session.State!), next) && session.StateId!.Value != originalRoot, "Replacement root was not installed.");
        }
        using (var file = RbfFile.OpenReadOnlyExisting(Path.Combine(directory, "schemas.rbf")))
        using (SegmentStore segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory, "state"), Options)) {
            using StateRevisionStore store = new(segments);
            StateRevision rewrite = store.Read(rewritten);
            Require(rewrite.LocalObjects.Count == 2 && rewrite.LocalObjects.All(row => row.Kind == ObjectVersionKind.Base),
                "Upgraded World and Alice require Base even after explicit Event reading.");
            Require(store.Read(unchanged).LocalObjects.Count == 0, "Installed upgraded State was not NoChange.");
            StateRevision edited = store.Read(delta);
            Require(edited.LocalObjects.Count == 1 && edited.LocalObjects[0].Kind == ObjectVersionKind.Delta, "Ordinary subsequent edit must use Delta.");
            Require(store.Read(replacement).RemovedObjectIds.Contains(originalRoot.Value), "Root replacement did not remove the old World.");
        }
        using (var repository = Repository.OpenReadOnlyExisting(directory, Models(), Options)) {
            CheckpointAddress[] frames = repository.ReadFrames("main").ToArray();
            var pair = repository.ReadPair(frames[3], repository.GetHead("main"));
            Require(((Observed)pair.First).Target.Score == 1004 && ((World)pair.Second).Alice.Score == 1006, "Historical Event changed after later State commits.");
        }
        // Record observable closure size and physical bytes, not a claim about physical read I/O.
        long bytes = Directory.EnumerateFiles(directory, "*.rbf", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
        File.WriteAllText(Path.Combine(directory, "metrics.txt"), $"TotalRbfBytes={bytes}\nForcedBaseObjectWrites=2\nNoChangeObjectWrites=0\nDeltaObjectWrites=1\n");
        SharedReadProbe.Verify(directory + "-shared-read");
        VerifyFreeHistory(directory + "-free-history");
        VerifyCheckpointHistory(directory + "-checkpoint-history");
        VerifyNamedFork(directory + "-named-fork");
        Console.WriteLine("EventHistoryUpgrade:True:EventOnlyCatalog:True:ReadPair:True:ExplicitEventRead:True:ForcedBaseThenDelta:True:RootReplacement:True:Readonly:True:SharedRead:True:EventFirstFreeHistory:True:IndependentCheckpoint:True:FixedHistoryQuery:True:NamedFork:True:PerBranchCheckout:True");
#endif
    }

    // These helpers have no knowledge of World, Alice, Bob, or a shared business base.
    private static BranchCheckout Begin(Repository repository, string name, IDurableObject fact) =>
        repository.CreateBranchFromEvent(name, fact, Policy);
    private static CheckpointAddress Record(BranchCheckout checkout, IDurableObject fact) => checkout.CommitEvent(fact, Policy);
    private static CheckpointAddress Save(BranchCheckout checkout, IDurableObject state) => checkout.CommitState(state, Policy);

    private static void VerifyFreeHistory(string directory) {
        using (var repository = Repository.CreateNew(directory, Models(), Options)) {
            using var checkout = Begin(repository, "free", new Bob { Score = 11 });
            Record(checkout, new Bob { Score = 12 });
            Require(checkout.State is null && checkout.StateId is null && checkout.StateRevisionAddress is null,
                "E/E history invented a State.");
            var checkpoint = (EventCheckpoint)repository.ReadCheckpoint(checkout.Head);
            Require(checkpoint.Event is Bob { Score: 12 } && checkpoint.PreviousState is null && checkpoint.PreviousStateAddress is null,
                "Event-only Checkpoint invented a PreviousState.");
        }
        // An Event-only checkout restores no Event graph and needs no application model capability.
        using (var repository = Repository.OpenExisting(directory, new StateModelRegistry(), Options)) {
            using var checkout = repository.Checkout("free");
            Require(checkout.State is null && repository.GetPreviousState(checkout.Head) is null,
                "Cold Event-only checkout did not preserve absence of State.");
        }
        ObjectId aliceId;
        FrameAddress firstState, replacement, unchanged, laterEvent;
        using (var repository = Repository.OpenExisting(directory, Models(), Options)) {
            using var checkout = repository.Checkout("free");
            Alice alice = new() { Score = 30 };
            alice.Self = alice;
            World world = new() { Alice = alice, Bob = new Bob { Score = 40 } };
            firstState = Save(checkout, world).RevisionAddress;
            var firstCheckpoint = (StateCheckpoint)repository.ReadCheckpoint(checkout.Head);
            Require(firstCheckpoint.PreviousEvent is Bob { Score: 12 } &&
                firstCheckpoint.PreviousEventAddress == repository.EnumerateEvents(checkout.Head).First(),
                "First State lost its strictly preceding Event.");
            // The unrelated Alice domain type is already a reachable child, then becomes the root.
            replacement = Save(checkout, alice).RevisionAddress;
            aliceId = checkout.StateId!.Value;
            Require(ReferenceEquals(checkout.State, alice) && checkout.StateId == aliceId,
                "Cross-type promotion replaced the live child or its identity.");
            unchanged = checkout.CommitState(Policy).RevisionAddress;
            laterEvent = Record(checkout, new Bob { Score = 50 }).RevisionAddress;
            Require(ReferenceEquals(checkout.State, alice), "Event advanced the editable State.");
        }
        using (var segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory, "state"), Options)) {
            using StateRevisionStore store = new(segments);
            var firstHeads = store.ReadLiveObjectHeadMap(firstState);
            var replacementHeads = store.ReadLiveObjectHeadMap(replacement);
            Require(firstHeads.TryGetValue(aliceId.Value, out var originalHead) && replacementHeads[aliceId.Value] == originalHead,
                "Promoted child did not retain its original identity and unchanged object head.");
            Require(store.Read(firstState).ParentRevisionAddress is null, "First State inherited an Event baseline.");
            Require(store.Read(replacement).ParentRevisionAddress == firstState &&
                store.Read(unchanged).ParentRevisionAddress == replacement && store.Read(unchanged).LocalObjects.Count == 0 &&
                store.Read(laterEvent).ParentRevisionAddress == unchanged, "Free history lost nearest-State graph parents.");
        }
        using (var repository = Repository.OpenExisting(directory, Models(), Options)) {
            using var checkout = repository.Checkout("free");
            Require(checkout.State is Alice { Score: 30 } && checkout.StateId == aliceId,
                "Cold checkout lost the actual replacement root type or identity.");
            var frames = repository.ReadFrames("free");
            Require(frames.Select(frame => frame.Kind).SequenceEqual(new[] { GraphFrameKind.Event, GraphFrameKind.Event,
                GraphFrameKind.State, GraphFrameKind.State, GraphFrameKind.State, GraphFrameKind.Event }),
                "Journal did not preserve free Event/State order.");
            Require(repository.ReadEvent(frames[0]) is Bob { Score: 11 } && repository.ReadEvent(frames[1]) is Bob { Score: 12 },
                "Event-first values changed.");
            // The same Bob CLR type also serves as a State, with role chosen solely by the API.
            Save(checkout, new Bob { Score = 60 });
            checkout.CommitState(Policy);
        }
        using (var repository = Repository.OpenExisting(directory, Models(), Options)) {
            using var checkout = repository.Checkout("free");
            Require(checkout.State is Bob { Score: 60 }, "Same CLR Event/State role or cold replacement failed.");
        }
    }

    private static void VerifyCheckpointHistory(string directory) {
        using var repository = Repository.CreateNew(directory, Models(), Options);
        Alice alice = new() { Score = 10, Labels = ["stable"] };
        alice.Self = alice;
        World world = new() { Alice = alice, Bob = new() { Score = 20 } };
        CheckpointAddress s0, e1, s1, e2, s2;
        using (var checkout = repository.CreateBranch("main", world, Policy)) {
            s0 = checkout.Head;
            var initial = (StateCheckpoint)repository.ReadCheckpoint(s0);
            Require(initial.PreviousEvent is null && initial.PreviousEventAddress is null,
                "Initial State invented a PreviousEvent.");
            e1 = checkout.CommitEvent(new Observed(alice), Policy);
            s1 = checkout.CommitState(Policy);
            e2 = checkout.CommitEvent(new Observed(alice), Policy);
            s2 = checkout.CommitState(Policy);
            CheckpointAddress s3 = checkout.CommitState(Policy);

            var atEvent = (EventCheckpoint)repository.ReadCheckpoint(e2);
            var atState = (StateCheckpoint)repository.ReadCheckpoint(s3);
            Observed occurrence = (Observed)atEvent.Event;
            World previous = (World)atEvent.PreviousState!;
            World restored = (World)atState.State;
            Observed previousEvent = (Observed)atState.PreviousEvent!;
            Require(atEvent.Address == e2 && atEvent.PreviousStateAddress == s1 &&
                atState.Address == s3 && atState.PreviousEventAddress == e2,
                "Checkpoint selection did not follow nearest strict logical ancestors.");
            Require(ReferenceEquals(occurrence.Target, occurrence.Alias) && ReferenceEquals(occurrence.Target.Self, occurrence.Target),
                "Independent Checkpoint lost graph-local aliases or cycles.");
            previous.Alice.Score = 101;
            occurrence.Target.Score = 102;
            restored.Alice.Score = 103;
            previousEvent.Target.Score = 104;
            Require(previous.Alice.Score == 101 && occurrence.Target.Score == 102 &&
                restored.Alice.Score == 103 && previousEvent.Target.Score == 104 && alice.Score == 10,
                "Checkpoint graphs shared mutable objects with each other or the active checkout.");
            Require(ReferenceEquals(atEvent.Event, occurrence) && ReferenceEquals(atEvent.PreviousState, previous) &&
                ReferenceEquals(atState.State, restored) && ReferenceEquals(atState.PreviousEvent, previousEvent),
                "Repeated Checkpoint getters discarded local edits.");
            var fresh = (EventCheckpoint)repository.ReadCheckpoint(e2);
            Require(((Observed)fresh.Event).Target.Score == 10 && ((World)fresh.PreviousState!).Alice.Score == 10,
                "An independent Checkpoint read reused mutable results from a previous read.");

            // Each MoveNext releases the repository guard, so reads and commits are legal in the loop.
            var fixedQuery = repository.EnumerateEvents(s2);
            using (var cursor = fixedQuery.GetEnumerator()) {
                Require(cursor.MoveNext() && cursor.Current == e2 && repository.ReadEvent(cursor.Current) is Observed,
                    "Default history order was not newest first or held a guard across yield.");
                checkout.CommitEvent(new Observed(alice), Policy);
                Require(cursor.MoveNext() && cursor.Current == e1 && !cursor.MoveNext(),
                    "Advancing the branch changed an already selected history end.");
            }
            var values = new List<long>();
            foreach (CheckpointAddress address in repository.EnumerateEvents(s2, HistoryOrder.OldestFirst)) {
                values.Add(((Observed)repository.ReadEvent(address)).Target.Score);
            }
            Require(values.SequenceEqual(new long[] { 10, 10 }) &&
                repository.EnumerateEvents(s2, HistoryOrder.OldestFirst).SequenceEqual(new[] { e1, e2 }),
                "Chronological query failed to cross intervening States.");
            Require(repository.EnumerateEvents(s2, afterExclusive: s1).SequenceEqual(new[] { e2 }) &&
                !repository.EnumerateEvents(s2, afterExclusive: s2).Any(), "Exclusive lower bound selected the wrong range.");
        }
        var beforeMove = repository.EnumerateEvents(s2);
        repository.MoveBranch("main", repository.GetHead("main"), s0);
        Require(beforeMove.SequenceEqual(new[] { e2, e1 }), "Moving a branch changed a fixed history query.");
        CheckpointAddress unrelated;
        using (var other = repository.CreateBranchFromEvent("other", new Bob { Score = 99 }, Policy)) {
            unrelated = other.Head;
        }
        bool rejected = false;
        try {
            using var cursor = repository.EnumerateEvents(s2, afterExclusive: unrelated).GetEnumerator();
            cursor.MoveNext();
        } catch (ArgumentException) {
            rejected = true;
        }
        Require(rejected, "A non-ancestor lower bound was not rejected before the first result.");
    }

    private static void VerifyNamedFork(string directory) {
        using (var repository = Repository.CreateNew(directory, Models(), Options)) {
            Alice alice = new() { Score = 10, Labels = ["source"] };
            alice.Self = alice;
            using var source = repository.CreateBranch("main", new World { Alice = alice, Bob = new() { Score = 20 } }, Policy);
            CheckpointAddress selected = source.CommitEvent(new Observed(alice), Policy);
            alice.Score = 99; // This uncommitted source edit must not enter either child.
            using var left = repository.Fork("left", selected);
            using var right = repository.Fork("right", selected);
            World a = (World)left.State!;
            World b = (World)right.State!;
            Require(left.Head == selected && right.Head == selected && source.Head == selected &&
                a.Alice.Score == 10 && b.Alice.Score == 10 && alice.Score == 99,
                "Fork did not preserve the selected Event head and its committed State.");
            Require(ReferenceEquals(a.Alice.Self, a.Alice) && ReferenceEquals(b.Alice.Self, b.Alice) &&
                !ReferenceEquals(a, b) && !ReferenceEquals(a.Alice, b.Alice) &&
                !ReferenceEquals(a.Alice, alice) && !ReferenceEquals(b.Alice, alice) &&
                !ReferenceEquals(a.Alice.Labels, b.Alice.Labels), "Fork lost cycles or shared mutable graphs.");
            a.Alice.Labels.Add("left");
            Require(b.Alice.Labels.Count == 1 && alice.Labels.Count == 1, "Fork shared a mutable container.");

            // The application chooses how each branch handles the recorded occurrence.
            // A shared Event head does not claim or complete an external effect.
            left.CommitEvent(new Bob { Score = 1 }, Policy);
            right.CommitEvent(new Bob { Score = 2 }, Policy);
            left.CommitEvent(new Bob { Score = 3 }, Policy);
            right.CommitEvent(new Bob { Score = 4 }, Policy);
            a.Alice.Score = 11;
            b.Alice.Score = 21;
            left.CommitState(Policy);
            right.CommitState(Policy);
            a.Alice.Score = 12;
            b.Alice.Score = 22;
            left.CommitState(Policy);
            right.CommitState(Policy);
            Require(source.Head == selected && alice.Score == 99, "Child commits advanced the source workspace.");
            left.Dispose();
            using var reopenedLeft = repository.Checkout("left");
            Require(((World)reopenedLeft.State!).Alice.Score == 12 && ((World)right.State!).Alice.Score == 22,
                "Disposing one branch did not permit its checkout beside other live branches.");

            using var eventSource = repository.CreateBranchFromEvent("event-source", new Bob { Score = 50 }, Policy);
            CheckpointAddress prefix = eventSource.CommitEvent(new Bob { Score = 51 }, Policy);
            using var first = repository.Fork("event-left", prefix);
            using var second = repository.Fork("event-right", prefix);
            Require(first.Head == prefix && second.Head == prefix && first.State is null && second.State is null,
                "An Event-only fork invented a State or changed its head.");
            Alice promoted = new() { Score = 60 };
            promoted.Self = promoted;
            first.CommitState(new World { Alice = promoted, Bob = new() { Score = 70 } }, Policy);
            first.CommitState(promoted, Policy); // World -> Alice is an actual cross-type root replacement.
            promoted.Score = 61;
            first.CommitState(Policy);
            second.CommitState(new Bob { Score = 80 }, Policy);
            second.CommitState(Policy);
            Require(ReferenceEquals(first.State, promoted) && eventSource.State is null && second.State is Bob { Score: 80 },
                "Event-only children did not retain independent first-State and replacement baselines.");
        }
        using (var repository = Repository.OpenExisting(directory, Models(), Options)) {
            using var source = repository.Checkout("main");
            using var left = repository.Checkout("left");
            using var right = repository.Checkout("right");
            using var first = repository.Checkout("event-left");
            using var second = repository.Checkout("event-right");
            Require(source.Head.Kind == GraphFrameKind.Event && ((World)source.State!).Alice.Score == 10 &&
                ((World)left.State!).Alice.Score == 12 && ((World)right.State!).Alice.Score == 22 &&
                first.State is Alice { Score: 61 } && second.State is Bob { Score: 80 },
                "Cold multi-branch checkout lost committed values or actual root types.");
            GraphFrameKind[] kinds = [GraphFrameKind.State, GraphFrameKind.Event, GraphFrameKind.Event,
                GraphFrameKind.Event, GraphFrameKind.State, GraphFrameKind.State];
            Require(repository.ReadFrames("left").Select(frame => frame.Kind).SequenceEqual(kinds) &&
                repository.ReadFrames("right").Select(frame => frame.Kind).SequenceEqual(kinds),
                "Serial child E/E/S/S commits did not retain separate logical histories.");
            Require(((World)left.State!).Alice.Labels.Count == 2 && ((World)right.State!).Alice.Labels.Count == 1,
                "Cold child graphs lost independently edited containers.");
        }
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
