using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;

namespace Atelia.DurableGraph.Persistence;

/// <summary>Finite bootstrap employee table and builtin-only operation bindings; neither queries a persisted Schema catalog.</summary>
internal static class BootstrapBindings {
    internal static ModelExecutionEnvironment CreateEnvironment() {
        StateModelRegistry definitions = new();
        BootstrapDefinitions.Register(definitions);
        return definitions.CreateEnvironment();
    }

    private static readonly Type[] Types = [
        typeof(string), typeof(SchemaCatalogRecord), typeof(LayoutDescriptorRecord), typeof(LayoutSlotRecord),
        typeof(List<LayoutDescriptorRecord>), typeof(List<LayoutSlotRecord>), typeof(TypeExprRecord),
        typeof(List<TypeExprRecord>),
    ];
    // CLR initialization serializes this finite closure. Only immutable layouts escape;
    // no shared lazy resolver or context-capturing operation binding survives here.
    private static readonly ObjectLayout[] Layouts = CreateLayouts();

    private static ObjectLayout[] CreateLayouts() {
        ModelExecutionEnvironment models = CreateEnvironment();
        ObjectLayout[] layouts = Types.Select(type => Binding(models, type).CurrentLayout).ToArray();
        if (layouts.Any(static layout => layout.List is { } list && list.CodecVersion != BuiltinObjectLayouts.ListCodecVersion)) {
            throw new InvalidOperationException("The fixed employee List codec no longer matches typed ABI 5.");
        }
        return layouts;
    }

    private static ObjectBinding Binding(ModelExecutionEnvironment models, Type type) =>
        models.TryGetCurrentObjectBinding(type, out ObjectBinding? binding) ? binding! :
        throw new InvalidDataException("Missing fixed employee binding.");

    internal static uint Code(ObjectLayout layout) {
        return TryCode(layout, out uint code) ? code :
            throw new InvalidDataException("This object layout is outside the fixed employee/builtin closure.");
    }

    internal static bool TryCode(ObjectLayout layout, out uint code) {
        for (int index = 0; index < Layouts.Length; index++) {
            if (Layouts[index].Equals(layout)) { code = (uint)index + 1; return true; }
        }
        code = 0;
        return false;
    }

    internal static bool IsBuiltin(ObjectLayout layout) => BuiltinObjectLayouts.IsBuiltin(layout);

    internal static bool IsEmployee(ObjectLayout layout) => !IsBuiltin(layout) && TryCode(layout, out _);

    internal static ObjectLayout Layout(uint code) => code > 0 && code <= Layouts.Length
        ? Layouts[code - 1] : throw new InvalidDataException("Unknown fixed employee code.");

    internal static ObjectLayout Layout(Type type) {
        int index = Array.IndexOf(Types, type);
        return index >= 0 ? Layouts[index] : throw new InvalidDataException("Unknown fixed employee CLR type.");
    }

    internal static ObjectReaderBinding Reader(ObjectLayout layout, ModelExecutionEnvironment? models = null) {
        if (!IsBuiltin(layout)) { _ = Code(layout); }
        return (models ?? CreateEnvironment()).ResolveObjectReader(layout);
    }

    internal static ObjectStateRecord WithPreparation(ObjectStateRecord row, ModelExecutionEnvironment? models = null) {
        if (!IsBuiltin(row.Layout)) { _ = Code(row.Layout); }
        models ??= CreateEnvironment();
        return Binding(models, models.GetDomainType(row.Layout.Type)).Normalize(row);
    }
}
