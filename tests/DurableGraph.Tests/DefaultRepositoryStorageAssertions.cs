
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Atelia.DurableGraph.Persistence;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    // Physical assertions identify their published frame through actual Head history.
    // Unpublished/corrupt fixtures must instead supply a concrete expected State source.
    private static StoredGraphRevision PublishedGraph(RepositoryResources resources, FrameAddress address) =>
        Atelia.DurableGraph.Persistence.Tests.MechanismFixture.Snapshot(resources, address);
    private static IReadOnlyList<uint> PublishedRemoved(RepositoryResources resources, FrameAddress address) {
        StoredGraphRevision graph = PublishedGraph(resources, address);
        return DefaultRemoved(resources.GraphStore, graph,
            graph.SourceGraph is { } source ? PublishedGraph(resources, source) : null);
    }
    // Concrete diagnostics of the default Repository wire; no legacy Revision projection.
    private static IReadOnlyList<ObjectVersionRecord> DefaultRecords(GraphRecordStore store, StoredGraphRevision current) {
        return current.OwnedBatch is { } batch ? store.ReadBatch(batch).Records : [];
    }

    private static IReadOnlyList<uint> DefaultRemoved(GraphRecordStore store, StoredGraphRevision current, StoredGraphRevision? source) {
        return source is not null
            ? source.ObjectHeads.Keys.Except(current.ObjectHeads.Keys).Order().ToArray()
            : [];
    }

    private static ObjectLayout DefaultLayout(GraphRecordStore store, StoredGraphRevision graph, uint id) {
        var source = store.ReadObjectChain(graph, id).Entries[0];
        SchemaCatalogView? catalog = source.SchemaRoot is { } schema ? SchemaCatalogView.Load(store, schema) : null;
        return TypedBaseBodyCodec.Decode(source.Record.Body, catalog).Layout;
    }
}
