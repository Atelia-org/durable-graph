namespace Atelia.DurableGraph;

/// <summary>The direction of a fixed logical history query.</summary>
/// <remarks>
/// Repository.EnumerateEvents defaults to NewestFirst. OldestFirst may buffer the selected
/// Event addresses; either direction validates a supplied lower bound before delivering an item.
/// </remarks>
public enum HistoryOrder {
    /// <summary>Return the oldest selected Event first; preparing the range may buffer all its Event addresses.</summary>
    OldestFirst,
    /// <summary>Return the newest selected Event first; an unbounded query reads ancestors on demand.</summary>
    NewestFirst,
}
