using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.Rbf;

namespace Atelia.DurableGraph.Persistence;

/// <summary>Persists closed Schema and container nodes in one append-only representation catalog.</summary>
/// <remarks>
/// The caller owns the file and its exclusive writer lifetime. Do not append to or
/// truncate it outside this store, or share it with another live SchemaStore.
/// Each registration is one frame and returns after DurableFlush, independently of
/// State publication. An append or flush failure faults this instance until reopen.
/// Writable reopen confirms recovered nonempty content with a DurableFlush before
/// returning. Read-only recovery validates observable bytes without confirming a
/// new durability barrier, and cannot be used to register schemas for State saves.
/// Recovery validates every physical frame and never truncates or skips bad bytes,
/// including an incomplete tail. Tombstones are not legal registration records.
/// This MVP registry is repository-wide and monotonic; future joint Commit/Ref
/// views may version its visibility without changing exact definition semantics.
/// </remarks>
public sealed class SchemaStore {
    private readonly IRbfFile _file;
    private readonly bool _readOnly;
    private Dictionary<RepresentationId, SchemaCatalogEntry> _nodes = new();
    private Dictionary<SchemaKey, DurableSchema> _schemas = new();
    private Dictionary<ObjectLayout, RepresentationId> _representationIds = new() { [ObjectLayout.String] = RepresentationId.String };
    private ulong _nextRepresentationId = 2;
    private bool _busy;
    private long _acceptedTail;

    public SchemaStore(IRbfFile file, bool readOnly = false) : this(file, readOnly, default) { }

    internal SchemaStore(IRbfFile file, bool readOnly, CancellationToken cancellationToken, Action<Action>? confirmExisting = null) {
        ArgumentNullException.ThrowIfNull(file);
        _file = file;
        _readOnly = readOnly;
        var frames = file.ScanForward(showTombstone: true).GetEnumerator();
        bool hadFrames = false;
        long? nextFrameOffset = null;
        while (frames.MoveNext()) {
            cancellationToken.ThrowIfCancellationRequested();
            hadFrames = true;
            try {
                using RbfPooledFrame frame = file.ReadPooledFrame(frames.Current.Ticket).Unwrap();
                if (frame.IsTombstone || frame.TailMetaLength != 0 || frame.Tag != SchemaCatalogWireCodec.RbfTag) {
                    throw new InvalidDataException("Unexpected frame in the dedicated SchemaStore file.");
                }
                // The decoder validates the complete batch before it can affect visibility.
                SchemaCatalogEntry[] entries = SchemaCatalogWireCodec.Read(frame.PayloadAndMeta, _nodes);
                foreach (SchemaCatalogEntry entry in entries) { Install(entry, _nodes, _schemas, _representationIds); }
                _nextRepresentationId += (uint)entries.Length;
                nextFrameOffset = file.GetPhysicalOffsetImmediatelyAfter(frames.Current.Ticket);
            }
            catch (Exception frameError) {
                frameError.Data["DurableGraph.Offset"] = frames.Current.Ticket.Offset;
                throw;
            }
        }
        if (frames.TerminationError is { } error) {
            var framingError = new InvalidDataException($"SchemaStore framing is invalid; no automatic tail recovery is performed. {error.Message}");
            if (nextFrameOffset is { } offset) { framingError.Data["DurableGraph.Offset"] = offset; }
            throw framingError;
        }
        // Merely reading bytes left in the OS cache after an uncertain outcome
        // is not a new durable barrier, including catalogs containing only containers.
        if (!readOnly && hadFrames) {
            if (confirmExisting is null) { file.DurableFlush(); }
            else { confirmExisting(() => file.DurableFlush()); }
        }
        _acceptedTail = file.TailOffset;
    }

    /// <summary>The number of user Schemas (class and inline), excluding containers and built-ins.</summary>
    public int Count {
        get {
            RequireAvailable();
            return _schemas.Count;
        }
    }

    public bool IsFaulted { get; private set; }

    public DurableSchema Register(DurableSchema schema) {
        RegisterBatch(new[] { schema });
        return GetRequired(SchemaCatalogWireCodec.Key(schema));
    }

    public void RegisterBatch(IEnumerable<DurableSchema> schemas) {
        RequireAvailable();
        if (_readOnly) { throw new InvalidOperationException("This SchemaStore is read-only."); }
        ArgumentNullException.ThrowIfNull(schemas);
        _busy = true;
        try { CommitRegistration(Prepare(schemas, [])); }
        finally { _busy = false; }
    }

