using System.Buffers;
using Atelia.Binary;
using Atelia.FrameStore;
using static Atelia.DurableGraph.Storage.Tests.StorageTestFrames;

namespace Atelia.DurableGraph.Storage.Tests;

public sealed class StateRevisionWireTests {
    [Fact]
    public void Genesis_head_map_Base_has_stable_v4_golden_bytes() {
        StateRevision revision = StateRevision.CreateObjectHeadMapBase(null,
            [ObjectVersionRecord.CreateBase(128, []), ObjectVersionRecord.CreateBase(1, [0xaa, 0xbb])], []);
        byte[] golden = [4, 1, 0, 2, 1, 1, 2, 0xaa, 0xbb, 0x80, 1, 1, 0, 0];
        Assert.Equal(golden, Encode(revision));
        AssertRevisionEqual(revision, StateRevisionWireReader.Read(golden));
    }

    [Theory]
    [InlineData(ObjectHeadMapKind.Base)]
    [InlineData(ObjectHeadMapKind.Delta)]
    public void Mixed_records_parent_and_prior_use_one_fixed_address_width(ObjectHeadMapKind kind) {
        FrameAddress prior = Address(3, 96);
        byte[] link = EncodeAddress(prior);
        ObjectVersionRecord[] records = [ObjectVersionRecord.CreateDelta(128, prior, [0xfe]), ObjectVersionRecord.CreateBase(1, [0xab])];
        StateRevision revision = kind == ObjectHeadMapKind.Base
            ? StateRevision.CreateObjectHeadMapBase(prior, records, [])
            : StateRevision.CreateObjectHeadMapDelta(prior, records, []);
        byte[] golden = [4, (byte)kind, 1, .. link, 2, 1, 1, 1, 0xab, 0x80, 1, 2, .. link, 1, 0xfe, 0];
        Assert.Equal(golden, Encode(revision));
        StateRevision decoded = StateRevisionWireReader.Read(golden);
        AssertRevisionEqual(revision, decoded);
        Assert.Equal(3, decoded.LocalObjects[0].EncodedPayloadBytes);
        Assert.Equal(15, decoded.LocalObjects[1].EncodedPayloadBytes);
        AssertTruncationAndTrailingRejected(golden);
    }

    [Fact]
    public void Complete_map_round_trip_preserves_parent_and_sorted_external_heads() {
        StateRevision source = StateRevision.CreateObjectHeadMapBase(Address(8, 32),
            [ObjectVersionRecord.CreateBase(9, [90]), ObjectVersionRecord.CreateBase(1, [10])],
            [new(8, Address(1, 96)), new(2, Address(7, 64))]);
        AssertRevisionEqual(source, StateRevisionWireReader.Read(Encode(source)));
    }

    [Fact]
    public void Large_id_and_multibyte_body_length_have_independent_golden_encoding() {
        byte[] body = Enumerable.Range(0, 128).Select(static value => (byte)value).ToArray();
        byte[] golden = [4, 1, 0, 1, 0xff, 0xff, 0xff, 0xff, 0x0f, 1, 0x80, 1, .. body, 0];
        StateRevision revision = StateRevision.CreateObjectHeadMapBase(null, [ObjectVersionRecord.CreateBase(uint.MaxValue, body)], []);
        Assert.Equal(golden, Encode(revision));
        AssertRevisionEqual(revision, StateRevisionWireReader.Read(golden));
    }

    [Fact]
    public void Decoded_body_is_owned_and_original_consumption_includes_redundant_length() {
        // Count, id and byte-length all use legal bounded redundant forms.
        byte[] encoded = [4, 1, 0, 0x81, 0, 0x81, 0, 1, 0x82, 0, 0x10, 0x20, 0x80, 0];
        StateRevision revision = StateRevisionWireReader.Read(encoded);
        Assert.Equal(5, revision.LocalObjects[0].EncodedPayloadBytes);
        Assert.Equal(new byte[] { 4, 1, 0, 1, 1, 1, 2, 0x10, 0x20, 0 }, Encode(revision));
        encoded.AsSpan().Fill(0xff);
        byte[] copy = revision.LocalObjects[0].Body.ToArray();
        copy.AsSpan().Clear();
        Assert.Equal([0x10, 0x20], revision.LocalObjects[0].Body.ToArray());
    }

    [Fact]
    public void Every_truncated_prefix_and_any_trailing_byte_is_rejected() =>
        AssertTruncationAndTrailingRejected([4, 1, 0, 1, 1, 1, 2, 0x10, 0x20, 0]);

