namespace Atelia.DurableGraph.Persistence;

internal enum ObjectSaveChangeKind {
    Insert,
    Update,
    NoChange,
    BaseOnlyUpdate,
}

/// <summary>
/// ObjectVersion payload byte measurements from one frozen post-live set. The input
/// to Plan must cover all post-live objects; removed objects are excluded. These
/// measurements exclude ObjectId, head-directory data, shared Frame structure, and
/// physical I/O. DeltaPayloadBytes includes the fixed 12-byte prior address.
/// Null means inapplicable: Insert has no Delta or reconstruction measurement,
/// BaseOnlyUpdate also has neither, Update has both, and NoChange has only the
/// reconstruction measurement. Known zero measurements are valid; Plan validates
/// the complete input.
/// </summary>
internal readonly record struct ObjectSaveEstimate(
    ObjectId ObjectId,
    ObjectSaveChangeKind ChangeKind,
    long BasePayloadBytes,
    long? DeltaPayloadBytes,
    long? ReconstructionPayloadBytes);
