using Atelia.FrameStore;
using static Atelia.DurableGraph.Storage.Tests.StorageTestFrames;

namespace Atelia.DurableGraph.Storage.Tests;

public sealed class ObjectVersionRecordTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Record_copies_input_and_exposes_only_readonly_content(bool delta) {
        byte[] input = [1, 2, 3, 4];
        FrameAddress prior = Address(1, 32);
        ObjectVersionRecord record = delta
            ? ObjectVersionRecord.CreateDelta(7, prior, input.AsSpan(1, 2))
            : ObjectVersionRecord.CreateBase(7, input.AsSpan(1, 2));
        input.AsSpan().Fill(99);
        byte[] outputCopy = record.Body.ToArray();
        outputCopy[0] = 88;

        Assert.Equal(7u, record.ObjectId);
        Assert.Equal([2, 3], record.Body.ToArray());
        Assert.Equal(delta ? ObjectVersionKind.Delta : ObjectVersionKind.Base, record.Kind);
        Assert.Equal(delta ? prior : (FrameAddress?)null, record.PriorAddress);
        Assert.Null(record.EncodedPayloadBytes);
        Assert.Equal(typeof(ReadOnlySpan<byte>), typeof(ObjectVersionRecord).GetProperty(nameof(record.Body))!.PropertyType);
        Assert.All(typeof(ObjectVersionRecord).GetProperties(), property => Assert.False(property.CanWrite));
    }

    [Fact]
    public void Empty_body_and_maximum_id_are_valid_but_zero_id_is_not() {
        FrameAddress prior = Address(1, 32);
        foreach (ObjectVersionRecord record in new[] {
            ObjectVersionRecord.CreateBase(uint.MaxValue, []),
            ObjectVersionRecord.CreateDelta(uint.MaxValue, prior, []),
        }) {
            Assert.Equal(uint.MaxValue, record.ObjectId);
            Assert.True(record.Body.IsEmpty);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => ObjectVersionRecord.CreateBase(0, [1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => ObjectVersionRecord.CreateDelta(0, prior, [1]));
    }

    [Fact]
    public void Delta_requires_a_nonempty_prior_address() {
        Assert.Throws<ArgumentOutOfRangeException>(() => ObjectVersionRecord.CreateDelta(1, default, []));
    }
}