    /// <summary>Registers complete layouts and returns IDs in input order after one durable barrier.</summary>
    /// <remarks>
    /// A class Schema node is already its object representation; no second registration is required.
    /// Base and inline dependencies are validated even for existing IDs. All inputs and the
    /// complete frame are prepared before appending; uncertain writes require reopening the store.
    /// </remarks>
    public RepresentationId[] RegisterRepresentations(IReadOnlyList<ObjectLayout> layouts) {
        RequireAvailable();
        if (_readOnly) { throw new InvalidOperationException("This SchemaStore is read-only."); }
        ArgumentNullException.ThrowIfNull(layouts);
        _busy = true;
        try {
            var frozen = new ObjectLayout[layouts.Count];
            for (int i = 0; i < frozen.Length; i++) {
                frozen[i] = layouts[i];
                ArgumentNullException.ThrowIfNull(frozen[i]);
            }
            Registration prepared = Prepare([], frozen);
            var result = new RepresentationId[frozen.Length];
            for (int i = 0; i < frozen.Length; i++) { result[i] = prepared.RepresentationIds[frozen[i]]; }
            CommitRegistration(prepared);
            return result;
        }
        finally { _busy = false; }
    }

    /// <summary>Resolves an object representation. Inline Schema IDs cannot be used as object headers.</summary>
    public ObjectLayout GetRepresentation(RepresentationId id) {
        RequireAvailable();
        if (id == RepresentationId.String) { return ObjectLayout.String; }
        if (_nodes.TryGetValue(id, out SchemaCatalogEntry? entry)) {
            return entry.Layout ?? throw new InvalidDataException($"Inline Schema node {id.Value} is not an object representation.");
        }
        throw new InvalidDataException($"Unknown repository representation ID {id.Value}.");
    }

    /// <summary>Binds the exact stored representation using this operation's frozen code catalog.</summary>
    /// <remarks>Only descriptors persist. CLR types/readers are resolved per supplied catalog, never cached globally here.</remarks>
    public ObjectReaderBinding ResolveReader(RepresentationId id, StateBindingContext bindings) {
        ArgumentNullException.ThrowIfNull(bindings);
        ObjectLayout layout = GetRepresentation(id);
        ObjectReaderBinding reader = bindings.ResolveObjectReader(layout);
        if (reader is null || !layout.Equals(reader.Layout)) {
            throw new InvalidDataException($"Reader binding does not match representation ID {id.Value}.");
        }
        return reader;
    }

    public DurableSchema GetRequired(string schemaId, int version) => GetRequired(new SchemaKey(schemaId, version));

    public DurableSchema GetRequired(TypeExpr type, int version) => GetRequired(new SchemaKey(type, version));

    /// <summary>Looks up an already registered exact layout without changing registry visibility.</summary>
    public bool TryGet(SchemaKey key, out DurableSchema? schema) {
        RequireAvailable();
        key.Validate();
        return _schemas.TryGetValue(key, out schema);
    }

    public DurableSchema GetRequired(SchemaKey key) {
        RequireAvailable();
        key.Validate();
        return _schemas.TryGetValue(key, out DurableSchema? schema)
            ? schema : throw new SchemaNotFoundException(key.SchemaId, key.Version);
    }

