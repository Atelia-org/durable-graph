using Atelia.FrameStore;
using Atelia.HeadStore;

namespace Atelia.DurableGraph.Persistence;

internal sealed class PendingRepositorySave : IDisposable {
    private enum PendingState { Prepared, Committing, Finished }
    private PendingState _state;
    internal PendingRepositorySave(RepositoryResources owner, RepositoryResources.PublicationBaseline baseline,
        HeadId? branch, HeadEntryId? expectedEntry, string? rootLabel, StoredGraphKind kind, ReadOnlyMemory<byte> summary,
        PreparedGraphWrite business, PreparedGraphWrite? catalog, FrameAddress? catalogSource) {
        Owner = owner;
        Baseline = baseline;
        Members = [new(branch, expectedEntry, rootLabel, kind, summary, business)];
        Catalog = catalog;
        CatalogSource = catalogSource;

    }
    internal RepositoryResources Owner { get; }
    internal RepositoryResources.PublicationBaseline Baseline { get; }
    internal PendingRepositorySave(RepositoryResources owner, RepositoryResources.PublicationBaseline baseline,
        PendingBusinessSave[] members, PreparedGraphWrite? catalog, FrameAddress? catalogSource) {
        Owner = owner;
        Baseline = baseline;
        Members = members;
        Catalog = catalog;
        CatalogSource = catalogSource;
    }
    internal PendingBusinessSave[] Members { get; }
    internal HeadId? Branch => Single.Branch;
    internal HeadEntryId? ExpectedEntry => Single.ExpectedEntry;
    internal string? RootLabel => Single.RootLabel;
    internal StoredGraphKind Kind => Single.Kind;
    internal ReadOnlyMemory<byte> Summary => Single.Summary;
    internal PreparedGraphWrite Business => Single.Business;
    private PendingBusinessSave Single => Members.Length == 1 ? Members[0]
        : throw new InvalidOperationException("This save has multiple members.");
    internal PreparedGraphWrite? Catalog { get; }
    internal FrameAddress? CatalogSource { get; }
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

internal sealed record PendingBusinessSave(HeadId? Branch, HeadEntryId? ExpectedEntry, string? RootLabel,
    StoredGraphKind Kind, ReadOnlyMemory<byte> Summary, PreparedGraphWrite Business);

internal sealed record PreparedSaveGroup(PendingBusinessSave[] Members, PreparedGraphWrite? Catalog);
