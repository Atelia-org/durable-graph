using System.Text;
using Microsoft.CodeAnalysis;

namespace Atelia.DurableGraph.Generator;

public sealed partial class DurableGraphGenerator {
    private static DurableTypeAnalysis? AnalyzeDurableEnum(SourceProductionContext context, INamedTypeSymbol type,
        string? schemaId, int version, bool hasErrors, INamedTypeSymbol? listType, INamedTypeSymbol? dictionaryType) {
        foreach (IFieldSymbol constant in GetDirectFields(type)) {
            if (!HasAttribute(constant.GetAttributes(), DurableFieldAttributeMetadataName) &&
                !HasAttribute(constant.GetAttributes(), TransientAttributeMetadataName)) continue;
            context.ReportDiagnostic(Diagnostic.Create(ClassifiedStaticField, GetSourceLocation(constant), constant.Name));
            hasErrors = true;
        }
        if (hasErrors || type.EnumUnderlyingType is null) return null;
        if (!TryGetTypeTag(type.EnumUnderlyingType, null, out string? tag, out int number, out string? name)) return null;
        // The schema describes one integer value; enum constants and the CLR's value__ field are not its layout.
        return new DurableTypeAnalysis(type, schemaId!, version,
            new() { new DurableFieldAnalysis(null, 1, tag!, number, name!) }, listType, dictionaryType);
    }

    private static string EnumProjectionHost(string schemaId) =>
        "global::Atelia.DurableGraph.Generated.EnumProjection_" + FamilyName(schemaId);

    private static string CurrentInlineProjection(ITypeSymbol domainType) {
        if (domainType.TypeKind != TypeKind.Enum) return domainType.ToDisplayString(GenericQualifiedNameFormat) + ".__DurableProjection";
        AttributeData attribute = GetAttribute(domainType.GetAttributes(), DurableTypeAttributeMetadataName)!;
        return EnumProjectionHost((string)attribute.ConstructorArguments[0].Value!) + ".Projection";
    }

    private static void AppendEnumDomainProjection(StringBuilder output, DurableTypeAnalysis type, FamilyLayout layout) {
        string domain = type.Symbol.ToDisplayString(GenericQualifiedNameFormat);
        string dto = CurrentDto(type, layout);
        string body = CurrentBody(type, layout);
        string scalar = type.Symbol.EnumUnderlyingType!.ToDisplayString(GenericQualifiedNameFormat);
        output.AppendLine("namespace Atelia.DurableGraph.Generated {");
        output.Append(type.Symbol.DeclaredAccessibility == Accessibility.Public ? "public" : "internal")
            .Append(" static class EnumProjection_").Append(FamilyName(type.SchemaId)).AppendLine(" {");
        output.Append("    internal static ").Append(RuntimeName).Append("StateValueBinding CreateCurrent(")
            .Append(RuntimeName).AppendLine("StateBindingContext context) {");
        output.Append("        var schema = new ").Append(SchemaName).Append("DurableSchema(context.GetTypeExpr(typeof(").Append(domain)
            .Append(")), ").Append(type.Version).Append(", new ").Append(SchemaName).Append("DurableFieldInfo[] { new(1, ")
            .Append(SchemaName).Append("TypeTag.").Append(type.Fields[0].TypeTag).Append(") }, baseSchema: null, kind: ")
            .Append(SchemaName).AppendLine("SchemaKind.InlineValue);");
        output.AppendLine("        context.BindSchema(schema);");
        output.Append("        return new ").Append(RuntimeName).Append("StateValueBinding(new ").Append(SchemaName)
            .Append("DurableFieldInfo(1, ").Append(SchemaName).Append("TypeTag.InlineValue, inlineSchema: schema), typeof(")
            .Append(dto).Append("), typeof(").Append(body).Append("), typeof(").Append(domain).AppendLine("), typeof(Projection));");
        output.AppendLine("    }");
        output.Append("    public readonly struct Projection : ").Append(RuntimeName).Append("IValueProjection<")
            .Append(domain).Append(", ").Append(dto).AppendLine("> {");
        output.Append("        public static ").Append(dto).Append(" Capture(in ").Append(domain).Append(" value, ")
            .Append(RuntimeName).Append("CaptureContext context, ").Append(SchemaName).Append("DurableFieldInfo slot) => new((")
            .Append(scalar).AppendLine(")value);");
        output.Append("        public static void Hydrate(ref ").Append(domain).Append(" target, in ").Append(dto).Append(" state, ")
            .Append(RuntimeName).Append("ObjectReadTable objects, ").Append(SchemaName).Append("DurableFieldInfo slot) => target = (")
            .Append(domain).Append(")state.").Append(layout.Fields[0].Name).AppendLine(";");
        output.AppendLine("    }");
        output.AppendLine("}");
        output.AppendLine("}");
    }
}
