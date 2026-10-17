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

    private BranchCheckout CreateBranchCore(string branchName, IDurableObject root, StoredGraphKind kind,
        ReadAmplificationBaseBudgetParameters? parameters) {
        RequireWriter();
        _busy = true;
        bool acquired = false;
        bool delivered = false;
        try {
            ArgumentNullException.ThrowIfNull(root);
            RepositoryResources.ValidateBranchName(branchName);
            if (_storageFacade!.GetHead(branchName) is not null) { throw new InvalidOperationException("Branch name already exists."); }
            acquired = _activeBranches.Add(branchName);
            if (!acquired) { throw new InvalidOperationException("Branch already has an active checkout."); }
            BranchCheckout checkout = NewCheckout(branchName, null, privateCatalog: false);
            PublishSave(checkout, root, kind, parameters ?? DefaultPolicy);
            delivered = true;
            return checkout;
        } finally {
            if (acquired && !delivered) { _activeBranches.Remove(branchName); }
            EndOperation();
        }
    }

    private BranchCheckout CheckoutCore(string branchName, bool privateCatalog = false) {
        RequireWriter();
        _busy = true;
        bool acquired = false;
        bool delivered = false;
        try {
            CheckpointAddress head = StoredHeadCore(branchName);
            acquired = _activeBranches.Add(branchName);
            if (!acquired) { throw new InvalidOperationException("Branch already has an active checkout."); }
            BranchCheckout checkout = RestoreCheckout(branchName, head, out PreparedStateRestoration? candidate, privateCatalog);
            if (candidate is not null) { _preparedState = candidate; }
            delivered = true;
            return checkout;
        } finally {
            if (acquired && !delivered) { _activeBranches.Remove(branchName); }
            EndOperation();
        }
    }

    private BranchCheckout NewCheckout(string branchName, CheckpointAddress? head, bool privateCatalog = false) {
        BranchCheckout? checkout = null;
        Action editingGuard = () => EnsureEditingAllowed(checkout!);
        var session = head is null
            ? new BranchSaveSession(_storageFacade!, branchName, _modelConfiguration!, editingGuard, privateCatalog)
            : new BranchSaveSession(_storageFacade!, branchName, head.Address, _modelConfiguration!, editingGuard, privateCatalog);
        checkout = new(this, branchName, session, head, privateCatalog);
        return checkout;
    }

    private BranchCheckout NewCheckout(string branchName, CheckpointAddress head,
        BusinessPersistenceAdapter business, bool privateCatalog = false) {
        BranchCheckout? checkout = null;
        var session = new BranchSaveSession(_storageFacade!, branchName, business,
            () => EnsureEditingAllowed(checkout!), privateCatalog, head.Address);
        checkout = new(this, branchName, session, head, privateCatalog);
        return checkout;
    }

    private BranchCheckout RestoreCheckout(string branchName, CheckpointAddress head,
        out PreparedStateRestoration? candidate, bool privateCatalog = false) {
        candidate = null;
        var store = _storageFacade.Store;
        if (head.Kind == CheckpointKind.Event) { store.ValidateMetadataRelations(head.Address); }
        CheckpointAddress? state = NearestStateCore(head);
        // An Event-only prefix never consults a resident State or its model environment.
        if (state is null) { return NewCheckout(branchName, head, privateCatalog); }
        if (PreparedStateReuseEnabled && _preparedState is { } prepared && prepared.Matches(state)) {
            DecodedRevision.SchemaViewBindingEnvironment environment = prepared.BindingEnvironment
                ?? throw new InvalidDataException("A split State slot must retain its reader-issued environment.");
            GraphReadContext context = new(environment.Models, RestorationStatistics);
            context.Statistics.PreparedStateHits++;
            if (ImmutableLeafReuseEnabled && !prepared.ImmutableLeavesCollected) {
                using var leafRequirements = environment.Models.BeginSchemaRequirementCollection();
                prepared.ValidateStoredSource(store, _modelConfiguration!, state, context);
                var materializedLeaf = GraphReader.RestorePreparedState(context, prepared.Selection,
                    out var leaves, collectImmutableLeaves: true);
                var leafBusiness = BusinessPersistenceAdapter.FromLoaded(store, environment, materializedLeaf);
                BranchCheckout leafCheckout = NewCheckout(branchName, head, leafBusiness, privateCatalog);
                candidate = new(state, prepared.Selection, leafRequirements.Complete(), leaves,
                    ImmutableLeavesCollected: true, BindingEnvironment: environment);
                return leafCheckout;
            }
            prepared.ValidateStoredSource(store, _modelConfiguration!, state, context);
            var materializedHit = GraphReader.RestorePreparedState(context, prepared.Selection, out _,
                reusedLeaves: ImmutableLeafReuseEnabled ? prepared.ImmutableLeaves : null);
            return NewCheckout(branchName, head,
                BusinessPersistenceAdapter.FromLoaded(store, environment, materializedHit), privateCatalog);
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
        DecodedRevision source = DecodedRevision.ReadStoredGraph(store, state.Address, _modelConfiguration!, sourceEnvironment);
        PreparedGraphSelection selection = GraphReader.PrepareState(source, reads, state.RootId);
        foreach ((ObjectId id, NormalizedObject row) in selection.Normalized.Objects) {
            sourceEnvironment.Models.CheckObjectLayout(row.SourceLayout, $"prepared State source object {id.Value}");
            sourceEnvironment.Models.CheckObjectLayout(row.Current.Layout, $"prepared State current object {id.Value}");
        }
        bool collectLeaves = PreparedStateReuseEnabled && ImmutableLeafReuseEnabled;
        var materialized = GraphReader.RestorePreparedState(reads, selection, out var immutableLeaves,
            collectImmutableLeaves: collectLeaves);
        var business = BusinessPersistenceAdapter.FromLoaded(store, sourceEnvironment, materialized);
        BranchCheckout restored = NewCheckout(branchName, head, business, privateCatalog);
        var certificate = requirements.Complete();
        if (PreparedStateReuseEnabled) {
            candidate = new(state, selection, certificate, immutableLeaves, collectLeaves, sourceEnvironment);
        }
        return restored;
    }

    private void EnsureEditingAllowed(BranchCheckout checkout) {
        RequireWriter();
        RequireCheckoutSource(checkout);
        _storageFacade!.EnsureEditingAllowed();
        RequireSchemaWriter(checkout);
    }

    private void RequireCheckoutSource(BranchCheckout checkout) {
        if (!checkout.IsLiveFor(this) || !_activeBranches.Contains(checkout.BranchName) ||
            _storageFacade!.GetHead(checkout.BranchName) != checkout.Head.Address) {
            throw new InvalidOperationException("Session does not own the expected branch head.");
        }
        FrameAddress? state = _storageFacade.GetStateHead(checkout.BranchName);
        if (state != checkout.StateRevisionAddress || (state is null ? checkout.State is not null :
                checkout.StateId != _storageFacade.Store.ReadMetadata(state.Value).RootId)) {
            throw new InvalidOperationException("Checkout State baseline does not match the business chain.");
        }
    }

    private CheckpointAddress CommitSave(BranchCheckout checkout, IDurableObject? domainEvent,
        IDurableObject? nextState, ReadAmplificationBaseBudgetParameters? parameters, bool toShared = false) {
        if (toShared) { RequireWriter(); }
        else { RequireSchemaWriter(checkout); }
        _busy = true;
        try {
            if (toShared) { RequireCheckoutSource(checkout); }
            return PublishSave(checkout, domainEvent ?? nextState!,
                domainEvent is null ? StoredGraphKind.State : StoredGraphKind.Event, parameters ?? DefaultPolicy, toShared);
        } finally { EndOperation(); }
    }

    private CheckpointAddress PublishSave(BranchCheckout checkout, IDurableObject root, StoredGraphKind kind,
        ReadAmplificationBaseBudgetParameters parameters, bool toShared = false) {
        using PreparedBranchSave pending = checkout.SaveSession.Prepare(root, kind, parameters, toShared);
        OperationStageObserver?.Invoke(RepositoryOperationStage.AfterPrepare);
        CheckpointAddress? head = null;
        pending.Commit(graph => head = Issue(ReadHistoryCore(graph.Address)));
        checkout.Head = head!;
        return head!;
    }

    internal void PrepareAndDiscardSave(BranchCheckout checkout, IDurableObject root, StoredGraphKind kind,
        ReadAmplificationBaseBudgetParameters? parameters, bool toShared = false) {
        if (toShared) { RequireWriter(); }
        else { RequireSchemaWriter(checkout); }
        _busy = true;
        try {
            ArgumentNullException.ThrowIfNull(root);
            if (toShared) { RequireCheckoutSource(checkout); }
            using PreparedBranchSave pending = checkout.SaveSession.Prepare(root, kind, parameters ?? DefaultPolicy, toShared);
            OperationStageObserver?.Invoke(RepositoryOperationStage.AfterPrepare);
        } finally { EndOperation(); }
    }

    private IDurableObject ReadGraph(CheckpointAddress frame, CheckpointKind kind) {
        RequireAvailable();
        CheckFrame(frame, kind);
        _busy = true;
        try {
            return ReadGraphRootCore(frame);
        } finally { EndOperation(); }
    }

    private CheckpointAddress StoredHeadCore(string branchName) {
        FrameAddress address = _storageFacade!.GetHead(branchName) ?? throw new KeyNotFoundException("Branch does not exist.");
        return Issue(ReadHistoryCore(address));
    }

}
