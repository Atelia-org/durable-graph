using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph.Persistence;

/// <summary>Restores explicit graph selections without owning a publication head or editing session.</summary>
internal static class GraphReader {
    internal static MaterializedGraph<T> Read<T>(StateRevisionStore store, SchemaStore schemas,
        FrameAddress revisionAddress, ObjectId rootId, StateModelSnapshot models,
        bool requireExactRootType = false) where T : class, IDurableObject {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(models);
        return Read<T>(new RevisionReadSession(store, schemas, models), revisionAddress, rootId, requireExactRootType);
    }

    /// <summary>Reuses stored decoding while retaining an independently allocated editable import.</summary>
    internal static MaterializedGraph<T> Read<T>(RevisionReadSession session,
        FrameAddress revisionAddress, ObjectId rootId, bool requireExactRootType = false) where T : class, IDurableObject {
        ArgumentNullException.ThrowIfNull(session);
        ReadSelection selection = Prepare<T>(session, revisionAddress, rootId, requireExactRootType);
        NormalizedRevision normalized = selection.Normalized;
        Dictionary<ObjectId, object> instances = [];
        Dictionary<object, ObjectId> bindings = new(ReferenceEqualityComparer.Instance);
        // All allocations precede all hydration, preserving forward/shared/cyclic references.
        foreach (ObjectId id in selection.Reachable) {
            NormalizedObject row = normalized.Objects[id];
            object allocated = Allocate(row, bindings, session.Statistics);
            if (row.Current.Kind != ObjectStateKind.String) { session.RequireUniqueMutableInstance(allocated); }
            instances.Add(id, allocated);
        }
        ObjectReadTable table = new(instances);
        foreach ((ObjectId id, object instance) in instances) {
            Hydrate(normalized.Objects[id], instance, table, session.Statistics);
        }
        // Keep full source string identity for a later editable import, including strings
        // made unreachable by Upgrade. Empty IDs share one instance: import the smallest ID
        // without rewriting any baseline DTO slots. Capture then observes necessary changes.
        foreach (NormalizedObject row in normalized.Objects.Values.OrderBy(static row => row.Current.Id)) {
            if (row.Current.Kind == ObjectStateKind.String) {
                bindings.TryAdd(row.Current.StringContent, row.Current.Id);
            }
        }
        ulong nextId = (ulong)normalized.Objects.Keys.Max().Value + 1;
        return new((T)instances[rootId], rootId, selection.RootModel, normalized, nextId, bindings);
    }

    /// <summary>
    /// Experimental read-only snapshots in input order. Both restores must succeed before
    /// delivery; user callback side effects are not rolled back. Cross-graph instance sharing
    /// or separation is not a contract. Shared results never feed editable imports.
    /// </summary>
    internal static (TFirst First, TSecond Second) ReadPair<TFirst, TSecond>(
        StateRevisionStore store, SchemaStore schemas,
        FrameAddress firstRevisionAddress, ObjectId firstRootId,
        FrameAddress secondRevisionAddress, ObjectId secondRootId,
        StateModelSnapshot models, GraphReadStatistics? statistics = null)
        where TFirst : class, IDurableObject where TSecond : class, IDurableObject {
        RevisionReadSession session = new(store, schemas, models, statistics);
        statistics = session.Statistics;
        ReadSelection first = Prepare<TFirst>(session, firstRevisionAddress, firstRootId);
        ReadSelection second = Prepare<TSecond>(session, secondRevisionAddress, secondRootId);
        HashSet<ObjectId> shared = FindSharedClosure(first, second);
        Dictionary<ObjectId, object> firstInstances = [];
        Dictionary<ObjectId, object> secondInstances = [];
        // A callback that returns a singleton must not accidentally merge two versions or IDs.
        // Only the proven shared rows below, and canonical Empty, may cross this guard.
        Dictionary<object, ObjectId> allocations = new(ReferenceEqualityComparer.Instance);
        foreach (ObjectId id in first.Reachable) {
            firstInstances.Add(id, Allocate(first.Normalized.Objects[id], allocations, statistics));
        }
        foreach (ObjectId id in second.Reachable) {
            if (shared.Contains(id)) {
                secondInstances.Add(id, firstInstances[id]);
                if (statistics is not null) { statistics.SharedObjects++; }
            } else {
                object allocated = Allocate(second.Normalized.Objects[id], allocations, statistics);
                secondInstances.Add(id, allocated);
                // Empty may also share across different heads; report actual same-ID reuse.
                if (statistics is not null && firstInstances.TryGetValue(id, out object? firstInstance) &&
                    ReferenceEquals(firstInstance, allocated)) {
                    statistics.SharedObjects++;
                }
            }
        }
        ObjectReadTable firstTable = new(firstInstances);
        ObjectReadTable secondTable = new(secondInstances);
        // Both complete instance tables exist before any callback sees reference targets.
        // Every edge of a shared object stays in the shared set, so either table is valid.
        foreach ((ObjectId id, object instance) in firstInstances) {
            Hydrate(first.Normalized.Objects[id], instance, firstTable, statistics);
        }
        foreach ((ObjectId id, object instance) in secondInstances) {
            if (!shared.Contains(id)) {
                Hydrate(second.Normalized.Objects[id], instance, secondTable, statistics);
            }
        }
        return ((TFirst)firstInstances[firstRootId], (TSecond)secondInstances[secondRootId]);
    }

