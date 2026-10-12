using Atelia.Binary;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Storage;

// Shared by keyed object records and the DB-087 direct directory root. The
// payload has no ObjectId: membership belongs to the containing graph/batch.
internal static class ObjectVersionPayloadCodec {
    internal static void Write(ref BareValueWriter writer, ObjectVersionKind kind,
        FrameAddress? prior, ReadOnlySpan<byte> body) {
        if (kind == ObjectVersionKind.Delta && prior is { } address) {
            FrameAddressValidator.ValidateRequired(address, nameof(prior));
        } else if (kind != ObjectVersionKind.Base || prior is not null) {
            throw new ArgumentException("A Base has no prior; a Delta requires a prior.", nameof(prior));
        }
        writer.WriteByte((byte)kind);
        if (prior is { } priorAddress) { FrameAddressWireCodec.Write(ref writer, priorAddress); }
        writer.WriteBytes(body);
    }

    internal static DecodedObjectVersionPayload Read(ref BareValueReader reader) {
        ReadParts(ref reader, out var kind, out var prior, out var body, out int bytes);
        return new(kind, prior, body.ToArray(), bytes);
    }

    internal static ObjectVersionRecord ReadRecord(ref BareValueReader reader, uint objectId) {
        ReadParts(ref reader, out var kind, out var prior, out var body, out int bytes);
        return ObjectVersionRecord.FromDecoded(objectId, kind, prior, body, bytes);
    }

    private static void ReadParts(ref BareValueReader reader, out ObjectVersionKind kind,
        out FrameAddress? prior, out ReadOnlySpan<byte> body, out int bytes) {
        int start = reader.ConsumedCount;
        kind = reader.ReadByte() switch {
            (byte)ObjectVersionKind.Base => ObjectVersionKind.Base,
            (byte)ObjectVersionKind.Delta => ObjectVersionKind.Delta,
            byte value => throw new InvalidDataException($"Unknown ObjectVersion kind {value}."),
        };
        prior = kind == ObjectVersionKind.Delta ? FrameAddressWireCodec.Read(ref reader) : null;
        body = reader.ReadBytes();
        bytes = reader.ConsumedCount - start;
    }
}

internal readonly record struct DecodedObjectVersionPayload(
    ObjectVersionKind Kind, FrameAddress? Prior, byte[] Body, int EncodedPayloadBytes) {
    internal ObjectVersionRecord ToRecord(uint objectId) =>
        ObjectVersionRecord.FromDecoded(objectId, Kind, Prior, Body, EncodedPayloadBytes);
}
