using System.Collections.ObjectModel;
using System.Globalization;

namespace Atelia.DurableGraph.Tests;

// Legacy test-only mechanism witness. Its Save/Accept vocabulary and reachability-derived
// removals are not the current public Repository save contract.

internal readonly record struct ProbeId {
    internal ProbeId(long value) {
        if (value <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "A probe identity must be positive.");
        }

        Value = value;
    }

    internal long Value { get; }

    public override string ToString() {
        return Value.ToString(CultureInfo.InvariantCulture);
    }
}

internal sealed class ProbeNode {
    internal ProbeNode(ProbeId id, int value) {
        ProbeIdValidation.ThrowIfInvalid(id, nameof(id));

        Id = id;
        Value = value;
    }

    internal ProbeId Id { get; private set; }

    internal bool ConstructorWasRun { get; } = true;

    internal int Value { get; set; }

    internal ProbeNode? Next { get; set; }

    internal ProbeNode? Alias { get; set; }

    internal int TransientValue { get; set; }

    internal Action<ProbeNode>? CaptureHook { get; set; }

    internal void HydrateForMaterialization(
        ProbeId id,
        in ProbeSnapshot snapshot,
        ProbeNode? next,
        ProbeNode? alias) {
        if (ConstructorWasRun || Id.Value > 0) {
            throw new InvalidOperationException(
                "Only an uninitialized, unbound probe node can be hydrated.");
        }

        ProbeIdValidation.ThrowIfInvalid(id, nameof(id));

        Id = id;
        Value = snapshot.Value;
        Next = next;
        Alias = alias;
    }
}

internal readonly record struct ProbeSnapshot(
    int Value,
    ProbeId? NextId,
    ProbeId? AliasId);

internal readonly record struct BaselineEntry(
    ProbeSnapshot Snapshot,
    bool RequiresRewrite);

internal sealed class NormalizedBaselineGraph {
    internal NormalizedBaselineGraph(
        ProbeId rootId,
        IReadOnlyDictionary<ProbeId, BaselineEntry> entries) {
        ArgumentNullException.ThrowIfNull(entries);
        if (rootId.Value <= 0) {
            throw new InvalidProbeGraphException(
                "The normalized baseline root identity must be positive.");
        }

        Dictionary<ProbeId, BaselineEntry> copy = new();
        foreach ((ProbeId id, BaselineEntry entry) in entries) {
            if (id.Value <= 0) {
                throw new InvalidProbeGraphException(
                    "Every normalized baseline entry identity must be positive.");
            }

            if (!copy.TryAdd(id, entry)) {
                throw new InvalidProbeGraphException(
                    $"The normalized baseline contains duplicate identity {id}.");
            }
        }

        if (!copy.ContainsKey(rootId)) {
            throw new InvalidProbeGraphException(
                $"The normalized baseline does not contain root identity {rootId}.");
        }

        foreach ((ProbeId ownerId, BaselineEntry entry) in copy) {
            ValidateReference(copy, ownerId, nameof(ProbeSnapshot.NextId), entry.Snapshot.NextId);
            ValidateReference(copy, ownerId, nameof(ProbeSnapshot.AliasId), entry.Snapshot.AliasId);
        }

        RootId = rootId;
        Entries = new ReadOnlyDictionary<ProbeId, BaselineEntry>(copy);
    }

    internal ProbeId RootId { get; }

    internal IReadOnlyDictionary<ProbeId, BaselineEntry> Entries { get; }

    private static void ValidateReference(
        IReadOnlyDictionary<ProbeId, BaselineEntry> entries,
        ProbeId ownerId,
        string fieldName,
        ProbeId? referenceId) {
        if (referenceId is not ProbeId targetId) {
            return;
        }

        if (targetId.Value <= 0 || !entries.ContainsKey(targetId)) {
            throw new InvalidProbeGraphException(
                $"Entry {ownerId} has dangling {fieldName} reference {targetId}.");
        }
    }
}

