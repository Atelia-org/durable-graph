using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Atelia.DurableGraph.Generator;

public sealed partial class DurableSchemaGenerator {
    // DB-075: build an audited candidate closure for non-generic reference-object models.
    // The candidate is emission material only; the generated binding performs the final
    // structure verification against the exact compiled type (DB-075 §5). Both the binary
    // and family generation paths share this single rule set.
    private static ImmutableLeafCandidate? TryBuildImmutableLeafCandidate(
        DurableTypeModel type, List<DurableTypeModel> types, INamedTypeSymbol? halfType) {
        if (type.Symbol.IsGenericType || type.IsInline || type.IsEnum) return null;
        List<ImmutableLeafDeclaration> declarations = new();
        HashSet<INamedTypeSymbol> visited = new(SymbolEqualityComparer.Default);
        return TryCollectReferenceTypeCandidate(type, types, halfType, visited, declarations)
            ? new ImmutableLeafCandidate(declarations) : null;
    }

    private static bool TryCollectReferenceTypeCandidate(
        DurableTypeModel type, List<DurableTypeModel> types, INamedTypeSymbol? halfType,
        HashSet<INamedTypeSymbol> visited, List<ImmutableLeafDeclaration> declarations) {
        if (!visited.Add(type.Symbol)) return true; // Defensive: class chains cannot cycle.
        if (!TryCollectDeclaration(type, types, halfType, visited, declarations, isStruct: false)) return false;
        INamedTypeSymbol? baseSymbol = type.Symbol.BaseType;
        if (baseSymbol is null || baseSymbol.SpecialType == SpecialType.System_Object) return true;
        if (baseSymbol.IsGenericType) return false; // Closed generic bases stay outside this slice.
        int index = types.FindIndex(candidate =>
            SymbolEqualityComparer.Default.Equals(candidate.Symbol, baseSymbol.OriginalDefinition));
        if (index < 0) return false; // Cross-assembly or unmodeled base: fail closed.
        return TryCollectReferenceTypeCandidate(types[index], types, halfType, visited, declarations);
    }

    private static bool TryCollectInlineStructCandidate(
        DurableTypeModel structModel, List<DurableTypeModel> types, INamedTypeSymbol? halfType,
        HashSet<INamedTypeSymbol> visited, List<ImmutableLeafDeclaration> declarations) {
        if (!visited.Add(structModel.Symbol)) return true; // CS0523 forbids value-type cycles.
        return TryCollectDeclaration(structModel, types, halfType, visited, declarations, isStruct: true);
    }

