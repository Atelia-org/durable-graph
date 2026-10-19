using Atelia.FrameStore;
using Atelia.HeadStore;

namespace Atelia.DurableGraph.Persistence;

internal sealed class PendingRepositorySave : IDisposable {
    private enum PendingState { Prepared, Committing, Finished }
    private PendingState _state;
    internal PendingRepositorySave(RepositoryResources owner, RepositoryResources.PublicationBaseline baseline,
        HeadId? branch, HeadEntryId? expectedEntry, string? rootLabel, StoredGraphKind kind, ReadOnlyMemory<byte> summary,
        PreparedGraphWrite business, PreparedGraphWrite? catalog, FrameAddress? catalogSource, FrameAddress? stateSource) {
        Owner = owner;
        Baseline = baseline;
        Branch = branch;
        ExpectedEntry = expectedEntry;
        RootLabel = rootLabel;
        Kind = kind;
        Summary = summary;
        Business = business;
        Catalog = catalog;
        CatalogSource = catalogSource;
        StateSource = stateSource;
    }
    internal RepositoryResources Owner { get; }
    internal RepositoryResources.PublicationBaseline Baseline { get; }
    internal HeadId? Branch { get; }
    internal HeadEntryId? ExpectedEntry { get; }
    internal string? RootLabel { get; }
    internal StoredGraphKind Kind { get; }
    internal ReadOnlyMemory<byte> Summary { get; }
    internal PreparedGraphWrite Business { get; }
    internal PreparedGraphWrite? Catalog { get; }
    internal FrameAddress? CatalogSource { get; }
    internal FrameAddress? StateSource { get; }
    internal bool IsPrepared => _state == PendingState.Prepared;
    internal void BeginCommit() {
        if (!IsPrepared) { throw new InvalidOperationException("A pending save is single-use."); }
        _state = PendingState.Committing;
    }
    internal void Finish() => _state = PendingState.Finished;
    public void Dispose() {
        if (_state == PendingState.Finished) { return; }
        if (_state == PendingState.Committing) { throw new InvalidOperationException("An active commit cannot be discarded."); }
        Owner.DiscardSave(this);
    }
}
