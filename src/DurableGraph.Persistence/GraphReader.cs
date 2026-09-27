using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using System.Collections.ObjectModel;
using System.Diagnostics;

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

    /// <summary>Restores an independent graph and imports its complete source for editing.</summary>
    internal static MaterializedGraph<T> Read<T>(RevisionReadSession session,
        FrameAddress revisionAddress, ObjectId rootId, bool requireExactRootType = false) where T : class, IDurableObject {
        ArgumentNullException.ThrowIfNull(session);
        PreparedGraphSelection selection = Prepare<T>(session, revisionAddress, rootId, requireExactRootType);
        var (instances, _) = Materialize(session, selection);
        return DeliverEditable<T>(selection, instances);
    }

    // Repository's certified State path uses the same preparation/materialization core.
    internal static PreparedGraphSelection PrepareState(RevisionReadSession session,
        FrameAddress revisionAddress, ObjectId rootId) => Prepare<IDurableObject>(session, revisionAddress, rootId);

    // reusedLeaves is trusted only after validation of the installed entry owning this exact
    // selection. It never accepts caller-owned live graphs or a different selection's table.
    internal static MaterializedGraph<IDurableObject> RestorePreparedState(RevisionReadSession session,
        PreparedGraphSelection selection, out IReadOnlyDictionary<ObjectId, object>? immutableLeaves,
        bool collectImmutableLeaves = false, IReadOnlyDictionary<ObjectId, object>? reusedLeaves = null) {
        immutableLeaves = null;
        var (instances, _) = Materialize(session, selection, reusedLeaves: reusedLeaves);
        if (collectImmutableLeaves) {
            Dictionary<ObjectId, object>? leaves = null;
            foreach (ObjectId id in selection.Reachable) {
                if (selection.Normalized.Objects[id].Model is StateModelBinding { IsImmutableLeaf: true }) {
                    (leaves ??= []).Add(id, instances[id]);
                }
            }
            if (leaves is not null) { immutableLeaves = new ReadOnlyDictionary<ObjectId, object>(leaves); }
        }
        return DeliverEditable<IDurableObject>(selection, instances);
    }

    /// <summary>Restores just one root without constructing an editable identity import.</summary>
    internal static T ReadRoot<T>(RevisionReadSession session, FrameAddress revisionAddress,
        ObjectId rootId, bool requireExactRootType = false) where T : class, IDurableObject {
        ArgumentNullException.ThrowIfNull(session);
        PreparedGraphSelection selection = Prepare<T>(session, revisionAddress, rootId, requireExactRootType);
        var (instances, _) = Materialize(session, selection);
        return (T)instances[rootId];
    }

    /// <summary>Restores two independent graphs with one operation's mutable allocation guard.</summary>
    internal static (IDurableObject First, IDurableObject Second) ReadIndependent(RevisionReadSession session,
        FrameAddress firstRevisionAddress, ObjectId firstRootId,
        FrameAddress secondRevisionAddress, ObjectId secondRootId) {
        ArgumentNullException.ThrowIfNull(session);
        PreparedGraphSelection first = Prepare<IDurableObject>(session, firstRevisionAddress, firstRootId);
        PreparedGraphSelection second = Prepare<IDurableObject>(session, secondRevisionAddress, secondRootId);
        var (firstInstances, secondInstances) = Materialize(session, first, second);
        return ((IDurableObject)firstInstances[firstRootId], (IDurableObject)secondInstances![secondRootId]);
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
        PreparedGraphSelection first = Prepare<TFirst>(session, firstRevisionAddress, firstRootId);
        PreparedGraphSelection second = Prepare<TSecond>(session, secondRevisionAddress, secondRootId);
        HashSet<ObjectId> shared = FindSharedClosure(first, second);
        var (firstInstances, secondInstances) = Materialize(session, first, second, shared);
        return ((TFirst)firstInstances[firstRootId], (TSecond)secondInstances![secondRootId]);
    }

    // A null shared set means independent graphs. Only ReadPair supplies a proven set;
    // even an empty proven set retains Pair's existing cross-graph string identity guard.
    private static (Dictionary<ObjectId, object> First, Dictionary<ObjectId, object>? Second) Materialize(
        RevisionReadSession session, PreparedGraphSelection first, PreparedGraphSelection? second = null,
        HashSet<ObjectId>? shared = null, IReadOnlyDictionary<ObjectId, object>? reusedLeaves = null) {
        long started = session.MeasureMaterialization ? Stopwatch.GetTimestamp() : 0;
        try {
            return MaterializeCore(session, first, second, shared, reusedLeaves);
        } finally {
            if (session.MeasureMaterialization) {
                session.Statistics.MaterializationElapsedTicks += Stopwatch.GetTimestamp() - started;
            }
        }
    }

    private static (Dictionary<ObjectId, object> First, Dictionary<ObjectId, object>? Second) MaterializeCore(
        RevisionReadSession session, PreparedGraphSelection first, PreparedGraphSelection? second,
        HashSet<ObjectId>? shared, IReadOnlyDictionary<ObjectId, object>? reusedLeaves) {
        Dictionary<object, ObjectId> allocations = new(ReferenceEqualityComparer.Instance);
        Dictionary<ObjectId, object> firstInstances = AllocateGraph(session, first, allocations, reusedLeaves: reusedLeaves);
        Dictionary<ObjectId, object>? secondInstances = second is null ? null : AllocateGraph(session, second,
            shared is null ? new(ReferenceEqualityComparer.Instance) : allocations, firstInstances, shared);
        ObjectReadTable firstTable = new(firstInstances);
        ObjectReadTable? secondTable = secondInstances is null ? null : new(secondInstances);
        // Every instance table is complete before any hydration callback runs. A shared
        // row's reference closure is also shared, so its first graph's table is sufficient.
        HydrateGraph(session, first, firstInstances, firstTable, reusedLeaves: reusedLeaves);
        if (second is not null) { HydrateGraph(session, second, secondInstances!, secondTable!, shared); }
        return (firstInstances, secondInstances);
    }

    private static Dictionary<ObjectId, object> AllocateGraph(RevisionReadSession session,
        PreparedGraphSelection selection, Dictionary<object, ObjectId> allocations,
        Dictionary<ObjectId, object>? firstInstances = null, HashSet<ObjectId>? shared = null,
        IReadOnlyDictionary<ObjectId, object>? reusedLeaves = null) {
        Dictionary<ObjectId, object> instances = [];
        // Register the entire trusted leaf mapping before ANY allocator can return one
        // of its instances under another ID, even if that ID precedes the leaf in traversal.
        if (reusedLeaves is not null) {
            foreach ((ObjectId id, object instance) in reusedLeaves) {
                if (selection.Normalized.Objects[id].Model is not StateModelBinding { IsImmutableLeaf: true } model ||
                    instance.GetType() != model.DomainType || !allocations.TryAdd(instance, id)) {
                    throw new InvalidDataException("Prepared immutable leaves must preserve exact types and distinct object identities.");
                }
                session.RequireUniqueMutableInstance(instance);
                instances.Add(id, instance);
                session.Statistics.ReusedImmutableLeaves++;
            }
        }
        foreach (ObjectId id in selection.Reachable) {
            if (reusedLeaves is not null && reusedLeaves.ContainsKey(id)) { continue; }
            if (shared is not null && shared.Contains(id)) {
                instances.Add(id, firstInstances![id]);
                session.Statistics.SharedObjects++;
                continue;
            }
            NormalizedObject row = selection.Normalized.Objects[id];
            object allocated = Allocate(row, allocations, session.Statistics);
            if (row.Current.Kind != ObjectStateKind.String) { session.RequireUniqueMutableInstance(allocated); }
            instances.Add(id, allocated);
            // Canonical Empty can also share across heads without a closure proof.
            // As before, Pair reports this actual same-ID reuse; independent reads do not.
            if (shared is not null && firstInstances!.TryGetValue(id, out object? firstInstance) &&
                ReferenceEquals(firstInstance, allocated)) {
                session.Statistics.SharedObjects++;
            }
        }
        return instances;
    }

    private static void HydrateGraph(RevisionReadSession session, PreparedGraphSelection selection,
        Dictionary<ObjectId, object> instances, ObjectReadTable table, HashSet<ObjectId>? shared = null,
        IReadOnlyDictionary<ObjectId, object>? reusedLeaves = null) {
        foreach ((ObjectId id, object instance) in instances) {
            if ((shared is null || !shared.Contains(id)) && (reusedLeaves is null || !reusedLeaves.ContainsKey(id))) {
                Hydrate(selection.Normalized.Objects[id], instance, table, session.Statistics);
            }
        }
    }

    // Only editable delivery retains a reverse identity map and complete source ID cursor.
    // All strings are imported in source-ID order, including those cut off by Upgrade;
    // canonical Empty therefore receives the smallest full-source ID, not the first reachable ID.
    private static MaterializedGraph<T> DeliverEditable<T>(PreparedGraphSelection selection,
        Dictionary<ObjectId, object> instances) where T : class, IDurableObject {
        NormalizedRevision normalized = selection.Normalized;
        Dictionary<object, ObjectId> bindings = new(ReferenceEqualityComparer.Instance);
        foreach ((ObjectId id, object instance) in instances) {
            if (normalized.Objects[id].Current.Kind != ObjectStateKind.String) { bindings.Add(instance, id); }
        }
        foreach (NormalizedObject row in normalized.Objects.Values.OrderBy(static row => row.Current.Id)) {
            if (row.Current.Kind == ObjectStateKind.String) {
                bindings.TryAdd(row.Current.StringContent, row.Current.Id);
            }
        }
        ulong nextId = (ulong)normalized.Objects.Keys.Max().Value + 1;
        return new((T)instances[selection.RootId], selection.RootId, selection.RootModel, normalized, nextId, bindings);
    }

    private static PreparedGraphSelection Prepare<T>(RevisionReadSession session, FrameAddress revisionAddress,
        ObjectId rootId, bool requireExactRootType = false) where T : class, IDurableObject {
        ArgumentOutOfRangeException.ThrowIfZero(rootId.Value, nameof(rootId));
        session.Statistics.PreparedGraphs++;
        DecodedRevision decoded = session.Read(revisionAddress);
        // A fresh preparation normalizes each selected graph even when its stored DTOs
        // hit the operation's exact-version cache. Certified State hits bypass this method.
        NormalizedRevision normalized = NormalizedRevision.Create(decoded, session.Models, session.Statistics);
        if (!normalized.Objects.TryGetValue(rootId, out NormalizedObject? root) ||
            root.Model is not StateModelBinding rootModel || rootModel.DomainType.IsAbstract ||
            (requireExactRootType ? rootModel.DomainType != typeof(T) : !typeof(T).IsAssignableFrom(rootModel.DomainType))) {
            throw new InvalidDataException("Root ID must select a durable object of the requested current domain type.");
        }
        ReachableVisitor visitor = new();
        visitor.Add(rootId);
        for (int index = 0; index < visitor.Ids.Count; index++) {
            NormalizedObject row = normalized.Objects[visitor.Ids[index]];
            session.Statistics.ReachabilityVisits++;
            row.Model.VisitReferences(row.Current, visitor);
        }
        return new(normalized, rootId, rootModel, session.Models, visitor.Ids);
    }

    private static HashSet<ObjectId> FindSharedClosure(PreparedGraphSelection first, PreparedGraphSelection second) {
        HashSet<ObjectId> secondReachable = new(second.Reachable);
        HashSet<ObjectId> candidates = [];
        foreach (ObjectId id in first.Reachable) {
            // RevisionDecoder checks Storage.Head against this exact view's head map.
            // Missing provenance never supplies a sharing proof, even on both sides.
            if (secondReachable.Contains(id) &&
                first.Normalized.Objects[id].Storage is { } firstStorage &&
                second.Normalized.Objects[id].Storage is { } secondStorage && firstStorage.Head == secondStorage.Head &&
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
