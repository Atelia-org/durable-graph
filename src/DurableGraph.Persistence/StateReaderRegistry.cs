using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
namespace Atelia.DurableGraph.Persistence;

/// <summary>Application-local exact-version readers, explicitly registered by model families.</summary>
/// <remarks>
/// Registration is not thread safe. A revision read snapshots the directory before invoking any
/// body callbacks; subsequent registration does not change that read. This is a code capability
/// directory, not the authority for persisted Schema definitions.
/// </remarks>
public sealed class StateReaderRegistry : IStateReaderRegistration {
    private readonly Dictionary<SchemaKey, StateReaderBinding> _bindings = [];
    private readonly Dictionary<string, StateDefinitionBinding> _definitions = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, StateValueUpgradeRuleSet> _valueUpgradeRules = [];

    public void Register(StateValueUpgradeRuleSet ruleSet) =>
        StateModelSnapshot.RegisterValueUpgradeRuleSet(_valueUpgradeRules, ruleSet);

    public void Register(StateDefinitionBinding definition) => StateModelSnapshot.RegisterDefinition(_definitions, definition);

    /// <summary>
    /// Registers one stable binding. Repeating the same instance is harmless; another binding
    /// for that key is rejected even when its Schema definition compares equal.
    /// </summary>
    public void Register(StateReaderBinding binding) {
        ArgumentNullException.ThrowIfNull(binding);
        SchemaKey key = new(binding.Schema.Type, binding.Schema.Version);
        if (_bindings.TryGetValue(key, out StateReaderBinding? existing)) {
            if (!ReferenceEquals(existing, binding)) {
                throw new InvalidOperationException($"A different reader is already registered for {key.SchemaId} v{key.Version}.");
            }
            return;
        }
        _bindings.Add(key, binding);
    }

    internal Dictionary<SchemaKey, StateReaderBinding> Snapshot() => new(_bindings);

    internal StateModelSnapshot Snapshot(Func<SchemaKey, DurableSchema?>? schemaLookup) =>
        new([], [], new(_bindings), new(_definitions, StringComparer.Ordinal),
            new(_valueUpgradeRules), schemaLookup: schemaLookup);
}
