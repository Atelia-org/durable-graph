namespace Atelia.DurableGraph.Schema;

/// <summary>Describes one complete persisted value slot with a positive identifier and exact value dependencies.</summary>
/// <remarks>A slot can describe a declared Schema field or a container element, key or value.</remarks>
public readonly record struct DurableFieldInfo {
    public DurableFieldInfo(int fieldId, TypeTag typeTag, string? targetSchemaId = null, DurableSchema? inlineSchema = null)
        : this(fieldId, typeTag, targetSchemaId is null ? null : TypeExpr.Named(targetSchemaId), inlineSchema) {
    }

    private DurableFieldInfo(int fieldId, TypeTag typeTag, TypeExpr? targetType, DurableSchema? inlineSchema,
        NullableValueLayout? nullableLayout = null) {
        if (fieldId <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(fieldId),
                fieldId,
                "Field identifiers must be positive.");
        }

        if (typeTag is TypeTag.Invalid || !Enum.IsDefined(typeTag)) {
            throw new ArgumentOutOfRangeException(
                nameof(typeTag),
                typeTag,
                "The type tag must identify a supported durable field type.");
        }

        if (typeTag == TypeTag.ObjectReference) {
            ArgumentNullException.ThrowIfNull(targetType);
            if ((targetType.Kind != TypeExprKind.Named && !targetType.IsArray && !targetType.IsList && !targetType.IsDictionary) || !targetType.IsClosed) {
                throw new ArgumentException("An object reference requires a closed named or array type.", nameof(targetType));
            }
        }
        else if (targetType is not null) {
            throw new ArgumentException("Only object references have a nominal target type.", nameof(targetType));
        }

        if (typeTag == TypeTag.InlineValue) {
            ArgumentNullException.ThrowIfNull(inlineSchema);
            if (inlineSchema.Kind != SchemaKind.InlineValue) {
                throw new ArgumentException("An inline field requires an exact inline value Schema.", nameof(inlineSchema));
            }
        }
        else if (inlineSchema is not null) {
            throw new ArgumentException("Only inline values have an exact inline Schema.", nameof(inlineSchema));
        }

        if (typeTag == TypeTag.Nullable) {
            ArgumentNullException.ThrowIfNull(nullableLayout);
        } else if (nullableLayout is not null) {
            throw new ArgumentException("Only nullable values have an exact nullable layout.", nameof(nullableLayout));
        }

        FieldId = fieldId;
        TypeTag = typeTag;
        TargetType = targetType;
        InlineSchema = inlineSchema;
        NullableLayout = nullableLayout;
    }

    /// <summary>Gets the positive identifier within the owning declaration or layout.</summary>
    /// <remarks>
    /// Schema fields use their declaration identifiers. Array and List elements and Nullable children use
    /// identifier one; Dictionary keys and values use identifiers one and two.
    /// </remarks>
    public int FieldId { get; }

    public TypeTag TypeTag { get; }

    /// <summary>Gets the target definition identifier for a named reference; array references have no definition identifier.</summary>
    public string? TargetSchemaId => TargetType?.DefinitionId;

    /// <summary>Gets the complete closed nominal reference constraint, without binding a version.</summary>
    public TypeExpr? TargetType { get; }

    public static DurableFieldInfo Reference(int fieldId, TypeExpr targetType) =>
        new(fieldId, TypeTag.ObjectReference, targetType, inlineSchema: null);

    /// <summary>Gets the immutable exact layout of an inline value, including its value dependencies.</summary>
    public DurableSchema? InlineSchema { get; }

    /// <summary>Gets the exact child layout of a nullable value.</summary>
    public NullableValueLayout? NullableLayout { get; }

    /// <summary>Gets the exact inline dependency, including one inside a nullable wrapper.</summary>
    public DurableSchema? ValueSchema => InlineSchema ?? NullableLayout?.ElementSlot.InlineSchema;

    public static DurableFieldInfo Nullable(int fieldId, DurableFieldInfo element) =>
        new(fieldId, TypeTag.Nullable, null, null, new NullableValueLayout(element));

    /// <summary>Returns the complete slot with a different positive identifier.</summary>
    /// <remarks>Preserves the exact target type, inline Schema and Nullable element layout.</remarks>
    public static DurableFieldInfo WithFieldId(DurableFieldInfo slot, int fieldId) => slot.TypeTag switch {
        TypeTag.ObjectReference => DurableFieldInfo.Reference(fieldId, slot.TargetType!),
        TypeTag.InlineValue => new(fieldId, TypeTag.InlineValue, inlineSchema: slot.InlineSchema),
        TypeTag.Nullable => DurableFieldInfo.Nullable(fieldId, slot.NullableLayout!.ElementSlot),
        _ => new(fieldId, slot.TypeTag),
    };

    /// <summary>Projects the slot to its nominal type, ignoring its identifier and inline Schema version.</summary>
    /// <remarks>The result does not establish equality of complete persisted value layouts.</remarks>
    public static TypeExpr NominalType(DurableFieldInfo slot) => slot.TypeTag switch {
        TypeTag.ObjectReference => slot.TargetType!,
        TypeTag.InlineValue => slot.InlineSchema!.Type,
        TypeTag.Nullable => TypeExpr.Nullable(NominalType(slot.NullableLayout!.ElementSlot)),
        _ => TypeExpr.Builtin(slot.TypeTag),
    };
}
