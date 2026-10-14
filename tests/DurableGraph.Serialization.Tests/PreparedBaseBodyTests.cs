using Atelia.Binary;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Serialization.Tests;

public sealed class PreparedBaseBodyTests {
    [Fact]
    public void OwnsItsInputSliceAndDoesNotExposeWritableStorage() {
        byte[] source = [0xFF, 0x02, 0x16, 0xFF];
        PreparedBaseBody prepared = new(source.AsSpan(1, 2));
        Array.Clear(source);
        byte[] copy = prepared.Body.ToArray();
        Array.Clear(copy);

        Assert.Equal<byte>([0x02, 0x16], prepared.Body.ToArray());
        Assert.Equal(typeof(ReadOnlySpan<byte>), typeof(PreparedBaseBody).GetProperty(nameof(PreparedBaseBody.Body))!.PropertyType);
        Assert.Null(typeof(PreparedBaseBody).GetProperty(nameof(PreparedBaseBody.Body))!.SetMethod);
        Assert.Empty(typeof(PreparedBaseBody).GetFields());
    }

    [Fact]
    public void EmptyBodyIsValid() {
        PreparedBaseBody prepared = new([]);
        Assert.True(prepared.Body.IsEmpty);
    }

    [Theory]
    [InlineData("", "00")]
    [InlineData("A", "0341")]
    [InlineData("\u00E9", "02E900")]
    [InlineData("\u4E2D", "022D4E")]
    public void StringPreparationUsesCanonicalContentAndOwnedReusableBytes(string value, string expectedHex) {
        PreparedBaseBody prepared = StringBodyPreparer.PrepareBase(value);
        byte[] copy = prepared.Body.ToArray();
        Array.Clear(copy);
        _ = StringBodyPreparer.PrepareBase("later preparation");

        Assert.Equal(Convert.FromHexString(expectedHex), prepared.Body.ToArray());
        BareValueReader reader = new(prepared.Body);
        string restored = reader.ReadString();
        reader.EnsureFullyConsumed();
        Assert.Equal(value, restored);
        if (value.Length == 0) { Assert.Same(string.Empty, restored); }
    }

    [Fact]
    public void StringPreparationPreservesUnpairedSurrogatesAndRejectsNull() {
        PreparedBaseBody prepared = StringBodyPreparer.PrepareBase(new string('\uD800', 1));
        Assert.Equal<byte>([0x02, 0x00, 0xD8], prepared.Body.ToArray());
        Assert.Throws<ArgumentNullException>(() => StringBodyPreparer.PrepareBase(null!));
    }
}
