using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;

namespace EventHistoryRecovery;

// Application protocol: this consumer always records exactly one Event followed by its State.
// Only under that protocol does an Event head mean unfinished in-memory work. Free E/E/S/S
// histories need their own durable processing cursor. No head kind proves external side effects.
// Recover with fresh resources; exceptions end the attempt, never trigger a transparent retry.
internal static class PendingRecovery {
    public static bool Complete<TState>(Repository repository, WorkingCopy session,
        Action<TState> rebuildTransient, Action<TState, IDurableObject> apply) where TState : class, IDurableObject {
        TState state = (TState)(session.State ?? throw new InvalidOperationException("This application requires an initial State."));
        rebuildTransient(state);
        if (repository.ReadCommitInfo(session.BaseCommitId).Kind != CommitKind.Event) { return false; }
        IDurableObject pending = repository.LoadEvent(session.BaseCommitId);
        apply(state, pending);
        session.CommitState();
        return true;
    }
}
