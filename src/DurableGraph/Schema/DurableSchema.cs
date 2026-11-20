using System.Collections.Immutable;

namespace Atelia.DurableGraph.Schema;

/// <summary>
/// Describes one wire-format-independent version of a durable schema.
/// </summary>
public sealed class DurableSchema : IEquatable<DurableSchema> {
    private readonly int _hashCode;

    public DurableSchema(
        string schemaId,
        int version,
        params DurableFieldInfo[] fields)
        : this(schemaId, version, fields, baseSchema: null) {
    }

    public DurableSchema(
        string schemaId,
        int version,
        SchemaKind kind,
        params DurableFieldInfo[] fields)
        : this(schemaId, version, fields, baseSchema: null, kind) {
    }

    public DurableSchema(
        string schemaId,
        int version,
        DurableFieldInfo[] fields,
        DurableSchema? baseSchema,
        SchemaKind kind = SchemaKind.ReferenceObject)
        : this(TypeExpr.Named(schemaId), version, fields, baseSchema, kind) {
    }

    public DurableSchema(TypeExpr type, int version, params DurableFieldInfo[] fields)
        : this(type, version, fields, baseSchema: null) {
    }

    public DurableSchema(TypeExpr type, int version, SchemaKind kind, params DurableFieldInfo[] fields)
        : this(type, version, fields, baseSchema: null, kind) {
    }

    public DurableSchema(TypeExpr type, int version, DurableFieldInfo[] fields,
        DurableSchema? baseSchema, SchemaKind kind = SchemaKind.ReferenceObject)
        : this(type, version, fields, baseSchema, kind, slots: null) {
    }

    internal static DurableSchema ForDeclaredNode(TypeExpr type, int version, IEnumerable<DeclaredSlotShape> slots) {
        ArgumentNullException.ThrowIfNull(slots);
        return new(type, version, [], null, SchemaKind.ReferenceObject, slots);
    }

    private DurableSchema(TypeExpr type, int version, DurableFieldInfo[] fields,
        DurableSchema? baseSchema, SchemaKind kind, IEnumerable<DeclaredSlotShape>? slots) {
        ArgumentNullException.ThrowIfNull(type);
        if (type.Kind != TypeExprKind.Named || !type.IsClosed) {
            throw new ArgumentException("An exact Schema requires a closed named type.", nameof(type));
        }
        if (slots is not null && !type.Arguments.IsEmpty) {
            throw new ArgumentException("A declared node requires a non-generic named type.", nameof(type));
        }

        if (version <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(version),
                version,
                "Schema versions must be positive.");
        }

        ArgumentNullException.ThrowIfNull(fields);
        if (!Enum.IsDefined(kind)) {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        if (baseSchema is not null &&
            (kind != SchemaKind.ReferenceObject || baseSchema.Kind != SchemaKind.ReferenceObject)) {
            throw new ArgumentException("Only reference object Schemas can have a reference object base.", nameof(baseSchema));
        }
        if (baseSchema is { IsDeclaredNode: true }) {
            throw new ArgumentException("A declared node cannot supply an inherited field layout.", nameof(baseSchema));
        }

        HashSet<TypeExpr> ancestorIds = new() { type };
        for (DurableSchema? ancestor = baseSchema; ancestor is not null; ancestor = ancestor.BaseSchema) {
            if (!ancestorIds.Add(ancestor.Type)) {
                throw new ArgumentException(
                    "A schema identity cannot occur more than once in an inheritance chain.",
                    nameof(baseSchema));
            }
        }

        DurableFieldInfo[] canonicalFields = (DurableFieldInfo[])fields.Clone();
        Array.Sort(
            canonicalFields,
            static (left, right) => left.FieldId.CompareTo(right.FieldId));

        for (int index = 0; index < canonicalFields.Length; index++) {
            DurableFieldInfo field = canonicalFields[index];

            if (field.FieldId <= 0 ||
                field.TypeTag is TypeTag.Invalid ||
                !Enum.IsDefined(field.TypeTag)) {
                throw new ArgumentException(
                    "Every field must contain a valid field identifier and type tag.",
                    nameof(fields));
            }

            if (index > 0 && canonicalFields[index - 1].FieldId == field.FieldId) {
                throw new ArgumentException(
                    $"Field identifier {field.FieldId} is duplicated.",
                    nameof(fields));
            }
        }

        Type = type;
        Version = version;
        Kind = kind;
        Fields = ImmutableArray.CreateRange(canonicalFields);
        BaseSchema = baseSchema;
        IsDeclaredNode = slots is not null;
        DeclaredSlots = slots is null ? [] : [.. slots];
        if (IsDeclaredNode && DeclaredSlots.Contains(null!)) {
            throw new ArgumentException("A declared node slot cannot be null.", nameof(slots));
        }

        // Immutable dependencies already have their hashes. Hashing a shared DAG
        // must not expand it repeatedly as a tree. This is never a persistent hash.
        HashCode hashCode = new();
        hashCode.Add(Type);
        hashCode.Add(Version);
        hashCode.Add(Kind);
        hashCode.Add(BaseSchema);
        foreach (DurableFieldInfo field in Fields) { hashCode.Add(field); }
        if (IsDeclaredNode) {
            hashCode.Add(IsDeclaredNode);
            foreach (DeclaredSlotShape slot in DeclaredSlots) { hashCode.Add(slot); }
        }
        _hashCode = hashCode.ToHashCode();
    }

