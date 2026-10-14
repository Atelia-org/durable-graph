using Microsoft.CodeAnalysis;

namespace Atelia.DurableGraph.Generator;

public sealed partial class DurableGraphGenerator {
    // Nominal metadata admission is shared by reference and fixed-inline fields.
    // Fixed inline additionally requires validated exported history and public execution helpers.
    private static bool HasExternalDurableNominalShape(INamedTypeSymbol type, Compilation compilation) {
        if (type.DeclaringSyntaxReferences.Length != 0 || type.DeclaredAccessibility != Accessibility.Public ||
            type.ContainingType is not null || type.Arity > 32 || type.IsRefLikeType ||
            (type.TypeKind != TypeKind.Class && type.TypeKind != TypeKind.Struct && type.TypeKind != TypeKind.Enum)) return false;
        foreach (ITypeParameterSymbol parameter in type.TypeParameters) {
            if (parameter.AllowsRefLikeType) return false;
        }

        if (!HasDurableContract(type, compilation)) return false;
        AttributeData attribute = GetAttribute(type.GetAttributes(), DurableTypeAttributeMetadataName)!;
        return attribute.ConstructorArguments.Length == 2 &&
            attribute.ConstructorArguments[0].Value is string id && !string.IsNullOrWhiteSpace(id) && CanEncodeStrictUtf8(id) &&
            attribute.ConstructorArguments[1].Value is int version && version > 0;
    }

    // A metadata name is a lookup key, not proof of contract identity. Apply the same
    // admission to source declarations, referenced slots, and imported model types.
    internal static bool HasDurableContract(INamedTypeSymbol type, Compilation compilation) {
        if (type.TypeKind != TypeKind.Class && type.TypeKind != TypeKind.Struct && type.TypeKind != TypeKind.Enum) return false;
        INamedTypeSymbol? attributeType = compilation.GetTypeByMetadataName(DurableTypeAttributeMetadataName);
        if (attributeType is null || attributeType.DeclaringSyntaxReferences.Length != 0 ||
            attributeType.ContainingAssembly.Identity.Name != "Atelia.DurableGraph") return false;
        AttributeData? attribute = GetAttribute(type.GetAttributes(), DurableTypeAttributeMetadataName);
        if (attribute is null || !SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeType)) return false;
        if (type.TypeKind != TypeKind.Class) return true;

        INamedTypeSymbol? marker = attributeType.ContainingAssembly.GetTypeByMetadataName(DurableObjectMetadataName);
        if (marker is null || marker.TypeKind != TypeKind.Interface) return false;
        foreach (INamedTypeSymbol implemented in type.AllInterfaces) {
            if (SymbolEqualityComparer.Default.Equals(implemented, marker)) return true;
        }
        return false;
    }
}
