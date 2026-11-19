namespace Atelia.DurableGraph.Schema;

/// <summary>The exact immutable representation of one reference object, independent of its CLR instance.</summary>
public sealed class ObjectLayout : IEquatable<ObjectLayout> {
    private ObjectLayout(ObjectStateKind kind, DurableSchema? schema, ArrayLayout? array, ListLayout? list = null,
        DictionaryLayout? dictionary = null) {
        Kind = kind;
        Schema = schema;
        Array = array;
        List = list;
        Dictionary = dictionary;
    }

    public static ObjectLayout String { get; } = new(ObjectStateKind.String, null, null);
    public static ObjectLayout ForDurable(DurableSchema schema) {
        ArgumentNullException.ThrowIfNull(schema);
        schema.RequireReferenceObject();
        return new(ObjectStateKind.Durable, schema, null);
    }
    public static ObjectLayout ForArray(ArrayLayout array) {
        ArgumentNullException.ThrowIfNull(array);
        return new(ObjectStateKind.Array, null, array);
    }

    public ObjectStateKind Kind { get; }
    public static ObjectLayout ForList(ListLayout list) {
        ArgumentNullException.ThrowIfNull(list);
        return new(ObjectStateKind.List, null, null, list);
    }
    public static ObjectLayout ForDictionary(DictionaryLayout dictionary) {
        ArgumentNullException.ThrowIfNull(dictionary);
        return new(ObjectStateKind.Dictionary, null, null, dictionary: dictionary);
    }
    public DurableSchema? Schema { get; }
    public ArrayLayout? Array { get; }
    public ListLayout? List { get; }
    public DictionaryLayout? Dictionary { get; }
    public TypeExpr Type => Kind switch {
        ObjectStateKind.String => TypeExpr.Builtin(TypeTag.String),
        ObjectStateKind.Durable => Schema!.Type,
        ObjectStateKind.Array => Array!.Type,
        ObjectStateKind.List => List!.Type,
        ObjectStateKind.Dictionary => Dictionary!.Type,
        _ => throw new InvalidOperationException("Unknown object layout."),
    };
    public bool Equals(ObjectLayout? other) => other is not null && Kind == other.Kind &&
        Equals(Schema, other.Schema) && Equals(Array, other.Array) && Equals(List, other.List) && Equals(Dictionary, other.Dictionary);
    public override bool Equals(object? obj) => obj is ObjectLayout other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Kind, Schema, Array, List, Dictionary);
}

/// <summary>Exact array element representation. Shape belongs to the individual frozen state.</summary>
public sealed class ArrayLayout : IEquatable<ArrayLayout> {
    public ArrayLayout(TypeExprKind constructor, DurableFieldInfo elementSlot, uint codecVersion = 1) {
        if (constructor is < TypeExprKind.VectorArray or > TypeExprKind.Rank4Array) {
            throw new ArgumentOutOfRangeException(nameof(constructor));
        }
        if (codecVersion != 1) { throw new ArgumentOutOfRangeException(nameof(codecVersion), "Unsupported array codec version."); }
        if (elementSlot.FieldId <= 0) { throw new ArgumentException("An array requires a complete element slot.", nameof(elementSlot)); }
        Constructor = constructor;
        CodecVersion = codecVersion;
        ElementSlot = DurableFieldInfo.WithFieldId(elementSlot, 1);
        TypeExpr element = DurableFieldInfo.NominalType(ElementSlot);
        Type = IsVector ? TypeExpr.VectorArray(element) : TypeExpr.MultiDimArray(element, Rank);
    }

    public TypeExprKind Constructor { get; }
    public uint CodecVersion { get; }
    public DurableFieldInfo ElementSlot { get; }
    public bool IsVector => Constructor == TypeExprKind.VectorArray;
    public int Rank => IsVector ? 1 : (int)Constructor - 3;
    public TypeExpr Type { get; }
    public bool Equals(ArrayLayout? other) => other is not null && Constructor == other.Constructor &&
        CodecVersion == other.CodecVersion && ElementSlot.Equals(other.ElementSlot);
    public override bool Equals(object? obj) => obj is ArrayLayout other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Constructor, CodecVersion, ElementSlot);
}
