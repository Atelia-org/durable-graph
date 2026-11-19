using Atelia.DurableGraph.Schema;
using Atelia.Binary;

namespace Atelia.DurableGraph.Persistence;

/// <summary>Canonical closed nominal type expressions, without exact Schema versions.</summary>
internal static class TypeExprWireCodec {
    internal static void Write(ref BareValueWriter writer, TypeExpr type) {
        ArgumentNullException.ThrowIfNull(type);
        if (!type.IsClosed) { throw new ArgumentException("Persisted type expressions must be closed.", nameof(type)); }
        WriteNode(ref writer, type);
    }

    private static void WriteNode(ref BareValueWriter writer, TypeExpr type) {
        writer.WriteByte((byte)type.Kind);
        if (type.Kind == TypeExprKind.Builtin) {
            writer.WriteByte((byte)type.BuiltinTag);
            return;
        }
        if (type.IsArray || type.IsList || type.IsNullable) {
            WriteNode(ref writer, type.ElementType!);
            return;
        }
        if (type.IsDictionary) {
            WriteNode(ref writer, type.KeyType!);
            WriteNode(ref writer, type.ValueType!);
            return;
        }
        writer.WriteString(type.DefinitionId!);
        writer.WriteVarUInt32((uint)type.Arguments.Length);
        foreach (TypeExpr argument in type.Arguments) { WriteNode(ref writer, argument); }
    }

    internal static TypeExpr Read(ref BareValueReader reader) {
        int remainingNodes = TypeExpr.MaximumNodeCount;
        return ReadNode(ref reader, 1, ref remainingNodes);
    }

    private static TypeExpr ReadNode(ref BareValueReader reader, int depth, ref int remainingNodes) {
        if (depth > TypeExpr.MaximumDepth || --remainingNodes < 0) {
            throw new InvalidDataException("The type expression exceeds its depth or node limit.");
        }
        byte tag = reader.ReadByte();
        if (tag == 1) {
            byte builtin = reader.ReadByte();
            if (!TypeTagFacts.IsBuiltin((TypeTag)builtin)) { throw new InvalidDataException("Unknown built-in type expression."); }
            return TypeExpr.Builtin((TypeTag)builtin);
        }
        if (tag is >= 4 and <= 7) {
            TypeExpr element = ReadNode(ref reader, depth + 1, ref remainingNodes);
            return tag == 4 ? TypeExpr.VectorArray(element) : TypeExpr.MultiDimArray(element, tag - 3);
        }
        if (tag == 8) { return TypeExpr.List(ReadNode(ref reader, depth + 1, ref remainingNodes)); }
        if (tag == 9) {
            TypeExpr child = ReadNode(ref reader, depth + 1, ref remainingNodes);
            try { return TypeExpr.Nullable(child); }
            catch (ArgumentException error) { throw new InvalidDataException("Invalid Nullable type operand.", error); }
        }
        if (tag == 10) {
            TypeExpr key = ReadNode(ref reader, depth + 1, ref remainingNodes);
            TypeExpr value = ReadNode(ref reader, depth + 1, ref remainingNodes);
            return TypeExpr.Dictionary(key, value);
        }
        if (tag != 2) {
            throw new InvalidDataException("Unknown or open type expression in a closed persisted type.");
        }
        string definitionId = reader.ReadString();
        if (string.IsNullOrWhiteSpace(definitionId)) { throw new InvalidDataException("A named type requires a nonblank definition identity."); }
        uint arity = reader.ReadVarUInt32();
        if (arity > TypeExpr.MaximumArity || arity > (uint)remainingNodes || arity > (uint)reader.RemainingCount / 2) {
            throw new InvalidDataException("Invalid type expression arity.");
        }
        var arguments = new TypeExpr[(int)arity];
        for (int index = 0; index < arguments.Length; index++) {
            arguments[index] = ReadNode(ref reader, depth + 1, ref remainingNodes);
        }
        return TypeExpr.Named(definitionId, arguments);
    }
}