    private Registration Prepare(IEnumerable<DurableSchema> roots, IReadOnlyList<ObjectLayout> layouts) {
        var nodes = new Dictionary<RepresentationId, SchemaCatalogEntry>(_nodes);
        var schemas = new Dictionary<SchemaKey, DurableSchema>(_schemas);
        var ids = new Dictionary<ObjectLayout, RepresentationId>(_representationIds);
        var additions = new List<SchemaCatalogEntry>();
        var heights = new Dictionary<DurableSchema, int>(ReferenceEqualityComparer.Instance);
        ulong next = _nextRepresentationId;
        foreach (DurableSchema schema in roots) {
            ArgumentNullException.ThrowIfNull(schema);
            AddClosure(schema, 1);
        }
        foreach (ObjectLayout layout in layouts) {
            if (layout.Schema is { } schema) { AddClosure(schema, 1); }
            else if (layout.Array is { } array) {
                if (array.ElementSlot.ValueSchema is { } inline) { AddClosure(inline, 1); }
                if (!ids.ContainsKey(layout)) { Add(SchemaCatalogEntry.ForArray(Allocate(), array)); }
            }
            else if (layout.List is { } list) {
                if (list.ElementSlot.ValueSchema is { } inline) { AddClosure(inline, 1); }
                if (!ids.ContainsKey(layout)) { Add(SchemaCatalogEntry.ForList(Allocate(), list)); }
            }
            else if (layout.Dictionary is { } dictionary) {
                if (dictionary.KeySlot.ValueSchema is { } keyInline) { AddClosure(keyInline, 1); }
                if (dictionary.ValueSlot.ValueSchema is { } valueInline) { AddClosure(valueInline, 1); }
                if (!ids.ContainsKey(layout)) { Add(SchemaCatalogEntry.ForDictionary(Allocate(), dictionary)); }
            }
        }
        // The shared codec validates nominal kind/arity and all integer dependencies
        // before there is any observable append or ID allocation in this instance.
        byte[]? payload = additions.Count == 0 ? null : SchemaCatalogWireCodec.Write(additions, _nodes);
        return new(nodes, schemas, ids, next, payload);

        RepresentationId Allocate() {
            if (next > uint.MaxValue) { throw new InvalidOperationException("Repository representation IDs are exhausted."); }
            return new((uint)next++);
        }

        void Add(SchemaCatalogEntry entry) {
            Install(entry, nodes, schemas, ids);
            additions.Add(entry);
        }

        int AddClosure(DurableSchema schema, int depth) {
            if (depth > SchemaCatalogWireCodec.MaximumDepth) {
                throw new ArgumentException("Schema layout exceeds the maximum depth.", nameof(roots));
            }
            if (heights.TryGetValue(schema, out int cached)) {
                if (depth + cached - 1 > SchemaCatalogWireCodec.MaximumDepth) {
                    throw new ArgumentException("Schema layout exceeds the maximum depth.", nameof(roots));
                }
                return cached;
            }
            int height = 1;
            if (schema.BaseSchema is { } ancestor) { height = Math.Max(height, 1 + AddClosure(ancestor, depth + 1)); }
            foreach (DurableFieldInfo field in schema.Fields) {
                if (field.ValueSchema is { } inline) { height = Math.Max(height, 1 + AddClosure(inline, depth + 1)); }
            }
            SchemaKey key = SchemaCatalogWireCodec.Key(schema);
            if (schemas.TryGetValue(key, out DurableSchema? old)) {
                if (!old.Equals(schema)) { throw new SchemaConflictException(old, schema); }
            }
            else { Add(SchemaCatalogEntry.ForSchema(Allocate(), schema)); }
            heights.Add(schema, height);
            return height;
        }
    }

    private static void Install(SchemaCatalogEntry entry,
        Dictionary<RepresentationId, SchemaCatalogEntry> nodes,
        Dictionary<SchemaKey, DurableSchema> schemas,
        Dictionary<ObjectLayout, RepresentationId> representationIds) {
        nodes.Add(entry.Id, entry);
        if (entry.Schema is { } schema) { schemas.Add(SchemaCatalogWireCodec.Key(schema), schema); }
        if (entry.Layout is { } layout) { representationIds.Add(layout, entry.Id); }
    }

    private void CommitRegistration(Registration prepared) {
        RequireUnchangedTail();
        if (prepared.Payload is not { } payload) { return; }
        try {
            _file.Append(SchemaCatalogWireCodec.RbfTag, payload).Unwrap();
            _file.DurableFlush();
            _acceptedTail = _file.TailOffset;
        }
        catch { IsFaulted = true; throw; }
        // All dictionaries were allocated and populated before the single write.
        _nodes = prepared.Nodes;
        _schemas = prepared.Schemas;
        _representationIds = prepared.RepresentationIds;
        _nextRepresentationId = prepared.NextId;
    }

    private sealed record Registration(Dictionary<RepresentationId, SchemaCatalogEntry> Nodes,
        Dictionary<SchemaKey, DurableSchema> Schemas,
        Dictionary<ObjectLayout, RepresentationId> RepresentationIds, ulong NextId, byte[]? Payload);

    private void RequireAvailable() {
        if (IsFaulted) { throw new InvalidOperationException("Schema registration outcome is uncertain; reopen the SchemaStore before further use."); }
        if (_busy) { throw new InvalidOperationException("SchemaStore operations cannot be reentered."); }
        RequireUnchangedTail();
    }

    private void RequireUnchangedTail() {
        if (_file.TailOffset != _acceptedTail) {
            IsFaulted = true;
            throw new InvalidOperationException("The SchemaStore file was changed outside its owning store; reopen before further use.");
        }
    }
}
