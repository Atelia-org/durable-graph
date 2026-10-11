using System.Buffers;
using Atelia.Binary;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence;

/// <summary>The graph role, business parent and root of the Revision in the same frame.</summary>
internal static class GraphMetadataCodec {
    private static ReadOnlySpan<byte> Magic => "DGH2"u8;
    private const byte Version = 1;

    internal static byte[] Encode(GraphFrameKind kind, FrameAddress? parent, ObjectId rootId) {
        ValidateKind(kind);
        if (rootId.IsNull) { throw new InvalidDataException("A graph requires a non-null root identity."); }
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BareValueWriter(buffer);
        writer.WriteRawBytes(Magic);
        writer.WriteByte(Version);
        writer.WriteByte((byte)kind);
        writer.WriteBoolean(parent.HasValue);
        if (parent is { } address) {
            Span<byte> bytes = stackalloc byte[FrameAddress.EncodedSize];
            if (!address.TryWrite(bytes)) { throw new InvalidDataException("A graph parent requires a valid address."); }
            writer.WriteRawBytes(bytes);
        }
        writer.WriteVarUInt32(rootId.Value);
        return buffer.WrittenSpan.ToArray();
    }

    internal static (GraphFrameKind Kind, FrameAddress? Parent, ObjectId RootId) Decode(ReadOnlySpan<byte> meta) {
        var reader = new BareValueReader(meta);
        if (!reader.ReadRawBytes(Magic.Length).SequenceEqual(Magic)) { throw new InvalidDataException("Unknown graph metadata magic."); }
        if (reader.ReadByte() != Version) { throw new InvalidDataException("Unknown graph metadata version."); }
        GraphFrameKind kind = (GraphFrameKind)reader.ReadByte();
        ValidateKind(kind);
        FrameAddress? parent = null;
        if (reader.ReadBoolean()) {
            if (!FrameAddress.TryRead(reader.ReadRawBytes(FrameAddress.EncodedSize), out var address)) {
                throw new InvalidDataException("Invalid graph business-parent address.");
            }
            parent = address;
        }
        ObjectId rootId = new(reader.ReadVarUInt32());
        if (rootId.IsNull) { throw new InvalidDataException("A graph requires a non-null root identity."); }
        reader.EnsureFullyConsumed();
        return (kind, parent, rootId);
    }

    private static void ValidateKind(GraphFrameKind kind) {
        if (kind is not GraphFrameKind.Event and not GraphFrameKind.State) { throw new InvalidDataException("Unknown graph kind."); }
    }
}
