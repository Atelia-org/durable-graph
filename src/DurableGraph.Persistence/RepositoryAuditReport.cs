namespace Atelia.DurableGraph;

/// <summary>Completed includes conclusively invalid data; Incomplete means some requested checks could not finish.</summary>
public enum RepositoryAuditCompletion { Completed, Incomplete, Cancelled }
public enum RepositoryAuditFindingKind { InvalidData, UnsupportedFormat, IoFailure, Warning }
public sealed record RepositoryAuditFinding(RepositoryAuditFindingKind Kind, string Code, string RelativePath, long? Offset, string Message);

/// <summary>Read-only evidence about persisted structure; does not validate application bodies or execute models.</summary>
public sealed class RepositoryAuditReport {
    internal RepositoryAuditReport() { }
    public int SchemaVersion => 1;
    public RepositoryAuditCompletion Completion { get; internal set; } = RepositoryAuditCompletion.Completed;
    internal List<RepositoryAuditFinding> MutableFindings { get; } = [];
    public IReadOnlyList<RepositoryAuditFinding> Findings => MutableFindings.AsReadOnly();
    public bool IsHealthy => Completion == RepositoryAuditCompletion.Completed && Findings.All(f => f.Kind == RepositoryAuditFindingKind.Warning);
    /// <summary>Complete, CRC-checked State frames with a recognized wire version inventoried before dependency validation.</summary>
    public long StateFrames { get; internal set; }
    /// <summary>Decoded Journal graph records whose preceding Journal ancestry was available.</summary>
    public long JournalEvents { get; internal set; }
}
