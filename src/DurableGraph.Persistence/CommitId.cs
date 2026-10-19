using Atelia.HeadStore;

namespace Atelia.DurableGraph;

/// <summary>A compact persistent commit location in the selected repository.</summary>
public readonly struct CommitId : IEquatable<CommitId> {
    public const int EncodedSize = 12;
    internal CommitId(HeadEntryId value) => Value = value;
    internal HeadEntryId Value { get; }
    public bool TryWrite(Span<byte> destination) => Value.TryWrite(destination);
    public static bool TryRead(ReadOnlySpan<byte> source, out CommitId value) {
        value = default;
        if (!HeadEntryId.TryRead(source, out var parsed)) { return false; }
        value = new(parsed);
        return true;
    }
    public bool Equals(CommitId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is CommitId other && Equals(other);
    public override int GetHashCode() => Value.GetHashCode();
    public static bool operator ==(CommitId left, CommitId right) => left.Equals(right);
    public static bool operator !=(CommitId left, CommitId right) => !left.Equals(right);
}
