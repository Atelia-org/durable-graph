namespace Atelia.DurableGraph.Persistence.MetaBootstrapP0;

// One owner-bound operation. Discard never appends or accepts either producer.
internal sealed class P0PendingSave : IDisposable {
    private enum PendingState { Prepared, Committing, Finished }
    private PendingState _state;

    internal P0PendingSave(P0Repository owner, P0Repository.PublicationBaseline baseline,
        string branch, P0GraphKind kind, P0PreparedGraphWrite business, P0PreparedGraphWrite? catalog) {
        Owner = owner;
        Baseline = baseline;
        Branch = branch;
        Kind = kind;
        Business = business;
        Catalog = catalog;
    }

    internal P0Repository Owner { get; }
    internal P0Repository.PublicationBaseline Baseline { get; }
    internal string Branch { get; }
    internal P0GraphKind Kind { get; }
    internal P0PreparedGraphWrite Business { get; }
    internal P0PreparedGraphWrite? Catalog { get; }
    internal bool IsPrepared => _state == PendingState.Prepared;

    internal void BeginCommit() {
        if (!IsPrepared) { throw new InvalidOperationException("A P0 pending save is single-use."); }
        _state = PendingState.Committing;
    }

    internal void Finish() => _state = PendingState.Finished;

    public void Dispose() {
        if (_state == PendingState.Finished) { return; }
        if (_state == PendingState.Committing) { throw new InvalidOperationException("An active P0 commit cannot be discarded."); }
        Owner.Discard(this);
    }
}
