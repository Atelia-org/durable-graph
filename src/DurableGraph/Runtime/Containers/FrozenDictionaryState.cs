namespace Atelia.DurableGraph.Runtime;

/// <summary>One frozen entry. References in either slot use ObjectId.</summary>
public readonly record struct DictionaryEntryState<TKeyState, TValueState>(TKeyState Key, TValueState Value)
    where TKeyState : unmanaged where TValueState : unmanaged;

/// <summary>Owned immutable mapping content and restore choice; entry order and current comparer code are not persistent state.</summary>
public sealed class FrozenDictionaryState<TKeyState, TValueState> : IFrozenDictionaryState
    where TKeyState : unmanaged where TValueState : unmanaged {
    private readonly DictionaryEntryState<TKeyState, TValueState>[] _entries;

    public FrozenDictionaryState(DictionaryComparerKind comparerKind,
        ReadOnlySpan<DictionaryEntryState<TKeyState, TValueState>> entries)
        : this(comparerKind, entries.ToArray(), takeOwnership: true) { }

    internal FrozenDictionaryState(DictionaryComparerKind comparerKind,
        DictionaryEntryState<TKeyState, TValueState>[] entries, bool takeOwnership) {
        if (!Enum.IsDefined(comparerKind)) { throw new ArgumentOutOfRangeException(nameof(comparerKind)); }
        ArgumentNullException.ThrowIfNull(entries);
        ComparerKind = comparerKind;
        _entries = takeOwnership ? entries : (DictionaryEntryState<TKeyState, TValueState>[])entries.Clone();
    }

    public DictionaryComparerKind ComparerKind { get; }
    public int Count => _entries.Length;
    public ReadOnlySpan<DictionaryEntryState<TKeyState, TValueState>> Entries => _entries;
    public DictionaryEntryState<TKeyState, TValueState> this[int index] => _entries[index];
}

internal interface IFrozenDictionaryState {
    int Count { get; }
    DictionaryComparerKind ComparerKind { get; }
}
