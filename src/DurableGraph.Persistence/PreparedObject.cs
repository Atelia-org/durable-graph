using Atelia.FrameStore;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph.Persistence;

/// <summary>
/// Owned current content and the producer's classification against an exact prior.
/// The producer owns the DTO/Schema correspondence; this row cannot authenticate it.
/// </summary>
internal sealed class PreparedObject {
    private PreparedObject(ObjectId objectId, FrameAddress? priorAddress, EncodedBaseObjectBody encodedBaseBody,
        PreparedDeltaBody? deltaBody, ObjectSaveChangeKind changeKind) {
        ArgumentOutOfRangeException.ThrowIfZero(objectId.Value, nameof(objectId));
        ArgumentNullException.ThrowIfNull(encodedBaseBody);
        if (priorAddress is { } prior && prior == default) {
            throw new ArgumentOutOfRangeException(nameof(priorAddress), "The prior must be a valid Frame address.");
        }
        ObjectId = objectId;
        PriorAddress = priorAddress;
        EncodedBaseBody = encodedBaseBody;
        DeltaBody = deltaBody;
        ChangeKind = changeKind;
    }

    internal ObjectId ObjectId { get; }
    internal FrameAddress? PriorAddress { get; }
    internal EncodedBaseObjectBody EncodedBaseBody { get; }
    internal PreparedDeltaBody? DeltaBody { get; }
    internal ObjectSaveChangeKind ChangeKind { get; }

    internal static PreparedObject New(ObjectId id, EncodedBaseObjectBody encodedBaseBody) =>
        new(id, null, encodedBaseBody, null, ObjectSaveChangeKind.Insert);

    internal static PreparedObject Unchanged(ObjectId id, FrameAddress prior, EncodedBaseObjectBody encodedBaseBody) =>
        new(id, prior, encodedBaseBody, null, ObjectSaveChangeKind.NoChange);

    internal static PreparedObject Compared(ObjectId id, FrameAddress prior, EncodedBaseObjectBody encodedBaseBody, PreparedDeltaBody deltaBody) {
        ArgumentNullException.ThrowIfNull(deltaBody);
        return new(id, prior, encodedBaseBody, deltaBody,
            deltaBody.HasChanges ? ObjectSaveChangeKind.Update : ObjectSaveChangeKind.NoChange);
    }

    internal static PreparedObject BaseOnlyUpdate(ObjectId id, FrameAddress prior, EncodedBaseObjectBody encodedBaseBody) =>
        new(id, prior, encodedBaseBody, null, ObjectSaveChangeKind.BaseOnlyUpdate);
}
