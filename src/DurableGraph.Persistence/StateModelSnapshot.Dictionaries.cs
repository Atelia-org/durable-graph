using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
namespace Atelia.DurableGraph.Persistence;

internal sealed partial class StateModelSnapshot {
    private readonly Dictionary<Type, DictionaryObjectBinding> _currentDictionaries = [];
    private readonly Dictionary<DictionaryLayout, ObjectReaderBinding> _dictionaryReaders = [];
    private readonly Dictionary<Type, object> _dictionaryComparers;
    private readonly Func<Type, object?>? _dictionaryComparerResolver;
    private readonly Dictionary<Type, object> _resolvedDictionaryComparers = [];

    private object ResolveDictionaryComparer(Type dictionaryType) {
        if (_resolvedDictionaryComparers.TryGetValue(dictionaryType, out object? cached)) { return cached; }
        object? result = _dictionaryComparers.TryGetValue(dictionaryType, out object? registered)
            ? registered : _dictionaryComparerResolver?.Invoke(dictionaryType);
        Type required = typeof(IEqualityComparer<>).MakeGenericType(dictionaryType.GetGenericArguments()[0]);
        if (result is null || !required.IsInstanceOfType(result)) {
            throw new InvalidDataException($"Application Dictionary comparer for {dictionaryType} is missing or does not implement {required}.");
        }
        _resolvedDictionaryComparers.Add(dictionaryType, result);
        return result;
    }

    private bool TryGetCurrentDictionaryBinding(Type domainType, out ObjectBinding? binding) {
        if (_currentDictionaries.TryGetValue(domainType, out DictionaryObjectBinding? prior)) {
            CheckClosureRequirements(prior);
            CheckDictionaryLayout(prior.DictionaryLayout);
            binding = prior;
            return true;
        }
        var active = ("Dictionary", (object)domainType);
        Begin(active);
        try {
            using var collection = BeginSchemaRequirementCollection();
            Type[] arguments = domainType.GetGenericArguments();
            // Reference operands close only their declared slots, allowing recursive maps.
            StateValueBinding key = ResolveCurrentValue(arguments[0]);
            StateValueBinding value = ResolveCurrentValue(arguments[1]);
            DictionaryLayout layout = new(key.Slot, value.Slot);
            DictionaryObjectBinding result = DictionaryObjectBinding.Create(domainType, layout, key, value,
                (target, source) => NormalizeDictionary(source, target),
                () => ResolveDictionaryComparer(domainType));
            if (result.DomainType != domainType || !result.DictionaryLayout.Equals(layout)) {
                throw new InvalidDataException("A Dictionary factory returned another current type or exact layout.");
            }
            CheckDictionaryLayout(layout);
            RememberClosureRequirements(result, collection);
            _currentDictionaries.Add(domainType, result);
            binding = result;
            return true;
        } finally { _closing.Remove(active); }
    }

    private ObjectReaderBinding ResolveDictionaryReader(ObjectLayout layout) {
        DictionaryLayout dictionary = layout.Dictionary!;
        if (_dictionaryReaders.TryGetValue(dictionary, out ObjectReaderBinding? prior)) {
            CheckClosureRequirements(prior);
            CheckDictionaryLayout(dictionary);
            return prior;
        }
        var active = ("Dictionary reader", (object)dictionary);
        Begin(active);
        try {
            using var collection = BeginSchemaRequirementCollection();
            // Historical state operations need no old domain key or value CLR declaration.
            CheckDictionaryLayout(dictionary);
            StateValueBinding key = ResolveStoredValue(dictionary.KeySlot);
            StateValueBinding value = ResolveStoredValue(dictionary.ValueSlot);
            ObjectReaderBinding result = DictionaryStateReader.Create(dictionary, key, value);
            if (!result.Layout.Equals(layout)) {
                throw new InvalidDataException("A Dictionary reader factory returned another exact layout.");
            }
            CheckDictionaryLayout(dictionary);
            RememberClosureRequirements(result, collection);
            _dictionaryReaders.Add(dictionary, result);
            return result;
        } finally { _closing.Remove(active); }
    }

    private void CheckDictionaryLayout(DictionaryLayout layout) {
        CheckContainerElement(layout.KeySlot);
        CheckContainerElement(layout.ValueSlot);
    }
}
