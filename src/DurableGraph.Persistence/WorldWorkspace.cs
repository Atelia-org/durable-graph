using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph.Persistence;

/// <summary>Owns one editable State baseline and serial candidates, independent of head publication.</summary>
internal sealed class WorldWorkspace<TWorld> where TWorld : class, IDurableObject {
    private readonly StateRevisionStore _store;
    private readonly SchemaStore _schemas;
    private readonly StateModelBinding _model;
    private readonly StateModelSnapshot _models;
    private readonly CaptureSession _capture;
    private NormalizedRevision? _baseline;
    private PreparedWorldSave<TWorld>? _pending;
    private bool _staging;

    private WorldWorkspace(StateRevisionStore store, SchemaStore schemas, TWorld world, ObjectId worldId,
        StateModelBinding model, StateModelSnapshot models, NormalizedRevision? baseline, CaptureSession capture) {
        _store = store;
        _schemas = schemas;
        World = world;
        WorldId = worldId;
        _model = model;
        _models = models;
        _baseline = baseline;
        _capture = capture;
    }

    internal TWorld World { get; private set; }
    internal ObjectId WorldId { get; private set; }
    internal FrameAddress? ParentRevisionAddress => _baseline?.RevisionAddress;

    internal static WorldWorkspace<TWorld> Create(StateRevisionStore store, SchemaStore schemas,
        TWorld world, StateModelRegistry models) {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(models);
        return CreateSnapshot(store, schemas, world, models.Snapshot(schemas));
    }

    internal static WorldWorkspace<TWorld> CreateSnapshot(StateRevisionStore store, SchemaStore schemas,
        TWorld world, StateModelSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (world.GetType() != typeof(TWorld) || !snapshot.TryGetCurrentModel(typeof(TWorld), out StateModelBinding? model)) {
            throw new ArgumentException("World must have its requested exact domain type registered.", nameof(world));
        }
        return new(store, schemas, world, default, model!, snapshot, null, new());
    }

    internal static WorldWorkspace<TWorld> Load(StateRevisionStore store, SchemaStore schemas,
        FrameAddress revisionAddress, ObjectId worldId, StateModelRegistry models) {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentOutOfRangeException.ThrowIfZero(worldId.Value, nameof(worldId));
        StateModelSnapshot snapshot = models.Snapshot(schemas);
        return LoadSnapshot(store, schemas, revisionAddress, worldId, snapshot);
    }

    internal static WorldWorkspace<TWorld> LoadSnapshot(StateRevisionStore store, SchemaStore schemas,
        FrameAddress revisionAddress, ObjectId worldId, StateModelSnapshot snapshot) =>
        LoadSnapshot(new RevisionReadSession(store, schemas, snapshot), revisionAddress, worldId);

    // Editable imports may share immutable decoded rows/strings with a pending Event, but
    // must use the independently allocated path, never the read-only pair's CLR sharing.
    internal static WorldWorkspace<TWorld> LoadSnapshot(RevisionReadSession reads,
        FrameAddress revisionAddress, ObjectId worldId) {
        MaterializedGraph<TWorld> loaded = GraphReader.Read<TWorld>(reads, revisionAddress, worldId,
            requireExactRootType: true);
        return new(reads.Store, reads.Schemas, loaded.Root, worldId, loaded.RootModel, reads.Models,
            loaded.Baseline, loaded.CreateCaptureSession());
    }

    internal PreparedWorldSave<TWorld> Stage(ReadAmplificationBaseBudgetParameters parameters) => Stage(World, parameters);

    /// <summary>Captures a same-exact-type replacement; the workspace root changes only after publication.</summary>
    internal PreparedWorldSave<TWorld> Stage(TWorld nextState, ReadAmplificationBaseBudgetParameters parameters) =>
        StageCore(nextState, parameters, independentSnapshot: false);

    /// <summary>
    /// Captures a separate root against the committed State. Dispose the candidate after the outer
    /// publication resolves: successful snapshots never install their DTOs or bindings into State.
    /// </summary>
    internal PreparedWorldSave<TWorld> StageSnapshot(IDurableObject root, ReadAmplificationBaseBudgetParameters parameters) =>
        StageCore(root, parameters, independentSnapshot: true);

    private PreparedWorldSave<TWorld> StageCore(IDurableObject root, ReadAmplificationBaseBudgetParameters parameters,
        bool independentSnapshot) {
        if (_staging || _pending is not null) {
            throw new InvalidOperationException("Resolve the current graph save before preparing another.");
        }
        ArgumentNullException.ThrowIfNull(root);
        if (independentSnapshot && _baseline is null) {
            throw new InvalidOperationException("An independent snapshot requires a committed State baseline.");
        }
        if (!independentSnapshot && root.GetType() != typeof(TWorld)) {
            throw new ArgumentException("Replacement State must have the workspace's exact domain type.", nameof(root));
        }
        _staging = true;
        CaptureContext? context = null;
        try {
            StateModelBinding? model = _model;
            if (independentSnapshot && !_models.TryGetCurrentModel(root.GetType(), out model)) {
                throw new ArgumentException("Snapshot root must have its actual domain type registered.", nameof(root));
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
                independentSnapshot ? null : (TWorld)root);
            return _pending;
        } catch {
            context?.Dispose();
            throw;
        } finally {
            _staging = false;
        }
    }

    internal void Install(PreparedWorldSave<TWorld> pending, CapturedGraph candidate,
        NormalizedRevision baseline, TWorld nextState) {
        RequirePending(pending);
        // Accept transfers the actual frozen candidate's live identity dictionary. Neither
        // recapture nor user callbacks may occur after publication.
        _capture.Accept(candidate);
        _baseline = baseline;
        World = nextState;
        WorldId = pending.RootId;
        _pending = null;
    }

    internal void Discard(PreparedWorldSave<TWorld> pending, CaptureContext context) {
        RequirePending(pending);
        context.Dispose();
        _pending = null;
    }

    private void RequirePending(PreparedWorldSave<TWorld> pending) {
        if (!ReferenceEquals(_pending, pending)) {
            throw new InvalidOperationException("The pending save does not belong to this workspace.");
        }
    }
}
