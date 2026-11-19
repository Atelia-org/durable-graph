using Atelia.DurableGraph.Serialization;

namespace Atelia.Text.Tests;

internal sealed class TestReceipt : LeafAcceptance {
    private bool _published;
    public override bool IsAccepted => _published;
    internal void Publish() => _published = true;

    // Simulate a successful State publication; consumption occurs only on a later edit or capture.
    internal static FrozenText Save(TextEditor editor) {
        TestReceipt receipt = new();
        FrozenText captured = editor.CaptureState(receipt);
        receipt.Publish();
        return captured;
    }
}
