using System.Collections.Frozen;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using CatalogDto = Atelia.DurableGraph.Generated.Family_503043.V1;
using DescriptorDto = Atelia.DurableGraph.Generated.Family_503044.V1;
using FieldDto = Atelia.DurableGraph.Generated.Family_503046.V1;
using TypeDto = Atelia.DurableGraph.Generated.Family_503054.V1;

namespace Atelia.DurableGraph.Persistence.MetaBootstrapP0;

/// <summary>Dirty state contains explicit final requests, never their derived dependency closure.</summary>
internal sealed class P0CatalogWorkspace {
    private readonly Action _ensureEditingAllowed;
    private readonly Dictionary<SchemaKey, DurableSchema> _schemas = [];
    private readonly HashSet<ObjectLayout> _containers = [];
    private P0CatalogView? _accepted;
    private IReadOnlyDictionary<SchemaKey, DurableSchema>? _bootstrapSchemas;
    private ObjectLayout[]? _bootstrapContainers;
    private IReadOnlyDictionary<ObjectLayout, ObjectLayout>? _bootstrapContainerReplacements;
    private bool _privateBootstrap;

    internal P0CatalogWorkspace(Action ensureEditingAllowed, P0CatalogView? accepted = null) {
        _ensureEditingAllowed = ensureEditingAllowed;
        _accepted = accepted;
    }

    // Load the frozen shared view inside the owner's preparation window, rather than
    // accepting external DTO/view material supplemented with a real graph address.
    internal static P0CatalogWorkspace CreatePrivateForkOwned(Action ensureEditingAllowed,
        P0GraphStore store, FrameAddress? sourceSharedRoot, IReadOnlyCollection<DurableSchema> schemaOverrides,
        IReadOnlyCollection<(ObjectLayout OldLayout, ObjectLayout NewLayout)> containerReplacements) {
        ArgumentNullException.ThrowIfNull(ensureEditingAllowed);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(schemaOverrides);
        ArgumentNullException.ThrowIfNull(containerReplacements);
        P0CatalogView? actualShared = sourceSharedRoot is { } address ? P0CatalogView.Load(store, address) : null;
        P0CatalogWorkspace workspace = new(ensureEditingAllowed, actualShared);
        foreach (DurableSchema schema in schemaOverrides) {
            Validate(schema);
            SchemaKey key = new(schema.Type, schema.Version);
            if (workspace._schemas.TryGetValue(key, out DurableSchema? prior) && !prior.Equals(schema)) {
                throw new SchemaConflictException(prior, schema);
            }
            workspace._schemas[key] = schema;
        }
        Dictionary<SchemaKey, DurableSchema> schemas = actualShared?.Schemas.ToDictionary() ?? [];
        foreach (var pair in P0CatalogSemantics.Collect(workspace._schemas.Values, [])) { schemas[pair.Key] = pair.Value; }
        ObjectLayout[] sourceContainers = actualShared?.ObjectLayouts.Where(static layout => layout.Schema is null).ToArray() ?? [];
        Dictionary<ObjectLayout, ObjectLayout> replacements = [];
        HashSet<ObjectLayout> requested = [];
        foreach ((ObjectLayout oldLayout, ObjectLayout newLayout) in containerReplacements) {
            ArgumentNullException.ThrowIfNull(oldLayout);
            ArgumentNullException.ThrowIfNull(newLayout);
            if (oldLayout.Kind is not (ObjectStateKind.List or ObjectStateKind.Array or ObjectStateKind.Dictionary) ||
                P0FixedBindings.IsBuiltin(oldLayout) || !sourceContainers.Contains(oldLayout)) {
                throw new InvalidDataException("A replaced layout must be an exact declared dynamic container in the frozen source.");
            }
            if (!requested.Add(oldLayout)) { throw new ArgumentException("A container layout can be replaced only once.", nameof(containerReplacements)); }
            if (newLayout.Kind is not (ObjectStateKind.List or ObjectStateKind.Array or ObjectStateKind.Dictionary) ||
                P0FixedBindings.IsEmployee(newLayout)) {
                throw new ArgumentException("A replacement must be a business List, Array or Dictionary layout.", nameof(containerReplacements));
            }
            ValidateLayout(newLayout);
            if (!oldLayout.Equals(newLayout)) { replacements.Add(oldLayout, newLayout); }
        }
        ObjectLayout[] containers = sourceContainers.Except(replacements.Keys).Concat(replacements.Values).Distinct().ToArray();
        // Preserve every untouched declaration. A retained exact dependency on a replaced
        // definition is rejected, rather than silently rewritten or removed from the view.
        // Only explicit Schema overrides authorize replacing an existing Schema key;
        // new container dependencies may add keys but cannot silently change old ones.
        IReadOnlyDictionary<SchemaKey, DurableSchema> closure = P0CatalogSemantics.Collect(schemas.Values, containers);
        P0CatalogSemantics.ValidateDeclarations(closure.Values, containers);
        workspace._bootstrapSchemas = closure.ToFrozenDictionary();
        workspace._bootstrapContainers = containers;
        workspace._bootstrapContainerReplacements = replacements.ToFrozenDictionary();
        workspace._privateBootstrap = true;
        return workspace;
    }

