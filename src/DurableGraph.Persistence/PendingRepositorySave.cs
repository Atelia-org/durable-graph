using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence;

// One owner-bound operation. Discard never appends or accepts either producer.
internal sealed class PendingRepositorySave : IDisposable {
    private enum PendingState { Prepared, Committing, Finished }
    private PendingState _state;

    internal PendingRepositorySave(RepositoryResources owner, RepositoryResources.PublicationBaseline baseline,
        string branch, StoredGraphKind kind, PreparedGraphWrite business, PreparedGraphWrite? catalog,
        FrameAddress? catalogSource) {
        Owner = owner;
        Baseline = baseline;
        Branch = branch;
        Kind = kind;
        Business = business;
        Catalog = catalog;
        CatalogSource = catalogSource;
    }

    internal RepositoryResources Owner { get; }
    internal RepositoryResources.PublicationBaseline Baseline { get; }
    internal string Branch { get; }
    internal StoredGraphKind Kind { get; }
    internal PreparedGraphWrite Business { get; }
    internal PreparedGraphWrite? Catalog { get; }
    internal FrameAddress? CatalogSource { get; }
    internal bool IsPrepared => _state == PendingState.Prepared;

    internal void BeginCommit() {
        if (!IsPrepared) { throw new InvalidOperationException("A P0 pending save is single-use."); }
        _state = PendingState.Committing;
    }

    internal void Finish() => _state = PendingState.Finished;

    public void Dispose() {
        if (_state == PendingState.Finished) { return; }
        if (_state == PendingState.Committing) { throw new InvalidOperationException("An active P0 commit cannot be discarded."); }
        Owner.DiscardSave(this);
    }
}
