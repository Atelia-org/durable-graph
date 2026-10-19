using System.Buffers;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Atelia.HeadStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class BranchGroupCommitTests : IDisposable {
    private const string Prefix = "dg-branch-group-commit-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private readonly string _otherPath = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1000000, 1);
    private static readonly DurableSchema HookSchema = new("GroupHook", 1, new DurableFieldInfo(1, TypeTag.Int32));

    [Fact]
    public void Mixed_group_accepts_both_new_schemas_publishes_once_and_preserves_state_event_install_rules() {
        BranchId worldId, playerId;
        CommitId worldGrouped, eventGrouped, worldContinued, playerContinued;
        FrameAddress schema, playerSeed;
        using (Repository repository = Repository.CreateStorageNew(_path, Models())) {
            worldId = Seed(repository, "world", 10);
            playerId = Seed(repository, "player", 20);
            using WorkingCopy world = repository.OpenWorkingCopy(worldId);
            using WorkingCopy player = repository.OpenWorkingCopy(playerId);
            ModelExecutionEnvironment worldModels = world.Models, playerModels = player.Models;
            var worldEnvironment = world.BindingEnvironment;
            var playerEnvironment = player.BindingEnvironment;
            Assert.NotNull(worldEnvironment);
            Assert.NotNull(playerEnvironment);
            RepositoryResources resources = DB096TestAccess.Resources(repository);
            HeadEntryId oldD = resources.Heads.ReadHead(1).Unwrap().Id;
            HeadEntryId oldS = resources.Heads.ReadHead(2).Unwrap().Id;
            CommitId oldWorld = world.BaseCommitId, oldPlayer = player.BaseCommitId;
            CommitId? playerStateSource = player.StateSourceCommitId;
            playerSeed = player.StateRevisionAddress!.Value;
            NormalizedGraphState playerBaseline = player.Baseline!;
            DB087Widget playerRoot = Assert.IsType<DB087Widget>(player.State);
            playerRoot.Numbers[2] = 900; // Event publication must leave this edit unsaved.
            BranchGroupState nextWorld = new() { Value = 58, Numbers = Enumerable.Repeat(0, 256).ToList() };
            BranchGroupEvent nextEvent = new() { Value = 71 };
            Assert.DoesNotContain(new SchemaKey("GroupState", 1), world.AcceptedCatalog!.Schemas.Keys);
            Assert.DoesNotContain(new SchemaKey("GroupEvent", 1), world.AcceptedCatalog!.Schemas.Keys);
            int publications = 0;
            repository.SaveStageObserver = stage => {
                if (stage == RepositorySaveStage.BeforePublication) { publications++; }
            };
            IReadOnlyDictionary<BranchId, CommitInfo> result = repository.CommitBranches([
                world.CreateStateCommitRequest(nextWorld, new byte[] { 1 }, NoRebase),
                player.CreateEventCommitRequest(nextEvent, new byte[] { 2 }, NoRebase)
            ]);
            repository.SaveStageObserver = null;
            Assert.Equal(1, publications);
            Assert.Equal(2, result.Count);
            worldGrouped = result[worldId].Id;
            eventGrouped = result[playerId].Id;
            Assert.NotEqual(oldWorld, worldGrouped);
            Assert.NotEqual(oldPlayer, eventGrouped);
            Assert.Equal(CommitKind.State, result[worldId].Kind);
            Assert.Equal(CommitKind.Event, result[playerId].Kind);
            Assert.Equal(worldId, result[worldId].CreatedOnBranchId);
            Assert.Equal(playerId, result[playerId].CreatedOnBranchId);
            Assert.Equal(worldGrouped, world.BaseCommitId);
            Assert.Equal(worldGrouped, world.StateSourceCommitId);
            Assert.Same(nextWorld, world.State);
            Assert.Equal(eventGrouped, player.BaseCommitId);
            Assert.Equal(playerStateSource, player.StateSourceCommitId);
            Assert.Equal(playerSeed, player.StateRevisionAddress);
            Assert.Same(playerRoot, player.State);
            Assert.Same(playerBaseline, player.Baseline);
            Assert.Equal(900, playerRoot.Numbers[2]);
            Assert.Equal(20, Assert.IsType<DB087Widget>(repository.LoadState(oldPlayer)).Numbers[2]);
            Assert.Equal(oldD, resources.Heads.ReadHead(1).Unwrap().Id);
            Assert.NotEqual(oldS, resources.Heads.ReadHead(2).Unwrap().Id);
            schema = repository.SharedSchemaRoot!.Value;
            Assert.Same(world.AcceptedCatalog, player.AcceptedCatalog);
            Assert.Same(worldModels, world.Models);
            Assert.Same(playerModels, player.Models);
            Assert.Same(worldEnvironment, world.BindingEnvironment);
            Assert.Same(playerEnvironment, player.BindingEnvironment);
            Assert.Contains(new SchemaKey("GroupState", 1), world.AcceptedCatalog!.Schemas.Keys);
            Assert.Contains(new SchemaKey("GroupEvent", 1), world.AcceptedCatalog!.Schemas.Keys);
            Assert.NotEqual(world.AcceptedCatalog!.DescriptorId(ObjectLayout.ForDurable(
                    world.AcceptedCatalog!.Schemas[new("GroupState", 1)])),
                world.AcceptedCatalog!.DescriptorId(ObjectLayout.ForDurable(
                    world.AcceptedCatalog!.Schemas[new("GroupEvent", 1)])));
            Assert.Equal(schema, DB096TestAccess.Snapshot(repository, worldGrouped).SchemaRoot);
            Assert.Equal(schema, DB096TestAccess.Snapshot(repository, eventGrouped).SchemaRoot);
            Assert.Equal(71, Assert.IsType<BranchGroupEvent>(repository.LoadEvent(eventGrouped)).Value);
            nextWorld.Numbers[3] = 43;
            worldContinued = SaveListDelta(repository, world, () => world.CommitState(parameters: NoRebase));
            Assert.Equal(oldD, resources.Heads.ReadHead(1).Unwrap().Id);
            Assert.Equal(schema, repository.SharedSchemaRoot);
        }
        using (Repository repository = Repository.OpenStorageExisting(_path, Models())) {
            Assert.Equal(schema, repository.SharedSchemaRoot);
            SchemaCatalogView catalog = SchemaCatalogView.Load(repository.GraphStore, schema);
            Assert.Contains(new SchemaKey("GroupState", 1), catalog.Schemas.Keys);
            Assert.Contains(new SchemaKey("GroupEvent", 1), catalog.Schemas.Keys);
            Assert.Equal(58, Assert.IsType<BranchGroupState>(repository.LoadState(worldGrouped)).Value);
            Assert.Equal(71, Assert.IsType<BranchGroupEvent>(repository.LoadEvent(eventGrouped)).Value);
            using WorkingCopy world = repository.OpenWorkingCopy(worldId);
            using WorkingCopy player = repository.OpenWorkingCopy(playerId);
            Assert.Equal(worldContinued, world.BaseCommitId);
            Assert.Equal(43, Assert.IsType<BranchGroupState>(world.State).Numbers[3]);
            Assert.Equal(eventGrouped, player.BaseCommitId);
            Assert.Equal(playerSeed, player.StateRevisionAddress);
            DB087Widget playerRoot = Assert.IsType<DB087Widget>(player.State);
            Assert.Equal(20, playerRoot.Numbers[2]);
            playerRoot.Numbers[4] = 24;
            playerContinued = SaveListDelta(repository, player, () => player.CommitState(parameters: NoRebase));
            Assert.Equal(eventGrouped, DB096TestAccess.Previous(repository, playerContinued));
            Assert.Equal(schema, repository.SharedSchemaRoot);
        }
        using Repository fresh = Repository.OpenStorageReadOnlyExisting(_path, Models());
        Assert.Equal(43, Assert.IsType<BranchGroupState>(fresh.LoadState(worldContinued)).Numbers[3]);
        Assert.Equal(24, Assert.IsType<DB087Widget>(fresh.LoadState(playerContinued)).Numbers[4]);
        Assert.Equal(20, Assert.IsType<DB087Widget>(fresh.LoadState(playerContinued)).Numbers[2]);
    }

    [Fact]
    public void Requests_defer_capture_but_own_summary_bytes_and_become_stale_after_success() {
        using Repository repository = Repository.CreateStorageNew(_path, Models());
        using WorkingCopy work = repository.OpenWorkingCopy(Seed(repository, "main"));
        int captures = 0;
        HookRoot root = new() { Value = 1, OnCapture = () => captures++ };
        byte[] summary = [1, 2];
        long appended = repository.GraphStore.AppendedFrames;
        CommitId oldBase = work.BaseCommitId;
        CommitRequest request = work.CreateStateCommitRequest(root, summary);
        Assert.Equal(0, captures);
        Assert.Equal(appended, repository.GraphStore.AppendedFrames);
        Assert.Equal(oldBase, work.BaseCommitId);
        root.Value = 9;
        summary[0] = 8;
        CommitInfo result = repository.CommitBranches([request])[work.BranchId];
        Assert.Equal(1, captures);
        Assert.Equal(new byte[] { 1, 2 }, result.Summary.ToArray());
        Assert.Equal(9, Assert.IsType<HookRoot>(repository.LoadState(result.Id)).Value);
        Assert.Same(root, work.State);
        Assert.Throws<InvalidOperationException>(() => repository.CommitBranches([request]));
        Assert.Equal(1, captures);
        Assert.Equal(result.Id, work.BaseCommitId);
        Assert.False(repository.IsFaulted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Independent_state_or_event_commit_invalidates_old_request_before_capture(bool eventCommit) {
        using Repository repository = Repository.CreateStorageNew(_path, Models());
        using WorkingCopy work = repository.OpenWorkingCopy(Seed(repository, "main"));
        int captures = 0;
        CommitRequest request = work.CreateStateCommitRequest(new HookRoot { OnCapture = () => captures++ });
        CommitInfo advanced = eventCommit ? work.CommitEvent(new DB087Widget()) : work.CommitState();
        long appended = repository.GraphStore.AppendedFrames;
        Assert.Throws<InvalidOperationException>(() => repository.CommitBranches([request]));
        Assert.Equal(0, captures);
        Assert.Equal(appended, repository.GraphStore.AppendedFrames);
        Assert.Equal(advanced.Id, work.BaseCommitId);
        Assert.False(repository.IsFaulted);
    }

    [Theory]
    [InlineData("null-collection")]
    [InlineData("empty")]
    [InlineData("null-member")]
    [InlineData("duplicate")]
    [InlineData("foreign")]
    [InlineData("disposed")]
    [InlineData("reopened-copy")]
    public void Whole_input_is_validated_before_the_first_capture(string invalid) {
        using Repository repository = Repository.CreateStorageNew(_path, Models());
        using WorkingCopy first = repository.OpenWorkingCopy(Seed(repository, "first"));
        BranchId secondId = Seed(repository, "second");
        using WorkingCopy second = repository.OpenWorkingCopy(secondId);
        using Repository foreign = Repository.CreateStorageNew(_otherPath, Models());
        using WorkingCopy foreignWork = foreign.OpenWorkingCopy(Seed(foreign, "foreign"));
        int captures = 0;
        CommitRequest good = first.CreateStateCommitRequest(new HookRoot { OnCapture = () => captures++ });
        CommitRequest other = second.CreateStateCommitRequest(new HookRoot { OnCapture = () => captures++ });
        IEnumerable<CommitRequest> requests = invalid switch {
            "null-collection" => null!,
            "empty" => [],
            "null-member" => [good, null!],
            "duplicate" => [good, first.CreateEventCommitRequest(new HookRoot { OnCapture = () => captures++ })],
            "foreign" => [good, foreignWork.CreateStateCommitRequest(new HookRoot { OnCapture = () => captures++ })],
            _ => [good, other]
        };
        WorkingCopy? reopened = null;
        if (invalid is "disposed" or "reopened-copy") {
            CommitId unchanged = second.BaseCommitId;
            second.Dispose();
            if (invalid == "reopened-copy") {
                reopened = repository.OpenWorkingCopy(secondId);
                Assert.Equal(unchanged, reopened.BaseCommitId);
            }
        }
        try {
            CommitId firstBase = first.BaseCommitId;
            CommitId secondBase = repository.ReadCurrentCommit(secondId).Commit.Id;
            long appended = repository.GraphStore.AppendedFrames;
            if (invalid is "null-collection" or "null-member") {
                Assert.Throws<ArgumentNullException>(() => repository.CommitBranches(requests));
            } else if (invalid is "empty" or "duplicate") {
                Assert.Throws<ArgumentException>(() => repository.CommitBranches(requests));
            } else {
                Assert.Throws<InvalidOperationException>(() => repository.CommitBranches(requests));
            }
            Assert.Equal(0, captures);
            Assert.Equal(appended, repository.GraphStore.AppendedFrames);
            Assert.Equal(firstBase, first.BaseCommitId);
            Assert.Equal(secondBase, repository.ReadCurrentCommit(secondId).Commit.Id);
            Assert.False(repository.IsFaulted);
        } finally { reopened?.Dispose(); }
    }

    [Fact]
    public void Second_capture_failure_keeps_owner_healthy_and_same_requests_recapture_current_values() {
        using Repository repository = Repository.CreateStorageNew(_path, Models());
        using WorkingCopy first = repository.OpenWorkingCopy(Seed(repository, "first"));
        using WorkingCopy second = repository.OpenWorkingCopy(Seed(repository, "second"));
        int firstCaptures = 0, secondCaptures = 0;
        bool fail = true;
        InvalidOperationException injected = new("second capture");
        HookRoot firstRoot = new() { Value = 1, OnCapture = () => firstCaptures++ };
        HookRoot secondRoot = new() { Value = 2, OnCapture = () => { secondCaptures++; if (fail) { throw injected; } } };
        CommitRequest firstRequest = first.CreateStateCommitRequest(firstRoot);
        CommitRequest secondRequest = second.CreateEventCommitRequest(secondRoot);
        CommitId firstBase = first.BaseCommitId, secondBase = second.BaseCommitId;
        FrameAddress? schema = repository.SharedSchemaRoot;
        long appended = repository.GraphStore.AppendedFrames;
        Assert.Same(injected, Assert.Throws<InvalidOperationException>(() => repository.CommitBranches([firstRequest, secondRequest])));
        Assert.Equal(1, firstCaptures);
        Assert.Equal(1, secondCaptures);
        Assert.Equal(firstBase, first.BaseCommitId);
        Assert.Equal(secondBase, second.BaseCommitId);
        Assert.Equal(schema, repository.SharedSchemaRoot);
        Assert.Equal(appended, repository.GraphStore.AppendedFrames);
        Assert.False(repository.IsFaulted);
        fail = false;
        firstRoot.Value = 11;
        secondRoot.Value = 22;
        var result = repository.CommitBranches([firstRequest, secondRequest]);
        Assert.Equal(2, firstCaptures);
        Assert.Equal(2, secondCaptures);
        Assert.Equal(11, Assert.IsType<HookRoot>(repository.LoadState(result[first.BranchId].Id)).Value);
        Assert.Equal(22, Assert.IsType<HookRoot>(repository.LoadEvent(result[second.BranchId].Id)).Value);
        Assert.Equal(secondBase, second.StateSourceCommitId);
        Assert.False(repository.IsFaulted);
    }

    [Fact]
    public void Group_declaration_conflicting_with_captured_layout_cleans_all_captures_before_append() {
        using Repository repository = Repository.CreateStorageNew(_path, Models());
        using WorkingCopy first = repository.OpenWorkingCopy(Seed(repository, "first"));
        using WorkingCopy second = repository.OpenWorkingCopy(Seed(repository, "second"));
        int firstCaptures = 0, secondCaptures = 0;
        HookRoot firstRoot = new() { Value = 1, OnCapture = () => firstCaptures++ };
        HookRoot secondRoot = new() { Value = 2, OnCapture = () => secondCaptures++ };
        CommitRequest firstRequest = first.CreateStateCommitRequest(firstRoot);
        CommitRequest secondRequest = second.CreateEventCommitRequest(secondRoot);
        CommitId firstBase = first.BaseCommitId, secondBase = second.BaseCommitId;
        NormalizedGraphState firstBaseline = first.Baseline!, secondBaseline = second.Baseline!;
        SchemaCatalogView catalog = first.AcceptedCatalog!;
        RepositoryResources resources = DB096TestAccess.Resources(repository);
        HeadEntryId oldD = resources.Heads.ReadHead(1).Unwrap().Id;
        HeadEntryId oldS = resources.Heads.ReadHead(2).Unwrap().Id;
        FrameAddress? schema = repository.SharedSchemaRoot;
        long appended = repository.GraphStore.AppendedFrames;
        // Registry forbids two different current models for the same TypeExpr.
        // This explicitly tests a group's declaration versus its captured layouts,
        // not two independently registered models with the same Schema key.
        DurableSchema conflicting = new("GroupHook", 1, new DurableFieldInfo(1, TypeTag.Int64));
        Assert.Throws<SchemaConflictException>(() => repository.CommitBranches([firstRequest, secondRequest], [conflicting]));
        Assert.Equal(1, firstCaptures);
        Assert.Equal(1, secondCaptures);
        Assert.Equal(appended, repository.GraphStore.AppendedFrames);
        Assert.Equal(schema, repository.SharedSchemaRoot);
        Assert.Equal(firstBase, first.BaseCommitId);
        Assert.Equal(secondBase, second.BaseCommitId);
        Assert.Same(firstBaseline, first.Baseline);
        Assert.Same(secondBaseline, second.Baseline);
        Assert.Equal(firstBase, repository.ReadCurrentCommit(first.BranchId).Commit.Id);
        Assert.Equal(secondBase, repository.ReadCurrentCommit(second.BranchId).Commit.Id);
        Assert.Equal(oldD, resources.Heads.ReadHead(1).Unwrap().Id);
        Assert.Equal(oldS, resources.Heads.ReadHead(2).Unwrap().Id);
        Assert.Same(catalog, first.AcceptedCatalog);
        Assert.DoesNotContain(new SchemaKey("GroupHook", 1), catalog.Schemas.Keys);
        Assert.False(repository.IsFaulted);
        firstRoot.Value = 10;
        secondRoot.Value = 20;
        var result = repository.CommitBranches([firstRequest, secondRequest]);
        Assert.Equal(2, firstCaptures);
        Assert.Equal(2, secondCaptures);
        Assert.Equal(10, Assert.IsType<HookRoot>(repository.LoadState(result[first.BranchId].Id)).Value);
        Assert.Equal(20, Assert.IsType<HookRoot>(repository.LoadEvent(result[second.BranchId].Id)).Value);
        Assert.False(repository.IsFaulted);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(5, 0)]
    [InlineData(5, 101)]
    public void Invalid_second_member_policy_is_rejected_before_first_capture(int threshold, int percent) {
        using Repository repository = Repository.CreateStorageNew(_path, Models());
        using WorkingCopy first = repository.OpenWorkingCopy(Seed(repository, "first"));
        using WorkingCopy second = repository.OpenWorkingCopy(Seed(repository, "second"));
        int captures = 0;
        CommitRequest firstRequest = first.CreateStateCommitRequest(new HookRoot { OnCapture = () => captures++ });
        CommitRequest secondRequest = second.CreateEventCommitRequest(new HookRoot { OnCapture = () => captures++ },
            parameters: new(threshold, percent));
        CommitId firstBase = first.BaseCommitId, secondBase = second.BaseCommitId;
        FrameAddress? schema = repository.SharedSchemaRoot;
        long appended = repository.GraphStore.AppendedFrames;
        Assert.Throws<ArgumentOutOfRangeException>(() => repository.CommitBranches([firstRequest, secondRequest]));
        Assert.Equal(0, captures);
        Assert.Equal(appended, repository.GraphStore.AppendedFrames);
        Assert.Equal(schema, repository.SharedSchemaRoot);
        Assert.Equal(firstBase, first.BaseCommitId);
        Assert.Equal(secondBase, second.BaseCommitId);
        Assert.False(repository.IsFaulted);
    }

    [Fact]
    public void Other_branch_catalog_growth_does_not_expire_a_request_with_unchanged_base() {
        using Repository repository = Repository.CreateStorageNew(_path, Models());
        using WorkingCopy first = repository.OpenWorkingCopy(Seed(repository, "first"));
        using WorkingCopy second = repository.OpenWorkingCopy(Seed(repository, "second"));
        CommitId firstBase = first.BaseCommitId;
        ModelExecutionEnvironment firstModels = first.Models, secondModels = second.Models;
        var firstEnvironment = first.BindingEnvironment;
        var secondEnvironment = second.BindingEnvironment;
        CommitRequest request = first.CreateStateCommitRequest(new BranchGroupState { Value = 7 });
        second.CommitEvent(new BranchGroupEvent { Value = 8 });
        Assert.Equal(firstBase, first.BaseCommitId);
        CommitInfo result = repository.CommitBranches([request])[first.BranchId];
        Assert.Equal(7, Assert.IsType<BranchGroupState>(repository.LoadState(result.Id)).Value);
        Assert.Contains(new SchemaKey("GroupState", 1), first.AcceptedCatalog!.Schemas.Keys);
        Assert.Contains(new SchemaKey("GroupEvent", 1), first.AcceptedCatalog!.Schemas.Keys);
        Assert.Same(first.AcceptedCatalog, second.AcceptedCatalog);
        Assert.Same(firstModels, first.Models);
        Assert.Same(secondModels, second.Models);
        Assert.Same(firstEnvironment, first.BindingEnvironment);
        Assert.Same(secondEnvironment, second.BindingEnvironment);
    }

    [Fact]
    public void Event_only_branches_can_jointly_commit_first_state_and_another_event() {
        using Repository repository = Repository.CreateStorageNew(_path, Models());
        using WorkingCopy first = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("first", new DB087Widget()).Id);
        using WorkingCopy second = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("second", new DB087Widget()).Id);
        Assert.Null(first.State);
        Assert.Null(second.State);
        Assert.Throws<InvalidOperationException>(() => first.CreateStateCommitRequest());
        var result = repository.CommitBranches([
            first.CreateStateCommitRequest(new BranchGroupState { Value = 5 }),
            second.CreateEventCommitRequest(new BranchGroupEvent { Value = 6 })
        ]);
        Assert.Equal(result[first.BranchId].Id, first.StateSourceCommitId);
        Assert.Equal(result[second.BranchId].Id, second.BaseCommitId);
        Assert.Null(second.StateSourceCommitId);
        Assert.Null(second.State);
        Assert.Equal(5, Assert.IsType<BranchGroupState>(repository.LoadState(first.BaseCommitId)).Value);
        Assert.Equal(6, Assert.IsType<BranchGroupEvent>(repository.LoadEvent(second.BaseCommitId)).Value);
    }

    private static BranchId Seed(Repository repository, string label, int value = 1) => repository.CreateRootBranchFromState(label,
        new DB087Widget { Value = value, Numbers = Enumerable.Repeat(value, 256).ToList() }, parameters: NoRebase).Id;

    private static CommitId SaveListDelta(Repository repository, WorkingCopy work, Func<CommitInfo> save) {
        FrameAddress priorGraph = work.StateRevisionAddress!.Value;
        ObjectId list = Assert.Single(work.Baseline!.Objects, pair => pair.Value.Current.Kind == ObjectStateKind.List).Key;
        FrameAddress prior = work.Baseline.Objects[list].Storage!.Value.Head;
        long priorBytes = work.Baseline.Objects[list].Storage!.Value.ReconstructionPayloadBytes;
        CommitInfo commit = save();
        StoredGraphRevision graph = DB096TestAccess.Snapshot(repository, commit.Id);
        Assert.Equal(priorGraph, graph.SourceGraph);
        ObjectVersionRecord delta = Assert.Single(repository.GraphStore.ReadBatch(graph.OwnedBatch!.Value).Records);
        Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
        Assert.Equal(list.Value, delta.ObjectId);
        Assert.Equal(prior, delta.PriorAddress);
        Assert.Equal(priorBytes + ObjectVersionPayloadSize.GetDeltaPayloadBytes(delta.Body.Length, prior),
            work.Baseline!.Objects[list].Storage!.Value.ReconstructionPayloadBytes);
        return commit.Id;
    }

    private static StateModelRegistry Models() {
        StateModelRegistry models = new();
        models.Register(global::Atelia.DurableGraph.Generated.Family_5030576964676574.Definition);
        models.Register(global::Atelia.DurableGraph.Generated.Family_47726F75705374617465.Definition);
        models.Register(global::Atelia.DurableGraph.Generated.Family_47726F75704576656E74.Definition);
        models.Register(new StateModelBinding<HookRoot, int>(new(HookSchema, BaseInt,
            static (in int prior, in int current) => new(prior != current, BaseInt(in current).Body)),
            [new StateReaderBinding<int>(HookSchema, static (ref BareValueReader reader) => reader.ReadVarInt32(),
                static (ref BareValueReader reader, in int _) => reader.ReadVarInt32(), static (in int _, IStateReferenceVisitor _) => { })],
            static row => row.GetState<int>(), static () => new(),
            static (HookRoot root, in int state, ObjectReadTable _) => root.Value = state,
            static (root, _) => { root.OnCapture?.Invoke(); return root.Value; }, static (in int _, IStateReferenceVisitor _) => { }));
        return models;
    }

    private static PreparedBaseBody BaseInt(in int value) {
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        writer.WriteVarInt32(value);
        return new(bytes.WrittenSpan);
    }
    private sealed class HookRoot : IDurableObject { internal int Value; internal Action? OnCapture; }

    public void Dispose() {
        SharedReadModel.DeleteFixture(_path, Prefix);
        SharedReadModel.DeleteFixture(_otherPath, Prefix);
    }
}

[DurableType("GroupState", 1)]
internal sealed partial class BranchGroupState : IDurableObject {
    [DurableField(1)] internal int Value = 0;
    [DurableField(2)] internal List<int> Numbers = [];
}

[DurableType("GroupEvent", 1)]
internal sealed partial class BranchGroupEvent : IDurableObject {
    [DurableField(1)] internal int Value = 0;
}
