using System.Buffers.Binary;
using Atelia.Data;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Storage.Tests;

internal static class StorageTestFrames {
    // A numeric codec witness is deliberately not a claim that a frame exists.
    internal static FrameAddress Address(uint fileNumber, long offset, int length = 32) {
        Span<byte> bytes = stackalloc byte[FrameAddress.EncodedSize];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, fileNumber);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[4..], SizedPtr.Create(offset, length).Packed);
        if (!FrameAddress.TryRead(bytes, out FrameAddress address)) {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }
        return address;
    }

    internal static byte[] EncodeAddress(FrameAddress address) {
        byte[] bytes = new byte[FrameAddress.EncodedSize];
        Assert.True(address.TryWrite(bytes));
        return bytes;
    }
}
