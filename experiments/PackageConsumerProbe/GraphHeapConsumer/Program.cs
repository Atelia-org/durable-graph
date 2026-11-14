using System.Reflection;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;

namespace GraphHeapConsumer;

internal static class Program {
    private static readonly List<string> Checks = [];
    private static readonly EntryId MainEntry = new(1), OtherEntry = new(2);

    private static int Main(string[] args) {
        if (args.Length != 3 || args[0] is not ("seed" or "resume" or "verify")) {
            Console.Error.WriteLine("Usage: GraphHeapConsumer <seed|resume|verify> <database> <evidence.json>");
            return 2;
        }
        try {
            CheckExport();
            string path = Path.GetFullPath(args[1]);
            if (args[0] == "seed") { Seed(path); }
            else if (args[0] == "resume") { Resume(path); }
            else { Verify(path); }
            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name!.StartsWith("Atelia.", StringComparison.Ordinal) && a != typeof(Program).Assembly)
                .OrderBy(a => a.GetName().Name, StringComparer.Ordinal)
                .Select(a => new { Name = a.GetName().Name, Path = a.Location,
                    Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location))) }).ToArray();
            File.WriteAllText(args[2], JsonSerializer.Serialize(new {
                Passed = true, Lane = args[0], ProcessId = Environment.ProcessId, Checks, LoadedAssemblies = assemblies
            }, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            Console.WriteLine($"GraphHeap:{args[0]}:Passed:{Checks.Count}");
            return 0;
        } catch (Exception error) {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static StateModelRegistry Models(bool includeEntries = true) {
        StateModelRegistry models = new();
        models.Register(GraphRef.Definition);
        if (includeEntries) { Atelia.DurableGraph.Generated.DurableDefinitions.Register(models); }
        else {
            models.Register(Atelia.DurableGraph.Generated.Family_4772617068486561702E436F6E73756D65722E576F726C64.Definition);
            models.Register(Atelia.DurableGraph.Generated.Family_4772617068486561702E436F6E73756D65722E456E7472794964.Definition);
        }
        return models;
    }

    private static void CheckExport() {
        Need(typeof(GraphRef).Assembly != typeof(Program).Assembly, "GraphRef is consumed from the Persistence package.");
        var export = typeof(GraphRef).Assembly.GetCustomAttributes<DurableSchemaExportAttribute>()
            .Single(e => e.SchemaId == "Atelia.DurableGraph.GraphRef" && e.SchemaVersion == 1);
        string[] fields = export.Manifest.Split('\n').Where(line => line.StartsWith("// field:", StringComparison.Ordinal)).ToArray();
        Need(export.ContractVersion == 1 && fields.SequenceEqual(new[] { "// field:1|9", "// field:2|9", "// field:3|9" }),
            "GraphRef exports the ordinary three-UInt32 inline contract.");
        Need(!default(GraphRef).IsBound && typeof(GraphRef).GetConstructors().Length == 0,
            "Default is unbound and applications cannot import arbitrary commits through constructors.");
    }

    private static Entry NewEntry(string text, int value) {
        Detail detail = new() { Value = value };
        detail.Next = detail;
        return new Entry { Text = text, Left = detail, Right = detail };
    }

    private static void Seed(string path) {
        using Repository repository = Repository.CreateNew(path, Models());
        BranchInfo mainBranch = repository.CreateRootBranchFromState("main", new World());
        using WorkingCopy main = repository.OpenWorkingCopy(mainBranch.Id);
        using GraphBuilder a = repository.CreateGraph(NewEntry("neutral", 10));
        using GraphBuilder b = repository.CreateGraph(NewEntry("market", 90));
        GraphRef a0 = repository.SaveGraph(a);
        GraphRef b0 = repository.SaveGraph(b);
        Need(a0.IsBound && b0.IsBound && a0 != b0, "Two builders receive distinct bound graph references.");
        ((Entry)a.Root).Text = "ally";
        ((Entry)a.Root).Left!.Value = 11;
        GraphRef a1 = repository.SaveGraph(a);
        using GraphBuilder fork = repository.OpenGraph(a0);
        CheckEntry((Entry)fork.Root, "neutral", 10);
        ((Entry)fork.Root).Text = "rival";
        ((Entry)fork.Root).Left!.Value = 12;
        GraphRef a2 = repository.SaveGraph(fork);
        GraphRef noOp = repository.SaveGraph(fork);
        Need(a2 != noOp, "A no-op save returns a distinct immutable snapshot identity.");
        CheckEntry((Entry)a.Root, "ally", 11, expectTransientDefault: false);
        using (GraphBuilder orphan = repository.CreateGraph(NewEntry("secret-orphan", 99))) { repository.SaveGraph(orphan); }
        Need(repository.ListBranches().Length == 1, "Heap saves do not register business branches.");

        World world = (World)main.State!;
        world.Original = a0;
        world.Entries[MainEntry] = a1;
        world.Entries[OtherEntry] = b0;
        CommitId mainView = main.CommitState().Id;
        repository.CreateTag("main-view", mainView);
        BranchInfo alternate = repository.ForkBranch(mainView, "alternate");
        using WorkingCopy alternative = repository.OpenWorkingCopy(alternate.Id);
        ((World)alternative.State!).Entries[MainEntry] = noOp;
        repository.CreateTag("alternate-view", alternative.CommitState().Id);
        Need(repository.ListBranches().Length == 2, "Only the two world branches are visible.");
        using GraphBuilder exactOld = repository.OpenGraph(world.Original);
        CheckEntry((Entry)exactOld.Root, "neutral", 10);
    }

    private static void Resume(string path) {
        Entry.RejectConstruction = true;
        using Repository repository = Repository.OpenExisting(path, Models());
        BranchInfo mainBranch = repository.ListBranches().Single(branch => branch.DisplayLabel == "main");
        using WorkingCopy main = repository.OpenWorkingCopy(mainBranch.Id);
        World world = (World)main.State!;
        Need(world.Scratch == 0 && !world.Unbound.IsBound, "The generated world restores without constructors or target expansion.");
        using GraphBuilder resumed = repository.OpenGraph(world.Original);
        CheckEntry((Entry)resumed.Root, "neutral", 10);
        ((Entry)resumed.Root).Text = "resumed";
        ((Entry)resumed.Root).Left!.Value = 13;
        GraphRef next = repository.SaveGraph(resumed);
        world.Entries[MainEntry] = next;
        repository.CreateTag("resumed-view", main.CommitState().Id);
        using GraphBuilder reopened = repository.OpenGraph(next);
        CheckEntry((Entry)reopened.Root, "resumed", 13);
        World old = (World)repository.LoadState(repository.ResolveTag("main-view"));
        using GraphBuilder oldEntry = repository.OpenGraph(old.Entries[MainEntry]);
        CheckEntry((Entry)oldEntry.Root, "ally", 11);
        Need(repository.ListBranches().Length == 2 && !repository.IsFaulted, "Cold continuation preserves world branches and owner health.");
    }

    private static void Verify(string path) {
        Entry.RejectConstruction = true;
        // Reading the directory does not require the types of graphs it references.
        using (Repository directoryReader = Repository.OpenReadOnlyExisting(path, Models(includeEntries: false))) {
            World directory = (World)directoryReader.LoadState(directoryReader.ResolveTag("resumed-view"));
            Need(directory.Entries.Count == 2 && directory.Entries[MainEntry].IsBound,
                "World directory restores without registering entry/detail models.");
        }
        using Repository repository = Repository.OpenReadOnlyExisting(path, Models());
        World main = (World)repository.LoadState(repository.ResolveTag("main-view"));
        World alternate = (World)repository.LoadState(repository.ResolveTag("alternate-view"));
        World resumed = (World)repository.LoadState(repository.ResolveTag("resumed-view"));
        Need(Search(repository, main, "ally").SequenceEqual(new[] { MainEntry }) && Search(repository, main, "rival").Count == 0,
            "The historical main world's search sees ally, not its sibling's rival.");
        Need(Search(repository, alternate, "rival").SequenceEqual(new[] { MainEntry }) && Search(repository, alternate, "ally").Count == 0,
            "The alternate world's search uses its own selected graph versions.");
        Need(Search(repository, resumed, "resumed").SequenceEqual(new[] { MainEntry }), "Cold continuation remains selected by the latest world.");
        foreach (World world in new[] { main, alternate, resumed }) {
            Need(Search(repository, world, "secret-orphan").Count == 0, "Unselected heap records never enter world search.");
        }
        using GraphBuilder first = repository.OpenGraph(resumed.Entries[MainEntry]);
        using GraphBuilder second = repository.OpenGraph(resumed.Entries[MainEntry]);
        Need(!ReferenceEquals(first.Root, second.Root) && !ReferenceEquals(((Entry)first.Root).Left, ((Entry)second.Root).Left),
            "Separate Open calls isolate mutable roots and shared descendants.");
        ((Entry)first.Root).Text = "only in read-only reader memory";
        ((Entry)first.Root).Left!.Value = -1;
        CheckEntry((Entry)second.Root, "resumed", 13);
        Rejected<InvalidOperationException>(() => repository.SaveGraph(first), "Read-only graph editing cannot publish.");
        Rejected<InvalidOperationException>(() => repository.OpenGraph(default), "Default references reject on explicit Open.");
        Need(!repository.IsFaulted, "Expected read-only/default admission failures preserve owner health.");
    }

    private static List<EntryId> Search(Repository repository, World world, string text) {
        List<EntryId> matches = [];
        foreach ((EntryId id, GraphRef reference) in world.Entries) {
            using GraphBuilder builder = repository.OpenGraph(reference);
            if (((Entry)builder.Root).Text.Contains(text, StringComparison.Ordinal)) { matches.Add(id); }
        }
        return matches;
    }

    private static void CheckEntry(Entry entry, string text, int value, bool expectTransientDefault = true) {
        Need(entry.Text == text && entry.Left?.Value == value, "The selected graph retains its exact text and child value.");
        Need(ReferenceEquals(entry.Left, entry.Right) && ReferenceEquals(entry.Left, entry.Left!.Next), "Shared children and cycles survive.");
        if (expectTransientDefault) { Need(entry.Scratch == 0, "Restoration bypasses constructors and transient initialization."); }
    }

    private static void Need(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
        Checks.Add(message);
    }

    private static void Rejected<T>(Action action, string message) where T : Exception {
        try { action(); } catch (T error) when (error.GetType() == typeof(T)) { Checks.Add(message); return; }
        throw new InvalidOperationException(message);
    }
}