    private static ReadSelection Prepare<T>(RevisionReadSession session, FrameAddress revisionAddress,
        ObjectId rootId, bool requireExactRootType = false) where T : class, IDurableObject {
        ArgumentOutOfRangeException.ThrowIfZero(rootId.Value, nameof(rootId));
        DecodedRevision decoded = session.Read(revisionAddress);
        // Current values and business Upgrade callbacks remain independent for each view,
        // even when every stored DTO came from the operation's exact-version cache.
        NormalizedRevision normalized = NormalizedRevision.Create(decoded, session.Models);
        if (!normalized.Objects.TryGetValue(rootId, out NormalizedObject? root) ||
            root.Model is not StateModelBinding rootModel || rootModel.DomainType.IsAbstract ||
            (requireExactRootType ? rootModel.DomainType != typeof(T) : !typeof(T).IsAssignableFrom(rootModel.DomainType))) {
            throw new InvalidDataException("Root ID must select a durable object of the requested current domain type.");
        }
        ReachableVisitor visitor = new();
        visitor.Add(rootId);
        for (int index = 0; index < visitor.Ids.Count; index++) {
            NormalizedObject row = normalized.Objects[visitor.Ids[index]];
            row.Model.VisitReferences(row.Current, visitor);
        }
        return new(decoded, normalized, rootModel, visitor.Ids);
    }

    private static HashSet<ObjectId> FindSharedClosure(ReadSelection first, ReadSelection second) {
        HashSet<ObjectId> secondReachable = new(second.Reachable);
        HashSet<ObjectId> candidates = [];
        foreach (ObjectId id in first.Reachable) {
            if (secondReachable.Contains(id) && first.Decoded.ObjectHeads[id] == second.Decoded.ObjectHeads[id] &&
                HasSameCurrentState(first.Normalized.Objects[id], second.Normalized.Objects[id])) {
                candidates.Add(id);
            }
        }

        Dictionary<ObjectId, List<ObjectId>> dependents = [];
        HashSet<ObjectId> excluded = [];
        foreach (ObjectId id in candidates) {
            ReferenceVisitor visitor = new(target => {
                if (target.IsNull) { return; }
                if (!candidates.Contains(target)) {
                    excluded.Add(id);
                } else {
                    if (!dependents.TryGetValue(target, out List<ObjectId>? owners)) {
                        dependents.Add(target, owners = []);
                    }
                    owners.Add(id);
                }
            });
            // Visit both current views through their actual binding, including container
            // entries and nested value slots. No class-field-only description is maintained.
            NormalizedObject firstRow = first.Normalized.Objects[id];
            NormalizedObject secondRow = second.Normalized.Objects[id];
            firstRow.Model.VisitReferences(firstRow.Current, visitor);
            secondRow.Model.VisitReferences(secondRow.Current, visitor);
        }
        Queue<ObjectId> pending = new(excluded);
        while (pending.TryDequeue(out ObjectId id)) {
            if (!candidates.Remove(id)) { continue; }
            if (dependents.TryGetValue(id, out List<ObjectId>? owners)) {
                foreach (ObjectId owner in owners) { pending.Enqueue(owner); }
            }
        }
        return candidates;
    }

