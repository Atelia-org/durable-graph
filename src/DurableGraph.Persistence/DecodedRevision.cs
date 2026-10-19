using Atelia.FrameStore;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;


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
        IReadOnlyDictionary<ObjectId, FrameAddress> objectHeads, IReadOnlyDictionary<ObjectId, ObjectStorageInfo>? objectStorage = null) {
        RevisionAddress = revisionAddress;
        Objects = new FrozenList<ObjectStateRecord>(objects.OrderBy(static row => row.Id));
        _objects = Objects.ToDictionary(static row => row.Id);
        Strings = strings;
        ObjectHeads = new System.Collections.ObjectModel.ReadOnlyDictionary<ObjectId, FrameAddress>(
            objectHeads.ToDictionary(static pair => pair.Key, static pair => pair.Value));
        ObjectStorage = new System.Collections.ObjectModel.ReadOnlyDictionary<ObjectId, ObjectStorageInfo>(
            objectStorage?.ToDictionary(static pair => pair.Key, static pair => pair.Value) ?? []);
    }

    // Only the concrete reader can add split-store provenance. A DTO/map fixture
    // or DecodedGraphRows.ToDecodedRevision remains useful for reads without save authority.
    private DecodedRevision(GraphRecordStore store, SchemaViewBindingEnvironment environment, DecodedGraphRows source)
        : this(source.Snapshot.Address, source.Rows.Values,
            StringReadTable.FromDecoded(source.Rows.Values.Where(static row => row.Kind == ObjectStateKind.String)
                .Select(static row => (row.Id, row.StringContent))),
            source.Snapshot.ObjectHeads.ToDictionary(static pair => new ObjectId(pair.Key), static pair => pair.Value),
            objectStorage: source.Storage) {
        GraphStore = store;
        SourceSnapshot = source.Snapshot;
        SelectedSchemaView = environment.SelectedSchemaView;
        SourceModels = environment.Models;
        BindingEnvironment = environment;
    }

    internal static DecodedRevision ReadStoredGraph(GraphRecordStore store, FrameAddress graph, StateModelRegistry registry) {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(registry);
        return ReadStoredGraph(store, graph, registry.FreezeConfiguration());
    }

    internal static DecodedRevision ReadStoredGraph(GraphRecordStore store, FrameAddress graph,
        StateModelRegistry.FrozenConfiguration configuration, SchemaViewBindingEnvironment? reuseEnvironment = null) {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(configuration);
        return ReadStoredGraph(store, store.ReadGraph(graph), configuration, reuseEnvironment);
    }

    // Only concrete reads hand off an already-qualified snapshot. This overload
    // neither invents save provenance for fixtures nor changes history authority.
    internal static DecodedRevision ReadStoredGraph(GraphRecordStore store, StoredGraphRevision source,
        StateModelRegistry.FrozenConfiguration configuration, SchemaViewBindingEnvironment? reuseEnvironment = null) {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(configuration);
        store.EnsureUsable();
        if (!ReferenceEquals(source.SourceOwner, store)) {
            throw new InvalidDataException("A business read requires its issuing Store's snapshot.");
        }
        if (source.Kind == StoredGraphKind.Meta || source.SchemaRoot is not { } schemaRoot) {
            throw new InvalidDataException("A business read requires its exact owner and selected Schema view.");
        }
        SchemaViewBindingEnvironment environment = reuseEnvironment ?? SchemaViewBindingEnvironment.Load(store, configuration, schemaRoot);
        environment.RequireMatches(store, configuration, schemaRoot);
        return new(store, environment, StoredGraphDecoder.Read(store, source, environment.Models, environment.SelectedSchemaView));
    }

    // Explicit same-S reuse, without an owner-wide history cache or externally supplied view.
    // Only actual meta loading can construct this environment; every reuse still reads the graph.
    internal sealed class SchemaViewBindingEnvironment {
        private readonly GraphRecordStore _store;
        private readonly StateModelRegistry.FrozenConfiguration _configuration;
        private readonly FrameAddress _schemaRoot;

        private SchemaViewBindingEnvironment(GraphRecordStore store, StateModelRegistry.FrozenConfiguration configuration,
            FrameAddress schemaRoot, SchemaCatalogView selected) {
            _store = store;
            _configuration = configuration;
            _schemaRoot = schemaRoot;
            SelectedSchemaView = selected;
            Models = configuration.CreateEnvironment(schemaLookup: key => selected.Schemas.GetValueOrDefault(key));
        }

        internal SchemaCatalogView SelectedSchemaView { get; }
        internal ModelExecutionEnvironment Models { get; }

        internal static SchemaViewBindingEnvironment Load(GraphRecordStore store,
            StateModelRegistry.FrozenConfiguration configuration, FrameAddress schemaRoot) =>
            new(store, configuration, schemaRoot, SchemaCatalogView.Load(store, schemaRoot));

        internal void RequireMatches(GraphRecordStore store, StateModelRegistry.FrozenConfiguration configuration,
            FrameAddress schemaRoot) {
            if (!ReferenceEquals(_store, store) || !ReferenceEquals(_configuration, configuration) || _schemaRoot != schemaRoot ||
                SelectedSchemaView.Snapshot is not { } selected || selected.Address != _schemaRoot || !ReferenceEquals(selected.SourceOwner, store)) {
                throw new InvalidDataException("A split binding environment requires its exact Store, frozen configuration and selected Schema root.");
            }
        }
    }

    public FrameAddress RevisionAddress { get; }
    public IReadOnlyList<ObjectStateRecord> Objects { get; }
    public StringReadTable Strings { get; }
    // Actual per-view locators, not a guess from the queried Revision's address.
    internal IReadOnlyDictionary<ObjectId, FrameAddress> ObjectHeads { get; }
    // These facts can seed an editable baseline only in the same stores' lifetimes.
    // Synthetic DTO views have no storage authority; their content remains usable.
    internal GraphRecordStore? GraphStore { get; }
    internal StoredGraphRevision? SourceSnapshot { get; }
    internal SchemaCatalogView? SelectedSchemaView { get; }
    internal ModelExecutionEnvironment? SourceModels { get; }
    internal SchemaViewBindingEnvironment? BindingEnvironment { get; }
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
