
using System.Buffers;
using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence;

internal static class TypedBaseBodyCodec {
    internal const byte FormatVersion = 5;
    internal static byte[] Encode(ObjectLayout layout, PreparedBaseBody body, SchemaCatalogView? catalog = null) {
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        writer.WriteByte(FormatVersion);
        if (catalog is not null && BootstrapBindings.IsEmployee(layout)) {
            throw new InvalidDataException("Employee layouts cannot escape into a business graph.");
        }
        if ((layout.Kind is ObjectStateKind.List or ObjectStateKind.Array or ObjectStateKind.Dictionary) && BootstrapBindings.IsBuiltin(layout)) {
            writer.WriteByte(2);
            TypeExprWireCodec.Write(ref writer, layout.Type);
        } else if (catalog is null || layout.Kind == ObjectStateKind.String) {
            writer.WriteByte(0);
            writer.WriteVarUInt32(BootstrapBindings.Code(layout));
        } else {
            writer.WriteByte(1);
            writer.WriteVarUInt32(catalog.DescriptorId(layout).Value);
        }
        writer.WriteRawBytes(body.Body);
        return buffer.WrittenSpan.ToArray();
    }

    internal static (ObjectLayout Layout, byte[] Body) Decode(ReadOnlySpan<byte> body, SchemaCatalogView? catalog) {
        BareValueReader reader = new(body);
        if (reader.ReadByte() != FormatVersion) { throw new InvalidDataException("Unknown employee/typed Base ABI; only version 5 is accepted."); }
        byte route = reader.ReadByte();
        ObjectLayout layout = route switch {
            0 => BootstrapBindings.Layout(reader.ReadVarUInt32()),
            1 when catalog is not null => catalog.ResolveLayout(new(reader.ReadVarUInt32())),
            2 => BuiltinObjectLayouts.Resolve(TypeExprWireCodec.Read(ref reader)),
            _ => throw new InvalidDataException("A dynamic descriptor requires its exact source Schema view."),
        };
        return (layout, body[reader.ConsumedCount..].ToArray());
    }
}

internal sealed class DecodedGraphRows {
    internal DecodedGraphRows(StoredGraphRevision snapshot, Dictionary<ObjectId, ObjectStateRecord> rows,
        Dictionary<ObjectId, ObjectStorageInfo> storage) {
        Snapshot = snapshot;
        Rows = new FrozenSortedDictionary<ObjectId, ObjectStateRecord>(new(rows));
        Storage = new FrozenSortedDictionary<ObjectId, ObjectStorageInfo>(new(storage));
    }
    internal StoredGraphRevision Snapshot { get; }
    internal IReadOnlyDictionary<ObjectId, ObjectStateRecord> Rows { get; }
    internal IReadOnlyDictionary<ObjectId, ObjectStorageInfo> Storage { get; }

    internal DecodedRevision ToDecodedRevision() => new(Snapshot.Address, Rows.Values,
        StringReadTable.FromDecoded(Rows.Values.Where(static row => row.Kind == ObjectStateKind.String)
            .Select(static row => (row.Id, row.StringContent))),
        Snapshot.ObjectHeads.ToDictionary(static pair => new ObjectId(pair.Key), static pair => pair.Value),
        objectStorage: Storage);
}

internal static class StoredGraphDecoder {
    internal static DecodedGraphRows Read(GraphRecordStore store, FrameAddress graph,
        ModelExecutionEnvironment? models = null, SchemaCatalogView? selected = null) {
        IEnumerable<ObjectVersionChain> chains = store.ReadObjectChains(graph, out StoredGraphRevision snapshot);
        Dictionary<ObjectId, ObjectStateRecord> rows = [];
        Dictionary<ObjectId, ObjectStorageInfo> storage = [];
        List<(ObjectStateRecord Row, ObjectReaderBinding Reader)> readers = [];
        Dictionary<FrameAddress, SchemaCatalogView> origins = [];
        ModelExecutionEnvironment? employees = models is null ? BootstrapBindings.CreateEnvironment() : null;
        foreach (ObjectVersionChain chain in chains) {
            uint id = chain.ObjectId;
            SchemaCatalogView? origin = null;
            if (chain.Entries[0].SchemaRoot is { } sourceSchema) {
                if (models is null) { throw new InvalidDataException("A meta graph cannot use a dynamic source view."); }
                if (!origins.TryGetValue(sourceSchema, out origin)) {
                    origin = SchemaCatalogView.Load(store, sourceSchema);
                    origins.Add(sourceSchema, origin);
                }
            }
            var decoded = TypedBaseBodyCodec.Decode(chain.Entries[0].Record.Body, origin);
            // A null model catalog is the meta reader, whose whitelist is deliberately finite.
            // Schema-free business container bodies use the standalone fixed reader instead.
            if (models is null && !BootstrapBindings.TryCode(decoded.Layout, out _)) {
                throw new InvalidDataException("A meta graph cannot contain structural business container objects.");
            }
            if (models is not null && BootstrapBindings.IsEmployee(decoded.Layout)) {
                throw new InvalidDataException("A business graph cannot contain fixed employee objects.");
            }
            if (selected is not null && !BootstrapBindings.IsBuiltin(decoded.Layout)) {
                _ = selected.DescriptorId(decoded.Layout); // Complete interpretation, not bare-ID equality.
            }
            ObjectReaderBinding reader = models is null ? BootstrapBindings.Reader(decoded.Layout, employees)
                : models.ResolveObjectReader(decoded.Layout);
            ObjectStateRecord row = reader.Read(new(id), new Bodies(chain, decoded.Body));
            rows.Add(row.Id, row);
            storage.Add(row.Id, new(chain.Head, chain.ReconstructionPayloadBytes));
            readers.Add((row, reader));
        }
        StateReferenceValidator validator = new(rows);
        foreach (var item in readers) { item.Reader.VisitReferences(item.Row, validator); }
        // Lookup equality can depend on referenced string content. Every stored row,
        // including unreachable rows, must pass before any Normalize/Upgrade can run.
        foreach (var item in readers) {
            if (item.Reader is DictionaryStateReader dictionary) {
                dictionary.ValidateLookupKeys(item.Row, id => rows.TryGetValue(id, out ObjectStateRecord? target)
                    ? target : throw new InvalidDataException($"Dictionary key object {id.Value} is not live."));
            }
        }
        return new(snapshot, rows, storage);
    }

    private sealed class Bodies(ObjectVersionChain chain, byte[] first) : IStateBodySource {
        public int Count => chain.Entries.Count;
        public ReadOnlySpan<byte> GetBody(int index) => index == 0 ? first : chain.Entries[index].Record.Body;
    }
}
