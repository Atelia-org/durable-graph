using System.Collections.Immutable;

namespace Atelia.DurableGraph.Schema;

/// <summary>Identifies a supported nominal type, or a declaration-scoped type parameter.</summary>
/// <remarks>Versions belong to exact Schemas, not to nominal type expressions.</remarks>
public sealed class TypeExpr : IEquatable<TypeExpr>, IComparable<TypeExpr> {
    public const int MaximumDepth = 64;
    public const int MaximumNodeCount = 4096;
    public const int MaximumArity = 32;

    private readonly int _hashCode;
    private readonly int _depth;
    private readonly int _nodeCount;

    private TypeExpr(TypeExprKind kind, TypeTag builtinTag, string? definitionId,
        ImmutableArray<TypeExpr> arguments, int parameterOrdinal) {
        Kind = kind;
        BuiltinTag = builtinTag;
        DefinitionId = definitionId;
        Arguments = arguments;
        ParameterOrdinal = parameterOrdinal;
        IsClosed = kind != TypeExprKind.Parameter;
        _depth = 1;
        _nodeCount = 1;
        HashCode hash = new();
        hash.Add(kind);
        hash.Add(builtinTag);
        hash.Add(definitionId, StringComparer.Ordinal);
        hash.Add(parameterOrdinal);
        foreach (TypeExpr argument in arguments) {
            ArgumentNullException.ThrowIfNull(argument);
            _depth = Math.Max(_depth, argument._depth + 1);
            _nodeCount += argument._nodeCount;
            IsClosed &= argument.IsClosed;
            hash.Add(argument);
        }
        if (_depth > MaximumDepth || _nodeCount > MaximumNodeCount) {
            throw new ArgumentException("The type expression exceeds its depth or expanded node limit.", nameof(arguments));
        }
        _hashCode = hash.ToHashCode();
    }

    public TypeExprKind Kind { get; }
    public TypeTag BuiltinTag { get; }
    public string? DefinitionId { get; }
    public ImmutableArray<TypeExpr> Arguments { get; }
    public int ParameterOrdinal { get; }
    public bool IsClosed { get; }

    /// <summary>Gets whether this expression is a supported array constructor.</summary>
    public bool IsArray => Kind is >= TypeExprKind.VectorArray and <= TypeExprKind.Rank4Array;

    public bool IsList => Kind == TypeExprKind.List;

    public bool IsNullable => Kind == TypeExprKind.Nullable;

    public bool IsDictionary => Kind == TypeExprKind.Dictionary;

    internal bool IsObjectReferenceTarget => Kind == TypeExprKind.Named || IsArray || IsList || IsDictionary;

    /// <summary>The key operand of a built-in Dictionary expression.</summary>
    public TypeExpr? KeyType => IsDictionary ? Arguments[0] : null;

    /// <summary>The value operand of a built-in Dictionary expression.</summary>
    public TypeExpr? ValueType => IsDictionary ? Arguments[1] : null;

    /// <summary>Gets the array rank, or zero for a non-array expression.</summary>
    public int ArrayRank => IsArray ? (int)Kind - (int)TypeExprKind.VectorArray + 1 : 0;

    /// <summary>Gets the array, List or nullable element expression, or null for another expression.</summary>
    public TypeExpr? ElementType => IsArray || IsList || IsNullable ? Arguments[0] : null;

    /// <summary>Constructs a nullable value expression; named and parameter children are checked when bound.</summary>
    public static TypeExpr Nullable(TypeExpr element) {
        ArgumentNullException.ThrowIfNull(element);
        if (element.IsNullable || element.IsArray || element.IsList || element.IsDictionary ||
            element.Kind == TypeExprKind.Builtin && element.BuiltinTag == TypeTag.String) {
            throw new ArgumentException("A nullable child must be a non-nullable supported value type.", nameof(element));
        }
        return new(TypeExprKind.Nullable, TypeTag.Invalid, null, [element], -1);
    }

    /// <summary>Constructs the built-in BCL List type.</summary>
    public static TypeExpr List(TypeExpr element) {
        ArgumentNullException.ThrowIfNull(element);
        return new(TypeExprKind.List, TypeTag.Invalid, null, [element], -1);
    }

    /// <summary>Constructs the experimental built-in BCL Dictionary type.</summary>
    public static TypeExpr Dictionary(TypeExpr key, TypeExpr value) {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        return new(TypeExprKind.Dictionary, TypeTag.Invalid, null, [key, value], -1);
    }

