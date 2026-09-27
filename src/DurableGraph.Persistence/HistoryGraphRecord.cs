using Atelia.EventJournal;
using StateFrameAddress = Atelia.DurableGraph.Storage.FrameAddress;

namespace Atelia.DurableGraph.Persistence;

/// <summary>The role of a graph in an EventHistory logical chain.</summary>
public enum GraphFrameKind : uint {
    Event = 1,
    State = 2,
}

internal sealed record HistoryGraphRecord(
    EventAddress Address,
    EventAddress? Parent,
    GraphFrameKind Kind,
    StateFrameAddress RevisionAddress,
    ObjectId RootId);
