namespace Atelia.DurableGraph.Persistence;

/// <summary>Restoration evidence counters, optionally accumulated across operations; not physical Frame I/O.</summary>
internal sealed class GraphReadStatistics {
    internal int DecodedObjects { get; set; }
    internal int CacheHits { get; set; }
    // Allocate calls include strings, and do not equal newly allocated CLR heap objects.
    internal int AllocatedObjects { get; set; }
    internal int HydratedObjects { get; set; }
    internal int SharedObjects { get; set; }
    internal int PreparedStateHits { get; set; }
    internal int PreparedStateMisses { get; set; }
    // Counts actual Normalize calls, including exact-current identity normalization.
    internal int NormalizedObjects { get; set; }
    internal int PreparedGraphs { get; set; }
    internal int ReachabilityVisits { get; set; }
    internal int CurrentReferenceValidationVisits { get; set; }
    internal int DictionaryValidationCalls { get; set; }
    internal int ReusedImmutableLeaves { get; set; }
    // Stopwatch ticks for allocation/table construction/hydration, excluding editable import.
    // Recorded only when the caller explicitly supplies a statistics instance.
    internal long MaterializationElapsedTicks { get; set; }
}
