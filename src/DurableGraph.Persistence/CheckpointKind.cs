namespace Atelia.DurableGraph;

/// <summary>The Event/State role of a checkpoint in logical business history.</summary>
public enum CheckpointKind : uint {
    Event = 1,
    State = 2,
}
