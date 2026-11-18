using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;

namespace DurableTextConsumer;

internal static class Program {
    private const string Initial = "alpha\r\nbeta\0\ud800\r\nalpha";
    private const string Saved = "[alpha\r\nBETA\0\ud800\r\nalpha]";
    private const string Resumed = "[alpha\r\ngamma\0\ud800\r\nalpha]\r\ntail";
    private static readonly List<string> Checks = [];
    private static int Main(string[] args) {
        if (args.Length != 3 || args[0] is not ("seed" or "resume" or "verify")) return 2;
        try {
            if (args[0] == "seed") Seed(args[1]);
            else if (args[0] == "resume") Resume(args[1]);
            else Verify(args[1]);
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name!.StartsWith("Atelia.", StringComparison.Ordinal) && a != typeof(Program).Assembly)
                .OrderBy(a => a.GetName().Name, StringComparer.Ordinal)
                .Select(a => new { Name = a.GetName().Name, Path = a.Location,
                    Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location))) }).ToArray();
            File.WriteAllText(args[2], JsonSerializer.Serialize(new {
                Passed = true, Lane = args[0], ProcessId = Environment.ProcessId, Checks, LoadedAssemblies = loaded
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"DurableText:{args[0]}:Passed:{Checks.Count}");
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static StateModelRegistry Models() {
        var registry = new StateModelRegistry();
        registry.Register(GraphRef.Definition);
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(registry);
        return registry;
    }
    private static void Seed(string path) {
        using var repository = Repository.CreateNew(path, Models());
        var branch = repository.CreateRootBranchFromState("main", new World());
        using var working = repository.OpenWorkingCopy(branch.Id);
        var text = new DurableText(Initial);
        var entry = new Entry { Body = text, Alias = text, Bodies = [text, text] };
        using var graph = repository.CreateGraph(entry);
        var original = repository.SaveGraph(graph);
        var held = text.Snapshot();
        bool rejected = false;
        try { ReplaceUnique(text, "alpha", "bad"); } catch (InvalidOperationException) { rejected = true; }
        Need(rejected && text.ToString() == Initial, "Ambiguous str_replace rejects before editing.");
        ReplaceUnique(text, "beta", "BETA");
        repository.SaveGraph(graph);
        text.ApplyEdits([new TextEdit(0, 0, "["), new TextEdit(text.Length, 0, "]")]);
        var latest = repository.SaveGraph(graph);
        Need(held.ToString() == Initial, "Old snapshot survives edits and accepted saves including UTF16/NUL/CRLF.");
        Check(entry, Saved);
        var builder = new StringBuilder("prefix:");
        text.AppendTo(builder);
        held.AppendTo(builder, 7, 4);
        Need(builder.ToString() == "prefix:" + Saved + "beta", "AppendTo appends current text and old snapshot range without clearing destination.");
        string input = text.Snapshot().ToString();
        var matches = Regex.Matches(input, "(alpha).*?(BETA)", RegexOptions.Singleline, TimeSpan.FromSeconds(1));
        Need(matches.Count == 1 && matches[0].Groups[2].Value == "BETA", "Standard full-text Regex consumes explicitly materialized snapshot with captures.");
        var world = (World)working.State!; world.Original = original; world.Latest = latest;
        repository.CreateTag("seed", working.CommitState().Id);
    }
    private static void Resume(string path) {
        using var repository = Repository.OpenExisting(path, Models());
        using var working = repository.OpenWorkingCopy(repository.ListBranches().Single().Id);
        var world = (World)working.State!;
        using var graph = repository.OpenGraph(world.Latest);
        using var independent = repository.OpenGraph(world.Latest);
        var entry = (Entry)graph.Root;
        Check(entry, Saved); Check((Entry)independent.Root, Saved);
        Need(!ReferenceEquals(entry.Body, ((Entry)independent.Root).Body), "Separate cold OpenGraph calls isolate mutable text identity.");
        var frozen = entry.Body.Snapshot();
        ReplaceUnique(entry.Body, "BETA", "gamma");
        entry.Body.Replace(entry.Body.Length, 0, "\r\ntail");
        world.Latest = repository.SaveGraph(graph);
        repository.CreateTag("resumed", working.CommitState().Id);
        Check(entry, Resumed); Check((Entry)independent.Root, Saved);
        Need(frozen.ToString() == Saved, "Cold snapshot survives continued save.");
        using var old = repository.OpenGraph(world.Original);
        Check((Entry)old.Root, Initial);
    }
    private static void Verify(string path) {
        TextSnapshot retained;
        using (var repository = Repository.OpenReadOnlyExisting(path, Models())) {
            var world = (World)repository.LoadState(repository.ResolveTag("resumed"));
            using var current = repository.OpenGraph(world.Latest);
            using var old = repository.OpenGraph(world.Original);
            Check((Entry)current.Root, Resumed); Check((Entry)old.Root, Initial);
            var seed = (World)repository.LoadState(repository.ResolveTag("seed"));
            using var seeded = repository.OpenGraph(seed.Latest);
            Check((Entry)seeded.Root, Saved);
            retained = ((Entry)current.Root).Body.Snapshot();
            Span<char> range = stackalloc char[5]; retained.CopyTo(8, range);
            Need(range.SequenceEqual("gamma"), "Snapshot CopyTo returns exact UTF16 range.");
        }
        Need(retained.ToString() == Resumed, "Snapshot remains usable after Repository and GraphBuilder disposal.");
    }
    private static void ReplaceUnique(DurableText text, string needle, string replacement) {
        if (needle.Length == 0) throw new ArgumentException("Tool rejects empty needle.");
        var snapshot = text.Snapshot();
        int first = snapshot.IndexOf(needle);
        if (first < 0 || snapshot.IndexOf(needle, first + needle.Length) >= 0) throw new InvalidOperationException("Expected exactly one match.");
        text.ApplyEdits([new TextEdit(first, needle.Length, replacement)]);
    }
    private static void Check(Entry entry, string expected) {
        Need(entry.Body.ToString() == expected && entry.Gist == "one-line", "Text content and independent gist survive.");
        Need(ReferenceEquals(entry.Body, entry.Alias) && entry.Bodies.Count == 2 && entry.Bodies.All(x => ReferenceEquals(x, entry.Body)),
            "Field and List<DurableText> references restore one aliased object.");
        Need(entry.Missing is null && entry.Empty.Length == 0 && !ReferenceEquals(entry.Empty, entry.Body), "Null, empty text and body remain distinct.");
    }
    private static void Need(bool condition, string message) {
        if (!condition) throw new InvalidOperationException(message);
        Checks.Add(message);
    }
}
