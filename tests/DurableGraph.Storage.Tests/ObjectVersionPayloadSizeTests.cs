using System.Buffers;
using Atelia.Binary;
using Atelia.FrameStore;
using static Atelia.DurableGraph.Storage.Tests.StorageTestFrames;

namespace Atelia.DurableGraph.Storage.Tests;

public sealed class ObjectVersionPayloadSizeTests {
    [Theory]
    [InlineData(0, 2)]
    [InlineData(127, 129)]
    [InlineData(128, 131)]
    [InlineData(16383, 16386)]
    [InlineData(16384, 16388)]
    public void Exact_Base_and_Delta_sizes_match_wire_body_length_boundaries(int bodyLength, int baseBytes) {
        Assert.Equal(12, FrameAddress.EncodedSize);
        FrameAddress prior = Address(1, 32);
        byte[] body = new byte[bodyLength];
        byte[] baseWire = Encode(ObjectVersionKind.Base, null, body);
        byte[] deltaWire = Encode(ObjectVersionKind.Delta, prior, body);
        ObjectVersionRecord decodedBase = Decode(baseWire);
        ObjectVersionRecord decodedDelta = Decode(deltaWire);
        Assert.Equal(baseBytes, baseWire.Length);
        Assert.Equal(baseBytes + 12, deltaWire.Length);
        Assert.Equal(baseBytes, decodedBase.EncodedPayloadBytes);
        Assert.Equal(baseBytes + 12, decodedDelta.EncodedPayloadBytes);
        Assert.Equal(body, decodedBase.Body.ToArray());
        Assert.Equal(body, decodedDelta.Body.ToArray());
        Assert.Equal(prior, decodedDelta.PriorAddress);
        Assert.Equal((long)baseBytes, ObjectVersionPayloadSize.GetBasePayloadBytes(bodyLength));
        Assert.Equal(baseBytes + 12L, ObjectVersionPayloadSize.GetDeltaPayloadBytes(bodyLength, prior));
        Assert.Equal(2L * baseBytes + 12, decodedBase.EncodedPayloadBytes!.Value + (long)decodedDelta.EncodedPayloadBytes!.Value);
    }

    [Fact]
    public void Address_numeric_values_do_not_change_payload_size() {
        FrameAddress[] priors = [Address(1, 32), Address(128, 4096), Address(uint.MaxValue, 65536)];
        foreach (FrameAddress prior in priors) {
            byte[] wire = Encode(ObjectVersionKind.Delta, prior, [99]);
            ObjectVersionRecord decoded = Decode(wire);
            Assert.Equal(15, wire.Length);
            Assert.Equal(15, decoded.EncodedPayloadBytes);
            Assert.Equal(prior, decoded.PriorAddress);
            Assert.Equal(15L, ObjectVersionPayloadSize.GetDeltaPayloadBytes(1, prior));
        }
    }

    [Fact]
    public void Max_body_length_uses_long_arithmetic_without_allocation() {
        Assert.Equal(2147483653L, ObjectVersionPayloadSize.GetBasePayloadBytes(int.MaxValue));
        Assert.Equal(2147483665L, ObjectVersionPayloadSize.GetDeltaPayloadBytes(int.MaxValue, Address(1, 32)));
    }

    [Fact]
    public void Negative_length_and_default_prior_are_rejected() {
        Assert.Throws<ArgumentOutOfRangeException>(() => ObjectVersionPayloadSize.GetBasePayloadBytes(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ObjectVersionPayloadSize.GetDeltaPayloadBytes(-1, Address(1, 32)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ObjectVersionPayloadSize.GetDeltaPayloadBytes(0, default));
    }

    private static byte[] Encode(ObjectVersionKind kind, FrameAddress? prior, byte[] body) {
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        ObjectVersionPayloadCodec.Write(ref writer, kind, prior, body);
        return bytes.WrittenSpan.ToArray();
    }

    private static ObjectVersionRecord Decode(byte[] bytes) {
        BareValueReader reader = new(bytes);
        // The containing batch supplies this key; its width is absent from the payload.
        ObjectVersionRecord record = ObjectVersionPayloadCodec.ReadRecord(ref reader, uint.MaxValue);
        reader.EnsureFullyConsumed();
        return record;
    }
}
