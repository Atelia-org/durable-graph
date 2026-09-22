using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Atelia.DurableGraph.Generator;

public sealed partial class DurableSchemaGenerator {
    private static string BinaryUpgradeName(int version) =>
        "UpgradeStateV" + version.ToString(CultureInfo.InvariantCulture) + "ToV" +
        (version + 1).ToString(CultureInfo.InvariantCulture);

    private static bool ValidateBinaryUpgradeMethods(SourceProductionContext context, DurableTypeModel type) {
        bool valid = true;
        for (int version = 1; version < type.Version; version++) {
            string name = BinaryUpgradeName(version);
            var members = type.Symbol.GetMembers(name);
            if (members.IsEmpty) {
                continue; // Historical readers do not require an editable-loading upgrade path.
            }
            if (members.Length != 1 || members[0] is not IMethodSymbol method ||
                !method.IsStatic || !method.ReturnsVoid || method.Arity != 0 ||
                (method.Parameters.Length != 2 && method.Parameters.Length != 3) || method.Parameters[0].RefKind != RefKind.In ||
                method.Parameters[1].RefKind != RefKind.Out ||
                (method.Parameters.Length == 3 && (method.Parameters[2].RefKind != RefKind.None ||
                    !HasMetadataName(method.Parameters[2].Type as INamedTypeSymbol, "Atelia.DurableGraph.UpgradeContext")))) {
                ReportInvalidGeneratedState(context, type.Symbol,
                    name + " must be one static void method with an in prior DTO, out next DTO and optional UpgradeContext");
                valid = false;
            }
            // DTO symbols are generated in this pass. The emitted typed call lets the compiler
            // check their exact types and the user's out definite assignment after generation.
        }
        return valid;
    }

    private static void AppendBinaryStateModel(
        StringBuilder source, DurableTypeModel type, List<BinaryVersionModel> versions,
        string indent, bool hasDomainBase, ImmutableLeafCandidate? candidate) {
        BinaryVersionModel current = versions[versions.Count - 1];
        string domain = type.Symbol.ToDisplayString(FullyQualifiedNameFormat);
        source.Append(indent).Append("private static readonly global::Atelia.DurableGraph.Runtime.CapturedStatePreparation<")
            .Append(current.Name).Append("> Preparation = new(").Append(current.Name)
            .AppendLine(".Schema, PrepareBaseBody, PrepareDeltaBody, StateEquals);");
        source.Append(indent).Append("private static readonly global::System.Func<").Append(domain)
            .Append(", global::Atelia.DurableGraph.Runtime.CaptureContext, ").Append(current.Name)
            .Append("> CaptureDelegate = static (value, context) => Capture(value");
        if (current.HasReferences) source.Append(", context");
        source.AppendLine(");");
        AppendBinaryUpgradeEdges(source, type, indent);
        AppendBinaryNormalize(source, type, versions, indent);
        AppendBinaryHydrate(source, type, current, indent, hasDomainBase);
        source.Append(indent).Append("internal static ").Append(domain).AppendLine(" Allocate() {");
        if (type.Symbol.IsAbstract) {
            source.Append(indent).AppendLine("    throw new global::System.InvalidOperationException(\"An abstract durable model cannot be allocated.\");");
        } else {
            source.Append(indent).Append("    return (").Append(domain)
                .Append(")global::System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(")
                .Append(domain).AppendLine("));");
        }
        source.Append(indent).AppendLine("}");
        source.Append(indent).Append("internal static readonly global::Atelia.DurableGraph.Runtime.StateModelBinding<")
            .Append(domain).Append(", ").Append(current.Name).AppendLine("> Model = new(");
        source.Append(indent).Append("    Preparation, new global::Atelia.DurableGraph.Runtime.StateReaderBinding[] { ");
        for (int index = 0; index < versions.Count; index++) {
            if (index != 0) source.Append(", ");
            source.Append("Reader").Append(versions[index].Name);
        }
        source.AppendLine(" }, Normalize, Allocate, Hydrate,");
        source.Append(indent).Append("    CaptureDelegate, VisitReferences, isImmutableLeaf: ")
            .Append(candidate is null ? "false" : "__DurableCheckImmutableLeafShape()").AppendLine(");");
        source.Append(indent).AppendLine("internal static void RegisterModel(global::Atelia.DurableGraph.IStateModelRegistration models) {");
        source.Append(indent).AppendLine("    global::System.ArgumentNullException.ThrowIfNull(models);");
        source.Append(indent).AppendLine("    models.Register(Model);");
        source.Append(indent).AppendLine("}");
    }

