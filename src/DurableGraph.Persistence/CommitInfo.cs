namespace Atelia.DurableGraph;

/// <summary>A lightweight description; Summary storage belongs exclusively to this result.</summary>
/// <remarks>Describes a saved State or Event without loading domain objects. The ID remains usable after reopening the same repository.</remarks>
public sealed class CommitInfo {
    internal CommitInfo(CommitId id, CommitKind kind, ReadOnlyMemory<byte> summary) {
        Id = id;
        Kind = kind;
        Summary = summary.ToArray();
    }
    /// <summary>The exact saved commit's persistent identity in its repository.</summary>
    public CommitId Id { get; }
    /// <summary>The branch on which this commit was published, even when another branch inherits it.</summary>
    public BranchId CreatedOnBranchId => new(Id.Value.HeadId);
    /// <summary>Whether the commit stores a State or an Event.</summary>
    public CommitKind Kind { get; }
    /// <summary>Owned opaque preview bytes captured with this commit, or empty bytes when omitted.</summary>
    public ReadOnlyMemory<byte> Summary { get; }
}

/// <summary>A registered branch and its fixed creation origin. Labels need not be unique.</summary>
/// <remarks>ForkedFromCommitId is null for roots. A fork records the selected commit itself, including when it was selected through another branch's inherited history.</remarks>
/// <param name="Id">The persistent branch identity.</param>
/// <param name="DisplayLabel">The fixed display label, not a lookup key.</param>
/// <param name="ForkedFromCommitId">The exact inherited source commit, or null for a root branch.</param>
public sealed record BranchInfo(BranchId Id, string DisplayLabel, CommitId? ForkedFromCommitId);

/// <summary>An immutable named bookmark for an exact saved commit.</summary>
/// <param name="Name">The unique, ordinal case-sensitive bookmark name.</param>
/// <param name="CommitId">The persistent target identity in this repository.</param>
public sealed record TagInfo(string Name, CommitId CommitId);

/// <summary>The selected branch together with its current commit, possibly inherited from another branch.</summary>
/// <remarks>The selected BranchId is the continuation target; Commit.CreatedOnBranchId identifies where the saved commit was actually published.</remarks>
/// <param name="BranchId">The branch whose current position was queried.</param>
/// <param name="Commit">Its current saved commit and preview summary.</param>
public sealed record CurrentCommit(BranchId BranchId, CommitInfo Commit);

/// <summary>The nearest saved State at or before a selected commit; no Event is replayed.</summary>
/// <remarks>StateCommitId and State are both null when no State exists in the selected history. The restored graph is independent and is not an editing session.</remarks>
/// <param name="SelectedCommitId">The queried State or Event position.</param>
/// <param name="StateCommitId">The actual State position, which may precede the selected position.</param>
/// <param name="State">The independently restored State root, or null when absent.</param>
public sealed record LoadedState(CommitId SelectedCommitId, CommitId? StateCommitId, IDurableObject? State);
