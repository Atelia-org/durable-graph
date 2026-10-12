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

    private static readonly StateModelSnapshot Snapshot = CreateSnapshot();
    private static readonly ObjectLayout[] Layouts = [
        ObjectLayout.String,
        Snapshot.ResolveCurrentModel(typeof(P0CatalogRecord)).CurrentLayout,
        Snapshot.ResolveCurrentModel(typeof(P0DescriptorRecord)).CurrentLayout,
        Snapshot.ResolveCurrentModel(typeof(P0FieldRecord)).CurrentLayout,
        Binding(typeof(List<P0DescriptorRecord>)).CurrentLayout,
        Binding(typeof(List<P0FieldRecord>)).CurrentLayout,
    ];

    private static ObjectBinding Binding(Type type) =>
        Snapshot.TryGetCurrentObjectBinding(type, out ObjectBinding? binding) ? binding! :
        throw new InvalidDataException("Missing fixed employee binding.");

    internal static uint Code(ObjectLayout layout) {
        for (int index = 0; index < Layouts.Length; index++) {
            if (Layouts[index].Equals(layout)) { return (uint)index + 1; }
        }
        throw new InvalidDataException("This object layout is outside P0's fixed employee closure.");
    }

    internal static ObjectLayout Layout(uint code) => code > 0 && code <= Layouts.Length
        ? Layouts[code - 1] : throw new InvalidDataException("Unknown fixed employee code.");

    internal static ObjectReaderBinding Reader(ObjectLayout layout) {
        _ = Code(layout);
        return Snapshot.ResolveObjectReader(layout);
    }

    internal static ObjectStateRecord WithPreparation(ObjectStateRecord row) =>
        Binding(Snapshot.GetDomainType(row.Layout.Type)).Normalize(row);
}
