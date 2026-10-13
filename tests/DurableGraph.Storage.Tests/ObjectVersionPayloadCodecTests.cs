using System.Buffers;
using Atelia.Binary;
using Atelia.FrameStore;
using static Atelia.DurableGraph.Storage.Tests.StorageTestFrames;

namespace Atelia.DurableGraph.Storage.Tests;

// These are the shared keyed-object/directory payload bytes, not the retired
// frame format or a substitute raw Store.
public sealed class ObjectVersionPayloadCodecTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Base_and_Delta_have_independent_payload_bytes_and_preserve_the_following_field(bool delta) {
        FrameAddress? prior = delta ? Address(3, 96) : null;
        ObjectVersionKind kind = delta ? ObjectVersionKind.Delta : ObjectVersionKind.Base;
        byte[] golden = delta ? [2, .. EncodeAddress(prior!.Value), 2, 0xaa, 0xbb] : [1, 2, 0xaa, 0xbb];
        ArrayBufferWriter<byte> bytes = new();
        Write(bytes, kind, prior, [0xaa, 0xbb]);
        Assert.Equal(golden, bytes.WrittenSpan.ToArray());
        BareValueWriter following = new(bytes);
        following.WriteByte(91);
        BareValueReader reader = new(bytes.WrittenSpan);
        DecodedObjectVersionPayload payload = ObjectVersionPayloadCodec.Read(ref reader);
        Assert.Equal(kind, payload.Kind);
        Assert.Equal(prior, payload.Prior);
        Assert.Equal(new byte[] { 0xaa, 0xbb }, payload.Body);
        Assert.Equal(golden.Length, payload.EncodedPayloadBytes);
        Assert.Equal(91, reader.ReadByte());
        reader.EnsureFullyConsumed();
    }

    [Fact]
    public void Redundant_body_length_keeps_actual_consumption_and_owned_bytes_while_rewrite_is_canonical() {
        byte[] encoded = [1, 0x82, 0, 0x10, 0x20];
        BareValueReader payloadReader = new(encoded);
        DecodedObjectVersionPayload payload = ObjectVersionPayloadCodec.Read(ref payloadReader);
        payloadReader.EnsureFullyConsumed();
        BareValueReader recordReader = new(encoded);
        ObjectVersionRecord record = ObjectVersionPayloadCodec.ReadRecord(ref recordReader, uint.MaxValue);
        recordReader.EnsureFullyConsumed();
        Assert.Equal(5, payload.EncodedPayloadBytes);
        Assert.Equal(5, record.EncodedPayloadBytes);
        Assert.Equal(uint.MaxValue, record.ObjectId);
        encoded.AsSpan().Fill(0xff);
        byte[] copy = record.Body.ToArray();
        copy.AsSpan().Clear();
        Assert.Equal(new byte[] { 0x10, 0x20 }, payload.Body);
        Assert.Equal(new byte[] { 0x10, 0x20 }, record.Body.ToArray());
        ArrayBufferWriter<byte> rewritten = new();
        Write(rewritten, record.Kind, record.PriorAddress, record.Body.ToArray());
        Assert.Equal(new byte[] { 1, 2, 0x10, 0x20 }, rewritten.WrittenSpan.ToArray());
        Assert.Equal(4L, ObjectVersionPayloadSize.GetBasePayloadBytes(record.Body.Length));
    }

    [Theory]
    [MemberData(nameof(MalformedPayloads))]
    public void Invalid_kinds_body_lengths_and_priors_fail_in_both_shared_read_routes(byte[] bytes) {
        Exception? payloadError = Record.Exception(() => Read(bytes, keyed: false));
        Exception? recordError = Record.Exception(() => Read(bytes, keyed: true));
        Assert.True(payloadError is InvalidDataException or EndOfStreamException, $"Unexpected payload failure: {payloadError}");
        Assert.True(recordError is InvalidDataException or EndOfStreamException, $"Unexpected record failure: {recordError}");
    }

    public static TheoryData<byte[]> MalformedPayloads => new() {
        new byte[] { 0 },
        new byte[] { 255 },
        new byte[] { 1, 0x80, 0x80, 0x80, 0x80, 8 }, // Body length exceeds Int32.
        new byte[] { 1, 127, 0 }, // Body length exceeds remaining payload.
        new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, // Invalid 12-byte opaque prior.
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_truncated_payload_prefix_is_rejected(bool delta) {
        byte[] golden = delta ? [2, .. EncodeAddress(Address(1, 32)), 2, 0x10, 0x20] : [1, 2, 0x10, 0x20];
        for (int length = 0; length < golden.Length; length++) {
            byte[] prefix = golden[..length];
            Exception? error = Record.Exception(() => Read(prefix, keyed: true));
            Assert.True(error is InvalidDataException or EndOfStreamException, $"Prefix {length}: {error}");
        }
    }

    [Theory]
    [InlineData(ObjectVersionKind.Base, true)]
    [InlineData(ObjectVersionKind.Delta, false)]
    [InlineData((ObjectVersionKind)0, false)]
    [InlineData((ObjectVersionKind)255, true)]
    public void Incompatible_kind_or_prior_is_rejected_before_any_output(ObjectVersionKind kind, bool hasPrior) {
        ArrayBufferWriter<byte> bytes = new();
        Assert.ThrowsAny<ArgumentException>(() => Write(bytes, kind, hasPrior ? Address(1, 32) : null, []));
        Assert.Equal(0, bytes.WrittenCount);
    }

    [Fact]
    public void Default_Delta_prior_is_rejected_before_any_output() {
        ArrayBufferWriter<byte> bytes = new();
        Assert.Throws<ArgumentOutOfRangeException>(() => Write(bytes, ObjectVersionKind.Delta, default(FrameAddress), []));
        Assert.Equal(0, bytes.WrittenCount);
    }

    private static void Write(ArrayBufferWriter<byte> bytes, ObjectVersionKind kind, FrameAddress? prior, byte[] body) {
        BareValueWriter writer = new(bytes);
        ObjectVersionPayloadCodec.Write(ref writer, kind, prior, body);
    }

    private static void Read(byte[] bytes, bool keyed) {
        BareValueReader reader = new(bytes);
        if (keyed) { _ = ObjectVersionPayloadCodec.ReadRecord(ref reader, 1); }
        else { _ = ObjectVersionPayloadCodec.Read(ref reader); }
        reader.EnsureFullyConsumed();
    }
}
