using Atelia.FrameStore;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph.Persistence;

/// <summary>
/// Turns the caller's complete frozen post-live contents into one appendable
/// Revision. Does not infer reachability, authenticate typed bodies, or publish.
/// </summary>
internal static class ObjectRevisionPlanner {
    internal static PreparedObjectRevision PrepareRevision(
        StateRevisionStore store,
        FrameAddress? parentRevisionAddress,
        IEnumerable<PreparedObject> objects,
        ReadAmplificationBaseBudgetParameters parameters,
        bool independentSnapshot = false) =>
        PrepareCore(store, parentRevisionAddress, objects, parameters, independentSnapshot, null);

    // Only a controlled baseline may supply historical accounting. Standalone prepared
    // object callers continue to establish it by reading the actual reconstruction chain.
    internal static PreparedObjectRevision PrepareLoadedRevision(
        StateRevisionStore store, NormalizedRevision source, IEnumerable<PreparedObject> objects,
        ReadAmplificationBaseBudgetParameters parameters, bool independentSnapshot = false) {
        if (!ReferenceEquals(source.SourceStore, store)) {
            throw new InvalidDataException("The normalized baseline belongs to another State Store lifetime.");
        }
        return PrepareCore(store, source.RevisionAddress, objects, parameters, independentSnapshot, source);
    }

    // Adapters establish the captured baseline and exact prior before calling this
    // classifier. The owned typed Base can use either adapter's actual type header.
    internal static PreparedObject ClassifyCaptured(PreparedCapturedObject row,
        EncodedBaseObjectBody typedBase, FrameAddress? exactPrior, bool requiresRewrite = false) {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(typedBase);
        if ((row.Previous is null) != (exactPrior is null)) {
            throw new ArgumentException("Captured existence and the exact prior must agree.", nameof(exactPrior));
        }
        if (row.Previous is null) {
            if (requiresRewrite) { throw new ArgumentException("A new captured object cannot require a source rewrite.", nameof(requiresRewrite)); }
            return PreparedObject.New(row.Current.Id, typedBase);
        }
        if (requiresRewrite) { return PreparedObject.BaseOnlyUpdate(row.Current.Id, exactPrior!.Value, typedBase); }
        return row.DeltaBody is null
            ? PreparedObject.Unchanged(row.Current.Id, exactPrior!.Value, typedBase)
            : PreparedObject.Compared(row.Current.Id, exactPrior!.Value, typedBase, row.DeltaBody);
    }

    // Pure object planning: callers authenticate heads/H and provide the complete
    // post-live set. This function owns no Store, Schema view, directory or publication.
    internal static (ObjectVersionRecord[] Records, ObjectSaveEstimate[] Estimates, ObjectRepresentationPlan RepresentationPlan)
        PlanObjectWrites(IEnumerable<PreparedObject> objects, IReadOnlyDictionary<ObjectId, long> priorReconstructionBytes,
        ReadAmplificationBaseBudgetParameters parameters) {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(priorReconstructionBytes);
        PreparedObject[] rows = objects.ToArray();
        Dictionary<ObjectId, PreparedObject> byId = [];
        foreach (PreparedObject row in rows) {
            if (row is null) { throw new ArgumentException("Prepared objects cannot contain null rows.", nameof(objects)); }
            if (!byId.TryAdd(row.ObjectId, row)) { throw new ArgumentException("ObjectIds must be unique.", nameof(objects)); }
        }
        Array.Sort(rows, static (left, right) => left.ObjectId.CompareTo(right.ObjectId));
        ObjectSaveEstimate[] estimates = new ObjectSaveEstimate[rows.Length];
        for (int index = 0; index < rows.Length; index++) {
            PreparedObject row = rows[index];
            long? deltaBytes = row.ChangeKind == ObjectSaveChangeKind.Update
                ? ObjectVersionPayloadSize.GetDeltaPayloadBytes(row.DeltaBody!.Body.Length, row.PriorAddress!.Value)
                : null;
            long? reconstructionBytes = null;
            if (row.ChangeKind is ObjectSaveChangeKind.Update or ObjectSaveChangeKind.NoChange) {
                if (!priorReconstructionBytes.TryGetValue(row.ObjectId, out long priorH)) {
                    throw new ArgumentException($"Object {row.ObjectId} requires prior reconstruction bytes.", nameof(priorReconstructionBytes));
                }
                reconstructionBytes = priorH;
            }
            estimates[index] = new(row.ObjectId, row.ChangeKind,
                ObjectVersionPayloadSize.GetBasePayloadBytes(row.EncodedBaseBody.Body.Length), deltaBytes, reconstructionBytes);
        }
        ObjectRepresentationPlan plan = ReadAmplificationBaseBudgetPolicy.Plan(estimates, parameters);
        ObjectVersionRecord[] records = plan.Writes.Select(decision => {
            PreparedObject row = byId[decision.ObjectId];
            return decision.Mode == ObjectRepresentationMode.Base
                ? ObjectVersionRecord.CreateBase(row.ObjectId.Value, row.EncodedBaseBody.Body)
                : ObjectVersionRecord.CreateDelta(row.ObjectId.Value, row.PriorAddress!.Value, row.DeltaBody!.Body);
        }).ToArray();
        return (records, estimates, plan);
    }

