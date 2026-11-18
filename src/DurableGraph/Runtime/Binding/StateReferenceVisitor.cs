using Atelia.DurableGraph.Schema;

namespace Atelia.DurableGraph.Runtime;

/// <summary>Receives only reference slots from a frozen DTO, independently of its byte body.</summary>
public interface IStateReferenceVisitor {
    void VisitString(ObjectId id);
    void VisitDurable(ObjectId id, string nominalSchemaId);

    /// <summary>Visits a slot constrained to a supported reference family.</summary>
    void VisitObject(ObjectId id, TypeExpr declaredType) {
        StateReferenceValidator.RequireReferenceType(declaredType);
        if (declaredType.Kind == TypeExprKind.Builtin) { VisitString(id); }
        else if (declaredType.Kind == TypeExprKind.Named) { VisitDurable(id, declaredType); }
        else { throw new NotSupportedException($"This visitor does not support reference type {declaredType}."); }
    }

    /// <summary>Visits a reference constrained by a complete constructed nominal identity.</summary>
    void VisitDurable(ObjectId id, TypeExpr nominalType) {
        ArgumentNullException.ThrowIfNull(nominalType);
        if (nominalType.Kind != TypeExprKind.Named || !nominalType.IsClosed) {
            throw new ArgumentException("A reference requires a closed named type.", nameof(nominalType));
        }
        if (!nominalType.Arguments.IsEmpty) {
            throw new NotSupportedException("This visitor does not support constructed nominal identities.");
        }
        VisitDurable(id, nominalType.DefinitionId!);
    }
}

/// <summary>Visits all inherited and declared reference slots in one exact-version DTO.</summary>
public delegate void StateReferenceVisitor<TState>(in TState state, IStateReferenceVisitor visitor)
    where TState : unmanaged;

/// <summary>Validates references against one complete stored or current DTO directory.</summary>
/// <remarks>Keep the directory stable while visiting. Schema ancestry comes from this view, never current CLR ancestry.</remarks>
public sealed class StateReferenceValidator : IStateReferenceVisitor {
    private readonly IReadOnlyDictionary<ObjectId, ObjectStateRecord> _objects;

    public StateReferenceValidator(IReadOnlyDictionary<ObjectId, ObjectStateRecord> objects) {
        ArgumentNullException.ThrowIfNull(objects);
        _objects = objects;
    }

    public void VisitString(ObjectId id) => VisitObject(id, TypeExpr.Builtin(TypeTag.String));

    public void VisitDurable(ObjectId id, string nominalSchemaId) => VisitDurable(id, TypeExpr.Named(nominalSchemaId));

    public void VisitDurable(ObjectId id, TypeExpr nominalType) => VisitObject(id, nominalType);

    public void VisitObject(ObjectId id, TypeExpr declaredType) {
        RequireReferenceType(declaredType);
        if (id.IsNull) {
            return;
        }
        if (!_objects.TryGetValue(id, out ObjectStateRecord? item) || !Accepts(item.Layout, declaredType)) {
            throw new InvalidDataException($"Object ID {id} does not satisfy reference type {declaredType} in this DTO view.");
        }
    }

    internal static bool Accepts(ObjectLayout layout, TypeExpr declaredType) {
        RequireReferenceType(declaredType);
        return layout.Kind switch {
            ObjectStateKind.String => declaredType == TypeExpr.Builtin(TypeTag.String),
            ObjectStateKind.Text => declaredType.Kind == TypeExprKind.Text,
            ObjectStateKind.Durable => declaredType.Kind == TypeExprKind.Named && Accepts(layout.Schema!, declaredType),
            ObjectStateKind.Array => declaredType.IsArray && layout.Array!.Type == declaredType,
            ObjectStateKind.List => declaredType.IsList && layout.List!.Type == declaredType,
            ObjectStateKind.Dictionary => declaredType.IsDictionary && layout.Dictionary!.Type == declaredType,
            _ => false,
        };
    }

    internal static bool Accepts(DurableSchema schema, string nominalSchemaId) => Accepts(schema, TypeExpr.Named(nominalSchemaId));

    internal static bool Accepts(DurableSchema schema, TypeExpr nominalType) {
        RequireNominal(nominalType);
        if (schema.Kind != SchemaKind.ReferenceObject) { return false; }
        for (DurableSchema? current = schema; current is not null; current = current.BaseSchema) {
            if (current.Type.Equals(nominalType)) {
                return true;
            }
        }
        return false;
    }

    private static void RequireNominal(TypeExpr nominalType) {
        ArgumentNullException.ThrowIfNull(nominalType);
        if (nominalType.Kind != TypeExprKind.Named || !nominalType.IsClosed) {
            throw new ArgumentException("A reference requires a closed named type.", nameof(nominalType));
        }
    }

    internal static void RequireReferenceType(TypeExpr declaredType) {
        ArgumentNullException.ThrowIfNull(declaredType);
        if (!declaredType.IsClosed || !(declaredType.IsObjectReferenceTarget || declaredType == TypeExpr.Builtin(TypeTag.String))) {
            throw new ArgumentException("A reference requires a closed object type or string.", nameof(declaredType));
        }
    }
}
