namespace Atelia.DurableGraph.Persistence;

/// <summary>
/// Selects object representations from payload measurements for one frozen, complete post-live set.
/// The optional Base budget is soft; required writes do not consume it.
/// </summary>
internal static class ReadAmplificationBaseBudgetPolicy {
    internal static void ValidateParameters(ReadAmplificationBaseBudgetParameters parameters) {
        if (parameters.ReadAmplificationThreshold < 1) {
            throw new ArgumentOutOfRangeException(
                nameof(parameters), parameters, "Read amplification threshold must be at least one.");
        }

        if (parameters.BaseBudgetPercent is < 1 or > 100) {
            throw new ArgumentOutOfRangeException(
                nameof(parameters), parameters, "Base budget percent must be between one and one hundred.");
        }

    }

    internal static ObjectRepresentationPlan Plan(
        IReadOnlyList<ObjectSaveEstimate> objects,
        ReadAmplificationBaseBudgetParameters parameters) {
        ArgumentNullException.ThrowIfNull(objects);
        ValidateParameters(parameters);

        HashSet<ObjectId> objectIds = new();
        List<ObjectWriteDecision> writes = new();
        List<Candidate> candidates = new();
        long graphBaseBytes = 0;

        foreach (ObjectSaveEstimate estimate in objects) {
            ValidateEstimate(estimate);
            if (!objectIds.Add(estimate.ObjectId)) {
                throw new ArgumentException("ObjectIds must be unique.", nameof(objects));
            }

            graphBaseBytes = checked(graphBaseBytes + estimate.BasePayloadBytes);
            long prospectiveReconstructionPayloadBytes = estimate.ChangeKind == ObjectSaveChangeKind.Update
                ? checked(estimate.ReconstructionPayloadBytes!.Value + estimate.DeltaPayloadBytes!.Value)
                : estimate.ReconstructionPayloadBytes.GetValueOrDefault();

            if (estimate.ChangeKind is ObjectSaveChangeKind.Insert or ObjectSaveChangeKind.BaseOnlyUpdate ||
                (estimate.ChangeKind == ObjectSaveChangeKind.Update &&
                    estimate.BasePayloadBytes <= estimate.DeltaPayloadBytes!.Value)) {
                writes.Add(new(estimate.ObjectId, ObjectRepresentationMode.Base));
                continue;
            }

            int writeIndex = -1;
            if (estimate.ChangeKind == ObjectSaveChangeKind.Update) {
                writeIndex = writes.Count;
                writes.Add(new(estimate.ObjectId, ObjectRepresentationMode.Delta));
            }

            // At a zero denominator, 0/0 has no motive and positive/0 always has one.
            if (prospectiveReconstructionPayloadBytes >
                (Int128)estimate.BasePayloadBytes * parameters.ReadAmplificationThreshold) {
                candidates.Add(new(
                    estimate.ObjectId, estimate.BasePayloadBytes, prospectiveReconstructionPayloadBytes, writeIndex));
            }
        }

        // All rows, including mandatory Base updates, have now passed overflow validation.
        candidates.Sort(CompareCandidates);
        long remainingBudget = (long)((Int128)graphBaseBytes * parameters.BaseBudgetPercent / 100);
        for (int i = 0; i < candidates.Count; i++) {
            Candidate candidate = candidates[i];
            bool exceedsBudget = candidate.BasePayloadBytes > remainingBudget;
            if (exceedsBudget && i != 0) {
                break;
            }

            ObjectWriteDecision decision = new(candidate.ObjectId, ObjectRepresentationMode.Base);
            if (candidate.WriteIndex >= 0) {
                writes[candidate.WriteIndex] = decision;
            }
            else {
                writes.Add(decision);
            }

            // Only the actual first candidate may overshoot, even when earlier costs were zero.
            if (exceedsBudget) {
                break;
            }

            remainingBudget -= candidate.BasePayloadBytes;
        }

        writes.Sort(static (left, right) => left.ObjectId.CompareTo(right.ObjectId));
        return new(writes.ToArray());
    }

    private static void ValidateEstimate(ObjectSaveEstimate estimate) {
        const string parameterName = "objects";
        if (estimate.ObjectId.IsNull) {
            throw new ArgumentException("ObjectIds must be nonzero.", parameterName);
        }

        if (estimate.BasePayloadBytes < 0 ||
            estimate.DeltaPayloadBytes < 0 ||
            estimate.ReconstructionPayloadBytes < 0) {
            throw new ArgumentOutOfRangeException(parameterName, "Payload byte measurements must be nonnegative.");
        }

        bool validShape;
        switch (estimate.ChangeKind) {
            case ObjectSaveChangeKind.Insert:
            case ObjectSaveChangeKind.BaseOnlyUpdate:
                validShape = estimate.DeltaPayloadBytes is null && estimate.ReconstructionPayloadBytes is null;
                break;
            case ObjectSaveChangeKind.Update:
                validShape = estimate.DeltaPayloadBytes is not null && estimate.ReconstructionPayloadBytes is not null;
                break;
            case ObjectSaveChangeKind.NoChange:
                validShape = estimate.DeltaPayloadBytes is null && estimate.ReconstructionPayloadBytes is not null;
                break;
            default:
                throw new ArgumentOutOfRangeException(parameterName, estimate.ChangeKind, "Unknown object change kind.");
        }

        if (!validShape) {
            throw new ArgumentException("Delta and reconstruction payload measurements must match the object change kind.", parameterName);
        }
    }

    private static int CompareCandidates(Candidate left, Candidate right) {
        // A zero-Base candidate necessarily has positive reconstruction cost: it ranks as infinity.
        int byAmplification;
        if (left.BasePayloadBytes == 0 || right.BasePayloadBytes == 0) {
            byAmplification = left.BasePayloadBytes == 0
                ? (right.BasePayloadBytes == 0 ? 0 : -1)
                : 1;
        }
        else {
            Int128 leftProduct = (Int128)left.ProspectiveReconstructionPayloadBytes * right.BasePayloadBytes;
            Int128 rightProduct = (Int128)right.ProspectiveReconstructionPayloadBytes * left.BasePayloadBytes;
            byAmplification = rightProduct.CompareTo(leftProduct);
        }

        return byAmplification != 0 ? byAmplification : left.ObjectId.CompareTo(right.ObjectId);
    }

    private readonly record struct Candidate(
        ObjectId ObjectId,
        long BasePayloadBytes,
        long ProspectiveReconstructionPayloadBytes,
        int WriteIndex);
}
