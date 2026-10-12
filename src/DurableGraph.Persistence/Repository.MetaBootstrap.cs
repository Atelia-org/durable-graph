using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph;

// Internal owner/checkout integration. The public factories still select the legacy backend.
public sealed partial class Repository {
    private readonly StateModelRegistry.FrozenConfiguration? _splitConfiguration;
    private readonly P0Repository? _splitRepository;
    private bool UsesMetaBootstrap => _splitRepository is not null;

    private Repository(GraphResources resources, StateModelRegistry.FrozenConfiguration configuration) {
        _resources = resources;
        _splitConfiguration = configuration;
        _splitRepository = new(resources);
    }

    internal static Repository CreateMetaBootstrapNew(string path, StateModelRegistry models) =>
        OpenMetaBootstrap(path, models, create: true, readOnly: false);
    internal static Repository OpenMetaBootstrapExisting(string path, StateModelRegistry models) =>
        OpenMetaBootstrap(path, models, create: false, readOnly: false);
    internal static Repository OpenMetaBootstrapReadOnlyExisting(string path, StateModelRegistry models) =>
        OpenMetaBootstrap(path, models, create: false, readOnly: true);

    private static Repository OpenMetaBootstrap(string path, StateModelRegistry models, bool create, bool readOnly) {
        ArgumentNullException.ThrowIfNull(models);
        GraphResources? resources = null;
        try {
            resources = create ? GraphResources.CreateMetaBootstrapNew(path) : readOnly
                ? GraphResources.OpenMetaBootstrapReadOnlyExisting(path) : GraphResources.OpenMetaBootstrapExisting(path);
            return new(resources, models.FreezeConfiguration());
        } catch (Exception primary) {
            try { resources?.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException("Repository opening and resource cleanup failed.", primary, cleanup); }
            throw;
        }
    }

    internal P0GraphStore MetaBootstrapStore {
        get { RequireAvailable(); RequireMetaBootstrapBackend(); return _splitRepository!.Store; }
    }
    internal FrameAddress? SharedSchemaRoot {
        get { RequireAvailable(); RequireMetaBootstrapBackend(); return _splitRepository!.SharedSchemaRoot; }
    }
    internal Action<P0SaveCheckpoint>? MetaBootstrapCheckpoint {
        get { RequireAvailable(); RequireMetaBootstrapBackend(); return _splitRepository!.Checkpoint; }
        set { RequireAvailable(); RequireMetaBootstrapBackend(); _splitRepository!.Checkpoint = value; }
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
            BranchCheckout checkout = NewMetaBootstrapCheckout(branchName, head);
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

    private void EnsureMetaBootstrapEditingAllowed(BranchCheckout checkout) {
        RequireWriter();
        RequireMetaBootstrapBackend();
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
        RequireMetaBootstrapBackend();
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

    private void RequireMetaBootstrapBackend() {
        if (!UsesMetaBootstrap) { throw new NotSupportedException("This operation requires split repository storage."); }
    }
}
