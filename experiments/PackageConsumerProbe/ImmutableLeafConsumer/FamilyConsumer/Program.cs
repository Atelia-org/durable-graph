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
            EnableLeafExperiment(repository);
            using BranchCheckout session = repository.Checkout("main");
            Require(((Leaf)session.State!).Value == 42, "The reopened world lost the leaf's persisted value.");
            Require(!ReferenceEquals(leaf, session.State), "The original live seed must not populate the leaf table.");
            using BranchCheckout first = repository.Fork("pure-first", session.Head);
            using BranchCheckout second = repository.Fork("pure-second", session.Head);
            Require(ReferenceEquals(first.State, second.State), "Pure leaf roots should reuse the successfully restored instance.");
            first.CommitState(Policy);
            second.CommitState(Policy);

            Owner owner = new() { First = new Leaf(7), Second = new Leaf(7) };
            owner.Alias = owner.First;
            owner.Next = owner;
            Require(!ReadImmutableLeafFlag(ResolveBinding(typeof(Owner))), "Mutable owner must not claim leaf capability.");
            using BranchCheckout source = repository.CreateBranch("owners", owner, Policy);
            using BranchCheckout left = repository.Fork("owner-left", source.Head);
            using BranchCheckout right = repository.Fork("owner-right", source.Head);
            Owner leftOwner = (Owner)left.State!, rightOwner = (Owner)right.State!;
            Require(!ReferenceEquals(leftOwner, rightOwner), "Mutable owner leaked across branches.");
            Require(ReferenceEquals(leftOwner.First, rightOwner.First), "Generated Family leaf did not reuse across forks.");
            Require(!ReferenceEquals(owner.First, leftOwner.First), "Live CreateBranch leaf must not seed restored sharing.");
            Require(!ReferenceEquals(leftOwner.First, leftOwner.Second), "Equal-valued leaves with different IDs merged.");
            Require(ReferenceEquals(leftOwner.First, leftOwner.Alias) && ReferenceEquals(leftOwner.Next, leftOwner), "Graph aliases/cycle lost.");
            leftOwner.Counter = 1;
            CheckpointAddress committed = left.CommitState(Policy);
            Require(rightOwner.Counter == 0 && rightOwner.First!.Value == 7, "One branch's edit mutated another branch.");
            Owner independent = (Owner)repository.ReadState(committed);
            Require(independent.Counter == 1 && !ReferenceEquals(independent.First, leftOwner.First), "ReadState must remain independent of the leaf table.");
        }
        using (Repository repository = Repository.OpenExisting(directory, Models(), Options)) {
            using BranchCheckout left = repository.Checkout("owner-left");
            using BranchCheckout right = repository.Checkout("owner-right");
            Require(((Owner)left.State!).Counter == 1 && ((Owner)right.State!).Counter == 0, "Cold reopen lost independent saves.");
            Require(((Owner)left.State!).First!.Value == 7 && ((Owner)right.State!).Second!.Value == 7, "Cold reopen lost leaf content.");
        }
        Console.WriteLine("ImmutableLeafFamily:Flag:True:Roundtrip:True:ForkSharing:True:MutableIsolation:True:ColdSave:True");
    }

    private static StateModelRegistry Models() {
        var models = new StateModelRegistry();
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
        return models;
    }

    private static StateModelBinding ResolveLeafBinding() => ResolveBinding(typeof(Leaf));

    private static StateModelBinding ResolveBinding(Type type) {
        // StateModelRegistry.Snapshot is internal to the delivered Persistence package; the
        // consumer reads it through reflection and uses only the public StateBindingContext
        // surface. The probe adds no public query API and uses no friend access (C5).
        MethodInfo snapshot = typeof(StateModelRegistry).GetMethod("Snapshot", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Missing internal StateModelRegistry.Snapshot method.");
        var context = (StateBindingContext)snapshot.Invoke(Models(), new object?[] { null })!;
        return context.ResolveCurrentModel(type);
    }

    private static void EnableLeafExperiment(Repository repository) {
        // Exercise the internal A/B candidate even if DB-082 measurement leaves the default disabled.
        // This is probe-only reflection, not a public option or friend-assembly dependency.
        PropertyInfo toggle = typeof(Repository).GetProperty("ImmutableLeafReuseEnabled", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Missing internal DB-082 experiment toggle.");
        toggle.SetValue(repository, true);
    }

    private static bool ReadImmutableLeafFlag(StateModelBinding binding) =>
        (bool)(typeof(StateModelBinding).GetProperty("IsImmutableLeaf", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Missing internal StateModelBinding.IsImmutableLeaf property.")
        ).GetValue(binding)!;

    private static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}

[DurableType("probe.leaf.owner", 1)]
public partial class Owner : IDurableObject {
    [DurableField(1)] public Leaf? First;
    [DurableField(2)] public Leaf? Second;
    [DurableField(3)] public Leaf? Alias;
    [DurableField(4)] public Owner? Next;
    [DurableField(5)] public int Counter;
}
