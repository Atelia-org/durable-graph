using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Atelia.DurableGraph.SchemaHistory;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Atelia.DurableGraph.Generator;

public sealed partial class DurableGraphGenerator {
    private static bool ValidateUpgradeRegistrations(SourceProductionContext context, List<DurableTypeAnalysis> types,
        Compilation compilation, out bool hasRegistrations) {
        hasRegistrations = false;
        bool valid = true;
        foreach (SyntaxTree tree in compilation.SyntaxTrees) {
            SemanticModel semantic = compilation.GetSemanticModel(tree);
            foreach (MethodDeclarationSyntax syntax in tree.GetRoot(context.CancellationToken).DescendantNodes().OfType<MethodDeclarationSyntax>()) {
                if (syntax.AttributeLists.Count == 0 || semantic.GetDeclaredSymbol(syntax, context.CancellationToken) is not IMethodSymbol method) continue;
                AttributeData? registration = GetAttribute(method.GetAttributes(), "Atelia.DurableGraph.DurableUpgradeAttribute");
                if (registration is null) continue;
                hasRegistrations = true;
                INamedTypeSymbol host = method.ContainingType;
                bool ownerSupported = registration.ConstructorArguments.Length == 2 &&
                    registration.ConstructorArguments[0].Value is INamedTypeSymbol owner &&
                    types.Exists(type => !type.IsInline && SymbolEqualityComparer.Default.Equals(type.Symbol, owner.OriginalDefinition));
                if (!ownerSupported || host.Arity != 0 || host.ContainingType is not null || !host.IsStatic ||
                    method.DeclaredAccessibility is not (Accessibility.Internal or Accessibility.Public)) {
                    ReportInvalidGeneratedState(context, host,
                        "DurableUpgrade requires a supported durable class owner and an accessible method in a top-level non-generic static host",
                        GetSourceLocation(method));
                    valid = false;
                }
            }
        }
        return valid;
    }

    private static void AppendFamilyUpgradeProviders(StringBuilder output, DurableTypeAnalysis? domain,
        List<FamilyLayout> versions, Compilation compilation, SourceProductionContext context) {
        if (!domain.HasValue || domain.Value.IsInline) return;
        DurableTypeAnalysis owner = domain.Value;
        List<(IMethodSymbol Method, int Version, TypePattern? ClosedOwner, bool Implicit)> methods = new();
        foreach (INamedTypeSymbol host in EnumerateSourceTypes(compilation.Assembly.GlobalNamespace)) {
            foreach (IMethodSymbol method in host.GetMembers().OfType<IMethodSymbol>()) {
                AttributeData? registration = GetAttribute(method.GetAttributes(), "Atelia.DurableGraph.DurableUpgradeAttribute");
                if (registration is null || registration.ConstructorArguments.Length != 2 ||
                    registration.ConstructorArguments[0].Value is not INamedTypeSymbol registeredOwner ||
                    registration.ConstructorArguments[1].Value is not int fromVersion) continue;
                if (!SymbolEqualityComparer.Default.Equals(registeredOwner.OriginalDefinition, owner.Symbol)) continue;
                TypePattern? closed = registeredOwner.IsUnboundGenericType ? null : GetNamedTypePattern(registeredOwner, GetBclListType(compilation), GetBclDictionaryType(compilation));
                if (host.Arity != 0 || host.ContainingType is not null || !host.IsStatic ||
                    method.DeclaredAccessibility is not (Accessibility.Internal or Accessibility.Public)) {
                    ReportInvalidGeneratedState(context, owner.Symbol, "DurableUpgrade methods require an accessible static method in a top-level non-generic static class", GetSourceLocation(method));
                    continue;
                }
                if (!ValidateFamilyUpgradeSignature(context, owner, method, fromVersion, allowLegacy: false)) continue;
                methods.Add((method, fromVersion, closed, false));
            }
        }
        for (int fromVersion = 1; fromVersion < owner.Version; fromVersion++) {
            string name = BinaryUpgradeName(fromVersion);
            var members = owner.Symbol.GetMembers(name);
            if (members.IsEmpty) continue;
            if (owner.Arity != 0 || members.Length != 1 || members[0] is not IMethodSymbol method) {
                ReportInvalidGeneratedState(context, owner.Symbol, "Generic domain upgrades use DurableUpgrade methods in a non-generic static host; each implicit edge requires one method");
                continue;
            }
            if (ValidateFamilyUpgradeSignature(context, owner, method, fromVersion, allowLegacy: true)) methods.Add((method, fromVersion, null, true));
        }
        if (methods.Count == 0) return;
        output.Append(", upgrades: new ").Append(RuntimeName).AppendLine("StateUpgradeProvider[] {");
        for (int index = 0; index < methods.Count; index++) {
            var entry = methods[index];
            string adapter = AppendFamilyUpgradeAdapter(context, owner, entry.Method, entry.Version, index, entry.Implicit);
            output.Append("        new(").Append(Literal(owner.SchemaId)).Append(", ").Append(entry.Version).Append(", typeof(")
                .Append(adapter).Append(").GetMethod(").Append(Literal(entry.Implicit ? "__DurableUpgradeAdapter" + Number(entry.Version) : "Invoke"))
                .Append(", global::System.Reflection.BindingFlags.Static | global::System.Reflection.BindingFlags.Public | global::System.Reflection.BindingFlags.NonPublic)!");
            if (entry.ClosedOwner is not null) {
                output.Append(", closedOwner: ");
                AppendTypePatternExpression(output, entry.ClosedOwner);
            }
            AppendUpgradeDependencies(output, entry.Method);
            output.AppendLine("),");
        }
        output.Append("    }");
    }

