using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;

namespace EventHistoryRecovery;

internal static class Program {
    private static StateModelRegistry Models(bool eventOnly = false) {
        StateModelRegistry models = new();
        if (!eventOnly) {
            Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
        } else {
            // Family suffixes are the Schema IDs encoded as UTF-8 hex, as in the root README.
            models.Register(Atelia.DurableGraph.Generated.Family_5265636F7665727944616D6167654576656E74.Definition);
            models.Register(Atelia.DurableGraph.Generated.Family_5265636F766572794163746F72536E617073686F74.Definition);
        }
        return models;
    }

    private static int Main(string[] args) {
        if (args.Length != 2) {
            Console.Error.WriteLine("Usage: EventHistoryRecoveryConsumer <init-record|recover|hot|next|verify> <directory>");
            return 2;
        }
        string mode = args[0], directory = Path.GetFullPath(args[1]);
        if (mode is not ("init-record" or "recover" or "hot" or "next" or "verify")) { return 2; }
        // The catch is inside using: report health, then leave and dispose this attempt.
        try {
            using var repository = mode is "init-record" or "hot"
                ? Repository.CreateNew(directory, Models())
                : mode == "verify" ? Repository.OpenReadOnlyExisting(directory, Models(eventOnly: true))
                : Repository.OpenExisting(directory, Models());
            try {
                if (mode == "verify") {
                    // This catalog intentionally excludes World and Character.
                    var events = repository.ReadEvents("main");
                    DamageEvent first = ((DamageEvent)repository.ReadEvent(events.First()));
                    CheckSnapshot(first, 10);
                    Require(events.Count() is 1 or 2, "Unexpected event count.");
                    Console.WriteLine("Verified:EventOnly:Hp=10:Observations=ready,armed");
                    return 0;
                }
                using var session = mode is "init-record" or "hot"
                    ? repository.CreateBranch("main", new World())
                    : repository.Checkout("main");
                if (mode is "init-record" or "hot" or "next") {
                    Require(session.Head.Kind == GraphFrameKind.State, "The application protocol requires completing its previous Event before accepting new work.");
                    int expectedHp = mode == "next" ? 7 : 10;
                    Require(((World)session.State!).Actor.Hp == expectedHp, "Unexpected initial State.");
                    var pending = new DamageEvent(((World)session.State!).Actor, 3);
                    session.CommitEvent(pending);
                    // A readonly array field alone does not freeze array elements. The snapshot
                    // owns its array and never exposes it. Strings can safely be shared.
                    EditObservations(((World)session.State!).Actor);
                    CheckSnapshot(pending, expectedHp);
                    if (mode == "init-record") {
                        Console.WriteLine("Recorded:Pending:StateHp=10:EventHp=10");
                        return 0;
                    }
                }
                bool completed = PendingRecovery.Complete<World>(repository, session, world => world.RebuildTransient(), Apply);
                int hp = ((World)session.State!).Actor.Hp;
                Require(hp == (mode == "next" ? 4 : 7), "Recovery used a stale State or replayed an already completed event.");
                Require(((World)session.State!).Actor.Observations.SequenceEqual(new[] { "changed", "later" }), "State observations differ between hot and cold processing.");
                Console.WriteLine($"Completed={completed}:StateHp={hp}:Pending={session.Head.Kind == GraphFrameKind.Event}");
                return 0;
            }
            catch (Exception error) {
                Console.Error.WriteLine(error);
                Console.Error.WriteLine($"Outcome={(error is GraphCommitException commit ? commit.Outcome.ToString() : "unclassified")}; IsFaulted={repository.IsFaulted}");
                return 1; // No automatic retry, rollback, deletion, or replacement Event.
            }
        }
        catch (Exception error) {
            Console.Error.WriteLine(error); // Open itself failed: no repository health is available.
            return 1;
        }
    }

    private static void Apply(World state, IDurableObject pending) {
        if (pending is not DamageEvent damage) { throw new InvalidOperationException("Unsupported pending event."); }
        Character actor = state.Find(damage.TargetSnapshot.ActorId);
        Require(actor.Hp == damage.TargetSnapshot.Hp, "Unexpected event basis.");
        actor.Hp -= damage.Amount; // Pure in-memory business operation; no external side effects.
        EditObservations(actor); // Also exercise snapshot isolation after cold Resume.
        CheckSnapshot(damage, actor.Hp + damage.Amount);
    }

    private static void EditObservations(Character actor) {
        actor.Observations.Add("later");
        actor.Observations.RemoveAt(0);
        actor.Observations[0] = "changed";
    }

    private static void CheckSnapshot(DamageEvent damage, int hp) {
        var snapshot = damage.TargetSnapshot;
        Require(snapshot.ActorId == "hero" && snapshot.Hp == hp, "Snapshot values changed.");
        // The second command captures already-edited observations, while E1 always keeps these originals.
        if (hp == 10) {
            Require(snapshot.ObservationCount == 2 &&
                snapshot.GetObservation(0) == "ready" && snapshot.GetObservation(1) == "armed",
                "Source collection edits changed E1's observation snapshot.");
        }
    }

    private static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