internal sealed class GraphDelta {
    internal GraphDelta(
        ProbeId resultRootId,
        IReadOnlyDictionary<ProbeId, ProbeSnapshot> upserts,
        IEnumerable<ProbeId> unreachableIds) {
        ArgumentNullException.ThrowIfNull(upserts);
        ArgumentNullException.ThrowIfNull(unreachableIds);
        if (resultRootId.Value <= 0) {
            throw new InvalidGraphDeltaException(
                "The graph delta result root identity must be positive.");
        }

        Dictionary<ProbeId, ProbeSnapshot> upsertCopy = new();
        foreach ((ProbeId id, ProbeSnapshot snapshot) in upserts) {
            if (id.Value <= 0) {
                throw new InvalidGraphDeltaException(
                    "Every graph delta Upsert identity must be positive.");
            }

            ValidateSnapshotReference(snapshot.NextId, nameof(ProbeSnapshot.NextId));
            ValidateSnapshotReference(snapshot.AliasId, nameof(ProbeSnapshot.AliasId));
            if (!upsertCopy.TryAdd(id, snapshot)) {
                throw new InvalidGraphDeltaException(
                    $"The graph delta contains duplicate Upsert identity {id}.");
            }
        }

        HashSet<ProbeId> unreachableCopy = [];
        foreach (ProbeId id in unreachableIds) {
            if (id.Value <= 0) {
                throw new InvalidGraphDeltaException(
                    "Every unreachable identity must be positive.");
            }

            unreachableCopy.Add(id);
        }

        ResultRootId = resultRootId;
        Upserts = new ReadOnlyDictionary<ProbeId, ProbeSnapshot>(upsertCopy);
        UnreachableIds = new ReadOnlySet<ProbeId>(unreachableCopy);
    }

    internal ProbeId ResultRootId { get; }

    internal IReadOnlyDictionary<ProbeId, ProbeSnapshot> Upserts { get; }

    internal IReadOnlySet<ProbeId> UnreachableIds { get; }

    private static void ValidateSnapshotReference(ProbeId? referenceId, string fieldName) {
        if (referenceId is ProbeId id && id.Value <= 0) {
            throw new InvalidGraphDeltaException(
                $"An Upsert has invalid {fieldName} reference {id}.");
        }
    }
}

internal static class GraphDeltaProbe {
    internal static GraphDelta PlanSave(
        NormalizedBaselineGraph baseline,
        ProbeNode currentRoot) {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(currentRoot);

        Stack<ProbeNode> pending = new();
        Dictionary<ProbeId, ProbeNode> instancesById = new();
        Dictionary<ProbeId, ProbeSnapshot> upserts = [];
        pending.Push(currentRoot);

        while (pending.Count > 0) {
            ProbeNode node = pending.Pop();
            if (!RegisterFirstVisit(instancesById, node)) {
                continue;
            }

            ProbeSnapshot snapshot = CaptureSnapshot(node, out ProbeNode? next, out ProbeNode? alias);

            if (!baseline.Entries.TryGetValue(node.Id, out BaselineEntry baselineEntry)
                || baselineEntry.RequiresRewrite
                || baselineEntry.Snapshot != snapshot) {
                upserts.Add(node.Id, snapshot);
            }

            PushReferences(pending, next, alias);
        }

        IEnumerable<ProbeId> unreachableIds = baseline.Entries.Keys
            .Where(id => !instancesById.ContainsKey(id));
        return new GraphDelta(currentRoot.Id, upserts, unreachableIds);
    }

    internal static NormalizedBaselineGraph CaptureCleanForAssertion(ProbeNode currentRoot) {
        ArgumentNullException.ThrowIfNull(currentRoot);

        Stack<ProbeNode> pending = new();
        Dictionary<ProbeId, ProbeNode> instancesById = new();
        Dictionary<ProbeId, BaselineEntry> entries = [];
        pending.Push(currentRoot);

        while (pending.Count > 0) {
            ProbeNode node = pending.Pop();
            if (!RegisterFirstVisit(instancesById, node)) {
                continue;
            }

            ProbeSnapshot snapshot = CaptureSnapshot(node, out ProbeNode? next, out ProbeNode? alias);
            entries.Add(node.Id, new BaselineEntry(snapshot, RequiresRewrite: false));
            PushReferences(pending, next, alias);
        }

        return new NormalizedBaselineGraph(currentRoot.Id, entries);
    }

