using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;

namespace Atelia.DurableGraph.Persistence.MetaBootstrapP0;

/// <summary>P0's finite employee table; there is no SchemaStore in this resolver.</summary>
internal static class P0FixedBindings {
    internal static StateModelSnapshot CreateSnapshot() {
        StateModelRegistry definitions = new();
        P0EmployeeDefinitions.Register(definitions);
        return definitions.Snapshot();
    }

    private static readonly Type[] Types = [
        typeof(string), typeof(P0CatalogRecord), typeof(P0DescriptorRecord), typeof(P0FieldRecord),
        typeof(List<P0DescriptorRecord>), typeof(List<P0FieldRecord>),
    ];
    // CLR initialization serializes this finite closure. Only immutable layouts escape;
    // no shared lazy resolver or context-capturing operation binding survives here.
    private static readonly ObjectLayout[] Layouts = CreateLayouts();

    private static ObjectLayout[] CreateLayouts() {
        StateModelSnapshot models = CreateSnapshot();
        return Types.Select(type => Binding(models, type).CurrentLayout).ToArray();
    }

    private static ObjectBinding Binding(StateModelSnapshot models, Type type) =>
        models.TryGetCurrentObjectBinding(type, out ObjectBinding? binding) ? binding! :
        throw new InvalidDataException("Missing fixed employee binding.");

    internal static uint Code(ObjectLayout layout) {
        for (int index = 0; index < Layouts.Length; index++) {
            if (Layouts[index].Equals(layout)) { return (uint)index + 1; }
        }
        throw new InvalidDataException("This object layout is outside P0's fixed employee closure.");
    }

    internal static ObjectLayout Layout(uint code) => code > 0 && code <= Layouts.Length
        ? Layouts[code - 1] : throw new InvalidDataException("Unknown fixed employee code.");

    internal static ObjectLayout Layout(Type type) {
        int index = Array.IndexOf(Types, type);
        return index >= 0 ? Layouts[index] : throw new InvalidDataException("Unknown fixed employee CLR type.");
    }

    internal static ObjectReaderBinding Reader(ObjectLayout layout, StateModelSnapshot? models = null) {
        _ = Code(layout);
        return (models ?? CreateSnapshot()).ResolveObjectReader(layout);
    }

    internal static ObjectStateRecord WithPreparation(ObjectStateRecord row, StateModelSnapshot? models = null) {
        _ = Code(row.Layout);
        models ??= CreateSnapshot();
        return Binding(models, models.GetDomainType(row.Layout.Type)).Normalize(row);
    }
}