    private static PreparedObjectRevision PrepareCore(
        StateRevisionStore store, FrameAddress? parentRevisionAddress, IEnumerable<PreparedObject> objects,
        ReadAmplificationBaseBudgetParameters parameters, bool independentSnapshot, NormalizedRevision? source) {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(objects);
        if (independentSnapshot && parentRevisionAddress is null) {
            throw new ArgumentException("An independent snapshot requires a State baseline.", nameof(parentRevisionAddress));
        }
        PreparedObject[] rows = objects.ToArray();
        Dictionary<ObjectId, PreparedObject> byId = [];
        foreach (PreparedObject row in rows) {
            if (row is null) {
                throw new ArgumentException("Prepared objects cannot contain null rows.", nameof(objects));
            }
            if (!byId.TryAdd(row.ObjectId, row)) {
                throw new ArgumentException("ObjectIds must be unique.", nameof(objects));
            }
        }
        Array.Sort(rows, static (left, right) => left.ObjectId.CompareTo(right.ObjectId));

        IReadOnlyDictionary<ObjectId, FrameAddress> parentHeads = parentRevisionAddress is { } parent
            ? store.ReadLiveObjectHeadMap(parent).ToDictionary(static pair => new ObjectId(pair.Key), static pair => pair.Value)
            : new Dictionary<ObjectId, FrameAddress>();
        foreach (PreparedObject row in rows) {
            bool exists = parentHeads.TryGetValue(row.ObjectId, out FrameAddress head);
            if (row.ChangeKind == ObjectSaveChangeKind.Insert) {
                if (exists) {
                    throw new ArgumentException($"New object {row.ObjectId} already exists in Parent.", nameof(objects));
                }
            }
            else if (!exists || row.PriorAddress != head) {
                throw new ArgumentException(
                    $"Object {row.ObjectId} must claim the object head selected by the exact Parent Revision.",
                    nameof(objects));
            }
        }

        Dictionary<ObjectId, long> priorH = [];
        foreach (PreparedObject row in rows) {
            if (row.ChangeKind is ObjectSaveChangeKind.Update or ObjectSaveChangeKind.NoChange) {
                priorH.Add(row.ObjectId, source is null
                    ? store.ReadObjectVersionChain(parentRevisionAddress!.Value, row.ObjectId.Value).ReconstructionPayloadBytes
                    : GetReconstructionPayloadBytes(source, row));
            }
        }
        var (records, estimates, plan) = PlanObjectWrites(rows, priorH, parameters);

        uint[] removedIds = parentRevisionAddress is not null && !independentSnapshot
            ? parentHeads.Keys.Where(id => !byId.ContainsKey(id)).Select(static id => id.Value).ToArray()
            : [];
        HashSet<uint>? localIds = independentSnapshot || removedIds.Length != 0
            ? records.Select(static record => record.ObjectId).ToHashSet() : null;
        StateRevision revision;
        if (independentSnapshot || (removedIds.Length != 0 &&
            IsObjectHeadMapBaseSmaller(rows, localIds!, removedIds))) {
            // Membership is the candidate closure, while each actual policy-selected write
            // remains local (including an optional Base for an unchanged object).
            revision = StateRevision.CreateObjectHeadMapBase(parentRevisionAddress, records,
                rows.Where(row => !localIds!.Contains(row.ObjectId.Value))
                    .Select(row => new KeyValuePair<uint, FrameAddress>(row.ObjectId.Value, parentHeads[row.ObjectId])));
        } else {
            revision = parentRevisionAddress is { } exactParent
                ? StateRevision.CreateObjectHeadMapDelta(exactParent, records, removedIds)
                : StateRevision.CreateObjectHeadMapBase(null, records, []);
        }
        return new(revision, estimates, plan);
    }

    private static bool IsObjectHeadMapBaseSmaller(PreparedObject[] rows, HashSet<uint> localIds,
        uint[] removedIds) {
        // The Parent and local records are identical in both representations. Compare
        // only the directory suffix, including its count and each ObjectId.
        long deltaBytes = VarUIntBytes((ulong)removedIds.Length);
        foreach (uint id in removedIds) { deltaBytes += VarUIntBytes(id); }
        long baseBytes = VarUIntBytes((ulong)(rows.Length - localIds.Count));
        foreach (PreparedObject row in rows) {
            if (localIds.Contains(row.ObjectId.Value)) { continue; }
            baseBytes += VarUIntBytes(row.ObjectId.Value) + FrameAddress.EncodedSize;
            if (baseBytes >= deltaBytes) { return false; }
        }
        return baseBytes < deltaBytes; // Ties retain Delta, including no external heads.
    }

    private static int VarUIntBytes(ulong value) {
        int bytes = 1;
        while (value >= 0x80) { value >>= 7; bytes++; }
        return bytes;
    }

    private static long GetReconstructionPayloadBytes(NormalizedRevision source, PreparedObject row) {
        if (!source.Objects.TryGetValue(row.ObjectId, out NormalizedObject? prior) ||
            prior.Storage is not { } storage || storage.Head != row.PriorAddress) {
            throw new InvalidDataException("Prepared object storage must match the controlled baseline head.");
        }
        return storage.ReconstructionPayloadBytes;
    }
}
