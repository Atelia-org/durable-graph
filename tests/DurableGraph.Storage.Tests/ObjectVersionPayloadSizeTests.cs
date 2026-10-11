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
        FrameAddress prior = Address(1, 32);
        byte[] body = new byte[bodyLength];
        StateRevision full = StateRevision.CreateObjectHeadMapBase(null, [ObjectVersionRecord.CreateBase(uint.MaxValue, body)], []);
        StateRevision delta = StateRevision.CreateObjectHeadMapDelta(prior, [ObjectVersionRecord.CreateDelta(uint.MaxValue, prior, body)], []);
        Assert.Equal(baseBytes, StateRevisionWireReader.Read(Encode(full)).LocalObjects[0].EncodedPayloadBytes);
        Assert.Equal(baseBytes + FrameAddress.EncodedSize, StateRevisionWireReader.Read(Encode(delta)).LocalObjects[0].EncodedPayloadBytes);
        Assert.Equal((long)baseBytes, ObjectVersionPayloadSize.GetBasePayloadBytes(bodyLength));
        Assert.Equal(baseBytes + (long)FrameAddress.EncodedSize, ObjectVersionPayloadSize.GetDeltaPayloadBytes(bodyLength, prior));
    }

    [Fact]
    public void Address_numeric_values_do_not_change_payload_size() {
        FrameAddress[] priors = [Address(1, 32), Address(128, 4096), Address(uint.MaxValue, 65536)];
        foreach (FrameAddress prior in priors) {
            StateRevision revision = StateRevision.CreateObjectHeadMapDelta(prior, [ObjectVersionRecord.CreateDelta(1, prior, [99])], []);
            Assert.Equal(15, StateRevisionWireReader.Read(Encode(revision)).LocalObjects[0].EncodedPayloadBytes);
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
}
