using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class StoredGraphBodySourceTests {
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Reader_borrows_only_the_Base_suffix_and_each_complete_Delta(int route) {
        DurableSchema user = new("BodySource", 1);
        SchemaCatalogView catalog = new SchemaCatalogWorkspace(static () => { }).PrepareOwned([], [user]).View;
        ObjectLayout layout = route switch {
            0 => ObjectLayout.String,
            1 => ObjectLayout.ForDurable(user),
            _ => ObjectLayout.ForList(new(new(1, TypeTag.Int32))),
        };
        byte[] raw = [7, 8];
        ObjectVersionRecord first = ObjectVersionRecord.CreateBase(1, TypedBaseBodyCodec.Encode(layout, new(raw), catalog));
        Assert.Equal((byte)route, first.Body[1]);
        var address = StorageTestData.TestAddress();
        var head = StorageTestData.TestAddress(offset: 256);
        ObjectVersionRecord delta = ObjectVersionRecord.CreateDelta(1, address, [0xFF]);
        ObjectVersionChain chain = new(1, head, [new(first, address, null, null, 0), new(delta, head, null, null, 0)]);

        var header = TypedBaseBodyCodec.ReadHeader(first.Body, catalog);
        Assert.Equal(layout, header.Layout);
        var bodies = new StoredGraphDecoder.ChainBodies(chain, header.Length);
        Assert.Equal(2, bodies.Count);
        Assert.True(bodies.GetBody(0).SequenceEqual(raw));
        Assert.True(first.Body.Overlaps(bodies.GetBody(0), out int headerLength));
        Assert.Equal(first.Body.Length - raw.Length, headerLength);
        Assert.True(delta.Body == bodies.GetBody(1));
    }
}