    internal DurableSchema? LookupBootstrapSchema(SchemaKey key) =>
        (_bootstrapSchemas ?? throw new InvalidOperationException("This workspace has no private bootstrap lookup."))
        .TryGetValue(key, out DurableSchema? schema) ? schema : null;

    internal P0CatalogView? Accepted => _accepted;
    internal int DirtyCount => _schemas.Count + _containers.Count + (_bootstrapContainerReplacements?.Count ?? 0);

    internal void Set(DurableSchema schema) {
        _ensureEditingAllowed();
        Validate(schema);
        SchemaKey key = new(schema.Type, schema.Version);
        CheckAccepted(key, schema);
        _schemas[key] = schema;
    }

    internal void Set(ObjectLayout layout) {
        _ensureEditingAllowed();
        if (layout.Schema is { } schema) { Set(schema); return; }
        ValidateLayout(layout);
        if (!P0FixedBindings.IsBuiltin(layout)) { _containers.Add(layout); }
    }

    internal P0PreparedCatalog Prepare() {
        _ensureEditingAllowed();
        return PrepareOwned([]);
    }

    internal P0PreparedCatalog PrepareOwned(IEnumerable<ObjectLayout> capturedLayouts) {
        ObjectLayout[] layouts = (_privateBootstrap ? _bootstrapContainers! : Array.Empty<ObjectLayout>()).Concat(_containers).Concat(capturedLayouts).Distinct().ToArray();
        foreach (ObjectLayout layout in layouts) { ValidateLayout(layout); }
        IEnumerable<DurableSchema> requested = _privateBootstrap ? _bootstrapSchemas!.Values.Concat(_schemas.Values) : _schemas.Values;
        IReadOnlyDictionary<SchemaKey, DurableSchema> closure = P0CatalogSemantics.Collect(requested, layouts);
        foreach ((SchemaKey key, DurableSchema schema) in closure) { CheckAccepted(key, schema); }
        IEnumerable<DurableSchema> existing = _privateBootstrap ? _bootstrapSchemas!.Values : _accepted?.Schemas.Values ?? [];
        IEnumerable<ObjectLayout> existingLayouts = _privateBootstrap ? _bootstrapContainers! : _accepted?.ObjectLayouts ?? [];
        if (_privateBootstrap) {
            _ = P0CatalogSemantics.Collect(existing.Concat(closure.Values), existingLayouts.Concat(layouts));
        }
        P0CatalogSemantics.ValidateDeclarations(existing.Concat(closure.Values), existingLayouts.Concat(layouts));
        Builder builder = new(_accepted, _privateBootstrap ? _bootstrapSchemas : null);
        foreach (DurableSchema schema in closure.Values.OrderBy(static value => value.Type).ThenBy(static value => value.Version)) {
            _ = builder.AddSchema(schema);
        }
        foreach (ObjectLayout layout in layouts.Where(static value => (value.Kind is ObjectStateKind.List or ObjectStateKind.Array or ObjectStateKind.Dictionary) && !P0FixedBindings.IsBuiltin(value))) {
            builder.AddContainer(layout);
        }
        if (_privateBootstrap) { builder.ReplaceContainers(_bootstrapContainerReplacements!); }
        return builder.Finish(this);
    }

    // Promotion imports the complete declared Head.S, including definitions that no
    // captured business object reaches. The owner derives and freezes both roots;
    // neither an external view nor this workspace's possibly older accepted view
    // can substitute for loading those actual graphs.
    internal void ValidateToSharedOwned(P0GraphStore store, FrameAddress? headSchemaRoot,
        FrameAddress? latestSharedRoot) {
        _ = CollectToSharedOwned(store, headSchemaRoot, latestSharedRoot, []);
    }

