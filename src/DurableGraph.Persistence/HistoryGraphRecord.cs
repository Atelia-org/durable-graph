using Atelia.FrameStore;
using Atelia.HeadStore;

namespace Atelia.DurableGraph.Persistence;

internal sealed record HistoryGraphRecord(HeadEntryId Id, FrameAddress Address, CommitKind Kind,
    ObjectId RootId, ReadOnlyMemory<byte> Summary) {
    internal FrameAddress RevisionAddress => Address;
}