    private static bool HasSameCurrentState(NormalizedObject first, NormalizedObject second) {
        if (first.RequiresRewrite || second.RequiresRewrite || !ReferenceEquals(first.Model, second.Model) ||
            !first.Current.Layout.Equals(second.Current.Layout)) {
            return false;
        }
        if (first.Current.Kind == ObjectStateKind.String) {
            return ReferenceEquals(first.Current.StringContent, second.Current.StringContent);
        }
        ICapturedStatePreparation? firstPreparation = first.Current.Preparation;
        ICapturedStatePreparation? secondPreparation = second.Current.Preparation;
        if (firstPreparation is null || !ReferenceEquals(firstPreparation, secondPreparation)) { return false; }
        // Keep the explicit complete-value proof for read-only sharing. Exact-current durable
        // normalization is identity, but removing this comparison requires a separate proof.
        // Missing comparison is not equality; errors propagate without preparing object bodies.
        return firstPreparation.ProvesSameState(first.Current, second.Current);
    }

    private static object Allocate(NormalizedObject row, Dictionary<object, ObjectId> allocations,
        GraphReadStatistics? statistics) {
        if (statistics is not null) { statistics.AllocatedObjects++; }
        object allocated = row.Model.Allocate(row.Current);
        bool canonicalEmpty = row.Current.Kind == ObjectStateKind.String && row.Current.StringContent.Length == 0;
        if (allocated is null || allocated.GetType() != row.Model.DomainType ||
            (!canonicalEmpty && !allocations.TryAdd(allocated, row.Current.Id))) {
            throw new InvalidDataException("Each nonempty object ID must allocate a distinct instance of its exact current type.");
        }
        return allocated;
    }

    private static void Hydrate(NormalizedObject row, object instance, ObjectReadTable table,
        GraphReadStatistics? statistics) {
        if (statistics is not null) { statistics.HydratedObjects++; }
        row.Model.Hydrate(instance, row.Current, table);
    }

    private sealed record ReadSelection(DecodedRevision Decoded, NormalizedRevision Normalized,
        StateModelBinding RootModel, IReadOnlyList<ObjectId> Reachable);

    private sealed class ReferenceVisitor(Action<ObjectId> visit) : IStateReferenceVisitor {
        public void VisitString(ObjectId objectId) => visit(objectId);
        public void VisitDurable(ObjectId objectId, string nominalSchemaId) => visit(objectId);
        public void VisitDurable(ObjectId objectId, TypeExpr nominalType) => visit(objectId);
        public void VisitObject(ObjectId objectId, TypeExpr declaredType) => visit(objectId);
    }

    // References have already been validated against the complete current directory.
    // This visitor computes reachability only; it owns no second field/type description.
    private sealed class ReachableVisitor : IStateReferenceVisitor {
        private readonly HashSet<ObjectId> _seen = [];
        internal List<ObjectId> Ids { get; } = [];
        internal void Add(ObjectId id) {
            if (!id.IsNull && _seen.Add(id)) {
                Ids.Add(id);
            }
        }
        public void VisitString(ObjectId objectId) => Add(objectId);
        public void VisitDurable(ObjectId objectId, string nominalSchemaId) => Add(objectId);
        public void VisitDurable(ObjectId objectId, TypeExpr nominalType) => Add(objectId);
        public void VisitObject(ObjectId objectId, TypeExpr declaredType) => Add(objectId);
    }
}

/// <summary>One independently restored graph and its complete source baseline for controlled editable import.</summary>
internal sealed class MaterializedGraph<T> where T : class, IDurableObject {
    private readonly ulong _nextId;
    private readonly IReadOnlyDictionary<object, ObjectId> _bindings;

    internal MaterializedGraph(T root, ObjectId rootId, StateModelBinding rootModel,
        NormalizedRevision baseline, ulong nextId, IReadOnlyDictionary<object, ObjectId> bindings) {
        Root = root;
        RootId = rootId;
        RootModel = rootModel;
        Baseline = baseline;
        _nextId = nextId;
        _bindings = bindings;
    }

    internal T Root { get; }
    internal ObjectId RootId { get; }
    internal StateModelBinding RootModel { get; }
    internal NormalizedRevision Baseline { get; }
    internal CaptureSession CreateCaptureSession() => new(_nextId, _bindings);
}
