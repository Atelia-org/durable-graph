using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.FrameStore;

namespace Atelia.DurableGraph;

// Concrete owner/checkout integration, shared by the public and internal probe factories.
public sealed partial class Repository {
    private readonly StateModelRegistry.FrozenConfiguration _splitConfiguration;
    private readonly P0Repository _splitRepository;

    private Repository(GraphResources resources, StateModelRegistry.FrozenConfiguration configuration) {
        _resources = resources;
        _splitConfiguration = configuration;
        _splitRepository = new(resources);
    }

    internal static Repository CreateMetaBootstrapNew(string path, StateModelRegistry models,
        long readCacheBudgetBytes = P0GraphStore.DefaultReadCacheBudgetBytes, Action<uint>? afterCreateRef = null,
        long rotationThresholdBytes = GraphResources.DefaultRotationThresholdBytes) =>
        OpenMetaBootstrap(path, models, create: true, readOnly: false, readCacheBudgetBytes, afterCreateRef, rotationThresholdBytes);
    internal static Repository OpenMetaBootstrapExisting(string path, StateModelRegistry models,
        long readCacheBudgetBytes = P0GraphStore.DefaultReadCacheBudgetBytes,
        long rotationThresholdBytes = GraphResources.DefaultRotationThresholdBytes) =>
        OpenMetaBootstrap(path, models, create: false, readOnly: false, readCacheBudgetBytes, rotationThresholdBytes: rotationThresholdBytes);
    internal static Repository OpenMetaBootstrapReadOnlyExisting(string path, StateModelRegistry models,
        long readCacheBudgetBytes = P0GraphStore.DefaultReadCacheBudgetBytes) =>
        OpenMetaBootstrap(path, models, create: false, readOnly: true, readCacheBudgetBytes);

    private static Repository OpenMetaBootstrap(string path, StateModelRegistry models, bool create, bool readOnly,
        long readCacheBudgetBytes, Action<uint>? afterCreateRef = null,
        long rotationThresholdBytes = GraphResources.DefaultRotationThresholdBytes) {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentOutOfRangeException.ThrowIfNegative(readCacheBudgetBytes);
        GraphResources? resources = null;
        try {
            resources = create ? GraphResources.CreateMetaBootstrapNew(path, readCacheBudgetBytes, afterCreateRef, rotationThresholdBytes) : readOnly
                ? GraphResources.OpenMetaBootstrapReadOnlyExisting(path, readCacheBudgetBytes)
                : GraphResources.OpenMetaBootstrapExisting(path, readCacheBudgetBytes, rotationThresholdBytes);
            return new(resources, models.FreezeConfiguration());
        } catch (Exception primary) {
            try { resources?.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException("Repository opening and resource cleanup failed.", primary, cleanup); }
            throw;
        }
    }

    internal P0GraphStore MetaBootstrapStore {
        get { RequireAvailable(); return _splitRepository.Store; }
    }
    internal FrameAddress? SharedSchemaRoot {
        get { RequireAvailable(); return _splitRepository.SharedSchemaRoot; }
    }
    internal Action<P0SaveCheckpoint>? MetaBootstrapCheckpoint {
        get { RequireAvailable(); return _splitRepository.Checkpoint; }
        set { RequireAvailable(); _splitRepository.Checkpoint = value; }
    }

    private BranchCheckout CreateMetaBootstrapBranch(string branchName, IDurableObject root, P0GraphKind kind,
        ReadAmplificationBaseBudgetParameters? parameters) {
        RequireWriter();
        _busy = true;
        bool acquired = false;
        bool delivered = false;
        try {
            ArgumentNullException.ThrowIfNull(root);
            RepositoryRootCodec.ValidateBranchName(branchName);
            if (_splitRepository!.GetHead(branchName) is not null) { throw new InvalidOperationException("Branch name already exists."); }
            acquired = _activeBranches.Add(branchName);
            if (!acquired) { throw new InvalidOperationException("Branch already has an active checkout."); }
            BranchCheckout checkout = NewMetaBootstrapCheckout(branchName, null);
            PublishMetaBootstrap(checkout, root, kind, parameters ?? DefaultPolicy);
            delivered = true;
            return checkout;
        } finally {
            if (acquired && !delivered) { _activeBranches.Remove(branchName); }
            EndOperation();
        }
    }

