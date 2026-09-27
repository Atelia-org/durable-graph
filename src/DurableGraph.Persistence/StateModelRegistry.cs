using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
namespace Atelia.DurableGraph.Persistence;

/// <summary>Explicit application-local current models and their exact historical readers.</summary>
/// <remarks>
/// This builder is not the authority for persisted Schema definitions. A repository copies its configuration
/// once when opened; later registrations affect future opens, not existing repositories. Low-level readers
/// may instead take independent snapshots. Closure factories/readers/traversal must be semantically stable;
/// successful closures may be reused, and callback counts must not determine application results.
/// Capture produces canonical current DTOs and preserves complete values and identities. Historical
/// Normalize/Upgrade is deterministic for its explicit inputs and environment and does not mutate inputs.
/// Mutable restoration allocates fresh exact instances and hydrates only the supplied target, without
/// mutating shared DTOs or previously delivered graphs or exposing a partially restored graph.
/// </remarks>
public sealed class StateModelRegistry : IStateModelRegistration {
    private readonly Dictionary<TypeExpr, StateModelBinding> _models = [];
    private readonly Dictionary<Type, StateModelBinding> _types = [];
    private readonly Dictionary<SchemaKey, StateReaderBinding> _readers = [];
    private readonly Dictionary<string, StateDefinitionBinding> _definitions = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, StateValueUpgradeRuleSet> _valueUpgradeRules = [];
    private Type? _arrayElementUpgradeRuleSet;
    private Type? _listElementUpgradeRuleSet;
    private Type? _dictionaryKeyUpgradeRuleSet;
    private Type? _dictionaryValueUpgradeRuleSet;
    private ListDeltaAlgorithm _listDeltaAlgorithm = ListDeltaAlgorithm.Adaptive;
    private readonly Dictionary<Type, object> _dictionaryComparers = [];
    private Func<Type, object?>? _dictionaryComparerResolver;

    /// <summary>Selects the current comparer for Application-mode instances of one closed Dictionary type.</summary>
    /// <remarks>
    /// Standard modes and CurrentDefault ignore this selection. Configuration is copied into future snapshots;
    /// existing repositories and independent snapshots keep their selection. All Application instances of this type share the comparer.
    /// The comparer must be stable and use only restored inline values, strings or reference identities,
    /// not the contents of referenced objects that may still await hydration.
    /// </remarks>
    public void UseDictionaryComparer<TKey, TValue>(IEqualityComparer<TKey> comparer) where TKey : notnull {
        ArgumentNullException.ThrowIfNull(comparer);
        Type dictionaryType = typeof(Dictionary<TKey, TValue>);
        if (_dictionaryComparers.TryGetValue(dictionaryType, out object? existing)) {
            if (!ReferenceEquals(existing, comparer)) {
                throw new InvalidOperationException($"A different Application comparer is already selected for {dictionaryType}.");
            }
            return;
        }
        _dictionaryComparers.Add(dictionaryType, comparer);
    }

    /// <summary>Selects one lazy fallback for Application-mode Dictionary types without an exact registration.</summary>
    /// <remarks>
    /// The argument is the closed domain Dictionary&lt;TKey,TValue&gt; type. Return an IEqualityComparer&lt;TKey&gt;.
    /// Missing, null or invalid results fail; they never fall back to Default. Successful results are shared
    /// and cached per snapshot and closed type. Exact DTO reading and normalization do not invoke this callback.
    /// Snapshot freezing retains delegate/comparer references, not copies of their mutable external state.
    /// Resolution policy and equality/hash behavior must remain stable for the environment lifetime.
    /// </remarks>
    public void UseDictionaryComparerResolver(Func<Type, object?> resolver) {
        ArgumentNullException.ThrowIfNull(resolver);
        if (_dictionaryComparerResolver is not null && !ReferenceEquals(_dictionaryComparerResolver, resolver)) {
            throw new InvalidOperationException("A different Dictionary comparer resolver is already selected.");
        }
        _dictionaryComparerResolver = resolver;
    }

    /// <summary>Selects how future model snapshots prepare List Delta bodies.</summary>
    /// <remarks>Adaptive is the default. Existing repositories and independent snapshots keep their writer selection. Algorithms share one persisted format and reader.</remarks>
    public void UseListDeltaAlgorithm(ListDeltaAlgorithm algorithm) {
        if (!Enum.IsDefined(algorithm)) { throw new ArgumentOutOfRangeException(nameof(algorithm)); }
        _listDeltaAlgorithm = algorithm;
    }

    /// <summary>Selects explicit value rules owned by Lists whose element layout changes.</summary>
    /// <remarks>This choice is independent of array element rules and is frozen per model snapshot.</remarks>
    public void UseListElementUpgrades(Type ruleSet) {
        ArgumentNullException.ThrowIfNull(ruleSet);
        if (!_valueUpgradeRules.ContainsKey(ruleSet)) {
            throw new InvalidOperationException($"Register the value upgrade ruleset for {ruleSet} before selecting it for Lists.");
        }
        if (_listElementUpgradeRuleSet is not null && _listElementUpgradeRuleSet != ruleSet) {
            throw new InvalidOperationException("A different List element upgrade ruleset is already selected.");
        }
        _listElementUpgradeRuleSet = ruleSet;
    }

    /// <summary>Selects explicit value rules for Dictionary keys whose exact layout changes.</summary>
    /// <remarks>Key and value selections are independent and frozen per model snapshot.</remarks>
    public void UseDictionaryKeyUpgrades(Type ruleSet) =>
        SelectDictionaryUpgrades(ref _dictionaryKeyUpgradeRuleSet, ruleSet, "key");

