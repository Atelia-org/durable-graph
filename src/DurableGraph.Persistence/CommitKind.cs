namespace Atelia.DurableGraph;

/// <summary>The Event/State role of a saved commit in business history.</summary>
public enum CommitKind : uint {
    /// <summary>An optional domain Event; recording it does not execute or replay a handler.</summary>
    Event = 1,
    /// <summary>A saved domain State; it does not require a preceding Event.</summary>
    State = 2,
}
