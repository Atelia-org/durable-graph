extern alias persistence;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using persistence::Atelia.DurableGraph;
using persistence::Atelia.DurableGraph.Persistence;

namespace ExplicitStoragePackage;

internal static class Program {
    private static int Main(string[] args) {
#if CORRECTED
        try {
            Need(args.Length == 3, "Expected phase, database and evidence path.");
            string phase = args[0], database = Path.GetFullPath(args[1]);
            StateModelRegistry models = Models();
#if FAMILY
            int[] fields = global::Atelia.DurableGraph.Generated.Family_70726F62652E6578706C696369742E7375626A656374.Definition
                .Templates.Single(template => template.Version == 1).Fields.Select(field => field.FieldId).ToArray();
#else
            int[] fields = Subject.Schema.Fields.Select(field => field.FieldId).ToArray();
#endif
            Need(fields.SequenceEqual([1, 2]), "Transient storage changed the persistent schema.");
            Subject.AccessorCalls = 0;
            if (phase == "write") {
                Subject input = new() { Value = 42, Control = 7, Scratch = 777 };
                Need(Subject.AccessorCalls == 1, "The fixture must exercise its setter before capture.");
                Subject.AccessorCalls = 0;
                using Repository repository = Repository.CreateNew(database, models, schemaWriteMode: SchemaWriteMode.Shared);
                using BranchCheckout branch = repository.CreateBranch("main", input);
                Need(Subject.AccessorCalls == 0, "Capture executed a Transient accessor.");
            } else if (phase == "read") {
                using Repository repository = Repository.OpenReadOnlyExisting(database, models);
                Subject restored = (Subject)repository.ReadState(repository.GetHead("main")!)!;
                Need(restored.Value == 42 && restored.Control == 7, "Cold values disagree.");
                Need(Subject.AccessorCalls == 0, "Restore executed a Transient accessor.");
                Need(restored.Scratch == 0 && Subject.AccessorCalls == 1,
                    "Transient backing must be default; only this explicit verification may call the getter.");
            } else {
                throw new InvalidDataException("Unknown phase.");
            }
            string[] names = ["Atelia.Binary", "Atelia.Data", "Atelia.DurableGraph", "Atelia.DurableGraph.Persistence",
                "Atelia.DurableGraph.Serialization", "Atelia.DurableGraph.Storage", "Atelia.FrameStore",
                "Atelia.Primitives", "Atelia.Rbf", "Atelia.VersionStore"];
            File.WriteAllText(args[2], JsonSerializer.Serialize(new {
                Protocol = "DG.ExplicitStorageClassificationConsumer.v1", Phase = phase, Passed = true,
                ProcessId = Environment.ProcessId, Route = Route, FieldIds = fields, Subject.AccessorCalls,
                HostAssembly = Evidence(typeof(Program).Assembly), LoadedAssemblies = names.Select(Assembly.Load).Select(Evidence),
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("ExplicitStorageClassificationConsumer:" + phase + ":Passed");
            return 0;
        } catch (Exception exception) {
            Console.Error.WriteLine(exception);
            return 1;
        }
#else
        return 0; // The invalid fixture must fail compilation before it can run.
#endif
    }
#if CORRECTED
#if FAMILY
    private const string Route = "Family";
#else
    private const string Route = "Default";
#endif
    private static StateModelRegistry Models() {
        StateModelRegistry models = new();
#if FAMILY
        global::Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
#else
        models.Register(Subject.__DurableState.Model);
        models.Register(LegalControl.__DurableState.Model);
#endif
        return models;
    }
    private static object Evidence(Assembly assembly) => new {
        Name = assembly.GetName().Name, Path = assembly.Location,
        Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
    };
    private static void Need(bool condition, string message) {
        if (!condition) { throw new InvalidDataException(message); }
    }
#endif
}
