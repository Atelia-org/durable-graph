using Atelia.DurableGraph.Schema;
namespace Atelia.DurableGraph.Persistence;

/// <summary>
/// Locates a Schema by its closed nominal type and definition version within an exact Schema view.
/// Exactness still requires equality of the complete definition.
/// </summary>
public readonly record struct SchemaKey {
    public SchemaKey(string schemaId, int version) : this(TypeExpr.Named(schemaId), version) {
    }

    public SchemaKey(TypeExpr type, int version) {
        ArgumentNullException.ThrowIfNull(type);
        if (type.Kind != TypeExprKind.Named || !type.IsClosed) {
            throw new ArgumentException("Schema keys require a closed named type.", nameof(type));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        Type = type;
        Version = version;
    }

    public TypeExpr Type { get; }
    public string SchemaId => Type?.DefinitionId!;
    public int Version { get; }

    internal void Validate() {
        ArgumentNullException.ThrowIfNull(Type);
        if (Type.Kind != TypeExprKind.Named || !Type.IsClosed) {
            throw new ArgumentException("Schema keys require a closed named type.");
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Version);
    }
}