    internal static NormalizedBaselineGraph AcceptForAssertion(
        NormalizedBaselineGraph baseline,
        GraphDelta delta) {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(delta);

        foreach (ProbeId unreachableId in delta.UnreachableIds) {
            if (!baseline.Entries.ContainsKey(unreachableId)) {
                throw new InvalidGraphDeltaException(
                    $"Unreachable identity {unreachableId} is absent from the accepted baseline.");
            }

            if (delta.Upserts.ContainsKey(unreachableId)) {
                throw new InvalidGraphDeltaException(
                    $"Identity {unreachableId} cannot be both Upserted and unreachable.");
            }
        }

        foreach ((ProbeId id, BaselineEntry entry) in baseline.Entries) {
            if (entry.RequiresRewrite
                && !delta.Upserts.ContainsKey(id)
                && !delta.UnreachableIds.Contains(id)) {
                throw new InvalidGraphDeltaException(
                    $"Rewrite-required identity {id} was neither Upserted nor made unreachable.");
            }
        }

        Dictionary<ProbeId, BaselineEntry> nextEntries = new();
        foreach ((ProbeId id, BaselineEntry entry) in baseline.Entries) {
            if (delta.UnreachableIds.Contains(id) || delta.Upserts.ContainsKey(id)) {
                continue;
            }

            if (entry.RequiresRewrite) {
                throw new InvalidGraphDeltaException(
                    $"Rewrite-required identity {id} cannot be preserved unchanged.");
            }

            nextEntries.Add(id, entry);
        }

        foreach ((ProbeId id, ProbeSnapshot snapshot) in delta.Upserts) {
            nextEntries[id] = new BaselineEntry(snapshot, RequiresRewrite: false);
        }

        try {
            NormalizedBaselineGraph accepted = new(delta.ResultRootId, nextEntries);
            ValidateExactReachableClosure(accepted);
            return accepted;
        } catch (InvalidProbeGraphException exception) {
            throw new InvalidGraphDeltaException(
                "The graph delta does not produce a valid normalized baseline.",
                exception);
        }
    }

    private static ProbeSnapshot CaptureSnapshot(
        ProbeNode node,
        out ProbeNode? next,
        out ProbeNode? alias) {
        node.CaptureHook?.Invoke(node);

        int value = node.Value;
        next = node.Next;
        alias = node.Alias;
        ProbeId? nextId = next?.Id;
        ProbeId? aliasId = alias?.Id;
        return new ProbeSnapshot(value, nextId, aliasId);
    }

    private static bool RegisterFirstVisit(
        IDictionary<ProbeId, ProbeNode> instancesById,
        ProbeNode node) {
        if (node.Id.Value <= 0) {
            throw new InvalidProbeGraphException(
                "Every current graph node identity must be positive.");
        }

        if (instancesById.ContainsKey(node.Id)) {
            ProbeNode registeredNode = instancesById[node.Id];
            if (!ReferenceEquals(registeredNode, node)) {
                throw new ProbeIdentityConflictException(node.Id, registeredNode, node);
            }

            return false;
        }

        instancesById.Add(node.Id, node);
        return true;
    }

    private static void ValidateExactReachableClosure(NormalizedBaselineGraph graph) {
        Stack<ProbeId> pending = new();
        HashSet<ProbeId> reachableIds = [];
        pending.Push(graph.RootId);

        while (pending.Count > 0) {
            ProbeId id = pending.Pop();
            if (!reachableIds.Add(id)) {
                continue;
            }

            ProbeSnapshot snapshot = graph.Entries[id].Snapshot;
            if (snapshot.AliasId is ProbeId aliasId) {
                pending.Push(aliasId);
            }

            if (snapshot.NextId is ProbeId nextId) {
                pending.Push(nextId);
            }
        }

        if (reachableIds.Count != graph.Entries.Count) {
            throw new InvalidGraphDeltaException(
                "The accepted graph contains entries outside the result root's reachable closure.");
        }
    }

    private static void PushReferences(
        Stack<ProbeNode> pending,
        ProbeNode? next,
        ProbeNode? alias) {
        if (alias is not null) {
            pending.Push(alias);
        }

        if (next is not null) {
            pending.Push(next);
        }
    }
}

internal sealed class ProbeIdentityConflictException : InvalidOperationException {
    internal ProbeIdentityConflictException(
        ProbeId id,
        ProbeNode registeredNode,
        ProbeNode conflictingNode)
        : base($"Probe identity {id} is used by two different CLR instances.") {
        Id = id;
        RegisteredNode = registeredNode;
        ConflictingNode = conflictingNode;
    }

    internal ProbeId Id { get; }

    internal ProbeNode RegisteredNode { get; }

    internal ProbeNode ConflictingNode { get; }
}

internal sealed class InvalidProbeGraphException : ArgumentException {
    internal InvalidProbeGraphException(string message)
        : base(message) {
    }
}

internal sealed class InvalidGraphDeltaException : InvalidOperationException {
    internal InvalidGraphDeltaException(string message)
        : base(message) {
    }

    internal InvalidGraphDeltaException(string message, Exception innerException)
        : base(message, innerException) {
    }
}

internal static class ProbeIdValidation {
    internal static void ThrowIfInvalid(ProbeId id, string parameterName) {
        if (id.Value <= 0) {
            throw new ArgumentOutOfRangeException(
                parameterName,
                id.Value,
                "A probe identity must be positive.");
        }
    }
}
