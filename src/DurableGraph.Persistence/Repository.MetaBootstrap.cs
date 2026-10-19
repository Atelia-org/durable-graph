using Atelia.DurableGraph.Persistence;

using Atelia.FrameStore;

namespace Atelia.DurableGraph;

// Concrete owner/checkout integration, shared by the public and internal probe factories.
public sealed partial class Repository {
    private readonly StateModelRegistry.FrozenConfiguration _modelConfiguration;
    private readonly RepositoryStorageFacade _storageFacade;

    private Repository(RepositoryResources resources, StateModelRegistry.FrozenConfiguration configuration) {
        _resources = resources;
        _modelConfiguration = configuration;
        _storageFacade = new(resources);
    }

    internal static Repository CreateStorageNew(string path, StateModelRegistry models,
        long readCacheBudgetBytes = GraphRecordStore.DefaultReadCacheBudgetBytes, Action<uint>? afterCreateHead = null,
        long rotationThresholdBytes = RepositoryResources.DefaultRotationThresholdBytes) =>
        OpenStorage(path, models, create: true, readOnly: false, readCacheBudgetBytes, afterCreateHead, rotationThresholdBytes);
    internal static Repository OpenStorageExisting(string path, StateModelRegistry models,
        long readCacheBudgetBytes = GraphRecordStore.DefaultReadCacheBudgetBytes,
        long rotationThresholdBytes = RepositoryResources.DefaultRotationThresholdBytes) =>
        OpenStorage(path, models, create: false, readOnly: false, readCacheBudgetBytes,
            rotationThresholdBytes: rotationThresholdBytes);
    internal static Repository OpenStorageReadOnlyExisting(string path, StateModelRegistry models,
        long readCacheBudgetBytes = GraphRecordStore.DefaultReadCacheBudgetBytes) =>
        OpenStorage(path, models, create: false, readOnly: true, readCacheBudgetBytes);

    private static Repository OpenStorage(string path, StateModelRegistry models, bool create, bool readOnly,
        long readCacheBudgetBytes, Action<uint>? afterCreateHead = null,
        long rotationThresholdBytes = RepositoryResources.DefaultRotationThresholdBytes) {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentOutOfRangeException.ThrowIfNegative(readCacheBudgetBytes);
        RepositoryResources? resources = null;
        try {
            resources = create ? RepositoryResources.CreateStorageNew(path, readCacheBudgetBytes, afterCreateHead, rotationThresholdBytes) : readOnly
                ? RepositoryResources.OpenStorageReadOnlyExisting(path, readCacheBudgetBytes)
                : RepositoryResources.OpenStorageExisting(path, readCacheBudgetBytes, rotationThresholdBytes);
            return new(resources, models.FreezeConfiguration());
        } catch (Exception primary) {
            try { resources?.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException("Repository opening and resource cleanup failed.", primary, cleanup); }
            throw;
        }
    }

    internal GraphRecordStore GraphStore {
        get { RequireAvailable(); return _storageFacade.Store; }
    }
    internal FrameAddress? SharedSchemaRoot {
        get { RequireAvailable(); return _storageFacade.SharedSchemaRoot; }
    }
    internal Action<RepositorySaveStage>? SaveStageObserver {
        get { RequireAvailable(); return _storageFacade.SaveStageObserver; }
        set { RequireAvailable(); _storageFacade.SaveStageObserver = value; }
    }

    private BranchInfo CreateBranchCore(string displayLabel, IDurableObject root, StoredGraphKind kind,
        ReadOnlyMemory<byte> summary, ReadAmplificationBaseBudgetParameters? parameters) {
        RequireWriter();
        _busy = true;
        try {
            ArgumentNullException.ThrowIfNull(root);
            RepositoryResources.ValidateDisplayLabel(displayLabel);
            var session = new BranchSaveSession(_storageFacade, null, null, _modelConfiguration,
                () => _resources.EnsureEditingAllowed());
            using var pending = session.Prepare(root, kind, parameters ?? DefaultPolicy, summary, displayLabel);
            OperationStageObserver?.Invoke(RepositoryOperationStage.AfterPrepare);
            BranchInfo? branch = null;
            pending.Commit(deliver: result => branch = ReadBranchInfoCore(new(result.BranchId)));
            return branch!;
        } finally { EndOperation(); }
    }

