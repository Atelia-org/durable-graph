using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class GraphRefTests : IDisposable {
    private const string Prefix = "dg-graph-ref-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Fact]
    public void DefinitionExportsAnOrdinaryInlineValueWithoutPublicCommitImport() {
        Assert.NotEqual(typeof(GraphRef).Assembly, typeof(GraphRefWorldFixture).Assembly);
        var export = Assert.Single(typeof(GraphRef).Assembly.GetCustomAttributes(typeof(DurableSchemaExportAttribute), false)
            .Cast<DurableSchemaExportAttribute>(), item => item.SchemaId == "Atelia.DurableGraph.GraphRef");
        Assert.Equal(1, export.ContractVersion);
        Assert.Equal(1, export.SchemaVersion);
        var template = Assert.Single(GraphRef.Definition.Templates);
        Assert.Equal(SchemaKind.InlineValue, template.Kind);
        Assert.Equal([1, 2, 3], template.Fields.Select(field => field.FieldId));
        Assert.All(template.Fields, field => Assert.Equal(TypeExpr.Builtin(TypeTag.UInt32), field.ValueType));
        Assert.Empty(typeof(GraphRef).GetConstructors());
        Assert.DoesNotContain(typeof(GraphRef).GetMethods(), method => method.IsStatic && method.ReturnType == typeof(GraphRef));

        GraphRef empty = default;
        Assert.False(empty.IsBound);
        Assert.Throws<InvalidOperationException>(() => empty.CommitId);
        Assert.Throws<ArgumentException>(() => new GraphRef(default));
        Assert.Equal(default(GraphRef), empty);
        Assert.False(empty.Equals(default(EventRef)));
    }

    [Fact]
    public void GeneratedWorldDirectoryPreservesExactValuesWithoutCapturingTargets() {
        GraphRef original;
        GraphRef changed;
        CommitId oldWorld;
        CommitId newWorld;
        GraphRefEntryIdFixture key = new(17);
        using (Repository repository = Repository.CreateNew(_path, Models(includeTarget: true))) {
            BranchInfo target = repository.CreateRootBranchFromState("target", new GraphRefTargetFixture { Text = "outside-world-content" });
            using WorkingCopy targetWork = repository.OpenWorkingCopy(target.Id);
            // Test-only internal construction exercises inline transport independently of
            // the heap API. It deliberately does not certify these ordinary State commits.
            original = new(targetWork.BaseCommitId);
            ((GraphRefTargetFixture)targetWork.State!).Text = "another-target-version";
            changed = new(targetWork.CommitState().Id);
            Assert.True(original.IsBound);
            Assert.Equal(original, new GraphRef(original.CommitId));
            Assert.Equal(original.GetHashCode(), new GraphRef(original.CommitId).GetHashCode());
            Assert.True(original != changed);

            GraphRefWorldFixture world = new() {
                Entries = { [key] = original, [new(18)] = default },
                Optional = original, PresentDefault = default(GraphRef),
                References = [original, changed, default], Array = [default, original]
            };
            BranchInfo branch = repository.CreateRootBranchFromState("world", world);
            using WorkingCopy work = repository.OpenWorkingCopy(branch.Id);
            oldWorld = work.BaseCommitId;
            ((GraphRefWorldFixture)work.State!).Entries[key] = changed;
            newWorld = work.CommitState().Id;
        }

        // The target family is intentionally absent: restoring the world must not
        // resolve targets or import their strings/objects into its live membership.
        using Repository reader = Repository.OpenReadOnlyExisting(_path, Models());
        GraphRefWorldFixture old = Assert.IsType<GraphRefWorldFixture>(reader.LoadState(oldWorld));
        GraphRefWorldFixture current = Assert.IsType<GraphRefWorldFixture>(reader.LoadState(newWorld));
        Assert.Equal(original, old.Entries[key]);
        Assert.Equal(changed, current.Entries[key]);
        Assert.False(current.Entries[new(18)].IsBound);
        Assert.Equal(original, current.Optional);
        Assert.Null(current.Absent);
        Assert.Equal(default(GraphRef), current.PresentDefault);
        Assert.False(current.PresentDefault!.Value.IsBound);
        Assert.Equal([original, changed, default], current.References);
        Assert.Equal([default, original], current.Array);
        Assert.NotSame(old.Entries, current.Entries);

        DecodedRevision decoded = DecodedRevision.ReadStoredGraph(reader.GraphStore, Snapshot(reader, newWorld), Models().FreezeConfiguration());
        Assert.Equal(4, decoded.Objects.Count); // World, directory, List and Array only.
        Assert.DoesNotContain(decoded.Objects, row => row.Kind == ObjectStateKind.String);
        Assert.Equal(0, reader.GraphStore.AppendedFrames);
    }

    [Fact]
    public void RestoringAnInlineReferenceRequiresItsDefinitionButNotItsTarget() {
        CommitId saved;
        using (Repository writer = Repository.CreateNew(_path, Models())) {
            BranchInfo branch = writer.CreateRootBranchFromState("world", new GraphRefWorldFixture {
                Entries = { [new(1)] = default }
            });
            saved = writer.ReadCurrentCommit(branch.Id).Commit.Id;
        }
        using Repository reader = Repository.OpenReadOnlyExisting(_path, Models(includeReference: false));
        Assert.Throws<InvalidDataException>(() => reader.LoadState(saved));
        Assert.False(reader.IsFaulted);
    }

    private static StateModelRegistry Models(bool includeTarget = false, bool includeReference = true) {
        StateModelRegistry models = new();
        models.Register(global::Atelia.DurableGraph.Generated.Family_475257.Definition);
        models.Register(global::Atelia.DurableGraph.Generated.Family_475245.Definition);
        if (includeTarget) { models.Register(global::Atelia.DurableGraph.Generated.Family_475254.Definition); }
        if (includeReference) { models.Register(GraphRef.Definition); }
        return models;
    }

    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}

[DurableType("GRW", 1)]
public sealed partial class GraphRefWorldFixture : IDurableObject {
    [DurableField(1)] public Dictionary<GraphRefEntryIdFixture, GraphRef> Entries = [];
    [DurableField(2)] public GraphRef? Optional;
    [DurableField(3)] public GraphRef? Absent;
    [DurableField(4)] public GraphRef? PresentDefault;
    [DurableField(5)] public List<GraphRef> References = [];
    [DurableField(6)] public GraphRef[] Array = [];
}

[DurableType("GRE", 1)]
public readonly partial struct GraphRefEntryIdFixture(uint value) : IEquatable<GraphRefEntryIdFixture> {
    [DurableField(1)] private readonly uint _value = value;
    public bool Equals(GraphRefEntryIdFixture other) => _value == other._value;
    public override bool Equals(object? obj) => obj is GraphRefEntryIdFixture other && Equals(other);
    public override int GetHashCode() => _value.GetHashCode();
}

[DurableType("GRT", 1)]
public sealed partial class GraphRefTargetFixture : IDurableObject {
    [DurableField(1)] public string? Text;
}
