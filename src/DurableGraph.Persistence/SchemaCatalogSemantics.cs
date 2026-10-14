using Atelia.DurableGraph.Schema;

namespace Atelia.DurableGraph.Persistence;

internal static class SchemaCatalogSemantics {
    internal const int MaximumExactDepth = 256;

    internal static IReadOnlyDictionary<SchemaKey, DurableSchema> Collect(
        IEnumerable<DurableSchema> roots, IEnumerable<ObjectLayout> layouts) {
        Dictionary<SchemaKey, DurableSchema> schemas = [];
        Dictionary<DurableSchema, int> heights = [];
        foreach (DurableSchema schema in roots) { Add(schema, 1); }
        foreach (ObjectLayout layout in layouts) {
            if (layout.Schema is { } schema) { Add(schema, 1); }
            else if ((layout.List?.ElementSlot.ValueSchema ?? layout.Array?.ElementSlot.ValueSchema) is { } inline) { Add(inline, 1); }
            else if (layout.Dictionary is { } dictionary) {
                if (dictionary.KeySlot.ValueSchema is { } key) { Add(key, 1); }
                if (dictionary.ValueSlot.ValueSchema is { } value) { Add(value, 1); }
            }
        }
        return schemas;

        int Add(DurableSchema schema, int depth) {
            if (depth > MaximumExactDepth) { throw new InvalidDataException("Exact Schema dependencies exceed depth 256."); }
            if (heights.TryGetValue(schema, out int priorHeight)) {
                if (depth + priorHeight - 1 > MaximumExactDepth) { throw new InvalidDataException("Exact Schema dependencies exceed depth 256."); }
                return priorHeight;
            }
            SchemaCatalogWorkspace.Validate(schema);
            SchemaKey key = new(schema.Type, schema.Version);
            if (schemas.TryGetValue(key, out DurableSchema? prior) && !prior.Equals(schema)) { throw new SchemaConflictException(prior, schema); }
            schemas[key] = schema;
            int height = 1;
            if (schema.BaseSchema is { } ancestor) { height = Math.Max(height, 1 + Add(ancestor, depth + 1)); }
            foreach (DurableFieldInfo field in schema.Fields) {
                if (field.ValueSchema is { } inline) { height = Math.Max(height, 1 + Add(inline, depth + 1)); }
            }
            heights.Add(schema, height);
            return height;
        }
    }

    internal static void ValidateDeclarations(IEnumerable<DurableSchema> schemas, IEnumerable<ObjectLayout> layouts) {
        SchemaDeclarationValidator declarations = new();
        foreach (DurableSchema schema in schemas) {
            declarations.ValidateDeclaration(schema.Type, schema.Kind);
            foreach (DurableFieldInfo field in schema.Fields) { declarations.ValidateSlot(field); }
        }
        foreach (ObjectLayout layout in layouts) {
            if (layout.List is { } list) { declarations.ValidateSlot(list.ElementSlot); }
            else if (layout.Array is { } array) { declarations.ValidateSlot(array.ElementSlot); }
            else if (layout.Dictionary is { } dictionary) {
                declarations.ValidateSlot(dictionary.KeySlot);
                declarations.ValidateSlot(dictionary.ValueSlot);
            }
        }
    }
}
