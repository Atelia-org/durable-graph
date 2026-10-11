using Atelia.DurableGraph.Schema;
namespace Atelia.DurableGraph.Persistence.Tests;

internal static class SchemaCatalogTestData {
    // Inputs explicitly use dependency order; this helper neither discovers nor sorts the closure.
    internal static SchemaCatalogEntry[] Entries(params DurableSchema[] schemas) => schemas
        .Select((schema, index) => SchemaCatalogEntry.ForSchema(new((uint)index + 2), schema)).ToArray();

    internal static byte[] Write(DurableSchema[] schemas) => SchemaCatalogWireCodec.Write(Entries(schemas));

    internal static Dictionary<SchemaKey, DurableSchema> Read(byte[] bytes) =>
        SchemaCatalogWireCodec.Read(bytes).Where(entry => entry.Schema is not null)
            .ToDictionary(entry => new SchemaKey(entry.Schema!.Type, entry.Schema!.Version), entry => entry.Schema!);
}
