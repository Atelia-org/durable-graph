using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Atelia.DurableGraph.Generator;

public sealed partial class DurableGraphGenerator {
    private static bool ValidateExportExecutionHelpers(Compilation compilation, FamilyLayout layout,
        INamedTypeSymbol? family, INamedTypeSymbol? dto, INamedTypeSymbol? body, Dictionary<string, List<SchemaExport>> exports) {
        if (family?.DeclaredAccessibility != Accessibility.Public || dto?.DeclaredAccessibility != Accessibility.Public ||
            body?.DeclaredAccessibility != Accessibility.Public || dto.TypeKind != TypeKind.Struct || body.TypeKind != TypeKind.Struct ||
            !dto.IsReadOnly || dto.IsRefLikeType || body.IsRefLikeType ||
            !family.GetMembers("Definition").OfType<IFieldSymbol>().Any(item => item.IsStatic && item.DeclaredAccessibility == Accessibility.Public &&
                Same(item.Type, Runtime("StateDefinitionBinding")))) return false;

        INamedTypeSymbol? stateOps = Runtime("IStateOps`1");
        for (int index = 0; index < layout.DynamicFields.Count; index++) {
            ITypeParameterSymbol state = body.TypeParameters[index * 2];
            ITypeParameterSymbol ops = body.TypeParameters[index * 2 + 1];
            if (!IsStateParameter(dto.TypeParameters[index]) || !IsStateParameter(state) ||
                !ops.HasValueTypeConstraint || ops.HasUnmanagedTypeConstraint || ops.HasReferenceTypeConstraint ||
                ops.HasNotNullConstraint || ops.AllowsRefLikeType || ops.ConstraintTypes.Length != 1 ||
                !ops.ConstraintTypes.OfType<INamedTypeSymbol>().Any(constraint =>
                    Same(constraint.OriginalDefinition, stateOps) && Same(constraint.TypeArguments[0], state))) return false;
        }
        ITypeSymbol? FieldType(FamilyField field) {
            if (field.DynamicIndex >= 0) return dto.TypeParameters[field.DynamicIndex];
            if (field.Field.InlineSchema is SchemaReference inline) {
                if (!exports.TryGetValue(inline.SchemaId, out List<SchemaExport>? entries)) return null;
                return entries[0].Owner.GetTypeByMetadataName("Atelia.DurableGraph.Generated." + FamilyName(inline.SchemaId))?
                    .GetTypeMembers("V" + Number(inline.Version), 0).SingleOrDefault();
            }
            int tag = FamilyFieldTag(field);
            return IsBinaryReference(tag) ? compilation.GetTypeByMetadataName("Atelia.DurableGraph.ObjectId") : compilation.GetTypeByMetadataName("System." + GetTypeTagName(GetBinarySlotTypeTag(tag)));
        }
        ITypeSymbol?[] fieldTypes = layout.Fields.Select(FieldType).ToArray();
        if (fieldTypes.Any(type => type is null)) return false;
        for (int index = 0; index < layout.Fields.Count; index++) {
            if (!dto.GetMembers(layout.Fields[index].Name).OfType<IFieldSymbol>().Any(member => member.DeclaredAccessibility == Accessibility.Public &&
                !member.IsStatic && member.IsReadOnly && Same(member.Type, fieldTypes[index]))) return false;
        }
        if (layout.Fields.Count > 0 && !dto.InstanceConstructors.Any(ctor => ctor.DeclaredAccessibility == Accessibility.Public &&
            ctor.Parameters.Length == fieldTypes.Length && ctor.Parameters.Select((parameter, index) =>
                parameter.RefKind == RefKind.None && Same(parameter.Type, fieldTypes[index])).All(value => value))) return false;

        INamedTypeSymbol bodyDto = dto.Arity == 0 ? dto : dto.Construct(body.TypeParameters.Where((_, index) => index % 2 == 0).Cast<ITypeSymbol>().ToArray());
        if (layout.Shape.Kind == 2 && !body.AllInterfaces.Any(item => Same(item.OriginalDefinition, stateOps) && Same(item.TypeArguments[0], bodyDto))) return false;
        INamedTypeSymbol? schema = compilation.GetTypeByMetadataName("Atelia.DurableGraph.Schema.DurableSchema");
        INamedTypeSymbol? writer = compilation.GetTypeByMetadataName("Atelia.Binary.BareValueWriter");
        INamedTypeSymbol? reader = compilation.GetTypeByMetadataName("Atelia.Binary.BareValueReader");
        ITypeSymbol boolean = compilation.GetSpecialType(SpecialType.System_Boolean);
        bool Method(string name, ITypeSymbol? result, params (ITypeSymbol? Type, RefKind Ref)[] parameters) =>
            body.GetMembers(name).OfType<IMethodSymbol>().Any(method => method.IsStatic && method.Arity == 0 &&
                method.DeclaredAccessibility == Accessibility.Public && method.RefKind == RefKind.None &&
                Same(method.ReturnType, result) && method.Parameters.Length == parameters.Length &&
                method.Parameters.Select((parameter, index) => parameter.RefKind == parameters[index].Ref && Same(parameter.Type, parameters[index].Type)).All(value => value) &&
                (name != "Apply" || (method.Parameters[3].HasExplicitDefaultValue && method.Parameters[3].ExplicitDefaultValue is false)));
        return Method("Write", compilation.GetSpecialType(SpecialType.System_Void), (writer, RefKind.Ref), (bodyDto, RefKind.In), (schema, RefKind.None)) &&
            Method("Read", bodyDto, (reader, RefKind.Ref), (schema, RefKind.None)) &&
            Method("Apply", bodyDto, (reader, RefKind.Ref), (bodyDto, RefKind.In), (schema, RefKind.None), (boolean, RefKind.None)) &&
            Method("Visit", compilation.GetSpecialType(SpecialType.System_Void), (bodyDto, RefKind.In), (Runtime("IStateReferenceVisitor"), RefKind.None), (schema, RefKind.None)) &&
            Method("PrepareBase", compilation.GetTypeByMetadataName("Atelia.DurableGraph.Serialization.PreparedBaseBody"), (bodyDto, RefKind.In), (schema, RefKind.None)) &&
            Method("PrepareDelta", compilation.GetTypeByMetadataName("Atelia.DurableGraph.Serialization.PreparedDeltaBody"), (bodyDto, RefKind.In), (bodyDto, RefKind.In), (schema, RefKind.None)) &&
            Method("StateEquals", boolean, (bodyDto, RefKind.In), (bodyDto, RefKind.In), (schema, RefKind.None));

        INamedTypeSymbol? Runtime(string name) => compilation.GetTypeByMetadataName("Atelia.DurableGraph.Runtime." + name);
        bool Same(ITypeSymbol? left, ITypeSymbol? right) => left is not null && right is not null && SymbolEqualityComparer.Default.Equals(left, right);
        bool IsStateParameter(ITypeParameterSymbol parameter) => parameter.HasUnmanagedTypeConstraint &&
            !parameter.HasReferenceTypeConstraint && !parameter.HasNotNullConstraint &&
            !parameter.AllowsRefLikeType && parameter.ConstraintTypes.Length == 0;
    }
}
