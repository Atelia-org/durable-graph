namespace Atelia.DurableGraph.Schema;

/// <summary>The immutable exact child of a nullable value slot; it has no independent Schema or object identity.</summary>
public sealed class NullableValueLayout : IEquatable<NullableValueLayout> {
    public NullableValueLayout(DurableFieldInfo elementSlot) {
        if (elementSlot.FieldId <= 0 || elementSlot.TypeTag is TypeTag.Invalid or TypeTag.String or
            TypeTag.ObjectReference or TypeTag.Nullable || !Enum.IsDefined(elementSlot.TypeTag)) {
            throw new ArgumentException("A nullable child must be a supported scalar or inline value.", nameof(elementSlot));
        }
        ElementSlot = DurableFieldInfo.WithFieldId(elementSlot, 1);
    }

    /// <summary>Gets the exact child slot, canonically numbered one.</summary>
    public DurableFieldInfo ElementSlot { get; }

    public bool Equals(NullableValueLayout? other) =>
        ReferenceEquals(this, other) || other is not null && ElementSlot == other.ElementSlot;

    public override bool Equals(object? obj) => obj is NullableValueLayout other && Equals(other);
    public override int GetHashCode() => ElementSlot.GetHashCode();
}
