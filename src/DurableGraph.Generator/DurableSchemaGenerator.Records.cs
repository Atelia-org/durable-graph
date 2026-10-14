using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Atelia.DurableGraph.Generator;

public sealed partial class DurableGraphGenerator {
    private static readonly DiagnosticDescriptor InvalidRecordStorage = new(
        id: "DG0021",
        title: "Invalid record storage classification",
        messageFormat: "Record '{0}' has unsupported or incorrectly classified storage: {1}",
        category: "DurableGraph.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static bool IsRecordBackingField(IFieldSymbol field) =>
        field.IsImplicitlyDeclared && field.AssociatedSymbol is IPropertySymbol;

    private static bool ReportRecordStorageErrors(SourceProductionContext context, INamedTypeSymbol type,
        List<IFieldSymbol> fields, Compilation compilation) {
        bool errors = false;
        HashSet<(SyntaxTree Tree, TextSpan Span)> classified = new();
        foreach (IFieldSymbol field in fields) {
            if (!field.IsStatic && field.IsImplicitlyDeclared && !IsRecordBackingField(field)) {
                Report(field, "the implicit field '" + field.Name + "' is not property backing storage");
            }
            foreach (AttributeData attribute in field.GetAttributes()) {
                if (IsStorageAttributeType(attribute.AttributeClass) && attribute.ApplicationSyntaxReference is SyntaxReference syntax) {
                    classified.Add((syntax.SyntaxTree, syntax.Span));
                }
            }
        }

        // Roslyn exposes field-like events as events, not as their implicit storage fields.
        foreach (IEventSymbol member in type.GetMembers().OfType<IEventSymbol>()) {
            if (!member.IsStatic && member.DeclaringSyntaxReferences.Any(reference =>
                reference.GetSyntax(context.CancellationToken).Parent?.Parent is EventFieldDeclarationSyntax)) {
                Report(member, "field-like event storage is not supported");
            }
        }

        foreach (SyntaxReference reference in type.DeclaringSyntaxReferences) {
            SyntaxNode declaration = reference.GetSyntax(context.CancellationToken);
            SemanticModel semantic = compilation.GetSemanticModel(declaration.SyntaxTree);
            foreach (AttributeSyntax attribute in declaration.DescendantNodes().OfType<AttributeSyntax>()) {
                // Do not inspect nested declarations as storage owned by this record.
                if (attribute.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault() != declaration ||
                    classified.Contains((attribute.SyntaxTree, attribute.Span))) continue;
                if (!IsStorageAttribute(semantic, attribute, context.CancellationToken)) continue;
                context.ReportDiagnostic(Diagnostic.Create(InvalidRecordStorage, attribute.GetLocation(),
                    type.ToDisplayString(QualifiedNameFormat),
                    "DurableField/Transient must target an actual field; use field: on a property with backing storage"));
                errors = true;
            }
        }
        return errors;

        void Report(ISymbol member, string reason) {
            context.ReportDiagnostic(Diagnostic.Create(InvalidRecordStorage, GetSourceLocation(member),
                type.ToDisplayString(QualifiedNameFormat), reason));
            errors = true;
        }
    }

    private static bool IsStorageAttributeType(INamedTypeSymbol? type) =>
        HasMetadataName(type, DurableFieldAttributeMetadataName) || HasMetadataName(type, TransientAttributeMetadataName);

    private static bool IsStorageAttribute(SemanticModel semantic, AttributeSyntax attribute,
        System.Threading.CancellationToken cancellationToken) {
        // Even ignored field targets retain attribute symbol information. Let Roslyn own
        // alias/suffix resolution; ambiguity involving a storage attribute must not hide it.
        SymbolInfo info = semantic.GetSymbolInfo(attribute, cancellationToken);
        return IsStorageAttributeSymbol(info.Symbol) || info.CandidateSymbols.Any(IsStorageAttributeSymbol);
    }

    private static bool IsStorageAttributeSymbol(ISymbol? symbol) {
        if (symbol is IAliasSymbol alias) symbol = alias.Target;
        return IsStorageAttributeType(symbol is IMethodSymbol constructor ? constructor.ContainingType : symbol as INamedTypeSymbol);
    }

    private static bool ReportRecordHelperCollisions(SourceProductionContext context, INamedTypeSymbol type,
        string schemaId, List<DurableFieldAnalysis> fields) {
        List<string> names = new() { "__DurableCapture", "__DurableHydrate", "__DurableCreateCurrent", "__DurableCreateTyped", "__DurableProjection" };
        foreach (DurableFieldAnalysis field in fields) {
            string suffix = FamilyName(schemaId) + "_" + Number(field.FieldId);
            names.Add("__DurableRead_" + suffix);
            names.Add("__DurableWrite_" + suffix);
            if (field.Symbol.IsReadOnly || IsRecordBackingField(field.Symbol)) names.Add("__DurableReadonly_" + suffix);
        }
        bool errors = false;
        foreach (string name in names) {
            var members = type.GetMembers(name);
            if (type.Name != name && members.IsEmpty) continue;
            ReportInvalidGeneratedState(context, type, "the generated helper name '" + name + "' is reserved",
                members.IsEmpty ? GetSourceLocation(type) : GetSourceLocation(members[0]));
            errors = true;
        }
        return errors;
    }
}
