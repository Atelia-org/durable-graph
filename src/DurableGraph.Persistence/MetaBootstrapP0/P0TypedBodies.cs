using System.Buffers;
using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.MetaBootstrapP0;

internal static class P0TypedBodyCodec {
    internal static byte[] Encode(ObjectLayout layout, PreparedBaseBody body, P0CatalogView? catalog = null) {
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        writer.WriteByte(1);
        if (catalog is null || layout.Kind == ObjectStateKind.String) {
            writer.WriteByte(0);
            writer.WriteVarUInt32(P0FixedBindings.Code(layout));
        } else {
            writer.WriteByte(1);
            writer.WriteVarUInt32(catalog.DescriptorId(layout).Value);
        }
        writer.WriteRawBytes(body.Body);
        return buffer.WrittenSpan.ToArray();
    }

    internal static (ObjectLayout Layout, byte[] Body) Decode(ReadOnlySpan<byte> body, P0CatalogView? catalog) {
        BareValueReader reader = new(body);
        if (reader.ReadByte() != 1) { throw new InvalidDataException("Unknown P0 typed Base format."); }
        byte route = reader.ReadByte();
        uint id = reader.ReadVarUInt32();
        ObjectLayout layout = route switch {
            0 => P0FixedBindings.Layout(id),
            1 when catalog is not null => catalog.ResolveLayout(new(id)),
            _ => throw new InvalidDataException("A dynamic descriptor requires its exact source Schema view."),
        };
        return (layout, body[reader.ConsumedCount..].ToArray());
    }
}

internal sealed class P0DecodedRows {
    internal P0DecodedRows(P0GraphSnapshot snapshot, Dictionary<ObjectId, ObjectStateRecord> rows,
        Dictionary<ObjectId, ObjectStorageInfo> storage) {
        Snapshot = snapshot;
        Rows = new FrozenSortedDictionary<ObjectId, ObjectStateRecord>(new(rows));
        Storage = new FrozenSortedDictionary<ObjectId, ObjectStorageInfo>(new(storage));
    }
    internal P0GraphSnapshot Snapshot { get; }
    internal IReadOnlyDictionary<ObjectId, ObjectStateRecord> Rows { get; }
    internal IReadOnlyDictionary<ObjectId, ObjectStorageInfo> Storage { get; }
}

internal static class P0TypedReader {
    internal static P0DecodedRows Read(P0GraphStore store, FrameAddress graph,
        StateModelSnapshot? models = null, P0CatalogView? selected = null) {
        P0GraphSnapshot snapshot = store.ReadGraph(graph);
        Dictionary<ObjectId, ObjectStateRecord> rows = [];
        Dictionary<ObjectId, ObjectStorageInfo> storage = [];
        List<(ObjectStateRecord Row, ObjectReaderBinding Reader)> readers = [];
        Dictionary<FrameAddress, P0CatalogView> origins = [];
        foreach (uint id in snapshot.Objects.Keys) {
            P0ObjectChain chain = store.ReadObjectChain(graph, id);
            P0CatalogView? origin = null;
            if (chain.Entries[0].SchemaRoot is { } sourceSchema) {
                if (models is null) { throw new InvalidDataException("A meta graph cannot use a dynamic source view."); }
                if (!origins.TryGetValue(sourceSchema, out origin)) {
                    origin = P0CatalogView.Load(store, sourceSchema);
                    origins.Add(sourceSchema, origin);
                }
            }
            var decoded = P0TypedBodyCodec.Decode(chain.Entries[0].Record.Body, origin);
            if (selected is not null && decoded.Layout.Kind != ObjectStateKind.String) {
                _ = selected.DescriptorId(decoded.Layout); // Complete interpretation, not bare-ID equality.
            }
            ObjectReaderBinding reader = models is null ? P0FixedBindings.Reader(decoded.Layout)
                : models.ResolveObjectReader(decoded.Layout);
            ObjectStateRecord row = reader.Read(new(id), new Bodies(chain, decoded.Body));
            rows.Add(row.Id, row);
            storage.Add(row.Id, new(chain.Head, chain.ReconstructionPayloadBytes));
            readers.Add((row, reader));
        }
        StateReferenceValidator validator = new(rows);
        foreach (var item in readers) { item.Reader.VisitReferences(item.Row, validator); }
        return new(snapshot, rows, storage);
    }

    private sealed class Bodies(P0ObjectChain chain, byte[] first) : IStateBodySource {
        public int Count => chain.Entries.Count;
        public ReadOnlySpan<byte> GetBody(int index) => index == 0 ? first : chain.Entries[index].Record.Body;
    }
}
