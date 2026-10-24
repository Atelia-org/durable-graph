using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Atelia.DurableGraph.Generator;

public sealed partial class DurableGraphGenerator {
    // Current projection helpers share the domain's generic scope. Historical Family
    // hosts do not, so their canonical parameter names remain independent.
    private sealed class CurrentExecutionNames {
        internal CurrentExecutionNames(INamedTypeSymbol domain) {
            string prefix = "__Durable";
            while (domain.Name.StartsWith(prefix, StringComparison.Ordinal) ||
                domain.TypeParameters.Any(parameter => parameter.Name.StartsWith(prefix, StringComparison.Ordinal))) {
                prefix += "_";
            }
            Prefix = prefix;
        }

        internal string Prefix { get; }
        internal string State(int index) => Prefix + "TState" + Number(index);
        internal string Ops(int index) => Prefix + "TOps" + Number(index);
        internal string Projection(int index) => Prefix + "TProjection" + Number(index);
        internal string States(FamilyLayout layout) => GenericList(Enumerable.Range(0, layout.DynamicFields.Count).Select(State));
        internal string Operations(FamilyLayout layout) => GenericList(Enumerable.Range(0, layout.DynamicFields.Count)
            .SelectMany(index => new[] { State(index), Ops(index) }));
    }

    private sealed class FamilyDomainField {
        internal FamilyDomainField(DurableTypeAnalysis declaration, IFieldSymbol symbol, int index) {
            Declaration = declaration; Symbol = symbol; Index = index;
        }
        internal DurableTypeAnalysis Declaration { get; }
        internal IFieldSymbol Symbol { get; }
        internal int Index { get; }
        internal string DomainType => Symbol.Type.ToDisplayString(GenericQualifiedNameFormat);
        internal string OwnerType => Symbol.ContainingType.ToDisplayString(GenericQualifiedNameFormat);
        internal string Suffix => FamilyName(Declaration.SchemaId) + "_" + Number(Declaration.Fields[Index].FieldId);
    }

    private static List<FamilyDomainField> FamilyDomainFields(INamedTypeSymbol type, List<DurableTypeAnalysis> types) {
        List<FamilyDomainField> result = new();
        DurableTypeAnalysis declaration = types.Single(candidate => SymbolEqualityComparer.Default.Equals(candidate.Symbol, type.OriginalDefinition));
        for (int index = 0; index < declaration.Fields.Count; index++) {
            IFieldSymbol symbol = (IFieldSymbol)type.GetMembers(declaration.Fields[index].Symbol.Name).Single();
            result.Add(new FamilyDomainField(declaration, symbol, index));
        }
        return result;
    }

    private static int OwnFieldOffset(FamilyLayout layout, List<FamilyDomainField> fields) => layout.Fields.Count - fields.Count;

    private static int OwnProjectionField(FamilyLayout layout, List<FamilyDomainField> fields, int dynamicIndex) {
        int offset = OwnFieldOffset(layout, fields);
        return fields.FindIndex(field => layout.Fields[offset + field.Index].DynamicIndex == dynamicIndex);
    }

    private static string CurrentExecutionParameters(FamilyLayout layout, List<FamilyDomainField> fields, CurrentExecutionNames names) => GenericList(Enumerable.Range(0, layout.DynamicFields.Count)
        .SelectMany(index => OwnProjectionField(layout, fields, index) >= 0
            ? new[] { names.State(index), names.Ops(index), names.Projection(index) }
            : new[] { names.State(index), names.Ops(index) }));

    private static void AppendCurrentExecutionConstraints(StringBuilder output, FamilyLayout layout, List<FamilyDomainField> domainFields, CurrentExecutionNames names) {
        AppendGenericConstraints(output, layout, true, names.Prefix);
        for (int index = 0; index < layout.DynamicFields.Count; index++) {
            int fieldIndex = OwnProjectionField(layout, domainFields, index);
            if (fieldIndex < 0) continue;
            output.Append(" where ").Append(names.Projection(index)).Append(": struct, ").Append(RuntimeName)
                .Append("IValueProjection<").Append(domainFields[fieldIndex].DomainType).Append(", ").Append(names.State(index)).Append('>');
        }
    }

