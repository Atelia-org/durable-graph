using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Runtime;

/// <summary>Owned prepared contents and their exact in-memory sources, without persistence authority.</summary>
public sealed class PreparedCapturedGraph {
    internal PreparedCapturedGraph(CapturedGraph? previous, CapturedGraph candidate, IEnumerable<PreparedCapturedObject> objects) {
        Previous = previous;
        Candidate = candidate;
        Objects = new FrozenList<PreparedCapturedObject>(objects);
    }

    public CapturedGraph? Previous { get; }
    public CapturedGraph Candidate { get; }
    /// <summary>The complete candidate object set in ascending ID order.</summary>
    public IReadOnlyList<PreparedCapturedObject> Objects { get; }

    private sealed class FrozenList<T>(IEnumerable<T> source) : IReadOnlyList<T> {
        private readonly T[] _items = source.ToArray();
        public int Count => _items.Length;
        public T this[int index] => _items[index];
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

/// <summary>One candidate object's prepared body and optional same-object comparison.</summary>
public sealed class PreparedCapturedObject {
    internal PreparedCapturedObject(
        ObjectStateRecord current,
        ObjectStateRecord? previous,
        PreparedBaseBody? baseBody,
        PreparedDeltaBody? deltaBody) {
        Current = current;
        Previous = previous;
        EagerBaseBody = baseBody;
        DeltaBody = deltaBody;
    }

    public ObjectStateRecord Current { get; }
    /// <summary>Null for a new object; otherwise the same ID from the previous in-memory graph.</summary>
    public ObjectStateRecord? Previous { get; }
    // Public CaptureSession.Prepare always supplies eager bytes. Only an internal
    // business save may carry an immutable text state through representation selection.
    internal PreparedBaseBody? EagerBaseBody { get; }
    public PreparedBaseBody BaseBody => EagerBaseBody ?? TextBodyCodec.PrepareBase(Current.TextState);
    /// <summary>Present for existing mutable objects; existing immutable strings need no Delta.</summary>
    public PreparedDeltaBody? DeltaBody { get; }
}
