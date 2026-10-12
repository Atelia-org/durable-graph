using Atelia.DurableGraph.Schema;

namespace Atelia.DurableGraph.Persistence;

/// <summary>Nominal kind/arity consistency; independent of catalog numbering and wire format.</summary>
internal sealed class SchemaDeclarationValidator {
    private readonly Dictionary<string, SchemaKind> _kinds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _arities = new(StringComparer.Ordinal);

    internal void ValidateDeclaration(TypeExpr type, SchemaKind kind) {
        if (type.Kind == TypeExprKind.Named) {
            string id = type.DefinitionId!;
            if (_kinds.TryGetValue(id, out SchemaKind previous) && previous != kind) {
                throw new InvalidDataException($"Schema family '{id}' cannot change kind across versions.");
            }
            _kinds[id] = kind;
        }
        ValidateArities(type);
    }

    internal void ValidateArities(TypeExpr type) {
        if (!type.IsClosed) { throw new InvalidDataException("Persisted type expressions must be closed."); }
        if (type.IsNullable) {
            TypeExpr child = type.ElementType!;
            if (child.Kind == TypeExprKind.Named) { ValidateDeclaration(child, SchemaKind.InlineValue); }
            else { ValidateArities(child); }
            return;
        }
        if (type.IsArray || type.IsList) { ValidateArities(type.ElementType!); return; }
        if (type.IsDictionary) {
            ValidateArities(type.KeyType!);
            ValidateArities(type.ValueType!);
            return;
        }
        if (type.Kind != TypeExprKind.Named) { return; }
        string id = type.DefinitionId!;
        if (_arities.TryGetValue(id, out int arity) && arity != type.Arguments.Length) {
            throw new InvalidDataException($"Schema definition '{id}' cannot change generic arity.");
        }
        _arities[id] = type.Arguments.Length;
        foreach (TypeExpr argument in type.Arguments) { ValidateArities(argument); }
    }

    internal void ValidateSlot(DurableFieldInfo slot) {
        if (slot.TargetType is { } target) { ValidateDeclaration(target, SchemaKind.ReferenceObject); }
        if (slot.ValueSchema is { } inline) { ValidateDeclaration(inline.Type, SchemaKind.InlineValue); }
    }
}
