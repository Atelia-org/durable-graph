using Atelia.Binary;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Runtime;

// Persistent key identity uses complete state bytes. Standard modes have framework
// lookup semantics; DB-055 modes use current domain behavior only when restoring a map.
internal static class DictionaryKeyPolicy {
    internal static void RequireCurrentKey(Type domainType, DurableFieldInfo slot) {
        if (slot.TypeTag == TypeTag.Nullable || Nullable.GetUnderlyingType(domainType) is not null) {
            throw new ArgumentException("Root Nullable Dictionary keys are not supported.");
        }
        if (domainType.IsEnum) {
            if (slot.TypeTag != TypeTag.InlineValue || !TryScalarTag(slot, out TypeTag tag) ||
                tag != EnumUnderlyingTag(domainType)) {
                throw new ArgumentException("An enum Dictionary key requires its exact single-integer representation.");
            }
            return;
        }
        if (!domainType.IsValueType && slot.TypeTag is TypeTag.String or TypeTag.ObjectReference) { return; }
        if (BuiltinStateValues.TryBindCurrent(domainType, out StateValueBinding builtin) &&
            builtin.Slot.TypeTag != TypeTag.String && slot == DurableFieldInfo.WithFieldId(builtin.Slot, slot.FieldId)) { return; }
        if (domainType.IsValueType && slot.TypeTag == TypeTag.InlineValue && !BuiltinStateValues.TryBindCurrent(domainType, out _)) { return; }
        throw new ArgumentException("Dictionary keys require a supported scalar, inline value, or reference representation.");
    }

    internal static DictionaryComparerKind Identify<TKey>(IEqualityComparer<TKey> comparer, DurableFieldInfo slot) where TKey : notnull {
        RequireCurrentKey(typeof(TKey), slot);
        if (comparer is DictionaryRestoreComparer<TKey> restored) {
            RequireStoredPolicy(restored.Kind, slot);
            return restored.Kind;
        }
        if (!typeof(TKey).IsValueType && ReferenceEquals(comparer, ReferenceEqualityComparer.Instance)) {
            return DictionaryComparerKind.ReferenceIdentity;
        }
        if (typeof(TKey) == typeof(string)) {
            if (ReferenceEquals(comparer, EqualityComparer<TKey>.Default) || ReferenceEquals(comparer, StringComparer.Ordinal)) {
                return DictionaryComparerKind.StringOrdinal;
            }
            if (ReferenceEquals(comparer, StringComparer.OrdinalIgnoreCase)) { return DictionaryComparerKind.StringOrdinalIgnoreCase; }
        }
        if (ReferenceEquals(comparer, EqualityComparer<TKey>.Default)) {
            return IsCurrentStandardScalar(typeof(TKey), slot) ? DictionaryComparerKind.ScalarDefault : DictionaryComparerKind.CurrentDefault;
        }
        return DictionaryComparerKind.Application;
    }

    internal static IEqualityComparer<TKey> CreateComparer<TKey>(DictionaryComparerKind kind, DurableFieldInfo slot,
        IEqualityComparer<TKey>? applicationComparer = null) where TKey : notnull {
        RequireCurrentKey(typeof(TKey), slot);
        RequireStoredPolicy(kind, slot);
        if ((kind == DictionaryComparerKind.ScalarDefault && !IsCurrentStandardScalar(typeof(TKey), slot)) ||
            (kind is DictionaryComparerKind.StringOrdinal or DictionaryComparerKind.StringOrdinalIgnoreCase && typeof(TKey) != typeof(string)) ||
            (kind == DictionaryComparerKind.ReferenceIdentity && typeof(TKey).IsValueType)) {
            throw new InvalidDataException("The current domain key cannot restore the recorded standard Dictionary comparison policy.");
        }
        return kind switch {
            DictionaryComparerKind.ScalarDefault => EqualityComparer<TKey>.Default,
            DictionaryComparerKind.StringOrdinal => (IEqualityComparer<TKey>)(object)StringComparer.Ordinal,
            DictionaryComparerKind.StringOrdinalIgnoreCase => (IEqualityComparer<TKey>)(object)StringComparer.OrdinalIgnoreCase,
            DictionaryComparerKind.ReferenceIdentity => (IEqualityComparer<TKey>)(object)ReferenceEqualityComparer.Instance,
            DictionaryComparerKind.CurrentDefault => new DictionaryRestoreComparer<TKey>(kind, EqualityComparer<TKey>.Default),
            DictionaryComparerKind.Application => new DictionaryRestoreComparer<TKey>(kind, applicationComparer ??
                throw new InvalidDataException("Application Dictionary comparison requires a current registered comparer.")),
            _ => throw new InvalidDataException("Unknown Dictionary comparison policy."),
        };
    }

