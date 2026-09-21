using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Text;
using Atelia.DurableGraph.SchemaHistory;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Atelia.DurableGraph.Generator;

[Generator(LanguageNames.CSharp)]
public sealed partial class DurableSchemaGenerator : IIncrementalGenerator {
    private const string DurableTypeAttributeMetadataName =
        "Atelia.DurableGraph.DurableTypeAttribute";
    private const string DurableFieldAttributeMetadataName =
        "Atelia.DurableGraph.DurableFieldAttribute";
    private const string TransientAttributeMetadataName =
        "Atelia.DurableGraph.TransientAttribute";
    private const string DurableObjectMetadataName =
        "Atelia.DurableGraph.IDurableObject";
    private const string SchemaHistoryManifestHeader =
        "// durable-graph-schema-history-manifest:9";
    private const string SchemaHistoryHeader =
        "// durable-graph-schema-history:1";
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static readonly DiagnosticDescriptor InvalidTypeShape = new(
        id: "DG0001",
        title: "Invalid durable type shape",
        messageFormat: "Type '{0}' must be a top-level enum, partial struct (including record struct), or partial class (including record class) implementing Atelia.DurableGraph.IDurableObject with supported generic constraints in an attributed hierarchy ending at object",
        category: "DurableGraph.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor InvalidSchemaMetadata = new(
        id: "DG0002",
        title: "Invalid durable schema metadata",
        messageFormat: "Durable type '{0}' must declare a non-empty schema ID and a positive version",
        category: "DurableGraph.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor UnclassifiedField = new(
        id: "DG0003",
        title: "Unclassified durable type field",
        messageFormat: "Field '{0}' must be marked with exactly one of DurableFieldAttribute or TransientAttribute",
        category: "DurableGraph.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor ConflictingFieldClassification = new(
        id: "DG0004",
        title: "Conflicting field classification",
        messageFormat: "Field '{0}' cannot be marked with both DurableFieldAttribute and TransientAttribute",
        category: "DurableGraph.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor InvalidFieldId = new(
        id: "DG0005",
        title: "Invalid durable field ID",
        messageFormat: "Durable field '{0}' must declare a positive field ID",
        category: "DurableGraph.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor DuplicateFieldId = new(
        id: "DG0006",
        title: "Duplicate durable field ID",
        messageFormat: "Durable field ID {0} is duplicated in type '{1}'",
        category: "DurableGraph.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor UnsupportedFieldType = new(
        id: "DG0007",
        title: "Unsupported durable field type",
        messageFormat: "Field '{0}' has unsupported durable type '{1}'",
        category: "DurableGraph.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ExistingSchemaMember = new(
        id: "DG0008",
        title: "Durable type already has a Schema member",
        messageFormat: "Type '{0}' already declares a member named 'Schema'",
        category: "DurableGraph.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor ClassifiedStaticField = new(
        id: "DG0009",
        title: "Static field has durable classification",
        messageFormat: "Static field '{0}' cannot be marked as durable or transient",
        category: "DurableGraph.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor ReadOnlyDurableField = new(
        id: "DG0011",
        title: "Durable field is readonly",
        messageFormat: "Durable field '{0}' cannot be readonly in this generated operation",
        category: "DurableGraph.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor MalformedSchemaHistory = new(
        id: "DG0012",
        title: "Malformed durable Schema history",
        messageFormat: "Schema history file '{0}' is malformed: {1}",
        category: "DurableGraph.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ConflictingSchemaHistory = new(
        id: "DG0013",
        title: "Conflicting durable Schema history",
        messageFormat: "Schema history for schema '{0}' version {1} conflicts with another shape",
        category: "DurableGraph.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor MissingSchemaHistory = new(
        id: "DG0014",
        title: "Missing durable Schema history",
        messageFormat: "Durable type '{0}' requires Schema history for schema '{1}' version {2}",
        category: "DurableGraph.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor CurrentSchemaHistoryMismatch = new(
        id: "DG0015",
        title: "Current durable Schema-history candidate mismatch",
        messageFormat: "Durable type '{0}' does not match Schema history for schema '{1}' version {2}",
        category: "DurableGraph.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ExistingSchemaSupportMember = new(
        id: "DG0016",
        title: "Durable type has a reserved Schema-support member",
        messageFormat: "Type '{0}' already uses the reserved generated Schema-support name '{1}'",
        category: "DurableGraph.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateSchemaId = new(
        id: "DG0017",
        title: "Duplicate durable schema ID",
        messageFormat: "Durable schema ID '{0}' is used by both type '{1}' and type '{2}'",
        category: "DurableGraph.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidDefinitionsOption = new(
        id: "DG0022", title: "Invalid Definition generation option",
        messageFormat: "DurableGraphGenerateDefinitions must be true or false; found '{0}'",
        category: "DurableGraph.Generator", defaultSeverity: DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly SymbolDisplayFormat QualifiedNameFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    private static readonly SymbolDisplayFormat FullyQualifiedNameFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    public void Initialize(IncrementalGeneratorInitializationContext context) {
        IncrementalValuesProvider<INamedTypeSymbol> durableTypes =
            context.SyntaxProvider.ForAttributeWithMetadataName(
                DurableTypeAttributeMetadataName,
                static (node, _) => node is TypeDeclarationSyntax or EnumDeclarationSyntax,
                static (attributeContext, _) =>
                    (INamedTypeSymbol)attributeContext.TargetSymbol);

        IncrementalValuesProvider<SchemaHistoryText> schemaHistoryFiles =
            context.AdditionalTextsProvider
                .Where(static file => StringComparer.OrdinalIgnoreCase.Equals(
                    Path.GetExtension(file.Path),
                    ".dgschema"))
                .Select(static (file, cancellationToken) => new SchemaHistoryText(
                    file.Path,
                    file.GetText(cancellationToken)?.ToString()));

        context.RegisterSourceOutput(
            durableTypes.Collect().Combine(schemaHistoryFiles.Collect()).Combine(context.CompilationProvider)
                .Combine(context.AnalyzerConfigOptionsProvider.Select(static (options, _) =>
                    options.GlobalOptions.TryGetValue("build_property.DurableGraphGenerateDefinitions", out string? value) ? value : null)),
            static (productionContext, input) =>
                GenerateSchemas(
                    productionContext,
                    input.Left.Left.Left,
                    input.Left.Left.Right,
                    input.Left.Right,
                    input.Right));
    }

    private static void GenerateSchemas(
        SourceProductionContext context,
        ImmutableArray<INamedTypeSymbol> candidateTypes,
        ImmutableArray<SchemaHistoryText> schemaHistoryFiles,
        Compilation compilation, string? definitionsOption) {
        bool forceDefinitions = false;
        if (!string.IsNullOrEmpty(definitionsOption) && !bool.TryParse(definitionsOption, out forceDefinitions)) {
            context.ReportDiagnostic(Diagnostic.Create(InvalidDefinitionsOption, Location.None, definitionsOption));
            return;
        }
        // Resolve from the actual core library, not a source-defined System.Half lookalike.
        INamedTypeSymbol? halfType = compilation.GetSpecialType(SpecialType.System_Object)
            .ContainingAssembly.GetTypeByMetadataName("System.Half");
        INamedTypeSymbol? listType = GetBclListType(compilation);
        INamedTypeSymbol? dictionaryType = GetBclDictionaryType(compilation);
        List<INamedTypeSymbol> types = GetDistinctSortedTypes(candidateTypes);
        List<DurableTypeModel> validTypes = new(types.Count);
        List<SchemaHistoryModel> history = ParseSchemaHistory(
            context,
            schemaHistoryFiles,
            out bool historyParsedSuccessfully);

        foreach (INamedTypeSymbol type in types) {
            context.CancellationToken.ThrowIfCancellationRequested();
            DurableTypeModel? model = CreateTypeModel(context, type, halfType, listType, dictionaryType, compilation);

            if (model.HasValue) {
                validTypes.Add(model.Value);
            }
        }

        validTypes = RemoveDuplicateSchemaIds(context, validTypes);
        validTypes = ValidateSchemaChains(context, validTypes, compilation);
        if (!TryImportSchemaReferences(context, compilation, validTypes, history, out List<SchemaHistoryModel> references, out string? referenceManifest)) return;
        List<SchemaHistoryModel> availableHistory = new(history);
        availableHistory.AddRange(references);

        if (!ValidateHistoryClosure(context, availableHistory)) {
            return;
        }
        if (!ValidateUpgradeRegistrations(context, validTypes, compilation, out bool hasUpgradeRegistrations)) {
            return;
        }
        if (!ValidateValueUpgradeRegistrations(context, validTypes, availableHistory, compilation, out bool hasValueUpgradeRegistrations)) {
            return;
        }

        if (validTypes.Count > 0 || types.Count == 0) {
            string manifestSource = RenderSchemaHistoryManifest(validTypes)
                .Replace("\r\n", "\n");
            if (references.Count > 0) manifestSource += "// references-sha256:" + SchemaHistoryReferenceProtocol.ComputeHash(referenceManifest!) + "\n";
            context.AddSource("DurableGraphSchemaHistoryReferences.g.cs", SourceText.From(referenceManifest!, new UTF8Encoding(false)));
            context.AddSource(
                "DurableGraphSchemaHistoryCandidates.g.cs",
                SourceText.From(manifestSource, Encoding.UTF8));
        }

        if (validTypes.Count > 0 || (types.Count == 0 && hasValueUpgradeRegistrations)) {
            if (references.Count > 0 || forceDefinitions || hasUpgradeRegistrations || hasValueUpgradeRegistrations || UsesGenericTemplates(validTypes, history)) {
                if (ValidateGenericTemplateHistory(context, validTypes, availableHistory)) {
                    GenerateGenericStates(context, validTypes, history, historyParsedSuccessfully, compilation, references);
                }
                return;
            }
            List<DurableTypeModel> schemaTypes = GenerateSchemas(context, validTypes, history);
            GenerateStates(context, validTypes, schemaTypes, history, historyParsedSuccessfully, halfType);
        }
    }

    private static List<DurableTypeModel> RemoveDuplicateSchemaIds(
        SourceProductionContext context,
        List<DurableTypeModel> types) {
        List<DurableTypeModel> result = new(types.Count);

        for (int index = 0; index < types.Count; index++) {
            DurableTypeModel current = types[index];
            DurableTypeModel? conflicting = null;

            for (int candidateIndex = 0;
                candidateIndex < types.Count;
                candidateIndex++) {
                if (candidateIndex != index &&
                    StringComparer.Ordinal.Equals(
                        current.SchemaId,
                        types[candidateIndex].SchemaId)) {
                    conflicting = types[candidateIndex];
                    break;
                }
            }

            if (conflicting.HasValue) {
                context.ReportDiagnostic(Diagnostic.Create(
                    DuplicateSchemaId,
                    GetSourceLocation(current.Symbol),
                    current.SchemaId,
                    current.Symbol.ToDisplayString(QualifiedNameFormat),
                    conflicting.Value.Symbol.ToDisplayString(QualifiedNameFormat)));
            } else {
                result.Add(current);
            }
        }

        return result;
    }

    private static List<INamedTypeSymbol> GetDistinctSortedTypes(
        ImmutableArray<INamedTypeSymbol> candidateTypes) {
        List<INamedTypeSymbol> result = new(candidateTypes.Length);

        foreach (INamedTypeSymbol candidate in candidateTypes) {
            bool alreadyAdded = false;

            foreach (INamedTypeSymbol existing in result) {
                if (SymbolEqualityComparer.Default.Equals(candidate, existing)) {
                    alreadyAdded = true;
                    break;
                }
            }

            if (!alreadyAdded) {
                result.Add(candidate);
            }
        }

        result.Sort(static (left, right) => StringComparer.Ordinal.Compare(
            left.ToDisplayString(QualifiedNameFormat),
            right.ToDisplayString(QualifiedNameFormat)));
        return result;
    }

    private static List<SchemaHistoryModel> ParseSchemaHistory(
        SourceProductionContext context,
        ImmutableArray<SchemaHistoryText> files,
        out bool valid) {
        valid = true;
        List<SchemaHistoryModel> history = new(files.Length);

        foreach (SchemaHistoryText file in files) {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (TryParseSchemaHistory(file, out SchemaHistoryModel model, out string? error)) {
                history.Add(model);
            } else {
                valid = false;
                context.ReportDiagnostic(Diagnostic.Create(
                    MalformedSchemaHistory,
                    CreateAdditionalFileLocation(file.Path),
                    file.Path,
                    error));
            }
        }

        history.Sort(static (left, right) => {
            int schemaComparison = StringComparer.Ordinal.Compare(
                left.SchemaId,
                right.SchemaId);

            if (schemaComparison != 0) {
                return schemaComparison;
            }

            int versionComparison = left.Version.CompareTo(right.Version);
            return versionComparison != 0
                ? versionComparison
                : StringComparer.Ordinal.Compare(left.Path, right.Path);
        });

        for (int index = 1; index < history.Count; index++) {
            SchemaHistoryModel previous = history[index - 1];
            SchemaHistoryModel current = history[index];

            if (StringComparer.Ordinal.Equals(previous.SchemaId, current.SchemaId) &&
                previous.Version == current.Version &&
                !HaveSameShape(previous, current)) {
                valid = false;
                context.ReportDiagnostic(Diagnostic.Create(
                    ConflictingSchemaHistory,
                    CreateAdditionalFileLocation(current.Path),
                    current.SchemaId,
                    current.Version));
            }
        }

        return history;
    }

    private static bool TryParseSchemaHistory(
        SchemaHistoryText file,
        out SchemaHistoryModel model,
        out string? error) {
        model = default;

        if (file.Content is null) {
            error = "the file could not be read";
            return false;
        }

        string normalized = file.Content.Replace("\r\n", "\n");
        if (normalized.IndexOf('\r') >= 0) {
            error = "bare carriage returns are not permitted";
            return false;
        }

        if (normalized.EndsWith("\n", StringComparison.Ordinal)) {
            normalized = normalized.Substring(0, normalized.Length - 1);
        }

        string[] lines = normalized.Split('\n');
        if (lines.Length > 0 && (lines[0] == "// durable-graph-schema-history:3" || lines[0] == "// durable-graph-schema-history:4" || lines[0] == "// durable-graph-schema-history:5" || lines[0] == "// durable-graph-schema-history:6" || lines[0] == "// durable-graph-schema-history:7" || lines[0] == "// durable-graph-schema-history:8" || lines[0] == "// durable-graph-schema-history:9")) {
            return TryParseTemplateHistory(file.Path, lines, out model, out error);
        }
        if (lines.Length < 5 ||
            (!StringComparer.Ordinal.Equals(lines[0], SchemaHistoryHeader) &&
                !StringComparer.Ordinal.Equals(lines[0], "// durable-graph-schema-history:2")) ||
            !StringComparer.Ordinal.Equals(lines[1], "// schema-begin") ||
            !StringComparer.Ordinal.Equals(lines[lines.Length - 1], "// schema-end")) {
            error = "the required header and single Schema-history record block were not found";
            return false;
        }

        const string SchemaPrefix = "// schema-id-base64:";
        if (!lines[2].StartsWith(SchemaPrefix, StringComparison.Ordinal) ||
            !TryDecodeSchemaId(lines[2].Substring(SchemaPrefix.Length), out string? schemaId)) {
            error = "the schema ID must be canonical base64-encoded non-empty UTF-8";
            return false;
        }

        const string VersionPrefix = "// version:";
        if (!lines[3].StartsWith(VersionPrefix, StringComparison.Ordinal) ||
            !TryParsePositiveCanonicalInt(
                lines[3].Substring(VersionPrefix.Length),
                out int version)) {
            error = "the version must be a positive canonical integer";
            return false;
        }

        List<SchemaHistoryFieldModel> fields = new(lines.Length - 5);
        int previousFieldId = 0;
        SchemaReference? baseSchema = null;
        int firstFieldLine = 4;
        int kind = 1;
        bool format2 = lines[0].EndsWith(":2", StringComparison.Ordinal);
        if (format2) {
            if (lines.Length < 6 || (lines[4] != "// kind:1" && lines[4] != "// kind:2")) {
                error = "format 2 requires a canonical Schema kind";
                return false;
            }
            kind = lines[4] == "// kind:2" ? 2 : 1;
            firstFieldLine++;
        }
        if (lines[firstFieldLine].StartsWith("// base:", StringComparison.Ordinal)) {
            string record = lines[firstFieldLine].Substring("// base:".Length);
            int separator = record.IndexOf('|');
            if (separator <= 0 || separator != record.LastIndexOf('|') ||
                !TryDecodeSchemaId(record.Substring(0, separator), out string? baseId) ||
                !TryParsePositiveCanonicalInt(record.Substring(separator + 1), out int baseVersion)) {
                error = "the base must contain a canonical UTF-8 base64 schema ID and positive canonical version";
                return false;
            }

            baseSchema = new SchemaReference(baseId!, baseVersion);
            firstFieldLine++;
        }

        for (int index = firstFieldLine; index < lines.Length - 1; index++) {
            const string FieldPrefix = "// field:";
            string line = lines[index];

            if (!line.StartsWith(FieldPrefix, StringComparison.Ordinal)) {
                error = "each field must use the canonical field record syntax";
                return false;
            }

            string fieldRecord = line.Substring(FieldPrefix.Length);
            string[] parts = fieldRecord.Split('|');
            string? targetSchemaId = null;
            int inlineVersion = 0;
            if (parts.Length < 2 || parts.Length > 4 ||
                !TryParsePositiveCanonicalInt(
                    parts[0],
                    out int fieldId) ||
                fieldId <= previousFieldId ||
                !TryParsePositiveCanonicalInt(
                    parts[1],
                    out int typeTagValue) ||
                (typeTagValue == 16
                    ? !format2 || parts.Length != 4 || !TryDecodeSchemaId(parts[2], out targetSchemaId) || !TryParsePositiveCanonicalInt(parts[3], out inlineVersion)
                    : typeTagValue == 15
                        ? parts.Length != 3 || !TryDecodeSchemaId(parts[2], out targetSchemaId)
                        : parts.Length != 2 || typeTagValue > 14 || !TryGetFieldTypeName(typeTagValue, out _))) {
                error = "fields must have increasing positive IDs and supported numeric type tags";
                return false;
            }

            fields.Add(new SchemaHistoryFieldModel(fieldId, typeTagValue,
                typeTagValue == 16 ? null : targetSchemaId,
                typeTagValue == 16 ? new SchemaReference(targetSchemaId!, inlineVersion) : null));
            previousFieldId = fieldId;
        }

        model = new SchemaHistoryModel(
            file.Path,
            schemaId!,
            version,
            fields,
            baseSchema, kind);
        error = null;
        return true;
    }

    private static bool TryDecodeSchemaId(
        string encoded,
        out string? schemaId) {
        try {
            byte[] bytes = Convert.FromBase64String(encoded);
            if (!StringComparer.Ordinal.Equals(Convert.ToBase64String(bytes), encoded)) {
                schemaId = null;
                return false;
            }

            schemaId = StrictUtf8.GetString(bytes);
            return !string.IsNullOrWhiteSpace(schemaId);
        } catch (FormatException) {
            schemaId = null;
            return false;
        } catch (DecoderFallbackException) {
            schemaId = null;
            return false;
        }
    }

    private static bool TryParsePositiveCanonicalInt(
        string text,
        out int value) {
        return int.TryParse(
                text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out value) &&
            value > 0 &&
            StringComparer.Ordinal.Equals(
                text,
                value.ToString(CultureInfo.InvariantCulture));
    }

    private static bool CanEncodeStrictUtf8(string value) {
        try {
            StrictUtf8.GetByteCount(value);
            return true;
        } catch (EncoderFallbackException) {
            return false;
        }
    }

    private static Location CreateAdditionalFileLocation(string path) {
        return Location.Create(
            path,
            new TextSpan(0, 0),
            new LinePositionSpan(
                new LinePosition(0, 0),
                new LinePosition(0, 0)));
    }

    private static List<SchemaHistoryModel> FindHistory(
        List<SchemaHistoryModel> history,
        string schemaId,
        int version) {
        List<SchemaHistoryModel> result = new();

        foreach (SchemaHistoryModel candidate in history) {
            if (StringComparer.Ordinal.Equals(candidate.SchemaId, schemaId) &&
                candidate.Version == version) {
                result.Add(candidate);
            }
        }

        return result;
    }

    private static bool AllHaveSameShape(List<SchemaHistoryModel> history) {
        for (int index = 1; index < history.Count; index++) {
            if (!HaveSameShape(history[0], history[index])) {
                return false;
            }
        }

        return true;
    }

    private static bool HaveSameFields(
        List<SchemaHistoryFieldModel> left,
        List<SchemaHistoryFieldModel> right) {
        if (left.Count != right.Count) {
            return false;
        }

        for (int index = 0; index < left.Count; index++) {
            if (left[index].FieldId != right[index].FieldId ||
                left[index].TypeTagValue != right[index].TypeTagValue ||
                !left[index].ValuePattern.Equals(right[index].ValuePattern) ||
                !StringComparer.Ordinal.Equals(left[index].TargetSchemaId, right[index].TargetSchemaId) ||
                !SameReference(left[index].InlineSchema, right[index].InlineSchema)) {
                return false;
            }
        }

        return true;
    }

    private static List<SchemaHistoryFieldModel> ToSchemaHistoryFields(
        List<DurableFieldModel> fields) {
        List<SchemaHistoryFieldModel> result = new(fields.Count);

        foreach (DurableFieldModel field in fields) {
            result.Add(new SchemaHistoryFieldModel(
                field.FieldId,
                field.TypeTagValue, field.TargetSchemaId, field.InlineSchema, field.ValuePattern));
        }

        return result;
    }

    private static DurableTypeModel? CreateTypeModel(
        SourceProductionContext context,
        INamedTypeSymbol type,
        INamedTypeSymbol? halfType,
        INamedTypeSymbol? listType,
        INamedTypeSymbol? dictionaryType,
        Compilation compilation) {
        string typeName = type.ToDisplayString(QualifiedNameFormat);

        if (!HasSupportedTypeShape(type, context.CancellationToken) || !HasDurableContract(type, compilation)) {
            context.ReportDiagnostic(Diagnostic.Create(
                InvalidTypeShape,
                GetSourceLocation(type),
                typeName));
            return null;
        }

        bool hasErrors = false;
        if (type.TypeKind == TypeKind.Class && type.BaseType is not null && type.BaseType.Arity > 0 &&
            !IsObjectBase(type.BaseType) &&
            !TryGetTypePattern(type.BaseType, type, halfType, listType, dictionaryType, compilation, out _)) {
            context.ReportDiagnostic(Diagnostic.Create(InvalidTypeShape, GetSourceLocation(type), typeName));
            return null;
        }
        AttributeData? durableTypeAttribute = GetAttribute(
            type.GetAttributes(),
            DurableTypeAttributeMetadataName);
        string? schemaId = null;
        int version = 0;

        if (durableTypeAttribute is null ||
            durableTypeAttribute.ConstructorArguments.Length != 2 ||
            durableTypeAttribute.ConstructorArguments[0].Value is not string candidateSchemaId ||
            string.IsNullOrWhiteSpace(candidateSchemaId) ||
            !CanEncodeStrictUtf8(candidateSchemaId) ||
            durableTypeAttribute.ConstructorArguments[1].Value is not int candidateVersion ||
            candidateVersion <= 0) {
            context.ReportDiagnostic(Diagnostic.Create(
                InvalidSchemaMetadata,
                GetAttributeLocation(durableTypeAttribute) ?? GetSourceLocation(type),
                typeName));
            hasErrors = true;
        } else {
            schemaId = candidateSchemaId;
            version = candidateVersion;
        }

        if (type.TypeKind == TypeKind.Enum) return CreateEnumModel(context, type, schemaId, version, hasErrors, listType, dictionaryType);

        ImmutableArray<ISymbol> schemaMembers = type.GetMembers("Schema");
        if (StringComparer.Ordinal.Equals(type.Name, "Schema") ||
            !schemaMembers.IsEmpty) {
            context.ReportDiagnostic(Diagnostic.Create(
                ExistingSchemaMember,
                schemaMembers.IsEmpty
                    ? GetSourceLocation(type)
                    : GetSourceLocation(schemaMembers[0]),
                typeName));
            hasErrors = true;
        }

        hasErrors |= ReportSchemaSupportNameCollisions(context, type);

        List<IFieldSymbol> fields = GetDirectFields(type);
        List<DurableFieldModel> durableFields = new();
        if (type.IsRecord) hasErrors |= ReportRecordStorageErrors(context, type, fields, compilation);

        foreach (IFieldSymbol field in fields) {
            AttributeData? durableFieldAttribute = GetAttribute(
                field.GetAttributes(),
                DurableFieldAttributeMetadataName);
            bool isDurable = durableFieldAttribute is not null;
            bool isTransient = HasAttribute(
                field.GetAttributes(),
                TransientAttributeMetadataName);

            if (field.IsStatic) {
                if (isDurable || isTransient) {
                    context.ReportDiagnostic(Diagnostic.Create(
                        ClassifiedStaticField,
                        GetSourceLocation(field),
                        field.Name));
                    hasErrors = true;
                }

                continue;
            }

            if (!isDurable && !isTransient) {
                context.ReportDiagnostic(Diagnostic.Create(
                    UnclassifiedField,
                    GetSourceLocation(field),
                    field.Name));
                hasErrors = true;
                continue;
            }

            if (isDurable && isTransient) {
                context.ReportDiagnostic(Diagnostic.Create(
                    ConflictingFieldClassification,
                    GetSourceLocation(field),
                    field.Name));
                hasErrors = true;
                continue;
            }

            if (isTransient) {
                continue;
            }

            int fieldId = 0;
            if (durableFieldAttribute!.ConstructorArguments.Length != 1 ||
                durableFieldAttribute.ConstructorArguments[0].Value is not int candidateFieldId ||
                candidateFieldId <= 0) {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidFieldId,
                    GetAttributeLocation(durableFieldAttribute) ?? GetSourceLocation(field),
                    field.Name));
                hasErrors = true;
                continue;
            }

            fieldId = candidateFieldId;

            string? targetSchemaId = null;
            SchemaReference? inlineSchema = null;
            TypePattern? valuePattern = null;
            if (!TryGetTypeTag(
                field.Type,
                halfType,
                out string? typeTag,
                out int typeTagValue,
                out string? fieldTypeName) &&
                !TryGetNominalReference(field.Type, type, compilation,
                    context.CancellationToken, out typeTag, out typeTagValue, out fieldTypeName, out targetSchemaId) &&
                !TryGetInlineValue(field.Type, type, compilation, context.CancellationToken,
                    out typeTag, out typeTagValue, out fieldTypeName, out inlineSchema) &&
                !TryGetParameterField(field.Type, out typeTag, out typeTagValue, out fieldTypeName) &&
                !TryGetArrayField(field.Type, out typeTag, out typeTagValue, out fieldTypeName) &&
                !TryGetListField(field.Type, listType, out typeTag, out typeTagValue, out fieldTypeName) &&
                !TryGetDictionaryField(field.Type, dictionaryType, out typeTag, out typeTagValue, out fieldTypeName) &&
                !TryGetNullableField(field.Type, type, halfType, listType, dictionaryType, compilation, context.CancellationToken,
                    out typeTag, out typeTagValue, out fieldTypeName, out inlineSchema)) {
                context.ReportDiagnostic(Diagnostic.Create(
                    UnsupportedFieldType,
                    GetSourceLocation(field),
                    field.Name,
                    field.Type.ToDisplayString(QualifiedNameFormat)));
                hasErrors = true;
                continue;
            }
            if (!TryGetTypePattern(field.Type, type, halfType, listType, dictionaryType, compilation, out valuePattern)) {
                context.ReportDiagnostic(Diagnostic.Create(UnsupportedFieldType, GetSourceLocation(field),
                    field.Name, field.Type.ToDisplayString(QualifiedNameFormat)));
                hasErrors = true;
                continue;
            }
            if (inlineSchema.HasValue) inlineSchema = new SchemaReference(inlineSchema.Value.SchemaId, inlineSchema.Value.Version, valuePattern!.IsNullable ? valuePattern.ElementType : valuePattern);

            durableFields.Add(new DurableFieldModel(
                field,
                fieldId,
                typeTag!,
                typeTagValue,
                fieldTypeName!, targetSchemaId, inlineSchema, valuePattern));
        }

