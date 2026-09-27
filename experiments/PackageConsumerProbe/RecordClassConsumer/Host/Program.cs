using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Storage;
using Atelia.RbfSegmentStore;
using RecordClassConsumer;
using RecordClassLibrary.Base;
using RecordClassLibrary.Facts;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

internal static class Program {
    private static readonly RbfSegmentStoreOptions Options = new() { NewStoreLayout = RbfSegmentStoreLayout.Flat };
    private static readonly ReadAmplificationBaseBudgetParameters DeltaPolicy = new(int.MaxValue, 1);

    private static StateModelRegistry Models(bool eventOnly = false) {
        var models = new StateModelRegistry();
        BaseCatalog.Register(models);
        FactCatalog.Register(models);
        if (!eventOnly) Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
        return models;
    }

    private static void Main(string[] args) {
        if (args.Length != 2) throw new ArgumentException("Expected seed|recover|check|with|upgrade|final|events and a directory.");
        Require(typeof(IDurableObject).Assembly.GetType("Atelia.DurableGraph.DurableBase") is null,
            "The package must remove the old framework base rather than retaining a compatibility shell.");
        string mode = args[0], directory = Path.GetFullPath(args[1]);
        if (mode == "events") {
            using (var eventRepository = Repository.OpenReadOnlyExisting(directory, Models(eventOnly: true), Options)) {
                var first = eventRepository.ReadEvent(eventRepository.EnumerateEvents(eventRepository.GetHead("main"), HistoryOrder.OldestFirst).First());
                Require(first is Damage<string> { Actor: "hero", Amount: 3 }, "Event-only catalog lost the exact record or its inherited field.");
            }
            using var repository = Repository.OpenReadOnlyExisting(directory, Models(), Options);
            var events = repository.EnumerateEvents(repository.GetHead("main"), HistoryOrder.OldestFirst).ToArray();
            var pair = repository.ReadPair(events.First(), repository.GetPreviousState(events.First())!);
            Require(pair.First is Damage<string> { Actor: "hero", Amount: 3 } && pair.Second is World { Hp: 10 },
                "Non-generic ReadPair must preserve the requested Event/State order and each root's actual type and content.");
            Console.WriteLine("RecordClass:EventOnly:True");
            return;
        }
        if (mode == "seed") {
            using var repository = Repository.CreateNew(directory, Models(), Options);
            using var session = repository.CreateBranch("main", new World());
            session.CommitEvent(new Damage<string>("hero", 3));
            Require(repository.ReadEvent(session.Head) is Damage<string> && ((World)session.State!).Hp == 10, "E1 must leave the State baseline unchanged.");
            Console.WriteLine("RecordClass:Seed:S0:E1:Pending:True");
            return;
        }
        if (mode == "upgrade") {
            Upgrade(directory);
            return;
        }
        if (mode == "final") {
            using var repository = Repository.OpenExisting(directory, Models(), Options);
            using var session = repository.Checkout("main");
            Require(session.Head.Kind == GraphFrameKind.State && ((World)session.State!).Hp == 7 &&
                ((World)session.State!).First.Counter == ((World)session.State!).Second.Counter + 1 && FactCatalog.UpgradeCalls == 0,
                "Current data lost its Delta or required Upgrade again in a fresh process.");
            Console.WriteLine("RecordClass:FinalProcess:NoUpgrade:True");
            return;
        }
        using (var repository = Repository.OpenExisting(directory, Models(), Options)) {
            using var session = repository.Checkout("main");
            CheckIdentity(((World)session.State!));
            if (mode == "recover") {
                Require(repository.ReadEvent(session.Head) is Damage<string> { Amount: 3 }, "Expected pending E1 from the earlier process.");
                Damage<string> damage = (Damage<string>)repository.ReadEvent(session.Head)!;
                ((World)session.State!).Hp -= checked((int)damage.Amount);
                ((World)session.State!).Last = damage;
                session.CommitState();
                Require(session.Head.Kind == GraphFrameKind.State && ((World)session.State!).Hp == 7, "S1 did not install.");
                Console.WriteLine("RecordClass:Resume:S1:Pending:False");
            } else if (mode == "check") {
                Require(session.Head.Kind == GraphFrameKind.State && ((World)session.State!).Hp == 7, "Completed E1 was replayed or State was lost.");
                Console.WriteLine("RecordClass:Reopen:NoReplay:True");
            } else if (mode == "with") {
#if HISTORY_V1
                throw new InvalidOperationException("The ordinary-class stage has no with syntax.");
#else
                var old = ((World)session.State!).First;
                var next = old with { };
                Require(next == old && !ReferenceEquals(next, old), "with must retain language equality but create a distinct instance.");
                Require(ReferenceEquals(next.Actor, old.Actor), "This with copy is shallow.");
                ((World)session.State!).First = next;
                // Keep the old instance reachable to prove its identity was not inherited by the copy.
                ((World)session.State!).Alias = old;
                session.CommitEvent(new Damage<string>("hero", 0));
                session.CommitState();
                Console.WriteLine("RecordClass:With:DistinctEqualIdentity:True");
#endif
            } else throw new ArgumentException("Unknown mode.");
        }
    }

