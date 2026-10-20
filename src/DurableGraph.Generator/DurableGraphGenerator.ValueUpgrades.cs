using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Atelia.DurableGraph.Generator;

public sealed partial class DurableGraphGenerator {
    private const string ValueRuleSetAttributeName = "Atelia.DurableGraph.ValueUpgradeRuleSetAttribute";
    private const string ValueProviderAttributeName = "Atelia.DurableGraph.DurableValueUpgradeAttribute";
    private const string UpgradeDependencyAttributeName = "Atelia.DurableGraph.UpgradeDependencyAttribute";

    private static IEnumerable<INamedTypeSymbol> GetValueRuleSets(Compilation compilation) =>
        EnumerateSourceTypes(compilation.Assembly.GlobalNamespace)
            .Where(type => GetAttribute(type.GetAttributes(), ValueRuleSetAttributeName) is not null)
            .OrderBy(type => type.ToDisplayString(), StringComparer.Ordinal);

    private static IEnumerable<IMethodSymbol> GetValueUpgradeMethods(Compilation compilation) =>
        EnumerateSourceTypes(compilation.Assembly.GlobalNamespace).SelectMany(type => type.GetMembers().OfType<IMethodSymbol>())
            .Where(method => GetAttribute(method.GetAttributes(), ValueProviderAttributeName) is not null)
            .OrderBy(method => method.ContainingType.ToDisplayString() + "." + method.Name, StringComparer.Ordinal);

    private static IEnumerable<AttributeData> GetUpgradeDependencies(IMethodSymbol method) =>
        method.GetAttributes().Where(attribute => attribute.AttributeClass is INamedTypeSymbol type && HasMetadataName(type, UpgradeDependencyAttributeName));

    private static bool HasUpgradeDependencies(IMethodSymbol method) => GetUpgradeDependencies(method).Any();

