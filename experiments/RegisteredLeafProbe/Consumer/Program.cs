using System.Security.Cryptography;
using System.Text.Json;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using Atelia.Text;
using Probe.Bitmap;

namespace Probe.Consumer;

internal static class Program {
    private const int BitCount = 32771;
    private static readonly string Original = "intro\r\n" + new string('a', 8192) + "\0\ud800\ud83d\ude00";
    private static readonly string Edited = Original.Remove(127, 3).Insert(127, "west");
    private static readonly string Final = Edited.Remove(1021, 2).Insert(1021, "east gate");

    public static void Main(string[] args) {
        if (args.Length != 3) throw new ArgumentException("Usage: seed|resume|verify|missing store receipt.json");
        object result = args[0] switch {
            "seed" => Seed(args[1]),
            "resume" => Resume(args[1]),
            "verify" => Verify(args[1]),
            "missing" => Missing(args[1]),
            _ => throw new ArgumentException(args[0])
        };
        var assemblies = new[] { typeof(VariableBitmap), typeof(TextEditor), typeof(IDurableObject), typeof(Repository), typeof(Program) }
            .Select(t => new { name = t.Assembly.GetName().Name, hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(t.Assembly.Location))) });
        File.WriteAllText(args[2], JsonSerializer.Serialize(new { mode = args[0], process = Environment.ProcessId, result, assemblies },
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{args[0]} passed");
    }

    internal static StateModelRegistry Models(bool text = true, bool bitmap = true) {
        var models = new StateModelRegistry();
        models.Register(GraphRef.Definition);
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
        if (text) {
            TextEditorPersistence.Register(models);
            TextEditorPersistence.Register(models);
        }
        if (bitmap) {
            BitmapPersistence.Register(models);
            BitmapPersistence.Register(models);
        }
        return models;
    }

    private static object Seed(string path) {
        using var repository = Repository.CreateNew(path, Models());
        var branch = repository.CreateRootBranchFromState("main", new ProbeWorld());
        using var working = repository.OpenWorkingCopy(branch.Id);
        var world = (ProbeWorld)working.State!;
        var text = new TextEditor(Original);
        var flags = new VariableBitmap(BitCount);
        flags[0] = true; flags[8] = true; flags[8193] = true;
        var entry = new MixedEntry { Body = text, Alias = text, Texts = [text, text], Flags = flags, OtherFlags = flags, Bitmaps = [flags, flags] };
        using var graph = repository.CreateGraph(entry);
        world.Original = repository.SaveGraph(graph);
        text.Replace(127, 3, "west");
        flags[1] = true;
        world.Hot = repository.SaveGraph(graph);
        var seed = working.CommitState();
        repository.CreateTag("seed", seed.Id);
        Check(entry, Edited, 1);
        using var history = repository.OpenGraph(world.Original);
        Check((MixedEntry)history.Root, Original, 0);
        return new { originalAndEdited = true, aliases = true, stage = "hot-save", actualRecords = Records.InspectMixedEdit(repository, world.Original, world.Hot) };
    }

    private static object Resume(string path) {
        using var repository = Repository.OpenExisting(path, Models());
        using var working = repository.OpenWorkingCopy(repository.ListBranches().Single().Id);
        var world = (ProbeWorld)working.State!;
        using var graph = repository.OpenGraph(world.Hot);
        using var other = repository.OpenGraph(world.Hot);
        var entry = (MixedEntry)graph.Root;
        var sibling = (MixedEntry)other.Root;
        Check(entry, Edited, 1);
        Check(sibling, Edited, 1);
        Need(!ReferenceEquals(entry.Body, sibling.Body) && !ReferenceEquals(entry.Flags, sibling.Flags), "separate opens must allocate separate leaf identities");
        var old = entry.Body.Snapshot();
        entry.Body.Replace(1021, 2, "east gate");
        entry.Flags.Resize(40011);
        entry.Flags[40010] = true;
        world.Latest = repository.SaveGraph(graph);
        repository.CreateTag("resumed", working.CommitState().Id);
        Check(entry, Final, 2);
        Check(sibling, Edited, 1);
        Need(old.ToString() == Edited, "old editor snapshot survived second save");
        return new { independentOpens = true, oldSnapshot = true, variableBitmapLength = entry.Flags.Length };
    }

    private static object Verify(string path) {
        using var repository = Repository.OpenReadOnlyExisting(path, Models());
        var world = (ProbeWorld)repository.LoadState(repository.ResolveTag("resumed"));
        foreach (var (id, value, generation) in new[] { (world.Original, Original, 0), (world.Hot, Edited, 1), (world.Latest, Final, 2) }) {
            using var graph = repository.OpenGraph(id);
            Check((MixedEntry)graph.Root, value, generation);
        }
        return new { reopenedHistoricalGenerations = 3, aliases = true, contentVerified = true };
    }

    private static object Missing(string path) {
        var failures = new List<object>();
        foreach (bool omitText in new[] { false, true }) {
            Exception? failure = null;
            try {
                using var repository = Repository.OpenReadOnlyExisting(path, Models(text: !omitText, bitmap: omitText));
                var world = (ProbeWorld)repository.LoadState(repository.ResolveTag("resumed"));
                using var graph = repository.OpenGraph(world.Latest);
                _ = graph.Root;
            } catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentException or KeyNotFoundException) {
                failure = ex;
            }
            Need(failure is not null, "missing leaf registration was accepted");
            string id = omitText ? "Atelia.Text.TextEditor" : "Probe.VariableBitmapV1";
            Need(failure!.Message.Contains(id, StringComparison.Ordinal), "rejection must identify the missing leaf");
            failures.Add(new { missing = omitText ? "text" : "bitmap", error = failure!.GetType().Name, failure.Message });
        }
        return failures;
    }

    private static void Check(MixedEntry e, string expected, int generation) {
        Need(e.Body.ToString() == expected && e.Body.Length == expected.Length, "text content");
        Need(ReferenceEquals(e.Alias, e.Body) && e.Texts.Count == 2 && e.Texts.All(v => ReferenceEquals(v, e.Body)), "text aliases");
        Need(ReferenceEquals(e.OtherFlags, e.Flags) && e.Bitmaps.Count == 2 && e.Bitmaps.All(v => ReferenceEquals(v, e.Flags)) && e.Missing is null, "bitmap aliases and null");
        Need(e.Flags.Length == (generation == 2 ? 40011 : BitCount), "bit length");
        for (int i = 0; i < e.Flags.Length; i++) {
            bool set = i is 0 or 8 or 8193 || (generation >= 1 && i == 1) || (generation == 2 && i == 40010);
            Need(e.Flags[i] == set, "bitmap mismatch");
        }
    }

    internal static void Need(bool condition, string message) {
        if (!condition) throw new InvalidOperationException(message);
    }
}