    [Theory]
    [InlineData(StateRevisionWireFormat.MaxCollectionCount)]
    [InlineData(StateRevisionWireFormat.MaxCollectionCount + 1)]
    public void Counts_are_bounded_before_allocation(int count) {
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        writer.WriteRawBytes([4, 1, 0]);
        writer.WriteVarUInt32((uint)count);
        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => StateRevisionWireReader.Read(buffer.WrittenSpan));
        Assert.Contains(count > StateRevisionWireFormat.MaxCollectionCount ? "provisional limit" : "remaining payload bounds", exception.Message);
    }

    [Theory]
    [InlineData(new byte[] { 4, 1, 0, 2, 1, 0, 0 })]
    [InlineData(new byte[] { 4, 1, 0, 0, 2, 1, 1, 5 })]
    public void Collection_minimum_entry_size_is_checked_before_allocation(byte[] encoded) {
        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => StateRevisionWireReader.Read(encoded));
        Assert.Contains("remaining payload bounds", exception.Message);
    }

    [Fact]
    public void Writer_rejects_oversize_collection_before_writing() {
        StateRevision revision = StateRevision.CreateObjectHeadMapBase(null,
            Enumerable.Range(1, StateRevisionWireFormat.MaxCollectionCount + 1).Select(value => ObjectVersionRecord.CreateBase((uint)value, [])), []);
        ArrayBufferWriter<byte> buffer = new();
        Assert.Throws<InvalidDataException>(() => StateRevisionWireWriter.Write(buffer, revision));
        Assert.Equal(0, buffer.WrittenCount);
    }

    [Theory]
    [MemberData(nameof(MalformedPayloads))]
    public void Malformed_payload_fails_closed(string description, byte[] payload) {
        Assert.False(string.IsNullOrWhiteSpace(description));
        AssertMalformed(payload);
    }

    public static TheoryData<string, byte[]> MalformedPayloads {
        get {
            byte[] link = EncodeAddress(Address(1, 32));
            byte[] empty = new byte[12];
            return new() {
                { "unsupported version", [255] },
                { "unknown map kind", [4, 255] },
                { "invalid parent marker", [4, 1, 255] },
                { "Delta requires parent", [4, 2, 0, 0, 0] },
                { "zero local ID", [4, 1, 0, 1, 0, 1, 0, 0] },
                { "duplicate local ID", [4, 1, 0, 2, 1, 1, 0, 1, 1, 0, 0] },
                { "descending local ID", [4, 1, 0, 2, 2, 1, 0, 1, 1, 0, 0] },
                { "unknown object kind", [4, 1, 0, 1, 1, 255, 0, 0] },
                { "zero object kind", [4, 1, 0, 1, 1, 0, 0, 0] },
                { "body length exceeds Int32", [4, 1, 0, 1, 1, 1, 0x80, 0x80, 0x80, 0x80, 8, 0] },
                { "body length exceeds remaining", [4, 1, 0, 1, 1, 1, 127, 0] },
                { "integer overflow", [4, 1, 0, 0xff, 0xff, 0xff, 0xff, 0x1f] },
                { "local external overlap", [4, 1, 0, 1, 1, 1, 0, 1, 1, .. link] },
                { "duplicate external ID", [4, 1, 0, 0, 2, 1, .. link, 1, .. link] },
                { "descending external ID", [4, 1, 0, 0, 2, 2, .. link, 1, .. link] },
                { "zero external ID", [4, 1, 0, 0, 1, 0, .. link] },
                { "local removed overlap", [4, 2, 1, .. link, 1, 1, 1, 0, 1, 1] },
                { "duplicate removed ID", [4, 2, 1, .. link, 0, 2, 1, 1] },
                { "descending removed ID", [4, 2, 1, .. link, 0, 2, 2, 1] },
                { "zero removed ID", [4, 2, 1, .. link, 0, 1, 0] },
                { "invalid parent", [4, 2, 1, .. empty, 0, 0] },
                { "invalid external", [4, 1, 0, 0, 1, 1, .. empty] },
                { "object Delta requires parent", [4, 1, 0, 1, 1, 2, .. link, 0, 0] },
                { "invalid prior", [4, 1, 1, .. link, 1, 1, 2, .. empty, 0, 0] },
            };
        }
    }

    private static void AssertTruncationAndTrailingRejected(byte[] golden) {
        for (int length = 0; length < golden.Length; length++) AssertMalformed(golden[..length]);
        AssertMalformed([.. golden, 0]);
        AssertMalformed([.. golden, 255]);
    }

    private static void AssertMalformed(byte[] payload) {
        Exception? exception = Record.Exception(() => StateRevisionWireReader.Read(payload));
        Assert.True(exception is InvalidDataException or EndOfStreamException, $"Unexpected exception: {exception}");
    }

    private static void AssertRevisionEqual(StateRevision expected, StateRevision actual) {
        Assert.Equal(expected.ParentRevisionAddress, actual.ParentRevisionAddress);
        Assert.Equal(expected.ObjectHeadMapKind, actual.ObjectHeadMapKind);
        Assert.Equal(expected.LocalObjectIds, actual.LocalObjectIds);
        Assert.Equal(expected.LocalObjects.Count, actual.LocalObjects.Count);
        for (int index = 0; index < expected.LocalObjects.Count; index++) {
            Assert.Equal(expected.LocalObjects[index].Kind, actual.LocalObjects[index].Kind);
            Assert.Equal(expected.LocalObjects[index].PriorAddress, actual.LocalObjects[index].PriorAddress);
            Assert.Equal(expected.LocalObjects[index].Body.ToArray(), actual.LocalObjects[index].Body.ToArray());
        }
        Assert.Equal(expected.RemovedObjectIds, actual.RemovedObjectIds);
        Assert.Equal(expected.ExternalObjectHeads.ToArray(), actual.ExternalObjectHeads.ToArray());
    }
}
