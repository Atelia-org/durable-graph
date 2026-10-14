using Atelia.FrameStore;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph.Persistence;

/// <summary>Classifies captured objects and plans their writes from authenticated storage accounting.</summary>
/// <remarks>Does not own a Store, Schema view, live directory or publication.</remarks>
internal static class ObjectWritePlanner {
    // The caller establishes the captured baseline and exact prior before classification,
    // and provides its actual owned typed Base header and body.
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
}
