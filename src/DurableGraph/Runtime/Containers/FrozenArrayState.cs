namespace Atelia.DurableGraph.Runtime;

/// <summary>The immutable, zero-based dimensions of one array object.</summary>
public sealed class ArrayShape : IEquatable<ArrayShape> {
    private readonly int[] _lengths;

    public ArrayShape(params int[] lengths) {
        ArgumentNullException.ThrowIfNull(lengths);
        if (lengths.Length is < 1 or > 4 || lengths.Any(length => length < 0 || length > Array.MaxLength)) {
            throw new InvalidDataException("Array shapes require one to four valid zero-based dimensions.");
        }
        _lengths = (int[])lengths.Clone();
        // A zero dimension makes the product zero regardless of multiplication order.
        long count = 0;
        if (!lengths.Contains(0)) {
            count = 1;
            foreach (int length in lengths) {
                count *= length;
                if (count > Array.MaxLength) {
                    throw new InvalidDataException("The array exceeds the supported frozen backing length.");
                }
            }
        }
        Count = (int)count;
    }

    public int Rank => _lengths.Length;
    public int Count { get; }
    public int this[int dimension] => _lengths[dimension];
    public ReadOnlySpan<int> Lengths => _lengths;

    internal static ArrayShape FromArray(Array value) {
        int rank = value.Rank;
        if (rank is < 1 or > 4 || (rank == 1 && !value.GetType().IsSZArray)) {
            throw new InvalidDataException("Only SZ arrays and rank two through four arrays are supported.");
        }
        int[] lengths = new int[rank];
        for (int dimension = 0; dimension < rank; dimension++) {
            if (value.GetLowerBound(dimension) != 0) {
                throw new InvalidDataException("Nonzero array lower bounds are not supported.");
            }
            lengths[dimension] = value.GetLength(dimension);
        }
        return new(lengths);
    }

    public bool Equals(ArrayShape? other) => other is not null && Lengths.SequenceEqual(other.Lengths);
    public override bool Equals(object? obj) => obj is ArrayShape other && Equals(other);
    public override int GetHashCode() {
        HashCode hash = new();
        foreach (int length in _lengths) { hash.Add(length); }
        return hash.ToHashCode();
    }
}

/// <summary>An owned, immutable row-major array snapshot. Reference elements are represented by ObjectId.</summary>
public sealed class FrozenArrayState<TState> : IFrozenArrayState where TState : unmanaged {
    private readonly TState[] _elements;

    public FrozenArrayState(ArrayShape shape, ReadOnlySpan<TState> elements)
        : this(shape, elements.ToArray(), takeOwnership: true) { }

    internal FrozenArrayState(ArrayShape shape, TState[] elements, bool takeOwnership) {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(elements);
        if (elements.Length != shape.Count) {
            throw new InvalidDataException("Frozen array elements do not match the shape.");
        }
        Shape = shape;
        _elements = takeOwnership ? elements : (TState[])elements.Clone();
    }

    public ArrayShape Shape { get; }
    public ReadOnlySpan<TState> Elements => _elements;
    public TState this[int index] => _elements[index];
}

internal interface IFrozenArrayState {
    ArrayShape Shape { get; }
}
