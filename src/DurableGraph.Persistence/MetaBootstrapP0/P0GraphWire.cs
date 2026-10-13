using System.Buffers;
using Atelia.Binary;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.MetaBootstrapP0;

// Default graph/batch/directory wire. Tests can encode malformed relations
// and append the resulting well-framed bytes through the real borrowed FrameStore.
internal static class P0GraphWire {
    internal const int MaxCollectionCount = 1_000_000;
    internal const uint BatchTag = 0x37424250; // PBB7
    internal const uint DirectoryTag = 0x37444250; // PBD7
    internal const uint GraphTag = 0x37474250; // PBG7
    private static ReadOnlySpan<byte> BatchMagic => "P0B1"u8;
    private static ReadOnlySpan<byte> DirectoryMagic => "P0D1"u8;
    private static ReadOnlySpan<byte> GraphMagic => "P0G1"u8;

    internal static byte[] EncodeBatch(FrameAddress? sourceGraph, FrameAddress? schemaRoot,
        IReadOnlyList<ObjectVersionRecord> records) {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0 || records.Count > MaxCollectionCount) {
            throw new ArgumentException("A batch requires a bounded nonempty record set.", nameof(records));
        }
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BareValueWriter(buffer);
        writer.WriteRawBytes(BatchMagic);
        WriteOptional(ref writer, sourceGraph);
        WriteOptional(ref writer, schemaRoot);
        writer.WriteVarUInt32((uint)records.Count);
        uint previous = 0;
        foreach (ObjectVersionRecord record in records) {
            if (record.ObjectId <= previous) { throw new ArgumentException("Batch IDs must be ascending and unique.", nameof(records)); }
            writer.WriteVarUInt32(record.ObjectId);
            ObjectVersionPayloadCodec.Write(ref writer, record.Kind, record.PriorAddress, record.Body);
            previous = record.ObjectId;
        }
        return buffer.WrittenSpan.ToArray();
    }

    internal static P0Batch DecodeBatch(ReadOnlySpan<byte> payload) {
        var reader = new BareValueReader(payload);
        RequireMagic(ref reader, BatchMagic);
        FrameAddress? sourceGraph = ReadOptional(ref reader), schemaRoot = ReadOptional(ref reader);
        uint count = reader.ReadVarUInt32();
        if (count == 0 || count > MaxCollectionCount || count > (uint)(reader.RemainingCount / 3)) {
            throw new InvalidDataException("Invalid batch record count.");
        }
        var records = new ObjectVersionRecord[(int)count];
        uint previous = 0;
        for (int i = 0; i < records.Length; i++) {
            uint id = reader.ReadVarUInt32();
            if (id <= previous) { throw new InvalidDataException("Batch IDs must be ascending and unique."); }
            records[i] = ObjectVersionPayloadCodec.ReadRecord(ref reader, id);
            previous = id;
        }
        reader.EnsureFullyConsumed();
        return new(sourceGraph, schemaRoot, new FrozenList<ObjectVersionRecord>(records));
    }

    internal static byte[] EncodeDirectory(FrameAddress? sourceGraph, ObjectVersionKind kind,
        FrameAddress? prior, ReadOnlySpan<byte> body) {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BareValueWriter(buffer);
        writer.WriteRawBytes(DirectoryMagic);
        WriteOptional(ref writer, sourceGraph);
        ObjectVersionPayloadCodec.Write(ref writer, kind, prior, body);
        return buffer.WrittenSpan.ToArray();
    }

    internal static P0DirectoryVersion DecodeDirectory(ReadOnlySpan<byte> payload) {
        var reader = new BareValueReader(payload);
        RequireMagic(ref reader, DirectoryMagic);
        FrameAddress? source = ReadOptional(ref reader);
        DecodedObjectVersionPayload version = ObjectVersionPayloadCodec.Read(ref reader);
        reader.EnsureFullyConsumed();
        return new(source, version.Kind, version.Prior, version.Body, version.EncodedPayloadBytes);
    }

    internal static byte[] EncodeGraph(P0GraphKind kind, FrameAddress? parent, ObjectId rootId,
        FrameAddress directoryRoot, FrameAddress? schemaRoot, FrameAddress? ownedBatch) {
        ValidateKind(kind);
        if (rootId.IsNull) { throw new ArgumentException("Graph root must be nonzero.", nameof(rootId)); }
        if (kind == P0GraphKind.Meta && schemaRoot is not null) { throw new ArgumentException("A fixed meta graph does not borrow a Schema root.", nameof(schemaRoot)); }
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BareValueWriter(buffer);
        writer.WriteRawBytes(GraphMagic);
        writer.WriteByte((byte)kind);
        WriteOptional(ref writer, parent);
        writer.WriteVarUInt32(rootId.Value);
        FrameAddressWireCodec.Write(ref writer, directoryRoot);
        WriteOptional(ref writer, schemaRoot);
        WriteOptional(ref writer, ownedBatch);
        return buffer.WrittenSpan.ToArray();
    }

    internal static P0GraphMetadata DecodeGraph(ReadOnlySpan<byte> payload) {
        var reader = new BareValueReader(payload);
        RequireMagic(ref reader, GraphMagic);
        P0GraphKind kind = (P0GraphKind)reader.ReadByte();
        ValidateKind(kind);
        FrameAddress? parent = ReadOptional(ref reader);
        ObjectId root = new(reader.ReadVarUInt32());
        if (root.IsNull) { throw new InvalidDataException("Graph root must be nonzero."); }
        FrameAddress directory = FrameAddressWireCodec.Read(ref reader);
        FrameAddress? schema = ReadOptional(ref reader), batch = ReadOptional(ref reader);
        if (kind == P0GraphKind.Meta && schema is not null) { throw new InvalidDataException("A fixed meta graph cannot borrow a Schema root."); }
        reader.EnsureFullyConsumed();
        return new(kind, parent, root, directory, schema, batch);
    }

    internal static void WriteOptional(ref BareValueWriter writer, FrameAddress? address) {
        writer.WriteBoolean(address.HasValue);
        if (address is { } actual) { FrameAddressWireCodec.Write(ref writer, actual); }
    }

    internal static FrameAddress? ReadOptional(ref BareValueReader reader) =>
        reader.ReadBoolean() ? FrameAddressWireCodec.Read(ref reader) : null;

    private static void RequireMagic(ref BareValueReader reader, ReadOnlySpan<byte> magic) {
        if (!reader.ReadRawBytes(magic.Length).SequenceEqual(magic)) { throw new InvalidDataException("Unknown P0 frame format."); }
    }

    private static void ValidateKind(P0GraphKind kind) {
        if (kind is not P0GraphKind.Meta and not P0GraphKind.State and not P0GraphKind.Event) {
            throw new InvalidDataException("Unknown P0 graph role.");
        }
    }
}
