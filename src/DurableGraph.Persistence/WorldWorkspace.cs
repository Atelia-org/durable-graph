using Atelia.FrameStore;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph.Persistence;

/// <summary>Legacy storage/source adapter for the shared editable business workspace.</summary>
internal sealed class WorldWorkspace {
    private readonly StateRevisionStore _store;
    private readonly SchemaStore _schemas;
    private readonly BusinessWorkspace _workspace;

    private WorldWorkspace(StateRevisionStore store, SchemaStore schemas, BusinessWorkspace workspace) {
        _store = store;
        _schemas = schemas;
        _workspace = workspace;
    }

    internal IDurableObject? World => _workspace.Root;
    internal ObjectId WorldId => _workspace.RootId;
    internal FrameAddress? ParentRevisionAddress => _workspace.Baseline?.RevisionAddress;

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
        return new(store, schemas, new(snapshot));
    }

    internal static WorldWorkspace CreateSnapshot(StateRevisionStore store, SchemaStore schemas,
        IDurableObject world, StateModelSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(snapshot);
        return new(store, schemas, new(snapshot, world));
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
        new(reads.Store, reads.Schemas, BusinessWorkspace.FromLoaded(reads.Models, loaded));

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
        BusinessSaveCandidate candidate = _workspace.Capture(root, independentSnapshot);
        try {
            NormalizedRevision? baseline = _workspace.Baseline;
            IReadOnlyList<PreparedCapturedObject> contents = candidate.PrepareContents();
            PreparedObjectRevision prepared = baseline is null
                ? CapturedRevisionPlanner.PrepareRevision(_store, _schemas, null,
                    new PreparedCapturedGraph(null, candidate.Graph, contents), parameters)
                : LoadedRevisionPlanner.Prepare(_store, _schemas, baseline, contents, parameters, independentSnapshot);
            // Snapshot completion deliberately retains the old State baseline and its rewrite
            // obligations. Only an advancing candidate prepares a replacement baseline.
            NormalizedRevision? next = independentSnapshot ? null :
                NormalizedRevision.FromCandidate(candidate.Graph, _workspace.Models, _store, _schemas, baseline);
            candidate.CompletePreparation(next);
            return new(candidate, prepared.Revision);
        } catch {
            candidate.AbortPreparation();
            throw;
        }
    }
}
