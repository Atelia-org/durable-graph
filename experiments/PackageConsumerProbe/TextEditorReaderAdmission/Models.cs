using Atelia.DurableGraph;

[DurableType("TextEditor.ReaderAdmission.Control", 1)]
public sealed partial class Control : IDurableObject {
    [DurableField(1)] public string Value = "text-free control";
}

[DurableType("TextEditor.Consumer.World", 1)]
public sealed partial class ReaderWorld : IDurableObject {
    [DurableField(1)] public GraphRef Original;
    [DurableField(2)] public GraphRef Latest;
}

// Same declaration identities and field shapes as the producer. This application
// has the marker/protocol assembly and no implementation package or text codec.
[Atelia.DurableGraph.Serialization.DurableLeaf("Atelia.Text.TextEditor", 1)]
public sealed class UnregisteredText { }

[DurableType("TextEditor.Consumer.Entry", 1)]
public sealed partial class ReaderEntry : IDurableObject {
    [DurableField(1)] public UnregisteredText Body = new();
    [DurableField(2)] public UnregisteredText Alias = new();
    [DurableField(3)] public List<UnregisteredText> Bodies = [];
    [DurableField(4)] public UnregisteredText? Missing;
    [DurableField(5)] public UnregisteredText Empty = new();
    [DurableField(6)] public string Gist = "";
}
