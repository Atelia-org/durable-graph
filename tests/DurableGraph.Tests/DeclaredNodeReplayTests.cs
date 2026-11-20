using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;

namespace Atelia.DurableGraph.Tests;

public sealed class DeclaredNodeReplayTests {
    private static readonly DeclaredNodeReplay Replay = new([
        new(new(DeclaredSlotKind.Int32)),
        new(new(DeclaredSlotKind.References, TypeExpr.Builtin(TypeTag.String))),
    ]);

    // Integer zero and two references, IDs 3 and 4.
    private static DeclaredCell[] Baseline() => ReadBase("00020304");

    private static DeclaredCell[] ReadBase(string hex) {
        BareValueReader input = new(Convert.FromHexString(hex));
        DeclaredCell[] result = Replay.ReadBase(ref input);
        input.EnsureFullyConsumed();
        return result;
    }

    private static DeclaredCell[] ApplyDelta(DeclaredCell[] previous, string hex) {
        BareValueReader input = new(Convert.FromHexString(hex));
        DeclaredCell[] result = Replay.ApplyDelta(ref input, previous);
        input.EnsureFullyConsumed();
        return result;
    }

    [Theory]
    [InlineData("800000")]     // Overlong scalar.
    [InlineData("0002FF")]     // Count exceeds remaining bytes.
    [InlineData("00FFFFFFFF0F")] // Count exceeds CLR array bounds.
    [InlineData("00018300")]   // Overlong object ID.
    public void RejectsMalformedBase(string hex) =>
        Assert.Throws<InvalidDataException>(() => ReadBase(hex));

    [Theory]
    [InlineData("")]               // No operations.
    [InlineData("0000")]           // Scalar unchanged.
    [InlineData("0201")]           // Slot outside the layout.
    [InlineData("00020004")]       // Same slot twice.
    [InlineData("0100020506")]     // Equal-length replacement requires a sparse patch.
    [InlineData("010100")]         // No changed entries.
    [InlineData("0101030900")]     // Third entry in a two-entry vector.
    [InlineData("0101010300")]     // Existing ID repeated.
    [InlineData("01010205010600")] // Descending positions after one valid change.
    [InlineData("0101020582000600")] // Overlong position after one valid change.
    public void BadDeltaPreservesItsPredecessor(string hex) {
        DeclaredCell[] previous = Baseline();
        Assert.Throws<InvalidDataException>(() => ApplyDelta(previous, hex));
        Assert.Equal(0u, previous[0].Word);
        Assert.Equal(new uint[] { 3, 4 }, previous[1].Items);
    }

    [Fact]
    public void IndependentDeltaChainsCanPatchResizeAndClearTheSameFrozenVector() {
        DeclaredCell[] previous = Baseline();
        DeclaredCell[] patched = ApplyDelta(previous, "0101020700");
        DeclaredCell[] grown = ApplyDelta(previous, "010003000403");
        DeclaredCell[] cleared = ApplyDelta(grown, "010000");
        Assert.Equal(new uint[] { 3, 4 }, previous[1].Items);
        Assert.Equal(new uint[] { 3, 7 }, patched[1].Items);
        Assert.Equal(new uint[] { 0, 4, 3 }, grown[1].Items);
        Assert.Empty(cleared[1].Items!);
    }
}
