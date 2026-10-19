namespace Atelia.DurableGraph;

/// <summary>An independently restored historical graph and its nearest opposite-role context.</summary>
/// <remarks>
/// Created by Repository.ReadCheckpoint only after all requested graphs have been restored.
/// Roots are stable within this result. Editing them changes memory only, not a branch or checkout;
/// pass a resulting State to an explicit checkout commit to save it. Each graph preserves its own
/// aliases and cycles but does not share mutable instances with the other graph. Strings and
/// application-owned global objects do not acquire a general deep-copy guarantee.
/// </remarks>
public abstract class Checkpoint {
    internal Checkpoint(CommitInfo address) => Commit = address;

    /// <summary>The selected committed position, identified by a persistent CommitId.</summary>
    public CommitInfo Commit { get; }
}

/// <summary>An Event graph and, when present, its nearest strict ancestor State graph.</summary>
/// <remarks>The previous State does not identify application progress or imply that applying this Event alone produces the next State.</remarks>
public sealed class EventCheckpoint : Checkpoint {
    internal EventCheckpoint(CommitInfo address, IDurableObject domainEvent,
        IDurableObject? previousState, CommitInfo? previousStateCommit) : base(address) {
        Event = domainEvent;
        PreviousState = previousState;
        PreviousStateCommit = previousStateCommit;
    }

    /// <summary>The selected Event's actual domain root; repeated access returns the same graph.</summary>
    public IDurableObject Event { get; }
    /// <summary>The nearest strict ancestor State's independent root, or null before the first State.</summary>
    public IDurableObject? PreviousState { get; }
    /// <summary>The position of PreviousState; null exactly when PreviousState is null.</summary>
    public CommitInfo? PreviousStateCommit { get; }
}

/// <summary>A State graph and, when present, its nearest strict ancestor Event graph.</summary>
/// <remarks>The previous Event need not be the direct parent or the State's unique cause, and does not indicate whether the application applied it.</remarks>
public sealed class StateCheckpoint : Checkpoint {
    internal StateCheckpoint(CommitInfo address, IDurableObject state,
        IDurableObject? previousEvent, CommitInfo? previousEventCommit) : base(address) {
        State = state;
        PreviousEvent = previousEvent;
        PreviousEventCommit = previousEventCommit;
    }

    /// <summary>The selected State's actual domain root; repeated access returns the same graph.</summary>
    public IDurableObject State { get; }
    /// <summary>The nearest strict ancestor Event's independent root, or null if there is none.</summary>
    public IDurableObject? PreviousEvent { get; }
    /// <summary>The position of PreviousEvent; null exactly when PreviousEvent is null.</summary>
    public CommitInfo? PreviousEventCommit { get; }
}