    internal P0PreparedCatalog PrepareToSharedOwned(P0GraphStore store, FrameAddress? headSchemaRoot,
        FrameAddress? latestSharedRoot, IEnumerable<ObjectLayout> capturedLayouts) {
        var (target, schemas, layouts) = CollectToSharedOwned(store, headSchemaRoot, latestSharedRoot, capturedLayouts);
        // Only the latest shared graph supplies meta priors, retained rows and the
        // destination ID domain. Source descriptor numbers are never copied.
        Builder builder = new(target, null);
        foreach (DurableSchema schema in schemas.Values.OrderBy(static value => value.Type).ThenBy(static value => value.Version)) {
            _ = builder.AddSchema(schema);
        }
        foreach (ObjectLayout layout in layouts.Where(static value => (value.Kind is ObjectStateKind.List or ObjectStateKind.Array or ObjectStateKind.Dictionary) && !P0FixedBindings.IsBuiltin(value))) {
            builder.AddContainer(layout);
        }
        // Keep the original workspace unchanged until the common pending save is
        // confirmed; discard preserves its accepted view and explicit dirty roots.
        return builder.Finish(this);
    }

    private (P0CatalogView? Target, IReadOnlyDictionary<SchemaKey, DurableSchema> Schemas, ObjectLayout[] Layouts)
        CollectToSharedOwned(P0GraphStore store, FrameAddress? headSchemaRoot, FrameAddress? latestSharedRoot,
            IEnumerable<ObjectLayout> capturedLayouts) {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(capturedLayouts);
        P0CatalogView? source = headSchemaRoot is { } head ? P0CatalogView.Load(store, head) : null;
        P0CatalogView? target = latestSharedRoot is { } shared ? P0CatalogView.Load(store, shared) : null;
        ObjectLayout[] layouts = (source?.ObjectLayouts ?? []).Concat(_containers).Concat(capturedLayouts).Distinct().ToArray();
        foreach (ObjectLayout layout in layouts) { ValidateLayout(layout); }
        IReadOnlyDictionary<SchemaKey, DurableSchema> schemas = P0CatalogSemantics.Collect(
            (source?.Schemas.Values ?? []).Concat(_schemas.Values), layouts);
        // Collect checks complete same-key equality across both views and every
        // exact base/inline/container dependency, before any IDs or bodies exist.
        ObjectLayout[] mergedLayouts = (target?.ObjectLayouts ?? []).Concat(layouts).Distinct().ToArray();
        IReadOnlyDictionary<SchemaKey, DurableSchema> merged = P0CatalogSemantics.Collect(
            (target?.Schemas.Values ?? []).Concat(schemas.Values), mergedLayouts);
        P0CatalogSemantics.ValidateDeclarations(merged.Values, mergedLayouts);
        return (target, schemas, layouts);
    }

    private void CheckAccepted(SchemaKey key, DurableSchema schema) {
        IReadOnlyDictionary<SchemaKey, DurableSchema>? accepted = _privateBootstrap ? _bootstrapSchemas : _accepted?.Schemas;
        if (accepted is not null && accepted.TryGetValue(key, out DurableSchema? prior) && !prior.Equals(schema)) {
            throw new SchemaConflictException(prior, schema);
        }
    }

    internal static void Validate(DurableSchema schema) {
        ArgumentNullException.ThrowIfNull(schema);
        if (schema.SchemaId is "P0C" or "P0D" or "P0F" or "P0H" or "P0T") {
            throw new NotSupportedException("Employee declaration identifiers are reserved.");
        }
        if (!schema.Type.IsClosed) { throw new NotSupportedException("Only closed Schemas can be persisted."); }
    }

    private static void ValidateLayout(ObjectLayout layout) {
        if (P0FixedBindings.IsBuiltin(layout)) { return; }
        if (layout.Schema is { } schema) { Validate(schema); return; }
        if (layout.Kind is not (ObjectStateKind.List or ObjectStateKind.Array or ObjectStateKind.Dictionary)) {
            throw new NotSupportedException("This slice supports business reference objects and exact Lists, Arrays and Dictionaries.");
        }
    }

    internal void RebaseOwned(P0CatalogView accepted) {
        if (_privateBootstrap) { throw new InvalidOperationException("A private bootstrap keeps its frozen catalog source."); }
        foreach (DurableSchema dirty in _schemas.Values) {
            if (accepted.Schemas.TryGetValue(new(dirty.Type, dirty.Version), out DurableSchema? prior) && !prior.Equals(dirty)) {
                throw new SchemaConflictException(prior, dirty);
            }
        }
        _accepted = accepted;
    }

