using Atelia.FrameStore;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph.Persistence;

/// <summary>Reads all live DTO/string content using Base type information and explicit model readers.</summary>
public static class RevisionDecoder {
    /// <summary>
    /// Reconstructs stored-exact versions and validates all supported references before returning.
    /// Failure returns no partial directory and does not write either store. Callers supply stores
    /// belonging to the same repository and keep them stable during this synchronous operation.
    /// </summary>
    public static DecodedRevision Read(
        StateRevisionStore store,
        SchemaStore schemas,
        FrameAddress revisionAddress,
        StateReaderRegistry readers) {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(readers);
        StateModelSnapshot bindings = readers.Snapshot(schemas);
        return ReadSnapshot(store, schemas, revisionAddress, bindings);
    }

    internal static DecodedRevision ReadSnapshot(
        StateRevisionStore store,
        SchemaStore schemas,
        FrameAddress revisionAddress,
        IReadOnlyDictionary<SchemaKey, StateReaderBinding> bindings) => ReadCore(store, schemas, revisionAddress, body => {
            ObjectLayout layout = body.Layout;
            if (layout.Kind == ObjectStateKind.String) { return StringObjectReader.Instance; }
            DurableSchema schema = layout.Schema ?? throw new InvalidDataException("Container readers require a model catalog.");
            SchemaKey key = new(schema.Type, schema.Version);
            if (!bindings.TryGetValue(key, out StateReaderBinding? binding)) {
                throw new InvalidDataException($"No reader is registered for {key.Type} v{key.Version}.");
            }
            return binding;
        });

    internal static DecodedRevision ReadSnapshot(
        StateRevisionStore store, SchemaStore schemas, FrameAddress revisionAddress, StateBindingContext bindings) =>
        ReadCore(store, schemas, revisionAddress, body => schemas.ResolveReader(body.RepresentationId, bindings));

    private static DecodedRevision ReadCore(
        StateRevisionStore store, SchemaStore schemas, FrameAddress revisionAddress,
        Func<DecodedBaseObjectBody, ObjectReaderBinding> resolveReader) =>
        ReadCore(store, schemas, revisionAddress, (id, _) => ReadObject(store, schemas, revisionAddress, id, resolveReader));

    internal static (ObjectStateRecord Row, ObjectReaderBinding Binding, ObjectStorageInfo Storage) ReadObject(
        StateRevisionStore store, SchemaStore schemas, FrameAddress revisionAddress, ObjectId id,
        Func<DecodedBaseObjectBody, ObjectReaderBinding> resolveReader) {
        ObjectVersionChain chain = store.ReadObjectVersionChain(revisionAddress, id.Value);
        DecodedBaseObjectBody body = TypedObjectVersionReader.DecodeBase(chain, schemas);
        ObjectReaderBinding binding = resolveReader(body);
        if (!body.Layout.Equals(binding.Layout)) {
            throw new InvalidDataException("The selected reader does not match the complete stored layout.");
        }
        // Readers return owned DTOs and consume the complete Base/Delta body chain.
        ObjectStateRecord row = binding.Read(id, TypedObjectVersionReader.CreateBodySource(chain, body));
        return (row, binding, new(chain.ObjectHeadAddress, chain.ReconstructionPayloadBytes));
    }

    internal static DecodedRevision ReadCore(StateRevisionStore store, SchemaStore schemas, FrameAddress revisionAddress,
        Func<ObjectId, FrameAddress, (ObjectStateRecord Row, ObjectReaderBinding Binding, ObjectStorageInfo Storage)> readObject) {
        // Membership and references are properties of this view, never of a cached row.
        IReadOnlyDictionary<ObjectId, FrameAddress> heads = store.ReadLiveObjectHeadMap(revisionAddress)
            .ToDictionary(static pair => new ObjectId(pair.Key), static pair => pair.Value);
        List<ObjectStateRecord> objects = [];
        List<(ObjectStateRecord Row, ObjectReaderBinding Binding)> boundRows = [];
        List<(ObjectId Id, string Value)> strings = [];
        Dictionary<ObjectId, ObjectStorageInfo> storage = [];

        // Object-first reconstruction: only one raw chain is retained at a time.
        foreach (ObjectId id in heads.Keys.Order()) {
            (ObjectStateRecord row, ObjectReaderBinding binding, ObjectStorageInfo info) = readObject(id, heads[id]);
            if (info.Head != heads[id]) {
                throw new InvalidDataException("Decoded object storage does not match this Revision's exact head.");
            }
            storage.Add(id, info);
            objects.Add(row);
            boundRows.Add((row, binding));
            if (row.Kind == ObjectStateKind.String) { strings.Add((id, row.StringContent)); }
        }

        StringReadTable table = StringReadTable.FromDecoded(strings);
        Dictionary<ObjectId, ObjectStateRecord> directory = objects.ToDictionary(static row => row.Id);
        StateReferenceValidator validator = new(directory);
        foreach ((ObjectStateRecord row, ObjectReaderBinding binding) in boundRows) {
            binding.VisitReferences(row, validator);
        }
        // Lookup equality can depend on referenced string content. Check every live row
        // after the complete exact directory exists, before any Upgrade can run.
        foreach ((ObjectStateRecord row, ObjectReaderBinding binding) in boundRows) {
            if (binding is DictionaryStateReader dictionary) {
                dictionary.ValidateLookupKeys(row, id => directory.TryGetValue(id, out ObjectStateRecord? target)
                    ? target : throw new InvalidDataException($"Dictionary key object {id.Value} is not live."));
            }
        }
        return new DecodedRevision(revisionAddress, objects, table, heads, store, schemas, storage);
    }
}
