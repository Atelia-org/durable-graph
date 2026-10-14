namespace Atelia.DurableGraph.Schema;

/// <summary>Exact key and value representations; count and comparer belong to each object state.</summary>
public sealed class DictionaryLayout : IEquatable<DictionaryLayout> {
    public DictionaryLayout(DurableFieldInfo keySlot, DurableFieldInfo valueSlot, uint codecVersion = 1) {
        if (codecVersion != 1) { throw new ArgumentOutOfRangeException(nameof(codecVersion), "Unsupported Dictionary codec version."); }
        if (keySlot.FieldId <= 0 || valueSlot.FieldId <= 0) { throw new ArgumentException("A Dictionary requires two complete slots."); }
        KeySlot = DurableFieldInfo.WithFieldId(keySlot, 1);
        ValueSlot = DurableFieldInfo.WithFieldId(valueSlot, 2);
        CodecVersion = codecVersion;
        Type = TypeExpr.Dictionary(DurableFieldInfo.NominalType(KeySlot), DurableFieldInfo.NominalType(ValueSlot));
    }

    public DurableFieldInfo KeySlot { get; }
    public DurableFieldInfo ValueSlot { get; }
    public uint CodecVersion { get; }
    public TypeExpr Type { get; }
    public bool Equals(DictionaryLayout? other) => other is not null && CodecVersion == other.CodecVersion &&
        KeySlot.Equals(other.KeySlot) && ValueSlot.Equals(other.ValueSlot);
    public override bool Equals(object? obj) => obj is DictionaryLayout other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(CodecVersion, KeySlot, ValueSlot);
}
