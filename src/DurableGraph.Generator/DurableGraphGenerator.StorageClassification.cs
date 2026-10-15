using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Atelia.DurableGraph.Generator;

public sealed partial class DurableGraphGenerator {
    private static readonly DiagnosticDescriptor InvalidExplicitStorageClassification = new(
        id: "DG0024",
        title: "Invalid explicit storage classification",
        messageFormat: "Durable type '{0}' has an invalid explicit storage classification: {1}",
        category: "DurableGraph.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static bool ReportExplicitStorageClassificationErrors(SourceProductionContext context,
        INamedTypeSymbol type, List<IFieldSymbol> explicitFields, Compilation compilation) {
        bool errors = false;
        HashSet<(SyntaxTree Tree, TextSpan Span)> classified = new();

        // The existing field loop owns explicit storage, including its error diagnostics.
        foreach (IFieldSymbol field in explicitFields) MarkClassified(field.GetAttributes());

        foreach (IFieldSymbol field in type.GetMembers().OfType<IFieldSymbol>()) {
            if (!field.IsImplicitlyDeclared) continue;
            AttributeData? durable = GetAttribute(field.GetAttributes(), DurableFieldAttributeMetadataName);
            AttributeData? transient = GetAttribute(field.GetAttributes(), TransientAttributeMetadataName);
            MarkClassified(field.GetAttributes());
            string name = field.AssociatedSymbol?.Name ?? field.Name;
            CheckStorage(field, name, durable is not null, transient is not null,
                GetAttributeLocation(durable ?? transient) ?? GetSourceLocation(field));
        }

        // Roslyn does not expose an event's backing field as an IFieldSymbol. Only an
        // ordinary field-like declaration has that target; partial/custom, abstract,
        // and extern declarations can contain ignored field: attributes without it.
        foreach (IEventSymbol member in type.GetMembers().OfType<IEventSymbol>()) {
            foreach (SyntaxReference reference in member.DeclaringSyntaxReferences) {
                if (reference.GetSyntax(context.CancellationToken).Parent?.Parent is not EventFieldDeclarationSyntax declaration ||
                    declaration.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.AbstractKeyword) ||
                        modifier.IsKind(SyntaxKind.ExternKeyword) || modifier.IsKind(SyntaxKind.PartialKeyword))) continue;
                SemanticModel semantic = compilation.GetSemanticModel(declaration.SyntaxTree);
                bool durable = false, transient = false;
                Location? location = null;
                foreach (AttributeListSyntax list in declaration.AttributeLists) {
                    if (list.Target?.Identifier.ValueText != "field") continue;
                    foreach (AttributeSyntax attribute in list.Attributes) {
                        bool isDurable = IsAttribute(semantic, attribute, DurableFieldAttributeMetadataName, context.CancellationToken);
                        bool isTransient = IsAttribute(semantic, attribute, TransientAttributeMetadataName, context.CancellationToken);
                        if (!isDurable && !isTransient) continue;
                        durable |= isDurable;
                        transient |= isTransient;
                        if (location is null || isDurable) location = attribute.GetLocation();
                        classified.Add((attribute.SyntaxTree, attribute.Span));
                    }
                }
                CheckStorage(member, member.Name, durable, transient, location ?? GetSourceLocation(member));
            }
        }

        foreach (SyntaxReference reference in type.DeclaringSyntaxReferences) {
            SyntaxNode declaration = reference.GetSyntax(context.CancellationToken);
            SemanticModel semantic = compilation.GetSemanticModel(declaration.SyntaxTree);
            foreach (AttributeSyntax attribute in declaration.DescendantNodes().OfType<AttributeSyntax>()) {
                if (attribute.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault() != declaration ||
                    classified.Contains((attribute.SyntaxTree, attribute.Span)) ||
                    !IsStorageAttribute(semantic, attribute, context.CancellationToken)) continue;
                ReportInvalid(attribute.GetLocation(),
                    "DurableField/Transient must target actual field storage; use an explicit field for persisted state");
            }
        }
        return errors;

        void MarkClassified(IEnumerable<AttributeData> attributes) {
            foreach (AttributeData attribute in attributes) {
                if (IsStorageAttributeType(attribute.AttributeClass) && attribute.ApplicationSyntaxReference is SyntaxReference syntax) {
                    classified.Add((syntax.SyntaxTree, syntax.Span));
                }
            }
        }

        void CheckStorage(ISymbol member, string name, bool durable, bool transient, Location location) {
            if (!durable && !transient) return;
            if (member.IsStatic) {
                context.ReportDiagnostic(Diagnostic.Create(ClassifiedStaticField, location, name));
                errors = true;
            } else if (durable && transient) {
                context.ReportDiagnostic(Diagnostic.Create(ConflictingFieldClassification, location, name));
                errors = true;
            } else if (durable) {
                ReportInvalid(location, "DurableField on '" + name + "' targets unsupported implicit storage; use an explicit field");
            }
            // Real instance Transient storage is excluded without entering durableFields.
        }

        void ReportInvalid(Location location, string reason) {
            context.ReportDiagnostic(Diagnostic.Create(InvalidExplicitStorageClassification, location,
                type.ToDisplayString(QualifiedNameFormat), reason));
            errors = true;
        }
    }

    private static bool IsAttribute(SemanticModel semantic, AttributeSyntax attribute, string metadataName,
        System.Threading.CancellationToken cancellationToken) {
        SymbolInfo info = semantic.GetSymbolInfo(attribute, cancellationToken);
        return Matches(info.Symbol) || info.CandidateSymbols.Any(Matches);

        bool Matches(ISymbol? symbol) => HasMetadataName(GetAttributeType(symbol), metadataName);
    }
}
