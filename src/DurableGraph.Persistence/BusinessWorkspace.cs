using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph.Persistence;

/// <summary>One editable graph, its complete normalized baseline and one actual Capture candidate.</summary>
internal sealed class BusinessWorkspace {
    private readonly StateModelSnapshot _models;
    private readonly CaptureSession _capture;
    private NormalizedRevision? _baseline;
    private BusinessSaveCandidate? _pending;
    private bool _staging;

    internal BusinessWorkspace(StateModelSnapshot models, IDurableObject? root = null)
        : this(models, new(), null, root, default) {
        if (root is not null && !models.TryGetCurrentModel(root.GetType(), out _)) {
            throw new ArgumentException("Root must have its actual domain type registered.", nameof(root));
        }
    }

    private BusinessWorkspace(StateModelSnapshot models, CaptureSession capture,
        NormalizedRevision? baseline, IDurableObject? root, ObjectId rootId) {
        ArgumentNullException.ThrowIfNull(models);
        _models = models;
        _capture = capture;
        _baseline = baseline;
        Root = root;
        RootId = rootId;
    }

    internal StateModelSnapshot Models => _models;
    internal NormalizedRevision? Baseline => _baseline;
    internal IDurableObject? Root { get; private set; }
    internal ObjectId RootId { get; private set; }

    // Import the controlled loader's actual object identities and allocation cursor.
    internal static BusinessWorkspace FromLoaded<T>(StateModelSnapshot models, MaterializedGraph<T> loaded)
        where T : class, IDurableObject =>
        new(models, loaded.CreateCaptureSession(), loaded.Baseline, loaded.Root, loaded.RootId);

    internal BusinessSaveCandidate Capture(IDurableObject root, bool independentSnapshot) {
        if (_staging || _pending is not null) {
            throw new InvalidOperationException("Resolve the current graph save before preparing another.");
        }
        ArgumentNullException.ThrowIfNull(root);
        // This guard includes lazy model resolution, Capture callbacks and the adapter's
        // subsequent catalog/body preparation, until CompletePreparation returns.
        _staging = true;
        CaptureContext? context = null;
        try {
            if (!_models.TryGetCurrentModel(root.GetType(), out StateModelBinding? model)) {
                throw new ArgumentException("Root must have its actual domain type registered.", nameof(root));
            }
            context = _capture.BeginCapture(_models);
            ObjectId rootId = model!.AddRoot(context, root);
            _pending = new(this, context, context.Seal(), root, rootId, independentSnapshot);
            return _pending;
        } catch {
            context?.Dispose();
            _staging = false;
            throw;
        }
    }

    internal IReadOnlyList<PreparedCapturedObject> PrepareContents(BusinessSaveCandidate candidate) {
        RequirePreparing(candidate);
        return _baseline is null ? _capture.Prepare(candidate.Graph).Objects
            : _capture.PrepareAgainst(candidate.Graph, _baseline.CurrentDtos);
    }

    internal void CompletePreparation(BusinessSaveCandidate candidate, NormalizedRevision? next) {
        RequirePreparing(candidate);
        if (candidate.IsIndependentSnapshot != (next is null)) {
            throw new InvalidOperationException("Only an advancing State candidate prepares a replacement baseline.");
        }
        _staging = false;
    }

    internal void RequirePreparing(BusinessSaveCandidate candidate) {
        RequirePending(candidate);
        if (!_staging) { throw new InvalidOperationException("The candidate's preparation has already completed."); }
    }

    internal void RequireReady(BusinessSaveCandidate candidate) {
        RequirePending(candidate);
        if (_staging) { throw new InvalidOperationException("The graph candidate is still being prepared."); }
    }

    internal void Accept(BusinessSaveCandidate candidate, NormalizedRevision installation) {
        RequireReady(candidate);
        if (candidate.IsIndependentSnapshot) { throw new InvalidOperationException("Independent snapshots cannot advance State."); }
        // Transfer the actual sealed identity dictionary, without recapture or user callbacks.
        _capture.Accept(candidate.Graph);
        _baseline = installation;
        Root = candidate.Root;
        RootId = candidate.RootId;
        _pending = null;
    }

