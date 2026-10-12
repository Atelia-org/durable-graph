using System.Buffers;
using System.Reflection;
using System.Runtime.CompilerServices;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
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
        using (Repository seed = Repository.CreateMetaBootstrapNew(_path, GeneratedModels())) {
            using BranchCheckout main = seed.CreateBranch("main", Ring());
            seed.CreateTag("state", main.Head);
            seed.CreateTag("event1", main.CommitEvent(new DB087Widget { Value = 51 }));
            main.Catalog.Set(Extra);
            seed.CreateTag("event2", main.CommitEvent(new DB087Widget { Value = 52 }));
        }
        FrameAddress saved;
        ObjectId listId;
        ObjectStorageInfo savedStorage;
        using (Repository repository = Repository.OpenMetaBootstrapExisting(_path, GeneratedModels())) {
            repository.PreparedStateReuseEnabled = reuse;
            Assert.Null(repository.PreparedStateEntry);
            CheckpointAddress state = repository.ResolveTag("state"), e1 = repository.ResolveTag("event1"), e2 = repository.ResolveTag("event2");
            GraphReadStatistics firstStats = Observe(repository);
            using BranchCheckout first = repository.Fork("first", e1);
            AssertCold(firstStats);
            PreparedStateRestoration? entry = repository.PreparedStateEntry;
            GraphReadStatistics secondStats = Observe(repository);
            using BranchCheckout second = repository.Fork("second", e2);
            AssertRepeat(secondStats, reuse);
            DB087Widget firstRoot = AssertRing(first.State), secondRoot = AssertRing(second.State);
            Assert.NotSame(firstRoot, secondRoot);
            Assert.NotSame(firstRoot.Child, secondRoot.Child);
            Assert.NotSame(firstRoot.Numbers, secondRoot.Numbers);
            Assert.Equal(3, secondStats.AllocatedObjects);
            Assert.Equal(firstStats.AllocatedObjects, secondStats.AllocatedObjects);
            Assert.Equal(firstStats.HydratedObjects, secondStats.HydratedObjects);
            Assert.Equal(e1, first.Head);
            Assert.Equal(e2, second.Head);
            Assert.Equal(state.RevisionAddress, second.StateRevisionAddress);
            Assert.Equal(state.RootId, second.StateId);
            Assert.Equal(repository.MetaBootstrapStore.ReadMetadata(state.RevisionAddress).SchemaRoot,
                second.BindingEnvironment!.SelectedSchemaView.Snapshot!.Address);
            Assert.NotEqual(repository.SharedSchemaRoot, second.BindingEnvironment.SelectedSchemaView.Snapshot.Address);
            Assert.Equal(first.Baseline!.Objects.Keys.OrderBy(id => id.Value), second.Baseline!.Objects.Keys.OrderBy(id => id.Value));
            foreach ((ObjectId id, NormalizedObject row) in first.Baseline.Objects) { Assert.Equal(row.Storage, second.Baseline.Objects[id].Storage); }
            if (reuse) { Assert.Same(entry, repository.PreparedStateEntry); }
            else { Assert.Null(entry); Assert.Null(repository.PreparedStateEntry); }

            listId = Assert.Single(second.Baseline.Objects, row => row.Value.Current.Kind == ObjectStateKind.List).Key;
            ObjectStorageInfo prior = second.Baseline.Objects[listId].Storage!.Value;
            secondRoot.Numbers[7] = 200;
            CheckpointAddress next = second.CommitState();
            saved = next.RevisionAddress;
            P0GraphSnapshot graph = repository.MetaBootstrapStore.ReadGraph(saved);
            Assert.Equal(e2.RevisionAddress, graph.Parent);
            Assert.Equal(state.RevisionAddress, graph.SourceGraph);
            ObjectVersionRecord delta = Assert.Single(repository.MetaBootstrapStore.ReadBatch(graph.OwnedBatch!.Value).Records);
            Assert.Equal(listId.Value, delta.ObjectId);
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
            Assert.Equal(prior.Head, delta.PriorAddress);
            savedStorage = second.Baseline!.Objects[listId].Storage!.Value;
            Assert.Equal(prior.ReconstructionPayloadBytes + ObjectVersionPayloadSize.GetDeltaPayloadBytes(delta.Body.Length, prior.Head),
                savedStorage.ReconstructionPayloadBytes);
            Assert.Equal(7, firstRoot.Numbers[7]);
            Assert.Equal(state.RevisionAddress, first.StateRevisionAddress);
            // A hot confirmed State must not install a new preparation certificate (DB-081).
            Assert.Same(entry, repository.PreparedStateEntry);
            using BranchCheckout replacement = repository.Fork("replacement", next);
            if (reuse) { Assert.NotSame(entry, repository.PreparedStateEntry); }
            firstRoot.Numbers[8] = 201;
            CheckpointAddress oldSave = first.CommitState(); // Eviction does not invalidate the previously delivered workspace.
            P0GraphSnapshot oldGraph = repository.MetaBootstrapStore.ReadGraph(oldSave.RevisionAddress);
            Assert.Equal(state.RevisionAddress, oldGraph.SourceGraph);
            Assert.Equal(e1.RevisionAddress, oldGraph.Parent);
            Assert.Equal(prior.Head, Assert.Single(repository.MetaBootstrapStore.ReadBatch(oldGraph.OwnedBatch!.Value).Records).PriorAddress);
        }
        using Repository reopened = Repository.OpenMetaBootstrapExisting(_path, GeneratedModels());
        Assert.Null(reopened.PreparedStateEntry);
        using BranchCheckout cold = reopened.Checkout("second");
        DB087Widget coldRoot = AssertRing(cold.State, changedIndex: 7, changedValue: 200);
        Assert.Equal(saved, cold.StateRevisionAddress);
        Assert.Equal(listId, Assert.Single(cold.Baseline!.Objects, row => row.Value.Current.Kind == ObjectStateKind.List).Key);
        Assert.Equal(savedStorage, cold.Baseline.Objects[listId].Storage);
        coldRoot.Numbers[8] = 202;
        CheckpointAddress continued = cold.CommitState();
        P0GraphSnapshot continuedGraph = reopened.MetaBootstrapStore.ReadGraph(continued.RevisionAddress);
        ObjectVersionRecord continuedDelta = Assert.Single(reopened.MetaBootstrapStore.ReadBatch(continuedGraph.OwnedBatch!.Value).Records);
        Assert.Equal(ObjectVersionKind.Delta, continuedDelta.Kind);
        Assert.Equal(savedStorage.Head, continuedDelta.PriorAddress);
        Assert.Equal(saved, continuedGraph.SourceGraph);
        DB087Widget continuedRead = Assert.IsType<DB087Widget>(reopened.ReadState(continued));
        Assert.Equal(200, continuedRead.Numbers[7]);
        Assert.Equal(202, continuedRead.Numbers[8]);
        AssertRing(reopened.ReadState(reopened.ResolveTag("state")));
    }

    [Fact]
    public void EventOnlyHeadsBypassTheSlotAndReadonlyQueriesNeverSeedOrReplaceIt() {
        using Repository repository = Repository.CreateMetaBootstrapNew(_path, GeneratedModels());
        using BranchCheckout main = repository.CreateBranch("main", Ring());
        CheckpointAddress state = main.Head;
        repository.ReadState(state);
        repository.ReadPair(state, state);
        repository.ReadCheckpoint(state);
        Assert.Null(repository.PreparedStateEntry);
        using BranchCheckout warm = repository.Fork("warm", state);
        PreparedStateRestoration entry = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
        using BranchCheckout events = repository.CreateBranchFromEvent("events", new DB087Widget { Value = 50 });
        CheckpointAddress e2 = events.CommitEvent(new DB087Widget { Value = 51 });
        repository.CreateBranch("event-copy", e2);
        GraphReadStatistics stats = Observe(repository);
        using BranchCheckout emptyFork = repository.Fork("event-fork", e2);
        using BranchCheckout emptyCheckout = repository.Checkout("event-copy");
        Assert.Null(emptyFork.State);
        Assert.Null(emptyCheckout.State);
        Assert.Null(emptyFork.Baseline);
        Assert.Equal(e2, emptyFork.Head);
        AssertNoGraphWork(stats);
        Assert.Same(entry, repository.PreparedStateEntry);
        repository.ReadState(state);
        repository.ReadPair(state, state);
        repository.ReadCheckpoint(e2);
        Assert.Same(entry, repository.PreparedStateEntry);
        stats = Observe(repository);
        CheckpointAddress firstState = emptyFork.CommitState(Ring());
        Assert.Same(entry, repository.PreparedStateEntry); // Capture/confirmed save is not cold State admission.
        using BranchCheckout firstStateFork = repository.Fork("first-state", firstState);
        AssertCold(stats);
        Assert.NotSame(entry, repository.PreparedStateEntry);
        Assert.Null(repository.MetaBootstrapStore.ReadGraph(firstState.RevisionAddress).SourceGraph);
    }

    [Theory]
    [InlineData("body")]
    [InlineData("normalize")]
    [InlineData("hydrate")]
    public void FailedColdReplacementKeepsOldPreparationAndSameNameRetryRemainsCold(string phase) {
        SeedProbes();
        Controls controls = new();
        using Repository repository = Repository.OpenMetaBootstrapExisting(_path, ProbeModels(2, controls));
        using BranchCheckout warm = repository.Fork("warm", repository.GetHead("main"));
        PreparedStateRestoration entry = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
        controls.FailPhase = phase;
        GraphReadStatistics failed = Observe(repository);
        BranchCheckout? delivered = null;
        Assert.Same(controls.Failure, Assert.Throws<InvalidDataException>(() => delivered = repository.Fork("retry", repository.GetHead("other"))));
        Assert.Null(delivered);
        Assert.Same(entry, repository.PreparedStateEntry);
        Assert.Equal(1, failed.PreparedStateMisses);
        Assert.DoesNotContain("retry", repository.ListBranches());
        Assert.False(repository.IsFaulted);
        if (phase is "body" or "normalize") { Assert.Equal(0, failed.AllocatedObjects); }
        controls.FailPhase = null;
        GraphReadStatistics retryStats = Observe(repository);
        using BranchCheckout retry = repository.Fork("retry", repository.GetHead("other"));
        AssertCold(retryStats);
        Assert.Equal(8, Assert.IsType<ProbeRoot>(retry.State).Value);
        Assert.NotSame(entry, repository.PreparedStateEntry);
        Assert.Equal(7, Assert.IsType<ProbeRoot>(warm.State).Value);
        CheckpointAddress savedWarm = warm.CommitState();
        Assert.Equal(ObjectVersionKind.Base, Assert.Single(repository.MetaBootstrapStore.ReadBatch(
            repository.MetaBootstrapStore.ReadGraph(savedWarm.RevisionAddress).OwnedBatch!.Value).Records).Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForkPublicationFailureDoesNotInstallUndeliveredStateMaterial(bool afterPublication) {
        SeedProbes();
        using Repository repository = Repository.OpenMetaBootstrapExisting(_path, ProbeModels(1, new()));
        using BranchCheckout warm = repository.Fork("warm", repository.GetHead("main"));
        PreparedStateRestoration previous = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
        var before = ResourcesOf(repository).Versions.ReadRef(1).Unwrap();
        repository.Checkpoint = point => {
            if (point == (afterPublication ? CommitCheckpoint.AfterPublication : CommitCheckpoint.BeforePublication)) {
                throw new IOException("split preparation Fork publication window");
            }
        };
        BranchCheckout? delivered = null;
        GraphCommitException error = Assert.Throws<GraphCommitException>(() => delivered = repository.Fork("retry", repository.GetHead("other")));
        Assert.Null(delivered);
        Assert.Equal(afterPublication ? GraphCommitOutcome.Published : GraphCommitOutcome.NotPublished, error.Outcome);
        Assert.Equal(afterPublication, repository.IsFaulted);
        repository.Checkpoint = null;
        if (afterPublication) {
            Assert.Null(repository.PreparedStateEntry);
            Assert.Throws<InvalidOperationException>(() => warm.CommitState());
        } else {
            Assert.Same(previous, repository.PreparedStateEntry);
            var unchanged = ResourcesOf(repository).Versions.ReadRef(1).Unwrap();
            Assert.Equal(before.Revision, unchanged.Revision);
            Assert.Equal(before.Payload.ToArray(), unchanged.Payload.ToArray());
            GraphReadStatistics stats = Observe(repository);
            using BranchCheckout retry = repository.Fork("retry", repository.GetHead("other"));
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
            using Repository repository = Repository.OpenMetaBootstrapExisting(_path, registry);
            CheckpointAddress state = repository.GetHead("main");
            using BranchCheckout warm = repository.Fork("warm", state);
            PreparedStateRestoration prepared = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
            Assert.True(controls.BodyDependencyChecks > 0);
            Assert.DoesNotContain(prepared.Selection.Normalized.Objects.Values, row => row.SourceLayout.Schema?.SchemaId == BodyDependency.SchemaId ||
                row.Current.Schema?.SchemaId == BodyDependency.SchemaId);
            var configuration = ConfigurationOf(repository);
            GraphReadContext context = new(prepared.Selection.Models);
            prepared.ValidateSplit(repository.MetaBootstrapStore, configuration, state, context);
            using Repository foreign = Repository.OpenMetaBootstrapReadOnlyExisting(clone, registry);
            Assert.Equal(state.RevisionAddress, foreign.GetHead("main").RevisionAddress); // Physical addresses match in another actual owner.
            P0BusinessWorkspace foreignBusiness = P0BusinessWorkspace.LoadState(foreign.MetaBootstrapStore, state.RevisionAddress, configuration);
            long appended = repository.MetaBootstrapStore.AppendedFrames;
            using P0Repository borrowed = new(ResourcesOf(repository));
            Assert.Throws<InvalidDataException>(() => new P0Session(borrowed, "warm", foreignBusiness, static () => { }));
            Assert.Equal(appended, repository.MetaBootstrapStore.AppendedFrames);
            P0BusinessWorkspace ownBusiness = P0BusinessWorkspace.LoadState(repository.MetaBootstrapStore, state.RevisionAddress, configuration);
            P0Session positive = new(borrowed, "warm", ownBusiness, static () => { });
            Assert.Equal(state.RevisionAddress, positive.StateSource!.Address);
            Assert.Equal(7, Assert.IsType<ProbeRoot>(positive.State).Value);
            Assert.Equal(appended, repository.MetaBootstrapStore.AppendedFrames);
            int allocations = controls.Allocations, bodyReads = controls.BodyReads;
            Assert.Throws<ArgumentException>(() => repository.Fork("foreign", foreign.GetHead("main")));
            Assert.Throws<InvalidDataException>(() => prepared.ValidateSplit(foreign.MetaBootstrapStore, configuration, state, context));
            Assert.Throws<InvalidDataException>(() => prepared.ValidateSplit(repository.MetaBootstrapStore, registry.FreezeConfiguration(), state, context));
            Assert.Throws<InvalidDataException>(() => prepared.ValidateSplit(repository.MetaBootstrapStore, configuration, state,
                new(configuration.CreateSnapshot())));
            Assert.Equal(allocations, controls.Allocations);
            Assert.Equal(bodyReads, controls.BodyReads);

            warm.Catalog.Set(WrongBodyDependency); // Absent from old S; this new S may legally declare the unknown key.
            CheckpointAddress newState = warm.CommitState();
            Assert.Same(prepared, repository.PreparedStateEntry);
            FrameAddress newSchema = repository.MetaBootstrapStore.ReadMetadata(newState.RevisionAddress).SchemaRoot!.Value;
            var wrongEnvironment = DecodedRevision.SplitBindingEnvironment.Load(repository.MetaBootstrapStore, configuration, newSchema);
            InvalidDataException dependencyError = Assert.Throws<InvalidDataException>(() => prepared.Requirements.Validate(wrongEnvironment.Models));
            Assert.Contains(BodyDependency.SchemaId, dependencyError.Message);
            Assert.Throws<InvalidDataException>(() => prepared.ValidateSplit(repository.MetaBootstrapStore, configuration, state,
                new(wrongEnvironment.Models)));
            Assert.Equal(allocations, controls.Allocations);
            Assert.Equal(bodyReads, controls.BodyReads);
            // This product request selects the real new S. Its body-only dependency fails;
            // an old valid certificate cannot be retagged for the new State.
            BranchCheckout? delivered = null;
            Assert.Throws<InvalidDataException>(() => delivered = repository.Fork("wrong-schema", newState));
            Assert.Null(delivered);
            Assert.Same(prepared, repository.PreparedStateEntry);
            Assert.DoesNotContain("wrong-schema", repository.ListBranches());
            Assert.Equal(allocations, controls.Allocations);
            Assert.False(repository.IsFaulted);
            GraphReadStatistics hit = Observe(repository);
            using BranchCheckout oldAgain = repository.Fork("old-again", state);
            AssertRepeat(hit, true);
            Assert.Equal(7, Assert.IsType<ProbeRoot>(oldAgain.State).Value);
        } finally { SharedReadModel.DeleteFixture(clone, Prefix); }
    }

    [Fact]
    public void CachedHistoricalRewriteSurvivesDiscardAndEventsUntilStateBaseThenColdContinuesDelta() {
        SeedProbes();
        Controls controls = new();
        using (Repository repository = Repository.OpenMetaBootstrapExisting(_path, ProbeModels(2, controls))) {
            CheckpointAddress original = repository.GetHead("main");
            using BranchCheckout first = repository.Fork("first", original);
            PreparedStateRestoration entry = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
            int normalized = controls.Normalizes;
            int bodyReads = controls.BodyReads;
            GraphReadStatistics hit = Observe(repository);
            using BranchCheckout second = repository.Fork("second", original);
            AssertRepeat(hit, true);
            Assert.Equal(normalized, controls.Normalizes);
            Assert.Equal(bodyReads, controls.BodyReads);
            ProbeRoot root = Assert.IsType<ProbeRoot>(second.State);
            Assert.NotSame(first.State, root);
            Assert.NotSame(Assert.IsType<ProbeRoot>(first.State).Padding, root.Padding);
            ObjectId rootId = second.StateId!.Value;
            Assert.True(second.Baseline!.Objects[rootId].RequiresRewrite);
            ObjectStorageInfo oldRoot = second.Baseline.Objects[rootId].Storage!.Value;
            second.PrepareAndDiscard(root);
            Assert.True(second.Baseline.Objects[rootId].RequiresRewrite);
            CheckpointAddress e1 = second.CommitEvent(root), e2 = second.CommitEvent(root);
            foreach (CheckpointAddress address in new[] { e1, e2 }) {
                P0GraphSnapshot graph = repository.MetaBootstrapStore.ReadGraph(address.RevisionAddress);
                Assert.Equal(original.RevisionAddress, graph.SourceGraph);
                Assert.Equal(ObjectVersionKind.Base, Assert.Single(repository.MetaBootstrapStore.ReadBatch(graph.OwnedBatch!.Value).Records).Kind);
            }
            Assert.True(second.Baseline.Objects[rootId].RequiresRewrite);
            Assert.Equal(oldRoot, second.Baseline.Objects[rootId].Storage);
            Assert.Same(entry, repository.PreparedStateEntry);
            CheckpointAddress confirmed = second.CommitState();
            P0GraphSnapshot written = repository.MetaBootstrapStore.ReadGraph(confirmed.RevisionAddress);
            ObjectVersionRecord @base = Assert.Single(repository.MetaBootstrapStore.ReadBatch(written.OwnedBatch!.Value).Records);
            Assert.Equal(ObjectVersionKind.Base, @base.Kind);
            Assert.Equal(rootId.Value, @base.ObjectId);
            Assert.False(second.Baseline!.Objects[rootId].RequiresRewrite);
            Assert.Same(entry, repository.PreparedStateEntry); // No hot preparation admission.
            repository.CreateTag("rewritten", confirmed);
            ObjectId listId = Assert.Single(second.Baseline.Objects, row => row.Value.Current.Kind == ObjectStateKind.List).Key;
            ObjectStorageInfo prior = second.Baseline.Objects[listId].Storage!.Value;
            root.Padding[7] = 200;
            CheckpointAddress deltaState = second.CommitState();
            P0GraphSnapshot deltaGraph = repository.MetaBootstrapStore.ReadGraph(deltaState.RevisionAddress);
            ObjectVersionRecord delta = Assert.Single(repository.MetaBootstrapStore.ReadBatch(deltaGraph.OwnedBatch!.Value).Records);
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
            Assert.Equal(listId.Value, delta.ObjectId);
            Assert.Equal(prior.Head, delta.PriorAddress);
            Assert.Equal(prior.ReconstructionPayloadBytes + ObjectVersionPayloadSize.GetDeltaPayloadBytes(delta.Body.Length, prior.Head),
                second.Baseline!.Objects[listId].Storage!.Value.ReconstructionPayloadBytes);
            Assert.Equal(7, Assert.IsType<ProbeRoot>(first.State).Padding[7]);
        }
        using Repository reopened = Repository.OpenMetaBootstrapExisting(_path, ProbeModels(2, new()));
        using BranchCheckout cold = reopened.Checkout("second");
        ProbeRoot coldRoot = Assert.IsType<ProbeRoot>(cold.State);
        Assert.Equal(7, coldRoot.Value);
        Assert.Equal(200, coldRoot.Padding[7]);
        ObjectId coldList = Assert.Single(cold.Baseline!.Objects, row => row.Value.Current.Kind == ObjectStateKind.List).Key;
        ObjectStorageInfo coldPrior = cold.Baseline.Objects[coldList].Storage!.Value;
        coldRoot.Padding[8] = 201;
        CheckpointAddress continued = cold.CommitState();
        P0GraphSnapshot graphAfterCold = reopened.MetaBootstrapStore.ReadGraph(continued.RevisionAddress);
        ObjectVersionRecord continuation = Assert.Single(reopened.MetaBootstrapStore.ReadBatch(graphAfterCold.OwnedBatch!.Value).Records);
        Assert.Equal(ObjectVersionKind.Delta, continuation.Kind);
        Assert.Equal(coldPrior.Head, continuation.PriorAddress);
        Assert.Equal(7, Assert.IsType<ProbeRoot>(reopened.ReadState(reopened.GetHead("main"))).Padding[7]);
        Assert.Equal(7, Assert.IsType<ProbeRoot>(reopened.ReadState(reopened.ResolveTag("rewritten"))).Padding[7]);
    }

    [Fact]
    public void OptionalLeafReuseBackfillsOnlyAfterSuccessAndRegistersTrustedLeavesBeforeOtherAllocation() {
        LeafControls controls = new();
        using Repository repository = Repository.CreateMetaBootstrapNew(_path, LeafModels(controls));
        using BranchCheckout main = repository.CreateBranch("main", new LeafOwner { First = new(7), Second = new(8) });
        Assert.False(repository.ImmutableLeafReuseEnabled);
        using BranchCheckout disabled = repository.Fork("disabled", main.Head);
        PreparedStateRestoration original = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
        Assert.False(original.ImmutableLeavesCollected);
        repository.ImmutableLeafReuseEnabled = true;
        controls.FailHydrateValue = 8;
        int beforeBackfill = controls.LeafHydrates;
        BranchCheckout? delivered = null;
        Assert.Same(controls.Failure, Assert.Throws<InvalidDataException>(() => delivered = repository.Fork("enabled", main.Head)));
        Assert.Null(delivered);
        Assert.Equal(beforeBackfill + 2, controls.LeafHydrates); // First leaf completed; the second leaf rejected backfill.
        Assert.Same(original, repository.PreparedStateEntry);
        Assert.False(original.ImmutableLeavesCollected);
        Assert.DoesNotContain("enabled", repository.ListBranches());
        Assert.False(repository.IsFaulted);
        controls.FailHydrateValue = null;
        using BranchCheckout enabled = repository.Fork("enabled", main.Head);
        PreparedStateRestoration filled = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
        Assert.NotSame(original, filled);
        Assert.True(filled.ImmutableLeavesCollected);
        Assert.Equal(2, filled.ImmutableLeafCount);
        LeafOwner enabledRoot = Assert.IsType<LeafOwner>(enabled.State);
        Assert.NotSame(Assert.IsType<LeafOwner>(disabled.State).First, enabledRoot.First);
        GraphReadStatistics stats = Observe(repository);
        using BranchCheckout hit = repository.Fork("hit", main.Head);
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
        using BranchCheckout valid = repository.Fork("valid", main.Head);
        Assert.Same(cached, Assert.IsType<LeafOwner>(valid.State).Second);
    }

    private void SeedProbes() {
        using Repository seed = Repository.CreateMetaBootstrapNew(_path, ProbeModels(1, new()));
        using BranchCheckout main = seed.CreateBranch("main", new ProbeRoot { Value = 7 });
        using BranchCheckout other = seed.CreateBranch("other", new ProbeRoot { Value = 8 });
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
        (StateModelRegistry.FrozenConfiguration)typeof(Repository).GetField("_splitConfiguration", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(repository)!;

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
