using Atelia.HeadStore;

namespace Atelia.DurableGraph;

/// <summary>A compact persistent branch location in the selected repository.</summary>
public readonly struct BranchId : IEquatable<BranchId> {
    public const int EncodedSize = 4;
    internal BranchId(HeadId value) => Value = value;
    internal HeadId Value { get; }
    public bool TryWrite(Span<byte> destination) => Value.TryWrite(destination);
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
