using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;

namespace TagPackageConsumerProbe;

[DurableType("TagState", 1)]
public sealed partial class TagState : IDurableObject {
    [DurableField(1)] public int Value;
    [DurableField(2)] public TagState? Self;
}

[DurableType("TagEvent", 1)]
public sealed partial class TagEvent : IDurableObject {
    [DurableField(1)] public int Code;
}

internal static class Program {
    private static StateModelRegistry Models() {
        StateModelRegistry models = new();
        models.Register(Atelia.DurableGraph.Generated.Family_5461675374617465.Definition);
        models.Register(Atelia.DurableGraph.Generated.Family_5461674576656E74.Definition);
        return models;
    }

    private static void Main(string[] args) {
        if (args.Length != 2) { throw new ArgumentException("Expected seed|metadata|continue|verify and database path."); }
        string path = Path.GetFullPath(args[1]);
        switch (args[0]) {
            case "seed": Seed(path); break;
            case "metadata": Metadata(path); break;
            case "continue": Continue(path); break;
            case "verify": Verify(path); break;
            default: throw new ArgumentException("Unknown lane.");
        }
    }

    private static void Seed(string path) {
        using var repository = Repository.CreateNew(path, Models());
        using var checkout = repository.CreateBranchFromEvent("main", new TagEvent { Code = 7 });
        CheckpointAddress firstEvent = checkout.Head;
        repository.CreateTag("event-start", firstEvent);
        checkout.CommitEvent(new TagEvent { Code = 8 });
        TagState state = new() { Value = 10 };
        state.Self = state;
        CheckpointAddress firstState = checkout.CommitState(state);
        repository.CreateTag("state-baseline", firstState);
        state.Value = 11;
        checkout.CommitState();
        Require(repository.ResolveTag("event-start") == firstEvent &&
            repository.ResolveTag("state-baseline") == firstState, "Branch advancement moved a tag.");
        Console.WriteLine("TagsSeed:EventFirst:State:AdvancedBranch");
    }

    private static void Metadata(string path) {
        using var repository = Repository.OpenReadOnlyExisting(path, new StateModelRegistry());
        CheckpointAddress firstEvent = repository.ResolveTag("event-start");
        CheckpointAddress firstState = repository.ResolveTag("state-baseline");
        Require(firstEvent.Kind == GraphFrameKind.Event && firstState.Kind == GraphFrameKind.State &&
            firstEvent.RootId.Value != 0 && firstState.RootId.Value != 0, "Tag metadata was not resolved without models.");
        Require(repository.GetPreviousState(firstEvent) is null, "Event-first tag unexpectedly has a State.");
        Require(repository.ResolveTag("event-start") == firstEvent, "Repeated tag resolution changed its position.");
        Console.WriteLine("TagsMetadata:EmptyRegistry:ReadOnly:EventFirst");
    }

    private static void Continue(string path) {
        CheckpointAddress oldOwner;
        using (var prior = Repository.OpenReadOnlyExisting(path, new StateModelRegistry())) {
            oldOwner = prior.ResolveTag("state-baseline");
        }
        using var repository = Repository.OpenExisting(path, Models());
        CheckpointAddress firstEvent = repository.ResolveTag("event-start");
        CheckpointAddress firstState = repository.ResolveTag("state-baseline");
        Require(oldOwner != firstState, "A reopened tag returned a stale owner handle.");
        bool rejected = false;
        try { repository.CreateTag("old-owner", oldOwner); }
        catch (ArgumentException) { rejected = true; }
        Require(rejected && !repository.IsFaulted, "Creating a tag accepted an old-owner address or faulted the repository.");
        Require(((TagEvent)repository.ReadEvent(firstEvent)).Code == 7, "Tagged Event changed after reopening.");
        CheckState((TagState)repository.ReadState(firstState), 10);

        repository.CreateBranch("ref-state", firstState);
        using (var byRef = repository.Checkout("ref-state")) {
            CheckState((TagState)byRef.State!, 10);
            ((TagState)byRef.State!).Value = 21;
            byRef.CommitState();
        }
        using (var fork = repository.Fork("fork-state", firstState)) {
            CheckState((TagState)fork.State!, 10);
            ((TagState)fork.State!).Value = 12;
            fork.CommitState();
        }
        repository.CreateBranch("ref-event", firstEvent);
        using (var byRef = repository.Checkout("ref-event")) {
            Require(byRef.State is null && byRef.Head == firstEvent, "Event tag ref did not retain its exact State-free head.");
            byRef.CommitState(new TagState { Value = 40 });
        }
        using (var fork = repository.Fork("fork-event", firstEvent)) {
            Require(fork.State is null && fork.Head == firstEvent, "Event tag Fork imported a later State.");
            fork.CommitState(new TagState { Value = 30 });
        }
        CheckpointAddress advanced;
        using (var source = repository.Checkout("main")) {
            ((TagState)source.State!).Value = 99;
            advanced = source.CommitState();
        }
        repository.MoveBranch("main", advanced, firstEvent);
        Require(repository.ResolveTag("event-start") == firstEvent &&
            repository.ResolveTag("state-baseline") == firstState, "Moving the source branch moved an immutable tag.");
        CheckState((TagState)repository.ReadState(firstState), 10);
        Console.WriteLine("TagsContinue:RefCheckout:NamedFork:EventFirstState:OldOwnerRejected:MoveStable");
    }

    private static void Verify(string path) {
        using var repository = Repository.OpenReadOnlyExisting(path, Models());
        CheckpointAddress firstEvent = repository.ResolveTag("event-start");
        Require(repository.GetHead("main") == firstEvent && ((TagEvent)repository.ReadEvent(firstEvent)).Code == 7,
            "Moved source branch or Event tag changed on cold reopen.");
        CheckState((TagState)repository.ReadState(repository.ResolveTag("state-baseline")), 10);
        CheckState((TagState)repository.ReadState(repository.GetHead("ref-state")), 21);
        CheckState((TagState)repository.ReadState(repository.GetHead("fork-state")), 12);
        Require(((TagState)repository.ReadState(repository.GetHead("ref-event"))).Value == 40 &&
            ((TagState)repository.ReadState(repository.GetHead("fork-event"))).Value == 30,
            "First States continued from the Event tag did not survive reopening.");
        Console.WriteLine("TagsVerify:ColdValues:Cycles:ImmutableTags");
    }

    private static void CheckState(TagState state, int expected) =>
        Require(state.Value == expected && ReferenceEquals(state, state.Self), "Tagged State lost its value or self-cycle.");

    private static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidDataException(message); }
    }
}