    private static string CurrentDto(DurableTypeAnalysis type, FamilyLayout layout, CurrentExecutionNames? names = null) =>
        FamilyType(type.SchemaId) + "." + (names is null ? layout.Dto : layout.Name + names.States(layout));
    private static string CurrentBody(DurableTypeAnalysis type, FamilyLayout layout, CurrentExecutionNames? names = null) =>
        FamilyType(type.SchemaId) + "." + (names is null ? layout.Body : "Body" + layout.Name + names.Operations(layout));

    private sealed class FamilyBaseProjection {
        internal FamilyBaseProjection(DurableTypeAnalysis type, FamilyLayout layout, List<SchemaTemplate> available, CurrentExecutionNames names) {
            SchemaReference reference = layout.Shape.BaseSchema!.Value;
            FamilyLayout ancestor = MakeFamilyLayout(FindHistory(available, reference.SchemaId, reference.Version)[0], available);
            Domain = type.Symbol.BaseType!.ToDisplayString(GenericQualifiedNameFormat);
            // Preserve the base's own parameter order, even when substitutions merge leaf slots.
            Dto = FamilyType(reference.SchemaId) + "." + ancestor.Name + GenericList(ancestor.DynamicFields.Select(field => {
                FamilyField leafField = layout.Fields[ancestor.Fields.IndexOf(field)];
                return leafField.DynamicIndex >= 0 ? names.State(leafField.DynamicIndex) : FamilyFieldStateType(leafField);
            }));
            FieldCount = ancestor.Fields.Count;
        }
        internal string Domain { get; }
        internal string Dto { get; }
        internal int FieldCount { get; }
        internal string Type => RuntimeName + "StateBaseProjection<" + Domain + ", " + Dto + ">";
    }

