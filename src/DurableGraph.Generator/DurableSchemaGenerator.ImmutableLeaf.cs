using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace Atelia.DurableGraph.Generator;

public sealed partial class DurableSchemaGenerator {
    // DB-072: conservative classification of non-generic reference-object models whose whole
    // persistent field closure is readonly immutable value leaves. Fail-closed everywhere the
    // symbol evidence is insufficient, including cross-assembly members and unmodeled bases.
    private static bool IsImmutableLeafModel(
        DurableTypeModel type, List<DurableTypeModel> types, INamedTypeSymbol? halfType) {
        if (type.Symbol.IsGenericType || type.IsInline || type.IsEnum) return false;
        HashSet<INamedTypeSymbol> visited = new(SymbolEqualityComparer.Default);
        return CheckReferenceTypeLeaf(type, types, halfType, visited);
    }

    private static bool CheckReferenceTypeLeaf(
        DurableTypeModel type, List<DurableTypeModel> types, INamedTypeSymbol? halfType,
        HashSet<INamedTypeSymbol> visited) {
        if (!visited.Add(type.Symbol)) return true; // Defensive: class chains cannot cycle.
        if (!AuditInstanceFields(type.Symbol)) return false;
        foreach (DurableFieldModel field in type.Fields) {
            if (!IsAllowedLeafShape(field, types, halfType, visited)) return false;
        }
        INamedTypeSymbol? baseSymbol = type.Symbol.BaseType;
        if (baseSymbol is null || baseSymbol.SpecialType == SpecialType.System_Object) return true;
        int index = types.FindIndex(candidate =>
            SymbolEqualityComparer.Default.Equals(candidate.Symbol, baseSymbol.OriginalDefinition));
        if (index < 0) return false; // Cross-assembly or unmodeled base: fail closed.
        return CheckReferenceTypeLeaf(types[index], types, halfType, visited);
    }

    private static bool CheckInlineStructLeaf(
        DurableTypeModel structModel, List<DurableTypeModel> types, INamedTypeSymbol? halfType,
        HashSet<INamedTypeSymbol> visited) {
        if (!visited.Add(structModel.Symbol)) return true; // CS0523 forbids value-type cycles.
        if (!AuditInstanceFields(structModel.Symbol)) return false;
        foreach (DurableFieldModel field in structModel.Fields) {
            if (!IsAllowedLeafShape(field, types, halfType, visited)) return false;
        }
        return true;
    }

    private static bool AuditInstanceFields(INamedTypeSymbol symbol) {
        foreach (ISymbol member in symbol.GetMembers()) {
            if (member is IEventSymbol { IsStatic: false } eventSymbol &&
                eventSymbol.AddMethod is { IsImplicitlyDeclared: true }) {
                // A field-like event synthesizes a mutable backing field that never appears in
                // GetMembers(); the event symbol is the only compile-time evidence of it.
                return false;
            }
            if (member is not IFieldSymbol field || field.IsStatic) continue;
            // Auto-property and field-like event backing fields escape the durable field
            // pipeline for non-record types and remain runtime-mutable state.
            if (field.IsImplicitlyDeclared && !symbol.IsRecord) return false;
            if (HasAttribute(field.GetAttributes(), TransientAttributeMetadataName)) return false;
            if (!HasAttribute(field.GetAttributes(), DurableFieldAttributeMetadataName)) return false;
            if (!field.IsReadOnly) return false;
        }
        return true;
    }

    private static bool IsAllowedLeafShape(
        DurableFieldModel field, List<DurableTypeModel> types, INamedTypeSymbol? halfType,
        HashSet<INamedTypeSymbol> visited) {
        if (IsAllowedScalarTag(field.TypeTagValue)) return true;
        switch (field.TypeTagValue) {
            case 16:
                return TryFindInlineModel(field.Symbol.Type, types, out DurableTypeModel inline) &&
                    (inline.IsEnum || CheckInlineStructLeaf(inline, types, halfType, visited));
            case 18:
                ITypeSymbol child = ((INamedTypeSymbol)field.Symbol.Type).TypeArguments[0];
                if (field.InlineSchema.HasValue) {
                    return TryFindInlineModel(child, types, out DurableTypeModel nested) &&
                        (nested.IsEnum || CheckInlineStructLeaf(nested, types, halfType, visited));
                }
                // Nullable<builtin>: reuse the builtin tag semantics; tag 4 (string) is not a leaf.
                return TryGetTypeTag(child, halfType, out _, out int childTag, out _) && IsAllowedScalarTag(childTag);
            default:
                return false; // 4 string, 15 references (object/array/List/Dictionary), 17 parameter.
        }
    }

    private static bool IsAllowedScalarTag(int tag) =>
        (tag >= 1 && tag <= 3) || (tag >= 5 && tag <= 14) || (tag >= 19 && tag <= 24);

    private static bool TryFindInlineModel(
        ITypeSymbol target, List<DurableTypeModel> types, out DurableTypeModel model) {
        if (target is INamedTypeSymbol named) {
            int index = types.FindIndex(candidate =>
                SymbolEqualityComparer.Default.Equals(candidate.Symbol, named.OriginalDefinition));
            if (index >= 0) {
                model = types[index];
                return true;
            }
        }
        model = default;
        return false;
    }
}
