using Atelia.Binary;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Persistence;

/// <summary>Encodes the persistent representation ID carried by Base records only.</summary>
/// <remarks>
/// Version 4 is the only supported format: a Bare UInt32 representation ID and the raw body.
/// This codec neither validates the raw body nor registers representations. Delta bodies have
/// no header and inherit the Base's exact interpretation.
/// </remarks>
internal static class BaseObjectBodyCodec {
    internal static EncodedBaseObjectBody EncodeString(PreparedBaseBody rawBody) =>
        Encode(RepresentationId.String, rawBody);

    /// <summary>Wraps a raw body using an ID already durably registered in the owning repository.</summary>
    internal static EncodedBaseObjectBody Encode(RepresentationId representationId, PreparedBaseBody rawBody) {
        if (representationId.Value == 0) {
            throw new ArgumentOutOfRangeException(nameof(representationId), "Representation ID zero is invalid.");
        }
        ArgumentNullException.ThrowIfNull(rawBody);
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        writer.WriteByte(4);
        writer.WriteVarUInt32(representationId.Value);
        buffer.Write(rawBody.Body);
        // TODO: After MVP, avoid the intermediate buffer/copy when wrapping prepared content.
        return new(buffer.WrittenSpan);
    }

    internal static DecodedBaseObjectBody Decode(ReadOnlySpan<byte> encodedBody, SchemaStore? schemas = null) {
        BareValueReader reader = new(encodedBody);
        byte version = reader.ReadByte();
        if (version != 4) {
            throw new InvalidDataException($"Unsupported Base type header version {version}.");
        }

        RepresentationId representationId = new(reader.ReadVarUInt32());
        if (representationId.Value == 0) { throw new InvalidDataException("Representation ID zero is invalid."); }
        ObjectLayout layout;
        if (representationId == RepresentationId.String && schemas is null) {
            layout = ObjectLayout.String;
        }
        else {
            if (schemas is null) {
                throw new InvalidDataException($"Representation ID {representationId.Value} requires its repository SchemaStore.");
            }
            layout = schemas.GetRepresentation(representationId);
        }

        return new(layout, representationId, encodedBody[reader.ConsumedCount..]);
    }
}
