using System.Buffers;
using Atelia.Binary;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Serialization.Tests;

public class StringBodyPreparerTests {
    public static TheoryData<string, byte[]> GoldenStrings => new() {
        { string.Empty, [0x00] },
        { "A", [0x03, 0x41] },
        { "你", [0x02, 0x60, 0x4F] },
        { "é", [0x02, 0xE9, 0x00] },
        { "🌟", [0x04, 0x3C, 0xD8, 0x1F, 0xDF] },
    };

    [Theory]
    [MemberData(nameof(GoldenStrings))]
    public void Prepared_content_uses_default_shortest_encoding(string value, byte[] expected) {
        Assert.Equal(expected, StringBodyPreparer.PrepareBase(value).Body.ToArray());
        Assert.Equal(value, Read(expected));
    }

    [Theory]
    [InlineData(new byte[] { 0x80, 0x00 }, "")]
    [InlineData(new byte[] { 0x01 }, "")]
    [InlineData(new byte[] { 0x02, 0x41, 0x00 }, "A")]
    [InlineData(new byte[] { 0x07, 0xE4, 0xBD, 0xA0 }, "你")]
    [InlineData(new byte[] { 0x05, 0xC3, 0xA9 }, "é")]
    public void Reader_accepts_bounded_redundant_representation(byte[] encoded, string expected) =>
        Assert.Equal(expected, Read(encoded));

    [Fact]
    public void Supplemental_and_unpaired_characters_keep_exact_code_units() {
        foreach (string value in new[] { "A🌟𐐷你", new string(['\uD800']), $"AAAAAAAAAA{new string(['\uD800'])}" }) {
            Assert.Equal(value, Read(StringBodyPreparer.PrepareBase(value).Body.ToArray()));
        }
        Assert.Equal(new byte[] { 0x02, 0x00, 0xD8 }, StringBodyPreparer.PrepareBase(new string(['\uD800'])).Body.ToArray());
    }

    [Theory]
    [InlineData(new byte[] { 0x03, 0xFF })]
    [InlineData(new byte[] { 0x05, 0xC0, 0x80 })]
    [InlineData(new byte[] { 0x07, 0xED, 0xA0, 0x80 })]
    [InlineData(new byte[] { 0x09, 0xF4, 0x90, 0x80, 0x80 })]
    [InlineData(new byte[] { 0xFE, 0xFF, 0xFF, 0xFF, 0x0F })]
    public void Invalid_string_leaves_cursor_unchanged(byte[] data) {
        BareValueReader reader = new(data);
        Exception? failure = null;
        try { reader.ReadString(); } catch (Exception exception) { failure = exception; }
        Assert.IsType<InvalidDataException>(failure);
        Assert.Equal(0, reader.ConsumedCount);
        Assert.Equal(data.Length, reader.RemainingCount);
    }

    [Theory]
    [InlineData(new byte[] { 0x05, 0x41 })]
    [InlineData(new byte[] { 0x04, 0x41, 0x00 })]
    public void Truncated_string_leaves_cursor_unchanged(byte[] data) {
        BareValueReader reader = new(data);
        Exception? failure = null;
        try { reader.ReadString(); } catch (Exception exception) { failure = exception; }
        Assert.IsType<EndOfStreamException>(failure);
        Assert.Equal(0, reader.ConsumedCount);
    }

    private static string Read(byte[] data) {
        BareValueReader reader = new(data);
        string value = reader.ReadString();
        reader.EnsureFullyConsumed();
        return value;
    }
}
