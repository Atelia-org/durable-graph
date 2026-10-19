using System.Buffers.Binary;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence;

// Business entries select one graph and own an opaque preview. Kind and Schema
// remain graph facts; HeadStore owns the entry identity and inherited history.
internal static class BusinessEntryPayloadCodec {
    internal const int Overhead = 4 + FrameAddress.EncodedSize + sizeof(uint);
    internal const int MaxSummaryLength = Atelia.HeadStore.HeadStore.MaxHeadEntryPayloadLength - Overhead;

    internal static void ValidateSummaryLength(int length) {
        if (length < 0 || length > MaxSummaryLength) {
            throw new ArgumentOutOfRangeException(nameof(length), "The summary exceeds the HeadEntry payload budget.");
        }
    }

    internal static byte[] Encode(FrameAddress graph, ReadOnlySpan<byte> summary) {
        FrameAddressValidator.ValidateRequired(graph, nameof(graph));
        ValidateSummaryLength(summary.Length);
        byte[] payload = new byte[Overhead + summary.Length];
        "P0E1"u8.CopyTo(payload);
        if (!graph.TryWrite(payload.AsSpan(4, FrameAddress.EncodedSize))) {
            throw new InvalidDataException("The business graph address cannot be encoded.");
        }
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4 + FrameAddress.EncodedSize), (uint)summary.Length);
        summary.CopyTo(payload.AsSpan(Overhead));
        return payload;
    }

    internal static (FrameAddress Graph, byte[] Summary) Decode(ReadOnlySpan<byte> payload) {
        if (payload.Length < Overhead || payload.Length > Atelia.HeadStore.HeadStore.MaxHeadEntryPayloadLength ||
            !payload[..4].SequenceEqual("P0E1"u8) ||
            !FrameAddress.TryRead(payload.Slice(4, FrameAddress.EncodedSize), out FrameAddress graph)) {
            throw new InvalidDataException("Invalid business entry descriptor.");
        }
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4 + FrameAddress.EncodedSize));
        if (length != (uint)(payload.Length - Overhead)) {
            throw new InvalidDataException("The business summary length does not match the entry payload.");
        }
        return (graph, payload[Overhead..].ToArray());
    }
}