    private static void AppendBinaryUpgradeEdges(StringBuilder source, DurableTypeModel type, string indent) {
        // Validate each declared edge even when another missing edge makes its path unusable.
        // Generated DTO symbols do not yet exist during the initial Roslyn symbol inspection.
        for (int version = 1; version < type.Version; version++) {
            string name = BinaryUpgradeName(version);
            if (type.Symbol.GetMembers(name).IsEmpty) continue;
            string from = "V" + version.ToString(CultureInfo.InvariantCulture);
            string to = "V" + (version + 1).ToString(CultureInfo.InvariantCulture);
            source.Append(indent).Append("private static ").Append(to).Append(" UpgradeEdge").Append(from)
                .Append("(in ").Append(from).AppendLine(" prior, global::Atelia.DurableGraph.UpgradeContext context) {");
            source.Append(indent).Append("    ").Append(type.Symbol.ToDisplayString(FullyQualifiedNameFormat))
                .Append('.').Append(name).Append("(in prior, out ").Append(to).Append(" next");
            if (((IMethodSymbol)type.Symbol.GetMembers(name)[0]).Parameters.Length == 3) source.Append(", context");
            source.AppendLine(");");
            source.Append(indent).AppendLine("    return next;");
            source.Append(indent).AppendLine("}");
        }
    }

    private static void AppendBinaryNormalize(
        StringBuilder source, DurableTypeModel type, List<BinaryVersionModel> versions, string indent) {
        BinaryVersionModel current = versions[versions.Count - 1];
        source.Append(indent).Append("internal static ").Append(current.Name)
            .AppendLine(" Normalize(global::Atelia.DurableGraph.Runtime.ObjectStateRecord item) {");
        source.Append(indent).AppendLine("    global::System.ArgumentNullException.ThrowIfNull(item);");
        foreach (BinaryVersionModel version in versions) {
            source.Append(indent).Append("    if (").Append(version.Name).AppendLine(".Schema.Equals(item.Schema)) {");
            int missing = 0;
            for (int step = version.Version; step < current.Version; step++) {
                if (type.Symbol.GetMembers(BinaryUpgradeName(step)).IsEmpty) { missing = step; break; }
            }
            if (missing != 0) {
                source.Append(indent).Append("        throw new global::System.IO.InvalidDataException(\"Missing single-object upgrade ")
                    .Append(BinaryUpgradeName(missing)).AppendLine(".\");");
            } else {
                source.Append(indent).Append("        var state").Append(version.Name)
                    .Append(" = item.GetState<").Append(version.Name).AppendLine(">();");
                for (int step = version.Version; step < current.Version; step++) {
                    string from = "V" + step.ToString(CultureInfo.InvariantCulture);
                    string to = "V" + (step + 1).ToString(CultureInfo.InvariantCulture);
                    source.Append(indent).Append("        var state").Append(to).Append(" = UpgradeEdge")
                        .Append(from).Append("(in state").Append(from)
                        .Append(", new global::Atelia.DurableGraph.UpgradeContext(item.Id, ")
                        .Append(from).Append(".Schema, ").Append(to).AppendLine(".Schema));");
                }
                source.Append(indent).Append("        return state").Append(current.Name).AppendLine(";");
            }
            source.Append(indent).AppendLine("    }");
        }
        source.Append(indent).AppendLine("    throw new global::System.IO.InvalidDataException(\"The object does not match an exact Schema in this model family.\");");
        source.Append(indent).AppendLine("}");
    }

    private static void AppendInlineHydrate(StringBuilder source, DurableTypeModel type, BinaryVersionModel current, string indent) =>
        AppendBinaryHydrate(source, type, current, indent, false);