    private BranchCheckout CheckoutMetaBootstrap(string branchName) {
        RequireWriter();
        _busy = true;
        bool acquired = false;
        bool delivered = false;
        try {
            CheckpointAddress head = MetaBootstrapHeadCore(branchName);
            acquired = _activeBranches.Add(branchName);
            if (!acquired) { throw new InvalidOperationException("Branch already has an active checkout."); }
            BranchCheckout checkout = RestoreMetaBootstrapCheckout(branchName, head, out PreparedStateRestoration? candidate);
            if (candidate is not null) { _preparedState = candidate; }
            delivered = true;
            return checkout;
        } finally {
            if (acquired && !delivered) { _activeBranches.Remove(branchName); }
            EndOperation();
        }
    }

    private BranchCheckout NewMetaBootstrapCheckout(string branchName, CheckpointAddress? head) {
        BranchCheckout? checkout = null;
        Action editingGuard = () => EnsureMetaBootstrapEditingAllowed(checkout!);
        var session = head is null
            ? new P0Session(_splitRepository!, branchName, _splitConfiguration!, editingGuard)
            : new P0Session(_splitRepository!, branchName, head.Address, _splitConfiguration!, editingGuard);
        checkout = new(this, branchName, session, head);
        return checkout;
    }

    private BranchCheckout NewMetaBootstrapCheckout(string branchName, CheckpointAddress head,
        P0BusinessWorkspace business) {
        BranchCheckout? checkout = null;
        var session = new P0Session(_splitRepository!, branchName, business,
            () => EnsureMetaBootstrapEditingAllowed(checkout!));
        checkout = new(this, branchName, session, head);
        return checkout;
    }

    private BranchCheckout RestoreMetaBootstrapCheckout(string branchName, CheckpointAddress head,
        out PreparedStateRestoration? candidate) {
        candidate = null;
        var store = _splitRepository.Store;
        if (head.Kind == GraphFrameKind.Event) { store.ValidateMetadataRelations(head.Address); }
        CheckpointAddress? state = NearestStateCore(head);
        // An Event-only prefix never consults a resident State or its model environment.
        if (state is null) { return NewMetaBootstrapCheckout(branchName, head); }
        if (PreparedStateReuseEnabled && _preparedState is { } prepared && prepared.Matches(state)) {
            DecodedRevision.SplitBindingEnvironment environment = prepared.BindingEnvironment
                ?? throw new InvalidDataException("A split State slot must retain its reader-issued environment.");
            GraphReadContext context = new(environment.Models, RestorationStatistics);
            context.Statistics.PreparedStateHits++;
            if (ImmutableLeafReuseEnabled && !prepared.ImmutableLeavesCollected) {
                using var leafRequirements = environment.Models.BeginSchemaRequirementCollection();
                prepared.ValidateSplit(store, _splitConfiguration!, state, context);
                var materializedLeaf = GraphReader.RestorePreparedState(context, prepared.Selection,
                    out var leaves, collectImmutableLeaves: true);
                var leafBusiness = P0BusinessWorkspace.FromLoaded(store, environment, materializedLeaf);
                BranchCheckout leafCheckout = NewMetaBootstrapCheckout(branchName, head, leafBusiness);
                candidate = new(state, prepared.Selection, leafRequirements.Complete(), leaves,
                    ImmutableLeavesCollected: true, BindingEnvironment: environment);
                return leafCheckout;
            }
            prepared.ValidateSplit(store, _splitConfiguration!, state, context);
            var materializedHit = GraphReader.RestorePreparedState(context, prepared.Selection, out _,
                reusedLeaves: ImmutableLeafReuseEnabled ? prepared.ImmutableLeaves : null);
            return NewMetaBootstrapCheckout(branchName, head,
                P0BusinessWorkspace.FromLoaded(store, environment, materializedHit));
        }

        var metadata = store.ReadMetadata(state.Address);
        if (metadata.Kind != P0GraphKind.State || metadata.RootId != state.RootId || metadata.SchemaRoot is not { } schemaRoot) {
            throw new InvalidDataException("Editable State must select its actual root and Schema pin.");
        }
        var sourceEnvironment = DecodedRevision.SplitBindingEnvironment.Load(store, _splitConfiguration!, schemaRoot);
        GraphReadContext reads = new(sourceEnvironment.Models, RestorationStatistics);
        reads.Statistics.PreparedStateMisses++;
        // Include stored reader/body checks as well as normalization and first full materialization.
        using var requirements = sourceEnvironment.Models.BeginSchemaRequirementCollection();
        DecodedRevision source = DecodedRevision.ReadSplit(store, state.Address, _splitConfiguration!, sourceEnvironment);
        PreparedGraphSelection selection = GraphReader.PrepareState(source, reads, state.RootId);
        foreach ((ObjectId id, NormalizedObject row) in selection.Normalized.Objects) {
            sourceEnvironment.Models.CheckObjectLayout(row.SourceLayout, $"prepared State source object {id.Value}");
            sourceEnvironment.Models.CheckObjectLayout(row.Current.Layout, $"prepared State current object {id.Value}");
        }
        bool collectLeaves = PreparedStateReuseEnabled && ImmutableLeafReuseEnabled;
        var materialized = GraphReader.RestorePreparedState(reads, selection, out var immutableLeaves,
            collectImmutableLeaves: collectLeaves);
        var business = P0BusinessWorkspace.FromLoaded(store, sourceEnvironment, materialized);
        BranchCheckout restored = NewMetaBootstrapCheckout(branchName, head, business);
        var certificate = requirements.Complete();
        if (PreparedStateReuseEnabled) {
            candidate = new(state, selection, certificate, immutableLeaves, collectLeaves, sourceEnvironment);
        }
        return restored;
    }

