using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;
using System.Buffers;
using System.Reflection;
using System.Runtime.CompilerServices;

using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class SplitPreparedStateReuseTests : IDisposable {
    private const string Prefix = "dg-split-prepared-state-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly TypeExpr PaddingType = TypeExpr.List(TypeExpr.Builtin(TypeTag.Int32));
    private static readonly DurableSchema ProbeV1 = ProbeSchema(1), ProbeV2 = ProbeSchema(2);
    private static readonly DurableSchema Extra = new("SplitPreparedExtra", 1, new DurableFieldInfo(1, TypeTag.Int32));
    private static readonly DurableSchema BodyDependency = new("SplitPreparedBodyDependency", 1, SchemaKind.InlineValue,
        new DurableFieldInfo(1, TypeTag.Int32));
    private static readonly DurableSchema WrongBodyDependency = new(BodyDependency.SchemaId, 1, SchemaKind.InlineValue,
        new DurableFieldInfo(1, TypeTag.Int64));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DifferentEventHeadsReuseOneStateButKeepMutableGraphsAndColdDeltaSourcesIndependent(bool reuse) {
        using (Repository seed = Repository.CreateStorageNew(_path, GeneratedModels())) {
            using WorkingCopy main = seed.OpenWorkingCopy(seed.CreateRootBranchFromState("main", Ring()).Id);
            seed.CreateTag("state", main.BaseCommitId);
            seed.CreateTag("event1", main.CommitEvent(new DB087Widget { Value = 51 }).Id);
            main.Catalog.Set(Extra);
            seed.CreateTag("event2", main.CommitEvent(new DB087Widget { Value = 52 }).Id);
        }
        FrameAddress saved;
        ObjectId listId;
        ObjectStorageInfo savedStorage;
        using (Repository repository = Repository.OpenStorageExisting(_path, GeneratedModels())) {
            repository.PreparedStateReuseEnabled = reuse;
            Assert.Null(repository.PreparedStateEntry);
            CommitId state = repository.ResolveTag("state"), e1 = repository.ResolveTag("event1"), e2 = repository.ResolveTag("event2");
            GraphReadStatistics firstStats = Observe(repository);
            using WorkingCopy first = repository.OpenWorkingCopy(repository.ForkBranch(e1, "first").Id);
            AssertCold(firstStats);
            PreparedStateRestoration? entry = repository.PreparedStateEntry;
            GraphReadStatistics secondStats = Observe(repository);
            using WorkingCopy second = repository.OpenWorkingCopy(repository.ForkBranch(e2, "second").Id);
            AssertRepeat(secondStats, reuse);
            DB087Widget firstRoot = AssertRing(first.State), secondRoot = AssertRing(second.State);
            Assert.NotSame(firstRoot, secondRoot);
            Assert.NotSame(firstRoot.Child, secondRoot.Child);
            Assert.NotSame(firstRoot.Numbers, secondRoot.Numbers);
            Assert.Equal(3, secondStats.AllocatedObjects);
            Assert.Equal(firstStats.AllocatedObjects, secondStats.AllocatedObjects);
            Assert.Equal(firstStats.HydratedObjects, secondStats.HydratedObjects);
            Assert.Equal(e1, first.BaseCommitId);
            Assert.Equal(e2, second.BaseCommitId);
            Assert.Equal(Describe(repository, state).Address, second.StateRevisionAddress);
            Assert.Equal(Describe(repository, state).RootId, second.StateId);
            Assert.Equal(repository.GraphStore.ReadMetadata(Describe(repository, state).Address).SchemaRoot,
                second.BindingEnvironment!.SelectedSchemaView.Snapshot!.Address);
            Assert.NotEqual(repository.SharedSchemaRoot, second.BindingEnvironment.SelectedSchemaView.Snapshot.Address);
            Assert.Equal(first.Baseline!.Objects.Keys.OrderBy(id => id.Value), second.Baseline!.Objects.Keys.OrderBy(id => id.Value));
            foreach ((ObjectId id, NormalizedObject row) in first.Baseline.Objects) { Assert.Equal(row.Storage, second.Baseline.Objects[id].Storage); }
            if (reuse) { Assert.Same(entry, repository.PreparedStateEntry); }
            else { Assert.Null(entry); Assert.Null(repository.PreparedStateEntry); }

            listId = Assert.Single(second.Baseline.Objects, row => row.Value.Current.Kind == ObjectStateKind.List).Key;
            ObjectStorageInfo prior = second.Baseline.Objects[listId].Storage!.Value;
            secondRoot.Numbers[7] = 200;
            CommitId next = second.CommitState().Id;
            saved = Describe(repository, next).Address;
            StoredGraphRevision graph = Snapshot(repository, next);
            Assert.Equal(e2.Value, ResourcesOf(repository).ReadPrevious(next.Value)!.Id);
            Assert.Equal(Describe(repository, state).Address, graph.SourceGraph);
            ObjectVersionRecord delta = Assert.Single(repository.GraphStore.ReadBatch(graph.OwnedBatch!.Value).Records);
            Assert.Equal(listId.Value, delta.ObjectId);
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
            Assert.Equal(prior.Head, delta.PriorAddress);
            savedStorage = second.Baseline!.Objects[listId].Storage!.Value;
            Assert.Equal(prior.ReconstructionPayloadBytes + ObjectVersionPayloadSize.GetDeltaPayloadBytes(delta.Body.Length, prior.Head),
                savedStorage.ReconstructionPayloadBytes);
            Assert.Equal(7, firstRoot.Numbers[7]);
            Assert.Equal(Describe(repository, state).Address, first.StateRevisionAddress);
            // A hot confirmed State must not install a new preparation certificate (DB-081).
            Assert.Same(entry, repository.PreparedStateEntry);
            using WorkingCopy replacement = repository.OpenWorkingCopy(repository.ForkBranch(next, "replacement").Id);
            if (reuse) { Assert.NotSame(entry, repository.PreparedStateEntry); }
            firstRoot.Numbers[8] = 201;
            CommitId oldSave = first.CommitState().Id; // Eviction does not invalidate the previously delivered workspace.
            StoredGraphRevision oldGraph = Snapshot(repository, oldSave);
            Assert.Equal(Describe(repository, state).Address, oldGraph.SourceGraph);
            Assert.Equal(e1.Value, ResourcesOf(repository).ReadPrevious(oldSave.Value)!.Id);
            Assert.Equal(prior.Head, Assert.Single(repository.GraphStore.ReadBatch(oldGraph.OwnedBatch!.Value).Records).PriorAddress);
        }
        using Repository reopened = Repository.OpenStorageExisting(_path, GeneratedModels());
        Assert.Null(reopened.PreparedStateEntry);
        using WorkingCopy cold = reopened.OpenWorkingCopy(BranchNamed(reopened, "second"));
        DB087Widget coldRoot = AssertRing(cold.State, changedIndex: 7, changedValue: 200);
        Assert.Equal(saved, cold.StateRevisionAddress);
        Assert.Equal(listId, Assert.Single(cold.Baseline!.Objects, row => row.Value.Current.Kind == ObjectStateKind.List).Key);
        Assert.Equal(savedStorage, cold.Baseline.Objects[listId].Storage);
        coldRoot.Numbers[8] = 202;
        CommitId continued = cold.CommitState().Id;
        StoredGraphRevision continuedGraph = Snapshot(reopened, continued);
        ObjectVersionRecord continuedDelta = Assert.Single(reopened.GraphStore.ReadBatch(continuedGraph.OwnedBatch!.Value).Records);
        Assert.Equal(ObjectVersionKind.Delta, continuedDelta.Kind);
        Assert.Equal(savedStorage.Head, continuedDelta.PriorAddress);
        Assert.Equal(saved, continuedGraph.SourceGraph);
        DB087Widget continuedRead = Assert.IsType<DB087Widget>(reopened.LoadState(continued));
        Assert.Equal(200, continuedRead.Numbers[7]);
        Assert.Equal(202, continuedRead.Numbers[8]);
        AssertRing(reopened.LoadState(reopened.ResolveTag("state")));
    }

    [Fact]
    public void EventOnlyHeadsBypassTheSlotAndReadonlyQueriesNeverSeedOrReplaceIt() {
        using Repository repository = Repository.CreateStorageNew(_path, GeneratedModels());
        using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", Ring()).Id);
        CommitId state = main.BaseCommitId;
        repository.LoadState(state);
        repository.ReadPair(state, state);
        repository.ReadCheckpoint(state);
        PreparedStateRestoration initial = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
        using WorkingCopy warm = repository.OpenWorkingCopy(repository.ForkBranch(state, "warm").Id);
        PreparedStateRestoration entry = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
        using WorkingCopy events = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("events", new DB087Widget { Value = 50 }).Id);
        CommitId e2 = events.CommitEvent(new DB087Widget { Value = 51 }).Id;
        repository.ForkBranch(e2, "event-copy");
        GraphReadStatistics stats = Observe(repository);
        using WorkingCopy emptyFork = repository.OpenWorkingCopy(repository.ForkBranch(e2, "event-fork").Id);
        using WorkingCopy emptyCheckout = repository.OpenWorkingCopy(BranchNamed(repository, "event-copy"));
        Assert.Null(emptyFork.State);
        Assert.Null(emptyCheckout.State);
        Assert.Null(emptyFork.Baseline);
        Assert.Equal(e2, emptyFork.BaseCommitId);
        AssertNoGraphWork(stats);
        Assert.Same(entry, repository.PreparedStateEntry);
        repository.LoadState(state);
        repository.ReadPair(state, state);
        repository.ReadCheckpoint(e2);
        Assert.Same(entry, repository.PreparedStateEntry);
        stats = Observe(repository);
        CommitId firstState = emptyFork.CommitState(Ring()).Id;
        Assert.Same(entry, repository.PreparedStateEntry); // Capture/confirmed save is not cold State admission.
        using WorkingCopy firstStateFork = repository.OpenWorkingCopy(repository.ForkBranch(firstState, "first-state").Id);
        AssertCold(stats);
        Assert.NotSame(entry, repository.PreparedStateEntry);
        Assert.Null(Snapshot(repository, firstState).SourceGraph);
    }

    [Theory]
    [InlineData("body")]
    [InlineData("normalize")]
    [InlineData("hydrate")]
    public void FailedColdReplacementKeepsOldPreparationAndSameBranchRetryRemainsCold(string phase) {
        SeedProbes();
        Controls controls = new();
        using Repository repository = Repository.OpenStorageExisting(_path, ProbeModels(2, controls));
        using WorkingCopy warm = repository.OpenWorkingCopy(repository.ForkBranch(repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id, "warm").Id);
        PreparedStateRestoration entry = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
        controls.FailPhase = phase;
        GraphReadStatistics failed = Observe(repository);
        WorkingCopy? delivered = null;
        Assert.Same(controls.Failure, Assert.Throws<InvalidDataException>(() => delivered = repository.OpenWorkingCopy(repository.ForkBranch(repository.ReadCurrentCommit(BranchNamed(repository, "other")).Commit.Id, "retry").Id)));
        Assert.Null(delivered);
        Assert.Same(entry, repository.PreparedStateEntry);
        Assert.Equal(1, failed.PreparedStateMisses);
        Assert.Contains(repository.ListBranches(), b => b.DisplayLabel == "retry");
        Assert.False(repository.IsFaulted);
        if (phase is "body" or "normalize") { Assert.Equal(0, failed.AllocatedObjects); }
        controls.FailPhase = null;
        GraphReadStatistics retryStats = Observe(repository);
        using WorkingCopy retry = repository.OpenWorkingCopy(BranchNamed(repository, "retry"));
        AssertCold(retryStats);
        Assert.Equal(8, Assert.IsType<ProbeRoot>(retry.State).Value);
        Assert.NotSame(entry, repository.PreparedStateEntry);
        Assert.Equal(7, Assert.IsType<ProbeRoot>(warm.State).Value);
        CommitId savedWarm = warm.CommitState().Id;
        Assert.Equal(ObjectVersionKind.Base, Assert.Single(repository.GraphStore.ReadBatch(
            Snapshot(repository, savedWarm).OwnedBatch!.Value).Records).Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForkPublicationFailureDoesNotInstallUndeliveredStateMaterial(bool afterPublication) {
        SeedProbes();
        using Repository repository = Repository.OpenStorageExisting(_path, ProbeModels(1, new()));
        using WorkingCopy warm = repository.OpenWorkingCopy(repository.ForkBranch(repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id, "warm").Id);
        PreparedStateRestoration previous = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
        var before = ResourcesOf(repository).Heads.ReadHead(1).Unwrap();
        repository.OperationStageObserver = point => {
            if (point == (afterPublication ? RepositoryOperationStage.AfterPublication : RepositoryOperationStage.BeforePublication)) {
                throw new IOException("split preparation Fork publication window");
            }
        };
        WorkingCopy? delivered = null;
        if (afterPublication) {
            RepositoryPublicationException error = Assert.Throws<RepositoryPublicationException>(() => delivered = repository.OpenWorkingCopy(repository.ForkBranch(repository.ReadCurrentCommit(BranchNamed(repository, "other")).Commit.Id, "retry").Id));
            Assert.Equal(RepositoryPublicationOutcome.Published, error.Outcome);
        } else {
            Assert.Throws<IOException>(() => delivered = repository.OpenWorkingCopy(repository.ForkBranch(repository.ReadCurrentCommit(BranchNamed(repository, "other")).Commit.Id, "retry").Id));
        }
        Assert.Null(delivered);
        Assert.Equal(afterPublication, repository.IsFaulted);
        repository.OperationStageObserver = null;
        if (afterPublication) {
            Assert.Null(repository.PreparedStateEntry);
            Assert.Throws<InvalidOperationException>(() => warm.CommitState());
        } else {
            Assert.Same(previous, repository.PreparedStateEntry);
            var unchanged = ResourcesOf(repository).Heads.ReadHead(1).Unwrap();
            Assert.Equal(before.Id, unchanged.Id);
            Assert.Equal(before.Payload.ToArray(), unchanged.Payload.ToArray());
            GraphReadStatistics stats = Observe(repository);
            using WorkingCopy retry = repository.OpenWorkingCopy(repository.ForkBranch(repository.ReadCurrentCommit(BranchNamed(repository, "other")).Commit.Id, "retry").Id);
            AssertCold(stats);
            Assert.NotSame(previous, repository.PreparedStateEntry);
        }
    }

    [Fact]
    public void BodyOnlyRequirementsAndExactOwnerConfigurationModelsAndSchemaAreCheckedBeforeAllocation() {
        SeedProbes();
        string clone = _path + "-clone";
        Directory.CreateDirectory(clone);
        foreach (string directory in Directory.EnumerateDirectories(_path, "*", SearchOption.AllDirectories)) {
            Directory.CreateDirectory(Path.Combine(clone, Path.GetRelativePath(_path, directory)));
        }
        foreach (string file in Directory.EnumerateFiles(_path, "*", SearchOption.AllDirectories)) {
            File.Copy(file, Path.Combine(clone, Path.GetRelativePath(_path, file)));
        }
        Controls controls = new() { CheckBodyDependency = true };
        StateModelRegistry registry = ProbeModels(1, controls);
        try {
            using Repository repository = Repository.OpenStorageExisting(_path, registry);
            CommitId state = repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id;
            using WorkingCopy warm = repository.OpenWorkingCopy(repository.ForkBranch(state, "warm").Id);
            PreparedStateRestoration prepared = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
            Assert.True(controls.BodyDependencyChecks > 0);
            Assert.DoesNotContain(prepared.Selection.Normalized.Objects.Values, row => row.SourceLayout.Schema?.SchemaId == BodyDependency.SchemaId ||
                row.Current.Schema?.SchemaId == BodyDependency.SchemaId);
            var configuration = ConfigurationOf(repository);
            GraphReadContext context = new(prepared.Selection.Models);
            prepared.ValidateStoredSource(repository.GraphStore, configuration, Describe(repository, state), null, context);
            using Repository foreign = Repository.OpenStorageReadOnlyExisting(clone, registry);
            Assert.Equal(Describe(foreign, state).Address, Describe(foreign, foreign.ReadCurrentCommit(BranchNamed(foreign, "main")).Commit.Id).Address); // Physical addresses match in another actual owner.
            BusinessPersistenceAdapter foreignBusiness = BusinessPersistenceAdapter.LoadState(foreign.GraphStore, Snapshot(foreign, state), configuration);
            long appended = repository.GraphStore.AppendedFrames;
            using RepositoryStorageFacade borrowed = new(ResourcesOf(repository));
            Assert.Throws<InvalidDataException>(() => new BranchSaveSession(borrowed, warm.BranchId.Value, warm.BaseCommitId.Value, configuration, static () => { }, foreignBusiness));
            Assert.Equal(appended, repository.GraphStore.AppendedFrames);
            BusinessPersistenceAdapter ownBusiness = BusinessPersistenceAdapter.LoadState(repository.GraphStore, Snapshot(repository, state), configuration);
            BranchSaveSession positive = new(borrowed, warm.BranchId.Value, warm.BaseCommitId.Value, configuration, static () => { }, ownBusiness);
            Assert.Equal(Describe(foreign, state).Address, positive.StateSource!.Address);
            Assert.Equal(7, Assert.IsType<ProbeRoot>(positive.State).Value);
            Assert.Equal(appended, repository.GraphStore.AppendedFrames);
            int allocations = controls.Allocations, bodyReads = controls.BodyReads;
            Assert.Equal(state, foreign.ReadCurrentCommit(BranchNamed(foreign, "main")).Commit.Id); // A copied repository retains compact IDs; owner qualification remains on snapshots.
            Assert.Throws<InvalidDataException>(() => prepared.ValidateStoredSource(foreign.GraphStore, configuration, Describe(repository, state), null, context));
            Assert.Throws<InvalidDataException>(() => prepared.ValidateStoredSource(repository.GraphStore, registry.FreezeConfiguration(), Describe(repository, state), null, context));
            Assert.Throws<InvalidDataException>(() => prepared.ValidateStoredSource(repository.GraphStore, configuration, Describe(repository, state), null,
                new(configuration.CreateEnvironment())));
            Assert.Equal(allocations, controls.Allocations);
            Assert.Equal(bodyReads, controls.BodyReads);

            warm.Catalog.Set(WrongBodyDependency); // Absent from old S; this new S may legally declare the unknown key.
            CommitId newState = warm.CommitState().Id;
            Assert.Same(prepared, repository.PreparedStateEntry);
            FrameAddress newSchema = repository.GraphStore.ReadMetadata(Describe(repository, newState).Address).SchemaRoot!.Value;
            var wrongEnvironment = DecodedRevision.SchemaViewBindingEnvironment.Load(repository.GraphStore, configuration, newSchema);
            InvalidDataException dependencyError = Assert.Throws<InvalidDataException>(() => prepared.Requirements.Validate(wrongEnvironment.Models));
            Assert.Contains(BodyDependency.SchemaId, dependencyError.Message);
            Assert.Throws<InvalidDataException>(() => prepared.ValidateStoredSource(repository.GraphStore, configuration, Describe(repository, state), null,
                new(wrongEnvironment.Models)));
            Assert.Equal(allocations, controls.Allocations);
            Assert.Equal(bodyReads, controls.BodyReads);
            // This product request selects the real new S. Its body-only dependency fails;
            // an old valid certificate cannot be retagged for the new State.
            WorkingCopy? delivered = null;
            Assert.Throws<InvalidDataException>(() => delivered = repository.OpenWorkingCopy(repository.ForkBranch(newState, "wrong-schema").Id));
            Assert.Null(delivered);
            Assert.Same(prepared, repository.PreparedStateEntry);
            Assert.Contains(repository.ListBranches(), b => b.DisplayLabel == "wrong-schema");
            Assert.Equal(allocations, controls.Allocations);
            Assert.False(repository.IsFaulted);
            GraphReadStatistics hit = Observe(repository);
            using WorkingCopy oldAgain = repository.OpenWorkingCopy(repository.ForkBranch(state, "old-again").Id);
            AssertRepeat(hit, true);
            Assert.Equal(7, Assert.IsType<ProbeRoot>(oldAgain.State).Value);
        } finally { SharedReadModel.DeleteFixture(clone, Prefix); }
    }

    [Fact]
    public void CachedHistoricalRewriteSurvivesDiscardAndEventsUntilStateBaseThenColdContinuesDelta() {
        SeedProbes();
        Controls controls = new();
        using (Repository repository = Repository.OpenStorageExisting(_path, ProbeModels(2, controls))) {
            CommitId original = repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id;
            using WorkingCopy first = repository.OpenWorkingCopy(repository.ForkBranch(original, "first").Id);
            PreparedStateRestoration entry = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
            int normalized = controls.Normalizes;
            int bodyReads = controls.BodyReads;
            GraphReadStatistics hit = Observe(repository);
            using WorkingCopy second = repository.OpenWorkingCopy(repository.ForkBranch(original, "second").Id);
            AssertRepeat(hit, true);
            Assert.Equal(normalized, controls.Normalizes);
            Assert.Equal(bodyReads, controls.BodyReads);
            ProbeRoot root = Assert.IsType<ProbeRoot>(second.State);
            Assert.NotSame(first.State, root);
            Assert.NotSame(Assert.IsType<ProbeRoot>(first.State).Padding, root.Padding);
            ObjectId rootId = second.StateId!.Value;
            Assert.True(second.Baseline!.Objects[rootId].RequiresRewrite);
            StoredGraphRevision inheritedSnapshot = Assert.IsType<StoredGraphRevision>(second.Baseline.SourceSnapshot);
            Assert.Same(entry.Selection.Normalized.SourceSnapshot, inheritedSnapshot);
            Assert.Same(first.Baseline!.SourceSnapshot, inheritedSnapshot);
            Assert.Equal(Describe(repository, original).Address, inheritedSnapshot.Address);
            Assert.Equal(second.BindingEnvironment!.SelectedSchemaView.Snapshot!.Address, inheritedSnapshot.SchemaRoot);
            ObjectStorageInfo oldRoot = second.Baseline.Objects[rootId].Storage!.Value;
            second.PrepareAndDiscard(root);
            Assert.True(second.Baseline.Objects[rootId].RequiresRewrite);
            CommitId e1 = second.CommitEvent(root).Id, e2 = second.CommitEvent(root).Id;
            foreach (CommitId address in new[] { e1, e2 }) {
                StoredGraphRevision graph = Snapshot(repository, address);
                Assert.Equal(Describe(repository, original).Address, graph.SourceGraph);
                Assert.Equal(ObjectVersionKind.Base, Assert.Single(repository.GraphStore.ReadBatch(graph.OwnedBatch!.Value).Records).Kind);
            }
            Assert.True(second.Baseline.Objects[rootId].RequiresRewrite);
            Assert.Equal(oldRoot, second.Baseline.Objects[rootId].Storage);
            Assert.Same(inheritedSnapshot, second.Baseline.SourceSnapshot);
            Assert.Same(entry, repository.PreparedStateEntry);
            CommitId confirmed = second.CommitState().Id;
            StoredGraphRevision written = Snapshot(repository, confirmed);
            ObjectVersionRecord @base = Assert.Single(repository.GraphStore.ReadBatch(written.OwnedBatch!.Value).Records);
            Assert.Equal(ObjectVersionKind.Base, @base.Kind);
            Assert.Equal(rootId.Value, @base.ObjectId);
            Assert.False(second.Baseline!.Objects[rootId].RequiresRewrite);
            StoredGraphRevision installedSnapshot = Assert.IsType<StoredGraphRevision>(second.Baseline.SourceSnapshot);
            Assert.Equal(Describe(repository, confirmed).Address, installedSnapshot.Address);
            Assert.Equal(Describe(repository, original).Address, installedSnapshot.SourceGraph);
            Assert.Equal(written.SchemaRoot, installedSnapshot.SchemaRoot);
            Assert.NotEqual(inheritedSnapshot.SchemaRoot, installedSnapshot.SchemaRoot);
            Assert.Same(entry, repository.PreparedStateEntry); // No hot preparation admission.
            repository.CreateTag("rewritten", confirmed);
            ObjectId listId = Assert.Single(second.Baseline.Objects, row => row.Value.Current.Kind == ObjectStateKind.List).Key;
            ObjectStorageInfo prior = second.Baseline.Objects[listId].Storage!.Value;
            root.Padding[7] = 200;
            CommitId interveningEvent = second.CommitEvent(root).Id;
            Assert.Same(installedSnapshot, second.Baseline.SourceSnapshot);
            CommitId deltaState = second.CommitState().Id;
            StoredGraphRevision deltaGraph = Snapshot(repository, deltaState);
            Assert.Equal(interveningEvent.Value, ResourcesOf(repository).ReadPrevious(deltaState.Value)!.Id);
            Assert.Equal(Describe(repository, confirmed).Address, deltaGraph.SourceGraph);
            Assert.Equal(installedSnapshot.SchemaRoot, deltaGraph.SchemaRoot);
            Assert.Equal(Describe(repository, deltaState).Address, second.Baseline!.SourceSnapshot!.Address);
            ObjectVersionRecord delta = Assert.Single(repository.GraphStore.ReadBatch(deltaGraph.OwnedBatch!.Value).Records);
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
            Assert.Equal(listId.Value, delta.ObjectId);
            Assert.Equal(prior.Head, delta.PriorAddress);
            Assert.Equal(prior.ReconstructionPayloadBytes + ObjectVersionPayloadSize.GetDeltaPayloadBytes(delta.Body.Length, prior.Head),
                second.Baseline!.Objects[listId].Storage!.Value.ReconstructionPayloadBytes);
            Assert.Equal(7, Assert.IsType<ProbeRoot>(first.State).Padding[7]);
        }
        using Repository reopened = Repository.OpenStorageExisting(_path, ProbeModels(2, new()));
        using WorkingCopy cold = reopened.OpenWorkingCopy(BranchNamed(reopened, "second"));
        ProbeRoot coldRoot = Assert.IsType<ProbeRoot>(cold.State);
        Assert.Equal(7, coldRoot.Value);
        Assert.Equal(200, coldRoot.Padding[7]);
        ObjectId coldList = Assert.Single(cold.Baseline!.Objects, row => row.Value.Current.Kind == ObjectStateKind.List).Key;
        ObjectStorageInfo coldPrior = cold.Baseline.Objects[coldList].Storage!.Value;
        coldRoot.Padding[8] = 201;
        CommitId continued = cold.CommitState().Id;
        StoredGraphRevision graphAfterCold = Snapshot(reopened, continued);
        ObjectVersionRecord continuation = Assert.Single(reopened.GraphStore.ReadBatch(graphAfterCold.OwnedBatch!.Value).Records);
        Assert.Equal(ObjectVersionKind.Delta, continuation.Kind);
        Assert.Equal(coldPrior.Head, continuation.PriorAddress);
        Assert.Equal(7, Assert.IsType<ProbeRoot>(reopened.LoadState(reopened.ReadCurrentCommit(BranchNamed(reopened, "main")).Commit.Id)).Padding[7]);
        Assert.Equal(7, Assert.IsType<ProbeRoot>(reopened.LoadState(reopened.ResolveTag("rewritten"))).Padding[7]);
    }

    [Fact]
    public void OptionalLeafReuseBackfillsOnlyAfterSuccessAndRegistersTrustedLeavesBeforeOtherAllocation() {
        LeafControls controls = new();
        using Repository repository = Repository.CreateStorageNew(_path, LeafModels(controls));
        using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", new LeafOwner { First = new(7), Second = new(8) }).Id);
        Assert.False(repository.ImmutableLeafReuseEnabled);
        using WorkingCopy disabled = repository.OpenWorkingCopy(repository.ForkBranch(main.BaseCommitId, "disabled").Id);
        PreparedStateRestoration original = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
        Assert.False(original.ImmutableLeavesCollected);
        repository.ImmutableLeafReuseEnabled = true;
        controls.FailHydrateValue = 8;
        int beforeBackfill = controls.LeafHydrates;
        WorkingCopy? delivered = null;
        Assert.Same(controls.Failure, Assert.Throws<InvalidDataException>(() => delivered = repository.OpenWorkingCopy(repository.ForkBranch(main.BaseCommitId, "enabled").Id)));
        Assert.Null(delivered);
        Assert.Equal(beforeBackfill + 2, controls.LeafHydrates); // First leaf completed; the second leaf rejected backfill.
        Assert.Same(original, repository.PreparedStateEntry);
        Assert.False(original.ImmutableLeavesCollected);
        Assert.Contains(repository.ListBranches(), b => b.DisplayLabel == "enabled");
        Assert.False(repository.IsFaulted);
        controls.FailHydrateValue = null;
        using WorkingCopy enabled = repository.OpenWorkingCopy(BranchNamed(repository, "enabled"));
        PreparedStateRestoration filled = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
        Assert.NotSame(original, filled);
        Assert.True(filled.ImmutableLeavesCollected);
        Assert.Equal(2, filled.ImmutableLeafCount);
        LeafOwner enabledRoot = Assert.IsType<LeafOwner>(enabled.State);
        Assert.NotSame(Assert.IsType<LeafOwner>(disabled.State).First, enabledRoot.First);
        GraphReadStatistics stats = Observe(repository);
        using WorkingCopy hit = repository.OpenWorkingCopy(repository.ForkBranch(main.BaseCommitId, "hit").Id);
        LeafOwner hitRoot = Assert.IsType<LeafOwner>(hit.State);
        Assert.NotSame(enabledRoot, hitRoot);
        Assert.Same(enabledRoot.First, hitRoot.First);
        Assert.Same(enabledRoot.Second, hitRoot.Second);
        Assert.Equal(2, stats.ReusedImmutableLeaves);
        Assert.Equal(1, stats.AllocatedObjects);
        Assert.Equal(1, stats.HydratedObjects);
        ObjectId[] leaves = filled.Selection.Reachable.Where(id => filled.Selection.Normalized.Objects[id].Model.DomainType == typeof(Leaf)).ToArray();
        Assert.Equal(2, leaves.Length);
        Leaf cached = enabledRoot.Second!;
        controls.Singleton = cached;
        int hydrates = controls.LeafHydrates;
        GraphReadStatistics guard = new();
        // An internal partial trusted mapping isolates the common pre-registration guard;
        // it is not a new repository API or permission to substitute arbitrary source rows.
        Assert.Throws<InvalidDataException>(() => GraphReader.RestorePreparedState(new GraphReadContext(filled.Selection.Models, guard),
            filled.Selection, out _, reusedLeaves: new Dictionary<ObjectId, object> { [leaves[1]] = cached }));
        Assert.Equal(0, guard.HydratedObjects);
        Assert.Equal(hydrates, controls.LeafHydrates);
        Assert.Equal((byte)8, cached.Value);
        Assert.Same(filled, repository.PreparedStateEntry);
        controls.Singleton = null;
        using WorkingCopy valid = repository.OpenWorkingCopy(repository.ForkBranch(main.BaseCommitId, "valid").Id);
        Assert.Same(cached, Assert.IsType<LeafOwner>(valid.State).Second);
    }

    private void SeedProbes() {
        using Repository seed = Repository.CreateStorageNew(_path, ProbeModels(1, new()));
        using WorkingCopy main = seed.OpenWorkingCopy(seed.CreateRootBranchFromState("main", new ProbeRoot { Value = 7 }).Id);
        using WorkingCopy other = seed.OpenWorkingCopy(seed.CreateRootBranchFromState("other", new ProbeRoot { Value = 8 }).Id);
    }
    private static DurableSchema ProbeSchema(int version) => new("SplitPreparedProbe", version,
        new DurableFieldInfo(1, TypeTag.Int32), DurableFieldInfo.Reference(2, PaddingType));
    private static StateModelRegistry ProbeModels(int version, Controls controls) {
        StateModelRegistry registry = new();
        registry.Register(new StateDefinitionBinding(BodyDependency.SchemaId, SchemaKind.InlineValue, 0, null,
            [new(BodyDependency.SchemaId, 1, SchemaKind.InlineValue, 0, [new(1, TypeExpr.Builtin(TypeTag.Int32))])]));
        registry.Register(new StateDefinitionBinding(ProbeV1.SchemaId, SchemaKind.ReferenceObject, 0, typeof(ProbeRoot),
            new StateSchemaTemplate[] {
                new(ProbeV1.SchemaId, 1, SchemaKind.ReferenceObject, 0, [new(1, TypeExpr.Builtin(TypeTag.Int32)), new(2, PaddingType)]),
                new(ProbeV1.SchemaId, 2, SchemaKind.ReferenceObject, 0, [new(1, TypeExpr.Builtin(TypeTag.Int32)), new(2, PaddingType)]),
            }.Take(version), currentModelFactory: (_, context) => {
                DurableSchema schema = version == 1 ? ProbeV1 : ProbeV2;
                return new StateModelBinding<ProbeRoot, ProbeState>(new(schema, ProbeBase,
                    static (in ProbeState prior, in ProbeState next) => new(prior != next, ProbeBase(in next).Body),
                    static (in ProbeState left, in ProbeState right) => left == right),
                    Enumerable.Range(1, version).Select(v => context.ResolveReader(v == 1 ? ProbeV1 : ProbeV2)),
                    row => {
                        controls.Normalizes++;
                        ProbeState state = row.GetState<ProbeState>();
                        if (controls.FailPhase == "normalize" && state.Value == 8) { throw controls.Failure; }
                        return state;
                    }, () => { controls.Allocations++; return new(); },
                    (ProbeRoot root, in ProbeState state, ObjectReadTable objects) => {
                        controls.Hydrates++;
                        if (controls.FailPhase == "hydrate" && state.Value == 8) { throw controls.Failure; }
                        root.Value = state.Value;
                        root.Padding = objects.ResolveObject<List<int>>(state.Padding)!;
                    }, static (root, context) => new(root.Value, context.CaptureObject(root.Padding, PaddingType)), VisitProbe);
            }, historicalReaderFactory: (schema, context) => {
                ProbeState Read(ref BareValueReader reader) {
                    int value = reader.ReadVarInt32();
                    ObjectId padding = new(reader.ReadVarUInt32());
                    controls.BodyReads++;
                    if (controls.CheckBodyDependency) {
                        controls.BodyDependencyChecks++;
                        context.BindSchema(BodyDependency);
                    }
                    if (controls.FailPhase == "body" && value == 8) { throw controls.Failure; }
                    return new(value, padding);
                }
                return new StateReaderBinding<ProbeState>(schema, Read,
                    (ref BareValueReader reader, in ProbeState _) => Read(ref reader), VisitProbe);
            }));
        return registry;
    }
    private static void VisitProbe(in ProbeState state, IStateReferenceVisitor visitor) => visitor.VisitObject(state.Padding, PaddingType);
    private static PreparedBaseBody ProbeBase(in ProbeState state) {
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        writer.WriteVarInt32(state.Value);
        writer.WriteVarUInt32(state.Padding.Value);
        return new(bytes.WrittenSpan);
    }
    private sealed class ProbeRoot : IDurableObject { internal int Value; internal List<int> Padding = Enumerable.Range(0, 128).ToList(); }
    private readonly record struct ProbeState(int Value, ObjectId Padding);
    private sealed class Controls {
        internal int BodyReads, Normalizes, Allocations, Hydrates, BodyDependencyChecks;
        internal bool CheckBodyDependency;
        internal string? FailPhase;
        internal InvalidDataException Failure { get; } = new("Split State preparation callback failed.");
    }

    private static StateModelRegistry GeneratedModels() {
        StateModelRegistry registry = new();
        registry.Register(global::Atelia.DurableGraph.Generated.Family_5030576964676574.Definition);
        return registry;
    }
    private static DB087Widget Ring() {
        List<int> numbers = Enumerable.Range(0, 128).ToList();
        DB087Widget root = new() { Value = 1, Numbers = numbers };
        root.Child = new() { Value = 2, Numbers = numbers, Child = root };
        return root;
    }
    private static DB087Widget AssertRing(IDurableObject? value, int changedIndex = -1, int changedValue = 0) {
        DB087Widget root = Assert.IsType<DB087Widget>(value);
        Assert.Equal(1, root.Value);
        DB087Widget child = Assert.IsType<DB087Widget>(root.Child);
        Assert.Equal(2, child.Value);
        Assert.Same(root, child.Child);
        Assert.Same(root.Numbers, child.Numbers);
        Assert.Equal(128, root.Numbers.Count);
        for (int i = 0; i < 128; i++) { Assert.Equal(i == changedIndex ? changedValue : i, root.Numbers[i]); }
        return root;
    }
    private static GraphReadStatistics Observe(Repository repository) {
        GraphReadStatistics statistics = new();
        repository.RestorationStatistics = statistics;
        return statistics;
    }
    private static void AssertCold(GraphReadStatistics stats) {
        Assert.Equal(1, stats.PreparedStateMisses);
        Assert.Equal(0, stats.PreparedStateHits);
        Assert.True(stats.NormalizedObjects > 0);
        Assert.True(stats.PreparedGraphs > 0);
        Assert.True(stats.ReachabilityVisits > 0);
    }
    private static void AssertRepeat(GraphReadStatistics stats, bool reuse) {
        if (!reuse) { AssertCold(stats); return; }
        Assert.Equal(1, stats.PreparedStateHits);
        Assert.Equal(0, stats.PreparedStateMisses);
        Assert.Equal(0, stats.NormalizedObjects);
        Assert.Equal(0, stats.PreparedGraphs);
        Assert.Equal(0, stats.ReachabilityVisits);
    }
    private static void AssertNoGraphWork(GraphReadStatistics stats) {
        Assert.Equal(0, stats.PreparedStateHits);
        Assert.Equal(0, stats.PreparedStateMisses);
        Assert.Equal(0, stats.NormalizedObjects);
        Assert.Equal(0, stats.PreparedGraphs);
        Assert.Equal(0, stats.AllocatedObjects);
        Assert.Equal(0, stats.HydratedObjects);
    }
    private static StateModelRegistry.FrozenConfiguration ConfigurationOf(Repository repository) =>
        (StateModelRegistry.FrozenConfiguration)typeof(Repository).GetField("_modelConfiguration", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(repository)!;

    private static readonly DurableSchema LeafSchema = new("SplitPreparedLeaf", 1, new DurableFieldInfo(1, TypeTag.Byte));
    private static readonly DurableSchema OwnerSchema = new("SplitPreparedLeafOwner", 1,
        DurableFieldInfo.Reference(1, LeafSchema.Type), DurableFieldInfo.Reference(2, LeafSchema.Type));
    private static StateModelRegistry LeafModels(LeafControls controls) {
        StateReaderBinding ownerReader = new StateReaderBinding<OwnerState>(OwnerSchema,
            static (ref BareValueReader reader) => new(new(reader.ReadVarUInt32()), new(reader.ReadVarUInt32())),
            static (ref BareValueReader reader, in OwnerState _) => new(new(reader.ReadVarUInt32()), new(reader.ReadVarUInt32())), VisitOwner);
        StateReaderBinding leafReader = new StateReaderBinding<byte>(LeafSchema,
            static (ref BareValueReader reader) => reader.ReadByte(),
            static (ref BareValueReader reader, in byte _) => reader.ReadByte(), static (in byte _, IStateReferenceVisitor _) => { });
        StateModelRegistry registry = new();
        registry.Register(new StateModelBinding<LeafOwner, OwnerState>(new(OwnerSchema, OwnerBase,
            static (in OwnerState prior, in OwnerState next) => new(prior != next, OwnerBase(in next).Body)), [ownerReader],
            static row => row.GetState<OwnerState>(), static () => new(),
            static (LeafOwner root, in OwnerState state, ObjectReadTable objects) => {
                root.First = objects.ResolveDurable<Leaf>(state.First);
                root.Second = objects.ResolveDurable<Leaf>(state.Second);
            }, static (root, context) => new(context.CaptureDurable(root.First, LeafSchema.Type), context.CaptureDurable(root.Second, LeafSchema.Type)), VisitOwner));
        registry.Register(new StateModelBinding<Leaf, byte>(new(LeafSchema, LeafBase,
            static (in byte prior, in byte next) => new(prior != next, LeafBase(in next).Body)), [leafReader],
            static row => row.GetState<byte>(), () => controls.Singleton ?? new Leaf(0),
            (Leaf leaf, in byte state, ObjectReadTable _) => {
                controls.LeafHydrates++;
                if (controls.FailHydrateValue == state) { throw controls.Failure; }
                LeafValue(leaf) = state;
            },
            static (leaf, _) => leaf.Value, static (in byte _, IStateReferenceVisitor _) => { }, isImmutableLeaf: true));
        return registry;
    }
    private static void VisitOwner(in OwnerState state, IStateReferenceVisitor visitor) {
        visitor.VisitDurable(state.First, LeafSchema.Type);
        visitor.VisitDurable(state.Second, LeafSchema.Type);
    }
    private static PreparedBaseBody OwnerBase(in OwnerState state) {
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        writer.WriteVarUInt32(state.First.Value);
        writer.WriteVarUInt32(state.Second.Value);
        return new(bytes.WrittenSpan);
    }
    private static PreparedBaseBody LeafBase(in byte value) => new(new byte[] { value });
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = nameof(Leaf.Value))]
    private static extern ref byte LeafValue(Leaf leaf);
    private sealed class Leaf(byte value) : IDurableObject { internal readonly byte Value = value; }
    private sealed class LeafOwner : IDurableObject { internal Leaf? First, Second; }
    private readonly record struct OwnerState(ObjectId First, ObjectId Second);
    private sealed class LeafControls {
        internal Leaf? Singleton;
        internal int LeafHydrates;
        internal byte? FailHydrateValue;
        internal InvalidDataException Failure { get; } = new("The second leaf rejected preparation backfill.");
    }
    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
