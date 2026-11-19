namespace Atelia.DurableGraph;

/// <summary>Reason a conditional text edit batch was rejected.</summary>
public enum TextEditFailureKind {
    /// <summary>The batch succeeded.</summary>
    None,
    /// <summary>The snapshot belongs to a different editor or an earlier content version.</summary>
    StaleSnapshot,
    /// <summary>The start offset is outside the snapshot.</summary>
    InvalidStart,
    /// <summary>The removal length is negative or exceeds the remaining text.</summary>
    InvalidLength,
    /// <summary>The replacement string is null.</summary>
    MissingReplacement,
    /// <summary>The resulting length exceeds <see cref="int.MaxValue"/> UTF-16 units.</summary>
    ResultTooLong,
    /// <summary>The two identified edits cannot both be applied at their snapshot coordinates.</summary>
    ConflictingEdits,
}

/// <summary>Describes a rejected edit batch using indices in the caller's original list.</summary>
public readonly record struct TextEditFailure {
    internal TextEditFailure(TextEditFailureKind kind, int? editIndex = null, int? conflictingEditIndex = null) {
        Kind = kind;
        EditIndex = editIndex;
        ConflictingEditIndex = conflictingEditIndex;
    }

    /// <summary>Reason for rejection, or None on success.</summary>
    public TextEditFailureKind Kind { get; }
    /// <summary>Zero-based index of the invalid edit or a member of the conflicting pair, when applicable.</summary>
    public int? EditIndex { get; }
    /// <summary>Zero-based index of the other edit for ConflictingEdits, otherwise null.</summary>
    public int? ConflictingEditIndex { get; }
}