    /// <summary>Gets the complete closed nominal family, including ordered generic arguments.</summary>
    public TypeExpr Type { get; }

    /// <summary>Gets the declaration identity. Generic arguments are available from <see cref="Type"/>.</summary>
    public string SchemaId => Type.DefinitionId!;

    public int Version { get; }

    public SchemaKind Kind { get; }

    /// <summary>Gets the fields declared by this schema, excluding inherited fields.</summary>
    public ImmutableArray<DurableFieldInfo> Fields { get; }

    /// <summary>Gets the exact base schema, including its immutable ancestor chain.</summary>
    public DurableSchema? BaseSchema { get; }

    internal bool IsDeclaredNode { get; }
    internal ImmutableArray<DeclaredSlotShape> DeclaredSlots { get; }

    public bool Equals(DurableSchema? other) {
        if (ReferenceEquals(this, other)) { return true; }
        if (other is null) { return false; }
        // Declared nodes are fieldless reference objects and cannot occur as base or inline Schemas.
        if (IsDeclaredNode || other.IsDeclaredNode) {
            return IsDeclaredNode == other.IsDeclaredNode && Version == other.Version && Type == other.Type &&
                DeclaredSlots.AsSpan().SequenceEqual(other.DeclaredSlots.AsSpan());
        }
        // A fieldless root has no dependency graph to traverse.
        if (BaseSchema is null && other.BaseSchema is null && Fields.IsEmpty && other.Fields.IsEmpty) {
            return Version == other.Version && Kind == other.Kind && Type == other.Type;
        }
        var pending = new Stack<(DurableSchema Left, DurableSchema Right)>();
        var seen = new HashSet<(DurableSchema, DurableSchema)>(SchemaPairComparer.Instance);
        pending.Push((this, other));
        while (pending.TryPop(out var pair)) {
            var (left, right) = pair;
            if (ReferenceEquals(left, right) || !seen.Add(pair)) { continue; }
            if (left.Version != right.Version || left.Kind != right.Kind ||
                left.Type != right.Type ||
                left.Fields.Length != right.Fields.Length) { return false; }
            if ((left.BaseSchema is null) != (right.BaseSchema is null)) { return false; }
            if (left.BaseSchema is not null) { pending.Push((left.BaseSchema, right.BaseSchema!)); }
            for (int index = 0; index < left.Fields.Length; index++) {
                DurableFieldInfo a = left.Fields[index], b = right.Fields[index];
                if (a.FieldId != b.FieldId || a.TypeTag != b.TypeTag) { return false; }
                if (a.TypeTag == TypeTag.Nullable) {
                    a = a.NullableLayout!.ElementSlot;
                    b = b.NullableLayout!.ElementSlot;
                }
                if (a.TypeTag != b.TypeTag ||
                    a.TargetType != b.TargetType ||
                    (a.InlineSchema is null) != (b.InlineSchema is null)) { return false; }
                if (a.InlineSchema is not null) { pending.Push((a.InlineSchema, b.InlineSchema!)); }
            }
        }
        return true;
    }

    public override bool Equals(object? obj) {
        return obj is DurableSchema other && Equals(other);
    }

    public override int GetHashCode() => _hashCode;

    internal void RequireReferenceObject() {
        if (Kind != SchemaKind.ReferenceObject) {
            throw new ArgumentException("An identity-bearing object requires a reference object Schema.");
        }
    }

    private sealed class SchemaPairComparer : IEqualityComparer<(DurableSchema, DurableSchema)> {
        internal static readonly SchemaPairComparer Instance = new();
        public bool Equals((DurableSchema, DurableSchema) x, (DurableSchema, DurableSchema) y) =>
            ReferenceEquals(x.Item1, y.Item1) && ReferenceEquals(x.Item2, y.Item2);
        public int GetHashCode((DurableSchema, DurableSchema) pair) => HashCode.Combine(
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(pair.Item1),
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(pair.Item2));
    }

    public static bool operator ==(DurableSchema? left, DurableSchema? right) {
        return Equals(left, right);
    }

    public static bool operator !=(DurableSchema? left, DurableSchema? right) {
        return !Equals(left, right);
    }
}
