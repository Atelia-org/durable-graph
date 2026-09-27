using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph.Persistence;

/// <summary>Owns one editable State baseline and serial candidates, independent of head publication.</summary>
internal sealed class WorldWorkspace {
    private readonly StateRevisionStore _store;
    private readonly SchemaStore _schemas;
    private readonly StateModelSnapshot _models;
    private readonly CaptureSession _capture;
    private NormalizedRevision? _baseline;
    private PreparedWorldSave? _pending;
    private bool _staging;

    private WorldWorkspace(StateRevisionStore store, SchemaStore schemas, IDurableObject? world, ObjectId worldId,
        StateModelSnapshot models, NormalizedRevision? baseline, CaptureSession capture) {
        _store = store;
        _schemas = schemas;
        World = world;
        WorldId = worldId;
        _models = models;
        _baseline = baseline;
        _capture = capture;
    }

    internal IDurableObject? World { get; private set; }
    internal ObjectId WorldId { get; private set; }
    internal FrameAddress? ParentRevisionAddress => _baseline?.RevisionAddress;

    internal static WorldWorkspace Create(StateRevisionStore store, SchemaStore schemas,
        IDurableObject world, StateModelRegistry models) {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(models);
        return CreateSnapshot(store, schemas, world, models.Snapshot(schemas));
    }

    internal static WorldWorkspace CreateEmpty(StateRevisionStore store, SchemaStore schemas,
        StateModelSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(snapshot);
        return new(store, schemas, null, default, snapshot, null, new());
    }

    internal static WorldWorkspace CreateSnapshot(StateRevisionStore store, SchemaStore schemas,
        IDurableObject world, StateModelSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.TryGetCurrentModel(world.GetType(), out _)) {
            throw new ArgumentException("World must have its actual domain type registered.", nameof(world));
        }
        return new(store, schemas, world, default, snapshot, null, new());
    }

    internal static WorldWorkspace Load(StateRevisionStore store, SchemaStore schemas,
        FrameAddress revisionAddress, ObjectId worldId, StateModelRegistry models) {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentOutOfRangeException.ThrowIfZero(worldId.Value, nameof(worldId));
        StateModelSnapshot snapshot = models.Snapshot(schemas);
        return LoadSnapshot(store, schemas, revisionAddress, worldId, snapshot);
    }

    internal static WorldWorkspace LoadSnapshot(StateRevisionStore store, SchemaStore schemas,
        FrameAddress revisionAddress, ObjectId worldId, StateModelSnapshot snapshot) =>
        LoadSnapshot(new RevisionReadSession(store, schemas, snapshot), revisionAddress, worldId);

    // Editable imports may share immutable decoded rows/strings with other reads, but
    // must use the independently allocated path, never the read-only pair's CLR sharing.
    internal static WorldWorkspace LoadSnapshot(RevisionReadSession reads,
        FrameAddress revisionAddress, ObjectId worldId) {
        MaterializedGraph<IDurableObject> loaded = GraphReader.Read<IDurableObject>(reads, revisionAddress, worldId);
        return FromLoaded(reads, loaded);
    }

    internal static WorldWorkspace FromLoaded<TWorld>(RevisionReadSession reads,
        MaterializedGraph<TWorld> loaded) where TWorld : class, IDurableObject =>
        new(reads.Store, reads.Schemas, loaded.Root, loaded.RootId, reads.Models,
            loaded.Baseline, loaded.CreateCaptureSession());

    internal PreparedWorldSave Stage(ReadAmplificationBaseBudgetParameters parameters) =>
        Stage(World ?? throw new InvalidOperationException("An explicit State root is required before the first State."), parameters);

    /// <summary>Captures an actual-type replacement; the workspace root changes only after publication.</summary>
    internal PreparedWorldSave Stage(IDurableObject nextState, ReadAmplificationBaseBudgetParameters parameters) =>
        StageCore(nextState, parameters, independentSnapshot: false);

    /// <summary>
    /// Captures a separate root against the committed State, or as a complete Base before any State.
    /// Dispose the candidate after the outer
    /// publication resolves: successful snapshots never install their DTOs or bindings into State.
    /// </summary>
    internal PreparedWorldSave StageSnapshot(IDurableObject root, ReadAmplificationBaseBudgetParameters parameters) =>
        StageCore(root, parameters, independentSnapshot: true);

    private PreparedWorldSave StageCore(IDurableObject root, ReadAmplificationBaseBudgetParameters parameters,
        bool independentSnapshot) {
        if (_staging || _pending is not null) {
            throw new InvalidOperationException("Resolve the current graph save before preparing another.");
        }
        ArgumentNullException.ThrowIfNull(root);
        _staging = true;
        CaptureContext? context = null;
        try {
            if (!_models.TryGetCurrentModel(root.GetType(), out StateModelBinding? model)) {
                throw new ArgumentException("Root must have its actual domain type registered.", nameof(root));
            }
            context = _capture.BeginCapture(_models);
            ObjectId rootId = model!.AddRoot(context, root);
            CapturedGraph candidate = context.Seal();
            PreparedObjectRevision prepared = _baseline is null
                ? CapturedRevisionPlanner.PrepareRevision(_store, _schemas, null, _capture.Prepare(candidate), parameters)
                : LoadedRevisionPlanner.Prepare(_store, _schemas, _baseline,
                    _capture.PrepareAgainst(candidate, _baseline.CurrentDtos), parameters, independentSnapshot);
            // Snapshot completion deliberately retains the old State baseline and its rewrite
            // obligations. Only an advancing candidate prepares a replacement baseline.
            NormalizedRevision? next = independentSnapshot ? null :
                NormalizedRevision.FromCandidate(candidate, _models, _store, _schemas, _baseline);
            _pending = new(this, context, candidate, rootId, prepared.Revision, next,
                independentSnapshot ? null : root);
            return _pending;
        } catch {
            context?.Dispose();
            throw;
        } finally {
            _staging = false;
        }
    }

    internal void Install(PreparedWorldSave pending, CapturedGraph candidate,
        NormalizedRevision baseline, IDurableObject nextState) {
        RequirePending(pending);
        // Accept transfers the actual frozen candidate's live identity dictionary. Neither
        // recapture nor user callbacks may occur after publication.
        _capture.Accept(candidate);
        _baseline = baseline;
        World = nextState;
        WorldId = pending.RootId;
        _pending = null;
    }

    internal void Discard(PreparedWorldSave pending, CaptureContext context) {
        RequirePending(pending);
        context.Dispose();
        _pending = null;
    }

    private void RequirePending(PreparedWorldSave pending) {
        if (!ReferenceEquals(_pending, pending)) {
            throw new InvalidOperationException("The pending save does not belong to this workspace.");
        }
    }
}