    private WorkingCopy CheckoutCore(BranchId branchId) {
        RequireWriter();
        _busy = true;
        bool acquired = false;
        bool delivered = false;
        try {
            var head = _resources.ReadCurrent(branchId.Value);
            acquired = _activeBranches.Add(branchId);
            if (!acquired) { throw new InvalidOperationException("Branch already has an active working copy."); }
            var work = RestoreCheckout(branchId, head, out PreparedStateRestoration? candidate);
            if (candidate is not null) { _preparedState = candidate; }
            delivered = true;
            return work;
        } finally {
            if (acquired && !delivered) { _activeBranches.Remove(branchId); }
            EndOperation();
        }
    }

    private WorkingCopy NewCheckout(BranchId branchId, HistoryGraphRecord head,
        BusinessPersistenceAdapter? business = null) {
        WorkingCopy? work = null;
        var session = new BranchSaveSession(_storageFacade, branchId.Value, head.Id, _modelConfiguration,
            () => EnsureEditingAllowed(work!), business);
        var state = _resources.FindNearestState(head.Id, inclusive: true);
        work = new(this, branchId, session, new(head.Id), state is null ? null : new(state.Id));
        return work;
    }

    private WorkingCopy RestoreCheckout(BranchId branchId, HistoryGraphRecord head,
        out PreparedStateRestoration? candidate) {
        candidate = null;
        var store = _storageFacade.Store;
        if (head.Kind == CommitKind.Event) { store.ValidateBusinessMetadata(head.Address, _resources.FindNearestState(head.Id, inclusive: false)?.Address); }
        HistoryGraphRecord? state = _resources.FindNearestState(head.Id, inclusive: true);
        // An Event-only prefix never consults a resident State or its model environment.
        if (state is null) { return NewCheckout(branchId, head); }
        if (PreparedStateReuseEnabled && _preparedState is { } prepared && prepared.Matches(state)) {
            DecodedRevision.SchemaViewBindingEnvironment environment = prepared.BindingEnvironment
                ?? throw new InvalidDataException("A split State slot must retain its reader-issued environment.");
            GraphReadContext context = new(environment.Models, RestorationStatistics);
            context.Statistics.PreparedStateHits++;
            if (ImmutableLeafReuseEnabled && !prepared.ImmutableLeavesCollected) {
                using var leafRequirements = environment.Models.BeginSchemaRequirementCollection();
                prepared.ValidateStoredSource(store, _modelConfiguration!, state, _resources.FindNearestState(state.Id, inclusive: false)?.Address, context);
                var materializedLeaf = GraphReader.RestorePreparedState(context, prepared.Selection,
                    out var leaves, collectImmutableLeaves: true);
                var leafBusiness = BusinessPersistenceAdapter.FromLoaded(store, environment, materializedLeaf);
                WorkingCopy leafCheckout = NewCheckout(branchId, head, leafBusiness);
                candidate = new(state, prepared.Selection, leafRequirements.Complete(), leaves,
                    ImmutableLeavesCollected: true, BindingEnvironment: environment);
                return leafCheckout;
            }
            prepared.ValidateStoredSource(store, _modelConfiguration!, state, _resources.FindNearestState(state.Id, inclusive: false)?.Address, context);
            var materializedHit = GraphReader.RestorePreparedState(context, prepared.Selection, out _,
                reusedLeaves: ImmutableLeafReuseEnabled ? prepared.ImmutableLeaves : null);
            return NewCheckout(branchId, head,
                BusinessPersistenceAdapter.FromLoaded(store, environment, materializedHit));
        }

        var metadata = store.ReadMetadata(state.Address);
        if (metadata.Kind != StoredGraphKind.State || metadata.RootId != state.RootId || metadata.SchemaRoot is not { } schemaRoot) {
            throw new InvalidDataException("Editable State must select its actual root and Schema pin.");
        }
        var sourceEnvironment = DecodedRevision.SchemaViewBindingEnvironment.Load(store, _modelConfiguration!, schemaRoot);
        GraphReadContext reads = new(sourceEnvironment.Models, RestorationStatistics);
        reads.Statistics.PreparedStateMisses++;
        // Include stored reader/body checks as well as normalization and first full materialization.
        using var requirements = sourceEnvironment.Models.BeginSchemaRequirementCollection();
        DecodedRevision source = DecodedRevision.ReadStoredGraph(store, QualifyBusiness(state), _modelConfiguration!, sourceEnvironment);
        PreparedGraphSelection selection = GraphReader.PrepareState(source, reads, state.RootId);
        foreach ((ObjectId id, NormalizedObject row) in selection.Normalized.Objects) {
            sourceEnvironment.Models.CheckObjectLayout(row.SourceLayout, $"prepared State source object {id.Value}");
            sourceEnvironment.Models.CheckObjectLayout(row.Current.Layout, $"prepared State current object {id.Value}");
        }
        bool collectLeaves = PreparedStateReuseEnabled && ImmutableLeafReuseEnabled;
        var materialized = GraphReader.RestorePreparedState(reads, selection, out var immutableLeaves,
            collectImmutableLeaves: collectLeaves);
        var business = BusinessPersistenceAdapter.FromLoaded(store, sourceEnvironment, materialized);
        WorkingCopy restored = NewCheckout(branchId, head, business);
        var certificate = requirements.Complete();
        if (PreparedStateReuseEnabled) {
            candidate = new(state, selection, certificate, immutableLeaves, collectLeaves, sourceEnvironment);
        }
        return restored;
    }

