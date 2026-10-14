using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Atelia.DurableGraph.Persistence;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    // Concrete diagnostics of the default Repository wire; no legacy Revision projection.
    private static IReadOnlyList<ObjectVersionRecord> DefaultRecords(GraphRecordStore store, FrameAddress graph) {
        StoredGraphRevision current = store.ReadGraph(graph);
        return current.OwnedBatch is { } batch ? store.ReadBatch(batch).Records : [];
    }

    private static IReadOnlyList<uint> DefaultRemoved(GraphRecordStore store, FrameAddress graph) {
        StoredGraphRevision current = store.ReadGraph(graph);
        return current.SourceGraph is { } source
            ? store.ReadGraph(source).ObjectHeads.Keys.Except(current.ObjectHeads.Keys).Order().ToArray()
            : [];
    }

    private static ObjectLayout DefaultLayout(GraphRecordStore store, FrameAddress graph, uint id) {
        var source = store.ReadObjectChain(graph, id).Entries[0];
        SchemaCatalogView? catalog = source.SchemaRoot is { } schema ? SchemaCatalogView.Load(store, schema) : null;
        return TypedBaseBodyCodec.Decode(source.Record.Body, catalog).Layout;
    }
}
