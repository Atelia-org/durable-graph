using Atelia.FrameStore;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;

namespace Atelia.DurableGraph.Persistence;

/// <summary>A complete, immutable stored-exact DTO/string view of one Revision's live membership.</summary>
/// <remarks>
/// Objects may have different historical Schema versions. This view has no persisted roots,
/// domain instances, upgrades, or editable CaptureSession baseline. All content remains usable
/// after its stores close; the address identifies the queried view, not a published branch.
/// </remarks>
public sealed class DecodedRevision {
    private readonly Dictionary<ObjectId, ObjectStateRecord> _objects;

    // Synthetic exact-view fixtures have no persisted locators; product reads supply the actual map.
    internal DecodedRevision(FrameAddress revisionAddress, IEnumerable<ObjectStateRecord> objects, StringReadTable strings)
        : this(revisionAddress, objects, strings, new Dictionary<ObjectId, FrameAddress>()) { }

    internal DecodedRevision(FrameAddress revisionAddress, IEnumerable<ObjectStateRecord> objects, StringReadTable strings,
        IReadOnlyDictionary<ObjectId, FrameAddress> objectHeads, StateRevisionStore? sourceStore = null,
        SchemaStore? sourceSchemas = null, IReadOnlyDictionary<ObjectId, ObjectStorageInfo>? objectStorage = null) {
        RevisionAddress = revisionAddress;
        Objects = new FrozenList<ObjectStateRecord>(objects.OrderBy(static row => row.Id));
        _objects = Objects.ToDictionary(static row => row.Id);
        Strings = strings;
        ObjectHeads = new System.Collections.ObjectModel.ReadOnlyDictionary<ObjectId, FrameAddress>(
            objectHeads.ToDictionary(static pair => pair.Key, static pair => pair.Value));
        SourceStore = sourceStore;
        SourceSchemas = sourceSchemas;
        ObjectStorage = new System.Collections.ObjectModel.ReadOnlyDictionary<ObjectId, ObjectStorageInfo>(
            objectStorage?.ToDictionary(static pair => pair.Key, static pair => pair.Value) ?? []);
    }

    // Only the concrete reader can add split-store provenance. A DTO/map fixture
    // or P0DecodedRows.ToDecodedRevision remains useful for reads without save authority.
    private DecodedRevision(P0GraphStore store, StateModelSnapshot models, P0CatalogView selected, P0DecodedRows source)
        : this(source.Snapshot.Address, source.Rows.Values,
            StringReadTable.FromDecoded(source.Rows.Values.Where(static row => row.Kind == ObjectStateKind.String)
                .Select(static row => (row.Id, row.StringContent))),
            source.Snapshot.Objects.ToDictionary(static pair => new ObjectId(pair.Key), static pair => pair.Value),
            objectStorage: source.Storage) {
        SplitStore = store;
        SelectedSchemaView = selected;
        SourceModels = models;
    }

    internal static DecodedRevision ReadSplit(P0GraphStore store, FrameAddress graph, StateModelRegistry registry) {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(registry);
        P0GraphSnapshot source = store.ReadGraph(graph);
        if (source.Kind == P0GraphKind.Meta || source.SchemaRoot is not { } schemaRoot) {
            throw new InvalidDataException("A business read requires its exact owner and selected Schema view.");
        }
        P0CatalogView selected = P0CatalogView.Load(store, schemaRoot);
        StateModelSnapshot models = registry.Snapshot(schemaLookup: key => selected.Schemas.GetValueOrDefault(key));
        return new(store, models, selected, P0TypedReader.Read(store, graph, models, selected));
    }

    public FrameAddress RevisionAddress { get; }
    public IReadOnlyList<ObjectStateRecord> Objects { get; }
    public StringReadTable Strings { get; }
    // Actual per-view locators, not a guess from the queried Revision's address.
    internal IReadOnlyDictionary<ObjectId, FrameAddress> ObjectHeads { get; }
    // These facts can seed an editable baseline only in the same stores' lifetimes.
    // Synthetic DTO views have no storage authority; their content remains usable.
    internal StateRevisionStore? SourceStore { get; }
    internal SchemaStore? SourceSchemas { get; }
    internal P0GraphStore? SplitStore { get; }
    internal P0CatalogView? SelectedSchemaView { get; }
    internal StateModelSnapshot? SourceModels { get; }
    internal IReadOnlyDictionary<ObjectId, ObjectStorageInfo> ObjectStorage { get; }

    /// <summary>Returns the exact content for a live ID; zero and absent IDs are invalid.</summary>
    public ObjectStateRecord GetRequired(ObjectId objectId) => _objects.TryGetValue(objectId, out ObjectStateRecord? row)
        ? row
        : throw new InvalidDataException($"Object ID {objectId} is not live in this decoded Revision.");

    // Expose no ICollection.SyncRoot (Array.AsReadOnly would leak its backing array).
    private sealed class FrozenList<T>(IEnumerable<T> source) : IReadOnlyList<T> {
        private readonly T[] _items = source.ToArray();
        public int Count => _items.Length;
        public T this[int index] => _items[index];
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