    private static void CheckIdentity(World world) {
        Require(!ReferenceEquals(world.First, world.Second), "Two equal inputs were merged.");
        Require(world.First.Actor == "hero" && world.Second.Actor == "hero", "Generic ancestor storage was not hydrated.");
#if !HISTORY_V1
        Require(world.First == world.Second && world.First == world.Alias, "C# record value equality changed.");
#endif
        // Before with, Alias and First share one ID. Afterwards all three are separate,
        // equal objects; the independently restored Last is the completed event snapshot.
        if (world.Last is null) Require(ReferenceEquals(world.First, world.Alias), "Original alias was lost.");
        else Require(!ReferenceEquals(world.Second, world.Alias), "Equal original instances were merged after continuation.");
    }

    private static void Upgrade(string directory) {
#if !HISTORY_V3
        throw new InvalidOperationException("The schema-upgrade stage requires V3 sources (Damage schema v2).");
#else
        FrameAddress rewritten, changed;
        using (var repository = Repository.OpenExisting(directory, Models(), Options)) {
            using var session = repository.Checkout("main");
            CheckIdentity(((World)session.State!));
            Require(session.Head.Kind == GraphFrameKind.State && ((World)session.State!).Hp == 7 && FactCatalog.UpgradeCalls == 4,
                "Each of the four retained Damage instances must upgrade once; aliases must not duplicate work.");
            Require(!ReferenceEquals(((World)session.State!).First, ((World)session.State!).Alias), "with copy inherited the source identity.");
            session.CommitEvent(((World)session.State!).Last!);
            rewritten = session.CommitState(DeltaPolicy).RevisionAddress;
            ((World)session.State!).First.Counter++;
            session.CommitEvent(((World)session.State!).Last!);
            changed = session.CommitState(DeltaPolicy).RevisionAddress;
        }
        using (var segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory, "state"), Options)) {
            using var states = new StateRevisionStore(segments);
            var bases = states.Read(rewritten).LocalObjects;
            Require(bases.Count == 4 && bases.All(row => row.Kind == ObjectVersionKind.Base), "Upgraded Damage records must rewrite Base.");
            var deltas = states.Read(changed).LocalObjects;
            Require(deltas.Count == 1 && deltas[0].Kind == ObjectVersionKind.Delta, "A same-instance mutable record change must use the existing Delta path.");
        }
        using (var repository = Repository.OpenExisting(directory, Models(), Options)) {
            using var session = repository.Checkout("main");
            Require(session.Head.Kind == GraphFrameKind.State && ((World)session.State!).First.Counter == ((World)session.State!).Second.Counter + 1 &&
                FactCatalog.UpgradeCalls == 4, "Cold current-state reopen repeated Upgrade or lost the Delta.");
        }
        Console.WriteLine("RecordClass:Upgrade:BaseThenDelta:ColdReopen:True");
#endif
    }

    private static void Require(bool condition, string message) {
        if (!condition) throw new InvalidOperationException(message);
    }
}
