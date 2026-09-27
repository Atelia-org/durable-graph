using Atelia.DurableGraph.Schema;
using System.Reflection;

namespace Atelia.DurableGraph.Runtime;

public abstract partial class StateBindingContext {
    private readonly Dictionary<(ArrayLayout Source, ArrayLayout Target), ArrayUpgradePlan> _arrayUpgradePlans = [];

    /// <summary>The explicitly selected array-owner value rule set in this frozen catalog.</summary>
    public virtual Type? ArrayElementUpgradeRuleSet => null;

    /// <summary>Normalizes one frozen array without changing its identity or dimensions.</summary>
    /// <remarks>All element tools and exact requirements are checked before the first callback, including for empty arrays.</remarks>
    public ObjectStateRecord NormalizeArray(ObjectStateRecord source, ArrayObjectBinding target) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (source.Id.IsNull || source.Kind != ObjectStateKind.Array || source.Layout.Type != target.ArrayLayout.Type) {
            throw new InvalidDataException("Array upgrade requires an exact source in the same nominal array family.");
        }
        ArrayLayout prior = source.Layout.Array!;
        var key = (prior, target.ArrayLayout);
        if (!_arrayUpgradePlans.TryGetValue(key, out ArrayUpgradePlan? plan)) {
            plan = PrepareArrayUpgrade(prior, target.ArrayLayout);
            _arrayUpgradePlans.Add(key, plan);
        }
        plan.Requirements.Validate(this);
        if (plan.CurrentStateType != target.ElementBinding.StateType) {
            throw new InvalidDataException("The array target binding has a different exact element representation.");
        }
        return plan.Apply(source, target);
    }

    private ArrayUpgradePlan PrepareArrayUpgrade(ArrayLayout source, ArrayLayout target) {
        using var collection = BeginSchemaRequirementCollection();
        ExactSchemaRequirementSet.Builder requirements = new();
        if (source.ElementSlot.ValueSchema is { } priorSchema) {
            BindSchema(priorSchema);
            requirements.Add(priorSchema, "array upgrade.source.element");
        }
        if (target.ElementSlot.ValueSchema is { } nextSchema) {
            BindSchema(nextSchema);
            requirements.Add(nextSchema, "array upgrade.target.element");
        }
        Type priorType = ResolveStoredValue(source.ElementSlot).StateType;
        Type nextType = ResolveStoredValue(target.ElementSlot).StateType;
        ValueUpgradePlan? value = null;
        if (source.ElementSlot != target.ElementSlot) {
            Type ruleSet = ArrayElementUpgradeRuleSet ?? throw new InvalidDataException(
                "Changing an array element layout requires an explicitly selected array element upgrade rule set.");
            // Arrays have no user-defined adjacent owner versions. Bind the exact two
            // endpoints directly; never search a version path or invent a declaration.
            value = PrepareValueUpgrade(ruleSet, source.ElementSlot, target.ElementSlot, 1);
            value.CollectRequirements(requirements, "array upgrade.element", []);
        } else if (priorType != nextType) {
            throw new InvalidDataException("Equal array element slots have different state representations.");
        }
        MethodInfo factory = typeof(StateBindingContext).GetMethod(nameof(CreateArrayUpgradeTyped), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(priorType, nextType);
        requirements.Build().Validate(this);
        requirements.Add(collection.Complete());
        return factory.CreateDelegate<Func<ValueUpgradePlan?, ExactSchemaRequirementSet, ArrayUpgradePlan>>()(value, requirements.Build());
    }

    private static ArrayUpgradePlan CreateArrayUpgradeTyped<TPrior, TNext>(ValueUpgradePlan? value,
        ExactSchemaRequirementSet requirements) where TPrior : unmanaged where TNext : unmanaged =>
        new TypedArrayUpgradePlan<TPrior, TNext>(value, requirements);

    private abstract class ArrayUpgradePlan(ExactSchemaRequirementSet requirements) {
        internal ExactSchemaRequirementSet Requirements { get; } = requirements;
        internal abstract Type CurrentStateType { get; }
        internal abstract ObjectStateRecord Apply(ObjectStateRecord source, ArrayObjectBinding target);
    }

    private sealed class TypedArrayUpgradePlan<TPrior, TNext>(ValueUpgradePlan? value,
        ExactSchemaRequirementSet requirements) : ArrayUpgradePlan(requirements)
        where TPrior : unmanaged where TNext : unmanaged {
        internal override Type CurrentStateType => typeof(TNext);

        internal override ObjectStateRecord Apply(ObjectStateRecord source, ArrayObjectBinding target) {
            FrozenArrayState<TPrior> prior;
            try { prior = source.GetArrayState<TPrior>(); }
            catch (InvalidOperationException error) { throw new InvalidDataException("The source array has a different exact element representation.", error); }
            if (prior.Shape.Rank != source.Layout.Array!.Rank || prior.Shape.Rank != target.ArrayLayout.Rank) {
                throw new InvalidDataException("The frozen array shape does not match its exact layout.");
            }
            if (value is null) { return target.CreateStateRecord(source.Id, prior); }

            UpgradeContext owner = new(source.Id, source.Layout, ObjectLayout.ForArray(target.ArrayLayout), prior.Shape);
            var tools = new Dictionary<ValueUpgradePlan, Delegate>(ReferenceEqualityComparer.Instance);
            ValueUpgrade<TPrior, TNext> convert = (ValueUpgrade<TPrior, TNext>)value.CreateTool(owner, tools);
            TNext[] next = new TNext[prior.Elements.Length];
            for (int index = 0; index < next.Length; index++) {
                next[index] = convert(in prior.Elements[index]);
            }
            return target.CreateStateRecord(source.Id, new FrozenArrayState<TNext>(prior.Shape, next, takeOwnership: true));
        }
    }
}