    internal void Discard(BusinessSaveCandidate candidate) {
        RequireReady(candidate);
        Release(candidate);
    }

    internal void AbortPreparation(BusinessSaveCandidate candidate) {
        // Failure cleanup is confined to the same candidate; a rejected nested Capture
        // never receives a candidate with which to clear the outer preparation.
        RequirePending(candidate);
        Release(candidate);
        _staging = false;
    }

    private void Release(BusinessSaveCandidate candidate) {
        candidate.ReleaseContext();
        _pending = null;
    }

    private void RequirePending(BusinessSaveCandidate candidate) {
        if (!ReferenceEquals(_pending, candidate)) {
            throw new InvalidOperationException("The pending graph does not belong to this workspace.");
        }
    }
}

/// <summary>The actual sealed Capture and its preparation/installation lifetime.</summary>
internal sealed class BusinessSaveCandidate : IDisposable {
    private BusinessWorkspace? _owner;
    private readonly CaptureContext _context;
    private NormalizedRevision? _next;
    private NormalizedRevision? _installation;

    internal BusinessSaveCandidate(BusinessWorkspace owner, CaptureContext context, CapturedGraph graph,
        IDurableObject root, ObjectId rootId, bool independentSnapshot) {
        _owner = owner;
        _context = context;
        Graph = graph;
        Root = root;
        RootId = rootId;
        IsIndependentSnapshot = independentSnapshot;
    }

    internal CapturedGraph Graph { get; }
    internal IDurableObject Root { get; }
    internal ObjectId RootId { get; }
    internal bool IsIndependentSnapshot { get; }
    internal NormalizedRevision NextBaseline {
        get {
            RequireReady();
            if (_installation is not null) { throw new InvalidOperationException("This graph installation is already prepared."); }
            return _next ?? throw new InvalidOperationException("Independent snapshots cannot prepare a State installation.");
        }
    }

    internal IReadOnlyList<PreparedCapturedObject> PrepareContents() => RequireOwner().PrepareContents(this);

    internal void CompletePreparation(NormalizedRevision? next) {
        RequireOwner().CompletePreparation(this, next);
        _next = next;
    }

    internal void RequireReady() => RequireOwner().RequireReady(this);

    internal void PrepareInstallation(NormalizedRevision installation) {
        RequireReady();
        ArgumentNullException.ThrowIfNull(installation);
        if (IsIndependentSnapshot || _next is null) { throw new InvalidOperationException("Independent snapshots cannot advance State."); }
        if (_installation is not null) { throw new InvalidOperationException("This graph installation is already prepared."); }
        // Concrete backend completion preserves this candidate's DTO table and issuing
        // environment. Another read/save's otherwise valid material cannot replace it.
        if (installation.RevisionAddress == default || !ReferenceEquals(installation.CurrentDtos, _next.CurrentDtos) ||
            !ReferenceEquals(installation.SourceModels, _next.SourceModels) ||
            !ReferenceEquals(installation.SourceStore, _next.SourceStore) ||
            !ReferenceEquals(installation.SourceSchemas, _next.SourceSchemas) ||
            !ReferenceEquals(installation.SplitStore, _next.SplitStore)) {
            throw new InvalidDataException("Installation must complete this actual candidate in its issuing backend and model environment.");
        }
        _installation = installation;
    }

    internal void Install() {
        BusinessWorkspace owner = RequireOwner();
        owner.RequireReady(this);
        NormalizedRevision installation = _installation
            ?? throw new InvalidOperationException("Prepare the installation before publishing and installing it.");
        owner.Accept(this, installation);
        _owner = null;
    }

    internal void AbortPreparation() {
        if (_owner is { } owner) {
            owner.AbortPreparation(this);
            _owner = null;
        }
    }

    public void Dispose() {
        if (_owner is { } owner) {
            owner.Discard(this);
            _owner = null;
        }
    }

    internal void ReleaseContext() => _context.Dispose();

    private BusinessWorkspace RequireOwner() => _owner
        ?? throw new InvalidOperationException("The graph candidate has already been resolved.");
}