    private static void AppendFamilyDomainProjection(StringBuilder output, DurableTypeAnalysis type, FamilyLayout layout, List<DurableTypeAnalysis> types, List<SchemaTemplate> available, INamedTypeSymbol? halfType) {
        if (type.IsEnum) { AppendEnumDomainProjection(output, type, layout); return; }
        CurrentExecutionNames names = new(type.Symbol);
        ImmutableLeafCandidate? candidate = TryBuildImmutableLeafCandidate(type, types, halfType);
        bool hasNamespace = !type.Symbol.ContainingNamespace.IsGlobalNamespace;
        if (hasNamespace) output.Append("namespace ").Append(type.Symbol.ContainingNamespace.ToDisplayString()).AppendLine(" {");
        string domain = type.Symbol.ToDisplayString(GenericQualifiedNameFormat);
        string dto = CurrentDto(type, layout, names);
        List<FamilyDomainField> fields = FamilyDomainFields(type.Symbol, types);
        string parameters = CurrentExecutionParameters(layout, fields, names);
        int offset = OwnFieldOffset(layout, fields);
        FamilyBaseProjection? ancestor = layout.Shape.BaseSchema.HasValue ? new FamilyBaseProjection(type, layout, available, names) : null;
        output.Append(type.Symbol.IsRecord ? (type.IsInline ? "partial record struct " : "partial record class ") : type.IsInline ? "partial struct " : "partial class ").Append(EscapeIdentifier(type.Symbol.Name))
            .Append(DomainParameters(type.Symbol)).Append(DomainConstraints(type.Symbol)).AppendLine(" {");
        AppendFamilyFieldAccessors(output, type);
        if (candidate is not null) AppendImmutableLeafShapeCheck(output, candidate, "    ");
        AppendFamilyCurrentFactory(output, type, layout, fields, ancestor, candidate, names);
        output.Append("    private static ").Append(dto).Append(' ').Append(FamilyCaptureName).Append(parameters).Append('(')
            .Append(type.IsInline ? "in " : string.Empty).Append(domain).Append(" value, ").Append(RuntimeName).Append("CaptureContext context, ")
            .Append(SchemaName).Append("DurableSchema schema");
        if (ancestor is not null) output.Append(", ").Append(ancestor.Type).Append(" baseProjection");
        output.Append(')');
        AppendCurrentExecutionConstraints(output, layout, fields, names);
        output.AppendLine(" {");
        if (!type.IsInline) output.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(value);");
        if (ancestor is not null) {
            output.AppendLine("        var baseState = baseProjection.Capture(value, context);");
            foreach (FamilyField field in layout.Fields.Take(offset)) output.Append("        var ").Append(field.Argument)
                .Append(" = baseState.").Append(field.Name).AppendLine(";");
        }
        for (int index = 0; index < fields.Count; index++) {
            FamilyField stateField = layout.Fields[offset + index];
            FamilyDomainField field = fields[index];
            output.Append("        ref readonly var field").Append(index).Append(" = ref ").Append(field.OwnerType).Append('.').Append(FamilyReadPrefix).Append(field.Suffix)
                .Append('(').Append(type.IsInline ? "in " : string.Empty).AppendLine("value);");
            output.Append("        var ").Append(stateField.Argument).Append(" = ");
            if (stateField.DynamicIndex >= 0) output.Append(names.Projection(stateField.DynamicIndex)).Append(".Capture(in field").Append(index)
                .Append(", context, ").Append(stateField.Slot()).Append(')');
            else if (stateField.Field.InlineSchema.HasValue) output.Append(CurrentInlineProjection(field.Symbol.Type)).Append(".Capture(in field").Append(index)
                .Append(", context, ").Append(stateField.Slot()).Append(')');
            else if (FamilyFieldTag(stateField) == 4) output.Append("context.CaptureString(field").Append(index).Append(')');
            else if (FamilyFieldTag(stateField) == 15) output.Append("context.CaptureObject(field").Append(index).Append(", ").Append(stateField.Slot()).Append(".TargetType!)");
            else output.Append("field").Append(index);
            output.AppendLine(";");
        }
        output.Append("        return new ").Append(dto).Append('(').Append(string.Join(", ", layout.Fields.Select(field => field.Argument))).AppendLine(");");
        output.AppendLine("    }");
        AppendFamilyCurrentHydrate(output, type, layout, fields, ancestor, names);
        if (type.IsInline) {
            output.Append("    public readonly struct ").Append(FamilyProjectionName).Append(parameters).Append(" : ").Append(RuntimeName)
                .Append("IValueProjection<").Append(domain).Append(", ").Append(dto).Append('>');
            AppendCurrentExecutionConstraints(output, layout, fields, names);
            output.AppendLine(" {");
            output.Append("        public static ").Append(dto).Append(" Capture(in ").Append(domain).Append(" value, ").Append(RuntimeName)
                .Append("CaptureContext context, ").Append(SchemaName).Append("DurableFieldInfo slot) => ").Append(FamilyCaptureName).Append(parameters).AppendLine("(in value, context, slot.InlineSchema!);");
            output.Append("        public static void Hydrate(ref ").Append(domain).Append(" target, in ").Append(dto).Append(" state, ").Append(RuntimeName)
                .Append("ObjectReadTable objects, ").Append(SchemaName).Append("DurableFieldInfo slot) => ").Append(FamilyHydrateName).Append(parameters)
                .AppendLine("(ref target, in state, objects, slot.InlineSchema!);");
            output.AppendLine("    }");
        }
        output.AppendLine("}");
        if (hasNamespace) output.AppendLine("}");
    }

