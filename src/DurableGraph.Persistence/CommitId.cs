using Atelia.HeadStore;

namespace Atelia.DurableGraph;

/// <summary>A compact persistent commit location in the selected repository.</summary>
/// <remarks>Can be encoded and reused after reopening the same repository. Decoding checks representation, not existence or State/Event role; repository operations validate the exact location. Cross-repository misuse detection is probabilistic, so callers must select the original repository.</remarks>
public readonly struct CommitId : IEquatable<CommitId> {
    /// <summary>The fixed encoded size in bytes.</summary>
    public const int EncodedSize = 12;
    internal CommitId(HeadEntryId value) => Value = value;
    internal HeadEntryId Value { get; }
    /// <summary>Writes the persistent location when the destination has sufficient space.</summary>
    /// <returns>False for the default ID or insufficient space; otherwise writes the first EncodedSize bytes.</returns>
    public bool TryWrite(Span<byte> destination) => Value.TryWrite(destination);
    /// <summary>Decodes an exactly sized location without opening or checking a repository.</summary>
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