    private static bool ValidRuleSet(INamedTypeSymbol? type, Compilation compilation) => type is not null && type.TypeKind == TypeKind.Class &&
        type.Arity == 0 && type.ContainingType is null && type.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal &&
        SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly) &&
        GetAttribute(type.GetAttributes(), ValueRuleSetAttributeName) is not null;

    private static bool ValidateValueUpgradeRegistrations(SourceProductionContext context, List<DurableTypeAnalysis> types,
        List<SchemaTemplate> history, Compilation compilation, out bool hasRegistrations) {
        hasRegistrations = false;
        bool valid = true;
        foreach (SyntaxTree tree in compilation.SyntaxTrees) {
            SemanticModel semantic = compilation.GetSemanticModel(tree);
            foreach (TypeDeclarationSyntax syntax in tree.GetRoot(context.CancellationToken).DescendantNodes().OfType<TypeDeclarationSyntax>()) {
                if (semantic.GetDeclaredSymbol(syntax, context.CancellationToken) is not INamedTypeSymbol marker ||
                    GetAttribute(marker.GetAttributes(), ValueRuleSetAttributeName) is null) continue;
                hasRegistrations = true;
                if (!ValidRuleSet(marker, compilation)) {
                    ReportInvalidGeneratedState(context, marker,
                        "A value upgrade rule set requires an accessible top-level non-generic class marker", GetSourceLocation(marker));
                    valid = false;
                }
            }
            foreach (MethodDeclarationSyntax syntax in tree.GetRoot(context.CancellationToken).DescendantNodes().OfType<MethodDeclarationSyntax>()) {
                if (syntax.AttributeLists.Count == 0 || semantic.GetDeclaredSymbol(syntax, context.CancellationToken) is not IMethodSymbol method) continue;
                AttributeData? value = GetAttribute(method.GetAttributes(), ValueProviderAttributeName);
                AttributeData[] dependencies = GetUpgradeDependencies(method).ToArray();
                if (value is null && dependencies.Length == 0) continue;
                hasRegistrations = true;
                bool owner = GetAttribute(method.GetAttributes(), "Atelia.DurableGraph.DurableUpgradeAttribute") is not null ||
                    types.Any(type => !type.IsInline && SymbolEqualityComparer.Default.Equals(type.Symbol, method.ContainingType) &&
                        IsBinaryUpgradeName(method.Name, type.Version));
                if ((value is not null && owner) || (value is null && !owner)) {
                    ReportInvalidGeneratedState(context, method.ContainingType,
                        "Value dependencies belong to exactly one declared owner or value upgrade method", GetSourceLocation(method));
                    valid = false;
                }
                if (dependencies.Length != 0 && method.Parameters.Length != 3) {
                    ReportInvalidGeneratedState(context, method.ContainingType,
                        "A two-parameter owner upgrade cannot declare value dependencies; add UpgradeContext", GetSourceLocation(method));
                    valid = false;
                }
                HashSet<string> keys = new(StringComparer.Ordinal);
                foreach (AttributeData dependency in dependencies) {
                    var arguments = dependency.ConstructorArguments;
                    bool accepted = arguments.Length == 6 && arguments[0].Value is string key &&
                        !string.IsNullOrWhiteSpace(key) && (SyntaxFacts.IsValidIdentifier(key) || SyntaxFacts.GetKeywordKind(key) != SyntaxKind.None) &&
                        keys.Add(key) && arguments[1].Value is INamedTypeSymbol rule && ValidRuleSet(rule, compilation) &&
                        arguments[2].Value is string source && !string.IsNullOrWhiteSpace(source) && arguments[3].Value is int sourceId && sourceId > 0 &&
                        arguments[4].Value is string target && !string.IsNullOrWhiteSpace(target) && arguments[5].Value is int targetId && targetId > 0;
                    if (!accepted) {
                        ReportInvalidGeneratedState(context, method.ContainingType,
                            "UpgradeDependency requires a unique C# identifier key, a rule set declared in the same compilation, and positive declaration-segment field selectors", GetSourceLocation(method));
                        valid = false;
                    }
                }
                if (value is null) continue;
                var registration = value.ConstructorArguments;
                bool signature = method.IsStatic && method.ReturnsVoid && method.Parameters.Length == 3 &&
                    method.Parameters[0].RefKind == RefKind.In && method.Parameters[1].RefKind == RefKind.Out &&
                    method.Parameters[2].RefKind == RefKind.None && HasMetadataName(method.Parameters[2].Type as INamedTypeSymbol, "Atelia.DurableGraph.UpgradeContext") &&
                    method.ContainingType.IsStatic && method.ContainingType.Arity == 0 && method.ContainingType.ContainingType is null &&
                    method.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal &&
                    method.ContainingType.GetMembers(method.Name).Length == 1 && method.DeclaringSyntaxReferences.Length == 1;
                bool endpoints = registration.Length == 4 && registration[0].Value is INamedTypeSymbol rules && ValidRuleSet(rules, compilation) &&
                    registration[1].Value is string id && registration[2].Value is int from && registration[3].Value is int to &&
                    TryGetInlineVersion(id, from, types, history, out int sourceArity) &&
                    TryGetInlineVersion(id, to, types, history, out int targetArity) && sourceArity == targetArity;
                if (!signature || !endpoints) {
                    ReportInvalidGeneratedState(context, method.ContainingType,
                        "DurableValueUpgrade requires a rule set declared in the same compilation, retained inline-family endpoints, and one accessible static void (in prior, out next, UpgradeContext) method in a top-level non-generic static host", GetSourceLocation(method));
                    valid = false;
                }
            }
        }
        return valid;
    }

    private static bool TryGetInlineVersion(string id, int version, List<DurableTypeAnalysis> types,
        List<SchemaTemplate> history, out int arity) {
        foreach (DurableTypeAnalysis type in types) {
            if (type.SchemaId == id && type.Version == version && type.IsInline) { arity = type.Arity; return true; }
        }
        foreach (SchemaTemplate shape in history) {
            if (shape.SchemaId == id && shape.Version == version && shape.Kind == 2) { arity = shape.Arity; return true; }
        }
        arity = 0;
        return false;
    }

    private static string SymbolHex(ISymbol symbol) => BitConverter.ToString(StrictUtf8.GetBytes(symbol.ToDisplayString())).Replace("-", string.Empty);
    private static string ValueRuleSetClassName(INamedTypeSymbol marker) => "ValueUpgradeRules_" + SymbolHex(marker);
    private static string UpgradeSlotsClassName(IMethodSymbol method) => "UpgradeSlots_" + SymbolHex(method.ContainingType) + "_" +
        BitConverter.ToString(StrictUtf8.GetBytes(method.Name)).Replace("-", string.Empty);

    private static void AppendUpgradeDependencies(StringBuilder output, IMethodSymbol method) {
        AttributeData[] dependencies = GetUpgradeDependencies(method).ToArray();
        if (dependencies.Length == 0) return;
        output.Append(", dependencies: new ").Append(RuntimeName).Append("StateUpgradeDependency[] {");
        foreach (AttributeData dependency in dependencies) {
            var args = dependency.ConstructorArguments;
            output.Append(" new(").Append(Literal((string)args[0].Value!)).Append(", typeof(")
                .Append(((INamedTypeSymbol)args[1].Value!).ToDisplayString(GenericQualifiedNameFormat)).Append("), new(")
                .Append(Literal((string)args[2].Value!)).Append(", ").Append((int)args[3].Value!).Append("), new(")
                .Append(Literal((string)args[4].Value!)).Append(", ").Append((int)args[5].Value!).Append(")),");
        }
        output.Append(" }");
    }

    private static void AppendValueUpgradeRuleSets(StringBuilder output, List<DurableTypeAnalysis> types,
        List<SchemaTemplate> history, Compilation compilation, SourceProductionContext context) {
        IMethodSymbol[] providers = GetValueUpgradeMethods(compilation).ToArray();
        foreach (INamedTypeSymbol rules in GetValueRuleSets(compilation)) {
            AttributeData attribute = GetAttribute(rules.GetAttributes(), ValueRuleSetAttributeName)!;
            bool keepExact = attribute.NamedArguments.Any(argument => argument.Key == "AllowKeepExact" && argument.Value.Value is true);
            bool nullableLifting = attribute.NamedArguments.Any(argument => argument.Key == "AllowNullableLifting" && argument.Value.Value is true);
            output.Append("public static class ").Append(ValueRuleSetClassName(rules)).AppendLine(" {");
            output.Append("    public static readonly ").Append(RuntimeName).Append("StateValueUpgradeRuleSet Rules = new(typeof(")
                .Append(rules.ToDisplayString(GenericQualifiedNameFormat)).Append("), new ").Append(RuntimeName).AppendLine("StateValueUpgradeProvider[] {");
            foreach (IMethodSymbol method in providers) {
                var args = GetAttribute(method.GetAttributes(), ValueProviderAttributeName)!.ConstructorArguments;
                if (!SymbolEqualityComparer.Default.Equals((INamedTypeSymbol)args[0].Value!, rules)) continue;
                string id = (string)args[1].Value!;
                int from = (int)args[2].Value!, to = (int)args[3].Value!;
                TryGetInlineVersion(id, from, types, history, out int arity);
                string adapter = AppendValueUpgradeAdapter(context, method);
                output.Append("        new(");
                AppendValueUpgradeNominal(output, id, arity);
                output.Append(", ").Append(from).Append(", ");
                AppendValueUpgradeNominal(output, id, arity);
                output.Append(", ").Append(to).Append(", typeof(").Append(adapter).Append(").GetMethod(\"Invoke\", global::System.Reflection.BindingFlags.Static | global::System.Reflection.BindingFlags.NonPublic)!");
                AppendUpgradeDependencies(output, method);
                output.AppendLine("),");
            }
            output.Append("    }, allowKeepExact: ").Append(keepExact ? "true" : "false")
                .Append(", allowNullableLifting: ").Append(nullableLifting ? "true" : "false").AppendLine(");");
            output.AppendLine("}");
        }
        foreach (INamedTypeSymbol host in EnumerateSourceTypes(compilation.Assembly.GlobalNamespace)) {
            foreach (IMethodSymbol method in host.GetMembers().OfType<IMethodSymbol>().Where(HasUpgradeDependencies)) {
                output.Append("public static class ").Append(UpgradeSlotsClassName(method)).AppendLine(" {");
                foreach (AttributeData dependency in GetUpgradeDependencies(method)) {
                    string key = (string)dependency.ConstructorArguments[0].Value!;
                    output.Append("    public const string ").Append(EscapeIdentifier(key)).Append(" = ").Append(Literal(key)).AppendLine(";");
                }
                output.AppendLine("}");
            }
        }
    }

    private static void AppendValueUpgradeNominal(StringBuilder output, string id, int arity) {
        output.Append(SchemaName).Append("TypeExpr.Named(").Append(Literal(id));
        for (int ordinal = 0; ordinal < arity; ordinal++) output.Append(", ").Append(SchemaName).Append("TypeExpr.Parameter(").Append(ordinal).Append(')');
        output.Append(')');
    }

    // Generated DTO symbols are absent from the initial compilation. A source-shaped typed
    // adapter preserves aliases and constraints, leaving the final compiler to check the call.
    private static string AppendValueUpgradeAdapter(SourceProductionContext context, IMethodSymbol method) {
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
        string name = "__DurableValue_" + SymbolHex(method.ContainingType) + "_" +
            BitConverter.ToString(StrictUtf8.GetBytes(method.Name)).Replace("-", string.Empty);
        output.Append("internal static class ").Append(name).AppendLine(" {");
        output.Append("    internal static void Invoke").Append(syntax.TypeParameterList?.ToString()).Append("(in ")
            .Append(syntax.ParameterList.Parameters[0].Type!.ToString()).Append(" prior, out ")
            .Append(syntax.ParameterList.Parameters[1].Type!.ToString()).Append(" next, ").Append(RootName).Append("UpgradeContext context)");
        foreach (TypeParameterConstraintClauseSyntax constraint in syntax.ConstraintClauses) output.Append(' ').Append(constraint.ToString());
        output.AppendLine(" {");
        output.Append("        ").Append(method.ContainingType.ToDisplayString(GenericQualifiedNameFormat)).Append('.')
            .Append(EscapeIdentifier(method.Name)).Append(GenericList(method.TypeParameters.Select(parameter => EscapeIdentifier(parameter.Name))))
            .AppendLine("(in prior, out next, context);");
        output.AppendLine("    }");
        output.AppendLine("}");
        if (hasNamespace) output.AppendLine("}");
        context.AddSource("DurableValueUpgrade." + name + ".g.cs", SourceText.From(output.ToString().Replace("\r\n", "\n"), Encoding.UTF8));
        return "global::" + (hasNamespace ? containingNamespace + "." : string.Empty) + name;
    }
}
