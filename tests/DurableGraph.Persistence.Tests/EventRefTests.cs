using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class EventRefTests : IDisposable {
    private const string Prefix = "dg-event-ref-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Fact]
    public void PublicInlineDefinitionRoundTripsFieldsNullableListsArraysAndDefaultAcrossAssemblies() {
        Assert.NotEqual(typeof(EventRef).Assembly, typeof(DB099RefOwner).Assembly);
        var export = Assert.Single(typeof(EventRef).Assembly.GetCustomAttributes(typeof(DurableSchemaExportAttribute), false)
            .Cast<DurableSchemaExportAttribute>(), export => export.SchemaId == "Atelia.DurableGraph.EventRef");
        Assert.Equal(1, export.ContractVersion);
        Assert.Equal(1, export.SchemaVersion);
        var template = Assert.Single(EventRef.Definition.Templates);
        Assert.Equal(SchemaKind.InlineValue, template.Kind);
        Assert.Equal([1, 2, 3], template.Fields.Select(field => field.FieldId));
        Assert.All(template.Fields, field => Assert.Equal(TypeExpr.Builtin(TypeTag.UInt32), field.ValueType));

        CommitId state;
        EventRef reference;
        using (Repository repository = Repository.CreateNew(_path, Models())) {
            BranchInfo branch = repository.CreateRootBranchFromEvent("main", new DB099RefEvent { Value = 7, Text = "event-only-body" });
            using WorkingCopy work = repository.OpenWorkingCopy(branch.Id);
            reference = EventRef.FromCommit(repository.ReadCurrentCommit(branch.Id).Commit);
            Assert.True(reference.IsBound);
            Assert.Equal(work.BaseCommitId, reference.ToCommitId());
            state = work.CommitState(new DB099RefOwner {
                Reference = reference, Optional = reference, References = [reference, default],
                Array = [default, reference], PresentDefault = default(EventRef)
            }).Id;
        }

        using Repository reader = Repository.OpenReadOnlyExisting(_path, Models());
        DB099RefOwner owner = Assert.IsType<DB099RefOwner>(reader.LoadState(state));
        Assert.Equal(reference, owner.Reference);
        Assert.Equal(reference, owner.Optional);
        Assert.Null(owner.Absent);
        Assert.Equal(default(EventRef), owner.PresentDefault);
        Assert.False(owner.PresentDefault!.Value.IsBound);
        Assert.Equal([reference, default], owner.References);
        Assert.Equal([default, reference], owner.Array);
        Assert.Equal(7, reader.LoadEvent<DB099RefEvent>(owner.Reference).Value);
        Assert.Equal("event-only-body", reader.LoadEvent<DB099RefEvent>(owner.Optional!.Value).Text);
        DecodedRevision decoded = DecodedRevision.ReadStoredGraph(reader.GraphStore, Snapshot(reader, state), Models().FreezeConfiguration());
        Assert.DoesNotContain(decoded.Objects, row => row.Kind == ObjectStateKind.String);
        Assert.Equal(0, reader.GraphStore.AppendedFrames);
    }

    [Fact]
    public void DefaultAndWrongRoleAreRejectedOnlyWhenTheirMeaningIsRequired() {
        EventRef empty = default;
        Assert.False(empty.IsBound);
        Assert.Equal(empty, default(EventRef));
        Assert.Throws<InvalidOperationException>(() => empty.ToCommitId());
        Assert.Throws<ArgumentNullException>(() => EventRef.FromCommit(null!));
        Assert.Throws<ArgumentException>(() => EventRef.FromCommit(new CommitInfo(default, CommitKind.Event, default)));

        using Repository repository = Repository.CreateNew(_path, Models());
        BranchInfo branch = repository.CreateRootBranchFromState("main", new DB099RefOwner());
        using WorkingCopy work = repository.OpenWorkingCopy(branch.Id);
        CommitInfo state = repository.ReadCurrentCommit(branch.Id).Commit;
        Assert.Throws<ArgumentException>(() => EventRef.FromCommit(state));
        Assert.Throws<InvalidOperationException>(() => repository.LoadEvent(empty));
        Assert.Throws<InvalidOperationException>(() => repository.LoadEvent<DB099RefEvent>(empty));

        // Test-only forged descriptor: ordinary value capture does not authenticate its target.
        EventRef wrongRole = EventRef.FromCommit(new CommitInfo(state.Id, CommitKind.Event, default));
        DB099RefOwner owner = Assert.IsType<DB099RefOwner>(work.State);
        owner.Reference = wrongRole;
        CommitId saved = work.CommitState().Id;
        DB099RefOwner restored = Assert.IsType<DB099RefOwner>(repository.LoadState(saved));
        Assert.Equal(wrongRole, restored.Reference);
        Assert.Throws<ArgumentException>(() => repository.LoadEvent(restored.Reference));
        Assert.False(repository.IsFaulted);
    }

    [Fact]
    public void RepeatedReadsRestoreIndependentSavedContentAndCheckTheResultType() {
        using Repository repository = Repository.CreateNew(_path, Models());
        DB099RefEvent original = new() { Value = 7, Text = "captured" };
        BranchInfo branch = repository.CreateRootBranchFromEvent("main", original);
        EventRef reference = EventRef.FromCommit(repository.ReadCurrentCommit(branch.Id).Commit);
        Assert.Equal(reference, EventRef.FromCommit(repository.ReadCommitInfo(reference.ToCommitId())));
        Assert.Equal(reference.GetHashCode(), EventRef.FromCommit(repository.ReadCommitInfo(reference.ToCommitId())).GetHashCode());
        original.Value = 99;
        original.Text = "changed after capture";

        DB099RefEvent first = repository.LoadEvent<DB099RefEvent>(reference);
        Assert.Equal(7, first.Value);
        Assert.Equal("captured", first.Text);
        first.Value = 88;
        first.Text = "changed by reader";
        DB099RefEvent second = Assert.IsType<DB099RefEvent>(repository.LoadEvent(reference));
        Assert.NotSame(first, second);
        Assert.Equal(7, second.Value);
        Assert.Equal("captured", second.Text);
        Assert.IsType<DB099RefEvent>(repository.LoadEvent<IDurableObject>(reference));
        Assert.Throws<InvalidDataException>(() => repository.LoadEvent<DB099RefOwner>(reference));
        Assert.False(repository.IsFaulted);
    }

    [Fact]
    public void OwnerLoadsWithoutTargetBindingButResolvingRequiresTheTargetAndReferenceDefinitions() {
        EventRef reference;
        CommitId state;
        using (Repository repository = Repository.CreateNew(_path, Models())) {
            BranchInfo branch = repository.CreateRootBranchFromEvent("main", new DB099RefEvent { Value = 7 });
            using WorkingCopy work = repository.OpenWorkingCopy(branch.Id);
            reference = EventRef.FromCommit(repository.ReadCurrentCommit(branch.Id).Commit);
            state = work.CommitState(new DB099RefOwner { Reference = reference }).Id;
        }

        using (Repository reader = Repository.OpenReadOnlyExisting(_path, Models(includeEvent: false))) {
            DB099RefOwner owner = Assert.IsType<DB099RefOwner>(reader.LoadState(state));
            Assert.Equal(reference, owner.Reference);
            Assert.Throws<InvalidDataException>(() => reader.LoadEvent(owner.Reference));
            Assert.False(reader.IsFaulted);
        }
        using (Repository reader = Repository.OpenReadOnlyExisting(_path, Models(includeReference: false))) {
            Assert.Throws<InvalidDataException>(() => reader.LoadState(state));
        }
    }

    [Fact]
    public void ExactHistoricalEventUsesItsOwnSchemaAndNormalUpgradePipeline() {
        EventRef reference;
        using (Repository writer = Repository.CreateNew(_path, SharedReadModel.Models())) {
            BranchInfo branch = writer.CreateRootBranchFromEvent("main", new SharedReadModel.Node { Value = 7 });
            reference = EventRef.FromCommit(writer.ReadCurrentCommit(branch.Id).Commit);
        }

        int normalizations = 0;
        StateModelRegistry models = SharedReadModel.Models(currentVersion: 2, normalize: row => {
            Assert.Equal(1, row.Schema!.Version);
            normalizations++;
            return row.GetState<SharedReadModel.State>() with { Value = 8 };
        });
        using Repository reader = Repository.OpenReadOnlyExisting(_path, models);
        Assert.Equal((byte)8, reader.LoadEvent<SharedReadModel.Node>(reference).Value);
        Assert.Equal((byte)8, reader.LoadEvent<SharedReadModel.Node>(reference).Value);
        Assert.Equal(2, normalizations);
        DecodedRevision source = DecodedRevision.ReadStoredGraph(reader.GraphStore,
            Snapshot(reader, reference.ToCommitId()), models.FreezeConfiguration());
        Assert.Equal(1, source.GetRequired(Describe(reader, reference.ToCommitId()).RootId).Schema!.Version);
        Assert.Equal(0, reader.GraphStore.AppendedFrames);
    }

    [Fact]
    public void InvalidSourceReferencesAreRejectedBeforeAnUpgradeCanHideThem() {
        EventRef reference;
        StateModelRegistry corruptWriter = SharedReadModel.Models(prepareBase: (in SharedReadModel.State state) =>
            SharedReadModel.Base(state with { Next = new ObjectId(999) }));
        using (Repository writer = Repository.CreateNew(_path, corruptWriter)) {
            BranchInfo branch = writer.CreateRootBranchFromEvent("main", new SharedReadModel.Node { Value = 7 });
            reference = EventRef.FromCommit(writer.ReadCurrentCommit(branch.Id).Commit);
        }

        int normalizations = 0;
        StateModelRegistry models = SharedReadModel.Models(currentVersion: 2, normalize: row => {
            normalizations++;
            return row.GetState<SharedReadModel.State>() with { Next = default };
        });
        using Repository reader = Repository.OpenReadOnlyExisting(_path, models);
        Assert.Throws<InvalidDataException>(() => reader.LoadEvent(reference));
        Assert.Equal(0, normalizations);
        Assert.Equal(0, reader.GraphStore.AppendedFrames);
    }

    [Fact]
    public void SameRepositoryForkKeepsAncestorEventReferencesAcrossColdContinuation() {
        EventRef reference;
        BranchId fork;
        CommitId forkState;
        using (Repository repository = Repository.CreateNew(_path, Models())) {
            BranchInfo branch = repository.CreateRootBranchFromEvent("main", new DB099RefEvent { Value = 7 });
            using WorkingCopy main = repository.OpenWorkingCopy(branch.Id);
            reference = EventRef.FromCommit(repository.ReadCurrentCommit(branch.Id).Commit);
            CommitId state = main.CommitState(new DB099RefOwner { Reference = reference }).Id;
            fork = repository.ForkBranch(state, "fork").Id;
            using WorkingCopy copied = repository.OpenWorkingCopy(fork);
            DB099RefOwner owner = Assert.IsType<DB099RefOwner>(copied.State);
            Assert.Equal(reference, owner.Reference);
            Assert.NotEqual(fork, repository.ReadCommitInfo(reference.ToCommitId()).CreatedOnBranchId);
            main.CommitEvent(new DB099RefEvent { Value = 99 });
            copied.CommitEvent(new DB099RefEvent { Value = 88 });
            owner.References.Add(reference);
            forkState = copied.CommitState().Id;
            Assert.Equal(7, repository.LoadEvent<DB099RefEvent>(owner.Reference).Value);
        }

        using Repository reopened = Repository.OpenExisting(_path, Models());
        using WorkingCopy work = reopened.OpenWorkingCopy(fork);
        Assert.Equal(forkState, work.BaseCommitId);
        DB099RefOwner restored = Assert.IsType<DB099RefOwner>(work.State);
        Assert.Equal(reference, restored.Reference);
        Assert.Equal(reference, Assert.Single(restored.References));
        Assert.Equal(7, reopened.LoadEvent<DB099RefEvent>(restored.Reference).Value);
    }

    private static StateModelRegistry Models(bool includeEvent = true, bool includeReference = true) {
        StateModelRegistry models = new();
        models.Register(global::Atelia.DurableGraph.Generated.Family_44423039394F776E6572.Definition);
        if (includeEvent) { models.Register(global::Atelia.DurableGraph.Generated.Family_44423039394576656E74.Definition); }
        if (includeReference) { models.Register(EventRef.Definition); }
        return models;
    }

    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}

[DurableType("DB099Owner", 1)]
public sealed partial class DB099RefOwner : IDurableObject {
    [DurableField(1)] public EventRef Reference;
    [DurableField(2)] public EventRef? Optional;
    [DurableField(3)] public List<EventRef> References = [];
    [DurableField(4)] public EventRef[] Array = [];
    [DurableField(5)] public EventRef? Absent;
    [DurableField(6)] public EventRef? PresentDefault;
}

[DurableType("DB099Event", 1)]
public sealed partial class DB099RefEvent : IDurableObject {
    [DurableField(1)] public int Value;
    [DurableField(2)] public string? Text;
}
