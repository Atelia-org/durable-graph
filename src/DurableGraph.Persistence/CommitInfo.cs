namespace Atelia.DurableGraph;

/// <summary>A lightweight description; Summary storage belongs exclusively to this result.</summary>
public sealed class CommitInfo {
    internal CommitInfo(CommitId id, CommitKind kind, ReadOnlyMemory<byte> summary) {
        Id = id;
        Kind = kind;
        Summary = summary.ToArray();
    }
    public CommitId Id { get; }
    public BranchId CreatedOnBranchId => new(Id.Value.HeadId);
    public CommitKind Kind { get; }
    public ReadOnlyMemory<byte> Summary { get; }
}

/// <summary>A registered branch and its fixed creation origin. Labels need not be unique.</summary>
public sealed record BranchInfo(BranchId Id, string DisplayLabel, CommitId? ForkedFromCommitId);

/// <summary>The selected branch together with its current commit, possibly inherited from another branch.</summary>
public sealed record CurrentCommit(BranchId BranchId, CommitInfo Commit);

/// <summary>The nearest saved State at or before a selected commit; no Event is replayed.</summary>
public sealed record LoadedState(CommitId SelectedCommitId, CommitId? StateCommitId, IDurableObject? State);
