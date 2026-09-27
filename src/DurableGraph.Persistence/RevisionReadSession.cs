using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph.Persistence;

/// <summary>One synchronous read operation's owned stored DTO cache and frozen code catalog.</summary>
/// <remarks>
/// The caller keeps both stores stable. A cache hit proves only the exact ObjectVersion's
/// body was read, not that its references are valid in another Revision. Current state,
/// Upgrade results and editable baselines are not cached. Domain instances are retained
/// only by an operation-local allocation uniqueness guard, never reused as graph output.
/// </remarks>
internal sealed class RevisionReadSession {
    private readonly Dictionary<(ObjectId Id, FrameAddress Head),
        (ObjectStateRecord Row, ObjectReaderBinding Binding, ObjectStorageInfo Storage)> _objects = [];
    private readonly HashSet<object> _mutableInstances = new(ReferenceEqualityComparer.Instance);

    internal RevisionReadSession(StateRevisionStore store, SchemaStore schemas, StateModelSnapshot models,
        GraphReadStatistics? statistics = null) {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(models);
        Store = store;
        Schemas = schemas;
        Models = models;
        Statistics = statistics ?? new();
    }

    internal StateRevisionStore Store { get; }
    internal SchemaStore Schemas { get; }
    internal StateModelSnapshot Models { get; }
    internal GraphReadStatistics Statistics { get; }

    // Independent restores may share stored DTOs, but an application allocator
    // must not return a mutable singleton already allocated anywhere in this operation.
    internal void RequireUniqueMutableInstance(object instance) {
        if (!_mutableInstances.Add(instance)) {
            throw new InvalidDataException("Independent graphs must allocate distinct mutable instances.");
        }
    }

    internal DecodedRevision Read(FrameAddress revisionAddress) =>
        RevisionDecoder.ReadCore(Store, Schemas, revisionAddress, (id, head) => {
            if (_objects.TryGetValue((id, head), out var cached)) {
                Statistics.CacheHits++;
                return cached;
            }
            var decoded = RevisionDecoder.ReadObject(Store, Schemas, revisionAddress, id,
                body => Schemas.ResolveReader(body.RepresentationId, Models));
            // Only complete, owned results enter the cache. Failed body reads are retried.
            _objects.Add((id, head), decoded);
            Statistics.DecodedObjects++;
            return decoded;
        });
}
