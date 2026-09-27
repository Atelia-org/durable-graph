using System.Reflection;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.RbfSegmentStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class DB080MaterializationRequirementTests : IDisposable {
    private const string Prefix = "durable-db080-materialization-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirstMaterializationStandardChecksBecomePartOfResidentCertificate(bool duringAllocate) {
        const string dependencyId = "DB080MaterializationDependency";
        DurableSchema dependency = new(dependencyId, 1, SchemaKind.InlineValue,
            new DurableFieldInfo(1, TypeTag.Int32));
        StateModelSnapshot? context = null;
        int allocations = 0, hydrations = 0;
        var models = SharedReadModel.Models(allocate: () => {
            allocations++;
            if (duringAllocate) { context!.BindSchema(dependency); }
            return new();
        }, hydrate: _ => {
            hydrations++;
            if (!duringAllocate) { context!.BindSchema(dependency); }
        });
        models.Register(new StateDefinitionBinding(dependencyId, SchemaKind.InlineValue, 0, null,
            [new(dependencyId, 1, SchemaKind.InlineValue, 0,
                [new(1, TypeExpr.Builtin(TypeTag.Int32))])]));
        using Repository repository = Repository.CreateNew(_path, models,
            new() { NewStoreLayout = RbfSegmentStoreLayout.Flat });
        context = (StateModelSnapshot)typeof(Repository)
            .GetField("_models", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(repository)!;
        GraphResources resources = (GraphResources)typeof(Repository)
            .GetField("_resources", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(repository)!;
        using BranchCheckout main = repository.CreateBranch("main", new SharedReadModel.Node { Value = 7 });
        using (BranchCheckout first = repository.Fork("first", main.Head)) {
            Assert.Equal((byte)7, Assert.IsType<SharedReadModel.Node>(first.State).Value);
        }
        Assert.Equal(1, allocations);
        Assert.Equal(1, hydrations);
        PreparedStateRestoration prepared = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
        resources.Schemas.Register(new DurableSchema(dependencyId, 1, SchemaKind.InlineValue,
            new DurableFieldInfo(1, TypeTag.Int64)));
        GraphReadStatistics statistics = new();
        repository.RestorationStatistics = statistics;

        Assert.Throws<InvalidDataException>(() => repository.Fork("rejected", main.Head));
        Assert.Equal(1, statistics.PreparedStateHits);
        Assert.Equal(0, statistics.DecodedObjects);
        Assert.Equal(0, statistics.NormalizedObjects);
        Assert.Equal(0, statistics.AllocatedObjects);
        Assert.Equal(1, allocations);
        Assert.Equal(1, hydrations);
        Assert.Same(prepared, repository.PreparedStateEntry);
        Assert.DoesNotContain("rejected", repository.ListBranches());
        Assert.False(repository.IsFaulted);
    }

    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