    internal void PrepareInstall(P0PreparedCatalog candidate, P0GraphSnapshot graph) {
        if (graph.Kind != P0GraphKind.Meta || graph.SourceOwner is null || graph.RootId != candidate.View.RootId ||
            graph.Objects.Count != candidate.View.Rows.Count || candidate.View.Rows.Keys.Any(id => !graph.Objects.ContainsKey(id.Value))) {
            throw new InvalidDataException("Catalog installation must use its actual complete meta graph.");
        }
        if (candidate.View.Snapshot is null) { candidate.View.AttachSnapshot(graph); }
        if (candidate.View.Snapshot!.Address != graph.Address) { throw new InvalidOperationException("Catalog installation must use its actual graph."); }
    }

    internal void Accept(P0PreparedCatalog candidate) {
        _accepted = candidate.View;
        _schemas.Clear();
        _containers.Clear();
        _bootstrapContainers = null;
        _bootstrapContainerReplacements = null;
        _privateBootstrap = false;
    }

    private sealed class Builder {
        private readonly StateModelSnapshot _models = P0FixedBindings.CreateSnapshot();
        private readonly P0CatalogView? _source;
        private readonly Dictionary<ObjectId, ObjectStateRecord> _rows;
        private readonly List<ObjectId> _declared;
        private readonly List<ObjectId> _added = [];
        private readonly List<ObjectStateRecord> _new = [];
        private readonly Dictionary<string, ObjectId> _strings = new(StringComparer.Ordinal);
        private readonly Dictionary<DurableSchema, ObjectId> _schemaIds = [];
        private readonly Dictionary<TypeExpr, ObjectId> _typeIds = [];
        private readonly Dictionary<DurableFieldInfo, ObjectId> _fieldIds = [];
        private readonly Dictionary<ObjectLayout, ObjectId> _containers = [];
        private bool _replacedDeclaration;
        private ulong _next;
        private readonly ObjectId _root, _list;

        internal Builder(P0CatalogView? source, IReadOnlyDictionary<SchemaKey, DurableSchema>? bootstrapSchemas) {
            _source = source;
            _rows = source?.Rows.ToDictionary() ?? [];
            _declared = source?.DescriptorIds.ToList() ?? [];
            _next = source is null ? 3 : (ulong)_rows.Keys.Max().Value + 1;
            _root = source?.RootId ?? new(1);
            _list = source?.DescriptorListId ?? new(2);
            foreach (ObjectStateRecord row in _rows.Values.Where(static row => row.Kind == ObjectStateKind.String)) {
                _strings.TryAdd(row.StringContent, row.Id);
            }
            if (source is not null) {
                foreach (DurableSchema schema in source.Schemas.Values) {
                    if (bootstrapSchemas is null || bootstrapSchemas[new(schema.Type, schema.Version)].Equals(schema)) {
                        _schemaIds.Add(schema, source.SchemaNodeId(schema));
                    }
                }
            } else { Add(new(_root, Schema(typeof(P0CatalogRecord)), new CatalogDto(_list))); }
        }

        internal ObjectId AddSchema(DurableSchema schema) {
            if (_schemaIds.TryGetValue(schema, out ObjectId known)) { return known; }
            ObjectId ancestor = schema.BaseSchema is { } parent ? AddSchema(parent) : default;
            ObjectId type = Type(schema.Type);
            ObjectId[] fields = schema.Fields.Select(Field).ToArray();
            ObjectId list = Allocate();
            Add(new(list, List(typeof(List<P0FieldRecord>)), new FrozenListState<ObjectId>(fields)));
            ObjectId id = Allocate();
            Add(new(id, Schema(typeof(P0DescriptorRecord)), new DescriptorDto(
                schema.Kind == SchemaKind.ReferenceObject ? (byte)1 : (byte)3,
                type, schema.Version, list, default, 0, ancestor)));
            _schemaIds.Add(schema, id);
            if (_source is not null && _source.Schemas.TryGetValue(new(schema.Type, schema.Version), out DurableSchema? prior)) {
                int index = _declared.IndexOf(_source.SchemaNodeId(prior));
                if (index < 0) { throw new InvalidDataException("The source Schema is not a declared descriptor."); }
                _declared[index] = id;
                _added.Add(id);
                _replacedDeclaration = true;
            } else { Declare(id); }
            return id;
        }

