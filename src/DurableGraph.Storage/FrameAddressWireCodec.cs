using Atelia.Binary;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Storage;

internal static class FrameAddressWireCodec {
    internal static void Write(ref BareValueWriter writer, FrameAddress address) {
        FrameAddressValidator.ValidateRequired(address, nameof(address));
        Span<byte> bytes = stackalloc byte[FrameAddress.EncodedSize];
        if (!address.TryWrite(bytes)) { throw new InvalidDataException("The frame address cannot be encoded."); }
        writer.WriteRawBytes(bytes);
    }

    internal static FrameAddress Read(ref BareValueReader reader) {
        BareValueReader candidate = reader;
        if (!FrameAddress.TryRead(candidate.ReadRawBytes(FrameAddress.EncodedSize), out FrameAddress address)) {
            throw new InvalidDataException("The encoded frame address is invalid.");
        }
        reader = candidate;
        return address;
    }
}
