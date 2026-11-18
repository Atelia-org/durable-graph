using Atelia.DurableGraph;

[DurableType("DurableText.ReaderAdmission.Control", 1)]
public sealed partial class Control : IDurableObject {
    [DurableField(1)] public string Value = "plain-before-text";
}

[DurableType("DurableText.Consumer.World", 1)]
public sealed partial class ReaderWorld : IDurableObject {
    [DurableField(1)] public GraphRef Original;
    [DurableField(2)] public GraphRef Latest;
}