    public static TypeExpr Builtin(TypeTag tag) {
        if (!TypeTagFacts.IsBuiltin(tag)) {
            throw new ArgumentOutOfRangeException(nameof(tag), "Only the supported scalar and string tags are built-in types.");
        }
        return new(TypeExprKind.Builtin, tag, null, ImmutableArray<TypeExpr>.Empty, -1);
    }

    public static TypeExpr Named(string definitionId, params TypeExpr[] arguments) {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionId);
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Length > MaximumArity) {
            throw new ArgumentOutOfRangeException(nameof(arguments), "A type definition exceeds the supported generic arity.");
        }
        return new(TypeExprKind.Named, TypeTag.Invalid, definitionId, ImmutableArray.CreateRange(arguments), -1);
    }

    public static TypeExpr Parameter(int ordinal) {
        if (ordinal is < 0 or >= MaximumArity) {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }
        return new(TypeExprKind.Parameter, TypeTag.Invalid, null, ImmutableArray<TypeExpr>.Empty, ordinal);
    }

    /// <summary>Constructs a zero-based, single-dimensional vector type.</summary>
    public static TypeExpr VectorArray(TypeExpr element) {
        ArgumentNullException.ThrowIfNull(element);
        return new(TypeExprKind.VectorArray, TypeTag.Invalid, null, [element], -1);
    }

    /// <summary>Constructs a zero-based rectangular array type of rank two through four.</summary>
    public static TypeExpr MultiDimArray(TypeExpr element, int rank) {
        ArgumentNullException.ThrowIfNull(element);
        if (rank is < 2 or > 4) { throw new ArgumentOutOfRangeException(nameof(rank)); }
        return new((TypeExprKind)((int)TypeExprKind.VectorArray + rank - 1), TypeTag.Invalid, null, [element], -1);
    }

    public bool Equals(TypeExpr? other) => CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is TypeExpr expression && Equals(expression);
    public override int GetHashCode() => _hashCode;

    /// <summary>Compares the canonical structure, using ordinal definition identifiers.</summary>
    public int CompareTo(TypeExpr? other) {
        if (ReferenceEquals(this, other)) { return 0; }
        if (other is null) { return 1; }
        int comparison = Kind.CompareTo(other.Kind);
        if (comparison != 0) { return comparison; }
        if (Kind == TypeExprKind.Builtin) { return BuiltinTag.CompareTo(other.BuiltinTag); }
        if (Kind == TypeExprKind.Parameter) { return ParameterOrdinal.CompareTo(other.ParameterOrdinal); }
        comparison = StringComparer.Ordinal.Compare(DefinitionId, other.DefinitionId);
        if (comparison != 0) { return comparison; }
        comparison = Arguments.Length.CompareTo(other.Arguments.Length);
        if (comparison != 0) { return comparison; }
        for (int index = 0; index < Arguments.Length; index++) {
            comparison = Arguments[index].CompareTo(other.Arguments[index]);
            if (comparison != 0) { return comparison; }
        }
        return 0;
    }

    public override string ToString() => Kind switch {
        TypeExprKind.Builtin => BuiltinTag.ToString(),
        TypeExprKind.Parameter => $"!{ParameterOrdinal}",
        TypeExprKind.Named => Arguments.IsEmpty ? DefinitionId! : $"{DefinitionId}<{string.Join(",", Arguments)}>",
        TypeExprKind.VectorArray => $"{ElementType}[]",
        TypeExprKind.Rank2Array => $"{ElementType}[,]",
        TypeExprKind.Rank3Array => $"{ElementType}[,,]",
        TypeExprKind.Rank4Array => $"{ElementType}[,,,]",
        TypeExprKind.List => $"List<{ElementType}>",
        TypeExprKind.Nullable => $"Nullable<{ElementType}>",
        TypeExprKind.Dictionary => $"Dictionary<{KeyType},{ValueType}>",
        _ => throw new InvalidOperationException("Unknown type expression constructor."),
    };

    public static bool operator ==(TypeExpr? left, TypeExpr? right) => Equals(left, right);
    public static bool operator !=(TypeExpr? left, TypeExpr? right) => !Equals(left, right);
}

public enum TypeExprKind : byte {
    Builtin = 1,
    Named = 2,
    Parameter = 3,
    VectorArray = 4,
    Rank2Array = 5,
    Rank3Array = 6,
    Rank4Array = 7,
    List = 8,
    Nullable = 9,
    Dictionary = 10,
}
