using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence;

internal sealed record HistoryGraphRecord(FrameAddress Address, FrameAddress? Parent, CheckpointKind Kind, ObjectId RootId) {
    internal FrameAddress RevisionAddress => Address;
}