        internal ObjectId AddContainer(ObjectLayout layout) {
            if (_containers.TryGetValue(layout, out ObjectId known)) { return known; }
            if (_source is not null) {
                try {
                    ObjectId prior = _source.DescriptorId(layout);
                    _containers.Add(layout, prior);
                    return prior;
                }
                catch (InvalidDataException) { }
            }
            ObjectId type = layout.Kind == ObjectStateKind.List ? default : Type(layout.Type);
            ObjectId fields = default, element = default;
            byte kind;
            uint codec;
            if (layout.Dictionary is { } dictionary) {
                ObjectId[] values = [Field(dictionary.KeySlot), Field(dictionary.ValueSlot)];
                fields = Allocate();
                Add(new(fields, List(typeof(List<P0FieldRecord>)), new FrozenListState<ObjectId>(values)));
                kind = 5;
                codec = dictionary.CodecVersion;
            } else {
                element = Field(layout.List?.ElementSlot ?? layout.Array!.ElementSlot);
                kind = layout.Array is null ? (byte)2 : (byte)4;
                codec = layout.List?.CodecVersion ?? layout.Array!.CodecVersion;
            }
            ObjectId id = Allocate();
            Add(new(id, Schema(typeof(P0DescriptorRecord)), new DescriptorDto(kind, type, 0, fields, element, codec, default)));
            _containers.Add(layout, id);
            Declare(id);
            return id;
        }

        internal void ReplaceContainers(IReadOnlyDictionary<ObjectLayout, ObjectLayout> replacements) {
            if (replacements.Count == 0) { return; }
            Dictionary<ObjectId, ObjectId> ids = [];
            foreach ((ObjectLayout oldLayout, ObjectLayout newLayout) in replacements) {
                ObjectId oldId = _source!.DescriptorId(oldLayout);
                // Fixed layouts have no dynamic declaration. An existing destination
                // keeps its immutable descriptor; multiple old members may converge.
                ObjectId newId = P0FixedBindings.IsBuiltin(newLayout) ? default : AddContainer(newLayout);
                ids.Add(oldId, newId);
            }
            ObjectId[] declared = _declared.Select(id => ids.GetValueOrDefault(id, id))
                .Where(static id => !id.IsNull).Distinct().ToArray();
            _declared.Clear();
            _declared.AddRange(declared);
            _replacedDeclaration = true;
        }

        private ObjectId Type(TypeExpr type) {
            if (_typeIds.TryGetValue(type, out ObjectId known)) { return known; }
            ObjectId name = type.Kind == TypeExprKind.Named ? String(type.DefinitionId!) : default;
            ObjectId arguments = default;
            if (type.Arguments.Length != 0) {
                ObjectId[] values = type.Arguments.Select(Type).ToArray();
                arguments = Allocate();
                Add(new(arguments, List(typeof(List<P0TypeExprRecord>)), new FrozenListState<ObjectId>(values)));
            }
            ObjectId id = Allocate();
            Add(new(id, Schema(typeof(P0TypeExprRecord)), new TypeDto((byte)type.Kind, (byte)type.BuiltinTag, name, arguments)));
            _typeIds.Add(type, id);
            return id;
        }

        private ObjectId Field(DurableFieldInfo field) {
            if (_fieldIds.TryGetValue(field, out ObjectId known)) { return known; }
            ObjectId target = field.TargetType is { } nominal ? Type(nominal) : default;
            ObjectId inline = field.InlineSchema is { } schema ? AddSchema(schema) : default;
            ObjectId nullable = field.NullableLayout is { } child ? Field(child.ElementSlot) : default;
            ObjectId id = Allocate();
            Add(new(id, Schema(typeof(P0FieldRecord)), new FieldDto(field.FieldId, (byte)field.TypeTag, target, inline, nullable)));
            _fieldIds.Add(field, id);
            return id;
        }

        private void Declare(ObjectId id) { _added.Add(id); _declared.Add(id); }
        private ObjectId String(string value) {
            if (_strings.TryGetValue(value, out ObjectId prior)) { return prior; }
            ObjectId id = Allocate();
            _strings.Add(value, id);
            Add(new(id, value));
            return id;
        }
        private ObjectId Allocate() => _next <= uint.MaxValue ? new((uint)_next++)
            : throw new InvalidOperationException("The meta ObjectId domain is exhausted.");
        private DurableSchema Schema(Type type) => _models.ResolveCurrentModel(type).CurrentSchema;
        private ListLayout List(Type type) => _models.TryGetCurrentObjectBinding(type, out ObjectBinding? binding)
            ? binding!.CurrentLayout.List! : throw new InvalidDataException("Missing fixed List binding.");
        private void Add(ObjectStateRecord row) {
            ObjectStateRecord prepared = P0FixedBindings.WithPreparation(row, _models);
            _rows.Add(row.Id, prepared);
            _new.Add(prepared);
        }