    /// <summary>Selects explicit value rules for Dictionary values whose exact layout changes.</summary>
    public void UseDictionaryValueUpgrades(Type ruleSet) =>
        SelectDictionaryUpgrades(ref _dictionaryValueUpgradeRuleSet, ruleSet, "value");

    private void SelectDictionaryUpgrades(ref Type? selected, Type ruleSet, string slot) {
        ArgumentNullException.ThrowIfNull(ruleSet);
        if (!_valueUpgradeRules.ContainsKey(ruleSet)) {
            throw new InvalidOperationException($"Register the value upgrade ruleset for {ruleSet} before selecting it for Dictionary {slot}s.");
        }
        if (selected is not null && selected != ruleSet) {
            throw new InvalidOperationException($"A different Dictionary {slot} upgrade ruleset is already selected.");
        }
        selected = ruleSet;
    }

    /// <summary>Selects the registered explicit value rules used when an array's element layout changes.</summary>
    /// <remarks>The array owns this choice independently of its incoming fields. One frozen catalog selects one ruleset.</remarks>
    public void UseArrayElementUpgrades(Type ruleSet) {
        ArgumentNullException.ThrowIfNull(ruleSet);
        if (!_valueUpgradeRules.ContainsKey(ruleSet)) {
            throw new InvalidOperationException($"Register the value upgrade ruleset for {ruleSet} before selecting it for arrays.");
        }
        if (_arrayElementUpgradeRuleSet is not null && _arrayElementUpgradeRuleSet != ruleSet) {
            throw new InvalidOperationException("A different array element upgrade ruleset is already selected.");
        }
        _arrayElementUpgradeRuleSet = ruleSet;
    }

    /// <summary>Registers one stable ruleset; repeated registration of that instance is harmless.</summary>
    public void Register(StateValueUpgradeRuleSet ruleSet) =>
        StateModelSnapshot.RegisterValueUpgradeRuleSet(_valueUpgradeRules, ruleSet);

    public void Register(StateDefinitionBinding definition) {
        ArgumentNullException.ThrowIfNull(definition);
        foreach (StateModelBinding model in _models.Values) { ValidateOwnership(definition, model); }
        StateModelSnapshot.RegisterDefinition(_definitions, definition);
    }

    /// <summary>Registers one stable model atomically; repeated registration of that instance is harmless.</summary>
    public void Register(StateModelBinding model) {
        ArgumentNullException.ThrowIfNull(model);
        foreach (StateDefinitionBinding definition in _definitions.Values) { ValidateOwnership(definition, model); }
        TypeExpr id = model.CurrentSchema.Type;
        if (_models.TryGetValue(id, out StateModelBinding? existing)) {
            if (!ReferenceEquals(existing, model)) {
                throw new InvalidOperationException($"A different current model is already registered for {id}.");
            }
            return;
        }
        if (_types.ContainsKey(model.DomainType)) {
            throw new InvalidOperationException($"A current model is already registered for exact domain type {model.DomainType}.");
        }
        foreach (StateReaderBinding reader in model.Readers) {
            SchemaKey key = new(reader.Schema.Type, reader.Schema.Version);
            if (_readers.TryGetValue(key, out StateReaderBinding? prior) && !ReferenceEquals(reader, prior)) {
                throw new InvalidOperationException($"A different reader is already registered for {key.SchemaId} v{key.Version}.");
            }
        }
        foreach (StateReaderBinding reader in model.Readers) {
            _readers.TryAdd(new(reader.Schema.Type, reader.Schema.Version), reader);
        }
        _models.Add(id, model);
        _types.Add(model.DomainType, model);
    }

    private static void ValidateOwnership(StateDefinitionBinding definition, StateModelBinding model) {
        Type declaration = model.DomainType.IsGenericType ? model.DomainType.GetGenericTypeDefinition() : model.DomainType;
        bool sameClr = declaration == definition.DomainTypeDefinition;
        bool sameFamily = model.CurrentSchema.SchemaId == definition.DefinitionId;
        if (sameClr != sameFamily || (sameFamily && (model.CurrentSchema.Kind != definition.Kind ||
            model.CurrentSchema.Type.Arguments.Length != definition.Arity || model.CurrentSchema.Version != definition.CurrentVersion))) {
            throw new InvalidOperationException("A definition and concrete model disagree about declaration ownership or current version.");
        }
        // A concrete stable binding may coexist with its own declaration catalog. It remains
        // the selected binding for that exact CLR closure; templates serve other closures.
    }

    // Freeze every index before any reader, Upgrade, Capture or Hydrate callback runs.
    internal StateModelSnapshot Snapshot(SchemaStore? schemas = null) => new(
        new Dictionary<TypeExpr, StateModelBinding>(_models),
        new Dictionary<Type, StateModelBinding>(_types),
        new Dictionary<SchemaKey, StateReaderBinding>(_readers),
        new Dictionary<string, StateDefinitionBinding>(_definitions, StringComparer.Ordinal), schemas,
        new Dictionary<Type, StateValueUpgradeRuleSet>(_valueUpgradeRules), _arrayElementUpgradeRuleSet, _listElementUpgradeRuleSet,
        _listDeltaAlgorithm, _dictionaryKeyUpgradeRuleSet, _dictionaryValueUpgradeRuleSet,
        new Dictionary<Type, object>(_dictionaryComparers), _dictionaryComparerResolver);
}