    private static void AppendBinaryHydrate(
        StringBuilder source, DurableTypeModel type, BinaryVersionModel current, string indent, bool hasDomainBase) {
        string domain = type.Symbol.ToDisplayString(FullyQualifiedNameFormat);
        for (int index = 0; index < type.Fields.Count; index++) {
            DurableFieldModel field = type.Fields[index];
            if (!field.Symbol.IsReadOnly) continue;
            source.Append(indent).Append("[global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.Field, Name = \"")
                .Append(field.Symbol.Name).AppendLine("\")]");
            source.Append(indent).Append("private static extern ref ").Append(field.FieldTypeName)
                .Append(" ReadonlyField").Append(index.ToString(CultureInfo.InvariantCulture))
                .Append('(').Append(type.IsInline ? "ref " : string.Empty).Append(domain).AppendLine(" value);");
        }
        source.Append(indent).Append("internal static void Hydrate(").Append(type.IsInline ? "ref " : string.Empty).Append(domain).Append(type.IsInline ? " target, in " : " value, in ")
            .Append(type.IsInline ? InlineDtoTypeName(new SchemaReference(type.SchemaId, type.Version)) : current.Name)
            .AppendLine(" state, global::Atelia.DurableGraph.Runtime.ObjectReadTable objects) {");
        if (type.IsInline) source.Append(indent).Append("    ").Append(domain).AppendLine(" value = default;");
        else source.Append(indent).AppendLine("    global::System.ArgumentNullException.ThrowIfNull(value);");
        source.Append(indent).AppendLine("    global::System.ArgumentNullException.ThrowIfNull(objects);");
        int inheritedCount = current.Fields.Count - type.Fields.Count;
        // Resolve this declaring segment before invoking its base helper or writing fields.
        // Each base helper does the same, so a bad reference cannot leave partial assignments.
        for (int index = 0; index < type.Fields.Count; index++) {
            DurableFieldModel field = type.Fields[index];
            if (field.InlineSchema.HasValue) {
                source.Append(indent).Append("    ").Append(field.FieldTypeName).Append(" inline")
                    .Append(index.ToString(CultureInfo.InvariantCulture)).AppendLine(" = default;");
                source.Append(indent).Append("    ").Append(field.Symbol.Type.ToDisplayString(FullyQualifiedNameFormat))
                    .Append('.').Append(GeneratedStateTypeName).Append(".Hydrate(ref inline")
                    .Append(index.ToString(CultureInfo.InvariantCulture)).Append(", in state.")
                    .Append(current.Fields[inheritedCount + index].Name).AppendLine(", objects);");
                continue;
            }
            if (!IsBinaryReference(field.TypeTagValue)) continue;
            source.Append(indent).Append("    var reference").Append(index.ToString(CultureInfo.InvariantCulture))
                .Append(" = objects.");
            if (field.TypeTagValue == 4) source.Append("ResolveString(");
            else source.Append("ResolveDurable<")
                .Append(field.Symbol.Type.ToDisplayString(FullyQualifiedNameFormat)).Append(">(");
            source.Append("state.").Append(current.Fields[inheritedCount + index].Name).AppendLine(");");
        }
        if (hasDomainBase) {
            SchemaReference reference = GetCurrentBaseReference(type)!.Value;
            string baseState = type.Symbol.BaseType!.ToDisplayString(FullyQualifiedNameFormat) + "." + GeneratedStateTypeName;
            source.Append(indent).Append("    var baseState = new ").Append(baseState).Append(".V")
                .Append(reference.Version.ToString(CultureInfo.InvariantCulture)).Append('(');
            for (int index = 0; index < inheritedCount; index++) {
                if (index != 0) source.Append(", ");
                source.Append("state.").Append(current.Fields[index].Name);
            }
            source.AppendLine(");");
            source.Append(indent).Append("    ").Append(baseState).AppendLine(".Hydrate(value, in baseState, objects);");
        }
        for (int index = 0; index < type.Fields.Count; index++) {
            DurableFieldModel field = type.Fields[index];
            source.Append(indent).Append("    ");
            if (field.Symbol.IsReadOnly) {
                source.Append("ReadonlyField").Append(index.ToString(CultureInfo.InvariantCulture)).Append(type.IsInline ? "(ref value)" : "(value)");
            } else {
                source.Append("value.").Append(EscapeIdentifier(field.Symbol.Name));
            }
            source.Append(" = ");
            if (field.InlineSchema.HasValue) {
                source.Append("inline").Append(index.ToString(CultureInfo.InvariantCulture));
            } else if (IsBinaryReference(field.TypeTagValue)) {
                source.Append("reference").Append(index.ToString(CultureInfo.InvariantCulture)).Append('!');
            } else source.Append("state.").Append(current.Fields[inheritedCount + index].Name);
            source.AppendLine(";");
        }
        if (type.IsInline) source.Append(indent).AppendLine("    target = value;");
        source.Append(indent).AppendLine("}");
    }
}