        internal P0PreparedCatalog Finish(P0CatalogWorkspace owner) {
            if (_source is not null && _added.Count == 0 && !_replacedDeclaration) { return new(owner, _source, null, 0, []); }
            ObjectStateRecord list = P0FixedBindings.WithPreparation(new ObjectStateRecord(_list,
                List(typeof(List<P0DescriptorRecord>)), new FrozenListState<ObjectId>(_declared.ToArray())), _models);
            _rows[_list] = list;
            StateReferenceValidator validator = new(_rows);
            foreach (ObjectStateRecord row in _rows.Values) { P0FixedBindings.Reader(row.Layout, _models).VisitReferences(row, validator); }
            List<ObjectVersionRecord> records = [];
            List<ObjectId> encodedIds = [];
            foreach (ObjectStateRecord row in _new) {
                PreparedBaseBody raw = row.Kind == ObjectStateKind.String ? StringPayloadCodec.PrepareBase(row.StringContent)
                    : row.Preparation!.PrepareBase(row);
                records.Add(ObjectVersionRecord.CreateBase(row.Id.Value, P0TypedBodyCodec.Encode(row.Layout, raw)));
                encodedIds.Add(row.Id);
            }
            byte[] encodedBase = P0TypedBodyCodec.Encode(list.Layout, list.Preparation!.PrepareBase(list));
            encodedIds.Add(list.Id);
            long b = ObjectVersionPayloadSize.GetBasePayloadBytes(encodedBase.Length), h = b;
            if (_source is null) { records.Add(ObjectVersionRecord.CreateBase(_list.Value, encodedBase)); }
            else {
                PreparedDeltaBody delta = _replacedDeclaration
                    ? ListStateBody<ObjectId, ObjectIdStateOps>.PrepareDelta(
                        _source.Rows[_list].GetListState<ObjectId>(), list.GetListState<ObjectId>(), list.Layout.List!)
                    : ListStateBody<ObjectId, ObjectIdStateOps>.PrepareAppendDelta(_source.DescriptorIds.Count, _added.ToArray(), list.Layout.List!);
                var prior = _source.Snapshot!.Objects[_list.Value];
                long d = ObjectVersionPayloadSize.GetDeltaPayloadBytes(delta.Body.Length, prior);
                if (b <= d || _source.DescriptorListH + d > 5L * b) { records.Add(ObjectVersionRecord.CreateBase(_list.Value, encodedBase)); }
                else { records.Add(ObjectVersionRecord.CreateDelta(_list.Value, prior, delta.Body)); h = _source.DescriptorListH + d; }
            }
            P0CatalogView view = new(_root, _rows, descriptorListH: h);
            return new(owner, view, new(_source?.Snapshot?.Address, _root, records), _added.Count, encodedIds, _source?.DescriptorIds);
        }
    }
}

internal sealed class P0PreparedCatalog(P0CatalogWorkspace owner, P0CatalogView view,
    P0PreparedGraphWrite? write, int addedDescriptors, IEnumerable<ObjectId> encodedIds,
    IEnumerable<ObjectId>? acceptedDescriptors = null) {
    private P0GraphSnapshot? _installation;
    internal P0CatalogView View { get; } = view;
    internal P0PreparedGraphWrite? Write { get; } = write;
    internal int AddedDescriptors { get; } = addedDescriptors;
    internal IReadOnlyList<ObjectId> EncodedIds { get; } = new FrozenList<ObjectId>(encodedIds);
    internal int EncodedRows => EncodedIds.Count;
    internal int EncodedAcceptedDescriptors => EncodedIds.Intersect(acceptedDescriptors ?? []).Count();
    internal void PrepareInstall(P0GraphSnapshot graph) {
        if (_installation is not null) { throw new InvalidOperationException("This catalog installation is already prepared."); }
        owner.PrepareInstall(this, graph);
        _installation = graph;
    }
    internal void Install(P0GraphSnapshot graph) {
        if (!ReferenceEquals(_installation, graph)) { throw new InvalidOperationException("Prepare the catalog installation before publication."); }
        owner.Accept(this);
        _installation = null;
    }
}
