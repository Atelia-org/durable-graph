namespace Atelia.DurableGraph.Schema;

/// <summary>Exact List element representation. Count belongs to each frozen object state.</summary>
public sealed class ListLayout : IEquatable<ListLayout> {
    public ListLayout(DurableFieldInfo elementSlot, uint codecVersion = 2) {
        if (codecVersion != 2) { throw new ArgumentOutOfRangeException(nameof(codecVersion), "Unsupported List codec version."); }
        if (elementSlot.FieldId <= 0) { throw new ArgumentException("A List requires a complete element slot.", nameof(elementSlot)); }
        CodecVersion = codecVersion;
        ElementSlot = DurableFieldInfo.WithFieldId(elementSlot, 1);
        Type = TypeExpr.List(DurableFieldInfo.NominalType(ElementSlot));
    }

    public uint CodecVersion { get; }
    public DurableFieldInfo ElementSlot { get; }
    public TypeExpr Type { get; }
    public bool Equals(ListLayout? other) => other is not null && CodecVersion == other.CodecVersion && ElementSlot.Equals(other.ElementSlot);
    public override bool Equals(object? obj) => obj is ListLayout other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(CodecVersion, ElementSlot);
}
