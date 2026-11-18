using System.Security.Cryptography;
using System.Text.Json;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;

if (args.Length != 3 || args[0] is not ("seed" or "read")) return 2;
string stage = "Start";
Exception? failure = null;
string? observed = null;
StateModelRegistry Models() {
    var registry = new StateModelRegistry();
    registry.Register(GraphRef.Definition);
    Atelia.DurableGraph.Generated.DurableDefinitions.Register(registry);
    return registry;
}
try {
    if (args[0] == "seed") {
        stage = "CreateNew";
        using var repository = Repository.CreateNew(args[1], Models());
        stage = "CreateRootBranchFromState";
        var branch = repository.CreateRootBranchFromState("legacy-control", new Control());
        using var copy = repository.OpenWorkingCopy(branch.Id);
        repository.CreateTag("admission-control", copy.CommitState().Id);
        stage = "Completed";
        observed = branch.DisplayLabel;
    } else {
        stage = "OpenReadOnlyExisting";
        using var repository = Repository.OpenReadOnlyExisting(args[1], Models());
        stage = "ListBranches";
        var branches = repository.ListBranches();
        stage = "ResolveTag";
        var commit = repository.ResolveTag(branches.Single().DisplayLabel == "legacy-control" ? "admission-control" : "seed");
        stage = "LoadState";
        var root = repository.LoadState(commit);
        if (root is Control control) observed = control.Value;
        else if (root is ReaderWorld world) {
            stage = "OpenGraphWithText";
            using var graph = repository.OpenGraph(world.Latest);
            observed = graph.Root.ToString();
        } else throw new InvalidDataException("Unexpected root type for admission fixture.");
        stage = "Completed";
    }
} catch (Exception error) { failure = error; }
var chain = new List<object>();
for (var e = failure; e is not null; e = e.InnerException) chain.Add(new { Type = e.GetType().FullName, e.Message });
var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name!.StartsWith("Atelia.", StringComparison.Ordinal))
    .OrderBy(a => a.GetName().Name, StringComparer.Ordinal)
    .Select(a => new { Name = a.GetName().Name, Path = a.Location, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location))) }).ToArray();
File.WriteAllText(args[2], JsonSerializer.Serialize(new {
    Mode = args[0], Stage = stage, Rejected = failure is not null, Observed = observed,
    ProcessId = Environment.ProcessId, Exceptions = chain, Detail = failure?.ToString(), LoadedAssemblies = assemblies
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"ReaderAdmission:{args[0]}:{stage}:{(failure is null ? "Accepted" : "Rejected")}");
// This is an observation process. The runner checks the exact expected outcome;
// an arbitrary exception is never independently declared a successful rejection.
return 0;
