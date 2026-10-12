using System.Buffers;
using Atelia.Data;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph.Persistence;

/// <summary>Small graph-address envelope; model and representation metadata remain in Schema/State.</summary>
internal static class GraphEnvelopeCodec {
    private static ReadOnlySpan<byte> Magic => "DGH1"u8;
    private const byte Version = 1;

    internal static byte[] Encode(FrameAddress revisionAddress, ObjectId rootId) {
        Validate(revisionAddress, rootId);
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryPayloadWriter(buffer);
        writer.WriteSpan(Magic);
        writer.WriteByte(Version);
        writer.WriteUInt32(revisionAddress.FileNumber);
        writer.WriteUInt64(revisionAddress.FrameTicket.Serialize());
        writer.WriteUInt32(rootId.Value);
        return buffer.WrittenSpan.ToArray();
    }

    internal static (FrameAddress RevisionAddress, ObjectId RootId) Decode(ReadOnlySpan<byte> payload) {
        var reader = new BinaryPayloadReader(payload);
        foreach (byte expected in Magic) {
            if (reader.ReadByte() != expected) { throw new InvalidDataException("Unknown graph envelope magic."); }
        }
        if (reader.ReadByte() != Version) { throw AuditFormat.Unsupported("Unknown graph envelope version."); }
        uint fileNumber = reader.ReadUInt32();
        SizedPtr ticket = SizedPtr.Deserialize(reader.ReadUInt64());
        ObjectId rootId = new(reader.ReadUInt32());
        reader.EnsureFullyConsumed();
        if (fileNumber == 0 || ticket.Offset < 4 || ticket.Length < 24) {
            throw new InvalidDataException("Invalid StateRevision address in graph envelope.");
        }
        FrameAddress address = new(fileNumber, ticket);
        Validate(address, rootId);
        return (address, rootId);
    }

    internal static GraphFrameKind DecodeKind(uint kind) => kind switch {
        (uint)GraphFrameKind.Event => GraphFrameKind.Event,
        (uint)GraphFrameKind.State => GraphFrameKind.State,
        _ => throw new InvalidDataException($"Unknown EventHistory graph kind {kind}."),
    };

    private static void Validate(FrameAddress address, ObjectId rootId) {
        if (address.FileNumber == 0 || address.FrameTicket.Offset < 4 || address.FrameTicket.Length < 24) {
            throw new InvalidDataException("Graph envelope requires a valid absolute RBF frame address.");
        }
        if (rootId.IsNull) { throw new InvalidDataException("Graph envelope requires a non-null root identity."); }
    }
}