    private static void AppendFamilyFieldAccessors(StringBuilder output, DurableTypeAnalysis type) {
        string domain = type.Symbol.ToDisplayString(GenericQualifiedNameFormat);
        for (int index = 0; index < type.Fields.Count; index++) {
            DurableFieldAnalysis field = type.Fields[index];
            string fieldType = field.Symbol.Type.ToDisplayString(GenericQualifiedNameFormat.WithMiscellaneousOptions(
                GenericQualifiedNameFormat.MiscellaneousOptions | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier));
            string suffix = FamilyName(type.SchemaId) + "_" + Number(field.FieldId);
            bool backingStorage = field.Symbol.IsImplicitlyDeclared && field.Symbol.AssociatedSymbol is IPropertySymbol;
            output.Append("    internal static ref readonly ").Append(fieldType).Append(' ').Append(FamilyReadPrefix).Append(suffix).Append('(')
                .Append(type.IsInline ? "in " : string.Empty).Append(domain).Append(" value) => ref ");
            if (backingStorage) output.Append(FamilyReadonlyPrefix).Append(suffix)
                .Append(type.IsInline ? "(ref global::System.Runtime.CompilerServices.Unsafe.AsRef(in value))" : "(value)");
            else output.Append("value.").Append(EscapeIdentifier(field.Symbol.Name));
            output.AppendLine(";");
            if (field.Symbol.IsReadOnly || backingStorage) {
                output.Append("    [global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.Field, Name = ")
                    .Append(Literal(field.Symbol.MetadataName)).AppendLine(")]");
                output.Append("    private static extern ref ").Append(fieldType).Append(' ').Append(FamilyReadonlyPrefix).Append(suffix).Append('(')
                    .Append(type.IsInline ? "ref " : string.Empty).Append(domain).AppendLine(" value);");
            }
            output.Append("    internal static void ").Append(FamilyWritePrefix).Append(suffix).Append('(').Append(type.IsInline ? "ref " : string.Empty)
                .Append(domain).Append(" value, in ").Append(fieldType).Append(" field) => ");
            if (field.Symbol.IsReadOnly || backingStorage) output.Append(FamilyReadonlyPrefix).Append(suffix).Append('(').Append(type.IsInline ? "ref " : string.Empty).Append("value)");
            else output.Append("value.").Append(EscapeIdentifier(field.Symbol.Name));
            output.AppendLine(" = field;");
        }
    }

    private static void AppendFamilyCurrentHydrate(StringBuilder output, DurableTypeAnalysis type, FamilyLayout layout, List<FamilyDomainField> fields, FamilyBaseProjection? ancestor, CurrentExecutionNames names) {
        string domain = type.Symbol.ToDisplayString(GenericQualifiedNameFormat);
        string dto = CurrentDto(type, layout, names);
        int offset = OwnFieldOffset(layout, fields);
        output.Append("    private static void ").Append(FamilyHydrateName).Append(CurrentExecutionParameters(layout, fields, names)).Append('(')
            .Append(type.IsInline ? "ref " : string.Empty).Append(domain).Append(" target, in ").Append(dto).Append(" state, ")
            .Append(RuntimeName).Append("ObjectReadTable objects, ").Append(SchemaName).Append("DurableSchema schema");
        if (ancestor is not null) output.Append(", ").Append(ancestor.Type).Append(" baseProjection");
        output.Append(')');
        AppendCurrentExecutionConstraints(output, layout, fields, names);
        output.AppendLine(" {");
        if (type.IsInline) output.Append("        ").Append(domain).AppendLine(" value = default;");
        else output.AppendLine("        var value = target;");
        if (ancestor is not null) {
            if (ancestor.FieldCount == 0) output.Append("        var baseState = default(").Append(ancestor.Dto).AppendLine(");");
            else output.Append("        var baseState = new ").Append(ancestor.Dto).Append('(')
                .Append(string.Join(", ", layout.Fields.Take(offset).Select(field => "state." + field.Name))).AppendLine(");");
            output.AppendLine("        baseProjection.Hydrate(value, in baseState, objects);");
        }
        for (int index = 0; index < fields.Count; index++) {
            FamilyField stateField = layout.Fields[offset + index];
            FamilyDomainField field = fields[index];
            output.Append("        ").Append(field.DomainType).Append(" field").Append(index).Append(" = ");
            if (stateField.DynamicIndex >= 0 || stateField.Field.InlineSchema.HasValue) {
                output.AppendLine("default!;");
                output.Append("        ").Append(stateField.DynamicIndex >= 0 ? names.Projection(stateField.DynamicIndex) : CurrentInlineProjection(field.Symbol.Type))
                    .Append(".Hydrate(ref field").Append(index).Append(", in state.").Append(stateField.Name).Append(", objects, ").Append(stateField.Slot()).AppendLine(");");
            } else if (FamilyFieldTag(stateField) == 4) output.Append("objects.ResolveString(state.").Append(stateField.Name).AppendLine(")!;");
            else if (FamilyFieldTag(stateField) == 15) output.Append("objects.ResolveObject<").Append(field.DomainType).Append(">(state.").Append(stateField.Name).AppendLine(")!;");
            else output.Append("state.").Append(stateField.Name).AppendLine(";");
        }
        for (int index = 0; index < fields.Count; index++) output.Append("        ").Append(fields[index].OwnerType).Append('.').Append(FamilyWritePrefix).Append(fields[index].Suffix)
            .Append('(').Append(type.IsInline ? "ref " : string.Empty).Append("value, in field").Append(index).AppendLine(");");
        if (type.IsInline) output.AppendLine("        target = value;");
        output.AppendLine("    }");
    }

