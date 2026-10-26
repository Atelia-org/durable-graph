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
        Require(repository.ListTags().Length == 0, "A new repository contains tags.");
        BranchInfo main = repository.CreateRootBranchFromEvent("main", new TagEvent { Code = 7 });
        using var work = repository.OpenWorkingCopy(main.Id);
        CommitId firstEvent = work.BaseCommitId;
        repository.CreateTag("event-start", firstEvent);
        work.CommitEvent(new TagEvent { Code = 8 });
        TagState state = new() { Value = 10 };
        state.Self = state;
        CommitId firstState = work.CommitState(state).Id;
        repository.CreateTag("state-baseline", firstState);
        // A successful CommitState installs the supplied instance; later edits use the working State.
        ((TagState)work.State!).Value = 11;
        work.CommitState();
        Require(repository.ResolveTag("event-start") == firstEvent &&
            repository.ResolveTag("state-baseline") == firstState, "Branch advancement moved a tag.");
        Console.WriteLine("TagsSeed:EventFirst:State:AdvancedBranch");
    }

    private static void Metadata(string path) {
        using var repository = Repository.OpenReadOnlyExisting(path, new StateModelRegistry());
        CommitId firstEvent = repository.ResolveTag("event-start");
        CommitId firstState = repository.ResolveTag("state-baseline");
        TagInfo[] tags = repository.ListTags();
        Require(tags.SequenceEqual(new[] { new TagInfo("event-start", firstEvent), new TagInfo("state-baseline", firstState) }),
            "Tag listing did not preserve ordinal names and exact targets in a read-only empty-registry reopen.");
        Require(repository.ReadCommitInfo(firstEvent).Kind == CommitKind.Event &&
            repository.ReadCommitInfo(firstState).Kind == CommitKind.State,
            "Tag metadata was not resolved without models.");
        Require(repository.GetPreviousState(firstEvent) is null, "Event-first tag unexpectedly has a State.");
        Require(repository.ResolveTag("event-start") == firstEvent, "Repeated tag resolution changed its position.");
        Console.WriteLine("TagsMetadata:EmptyRegistry:ReadOnly:EventFirst");
    }

    private static void Continue(string path) {
        CommitId persisted = default;
        using (var prior = Repository.OpenReadOnlyExisting(path, new StateModelRegistry())) {
            CommitId selected = prior.ResolveTag("state-baseline");
            byte[] bytes = new byte[CommitId.EncodedSize];
            Require(selected.TryWrite(bytes) && CommitId.TryRead(bytes, out persisted), "CommitId codec failed.");
        }
        using var repository = Repository.OpenExisting(path, Models());
        CommitId firstEvent = repository.ResolveTag("event-start");
        CommitId firstState = repository.ResolveTag("state-baseline");
        Require(persisted == firstState, "A persistent same-repository CommitId changed after reopening.");
        repository.CreateTag("reopened-handle", persisted);
        Require(repository.ResolveTag("reopened-handle") == firstState, "A retained CommitId did not bind its exact State.");
        RequireRejected<ArgumentException>(() => repository.CreateTag("invalid", default));
        RequireRejected<ArgumentException>(() => repository.LoadState(firstEvent));
        Require(!repository.IsFaulted, "Predictable invalid-handle or role rejection faulted the repository.");
        Require(((TagEvent)repository.LoadEvent(firstEvent)).Code == 7, "Tagged Event changed after reopening.");
        CheckState((TagState)repository.LoadState(firstState), 10);

        // Creation and editing are separate: even an unedited fork is immediately durable.
        BranchInfo stateA = repository.ForkBranch(firstState, "state-a");
        using (var work = repository.OpenWorkingCopy(stateA.Id)) {
            CheckState((TagState)work.State!, 10);
            ((TagState)work.State!).Value = 21;
            work.CommitState();
        }
        BranchInfo stateB = repository.ForkBranch(firstState, "state-b");
        using (var work = repository.OpenWorkingCopy(stateB.Id)) {
            CheckState((TagState)work.State!, 10);
            ((TagState)work.State!).Value = 12;
            work.CommitState();
        }
        BranchInfo eventA = repository.ForkBranch(firstEvent, "event-a");
        using (var work = repository.OpenWorkingCopy(eventA.Id)) {
            Require(work.State is null && work.BaseCommitId == firstEvent, "Event fork did not retain its exact State-free current commit.");
            work.CommitState(new TagState { Value = 40 });
        }
        BranchInfo eventB = repository.ForkBranch(firstEvent, "event-b");
        using (var work = repository.OpenWorkingCopy(eventB.Id)) {
            Require(work.State is null && work.BaseCommitId == firstEvent, "Event fork imported a later State.");
            work.CommitState(new TagState { Value = 30 });
        }
        CommitId advanced;
        BranchId main = BranchNamed(repository, "main");
        using (var source = repository.OpenWorkingCopy(main)) {
            ((TagState)source.State!).Value = 99;
            advanced = source.CommitState().Id;
        }
        BranchInfo oldPoint = repository.ForkBranch(firstEvent, "from-old-event");
        Require(repository.ReadCurrentCommit(main).Commit.Id == advanced &&
            repository.ReadCurrentCommit(oldPoint.Id).Commit.Id == firstEvent,
            "Forking an old point rewound the source or manufactured a new commit.");
        Require(repository.ResolveTag("event-start") == firstEvent &&
            repository.ResolveTag("state-baseline") == firstState, "Advancing or forking changed an immutable tag.");
        CheckState((TagState)repository.LoadState(firstState), 10);
        Console.WriteLine("TagsContinue:StateForks:EventFirstStates:PersistentId:AppendOnly");
    }

    private static void Verify(string path) {
        using var repository = Repository.OpenReadOnlyExisting(path, Models());
        CommitId firstEvent = repository.ResolveTag("event-start");
        Require(repository.ReadCurrentCommit(BranchNamed(repository, "from-old-event")).Commit.Id == firstEvent &&
            ((TagEvent)repository.LoadEvent(firstEvent)).Code == 7,
            "The unedited old-point fork or Event tag changed on cold reopen.");
        CheckState((TagState)repository.LoadState(Current(repository, "main")), 99);
        CheckState((TagState)repository.LoadState(repository.ResolveTag("state-baseline")), 10);
        CheckState((TagState)repository.LoadState(repository.ResolveTag("reopened-handle")), 10);
        CheckState((TagState)repository.LoadState(Current(repository, "state-a")), 21);
        CheckState((TagState)repository.LoadState(Current(repository, "state-b")), 12);
        Require(((TagState)repository.LoadState(Current(repository, "event-a"))).Value == 40 &&
            ((TagState)repository.LoadState(Current(repository, "event-b"))).Value == 30,
            "First States continued from the Event tag did not survive reopening.");
        Console.WriteLine("TagsVerify:ColdValues:Cycles:ImmutableTags");
    }

    // Labels happen to be unique in this fixture; production callers can persist BranchId directly.
    private static BranchId BranchNamed(Repository repository, string label) =>
        repository.ListBranches().Single(branch => branch.DisplayLabel == label).Id;
    private static CommitId Current(Repository repository, string label) =>
        repository.ReadCurrentCommit(BranchNamed(repository, label)).Commit.Id;
    private static void RequireRejected<TException>(Action operation) where TException : Exception {
        try { operation(); }
        catch (TException) { return; }
        throw new InvalidDataException($"Expected {typeof(TException).Name}.");
    }

    private static void CheckState(TagState state, int expected) =>
        Require(state.Value == expected && ReferenceEquals(state, state.Self), "Tagged State lost its value or self-cycle.");

    private static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidDataException(message); }
    }
}
