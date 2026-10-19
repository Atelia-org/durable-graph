using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence;

// The Head contains only an optional graph address. Directory contents use the
// existing Dictionary graph representation, independently of this fixed header.
internal static class ManagementDirectoryPointer {
    private const int HeaderSize = 5;

    internal static byte[] Encode(ManagementDirectoryKind kind, FrameAddress? graph) {
        ReadOnlySpan<byte> magic = Magic(kind);
        if (graph is { } address) { FrameAddressValidator.ValidateRequired(address, nameof(graph)); }
        byte[] payload = new byte[HeaderSize + (graph.HasValue ? FrameAddress.EncodedSize : 0)];
        magic.CopyTo(payload);
        if (graph is { } selected) {
            payload[4] = 1;
            if (!selected.TryWrite(payload.AsSpan(HeaderSize))) {
                throw new InvalidDataException("The management directory graph address cannot be encoded.");
            }
        }
        return payload;
    }

    internal static FrameAddress? Decode(ManagementDirectoryKind kind, ReadOnlySpan<byte> payload) {
        ReadOnlySpan<byte> magic = Magic(kind);
        if (payload.Length < HeaderSize || !payload[..4].SequenceEqual(magic)) {
            throw new InvalidDataException("Unknown management directory pointer format.");
        }
        if (payload[4] == 0 && payload.Length == HeaderSize) { return null; }
        if (payload[4] != 1 || payload.Length != HeaderSize + FrameAddress.EncodedSize ||
            !FrameAddress.TryRead(payload[HeaderSize..], out FrameAddress graph)) {
            throw new InvalidDataException("Invalid management directory graph pointer.");
        }
        return graph;
    }

    private static ReadOnlySpan<byte> Magic(ManagementDirectoryKind kind) => kind switch {
        ManagementDirectoryKind.Branches => "P0R3"u8,
        ManagementDirectoryKind.Bookmarks => "P0T2"u8,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
