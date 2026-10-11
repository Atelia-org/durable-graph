namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class GraphMetadataCodecTests {
    [Fact]
    public void GoldenBytesFreezeRoleParentAndRootWithoutRevisionLocator() {
        byte[] golden = Convert.FromHexString("4447483201020007");
        Assert.Equal(golden, GraphMetadataCodec.Encode(GraphFrameKind.State, null, new ObjectId(7)));
        Assert.Equal((GraphFrameKind.State, (FrameAddress?)null, new ObjectId(7)), GraphMetadataCodec.Decode(golden));
        for (int length = 0; length < golden.Length; length++) {
            byte[] truncated = golden[..length];
            Assert.Throws<EndOfStreamException>(() => GraphMetadataCodec.Decode(truncated));
        }
        FrameAddress parent = TestAddress();
        byte[] linked = GraphMetadataCodec.Encode(GraphFrameKind.Event, parent, new ObjectId(9));
        Assert.Equal((GraphFrameKind.Event, (FrameAddress?)parent, new ObjectId(9)), GraphMetadataCodec.Decode(linked));
        Assert.Equal(golden.Length + FrameAddress.EncodedSize, linked.Length);
    }

    [Theory]
    [InlineData("0047483201020007")]
    [InlineData("4447483202020007")]
    [InlineData("4447483201000007")]
    [InlineData("4447483201030007")]
    [InlineData("4447483201020207")]
    [InlineData("4447483201020000")]
    [InlineData("444748320102000700")]
    public void InvalidMetadataFailsClosed(string hex) =>
        Assert.Throws<InvalidDataException>(() => GraphMetadataCodec.Decode(Convert.FromHexString(hex)));

    [Fact]
    public void InvalidWriteValuesAreRejectedAndBoundedRedundantRootIsAccepted() {
        Assert.Throws<InvalidDataException>(() => GraphMetadataCodec.Encode((GraphFrameKind)0, null, new(1)));
        Assert.Throws<InvalidDataException>(() => GraphMetadataCodec.Encode(GraphFrameKind.State, default(FrameAddress), new(1)));
        Assert.Throws<InvalidDataException>(() => GraphMetadataCodec.Encode(GraphFrameKind.State, null, default));
        Assert.Equal(new ObjectId(7), GraphMetadataCodec.Decode(Convert.FromHexString("444748320102008700")).RootId);
    }
}