    private void EnsureEditingAllowed(WorkingCopy work) {
        RequireCheckoutWriter(work);
        _storageFacade.EnsureEditingAllowed();
    }

    private void RequireCheckoutSource(WorkingCopy work) {
        if (!work.IsLiveFor(this) || !_activeBranches.Contains(work.BranchId) ||
            _resources.ReadCurrent(work.BranchId.Value).Id != work.BaseCommitId.Value) {
            throw new InvalidOperationException("Working copy does not own the expected branch head.");
        }
        var state = _resources.FindNearestState(work.BaseCommitId.Value, inclusive: true);
        if (state?.Address != work.StateRevisionAddress ||
            (state is null ? work.State is not null : work.StateId != state.RootId) ||
            state?.Id != work.StateSourceCommitId?.Value) {
            throw new InvalidOperationException("Working copy State baseline does not match business history.");
        }
    }

    private CommitInfo CommitSave(WorkingCopy work, IDurableObject? domainEvent, IDurableObject? nextState,
        ReadOnlyMemory<byte> summary, ReadAmplificationBaseBudgetParameters? parameters) {
        RequireCheckoutWriter(work);
        _busy = true;
        try {
            var kind = domainEvent is null ? StoredGraphKind.State : StoredGraphKind.Event;
            using var pending = work.SaveSession.Prepare(domainEvent ?? nextState!, kind, parameters ?? DefaultPolicy, summary);
            OperationStageObserver?.Invoke(RepositoryOperationStage.AfterPrepare);
            CommitInfo? info = null;
            // The owner catches delivery failures with Pending.ExpectedEntry, never a partially
            // advanced WorkingCopy value, and attaches only the actual HeadStore result IDs.
            pending.Commit(deliver: result => {
                info = Issue(_resources.ReadDescriptor(result.CommitId));
                work.SaveSession.Advance(result.CommitId);
                work.BaseCommitId = info.Id;
                if (kind == StoredGraphKind.State) { work.StateSourceCommitId = info.Id; }
            });
            return info!;
        } finally { EndOperation(); }
    }

    internal void PrepareAndDiscardSave(WorkingCopy work, IDurableObject root, StoredGraphKind kind,
        ReadAmplificationBaseBudgetParameters? parameters) {
        RequireCheckoutWriter(work);
        _busy = true;
        try {
            ArgumentNullException.ThrowIfNull(root);
            using var pending = work.SaveSession.Prepare(root, kind, parameters ?? DefaultPolicy);
            OperationStageObserver?.Invoke(RepositoryOperationStage.AfterPrepare);
        } finally { EndOperation(); }
    }

    private IDurableObject ReadGraph(CommitId commitId, CommitKind kind) {
        RequireAvailable();
        _busy = true;
        try { return ReadGraphRootCore(CheckCommit(commitId, kind)); }
        finally { EndOperation(); }
    }
}