    private static bool TryCollectDeclaration(
        DurableTypeModel type, List<DurableTypeModel> types, INamedTypeSymbol? halfType,
        HashSet<INamedTypeSymbol> visited, List<ImmutableLeafDeclaration> declarations, bool isStruct) {
        // Primary-constructor parameters are not excluded syntactically: a parameter becomes
        // instance state only when the compiler materializes a capture field, which is
        // implicitly declared and rejected by AuditInstanceFields (and by the runtime field
        // set check if the symbol view ever differs). A parameter consumed only by an
        // explicit readonly field initializer leaves no instance state behind.
        if (!AuditInstanceFields(type.Symbol)) return false;
        List<ImmutableLeafField> fields = new();
        foreach (DurableFieldModel field in type.Fields) {
            if (!TryBuildImmutableLeafFieldType(field, types, halfType, visited, declarations, out ImmutableLeafFieldType? fieldType)) return false;
            fields.Add(new ImmutableLeafField(field.Symbol.MetadataName, fieldType!));
        }
        string baseTypeExpression = string.Empty;
        if (!isStruct) {
            INamedTypeSymbol? baseSymbol = type.Symbol.BaseType;
            baseTypeExpression = baseSymbol is null || baseSymbol.SpecialType == SpecialType.System_Object
                ? "typeof(object)"
                : "typeof(" + baseSymbol.ToDisplayString(FullyQualifiedNameFormat) + ")";
        }
        declarations.Add(new ImmutableLeafDeclaration(type.Symbol, isStruct, baseTypeExpression, fields));
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
            // DB-075: every implicit backing (auto-property, positional/get-only/init record
            // storage) is excluded from the sharing optimization, records included.
            if (field.IsImplicitlyDeclared) return false;
            if (HasAttribute(field.GetAttributes(), TransientAttributeMetadataName)) return false;
            if (!HasAttribute(field.GetAttributes(), DurableFieldAttributeMetadataName)) return false;
            if (!field.IsReadOnly) return false;
        }
        return true;
    }

    private static bool TryBuildImmutableLeafFieldType(
        DurableFieldModel field, List<DurableTypeModel> types, INamedTypeSymbol? halfType,
        HashSet<INamedTypeSymbol> visited, List<ImmutableLeafDeclaration> declarations,
        out ImmutableLeafFieldType? fieldType) {
        fieldType = null;
        if (IsAllowedScalarTag(field.TypeTagValue)) {
            fieldType = new ImmutableLeafBuiltinType(field.TypeTagValue);
            return true;
        }
        if (field.TypeTagValue == 16) {
            return TryBuildInlineFieldType(field.Symbol.Type, types, halfType, visited, declarations, out fieldType);
        }
        if (field.TypeTagValue == 18) {
            if (field.Symbol.Type is not INamedTypeSymbol named || named.TypeArguments.Length != 1) return false;
            ITypeSymbol child = named.TypeArguments[0];
            if (field.InlineSchema.HasValue) {
                if (!TryBuildInlineFieldType(child, types, halfType, visited, declarations, out ImmutableLeafFieldType? childType)) return false;
                fieldType = new ImmutableLeafNullableType(childType!);
                return true;
            }
            // Nullable<builtin>: reuse the builtin tag semantics; tag 4 (string) is not a leaf.
            if (!TryGetTypeTag(child, halfType, out _, out int childTag, out _) || !IsAllowedScalarTag(childTag)) return false;
            fieldType = new ImmutableLeafNullableType(new ImmutableLeafBuiltinType(childTag));
            return true;
        }
        return false; // 4 string, 15 references (object/array/List/Dictionary), 17 parameters.
    }

    private static bool TryBuildInlineFieldType(
        ITypeSymbol target, List<DurableTypeModel> types, INamedTypeSymbol? halfType,
        HashSet<INamedTypeSymbol> visited, List<ImmutableLeafDeclaration> declarations,
        out ImmutableLeafFieldType? fieldType) {
        fieldType = null;
        if (target is not INamedTypeSymbol named || named.IsGenericType) return false;
        int index = types.FindIndex(candidate =>
            SymbolEqualityComparer.Default.Equals(candidate.Symbol, named.OriginalDefinition));
        if (index < 0) return false;
        DurableTypeModel model = types[index];
        if (model.IsEnum) {
            // Enums cannot be extended by another partial declaration; the field type identity
            // check is their whole verification.
            fieldType = new ImmutableLeafSameCompilationType(model.Symbol);
            return true;
        }
        if (!TryCollectInlineStructCandidate(model, types, halfType, visited, declarations)) return false;
        fieldType = new ImmutableLeafSameCompilationType(model.Symbol);
        return true;
    }

    private static bool IsAllowedScalarTag(int tag) =>
        (tag >= 1 && tag <= 3) || (tag >= 5 && tag <= 14) || (tag >= 19 && tag <= 24);

    private sealed class ImmutableLeafCandidate {
        public ImmutableLeafCandidate(List<ImmutableLeafDeclaration> declarations) {
            Declarations = declarations;
        }

        public List<ImmutableLeafDeclaration> Declarations { get; }
    }

    private sealed class ImmutableLeafDeclaration {
        public ImmutableLeafDeclaration(INamedTypeSymbol symbol, bool isStruct, string baseTypeExpression, List<ImmutableLeafField> fields) {
            Symbol = symbol;
            IsStruct = isStruct;
            BaseTypeExpression = baseTypeExpression;
            Fields = fields;
        }

        public INamedTypeSymbol Symbol { get; }
        public bool IsStruct { get; }
        public string BaseTypeExpression { get; }
        public List<ImmutableLeafField> Fields { get; }
    }

    private sealed class ImmutableLeafField {
        public ImmutableLeafField(string metadataName, ImmutableLeafFieldType fieldType) {
            MetadataName = metadataName;
            Type = fieldType;
        }

        public string MetadataName { get; }
        public ImmutableLeafFieldType Type { get; }
    }

    private abstract class ImmutableLeafFieldType;

    private sealed class ImmutableLeafBuiltinType(int tag) : ImmutableLeafFieldType {
        public int Tag { get; } = tag;
    }

    private sealed class ImmutableLeafSameCompilationType(INamedTypeSymbol symbol) : ImmutableLeafFieldType {
        public INamedTypeSymbol Symbol { get; } = symbol;
    }

    private sealed class ImmutableLeafNullableType(ImmutableLeafFieldType child) : ImmutableLeafFieldType {
        public ImmutableLeafFieldType Child { get; } = child;
    }

    // Emits the final structure verification shared by both generation paths. The aggregate
    // checks every audited declaration layer: exact base type, the complete declared instance
    // field set, and per-field identity, trusted type and init-only state. A mismatch returns
    // false; it does not raise and does not repair anything (DB-075 §5.1/§5.3).
    private static void AppendImmutableLeafShapeCheck(StringBuilder source, ImmutableLeafCandidate candidate, string indent) {
        source.Append(indent).AppendLine("private static bool __DurableCheckImmutableLeafShape() {");
        string body = indent + "    ";
        source.Append(body).AppendLine("const global::System.Reflection.BindingFlags shapeFlags =");
        source.Append(body).AppendLine("    global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.Public |");
        source.Append(body).AppendLine("    global::System.Reflection.BindingFlags.NonPublic | global::System.Reflection.BindingFlags.DeclaredOnly;");
        int typeIndex = 0;
        foreach (ImmutableLeafDeclaration declaration in candidate.Declarations) {
            string typeVariable = "shapeType" + typeIndex.ToString(CultureInfo.InvariantCulture);
            typeIndex++;
            source.Append(body).Append("var ").Append(typeVariable).Append(" = typeof(")
                .Append(declaration.Symbol.ToDisplayString(FullyQualifiedNameFormat)).AppendLine(");");
            if (declaration.IsStruct) {
                source.Append(body).Append("if (!").Append(typeVariable).AppendLine(".IsValueType) return false;");
            } else {
                source.Append(body).Append("if (").Append(typeVariable).Append(".IsValueType || ").Append(typeVariable)
                    .Append(".BaseType != ").Append(declaration.BaseTypeExpression).AppendLine(") return false;");
            }
            source.Append(body).Append("if (").Append(typeVariable).Append(".GetFields(shapeFlags).Length != ")
                .Append(declaration.Fields.Count.ToString(CultureInfo.InvariantCulture)).AppendLine(") return false;");
            for (int fieldIndex = 0; fieldIndex < declaration.Fields.Count; fieldIndex++) {
                ImmutableLeafField field = declaration.Fields[fieldIndex];
                string fieldVariable = "shapeField" + typeIndex.ToString(CultureInfo.InvariantCulture) + "_" +
                    fieldIndex.ToString(CultureInfo.InvariantCulture);
                source.Append(body).Append("var ").Append(fieldVariable).Append(" = ").Append(typeVariable)
                    .Append(".GetField(").Append(Literal(field.MetadataName)).AppendLine(", shapeFlags);");
                source.Append(body).Append("if (").Append(fieldVariable).Append(" is null || !").Append(fieldVariable)
                    .AppendLine(".IsInitOnly) return false;");
                AppendImmutableLeafFieldTypeCheck(source, body, fieldVariable + ".FieldType", field.Type);
            }
        }
        source.Append(indent).AppendLine("    return true;");
        source.Append(indent).AppendLine("}");
    }

    private static void AppendImmutableLeafFieldTypeCheck(
        StringBuilder source, string indent, string fieldTypeExpression, ImmutableLeafFieldType type) {
        if (type is ImmutableLeafBuiltinType builtin) {
            source.Append(indent).Append("if (").Append(fieldTypeExpression).Append(" != ")
                .Append(TrustedBuiltinTypeExpression(builtin.Tag)).AppendLine(") return false;");
            return;
        }
        if (type is ImmutableLeafSameCompilationType sameCompilation) {
            source.Append(indent).Append("if (").Append(fieldTypeExpression).Append(" != typeof(")
                .Append(sameCompilation.Symbol.ToDisplayString(FullyQualifiedNameFormat)).AppendLine(")) return false;");
            return;
        }
        ImmutableLeafNullableType nullable = (ImmutableLeafNullableType)type;
        source.Append(indent).Append("if (!(").Append(fieldTypeExpression).AppendLine(".IsGenericType &&");
        source.Append(indent).Append("    ").Append(fieldTypeExpression).Append(".GetGenericTypeDefinition() == ")
            .Append(TrustedCorelibTypeExpression("System.Nullable`1")).AppendLine(" &&");
        source.Append(indent).Append("    ").Append(fieldTypeExpression).Append(".GetGenericArguments().Length == 1 &&");
        source.Append(indent).Append("    ").Append(fieldTypeExpression).Append(".GetGenericArguments()[0] == ")
            .Append(TrustedChildTypeExpression(nullable.Child)).AppendLine(")) return false;");
    }

    private static string TrustedChildTypeExpression(ImmutableLeafFieldType type) => type switch {
        ImmutableLeafBuiltinType builtin => TrustedBuiltinTypeExpression(builtin.Tag),
        ImmutableLeafSameCompilationType sameCompilation =>
            "typeof(" + sameCompilation.Symbol.ToDisplayString(FullyQualifiedNameFormat) + ")",
        _ => throw new InvalidOperationException("A Nullable child cannot itself be nullable.")
    };

    private static string TrustedBuiltinTypeExpression(int tag) => tag switch {
        1 => "typeof(bool)", 2 => "typeof(int)", 3 => "typeof(long)",
        5 => "typeof(byte)", 6 => "typeof(sbyte)", 7 => "typeof(short)", 8 => "typeof(ushort)",
        9 => "typeof(uint)", 10 => "typeof(ulong)", 11 => "typeof(char)",
        13 => "typeof(float)", 14 => "typeof(double)", 20 => "typeof(decimal)",
        12 => TrustedCorelibTypeExpression("System.Half"),
        19 => TrustedCorelibTypeExpression("System.Guid"),
        21 => TrustedCorelibTypeExpression("System.TimeSpan"),
        22 => TrustedCorelibTypeExpression("System.DateOnly"),
        23 => TrustedCorelibTypeExpression("System.TimeOnly"),
        24 => TrustedCorelibTypeExpression("System.DateTimeOffset"),
        _ => throw new InvalidOperationException(
            "Unsupported immutable leaf builtin tag " + tag.ToString(CultureInfo.InvariantCulture) + ".")
    };

    private static string TrustedCorelibTypeExpression(string metadataName) =>
        "typeof(object).Assembly.GetType(" + Literal(metadataName) + ", throwOnError: false)";
}
