using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph.Persistence;

/// <summary>Loads one explicitly selected World after complete exact decoding and current DTO normalization.</summary>
internal static class LoadedWorld {
    /// <summary>
    /// Freezes a new World graph into a no-Parent plan. May persist Schema registrations;
    /// does not append State, run its persistence barrier, publish, or install a baseline.
    /// The host must keep the graph stable throughout this synchronous operation.
    /// </summary>
    public static PreparedWorldRevision PrepareNew<TWorld>(
        StateRevisionStore store,
        SchemaStore schemas,
        TWorld world,
        StateModelRegistry models,
        ReadAmplificationBaseBudgetParameters parameters) where TWorld : class, IDurableObject {
        ArgumentNullException.ThrowIfNull(world);
        if (world.GetType() != typeof(TWorld)) {
            throw new ArgumentException("World must have its requested exact domain type.", nameof(world));
        }
        WorldWorkspace workspace = WorldWorkspace.Create(store, schemas, world, models);
        using PreparedWorldSave pending = workspace.Stage(parameters);
        return new(pending.RootId, pending.Revision);
    }

    /// <summary>
    /// Creates a fixed-Parent editable view. Stores must belong to the same repository and remain
    /// usable and stable for Prepare. No instance constructors, Transient hook, write or publication runs here.
    /// </summary>
    public static LoadedWorld<TWorld> Load<TWorld>(
        StateRevisionStore store,
        SchemaStore schemas,
        FrameAddress revisionAddress,
        ObjectId worldId,
        StateModelRegistry models) where TWorld : class, IDurableObject {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentOutOfRangeException.ThrowIfZero(worldId.Value, nameof(worldId));
        RevisionReadSession reads = new(store, schemas, models.Snapshot(schemas));
        MaterializedGraph<TWorld> loaded = GraphReader.Read<TWorld>(reads, revisionAddress, worldId,
            requireExactRootType: true);
        return new(WorldWorkspace.FromLoaded(reads, loaded));
    }
}

/// <summary>One loaded World and its immutable Parent-bound comparison baseline.</summary>
/// <remarks>
/// Single-threaded. Prepare never advances this baseline. The host appends its owned plan,
/// then loads the returned address to obtain a new baseline. This is not Commit or publication.
/// </remarks>
internal sealed class LoadedWorld<TWorld> where TWorld : class, IDurableObject {
    private readonly WorldWorkspace _workspace;

    internal LoadedWorld(WorldWorkspace workspace) => _workspace = workspace;

    public TWorld World => (TWorld)_workspace.World!;
    public ObjectId WorldId => _workspace.WorldId;
    public FrameAddress ParentRevisionAddress => _workspace.ParentRevisionAddress!.Value;

    /// <summary>
    /// Freezes current content against the original Parent. May persist Schema registrations;
    /// never appends State, publishes, or accepts a new Parent. Failed captures can consume IDs.
    /// </summary>
    public PreparedWorldRevision Prepare(ReadAmplificationBaseBudgetParameters parameters) {
        using PreparedWorldSave pending = _workspace.Stage(parameters);
        return new(WorldId, pending.Revision);
    }
}

/// <summary>Owned frozen State contents with a fixed Parent and the selected World ID.</summary>
/// <remarks>Pass Revision to Storage.Append; the returned address still needs host publication.</remarks>
internal sealed class PreparedWorldRevision {
    internal PreparedWorldRevision(ObjectId worldId, StateRevision revision) {
        WorldId = worldId;
        Revision = revision;
    }

    public ObjectId WorldId { get; }
    public StateRevision Revision { get; }
}
