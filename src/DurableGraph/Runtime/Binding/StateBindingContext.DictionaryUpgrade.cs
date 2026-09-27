using Atelia.DurableGraph.Schema;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Atelia.DurableGraph.Runtime;

public abstract partial class StateBindingContext {
    private readonly Dictionary<(DictionaryLayout Source, DictionaryLayout Target), DictionaryUpgradePlan> _dictionaryUpgradePlans = [];

    /// <summary>The explicitly selected dictionary-owner key rule set in this frozen catalog.</summary>
    public virtual Type? DictionaryKeyUpgradeRuleSet => null;

    /// <summary>The explicitly selected dictionary-owner value rule set in this frozen catalog.</summary>
    public virtual Type? DictionaryValueUpgradeRuleSet => null;

    /// <summary>Normalizes one frozen dictionary while preserving its identity, count, and comparer strategy.</summary>
    /// <remarks>
    /// Both slots and all declared tools are checked before the first callback, including for empty dictionaries.
    /// Reference-dependent key collisions are subsequently checked against the complete normalized object table.
    /// </remarks>
    public ObjectStateRecord NormalizeDictionary(ObjectStateRecord source, DictionaryObjectBinding target) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (source.Id.IsNull || source.Kind != ObjectStateKind.Dictionary || source.Layout.Type != target.DictionaryLayout.Type) {
            throw new InvalidDataException("Dictionary upgrade requires an exact source in the same nominal dictionary family.");
        }
        DictionaryLayout prior = source.Layout.Dictionary!;
        var key = (prior, target.DictionaryLayout);
        if (!_dictionaryUpgradePlans.TryGetValue(key, out DictionaryUpgradePlan? plan)) {
            plan = PrepareDictionaryUpgrade(prior, target.DictionaryLayout);
            _dictionaryUpgradePlans.Add(key, plan);
        }
        plan.Requirements.Validate(this);
        if (plan.CurrentKeyStateType != target.KeyBinding.StateType || plan.CurrentValueStateType != target.ValueBinding.StateType) {
            throw new InvalidDataException("The dictionary target binding has different exact key or value representations.");
        }
        return plan.Apply(source, target);
    }

    private DictionaryUpgradePlan PrepareDictionaryUpgrade(DictionaryLayout source, DictionaryLayout target) {
        using var collection = BeginSchemaRequirementCollection();
        ExactSchemaRequirementSet.Builder requirements = new();
        AddSlotRequirements(source.KeySlot, "dictionary upgrade.source.key");
        AddSlotRequirements(source.ValueSlot, "dictionary upgrade.source.value");
        AddSlotRequirements(target.KeySlot, "dictionary upgrade.target.key");
        AddSlotRequirements(target.ValueSlot, "dictionary upgrade.target.value");

        Type priorKeyType = ResolveStoredValue(source.KeySlot).StateType;
        Type nextKeyType = ResolveStoredValue(target.KeySlot).StateType;
        Type priorValueType = ResolveStoredValue(source.ValueSlot).StateType;
        Type nextValueType = ResolveStoredValue(target.ValueSlot).StateType;
        ValueUpgradePlan? key = PrepareSlot(source.KeySlot, target.KeySlot, priorKeyType, nextKeyType, DictionaryKeyUpgradeRuleSet, "key");
        ValueUpgradePlan? value = PrepareSlot(source.ValueSlot, target.ValueSlot, priorValueType, nextValueType, DictionaryValueUpgradeRuleSet, "value");

        MethodInfo factory = typeof(StateBindingContext).GetMethod(nameof(CreateDictionaryUpgradeTyped), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(priorKeyType, nextKeyType, priorValueType, nextValueType);
        requirements.Build().Validate(this);
        requirements.Add(collection.Complete());
        return factory.CreateDelegate<Func<ValueUpgradePlan?, ValueUpgradePlan?, ExactSchemaRequirementSet, DictionaryUpgradePlan>>()(key, value, requirements.Build());

        void AddSlotRequirements(DurableFieldInfo slot, string path) {
            if (slot.ValueSchema is not { } schema) { return; }
            BindSchema(schema);
            requirements.Add(schema, path);
        }

        ValueUpgradePlan? PrepareSlot(DurableFieldInfo prior, DurableFieldInfo next, Type priorType, Type nextType, Type? selectedRules, string name) {
            if (prior == next) {
                if (priorType != nextType) { throw new InvalidDataException($"Equal dictionary {name} slots have different state representations."); }
                return null;
            }
            Type rules = selectedRules ?? throw new InvalidDataException(
                $"Changing a dictionary {name} layout requires an explicitly selected dictionary {name} upgrade rule set.");
            // Builtin owners bind the two exact endpoints. They do not search an adjacent-version path.
            ValueUpgradePlan plan = PrepareValueUpgrade(rules, prior, next, 1);
            plan.CollectRequirements(requirements, $"dictionary upgrade.{name}", []);
            return plan;
        }
    }

    private static DictionaryUpgradePlan CreateDictionaryUpgradeTyped<TPriorKey, TNextKey, TPriorValue, TNextValue>(
        ValueUpgradePlan? key, ValueUpgradePlan? value, ExactSchemaRequirementSet requirements)
        where TPriorKey : unmanaged where TNextKey : unmanaged where TPriorValue : unmanaged where TNextValue : unmanaged =>
        new TypedDictionaryUpgradePlan<TPriorKey, TNextKey, TPriorValue, TNextValue>(key, value, requirements);

    private abstract class DictionaryUpgradePlan(ExactSchemaRequirementSet requirements) {
        internal ExactSchemaRequirementSet Requirements { get; } = requirements;
        internal abstract Type CurrentKeyStateType { get; }
        internal abstract Type CurrentValueStateType { get; }
        internal abstract ObjectStateRecord Apply(ObjectStateRecord source, DictionaryObjectBinding target);
    }

    private sealed class TypedDictionaryUpgradePlan<TPriorKey, TNextKey, TPriorValue, TNextValue>(
        ValueUpgradePlan? key, ValueUpgradePlan? value, ExactSchemaRequirementSet requirements) : DictionaryUpgradePlan(requirements)
        where TPriorKey : unmanaged where TNextKey : unmanaged where TPriorValue : unmanaged where TNextValue : unmanaged {
        internal override Type CurrentKeyStateType => typeof(TNextKey);
        internal override Type CurrentValueStateType => typeof(TNextValue);

        internal override ObjectStateRecord Apply(ObjectStateRecord source, DictionaryObjectBinding target) {
            FrozenDictionaryState<TPriorKey, TPriorValue> prior;
            try { prior = source.GetDictionaryState<TPriorKey, TPriorValue>(); }
            catch (InvalidOperationException error) { throw new InvalidDataException("The source dictionary has different exact key or value representations.", error); }
            if (key is null && value is null) { return target.CreateStateRecord(source.Id, prior); }

            UpgradeContext owner = new(source.Id, source.Layout, ObjectLayout.ForDictionary(target.DictionaryLayout), prior.Count);
            var tools = new Dictionary<ValueUpgradePlan, Delegate>(ReferenceEqualityComparer.Instance);
            // Create both complete tools before invoking either: a bad value dependency must also prevent key callbacks.
            ValueUpgrade<TPriorKey, TNextKey>? convertKey = key is null ? null : (ValueUpgrade<TPriorKey, TNextKey>)key.CreateTool(owner, tools);
            ValueUpgrade<TPriorValue, TNextValue>? convertValue = value is null ? null : (ValueUpgrade<TPriorValue, TNextValue>)value.CreateTool(owner, tools);
            DictionaryEntryState<TNextKey, TNextValue>[] next = new DictionaryEntryState<TNextKey, TNextValue>[prior.Count];
            for (int index = 0; index < next.Length; index++) {
                ref readonly DictionaryEntryState<TPriorKey, TPriorValue> entry = ref prior.Entries[index];
                TPriorKey priorKey = entry.Key;
                TPriorValue priorValue = entry.Value;
                // The no-tool branch was checked to have identical CLR state types during plan binding.
                TNextKey nextKey = convertKey is null ? Unsafe.As<TPriorKey, TNextKey>(ref priorKey) : convertKey(in priorKey);
                TNextValue nextValue = convertValue is null ? Unsafe.As<TPriorValue, TNextValue>(ref priorValue) : convertValue(in priorValue);
                next[index] = new(nextKey, nextValue);
            }
            return target.CreateStateRecord(source.Id,
                new FrozenDictionaryState<TNextKey, TNextValue>(prior.ComparerKind, next, takeOwnership: true));
        }
    }
}
