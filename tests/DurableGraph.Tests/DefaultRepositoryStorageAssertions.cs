using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    // Concrete diagnostics of the default Repository wire; no legacy Revision projection.
    private static IReadOnlyList<ObjectVersionRecord> DefaultRecords(P0GraphStore store, FrameAddress graph) {
        P0GraphSnapshot current = store.ReadGraph(graph);
        return current.OwnedBatch is { } batch ? store.ReadBatch(batch).Records : [];
    }

    private static IReadOnlyList<uint> DefaultRemoved(P0GraphStore store, FrameAddress graph) {
        P0GraphSnapshot current = store.ReadGraph(graph);
        return current.SourceGraph is { } source
            ? store.ReadGraph(source).Objects.Keys.Except(current.Objects.Keys).Order().ToArray()
            : [];
    }

    private static ObjectLayout DefaultLayout(P0GraphStore store, FrameAddress graph, uint id) {
        var source = store.ReadObjectChain(graph, id).Entries[0];
        P0CatalogView? catalog = source.SchemaRoot is { } schema ? P0CatalogView.Load(store, schema) : null;
        return P0TypedBodyCodec.Decode(source.Record.Body, catalog).Layout;
    }
}
