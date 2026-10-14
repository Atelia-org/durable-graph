using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Atelia.DurableGraph.Generator;

public sealed partial class DurableSchemaGenerator {
    // The representation key contains only exact persisted identity, never a domain CLR name.
    // A future generic closure can supply its own representation key without changing value operations.
    private static string InlineHelperName(SchemaReference reference) {
        StringBuilder name = new("global::Atelia.DurableGraph.Generated.__Inline_");
        foreach (byte value in Encoding.UTF8.GetBytes(reference.SchemaId)) {
            name.Append(value.ToString("X2", CultureInfo.InvariantCulture));
        }
        return name.Append("_V").Append(reference.Version.ToString(CultureInfo.InvariantCulture)).ToString();
    }

    private static string InlineDtoTypeName(SchemaReference reference) =>
        InlineHelperName(reference) + ".V" + reference.Version.ToString(CultureInfo.InvariantCulture);

    private static string BinaryFieldTypeName(BinaryFieldModel field) {
        if (field.InlineSchema.HasValue) return InlineDtoTypeName(field.InlineSchema.Value);
        if (IsBinaryReference(field.TypeTagValue)) return RootName + "ObjectId";
        TryGetFieldTypeName(GetBinarySlotTypeTag(field.TypeTagValue), out string? name);
        return name!;
    }

    private static bool IsBinaryFieldHasReferences(BinaryFieldModel field) => field.HasReferences;

    private static bool InlineHasReferences(SchemaReference reference, List<SchemaTemplate> available) {
        HashSet<string> visited = new(StringComparer.Ordinal);
        Stack<SchemaReference> pending = new();
        pending.Push(reference);
        while (pending.Count > 0) {
            SchemaReference next = pending.Pop();
            if (!visited.Add(InlineHelperName(next))) continue;
            SchemaTemplate shape = FindHistory(available, next.SchemaId, next.Version)[0];
            foreach (SchemaTemplateField field in shape.Fields) {
                if (IsBinaryReference(field.TypeTagValue)) return true;
                if (field.InlineSchema.HasValue) pending.Push(field.InlineSchema.Value);
            }
        }
        return false;
    }

    private static void AppendInlineCaptureType(
        StringBuilder source, DurableTypeAnalysis type, BinaryVersionModel current) {
        bool hasNamespace = !type.Symbol.ContainingNamespace.IsGlobalNamespace;
        if (hasNamespace) source.Append("namespace ")
            .Append(type.Symbol.ContainingNamespace.ToDisplayString(QualifiedNameFormat)).AppendLine(" {");
        string indent = hasNamespace ? "    " : string.Empty;
        source.Append(indent).Append("partial struct ").Append(EscapeIdentifier(type.Symbol.Name)).AppendLine(" {");
        source.Append(indent).Append("    internal static class ").Append(GeneratedStateTypeName).AppendLine(" {");
        AppendBinaryCapture(source, type, current, indent + "        ", false);
        AppendInlineHydrate(source, type, current, indent + "        ");
        source.Append(indent).AppendLine("    }");
        source.Append(indent).AppendLine("}");
        if (hasNamespace) source.AppendLine("}");
    }

    private static void AppendRequiredInlineHelpers(
        StringBuilder source, List<DurableTypeAnalysis> types,
        Dictionary<ISymbol, List<BinaryVersionModel>> layouts, List<SchemaTemplate> available) {
        SortedDictionary<string, SchemaReference> required = new(StringComparer.Ordinal);
        Stack<SchemaReference> pending = new();
        foreach (DurableTypeAnalysis type in types) {
            if (!layouts.TryGetValue(type.Symbol, out List<BinaryVersionModel>? versions)) continue;
            foreach (BinaryVersionModel version in versions) {
                if (type.IsInline) pending.Push(new SchemaReference(type.SchemaId, version.Version));
                foreach (BinaryFieldModel field in version.Fields) {
                    if (field.InlineSchema.HasValue) pending.Push(field.InlineSchema.Value);
                }
            }
        }
        while (pending.Count > 0) {
            SchemaReference reference = pending.Pop();
            string name = InlineHelperName(reference);
            if (required.ContainsKey(name)) continue;
            required.Add(name, reference);
            SchemaTemplate shape = FindHistory(available, reference.SchemaId, reference.Version)[0];
            foreach (SchemaTemplateField field in shape.Fields) {
                if (field.InlineSchema.HasValue) pending.Push(field.InlineSchema.Value);
            }
        }
        if (required.Count == 0) return;
        source.AppendLine("namespace Atelia.DurableGraph.Generated {");
        foreach (KeyValuePair<string, SchemaReference> pair in required) {
            SchemaTemplate shape = FindHistory(available, pair.Value.SchemaId, pair.Value.Version)[0];
            List<BinaryFieldModel> fields = new();
            AppendBinaryFields(shape, available, fields, 0);
            BinaryVersionModel version = new(shape.Version, fields);
            source.Append("    internal static class ").Append(pair.Key.Substring(pair.Key.LastIndexOf('.') + 1)).AppendLine(" {");
            source.Append("        private static readonly global::Atelia.DurableGraph.Schema.DurableSchema ExactSchema = ");
            AppendSchemaExpression(source, shape, available);
            source.AppendLine(";");
            AppendBinaryDto(source, null, version, "        ");
            AppendBinaryDtoWrite(source, version, "        ");
            AppendBinaryPrepareBase(source, version, "        ");
            AppendBinaryDtoRead(source, version, "        ");
            AppendBinaryPrepareDelta(source, version, "        ");
            AppendBinaryStateEquality(source, version, "        ");
            AppendBinaryApplyDelta(source, version, "        ", inlineHelper: true);
            AppendBinaryStringReferenceValidation(source, version, "        ");
            AppendBinaryReferenceTraversal(source, version, "        ");
            source.AppendLine("    }");
        }
        source.AppendLine("}");
    }
}