    private static void AppendFamilyCurrentFactory(StringBuilder output, DurableTypeAnalysis type, FamilyLayout layout, List<FamilyDomainField> fields, FamilyBaseProjection? ancestor, ImmutableLeafCandidate? candidate, CurrentExecutionNames names) {
        string domain = type.Symbol.ToDisplayString(GenericQualifiedNameFormat);
        string dto = CurrentDto(type, layout, names);
        string body = CurrentBody(type, layout, names);
        string parameters = CurrentExecutionParameters(layout, fields, names);
        string result = RuntimeName + (type.IsInline ? "StateValueBinding" : "StateModelBinding");
        output.Append("    private static ").Append(result).Append(' ').Append(FamilyCurrentFactoryName).Append('(').Append(RuntimeName).AppendLine("StateBindingContext context) {");
        output.Append("        var schema = new ").Append(SchemaName).Append("DurableSchema(context.GetTypeExpr(typeof(").Append(domain).Append(")), ")
            .Append(type.Version).Append(", new ").Append(SchemaName).AppendLine("DurableFieldInfo[] {");
        foreach (DurableFieldAnalysis field in type.Fields) output.Append("            context.ResolveCurrentValue(typeof(").Append(field.Symbol.Type.ToDisplayString(GenericQualifiedNameFormat))
            .Append(")).WithFieldId(").Append(field.FieldId).AppendLine(").Slot,");
        output.Append("        }, ");
        if (!type.IsInline && !IsObjectBase(type.Symbol.BaseType)) output.Append("context.ResolveCurrentModel(typeof(")
            .Append(type.Symbol.BaseType!.ToDisplayString(GenericQualifiedNameFormat)).Append(")).CurrentSchema");
        else output.Append("null");
        output.Append(", ").Append(SchemaName).Append("SchemaKind.").Append(type.IsInline ? "InlineValue" : "ReferenceObject").AppendLine(");");
        output.AppendLine("        var binding = context.BindSchema(schema);");
        for (int index = 0; index < layout.DynamicFields.Count; index++) {
            FamilyField stateField = layout.DynamicFields[index];
            output.Append("        var value").Append(index).Append(" = binding.GetValue(");
            AppendTypePatternExpression(output, stateField.Pattern);
            if (stateField.Field.InlineSchema is SchemaReference inline) output.Append(", ").Append(inline.Version);
            output.AppendLine(");");
            int ownIndex = OwnProjectionField(layout, fields, index);
            if (ownIndex >= 0) output.Append("        var projection").Append(index).Append(" = context.ResolveCurrentValue(typeof(")
                .Append(fields[ownIndex].DomainType).AppendLine(")).ProjectionType!;");
        }
        if (layout.DynamicFields.Count == 0) output.Append("        return ").Append(FamilyTypedFactoryName).AppendLine("(schema, context);");
        else {
            output.Append("        var method = typeof(").Append(domain).Append(").GetMethod(").Append(Literal(FamilyTypedFactoryName)).Append(", global::System.Reflection.BindingFlags.Static | global::System.Reflection.BindingFlags.NonPublic | global::System.Reflection.BindingFlags.DeclaredOnly)!")
                .Append(".MakeGenericMethod(").Append(string.Join(", ", Enumerable.Range(0, layout.DynamicFields.Count).SelectMany(index =>
                    OwnProjectionField(layout, fields, index) >= 0
                    ? new[] { "value" + Number(index) + ".StateType", "value" + Number(index) + ".StateOpsType", "projection" + Number(index) }
                    : new[] { "value" + Number(index) + ".StateType", "value" + Number(index) + ".StateOpsType" }))).AppendLine(");");
            output.Append("        return method.CreateDelegate<global::System.Func<").Append(SchemaName).Append("DurableSchema, ").Append(RuntimeName)
                .Append("StateBindingContext, ").Append(result).AppendLine(">>()(schema, context);");
        }
        output.AppendLine("    }");
        output.Append("    private static ").Append(result).Append(' ').Append(FamilyTypedFactoryName).Append(parameters).Append('(')
            .Append(SchemaName).Append("DurableSchema schema, ").Append(RuntimeName).Append("StateBindingContext context)");
        AppendCurrentExecutionConstraints(output, layout, fields, names);
        output.AppendLine(" {");
        if (type.IsInline) {
            output.Append("        return new ").Append(RuntimeName).Append("StateValueBinding(new ").Append(SchemaName).Append("DurableFieldInfo(1, ")
                .Append(SchemaName).Append("TypeTag.InlineValue, inlineSchema: schema), typeof(").Append(dto).Append("), typeof(").Append(body)
                .Append("), typeof(").Append(domain).Append("), typeof(").Append(FamilyProjectionName).Append(parameters).AppendLine("));");
        } else {
            if (ancestor is not null) {
                // TODO: Profile deep inheritance before optimizing the repeated base DTO prefix copies.
                output.Append("        var baseProjection = context.BindBaseProjection<").Append(ancestor.Domain).Append(", ")
                    .Append(ancestor.Dto).AppendLine(">(schema.BaseSchema!);");
            }
            output.Append("        var preparation = new ").Append(RuntimeName).Append("StatePreparationBinding<").Append(dto).AppendLine(">(schema,");
            output.Append("            (in ").Append(dto).Append(" state) => ").Append(body).AppendLine(".PrepareBase(in state, schema),");
            output.Append("            (in ").Append(dto).Append(" prior, in ").Append(dto).Append(" current) => ").Append(body).AppendLine(".PrepareDelta(in prior, in current, schema));");
            output.Append("        return new ").Append(RuntimeName).Append("StateModelBinding<").Append(domain).Append(", ").Append(dto).AppendLine(">(preparation,");
            output.Append("            new ").Append(RuntimeName).AppendLine("StateReaderBinding[] { context.ResolveReader(schema) },");
            output.Append("            item => context.Normalize<").Append(dto).AppendLine(">(item, schema),");
            if (type.Symbol.IsAbstract) output.AppendLine("            static () => throw new global::System.InvalidOperationException(\"An abstract durable model cannot be allocated.\"),");
            else output.Append("            static () => (").Append(domain).Append(")global::System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(").Append(domain).AppendLine(")),");
            output.Append("            (").Append(domain).Append(" value, in ").Append(dto).Append(" state, ").Append(RuntimeName).Append("ObjectReadTable objects) => ").Append(FamilyHydrateName)
                .Append(parameters).Append("(value, in state, objects, schema").Append(ancestor is null ? string.Empty : ", baseProjection").AppendLine("),");
            output.Append("            (value, capture) => ").Append(FamilyCaptureName).Append(parameters).Append("(value, capture, schema")
                .Append(ancestor is null ? string.Empty : ", baseProjection").AppendLine("),");
            output.Append("            (in ").Append(dto).Append(" state, ").Append(RuntimeName).Append("IStateReferenceVisitor visitor) => ").Append(body).AppendLine(".Visit(in state, visitor, schema),");
            output.Append("            sourceReaderResolver: context.ResolveReader, supportsBaseProjection: true");
            if (candidate is not null) output.Append(", isImmutableLeaf: ").Append(ImmutableLeafShapeCheckName).Append("()");
            output.AppendLine(");");
        }
        output.AppendLine("    }");
    }
}
