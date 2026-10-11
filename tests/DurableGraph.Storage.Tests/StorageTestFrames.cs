using System.Buffers;
using System.Buffers.Binary;
using Atelia.Data;
using Atelia.FrameStore;
using Atelia.Rbf;
using Frames = Atelia.FrameStore.FrameStore;

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

    internal static FrameAddress WithWrongLength(FrameAddress address) {
        byte[] bytes = EncodeAddress(address);
        SizedPtr ticket = SizedPtr.FromPacked(BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(4)));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(4), SizedPtr.Create(ticket.Offset, ticket.Length + 4).Packed);
        Assert.True(FrameAddress.TryRead(bytes, out FrameAddress wrong));
        return wrong;
    }

    internal static byte[] Encode(StateRevision revision) {
        ArrayBufferWriter<byte> bytes = new();
        StateRevisionWireWriter.Write(bytes, revision);
        Assert.Equal(bytes.WrittenCount, StateRevisionWireWriter.Measure(revision));
        return bytes.WrittenSpan.ToArray();
    }

    internal static FrameAddress RawAppend(Frames frames, StateRevision revision, ReadOnlySpan<byte> tailMeta = default) =>
        frames.Append(StateRevisionWireFormat.RbfTag, Encode(revision), tailMeta).Unwrap();

    internal static long FrameCount(Frames frames) => frames.Inventory(_ => { }).Unwrap();

    internal static long RotationThreshold =>
        RbfScanBoundary.Empty.EndExclusive + RbfFile.MeasureWriteSize(24, 0).Unwrap().AppendLength;
}
