namespace Atelia.DurableGraph.Runtime;

/// <summary>Owned immutable List contents; reference elements are represented by ObjectId.</summary>
public sealed class FrozenListState<TState> : IFrozenListState where TState : unmanaged {
    private readonly TState[] _elements;

    public FrozenListState(ReadOnlySpan<TState> elements) : this(elements.ToArray(), takeOwnership: true) { }

    internal FrozenListState(TState[] elements, bool takeOwnership) {
        ArgumentNullException.ThrowIfNull(elements);
        _elements = takeOwnership ? elements : (TState[])elements.Clone();
    }

    public int Count => _elements.Length;
    public ReadOnlySpan<TState> Elements => _elements;
    public TState this[int index] => _elements[index];
}

internal interface IFrozenListState {
    int Count { get; }
}
