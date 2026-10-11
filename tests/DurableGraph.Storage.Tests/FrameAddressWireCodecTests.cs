using System.Buffers;
using Atelia.Binary;
using Atelia.FrameStore;
using static Atelia.DurableGraph.Storage.Tests.StorageTestFrames;

namespace Atelia.DurableGraph.Storage.Tests;

public sealed class FrameAddressWireCodecTests {
    [Fact]
    public void Opaque_address_uses_exact_fixed_width_and_preserves_following_field() {
        FrameAddress address = Address(7, 96);
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        FrameAddressWireCodec.Write(ref writer, address);
        writer.WriteByte(91);
        Assert.Equal(FrameAddress.EncodedSize + 1, buffer.WrittenCount);
        Assert.Equal(EncodeAddress(address), buffer.WrittenSpan[..FrameAddress.EncodedSize].ToArray());
        BareValueReader reader = new(buffer.WrittenSpan);
        Assert.Equal(address, FrameAddressWireCodec.Read(ref reader));
        Assert.Equal(91, reader.ReadByte());
        reader.EnsureFullyConsumed();
    }

    [Fact]
    public void Default_address_is_rejected_before_writing() {
        ArrayBufferWriter<byte> buffer = new();
        Assert.Throws<ArgumentOutOfRangeException>(() => Write(buffer, default));
        Assert.Equal(0, buffer.WrittenCount);
    }

    [Fact]
    public void Truncated_or_invalid_address_does_not_advance_reader() {
        byte[] address = EncodeAddress(Address(1, 32));
        for (int length = 0; length < address.Length; length++) {
            AssertReadRejected(address[..length]);
        }
        AssertReadRejected(new byte[FrameAddress.EncodedSize]);
    }

    private static void Write(ArrayBufferWriter<byte> buffer, FrameAddress address) {
        BareValueWriter writer = new(buffer);
        FrameAddressWireCodec.Write(ref writer, address);
    }

    private static void AssertReadRejected(byte[] source) {
        BareValueReader reader = new(source);
        try {
            _ = FrameAddressWireCodec.Read(ref reader);
            Assert.Fail("Invalid address was accepted.");
        } catch (Exception exception) when (exception is EndOfStreamException or InvalidDataException) {
            Assert.Equal(0, reader.ConsumedCount);
        }
    }
}
