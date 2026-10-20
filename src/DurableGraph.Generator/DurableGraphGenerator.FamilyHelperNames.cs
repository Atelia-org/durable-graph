using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Atelia.DurableGraph.Generator;

public sealed partial class DurableGraphGenerator {
    private const string FamilyCaptureName = "__DurableCapture";
    private const string FamilyHydrateName = "__DurableHydrate";
    private const string FamilyCurrentFactoryName = "__DurableCreateCurrent";
    private const string FamilyTypedFactoryName = "__DurableCreateTyped";
    private const string FamilyProjectionName = "__DurableProjection";
    private const string FamilyReadPrefix = "__DurableRead_";
    private const string FamilyWritePrefix = "__DurableWrite_";
    private const string FamilyReadonlyPrefix = "__DurableReadonly_";
    private const string FamilyUpgradeAdapterPrefix = "__DurableUpgradeAdapter";
    private const string ImmutableLeafShapeCheckName = "__DurableCheckImmutableLeafShape";

    private static bool ReportFamilyHelperCollisions(SourceProductionContext context, DurableTypeAnalysis type,
        List<DurableTypeAnalysis> types, INamedTypeSymbol? halfType) {
        if (type.IsEnum) return false; // Enum helpers live outside the domain declaration.
        List<string> names = new() {
            FamilyCaptureName, FamilyHydrateName, FamilyCurrentFactoryName, FamilyTypedFactoryName,
        };
        if (type.IsInline) names.Add(FamilyProjectionName);
        if (TryBuildImmutableLeafCandidate(type, types, halfType) is not null) names.Add(ImmutableLeafShapeCheckName);
        if (!type.IsInline && type.Arity == 0) {
            foreach (IMethodSymbol method in type.Symbol.GetMembers().OfType<IMethodSymbol>()) {
                if (TryGetBinaryUpgradeVersion(method.Name, type.Version, out int version))
                    names.Add(FamilyUpgradeAdapterPrefix + Number(version));
            }
        }
        foreach (DurableFieldAnalysis field in type.Fields) {
            string suffix = FamilyName(type.SchemaId) + "_" + Number(field.FieldId);
            names.Add(FamilyReadPrefix + suffix);
            names.Add(FamilyWritePrefix + suffix);
            if (field.Symbol.IsReadOnly || IsRecordBackingField(field.Symbol)) names.Add(FamilyReadonlyPrefix + suffix);
        }
        bool errors = false;
        foreach (string name in names) {
            var members = type.Symbol.GetMembers(name);
            ITypeParameterSymbol? parameter = type.Symbol.TypeParameters.FirstOrDefault(item => item.Name == name);
            if (type.Symbol.Name != name && members.IsEmpty && parameter is null) continue;
            ReportInvalidGeneratedState(context, type.Symbol, "the generated helper name '" + name + "' is reserved",
                parameter is not null ? GetSourceLocation(parameter) : members.IsEmpty ? GetSourceLocation(type.Symbol) : GetSourceLocation(members[0]));
            errors = true;
        }
        return errors;
    }
}
