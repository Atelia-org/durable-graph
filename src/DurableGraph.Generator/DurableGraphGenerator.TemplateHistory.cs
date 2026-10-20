using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Atelia.DurableGraph.SchemaHistory;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Atelia.DurableGraph.Generator;

public sealed partial class DurableGraphGenerator {
    private static INamedTypeSymbol? GetBclListType(Compilation compilation) {
        // Resolve a metadata symbol from this compilation. A source-defined lookalike is not a builtin.
        INamedTypeSymbol? symbol = compilation.GetTypeByMetadataName("System.Collections.Generic.List`1");
        return symbol is not null && symbol.DeclaringSyntaxReferences.Length == 0 ? symbol : null;
    }

    private static bool IsBclList(ITypeSymbol type, INamedTypeSymbol? listType) =>
        listType is not null && type is INamedTypeSymbol named && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, listType);

    private static INamedTypeSymbol? GetBclDictionaryType(Compilation compilation) {
        INamedTypeSymbol? symbol = compilation.GetTypeByMetadataName("System.Collections.Generic.Dictionary`2");
        return symbol is not null && symbol.DeclaringSyntaxReferences.Length == 0 ? symbol : null;
    }

    private static bool IsBclDictionary(ITypeSymbol type, INamedTypeSymbol? dictionaryType) =>
        dictionaryType is not null && type is INamedTypeSymbol named && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, dictionaryType);

    private static bool IsNullableValue(ITypeSymbol type) =>
        type is INamedTypeSymbol named && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;

    private static bool TryGetNullableField(ITypeSymbol type, INamedTypeSymbol owner,
        INamedTypeSymbol? halfType, INamedTypeSymbol? listType, INamedTypeSymbol? dictionaryType, Compilation compilation, System.Threading.CancellationToken cancellationToken,
        out string? tag, out int number, out string? name, out SchemaReference? inline) {
        tag = null; number = 0; name = null; inline = null;
        if (!IsNullableValue(type)) return false;
        ITypeSymbol child = ((INamedTypeSymbol)type).TypeArguments[0];
        if (!TryGetTypePattern(type, owner, halfType, listType, dictionaryType, compilation, out _)) return false;
        if (child is INamedTypeSymbol named && GetAttribute(named.GetAttributes(), DurableTypeAttributeMetadataName) is not null &&
            !TryGetInlineValue(child, owner, compilation, cancellationToken, out _, out _, out _, out inline)) return false;
        tag = "Nullable"; number = 18; name = type.ToDisplayString(FullyQualifiedNameFormat);
        return true;
    }

    private static bool TryGetListField(ITypeSymbol type, INamedTypeSymbol? listType, out string? tag, out int number, out string? name) {
        tag = null; number = 0; name = null;
        if (!IsBclList(type, listType)) return false;
        tag = "ObjectReference"; number = 15; name = type.ToDisplayString(FullyQualifiedNameFormat);
        return true;
    }

    private static bool TryGetDictionaryField(ITypeSymbol type, INamedTypeSymbol? dictionaryType, out string? tag, out int number, out string? name) {
        tag = null; number = 0; name = null;
        if (!IsBclDictionary(type, dictionaryType)) return false;
        tag = "ObjectReference"; number = 15; name = type.ToDisplayString(FullyQualifiedNameFormat);
        return true;
    }

    private static bool TryGetArrayField(ITypeSymbol type, out string? tag, out int number, out string? name) {
        tag = null; number = 0; name = null;
        if (type is not IArrayTypeSymbol array || array.Rank > 4 || (array.Rank == 1 && !array.IsSZArray)) return false;
        tag = "ObjectReference"; number = 15; name = type.ToDisplayString(FullyQualifiedNameFormat);
        return true;
    }

    private static bool TryGetParameterField(ITypeSymbol type, out string? tag, out int number, out string? name) {
        tag = null; number = 0; name = null;
        if (type is not ITypeParameterSymbol parameter || parameter.Ordinal >= 32 || parameter.AllowsRefLikeType) return false;
        tag = "Parameter"; number = 17; name = type.ToDisplayString(FullyQualifiedNameFormat);
        return true;
    }

    private static bool TryGetTypePattern(ITypeSymbol type, INamedTypeSymbol owner, INamedTypeSymbol? halfType, INamedTypeSymbol? listType, INamedTypeSymbol? dictionaryType, Compilation compilation, out TypePattern? pattern) {
        pattern = null;
        if (TryGetTypeTag(type, halfType, out _, out int builtin, out _)) {
            pattern = TypePattern.Builtin(builtin); return true;
        }
        if (type is ITypeParameterSymbol parameter) {
            if (parameter.Ordinal >= owner.Arity || parameter.AllowsRefLikeType) return false;
            pattern = TypePattern.Parameter(parameter.Ordinal); return true;
        }
        if (IsNullableValue(type)) {
            ITypeSymbol child = ((INamedTypeSymbol)type).TypeArguments[0];
            if (!child.IsValueType || !TryGetTypePattern(child, owner, halfType, listType, dictionaryType, compilation, out TypePattern? element)) return false;
            try { pattern = TypePattern.NullableOf(element!); return true; }
            catch (ArgumentException) { return false; }
        }
        if (type is IArrayTypeSymbol array) {
            if (array.Rank > 4 || (array.Rank == 1 && !array.IsSZArray) ||
                !TryGetTypePattern(array.ElementType, owner, halfType, listType, dictionaryType, compilation, out TypePattern? element)) return false;
            try { pattern = TypePattern.ArrayOf(element!, array.Rank); return true; }
            catch (ArgumentException) { return false; }
        }
        if (IsBclList(type, listType)) {
            if (!TryGetTypePattern(((INamedTypeSymbol)type).TypeArguments[0], owner, halfType, listType, dictionaryType, compilation, out TypePattern? element)) return false;
            try { pattern = TypePattern.ListOf(element!); return true; }
            catch (ArgumentException) { return false; }
        }
        if (IsBclDictionary(type, dictionaryType)) {
            INamedTypeSymbol dictionary = (INamedTypeSymbol)type;
            ITypeSymbol keyType = dictionary.TypeArguments[0];
            // Key representation uses the ordinary supported slot closure. Domain equality is
            // selected independently at Runtime; only a root Nullable key remains excluded.
            if (IsNullableValue(keyType)) return false;
            if (!TryGetTypePattern(keyType, owner, halfType, listType, dictionaryType, compilation, out TypePattern? key) ||
                !TryGetTypePattern(dictionary.TypeArguments[1], owner, halfType, listType, dictionaryType, compilation, out TypePattern? value)) return false;
            try { pattern = TypePattern.DictionaryOf(key!, value!); return true; }
            catch (ArgumentException) { return false; }
        }
        if (type is not INamedTypeSymbol named || !HasDurableContract(named, compilation) || named.Arity > 32 || named.IsRefLikeType || named.ContainingType is not null ||
            (!SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, owner.ContainingAssembly) &&
                !HasExternalDurableNominalShape(named, compilation))) return false;
        AttributeData? attribute = GetAttribute(named.GetAttributes(), DurableTypeAttributeMetadataName);
        if (attribute is null || attribute.ConstructorArguments.Length != 2 ||
            attribute.ConstructorArguments[0].Value is not string id || string.IsNullOrWhiteSpace(id) || !CanEncodeStrictUtf8(id)) return false;
        TypePattern[] arguments = new TypePattern[named.TypeArguments.Length];
        for (int index = 0; index < arguments.Length; index++) {
            if (!TryGetTypePattern(named.TypeArguments[index], owner, halfType, listType, dictionaryType, compilation, out TypePattern? argument)) return false;
            arguments[index] = argument!;
        }
        try { pattern = TypePattern.Named(id, arguments); return true; }
        catch (ArgumentException) { return false; }
    }

    private static TypePattern GetNamedTypePattern(INamedTypeSymbol type, INamedTypeSymbol? listType, INamedTypeSymbol? dictionaryType) {
        INamedTypeSymbol root = type;
        while (root.BaseType is not null) root = root.BaseType;
        INamedTypeSymbol? half = root.ContainingAssembly.GetTypeByMetadataName("System.Half");
        TypePattern ConvertType(ITypeSymbol item) {
            if (item is ITypeParameterSymbol parameter) return TypePattern.Parameter(parameter.Ordinal);
            if (TryGetTypeTag(item, half, out _, out int tag, out _)) return TypePattern.Builtin(tag);
            if (IsNullableValue(item)) return TypePattern.NullableOf(ConvertType(((INamedTypeSymbol)item).TypeArguments[0]));
            if (item is IArrayTypeSymbol array) return TypePattern.ArrayOf(ConvertType(array.ElementType), array.Rank);
            if (IsBclList(item, listType)) return TypePattern.ListOf(ConvertType(((INamedTypeSymbol)item).TypeArguments[0]));
            if (IsBclDictionary(item, dictionaryType)) {
                INamedTypeSymbol dictionary = (INamedTypeSymbol)item;
                return TypePattern.DictionaryOf(ConvertType(dictionary.TypeArguments[0]), ConvertType(dictionary.TypeArguments[1]));
            }
            INamedTypeSymbol named = (INamedTypeSymbol)item;
            AttributeData attribute = GetAttribute(named.GetAttributes(), DurableTypeAttributeMetadataName)!;
            TypePattern[] arguments = new TypePattern[named.TypeArguments.Length];
            for (int index = 0; index < arguments.Length; index++) arguments[index] = ConvertType(named.TypeArguments[index]);
            return TypePattern.Named((string)attribute.ConstructorArguments[0].Value!, arguments);
        }
        return ConvertType(type);
    }

    private static void AppendTypePatternExpression(StringBuilder text, TypePattern pattern) {
        const string prefix = "global::Atelia.DurableGraph.Schema.TypeExpr.";
        if (pattern.Kind == PatternKind.Builtin) {
            text.Append(prefix).Append("Builtin((global::Atelia.DurableGraph.Schema.TypeTag)").Append(pattern.BuiltinTag).Append(')');
        } else if (pattern.Kind == PatternKind.Parameter) {
            text.Append(prefix).Append("Parameter(").Append(pattern.ParameterOrdinal).Append(')');
        } else if (pattern.IsNullable) {
            text.Append(prefix).Append("Nullable(");
            AppendTypePatternExpression(text, pattern.ElementType!);
            text.Append(')');
        } else if (pattern.IsList) {
            text.Append(prefix).Append("List(");
            AppendTypePatternExpression(text, pattern.ElementType!);
            text.Append(')');
        } else if (pattern.IsDictionary) {
            text.Append(prefix).Append("Dictionary(");
            AppendTypePatternExpression(text, pattern.KeyType!);
            text.Append(", ");
            AppendTypePatternExpression(text, pattern.ValueType!);
            text.Append(')');
        } else if (pattern.IsArray) {
            text.Append(prefix).Append(pattern.ArrayRank == 1 ? "VectorArray(" : "MultiDimArray(");
            AppendTypePatternExpression(text, pattern.ElementType!);
            if (pattern.ArrayRank != 1) text.Append(", ").Append(pattern.ArrayRank);
            text.Append(')');
        } else {
            text.Append(prefix).Append("Named(").Append(SymbolDisplay.FormatLiteral(pattern.DefinitionId!, true));
            foreach (TypePattern argument in pattern.Arguments) {
                text.Append(", "); AppendTypePatternExpression(text, argument);
            }
            text.Append(')');
        }
    }

    private static bool UsesFamilyTemplates(List<DurableTypeAnalysis> types, List<SchemaTemplate> history) {
        foreach (DurableTypeAnalysis type in types) if (type.IsEnum || type.Symbol.IsRecord || UsesFamilyTemplate(CurrentShape(type))) return true;
        if (types.Count > 0) foreach (SchemaTemplate shape in history) {
            if (shape.Kind == 2 && !types.Exists(type => type.SchemaId == shape.SchemaId)) return true;
        }
        foreach (SchemaTemplate shape in history) if (UsesFamilyTemplate(shape)) return true;
        return false;
    }

    private static bool UsesFamilyTemplate(SchemaTemplate shape) {
        if (shape.Arity != 0 || (shape.BaseSchema.HasValue && shape.BaseSchema.Value.Type.Arguments.Count != 0)) return true;
        foreach (SchemaTemplateField field in shape.Fields) {
            if (field.ValuePattern.ContainsParameter || field.ValuePattern.Arguments.Count != 0) return true;
        }
        return false;
    }

    private static bool ValidateFamilyTemplateHistory(SourceProductionContext context, List<DurableTypeAnalysis> types, List<SchemaTemplate> history) {
        bool valid = true;
        List<SchemaTemplate> available = new(history);
        foreach (DurableTypeAnalysis type in types) {
            context.CancellationToken.ThrowIfCancellationRequested();
            SchemaTemplate current = CurrentShape(type);
            foreach (SchemaTemplate old in history) {
                context.CancellationToken.ThrowIfCancellationRequested();
                if (old.SchemaId != current.SchemaId) continue;
                if (old.Kind != current.Kind || old.Arity != current.Arity ||
                    (old.Version == current.Version && !HaveSameShape(old, current))) {
                    context.ReportDiagnostic(Diagnostic.Create(CurrentSchemaHistoryMismatch, GetSourceLocation(type.Symbol),
                        type.Symbol.ToDisplayString(QualifiedNameFormat), type.SchemaId, type.Version));
                    valid = false;
                }
            }
            for (int version = 1; version < type.Version; version++) {
                context.CancellationToken.ThrowIfCancellationRequested();
                if (FindHistory(history, type.SchemaId, version).Count == 0) {
                    context.ReportDiagnostic(Diagnostic.Create(MissingSchemaHistory, GetSourceLocation(type.Symbol),
                        type.Symbol.ToDisplayString(QualifiedNameFormat), type.SchemaId, version));
                    valid = false;
                    break;
                }
            }
            available.RemoveAll(entry => entry.SchemaId == current.SchemaId && entry.Version == current.Version);
            available.Add(current);
        }
        return ValidateHistoryClosure(context, available) && valid;
    }

    private static bool ValidatePatternParameters(SourceProductionContext context, SchemaTemplate shape) {
        List<TypePattern> patterns = new();
        if (shape.BaseSchema.HasValue) patterns.Add(shape.BaseSchema.Value.Type);
        foreach (SchemaTemplateField field in shape.Fields) patterns.Add(field.ValuePattern);
        foreach (TypePattern pattern in patterns) {
            if (!pattern.ParametersFit(shape.Arity)) {
                ReportInvalidHistoryAncestry(context, shape, "a type parameter lies outside its declaration's arity"); return false;
            }
        }
        return true;
    }

    private static bool ValidateTemplateNominalShapes(SourceProductionContext context, List<SchemaTemplate> records) {
        bool valid = true;
        Dictionary<string, int> arities = new(StringComparer.Ordinal);
        Dictionary<string, int> kinds = new(StringComparer.Ordinal);
        foreach (SchemaTemplate shape in records) {
            bool arityMatches = Require(arities, shape.SchemaId, shape.Arity);
            bool kindMatches = Require(kinds, shape.SchemaId, shape.Kind);
            if (!arityMatches || !kindMatches) {
                ReportInvalidHistoryAncestry(context, shape, "a Schema family has conflicting kind or arity");
                valid = false;
            }
            if (shape.BaseSchema.HasValue && !Check(shape.BaseSchema.Value.Type, 1)) {
                ReportInvalidHistoryAncestry(context, shape, "a base type pattern has conflicting kind or arity");
                valid = false;
            }
            foreach (SchemaTemplateField field in shape.Fields) {
                if (!Check(field.ValuePattern, field.TypeTagValue == 15 ? 1 : field.TypeTagValue == 16 ? 2 : 0)) {
                    ReportInvalidHistoryAncestry(context, shape, "a field type pattern has conflicting kind or arity");
                    valid = false;
                }
            }
        }
        return valid;

        bool Check(TypePattern pattern, int rootKind) {
            if (!CheckNullable(pattern)) return false;
            if (rootKind != 0 && pattern.Kind == PatternKind.Named && !Require(kinds, pattern.DefinitionId!, rootKind)) return false;
            foreach (TypePattern named in pattern.NamedNodes()) {
                if (!Require(arities, named.DefinitionId!, named.Arguments.Count)) return false;
            }
            return true;
        }

        bool CheckNullable(TypePattern pattern) {
            if (pattern.IsNullable && pattern.ElementType!.Kind == PatternKind.Named &&
                !Require(kinds, pattern.ElementType.DefinitionId!, 2)) return false;
            foreach (TypePattern argument in pattern.Arguments) if (!CheckNullable(argument)) return false;
            return true;
        }

        bool Require(Dictionary<string, int> values, string id, int value) {
            if (values.TryGetValue(id, out int previous)) return previous == value;
            values.Add(id, value);
            return true;
        }
    }

    private static bool TryParseTemplateHistory(string path, string[] lines, out SchemaTemplate model, out string? error) {
        model = default;
        bool allowTemporalScalars = lines[0] == "// durable-graph-schema-history:9";
        bool allowBclScalars = allowTemporalScalars || lines[0] == "// durable-graph-schema-history:8";
        bool allowDictionaries = allowBclScalars || lines[0] == "// durable-graph-schema-history:7";
        bool allowNullable = allowDictionaries || lines[0] == "// durable-graph-schema-history:6";
        bool allowLists = allowNullable || lines[0] == "// durable-graph-schema-history:5";
        bool allowArrays = allowLists || lines[0] == "// durable-graph-schema-history:4";
        error = "format 3/4/5/6/7/8/9 requires canonical kind, arity, and type-pattern records";
        if (lines.Length < 7 || lines[1] != "// schema-begin" || lines[lines.Length - 1] != "// schema-end" ||
            !lines[2].StartsWith("// schema-id-base64:", StringComparison.Ordinal) ||
            !TryDecodeSchemaId(lines[2].Substring(20), out string? id) ||
            !lines[3].StartsWith("// version:", StringComparison.Ordinal) || !TryParsePositiveCanonicalInt(lines[3].Substring(11), out int version) ||
            (lines[4] != "// kind:1" && lines[4] != "// kind:2") ||
            !lines[5].StartsWith("// arity:", StringComparison.Ordinal) || !TryParseArity(lines[5].Substring(9), out int arity)) return false;
        int kind = lines[4] == "// kind:2" ? 2 : 1;
        int cursor = 6;
        SchemaReference? baseSchema = null;
        if (lines[cursor].StartsWith("// base:", StringComparison.Ordinal)) {
            string[] parts = lines[cursor++].Substring(8).Split('|');
            if (parts.Length != 2 || !TypePattern.TryParse(parts[0], arity, out TypePattern? pattern, allowArrays, allowLists, allowNullable, allowDictionaries, allowBclScalars, allowTemporalScalars) || pattern!.Kind != PatternKind.Named ||
                !TryParsePositiveCanonicalInt(parts[1], out int baseVersion)) return false;
            baseSchema = new SchemaReference(pattern.DefinitionId!, baseVersion, pattern);
        }
        if (kind == 2 && baseSchema.HasValue) return false;
        List<SchemaTemplateField> fields = new();
        int previous = 0;
        while (cursor < lines.Length - 1) {
            string line = lines[cursor++];
            if (!line.StartsWith("// field:", StringComparison.Ordinal)) return false;
            string[] parts = line.Substring(9).Split('|');
            if (parts.Length < 2 || !TryParsePositiveCanonicalInt(parts[0], out int fieldId) || fieldId <= previous ||
                !TryParsePositiveCanonicalInt(parts[1], out int tag) || tag > (allowTemporalScalars ? 24 : allowBclScalars ? 21 : allowNullable ? 18 : 17)) return false;
            previous = fieldId;
            TypePattern? pattern;
            SchemaReference? inline = null;
            if (TypePattern.IsBuiltinTag(tag)) {
                if (parts.Length != 2) return false;
                pattern = TypePattern.Builtin(tag);
            } else {
                if (parts.Length < 3 || !TypePattern.TryParse(parts[2], arity, out pattern, allowArrays, allowLists, allowNullable, allowDictionaries, allowBclScalars, allowTemporalScalars)) return false;
                bool expectedKind = tag == 18 ? pattern!.IsNullable : tag == 17 ? pattern!.Kind == PatternKind.Parameter :
                    pattern!.Kind == PatternKind.Named || (tag == 15 && (pattern.IsArray || pattern.IsList || pattern.IsDictionary));
                if (!expectedKind) return false;
                TypePattern child = tag == 18 ? pattern!.ElementType! : pattern!;
                bool hasInlineVersion = tag == 16 || (tag == 18 && child.Kind == PatternKind.Named);
                if (parts.Length != (hasInlineVersion ? 4 : 3)) return false;
                if (hasInlineVersion) {
                    if (!TryParsePositiveCanonicalInt(parts[3], out int inlineVersion)) return false;
                    inline = new SchemaReference(child.DefinitionId!, inlineVersion, child);
                }
            }
            fields.Add(new SchemaTemplateField(fieldId, tag, tag == 15 ? pattern.DefinitionId : null, inline, pattern));
        }
        model = new SchemaTemplate(path, id!, version, fields, baseSchema, kind, arity);
        error = null;
        return true;
    }

    private static bool TryParseArity(string text, out int arity) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out arity) && arity >= 0 && arity <= 32 &&
        text == arity.ToString(CultureInfo.InvariantCulture);
}
