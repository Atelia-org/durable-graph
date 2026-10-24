using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;

namespace EventReferenceConsumer;

internal static class Program {
    private const string MainText = "main:accepted\\\"payload";
    private const string ForkText = "fork:accepted\\\"payload";
    private static readonly List<string> Checks = [];

    private static int Main(string[] args) {
        if (args.Length != 3 || args[0] is not ("seed" or "verify")) {
            Console.Error.WriteLine("Usage: EventReferenceConsumer <seed|verify> <database> <evidence.json>");
            return 2;
        }
        try {
            CheckExport();
            if (args[0] == "seed") { Seed(Path.GetFullPath(args[1])); }
            else { Verify(Path.GetFullPath(args[1])); }
            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name!.StartsWith("Atelia.", StringComparison.Ordinal) && a != typeof(Program).Assembly)
                .OrderBy(a => a.GetName().Name, StringComparer.Ordinal)
                .Select(a => new { Name = a.GetName().Name, Path = a.Location,
                    Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location))) }).ToArray();
            File.WriteAllText(args[2], JsonSerializer.Serialize(new {
                Passed = true, Lane = args[0], ProcessId = Environment.ProcessId,
                Checks, LoadedAssemblies = assemblies
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"EventReference:{args[0]}:Passed:{Checks.Count}");
            return 0;
        } catch (Exception error) {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static StateModelRegistry Models() {
        StateModelRegistry models = new();
        models.Register(EventRef.Definition);
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
        return models;
    }

    private static void CheckExport() {
        Need(typeof(EventRef).Assembly != typeof(Program).Assembly, "EventRef comes from a different assembly.");
        var export = typeof(EventRef).Assembly.GetCustomAttributes<DurableSchemaExportAttribute>()
            .Single(e => e.SchemaId == "Atelia.DurableGraph.EventRef" && e.SchemaVersion == 1);
        Need(export.ContractVersion == 1, "The public package exports the ordinary inline contract.");
        string[] fields = export.Manifest.Split('\n').Where(line => line.StartsWith("// field:", StringComparison.Ordinal)).ToArray();
        Need(fields.SequenceEqual(new[] { "// field:1|9", "// field:2|9", "// field:3|9" }),
            "The exported EventRef shape is exactly three scalar UInt32 fields.");
        Need(!default(EventRef).IsBound, "Default is unbound.");
        Rejected<InvalidOperationException>(() => default(EventRef).ToCommitId(), "Default ToCommitId rejects.");
        Rejected<ArgumentNullException>(() => EventRef.FromCommit(null!), "Null descriptors reject.");
    }

    private static void Seed(string path) {
        using var repository = Repository.CreateNew(path, Models());
        BranchInfo mainBranch = repository.CreateRootBranchFromState("main", new ReferenceState());
        using var main = repository.OpenWorkingCopy(mainBranch.Id);
        var original = new Fact { Sequence = 7, Text = MainText };
        CommitInfo first = main.CommitEvent(original);
        EventRef mainRef = EventRef.FromCommit(first);
        Need(mainRef.IsBound && mainRef.ToCommitId() == first.Id, "A receipt preserves its exact commit.");
        Need(mainRef == EventRef.FromCommit(repository.ReadCommitInfo(first.Id)) && mainRef.GetHashCode() == EventRef.FromCommit(first).GetHashCode(),
            "References compare and hash by position.");
        repository.CreateTag("main-first", first.Id);
        original.Text = "caller mutation after publication";
        CheckFact(repository.LoadEvent<Fact>(mainRef), 7, MainText);
        Fill((ReferenceState)main.State!, mainRef, mainRef, 7);
        CommitInfo mainState = main.CommitState();
        repository.CreateTag("main-basis", mainState.Id);
        Rejected<ArgumentException>(() => EventRef.FromCommit(mainState), "A State descriptor cannot issue an EventRef.");
        Rejected<ArgumentException>(() => repository.LoadEvent(mainState.Id), "The original CommitId overload still rejects State role.");
        BranchInfo forkBranch = repository.ForkBranch(mainState.Id, "fork");
        using (var fork = repository.OpenWorkingCopy(forkBranch.Id)) {
            Need(((ReferenceState)fork.State!).Direct == mainRef, "A same-repository fork inherits the original reference.");
            CommitInfo other = fork.CommitEvent(new Fact { Sequence = 7, Text = ForkText });
            EventRef forkRef = EventRef.FromCommit(other);
            repository.CreateTag("fork-event", other.Id);
            Need(forkRef != mainRef, "Equal business sequences on two branches have different references.");
            Fill((ReferenceState)fork.State!, forkRef, mainRef, 7);
            repository.CreateTag("fork-state", fork.CommitState().Id);
        }
        CommitInfo later = main.CommitEvent(new Fact { Sequence = 8, Text = "later-main" });
        repository.CreateTag("main-later", later.Id);
        ((ReferenceState)main.State!).Progress = 8;
        repository.CreateTag("main-state", main.CommitState().Id);
        Need(!repository.IsFaulted, "Expected admission failures leave the writer healthy.");
    }

    private static void Fill(ReferenceState state, EventRef direct, EventRef original, int progress) {
        state.Direct = direct;
        state.Optional = original;
        state.Absent = null;
        state.Unbound = default;
        state.References = [original, direct, default];
        state.ReferenceArray = [direct, original, default];
        state.Progress = progress;
    }

    private static void Verify(string path) {
        ReferenceState.RejectConstruction = true;
        using var repository = Repository.OpenReadOnlyExisting(path, Models());
        EventRef mainRef = EventRef.FromCommit(repository.ReadCommitInfo(repository.ResolveTag("main-first")));
        EventRef forkRef = EventRef.FromCommit(repository.ReadCommitInfo(repository.ResolveTag("fork-event")));
        var main = (ReferenceState)repository.LoadState(repository.ResolveTag("main-state"));
        var fork = (ReferenceState)repository.LoadState(repository.ResolveTag("fork-state"));
        CheckState(main, mainRef, mainRef, 8);
        CheckState(fork, forkRef, mainRef, 7);
        Need(mainRef.ToCommitId() != repository.ResolveTag("main-later"), "The old reference does not follow the branch tip.");
        CheckFact(repository.LoadEvent<Fact>(mainRef), 7, MainText);
        CheckFact(repository.LoadEvent<Fact>(forkRef), 7, ForkText);
        CheckFact((Fact)repository.LoadEvent(mainRef), 7, MainText);
        Fact first = repository.LoadEvent<Fact>(mainRef);
        first.Text = "mutated independent read";
        Fact second = repository.LoadEvent<Fact>(mainRef);
        Need(!ReferenceEquals(first, second), "Independent Event loads have distinct mutable roots.");
        CheckFact(second, 7, MainText);
        Rejected<InvalidDataException>(() => repository.LoadEvent<ReferenceState>(mainRef), "The generic overload rejects a wrong result type.");
        Rejected<InvalidOperationException>(() => repository.LoadEvent(default(EventRef)), "The EventRef overload rejects default.");
        Rejected<ArgumentException>(() => EventRef.FromCommit(repository.ReadCommitInfo(repository.ResolveTag("main-state"))),
            "Cold State descriptors still reject EventRef issuance.");
        Need(!repository.IsFaulted, "Read validation failures preserve the ordinary owner fault contract.");
    }

    private static void CheckState(ReferenceState state, EventRef direct, EventRef original, int progress) {
        Need(state.Direct == direct && state.Optional == original && state.Absent is null && !state.Unbound.IsBound,
            "Direct, nullable-present, nullable-absent and default fields survive cold restoration.");
        Need(state.References.SequenceEqual(new[] { original, direct, default(EventRef) }) &&
            state.ReferenceArray.SequenceEqual(new[] { direct, original, default(EventRef) }), "List and array references survive cold restoration.");
        Need(state.Progress == progress && state.Scratch == 0, "Cold restoration keeps progress and bypasses construction/Transient initialization.");
    }

    private static void CheckFact(Fact fact, long sequence, string text) =>
        Need(fact.Sequence == sequence && fact.Text == text, "The exact Event root retains its accepted fact.");

    private static void Need(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
        Checks.Add(message);
    }

    private static void Rejected<T>(Action action, string message) where T : Exception {
        try { action(); } catch (T error) when (error.GetType() == typeof(T)) { Checks.Add(message); return; }
        throw new InvalidOperationException(message);
    }
}
