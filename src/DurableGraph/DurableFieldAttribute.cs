namespace Atelia.DurableGraph;

/// <summary>
/// Marks a field as persisted and assigns its stable identity within a durable type.
/// On a durable record property or positional parameter, use the field: target to
/// classify its backing storage. Ordinary class/struct property backing storage is
/// not supported for persistence; use an explicit field. Capture and restoration
/// do not invoke property accessors.
/// </summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class DurableFieldAttribute : Attribute {
    public DurableFieldAttribute(int fieldId) {
        if (fieldId <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(fieldId),
                fieldId,
                "Field identifiers must be positive.");
        }

        FieldId = fieldId;
    }

    public int FieldId { get; }
}