    private static bool ValidateFamilyUpgradeSignature(SourceProductionContext context, DurableTypeAnalysis owner,
        IMethodSymbol method, int fromVersion, bool allowLegacy) {
        if (method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken) is MethodDeclarationSyntax declaration &&
            declaration.ParameterList.Parameters.Any(parameter => parameter.Type?.ToString().Contains("__DurableState") == true)) {
            ReportInvalidGeneratedState(context, owner.Symbol,
                "A generic-aware compilation names historical DTOs through " + FamilyType(owner.SchemaId) +
                "; replace the former domain-nested __DurableState aliases", GetSourceLocation(method));
            return false;
        }
        bool legacy = allowLegacy && method.Arity == 0 && method.Parameters.Length == 2;
        if (legacy && HasUpgradeDependencies(method)) {
            ReportInvalidGeneratedState(context, owner.Symbol,
                "A two-parameter owner upgrade cannot declare value dependencies; add UpgradeContext", GetSourceLocation(method));
            return false;
        }
        bool valid = fromVersion > 0 && fromVersion < owner.Version && method.IsStatic && method.ReturnsVoid &&
            method.Parameters.Length >= 2 && method.Parameters[0].RefKind == RefKind.In && method.Parameters[1].RefKind == RefKind.Out &&
            (legacy || (method.Parameters.Length == 3 && method.Parameters[2].RefKind == RefKind.None &&
                method.Parameters[2].Type is INamedTypeSymbol contextType && HasMetadataName(contextType, "Atelia.DurableGraph.UpgradeContext"))) &&
            method.ContainingType.GetMembers(method.Name).Length == 1 && method.DeclaringSyntaxReferences.Length == 1;
        if (!valid) ReportInvalidGeneratedState(context, owner.Symbol,
            "Upgrade requires one static void method with in prior, out next, UpgradeContext and a retained adjacent version; only existing non-generic implicit methods may omit Context", GetSourceLocation(method));
        return valid;
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateSourceTypes(INamespaceSymbol scope) {
        foreach (INamedTypeSymbol type in scope.GetTypeMembers()) yield return type;
        foreach (INamespaceSymbol child in scope.GetNamespaceMembers()) foreach (INamedTypeSymbol type in EnumerateSourceTypes(child)) yield return type;
    }

    // Initial generator symbols cannot resolve DTOs emitted during this pass. Preserve the
    // user's local type aliases in an isolated source and emit a typed forwarder; the final
    // compiler checks the invocation before the runtime ever inspects the method metadata.
    private static string AppendFamilyUpgradeAdapter(SourceProductionContext context, DurableTypeAnalysis owner,
        IMethodSymbol method, int fromVersion, int index, bool implicitMethod) {
        MethodDeclarationSyntax syntax = (MethodDeclarationSyntax)method.DeclaringSyntaxReferences[0].GetSyntax(context.CancellationToken);
        CompilationUnitSyntax root = (CompilationUnitSyntax)syntax.SyntaxTree.GetRoot(context.CancellationToken);
        StringBuilder output = new("// <auto-generated/>\n#nullable enable\n");
        foreach (UsingDirectiveSyntax directive in root.Usings) {
            if (directive.GlobalKeyword.RawKind == 0) output.AppendLine(directive.ToString());
        }
        string containingNamespace = method.ContainingNamespace.ToDisplayString();
        bool hasNamespace = !method.ContainingNamespace.IsGlobalNamespace;
        if (hasNamespace) output.Append("namespace ").Append(containingNamespace).AppendLine(" {");
        foreach (BaseNamespaceDeclarationSyntax declaration in syntax.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse()) {
            foreach (UsingDirectiveSyntax directive in declaration.Usings) output.AppendLine(directive.ToString());
        }
        string hostName = implicitMethod ? EscapeIdentifier(owner.Symbol.Name) : "__DurableUpgrade_" + FamilyName(owner.SchemaId) + "_" + Number(index);
        output.Append(implicitMethod ? (owner.Symbol.IsRecord ? "partial record class " : "partial class ") : "internal static class ").Append(hostName).AppendLine(" {");
        string adapterName = implicitMethod ? "__DurableUpgradeAdapter" + Number(fromVersion) : "Invoke";
        output.Append("    internal static void ").Append(adapterName).Append(syntax.TypeParameterList?.ToString()).Append("(in ")
            .Append(syntax.ParameterList.Parameters[0].Type!.ToString()).Append(" prior, out ")
            .Append(syntax.ParameterList.Parameters[1].Type!.ToString()).Append(" next, ").Append(RootName).Append("UpgradeContext context)");
        foreach (TypeParameterConstraintClauseSyntax constraint in syntax.ConstraintClauses) output.Append(' ').Append(constraint.ToString());
        output.AppendLine(" {");
        output.Append("        ").Append(method.ContainingType.ToDisplayString(GenericQualifiedNameFormat)).Append('.').Append(EscapeIdentifier(method.Name))
            .Append(GenericList(method.TypeParameters.Select(parameter => EscapeIdentifier(parameter.Name)))).Append("(in prior, out next");
        if (method.Parameters.Length == 3) output.Append(", context");
        output.AppendLine(");");
        output.AppendLine("    }");
        output.AppendLine("}");
        if (hasNamespace) output.AppendLine("}");
        context.AddSource("DurableUpgrade." + FamilyName(owner.SchemaId) + "." + Number(index) + ".g.cs",
            SourceText.From(output.ToString().Replace("\r\n", "\n"), Encoding.UTF8));
        return implicitMethod ? owner.Symbol.ToDisplayString(GenericQualifiedNameFormat) : "global::" + (hasNamespace ? containingNamespace + "." : string.Empty) + hostName;
    }
}
