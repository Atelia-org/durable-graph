using Atelia.Binary;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.ListDeltaReplayProbe;

internal sealed record ProductDiffDiagnostics(string Outcome, bool Triggered, int IncumbentBytes,
    int ChallengerWrittenBytes, int SearchBudgetPerMatcher, int ExplicitLocalBytes,
    long StateEqualsCalls, long ElementBaseWrites, long ElementDeltaPrepares);

/// <summary>Untimed observation of the actual product writer, without treating Adaptive as a matcher.</summary>
internal static class ProductDiagnostics {
    internal static ProductDiffDiagnostics Observe<TState, TOps>(FrozenListState<TState> prior,
        FrozenListState<TState> current, ListLayout layout, ListDeltaAlgorithm algorithm, PreparedDeltaBody expected)
        where TState : unmanaged where TOps : IStateOps<TState> {
        CountedOps<TState, TOps>.Reset();
        PreparedDeltaBody observed = ListStateBody<TState, CountedOps<TState, TOps>>.PrepareDeltaObserved(
            prior, current, layout, out ListDeltaCompetitionObservation observation, algorithm);
        if (observed.HasChanges != expected.HasChanges || !observed.Body.SequenceEqual(expected.Body)) {
            throw new InvalidOperationException("Product writer instrumentation changed bytes or HasChanges.");
        }
        PreparedDeltaBody local = ListStateBody<TState, TOps>.PrepareDelta(prior, current, layout, ListDeltaAlgorithm.LocalResync);
        if (algorithm == ListDeltaAlgorithm.Adaptive &&
            (observed.HasChanges != local.HasChanges || observed.Body.Length > local.Body.Length)) {
            throw new InvalidOperationException("Adaptive exceeded its explicit Local incumbent.");
        }
        int expectedBudget = (int)Math.Min(1_000_000L, 4096L + 8L * (prior.Count + (long)current.Count));
        if (algorithm == ListDeltaAlgorithm.Adaptive && observed.HasChanges &&
            (observation.SearchBudgetPerMatcher != expectedBudget || observation.IncumbentBytes != local.Body.Length)) {
            throw new InvalidOperationException("Adaptive did not retain the complete Local incumbent and independent full search budget.");
        }
        return new(observation.Outcome.ToString(), observation.Triggered, observation.IncumbentBytes,
            observation.ChallengerWrittenBytes, observation.SearchBudgetPerMatcher, local.Body.Length,
            CountedOps<TState, TOps>.EqualityCalls, CountedOps<TState, TOps>.BaseCalls, CountedOps<TState, TOps>.DeltaCalls);
    }

    // Counts direct List element calls, not recursively expanded generated struct fields.
    // Static state is intentionally confined to this single-threaded measurement host.
    private readonly struct CountedOps<TState, TOps> : IStateOps<TState>
        where TState : unmanaged where TOps : IStateOps<TState> {
        internal static long EqualityCalls;
        internal static long BaseCalls;
        internal static long DeltaCalls;
        internal static void Reset() { EqualityCalls = BaseCalls = DeltaCalls = 0; }
        public static bool StateEquals(in TState left, in TState right, DurableFieldInfo slot) {
            EqualityCalls++;
            return TOps.StateEquals(in left, in right, slot);
        }
        public static void WriteBase(ref BareValueWriter writer, in TState value, DurableFieldInfo slot) {
            BaseCalls++;
            TOps.WriteBase(ref writer, in value, slot);
        }
        public static PreparedDeltaBody PrepareDelta(in TState prior, in TState current, DurableFieldInfo slot) {
            DeltaCalls++;
            return TOps.PrepareDelta(in prior, in current, slot);
        }
        public static TState ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => TOps.ReadBase(ref reader, slot);
        public static TState ApplyDelta(ref BareValueReader reader, in TState prior, DurableFieldInfo slot) =>
            TOps.ApplyDelta(ref reader, in prior, slot);
        public static void VisitReferences(in TState state, IStateReferenceVisitor visitor, DurableFieldInfo slot) =>
            TOps.VisitReferences(in state, visitor, slot);
    }
}
