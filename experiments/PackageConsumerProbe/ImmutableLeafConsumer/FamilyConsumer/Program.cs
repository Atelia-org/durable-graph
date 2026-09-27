using System.Reflection;
using Atelia.Data;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;

namespace ImmutableLeafProbe;

internal static class Program {
    private static readonly RbfSegmentStoreOptions Options = new() { NewStoreLayout = RbfSegmentStoreLayout.Flat };
    private static readonly ReadAmplificationBaseBudgetParameters Policy = new(int.MaxValue, 1);

    private static void Main(string[] args) {
        string directory = Path.GetFullPath(args.Single());
        Require(ReadImmutableLeafFlag(ResolveLeafBinding()), "The Family leaf binding must report IsImmutableLeaf=true.");
        Leaf leaf = new(42);
        using (Repository repository = Repository.CreateNew(directory, Models(), Options)) {
            using BranchCheckout session = repository.CreateBranch("main", leaf, Policy);
            Require(((Leaf)session.State!).Value == 42, "CreateBranch lost the seed value.");
        }
        using (Repository repository = Repository.OpenExisting(directory, Models(), Options)) {
            using BranchCheckout session = repository.Checkout("main");
            Require(((Leaf)session.State!).Value == 42, "The reopened world lost the leaf's persisted value.");
        }
        Console.WriteLine("ImmutableLeafFamily:Flag:True:Roundtrip:True");
    }

    private static StateModelRegistry Models() {
        var models = new StateModelRegistry();
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
        return models;
    }

    private static StateModelBinding ResolveLeafBinding() {
        // StateModelRegistry.Snapshot is internal to the delivered Persistence package; the
        // consumer reads it through reflection and uses only the public StateBindingContext
        // surface. The probe adds no public query API and uses no friend access (C5).
        MethodInfo snapshot = typeof(StateModelRegistry).GetMethod("Snapshot", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Missing internal StateModelRegistry.Snapshot method.");
        var context = (StateBindingContext)snapshot.Invoke(Models(), new object?[] { null })!;
        return context.ResolveCurrentModel(typeof(Leaf));
    }

    private static bool ReadImmutableLeafFlag(StateModelBinding binding) =>
        (bool)(typeof(StateModelBinding).GetProperty("IsImmutableLeaf", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Missing internal StateModelBinding.IsImmutableLeaf property.")
        ).GetValue(binding)!;

    private static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