    private void EnsureMetaBootstrapEditingAllowed(BranchCheckout checkout) {
        RequireWriter();
        RequireMetaBootstrapCheckout(checkout);
        _splitRepository!.EnsureEditingAllowed();
    }

    private void RequireMetaBootstrapCheckout(BranchCheckout checkout) {
        if (!checkout.IsLiveFor(this) || !_activeBranches.Contains(checkout.BranchName) ||
            _splitRepository!.GetHead(checkout.BranchName) != checkout.Head.Address) {
            throw new InvalidOperationException("Session does not own the expected branch head.");
        }
        FrameAddress? state = _splitRepository.GetStateHead(checkout.BranchName);
        if (state != checkout.StateRevisionAddress || (state is null ? checkout.State is not null :
                checkout.StateId != _splitRepository.Store.ReadMetadata(state.Value).RootId)) {
            throw new InvalidOperationException("Checkout State baseline does not match the business chain.");
        }
    }

    private CheckpointAddress CommitMetaBootstrap(BranchCheckout checkout, IDurableObject? domainEvent,
        IDurableObject? nextState, ReadAmplificationBaseBudgetParameters? parameters) {
        RequireWriter();
        _busy = true;
        try {
            RequireMetaBootstrapCheckout(checkout);
            return PublishMetaBootstrap(checkout, domainEvent ?? nextState!,
                domainEvent is null ? P0GraphKind.State : P0GraphKind.Event, parameters ?? DefaultPolicy);
        } finally { EndOperation(); }
    }

    private CheckpointAddress PublishMetaBootstrap(BranchCheckout checkout, IDurableObject root, P0GraphKind kind,
        ReadAmplificationBaseBudgetParameters parameters) {
        using P0SessionSave pending = checkout.SplitSession.Prepare(root, kind, parameters);
        Checkpoint?.Invoke(CommitCheckpoint.AfterPrepare);
        CheckpointAddress? head = null;
        pending.Commit(graph => head = Issue(ReadHistoryCore(graph.Address)));
        checkout.Head = head!;
        return head!;
    }

    internal void PrepareAndDiscardMetaBootstrap(BranchCheckout checkout, IDurableObject root, P0GraphKind kind,
        ReadAmplificationBaseBudgetParameters? parameters) {
        RequireWriter();
        _busy = true;
        try {
            ArgumentNullException.ThrowIfNull(root);
            RequireMetaBootstrapCheckout(checkout);
            using P0SessionSave pending = checkout.SplitSession.Prepare(root, kind, parameters ?? DefaultPolicy);
            Checkpoint?.Invoke(CommitCheckpoint.AfterPrepare);
        } finally { EndOperation(); }
    }

    private IDurableObject ReadMetaBootstrap(CheckpointAddress frame, GraphFrameKind kind) {
        RequireAvailable();
        CheckFrame(frame, kind);
        _busy = true;
        try {
            return ReadMetaBootstrapRootCore(frame);
        } finally { EndOperation(); }
    }

    private CheckpointAddress MetaBootstrapHeadCore(string branchName) {
        FrameAddress address = _splitRepository!.GetHead(branchName) ?? throw new KeyNotFoundException("Branch does not exist.");
        return Issue(ReadHistoryCore(address));
    }

}