    internal static void RequireStoredPolicy(DictionaryComparerKind kind, DurableFieldInfo slot) {
        bool valid = kind switch {
            DictionaryComparerKind.ScalarDefault => TryScalarTag(slot, out _),
            DictionaryComparerKind.StringOrdinal or DictionaryComparerKind.StringOrdinalIgnoreCase => slot.TypeTag == TypeTag.String,
            DictionaryComparerKind.ReferenceIdentity => slot.TypeTag is TypeTag.String or TypeTag.ObjectReference,
            DictionaryComparerKind.CurrentDefault or DictionaryComparerKind.Application =>
                slot.TypeTag is TypeTag.InlineValue or TypeTag.String or TypeTag.ObjectReference || TryScalarTag(slot, out _),
            _ => false,
        };
        if (!valid) { throw new InvalidDataException("Dictionary comparison policy is incompatible with its exact key slot."); }
    }

    internal static void RequireKeyCount(int count, DurableFieldInfo slot) {
        // Minimum zero is possible only for recursively empty inline layouts: there
        // is exactly one persistent key. Reject before allocating count-sized buffers.
        if (count > 1 && StateBodySize.MinimumBaseBytes(slot) == 0) {
            throw new InvalidDataException("A Dictionary with a zero-width key representation can contain at most one entry.");
        }
    }

    private static bool IsCurrentStandardScalar(Type domainType, DurableFieldInfo slot) =>
        domainType.IsEnum || (BuiltinStateValues.TryBindCurrent(domainType, out StateValueBinding builtin) &&
            builtin.Slot.TypeTag != TypeTag.String && slot == DurableFieldInfo.WithFieldId(builtin.Slot, slot.FieldId));

    internal static bool TryScalarTag(DurableFieldInfo slot, out TypeTag tag) {
        tag = slot.TypeTag;
        if (TypeTagFacts.IsBuiltin(tag) && tag != TypeTag.String) { return true; }
        if (tag != TypeTag.InlineValue) { return false; }
        DurableSchema schema = slot.InlineSchema!;
        // This validates a representation, not historical CLR enum provenance. An
        // equivalent single-integer struct has the same persisted schema contract.
        if (schema.Kind != SchemaKind.InlineValue || schema.BaseSchema is not null ||
            schema.Type.Arguments.Length != 0 || schema.Fields.Length != 1 || schema.Fields[0].FieldId != 1) { return false; }
        tag = schema.Fields[0].TypeTag;
        return tag is TypeTag.Byte or TypeTag.SByte or TypeTag.Int16 or TypeTag.UInt16 or
            TypeTag.Int32 or TypeTag.UInt32 or TypeTag.Int64 or TypeTag.UInt64;
    }

    internal static object ReadScalar(ReadOnlySpan<byte> encoded, DurableFieldInfo slot) {
        if (!TryScalarTag(slot, out TypeTag tag)) { throw new InvalidDataException("Unsupported scalar key representation."); }
        BareValueReader reader = new(encoded);
        object result = tag switch {
            TypeTag.Boolean => reader.ReadBoolean(), TypeTag.Byte => reader.ReadByte(), TypeTag.SByte => reader.ReadSByte(),
            TypeTag.Int16 => reader.ReadVarInt16(), TypeTag.UInt16 => reader.ReadVarUInt16(), TypeTag.Int32 => reader.ReadVarInt32(),
            TypeTag.UInt32 => reader.ReadVarUInt32(), TypeTag.Int64 => reader.ReadVarInt64(), TypeTag.UInt64 => reader.ReadVarUInt64(),
            TypeTag.Char => reader.ReadCharLE(), TypeTag.Half => reader.ReadHalfLE(), TypeTag.Single => reader.ReadSingleLE(),
            TypeTag.Double => reader.ReadDoubleLE(), TypeTag.Guid => BclValueCodec.ReadGuid(ref reader),
            TypeTag.Decimal => BclValueCodec.ReadDecimal(ref reader), TypeTag.TimeSpan => BclValueCodec.ReadTimeSpan(ref reader),
            TypeTag.DateOnly => BclValueCodec.ReadDateOnly(ref reader), TypeTag.TimeOnly => BclValueCodec.ReadTimeOnly(ref reader),
            TypeTag.DateTimeOffset => BclValueCodec.ReadDateTimeOffset(ref reader),
            _ => throw new InvalidDataException("Unsupported scalar key."),
        };
        reader.EnsureFullyConsumed();
        return result;
    }

    internal static ObjectId ReadReference(ReadOnlySpan<byte> encoded) {
        BareValueReader reader = new(encoded);
        ObjectId id = new(reader.ReadVarUInt32());
        reader.EnsureFullyConsumed();
        if (id.IsNull) { throw new InvalidDataException("Dictionary keys cannot be null."); }
        return id;
    }

    private static TypeTag EnumUnderlyingTag(Type type) => Type.GetTypeCode(Enum.GetUnderlyingType(type)) switch {
        TypeCode.Byte => TypeTag.Byte, TypeCode.SByte => TypeTag.SByte, TypeCode.Int16 => TypeTag.Int16,
        TypeCode.UInt16 => TypeTag.UInt16, TypeCode.Int32 => TypeTag.Int32, TypeCode.UInt32 => TypeTag.UInt32,
        TypeCode.Int64 => TypeTag.Int64, TypeCode.UInt64 => TypeTag.UInt64,
        _ => throw new ArgumentException("Unsupported enum underlying type."),
    };
}