        durableFields.Sort(static (left, right) => left.FieldId.CompareTo(right.FieldId));
        hasErrors |= ReportDuplicateFieldIds(context, typeName, durableFields);
        if (type.IsRecord && schemaId is not null) hasErrors |= ReportRecordHelperCollisions(context, type, schemaId, durableFields);

        if (hasErrors) {
            return null;
        }

        return new DurableTypeModel(type, schemaId!, version, durableFields, listType, dictionaryType);
    }

    private static bool HasSupportedTypeShape(
        INamedTypeSymbol type,
        System.Threading.CancellationToken cancellationToken) {
        return HasDurableTypeShape(type, cancellationToken);
    }

    private static bool HasPartialModifier(SyntaxTokenList modifiers) {
        foreach (SyntaxToken modifier in modifiers) {
            if (modifier.IsKind(SyntaxKind.PartialKeyword)) {
                return true;
            }
        }

        return false;
    }

    private static bool HasFileModifier(SyntaxTokenList modifiers) {
        foreach (SyntaxToken modifier in modifiers) {
            if (modifier.IsKind(SyntaxKind.FileKeyword)) {
                return true;
            }
        }

        return false;
    }

    private static List<IFieldSymbol> GetDirectFields(INamedTypeSymbol type) {
        List<IFieldSymbol> fields = new();

        foreach (ISymbol member in type.GetMembers()) {
            if (member is IFieldSymbol field && (!field.IsImplicitlyDeclared || type.IsRecord)) {
                fields.Add(field);
            }
        }

        fields.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.Name, right.Name));
        return fields;
    }

    private static bool ReportDuplicateFieldIds(
        SourceProductionContext context,
        string typeName,
        List<DurableFieldModel> fields) {
        bool foundDuplicate = false;

        for (int start = 0; start < fields.Count;) {
            int end = start + 1;

            while (end < fields.Count && fields[end].FieldId == fields[start].FieldId) {
                end++;
            }

            if (end - start > 1) {
                foundDuplicate = true;
                context.ReportDiagnostic(Diagnostic.Create(
                    DuplicateFieldId,
                    GetSourceLocation(fields[start + 1].Symbol),
                    fields[start].FieldId,
                    typeName));
            }

            start = end;
        }

        return foundDuplicate;
    }

    private static bool TryGetTypeTag(
        ITypeSymbol type,
        INamedTypeSymbol? halfType,
        out string? typeTag,
        out int typeTagValue,
        out string? fieldTypeName) {
        typeTagValue = type.SpecialType switch {
            SpecialType.System_Boolean => 1,
            SpecialType.System_Int32 => 2,
            SpecialType.System_Int64 => 3,
            SpecialType.System_String => 4,
            SpecialType.System_Byte => 5,
            SpecialType.System_SByte => 6,
            SpecialType.System_Int16 => 7,
            SpecialType.System_UInt16 => 8,
            SpecialType.System_UInt32 => 9,
            SpecialType.System_UInt64 => 10,
            SpecialType.System_Char => 11,
            SpecialType.System_Single => 13,
            SpecialType.System_Double => 14,
            SpecialType.System_Decimal => 20,
            _ => GetCoreLibraryScalarTag(type, halfType),
        };
        if (TryGetFieldTypeName(typeTagValue, out fieldTypeName)) {
            typeTag = GetTypeTagName(typeTagValue);
            return true;
        }

        typeTag = null;
        return false;
    }

    private static int GetCoreLibraryScalarTag(ITypeSymbol type, INamedTypeSymbol? halfType) {
        // The Half symbol comes from this compilation's actual System.Object core library.
        // Resolve the other metadata symbols there too; source-defined System names are not builtins.
        if (halfType is null) return 0;
        if (SymbolEqualityComparer.Default.Equals(type, halfType)) return 12;
        int tag = type.Name switch {
            "Guid" => 19,
            "TimeSpan" => 21,
            "DateOnly" => 22,
            "TimeOnly" => 23,
            "DateTimeOffset" => 24,
            _ => 0,
        };
        return tag != 0 && SymbolEqualityComparer.Default.Equals(type,
            halfType.ContainingAssembly.GetTypeByMetadataName("System." + type.Name)) ? tag : 0;
    }

    private static bool TryGetNominalReference(
        ITypeSymbol fieldType, INamedTypeSymbol owner, Compilation compilation,
        System.Threading.CancellationToken cancellationToken,
        out string? typeTag, out int typeTagValue, out string? fieldTypeName, out string? targetSchemaId) {
        typeTag = null;
        typeTagValue = 0;
        fieldTypeName = null;
        targetSchemaId = null;
        if (fieldType is not INamedTypeSymbol target || target.TypeKind != TypeKind.Class ||
            !(SymbolEqualityComparer.Default.Equals(target.ContainingAssembly, owner.ContainingAssembly)
                ? HasDurableTypeShape(target, cancellationToken) && HasDurableContract(target, compilation)
                : HasExternalDurableNominalShape(target, compilation))) {
            return false;
        }
        AttributeData? attribute = GetAttribute(target.GetAttributes(), DurableTypeAttributeMetadataName);
        if (attribute is null || attribute.ConstructorArguments.Length != 2 ||
            attribute.ConstructorArguments[0].Value is not string identity ||
            string.IsNullOrWhiteSpace(identity) || !CanEncodeStrictUtf8(identity)) {
            return false;
        }
        typeTag = "ObjectReference";
        typeTagValue = 15;
        fieldTypeName = fieldType.ToDisplayString(QualifiedNameFormat);
        targetSchemaId = identity;
        return true;
    }

    private static bool TryGetInlineValue(
        ITypeSymbol fieldType, INamedTypeSymbol owner, Compilation compilation,
        System.Threading.CancellationToken cancellationToken,
        out string? typeTag, out int typeTagValue, out string? fieldTypeName, out SchemaReference? inlineSchema) {
        typeTag = null; typeTagValue = 0; fieldTypeName = null; inlineSchema = null;
        if (fieldType is not INamedTypeSymbol target || (target.TypeKind != TypeKind.Struct && target.TypeKind != TypeKind.Enum) ||
            (SymbolEqualityComparer.Default.Equals(target.ContainingAssembly, owner.ContainingAssembly)
                ? !HasDurableTypeShape(target, cancellationToken) || !HasDurableContract(target, compilation)
                : !HasExternalDurableNominalShape(target, compilation))) return false;
        AttributeData? attribute = GetAttribute(target.GetAttributes(), DurableTypeAttributeMetadataName);
        if (attribute is null || attribute.ConstructorArguments.Length != 2 ||
            attribute.ConstructorArguments[0].Value is not string identity ||
            string.IsNullOrWhiteSpace(identity) || !CanEncodeStrictUtf8(identity) ||
            attribute.ConstructorArguments[1].Value is not int version || version <= 0) return false;
        typeTag = "InlineValue"; typeTagValue = 16;
        fieldTypeName = fieldType.ToDisplayString(FullyQualifiedNameFormat);
        inlineSchema = new SchemaReference(identity, version);
        return true;
    }

    private static bool TryGetFieldTypeName(
        int typeTagValue,
        out string? fieldTypeName) {
        if (TypePattern.IsBuiltinTag(typeTagValue)) {
            fieldTypeName = "global::System." + GetTypeTagName(typeTagValue);
            return true;
        }

        fieldTypeName = null;
        return false;
    }

    private static string RenderSchemaHistoryManifest(List<DurableTypeModel> types) {
        List<DurableTypeModel> sortedTypes = new(types);
        sortedTypes.Sort(static (left, right) => {
            int schemaComparison = StringComparer.Ordinal.Compare(
                left.SchemaId,
                right.SchemaId);

            if (schemaComparison != 0) {
                return schemaComparison;
            }

            int versionComparison = left.Version.CompareTo(right.Version);
            return versionComparison != 0
                ? versionComparison
                : StringComparer.Ordinal.Compare(
                    left.Symbol.ToDisplayString(QualifiedNameFormat),
                    right.Symbol.ToDisplayString(QualifiedNameFormat));
        });

        StringBuilder source = new();
        source.AppendLine(SchemaHistoryManifestHeader);

        foreach (DurableTypeModel type in sortedTypes) {
            source.AppendLine("// schema-begin");
            source.Append("// schema-id-base64:")
                .AppendLine(Convert.ToBase64String(StrictUtf8.GetBytes(type.SchemaId)));
            source.Append("// version:")
                .AppendLine(type.Version.ToString(CultureInfo.InvariantCulture));
            source.Append("// kind:").AppendLine(type.IsInline ? "2" : "1");
            source.Append("// arity:").AppendLine(type.Arity.ToString(CultureInfo.InvariantCulture));
            SchemaReference? baseSchema = GetCurrentBaseReference(type);
            if (baseSchema.HasValue) {
                source.Append("// base:")
                    .Append(baseSchema.Value.Type.ToString())
                    .Append('|')
                    .AppendLine(baseSchema.Value.Version.ToString(CultureInfo.InvariantCulture));
            }

            foreach (DurableFieldModel field in type.Fields) {
                source.Append("// field:")
                    .Append(field.FieldId.ToString(CultureInfo.InvariantCulture))
                    .Append('|')
                    .Append(field.TypeTagValue.ToString(CultureInfo.InvariantCulture));
                if (field.TypeTagValue == 15 || field.TypeTagValue == 17 || (field.TypeTagValue == 18 && !field.InlineSchema.HasValue)) {
                    source.Append('|').Append(field.ValuePattern.ToString());
                }
                if (field.InlineSchema.HasValue) {
                    source.Append('|').Append(field.ValuePattern.ToString())
                        .Append('|').Append(field.InlineSchema.Value.Version.ToString(CultureInfo.InvariantCulture));
                }
                source.AppendLine();
            }

            source.AppendLine("// schema-end");
        }

        return source.ToString();
    }

    private static string GetTypeTagName(int typeTagValue) {
        switch (typeTagValue) {
            case 1:
                return "Boolean";
            case 2:
                return "Int32";
            case 3:
                return "Int64";
            case 4:
                return "String";
            case 5:
                return "Byte";
            case 6:
                return "SByte";
            case 7:
                return "Int16";
            case 8:
                return "UInt16";
            case 9:
                return "UInt32";
            case 10:
                return "UInt64";
            case 11:
                return "Char";
            case 12:
                return "Half";
            case 13:
                return "Single";
            case 14:
                return "Double";
            case 19:
                return "Guid";
            case 20:
                return "Decimal";
            case 21:
                return "TimeSpan";
            case 22:
                return "DateOnly";
            case 23:
                return "TimeOnly";
            case 24:
                return "DateTimeOffset";
            case 15:
                return "ObjectReference";
            case 16:
                return "InlineValue";
            default:
                throw new InvalidOperationException("Unsupported Schema-history type tag.");
        }
    }

    private static string EscapeIdentifier(string identifier) {
        return SyntaxFacts.GetKeywordKind(identifier) != SyntaxKind.None ||
            SyntaxFacts.GetContextualKeywordKind(identifier) != SyntaxKind.None
            ? "@" + identifier
            : identifier;
    }

    private static AttributeData? GetAttribute(
        ImmutableArray<AttributeData> attributes,
        string metadataName) {
        foreach (AttributeData attribute in attributes) {
            if (HasMetadataName(attribute.AttributeClass, metadataName)) {
                return attribute;
            }
        }

        return null;
    }

    private static bool HasAttribute(
        ImmutableArray<AttributeData> attributes,
        string metadataName) {
        return GetAttribute(attributes, metadataName) is not null;
    }

    private static bool HasMetadataName(
        INamedTypeSymbol? type,
        string metadataName) {
        if (type is null || type.ContainingType is not null) {
            return false;
        }

        int separator = metadataName.LastIndexOf('.');
        string expectedNamespace = separator < 0
            ? string.Empty
            : metadataName.Substring(0, separator);
        string expectedTypeName = separator < 0
            ? metadataName
            : metadataName.Substring(separator + 1);

        return StringComparer.Ordinal.Equals(type.MetadataName, expectedTypeName) &&
            StringComparer.Ordinal.Equals(
                type.ContainingNamespace.ToDisplayString(),
                expectedNamespace);
    }

    private static Location? GetAttributeLocation(AttributeData? attribute) {
        return attribute?.ApplicationSyntaxReference?.GetSyntax().GetLocation();
    }

    private static Location GetSourceLocation(ISymbol symbol) {
        if (symbol is IFieldSymbol field && field.IsImplicitlyDeclared && field.AssociatedSymbol is IPropertySymbol property) {
            return GetSourceLocation(property);
        }
        foreach (Location location in symbol.Locations) {
            if (location.IsInSource) {
                return location;
            }
        }

        return Location.None;
    }

    private readonly struct DurableFieldModel {
        public DurableFieldModel(
            IFieldSymbol? symbol,
            int fieldId,
            string typeTag,
            int typeTagValue,
            string fieldTypeName,
            string? targetSchemaId = null, SchemaReference? inlineSchema = null, TypePattern? valuePattern = null) {
            InlineSchema = inlineSchema;
            _symbol = symbol;
            FieldId = fieldId;
            TypeTag = typeTag;
            TypeTagValue = typeTagValue;
            FieldTypeName = fieldTypeName;
            TargetSchemaId = targetSchemaId;
            ValuePattern = valuePattern ?? (inlineSchema?.Type ?? (targetSchemaId is not null ? TypePattern.Named(targetSchemaId) : TypePattern.Builtin(typeTagValue)));
        }

        // Synthetic enum representation fields have no CLR instance field.
        private readonly IFieldSymbol? _symbol;
        public IFieldSymbol Symbol => _symbol ?? throw new InvalidOperationException("A synthetic representation field has no domain field symbol.");

        public int FieldId { get; }

        public string TypeTag { get; }

        public int TypeTagValue { get; }

        public string FieldTypeName { get; }
        public string? TargetSchemaId { get; }
        public SchemaReference? InlineSchema { get; }
        public TypePattern ValuePattern { get; }
    }

    private readonly struct DurableTypeModel {
        public DurableTypeModel(
            INamedTypeSymbol symbol,
            string schemaId,
            int version,
            List<DurableFieldModel> fields, INamedTypeSymbol? listType, INamedTypeSymbol? dictionaryType) {
            ListType = listType;
            DictionaryType = dictionaryType;
            Symbol = symbol;
            SchemaId = schemaId;
            Version = version;
            Fields = fields;
        }

        public INamedTypeSymbol Symbol { get; }
        public INamedTypeSymbol? ListType { get; }
        public INamedTypeSymbol? DictionaryType { get; }

        public string SchemaId { get; }

        public int Version { get; }

        public List<DurableFieldModel> Fields { get; }
        public bool IsEnum => Symbol.TypeKind == TypeKind.Enum;
        public bool IsInline => Symbol.TypeKind is TypeKind.Struct or TypeKind.Enum;
        public int Arity => Symbol.Arity;
    }

    private readonly struct SchemaHistoryText {
        public SchemaHistoryText(string path, string? content) {
            Path = path;
            Content = content;
        }

        public string Path { get; }

        public string? Content { get; }
    }

    private readonly struct SchemaHistoryFieldModel {
        public SchemaHistoryFieldModel(int fieldId, int typeTagValue, string? targetSchemaId = null, SchemaReference? inlineSchema = null, TypePattern? valuePattern = null) {
            InlineSchema = inlineSchema;
            FieldId = fieldId;
            TypeTagValue = typeTagValue;
            TargetSchemaId = targetSchemaId;
            ValuePattern = valuePattern ?? (inlineSchema?.Type ?? (targetSchemaId is not null ? TypePattern.Named(targetSchemaId) : TypePattern.Builtin(typeTagValue)));
        }

        public int FieldId { get; }

        public int TypeTagValue { get; }
        public string? TargetSchemaId { get; }
        public SchemaReference? InlineSchema { get; }
        public TypePattern ValuePattern { get; }
    }

    private readonly struct SchemaHistoryModel {
        public SchemaHistoryModel(
            string path,
            string schemaId,
            int version,
            List<SchemaHistoryFieldModel> fields,
            SchemaReference? baseSchema = null, int kind = 1, int arity = 0) {
            Kind = kind;
            Path = path;
            SchemaId = schemaId;
            Version = version;
            Fields = fields;
            BaseSchema = baseSchema;
            Arity = arity;
        }

        public string Path { get; }

        public string SchemaId { get; }

        public int Version { get; }

        public List<SchemaHistoryFieldModel> Fields { get; }

        public SchemaReference? BaseSchema { get; }
        public int Kind { get; }
        public int Arity { get; }
    }

}
