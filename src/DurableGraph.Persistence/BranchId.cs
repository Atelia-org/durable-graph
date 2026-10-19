using Atelia.HeadStore;

namespace Atelia.DurableGraph;

/// <summary>A compact persistent branch location in the selected repository.</summary>
/// <remarks>Can be encoded and reused after reopening the same repository. Decoding checks representation, not membership; repository operations validate the location. Cross-repository misuse detection is probabilistic, so callers must select the original repository.</remarks>
public readonly struct BranchId : IEquatable<BranchId> {
    /// <summary>The fixed encoded size in bytes.</summary>
    public const int EncodedSize = 4;
    internal BranchId(HeadId value) => Value = value;
    internal HeadId Value { get; }
    /// <summary>Writes the persistent location when the destination has sufficient space.</summary>
    /// <returns>False for the default ID or insufficient space; otherwise writes the first EncodedSize bytes.</returns>
    public bool TryWrite(Span<byte> destination) => Value.TryWrite(destination);
    /// <summary>Decodes an exactly sized location without opening or checking a repository.</summary>
    public static bool TryRead(ReadOnlySpan<byte> source, out BranchId value) {
        value = default;
        if (!HeadId.TryRead(source, out var parsed)) { return false; }
        value = new(parsed);
        return true;
    }
    public bool Equals(BranchId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is BranchId other && Equals(other);
    public override int GetHashCode() => Value.GetHashCode();
    public static bool operator ==(BranchId left, BranchId right) => left.Equals(right);
    public static bool operator !=(BranchId left, BranchId right) => !left.Equals(right);
}
