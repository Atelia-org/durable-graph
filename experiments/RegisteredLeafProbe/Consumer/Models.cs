using Atelia.DurableGraph;
using Atelia.Text;
using Probe.Bitmap;

namespace Probe.Consumer;

[DurableType("Probe.RegisteredLeaves.World", 1)]
public sealed partial class ProbeWorld : IDurableObject {
    [DurableField(1)] public GraphRef Original;
    [DurableField(2)] public GraphRef Hot;
    [DurableField(3)] public GraphRef Latest;
}

[DurableType("Probe.RegisteredLeaves.Mixed", 1)]
public sealed partial class MixedEntry : IDurableObject {
    [DurableField(1)] public TextEditor Body = new();
    [DurableField(2)] public TextEditor? Alias;
    [DurableField(3)] public List<TextEditor> Texts = [];
    [DurableField(4)] public VariableBitmap Flags = new(0);
    [DurableField(5)] public VariableBitmap? OtherFlags;
    [DurableField(6)] public List<VariableBitmap> Bitmaps = [];
    [DurableField(7)] public VariableBitmap? Missing;
}

[DurableType("Probe.RegisteredLeaves.Builtin", 1)]
public sealed partial class BuiltinEntry : IDurableObject {
    [DurableField(1)] public DurableText Body = new();
}

[DurableType("Probe.RegisteredLeaves.Registered", 1)]
public sealed partial class RegisteredEntry : IDurableObject {
    [DurableField(1)] public TextEditor Body = new();
}
