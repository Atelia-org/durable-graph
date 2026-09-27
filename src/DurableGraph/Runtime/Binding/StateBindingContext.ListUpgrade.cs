using Atelia.DurableGraph.Schema;
using System.Reflection;

namespace Atelia.DurableGraph.Runtime;

public abstract partial class StateBindingContext {
    private readonly Dictionary<(ListLayout Source, ListLayout Target), ListUpgradePlan> _listUpgradePlans = [];

    /// <summary>The explicitly selected list-owner value rule set in this frozen catalog.</summary>
    public virtual Type? ListElementUpgradeRuleSet => null;

    /// <summary>Normalizes one frozen list without changing its identity or element count.</summary>
    /// <remarks>All element tools and exact requirements are checked before the first callback, including for empty lists.</remarks>
    public ObjectStateRecord NormalizeList(ObjectStateRecord source, ListObjectBinding target) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (source.Id.IsNull || source.Kind != ObjectStateKind.List || source.Layout.Type != target.ListLayout.Type) {
            throw new InvalidDataException("List upgrade requires an exact source in the same nominal list family.");
        }
        ListLayout prior = source.Layout.List!;
        var key = (prior, target.ListLayout);
        if (!_listUpgradePlans.TryGetValue(key, out ListUpgradePlan? plan)) {
            plan = PrepareListUpgrade(prior, target.ListLayout);
            _listUpgradePlans.Add(key, plan);
        }
        plan.Requirements.Validate(this);
        if (plan.CurrentStateType != target.ElementBinding.StateType) {
            throw new InvalidDataException("The list target binding has a different exact element representation.");
        }
        return plan.Apply(source, target);
    }

    private ListUpgradePlan PrepareListUpgrade(ListLayout source, ListLayout target) {
        using var collection = BeginSchemaRequirementCollection();
        ExactSchemaRequirementSet.Builder requirements = new();
        if (source.ElementSlot.ValueSchema is { } priorSchema) {
            BindSchema(priorSchema);
            requirements.Add(priorSchema, "list upgrade.source.element");
        }
        if (target.ElementSlot.ValueSchema is { } nextSchema) {
            BindSchema(nextSchema);
            requirements.Add(nextSchema, "list upgrade.target.element");
        }
        Type priorType = ResolveStoredValue(source.ElementSlot).StateType;
        Type nextType = ResolveStoredValue(target.ElementSlot).StateType;
        ValueUpgradePlan? value = null;
        if (source.ElementSlot != target.ElementSlot) {
            Type ruleSet = ListElementUpgradeRuleSet ?? throw new InvalidDataException(
                "Changing a list element layout requires an explicitly selected list element upgrade rule set.");
            // Builtin content owners have no adjacent user versions. Bind exactly
            // the stored and current element endpoints, without searching a path.
            value = PrepareValueUpgrade(ruleSet, source.ElementSlot, target.ElementSlot, 1);
            value.CollectRequirements(requirements, "list upgrade.element", []);
        } else if (priorType != nextType) {
            throw new InvalidDataException("Equal list element slots have different state representations.");
        }
        MethodInfo factory = typeof(StateBindingContext).GetMethod(nameof(CreateListUpgradeTyped), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(priorType, nextType);
        requirements.Build().Validate(this);
        requirements.Add(collection.Complete());
        return factory.CreateDelegate<Func<ValueUpgradePlan?, ExactSchemaRequirementSet, ListUpgradePlan>>()(value, requirements.Build());
    }

    private static ListUpgradePlan CreateListUpgradeTyped<TPrior, TNext>(ValueUpgradePlan? value,
        ExactSchemaRequirementSet requirements) where TPrior : unmanaged where TNext : unmanaged =>
        new TypedListUpgradePlan<TPrior, TNext>(value, requirements);

    private abstract class ListUpgradePlan(ExactSchemaRequirementSet requirements) {
        internal ExactSchemaRequirementSet Requirements { get; } = requirements;
        internal abstract Type CurrentStateType { get; }
        internal abstract ObjectStateRecord Apply(ObjectStateRecord source, ListObjectBinding target);
    }

    private sealed class TypedListUpgradePlan<TPrior, TNext>(ValueUpgradePlan? value,
        ExactSchemaRequirementSet requirements) : ListUpgradePlan(requirements)
        where TPrior : unmanaged where TNext : unmanaged {
        internal override Type CurrentStateType => typeof(TNext);

        internal override ObjectStateRecord Apply(ObjectStateRecord source, ListObjectBinding target) {
            FrozenListState<TPrior> prior;
            try { prior = source.GetListState<TPrior>(); }
            catch (InvalidOperationException error) { throw new InvalidDataException("The source list has a different exact element representation.", error); }
            if (value is null) { return target.CreateStateRecord(source.Id, prior); }

            UpgradeContext owner = new(source.Id, source.Layout, ObjectLayout.ForList(target.ListLayout), prior.Count);
            var tools = new Dictionary<ValueUpgradePlan, Delegate>(ReferenceEqualityComparer.Instance);
            ValueUpgrade<TPrior, TNext> convert = (ValueUpgrade<TPrior, TNext>)value.CreateTool(owner, tools);
            TNext[] next = new TNext[prior.Count];
            for (int index = 0; index < next.Length; index++) {
                next[index] = convert(in prior.Elements[index]);
            }
            return target.CreateStateRecord(source.Id, new FrozenListState<TNext>(next, takeOwnership: true));
        }
    }
}
