using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence;

/// <summary>The role of a graph in a logical business history.</summary>
public enum GraphFrameKind : uint {
    Event = 1,
    State = 2,
}

internal sealed record HistoryGraphRecord(FrameAddress Address, FrameAddress? Parent, GraphFrameKind Kind, ObjectId RootId) {
    internal FrameAddress RevisionAddress => Address;
}
