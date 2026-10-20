using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Atelia.DurableGraph.SchemaHistory;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Atelia.DurableGraph.Generator;

public sealed partial class DurableGraphGenerator {
    private const string RootName = "global::Atelia.DurableGraph.";
    private const string SchemaName = RootName + "Schema.";
    private const string RuntimeName = RootName + "Runtime.";
    private static readonly SymbolDisplayFormat GenericQualifiedNameFormat = SymbolDisplayFormat.FullyQualifiedFormat;

    private static string FamilyName(string id) => "Family_" + BitConverter.ToString(StrictUtf8.GetBytes(id)).Replace("-", string.Empty);
    private static string FamilyType(string id) => RootName + "Generated." + FamilyName(id);
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, true);
    private static string GenericList(IEnumerable<string> names) {
        string text = string.Join(", ", names);
        return text.Length == 0 ? string.Empty : "<" + text + ">";
    }

    // State parameters describe unresolved value representations. They never carry domain
    // types or operation instances, including when a nominal parameter is only phantom.
    private sealed class FamilyField {
        internal FamilyField(int segment, int segmentDepth, int declaredIndex, SchemaTemplateField field, TypePattern pattern) {
            Segment = segment;
            SegmentDepth = segmentDepth;
            DeclaredIndex = declaredIndex;
            Field = field;
            Pattern = pattern;
        }
        internal int Segment { get; }
        internal int SegmentDepth { get; }
        internal int DeclaredIndex { get; }
        internal SchemaTemplateField Field { get; }
        internal TypePattern Pattern { get; }
        internal int DynamicIndex { get; set; } = -1;
        internal string Name => "Segment" + Number(Segment) + "Field" + Number(Field.FieldId);
        internal string Argument => "value" + Name;
        internal string Slot(string schema = "schema") => schema + string.Concat(Enumerable.Repeat(".BaseSchema!", SegmentDepth)) + ".Fields[" + Number(DeclaredIndex) + "]";
    }

    private sealed class FamilyLayout {
        internal FamilyLayout(SchemaTemplate shape, List<FamilyField> fields) { Shape = shape; Fields = fields; }
        internal SchemaTemplate Shape { get; }
        internal List<FamilyField> Fields { get; }
        internal List<FamilyField> DynamicFields { get; } = new();
        internal string Name => "V" + Number(Shape.Version);
        internal string StateParameters => GenericList(Enumerable.Range(0, DynamicFields.Count).Select(i => "TState" + Number(i)));
        internal string OperationParameters => GenericList(Enumerable.Range(0, DynamicFields.Count).SelectMany(i => new[] { "TState" + Number(i), "TOps" + Number(i) }));
        internal string Dto => Name + StateParameters;
        internal string Body => "Body" + Name + OperationParameters;
    }

    private static List<FamilyField> FlattenFamilyFields(SchemaTemplate shape, List<SchemaTemplate> available) {
        List<FamilyField> result = new();
        TypePattern[] arguments = Enumerable.Range(0, shape.Arity).Select(TypePattern.Parameter).ToArray();
        int nextSegment = 0;
        Flatten(shape, arguments, 0);
        return result;

        void Flatten(SchemaTemplate current, IReadOnlyList<TypePattern> replacements, int depth) {
            if (depth > 256) throw new InvalidOperationException("The generic base layout exceeds the supported depth.");
            if (current.BaseSchema is SchemaReference ancestor) {
                TypePattern baseType = ancestor.Type.Substitute(replacements);
                Flatten(FindHistory(available, ancestor.SchemaId, ancestor.Version)[0], baseType.Arguments, depth + 1);
            }
            int segment = nextSegment++;
            for (int index = 0; index < current.Fields.Count; index++) {
                SchemaTemplateField field = current.Fields[index];
                result.Add(new FamilyField(segment, depth, index, field, field.ValuePattern.Substitute(replacements)));
            }
        }
    }

    private static FamilyLayout MakeFamilyLayout(SchemaTemplate shape, List<SchemaTemplate> available) {
        FamilyLayout layout = new(shape, FlattenFamilyFields(shape, available));
        Dictionary<string, int> slots = new(StringComparer.Ordinal);
        foreach (FamilyField field in layout.Fields) {
            bool dynamic = field.Pattern.Kind == PatternKind.Parameter || field.Pattern.IsNullable ||
                (field.Field.TypeTagValue == 17 && field.Pattern.Kind != PatternKind.Builtin) ||
                (field.Field.InlineSchema is SchemaReference inline &&
                 (field.Pattern.Arguments.Count > 0 || HasDynamicInlineLayout(inline, available, new HashSet<string>(StringComparer.Ordinal))));
            if (!dynamic) continue;
            string key = field.Pattern.ToString() + ":" + (field.Field.InlineSchema?.Version.ToString(CultureInfo.InvariantCulture) ?? "parameter");
            if (!slots.TryGetValue(key, out int index)) {
                index = layout.DynamicFields.Count;
                slots.Add(key, index);
                layout.DynamicFields.Add(field);
            }
            field.DynamicIndex = index;
        }
        return layout;
    }

    private static bool HasDynamicInlineLayout(SchemaReference reference, List<SchemaTemplate> available, HashSet<string> visited) {
        string key = reference.SchemaId + ":" + Number(reference.Version);
        if (!visited.Add(key)) return false;
        SchemaTemplate shape = FindHistory(available, reference.SchemaId, reference.Version)[0];
        if (shape.Arity > 0) return true;
        foreach (SchemaTemplateField field in shape.Fields) {
            if (field.ValuePattern.ContainsParameter || field.ValuePattern.IsNullable || (field.InlineSchema is SchemaReference child &&
                (child.Type.Arguments.Count > 0 || HasDynamicInlineLayout(child, available, visited)))) return true;
        }
        return false;
    }

    private static string FamilyFieldStateType(FamilyField field) {
        if (field.DynamicIndex >= 0) return "TState" + Number(field.DynamicIndex);
        if (field.Field.InlineSchema is SchemaReference inline) return FamilyType(inline.SchemaId) + ".V" + Number(inline.Version);
        int tag = field.Pattern.Kind == PatternKind.Builtin ? field.Pattern.BuiltinTag : field.Field.TypeTagValue;
        if (IsBinaryReference(tag)) return RootName + "ObjectId";
        return "global::System." + GetTypeTagName(GetBinarySlotTypeTag(tag));
    }

    private static string FamilyFieldOps(FamilyField field) => field.DynamicIndex >= 0
        ? "TOps" + Number(field.DynamicIndex)
        : FamilyType(field.Field.InlineSchema!.Value.SchemaId) + ".BodyV" + Number(field.Field.InlineSchema.Value.Version);

    private static bool UsesStateOps(FamilyField field) => field.DynamicIndex >= 0 || field.Field.InlineSchema.HasValue;
    private static int FamilyFieldTag(FamilyField field) => field.Pattern.Kind == PatternKind.Builtin ? field.Pattern.BuiltinTag : field.Field.TypeTagValue;
    private static string FamilyFieldRead(FamilyField field) => BinarySlotRead(FamilyFieldTag(field));
    private static string FamilyFieldEquality(FamilyField field, string left, string right) {
        if (FamilyFieldTag(field) == 20) return
            "global::Atelia.DurableGraph.Serialization.ScalarStateEquality.DecimalEquals(in " + left + ", in " + right + ")";
        if (FamilyFieldTag(field) == 24) return left + ".EqualsExact(" + right + ")";
        string? bits = FamilyFieldTag(field) switch { 12 => "HalfToUInt16Bits", 13 => "SingleToUInt32Bits", 14 => "DoubleToUInt64Bits", _ => null };
        return bits is null ? left + " == " + right : "global::System.BitConverter." + bits + "(" + left + ") == global::System.BitConverter." + bits + "(" + right + ")";
    }

    private static void AppendGenericConstraints(StringBuilder output, FamilyLayout layout, bool operations, string prefix = "") {
        for (int index = 0; index < layout.DynamicFields.Count; index++) {
            output.Append(" where ").Append(prefix).Append("TState").Append(index).Append(": unmanaged");
            if (operations) output.Append(" where ").Append(prefix).Append("TOps").Append(index).Append(": struct, ").Append(RuntimeName)
                .Append("IStateOps<").Append(prefix).Append("TState").Append(index).Append('>');
        }
    }

    private static void AppendFamilyDto(StringBuilder output, FamilyLayout layout) {
        output.Append("    public readonly struct ").Append(layout.Dto);
        AppendGenericConstraints(output, layout, false);
        output.AppendLine(" {");
        foreach (FamilyField field in layout.Fields) output.Append("        public readonly ").Append(FamilyFieldStateType(field)).Append(' ').Append(field.Name).AppendLine(";");
        if (layout.Fields.Count != 0) {
            output.Append("        public ").Append(layout.Name).Append('(')
                .Append(string.Join(", ", layout.Fields.Select(field => FamilyFieldStateType(field) + " " + field.Argument))).AppendLine(") {");
            foreach (FamilyField field in layout.Fields) output.Append("            ").Append(field.Name).Append(" = ").Append(field.Argument).AppendLine(";");
            output.AppendLine("        }");
        }
        output.AppendLine("    }");
    }

    private static void AppendFamilyBody(StringBuilder output, FamilyLayout layout) {
        string dto = layout.Dto;
        output.Append("    public readonly struct ").Append(layout.Body);
        if (layout.Shape.Kind == 2) output.Append(" : ").Append(RuntimeName).Append("IStateOps<").Append(dto).Append('>');
        AppendGenericConstraints(output, layout, true);
        output.AppendLine(" {");
        output.Append("        public static void Write(ref ").Append(BinaryNamespace).Append("BareValueWriter writer, in ").Append(dto)
            .Append(" value, ").Append(SchemaName).AppendLine("DurableSchema schema) {");
        foreach (FamilyField field in layout.Fields) {
            if (UsesStateOps(field)) output.Append("            ").Append(FamilyFieldOps(field)).Append(".WriteBase(ref writer, in value.")
                .Append(field.Name).Append(", ").Append(field.Slot()).AppendLine(");");
            else output.Append("            ").Append(BinarySlotWrite(FamilyFieldTag(field), "value." + field.Name)).AppendLine(";");
        }
        output.AppendLine("        }");
        output.Append("        public static ").Append(dto).Append(" Read(ref ").Append(BinaryNamespace).Append("BareValueReader reader, ")
            .Append(SchemaName).AppendLine("DurableSchema schema) {");
        foreach (FamilyField field in layout.Fields) output.Append("            var ").Append(field.Argument).Append(" = ")
            .Append(UsesStateOps(field) ? FamilyFieldOps(field) + ".ReadBase(ref reader, " + field.Slot() + ")" : FamilyFieldRead(field)).AppendLine(";");
        AppendFamilyReturn(output, layout, "            ");
        output.AppendLine("        }");
        output.Append("        public static ").Append(PayloadNamespace).Append("PreparedBaseBody PrepareBase(in ").Append(dto).Append(" value, ")
            .Append(SchemaName).AppendLine("DurableSchema schema) {");
        output.AppendLine("            var buffer = new global::System.Buffers.ArrayBufferWriter<byte>();");
        output.Append("            var writer = new ").Append(BinaryNamespace).AppendLine("BareValueWriter(buffer);");
        output.AppendLine("            Write(ref writer, in value, schema);");
        output.Append("            return new ").Append(PayloadNamespace).AppendLine("PreparedBaseBody(buffer.WrittenSpan);");
        output.AppendLine("        }");
        AppendFamilyStateEquality(output, layout);
        AppendFamilyDelta(output, layout);
        AppendFamilyApply(output, layout);
        output.Append("        public static void Visit(in ").Append(dto).Append(" state, ").Append(RuntimeName).Append("IStateReferenceVisitor visitor, ")
            .Append(SchemaName).AppendLine("DurableSchema schema) {");
        foreach (FamilyField field in layout.Fields) {
            if (UsesStateOps(field)) output.Append("            ").Append(FamilyFieldOps(field)).Append(".VisitReferences(in state.").Append(field.Name)
                .Append(", visitor, ").Append(field.Slot()).AppendLine(");");
            else if (FamilyFieldTag(field) == 4) output.Append("            visitor.VisitString(state.").Append(field.Name).AppendLine(");");
            else if (FamilyFieldTag(field) == 15) output.Append("            visitor.VisitObject(state.").Append(field.Name).Append(", ")
                .Append(field.Slot()).AppendLine(".TargetType!);");
        }
        output.AppendLine("        }");
        if (layout.Shape.Kind == 2) {
            output.Append("        public static bool StateEquals(in ").Append(dto).Append(" left, in ").Append(dto)
                .Append(" right, ").Append(SchemaName).AppendLine("DurableFieldInfo slot) => StateEquals(in left, in right, slot.InlineSchema!);");
            output.Append("        public static void WriteBase(ref ").Append(BinaryNamespace).Append("BareValueWriter writer, in ").Append(dto)
                .Append(" value, ").Append(SchemaName).AppendLine("DurableFieldInfo slot) => Write(ref writer, in value, slot.InlineSchema!);");
            output.Append("        public static ").Append(dto).Append(" ReadBase(ref ").Append(BinaryNamespace).Append("BareValueReader reader, ")
                .Append(SchemaName).AppendLine("DurableFieldInfo slot) => Read(ref reader, slot.InlineSchema!);");
            output.Append("        public static ").Append(PayloadNamespace).Append("PreparedDeltaBody PrepareDelta(in ").Append(dto).Append(" prior, in ").Append(dto)
                .Append(" current, ").Append(SchemaName).AppendLine("DurableFieldInfo slot) => PrepareDelta(in prior, in current, slot.InlineSchema!);");
            output.Append("        public static ").Append(dto).Append(" ApplyDelta(ref ").Append(BinaryNamespace).Append("BareValueReader reader, in ").Append(dto)
                .Append(" prior, ").Append(SchemaName).AppendLine("DurableFieldInfo slot) => Apply(ref reader, in prior, slot.InlineSchema!, true);");
            output.Append("        public static void VisitReferences(in ").Append(dto).Append(" state, ").Append(RuntimeName).Append("IStateReferenceVisitor visitor, ")
                .Append(SchemaName).AppendLine("DurableFieldInfo slot) => Visit(in state, visitor, slot.InlineSchema!);");
        }
        output.AppendLine("    }");
    }

    private static void AppendFamilyStateEquality(StringBuilder output, FamilyLayout layout) {
        output.Append("        public static bool StateEquals(in ").Append(layout.Dto).Append(" left, in ")
            .Append(layout.Dto).Append(" right, ").Append(SchemaName).AppendLine("DurableSchema schema) {");
        foreach (FamilyField field in layout.Fields) {
            output.Append("            if (!(");
            if (UsesStateOps(field)) output.Append(FamilyFieldOps(field)).Append(".StateEquals(in left.").Append(field.Name)
                .Append(", in right.").Append(field.Name).Append(", ").Append(field.Slot()).Append(')');
            else output.Append(FamilyFieldEquality(field, "left." + field.Name, "right." + field.Name));
            output.AppendLine(")) return false;");
        }
        output.AppendLine("            return true;");
        output.AppendLine("        }");
    }

    private static void AppendFamilyDelta(StringBuilder output, FamilyLayout layout) {
        output.Append("        public static ").Append(PayloadNamespace).Append("PreparedDeltaBody PrepareDelta(in ").Append(layout.Dto).Append(" prior, in ")
            .Append(layout.Dto).Append(" current, ").Append(SchemaName).AppendLine("DurableSchema schema) {");
        int masks = (layout.Fields.Count + 7) / 8;
        for (int index = 0; index < masks; index++) output.Append("            byte mask").Append(index).AppendLine(" = 0;");
        for (int index = 0; index < layout.Fields.Count; index++) {
            FamilyField field = layout.Fields[index];
            if (UsesStateOps(field)) output.Append("            var delta").Append(index).Append(" = ").Append(FamilyFieldOps(field))
                .Append(".PrepareDelta(in prior.").Append(field.Name).Append(", in current.").Append(field.Name).Append(", ").Append(field.Slot()).AppendLine(");");
            output.Append("            if (").Append(UsesStateOps(field) ? "delta" + Number(index) + ".HasChanges" : "!(" + FamilyFieldEquality(field, "prior." + field.Name, "current." + field.Name) + ")")
                .Append(") mask").Append(index / 8).Append(" |= ").Append(1 << (index % 8)).AppendLine(";");
        }
        output.AppendLine("            var buffer = new global::System.Buffers.ArrayBufferWriter<byte>();");
        output.Append("            var writer = new ").Append(BinaryNamespace).AppendLine("BareValueWriter(buffer);");
        for (int index = 0; index < masks; index++) output.Append("            writer.WriteByte(mask").Append(index).AppendLine(");");
        for (int index = 0; index < layout.Fields.Count; index++) {
            FamilyField field = layout.Fields[index];
            output.Append("            if ((mask").Append(index / 8).Append(" & ").Append(1 << (index % 8)).Append(") != 0) ");
            if (UsesStateOps(field)) output.Append("writer.WriteRawBytes(delta").Append(index).AppendLine(".Body);");
            else output.Append(BinarySlotWrite(FamilyFieldTag(field), "current." + field.Name)).AppendLine(";");
        }
        output.Append("            return new ").Append(PayloadNamespace).Append("PreparedDeltaBody(")
            .Append(masks == 0 ? "false" : string.Join(" || ", Enumerable.Range(0, masks).Select(i => "mask" + Number(i) + " != 0")))
            .AppendLine(", buffer.WrittenSpan);");
        output.AppendLine("        }");
    }

    private static void AppendFamilyApply(StringBuilder output, FamilyLayout layout) {
        output.Append("        public static ").Append(layout.Dto).Append(" Apply(ref ").Append(BinaryNamespace).Append("BareValueReader reader, in ")
            .Append(layout.Dto).Append(" prior, ").Append(SchemaName).AppendLine("DurableSchema schema, bool requireChanges = false) {");
        int masks = (layout.Fields.Count + 7) / 8;
        for (int index = 0; index < masks; index++) output.Append("            byte mask").Append(index).AppendLine(" = reader.ReadByte();");
        output.Append("            if (requireChanges && (")
            .Append(masks == 0 ? "true" : string.Join(" && ", Enumerable.Range(0, masks).Select(i => "mask" + Number(i) + " == 0")))
            .AppendLine(")) throw new global::System.IO.InvalidDataException(\"Nested Delta must change a slot.\");");
        if (layout.Fields.Count % 8 != 0) output.Append("            if ((mask").Append(masks - 1).Append(" & ")
            .Append(255 ^ ((1 << (layout.Fields.Count % 8)) - 1)).AppendLine(") != 0) throw new global::System.IO.InvalidDataException(\"Delta padding is nonzero.\");");
        for (int index = 0; index < layout.Fields.Count; index++) {
            FamilyField field = layout.Fields[index];
            output.Append("            var ").Append(field.Argument).Append(" = prior.").Append(field.Name).AppendLine(";");
            output.Append("            if ((mask").Append(index / 8).Append(" & ").Append(1 << (index % 8)).AppendLine(") != 0) {");
            output.Append("                ").Append(field.Argument).Append(" = ")
                .Append(UsesStateOps(field) ? FamilyFieldOps(field) + ".ApplyDelta(ref reader, in prior." + field.Name + ", " + field.Slot() + ")" : FamilyFieldRead(field)).AppendLine(";");
            if (!UsesStateOps(field)) output.Append("                if (").Append(FamilyFieldEquality(field, "prior." + field.Name, field.Argument))
                .AppendLine(") throw new global::System.IO.InvalidDataException(\"Delta redundantly writes the prior value.\");");
            output.AppendLine("            }");
        }
        AppendFamilyReturn(output, layout, "            ");
        output.AppendLine("        }");
    }

    private static void AppendFamilyReturn(StringBuilder output, FamilyLayout layout, string indent) =>
        output.Append(indent).Append("return new ").Append(layout.Dto).Append('(')
            .Append(string.Join(", ", layout.Fields.Select(field => field.Argument))).AppendLine(");");
}
