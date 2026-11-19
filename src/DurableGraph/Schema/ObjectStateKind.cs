namespace Atelia.DurableGraph.Schema;

/// <summary>The in-memory content kind of a captured or decoded object; not a wire-format type code.</summary>
public enum ObjectStateKind {
    Durable,
    String,
    Array,
    List,
    Dictionary,
}
