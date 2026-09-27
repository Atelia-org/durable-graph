using Atelia.DurableGraph.Schema;
using System.Reflection;

namespace Atelia.DurableGraph.Runtime;

public abstract partial class StateBindingContext {
    private readonly Dictionary<(DurableSchema Source, DurableSchema Target), UpgradePlan> _upgradePlans = [];

    /// <summary>Prebinds every adjacent owner edge before executing this object's first business conversion.</summary>
    public TCurrent Normalize<TCurrent>(ObjectStateRecord source, DurableSchema current) where TCurrent : unmanaged {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(current);
        if (source.Kind != ObjectStateKind.Durable || source.Id.IsNull || source.Schema!.Type != current.Type ||
            source.Schema.Version > current.Version || current.Kind != SchemaKind.ReferenceObject) {
            throw new InvalidDataException("Upgrade requires an older exact DTO in the same closed object family.");
        }
        var key = (source.Schema, current);
        if (!_upgradePlans.TryGetValue(key, out UpgradePlan? plan)) {
            plan = PrepareUpgradePlan(source.Schema, current);
            _upgradePlans.Add(key, plan);
        }
        plan.Requirements.Validate(this);
        if (plan.CurrentStateType != typeof(TCurrent)) {
            throw new InvalidDataException("The requested current DTO type does not match its exact Schema.");
        }
        ObjectStateRecord result = source;
        foreach (UpgradeStep step in plan.Steps) { result = step.Apply(result); }
        try { return result.GetState<TCurrent>(); }
        catch (InvalidOperationException error) { throw new InvalidDataException("The upgraded object has the wrong DTO type.", error); }
    }

    private UpgradePlan PrepareUpgradePlan(DurableSchema source, DurableSchema current) {
        using var collection = BeginSchemaRequirementCollection();
        BindSchema(source);
        BindSchema(current);
        Type currentStateType = ResolveReader(current).StateType;
        ExactSchemaRequirementSet.Builder requirements = new();
        string requirementRoot = $"upgrade {source.Type} v{source.Version}->v{current.Version}";
        requirements.Add(source, $"{requirementRoot}.source");
        requirements.Add(current, $"{requirementRoot}.current");
        if (source.Version == current.Version) {
            if (!source.Equals(current)) { throw new InvalidDataException("Equal Schema keys have different complete layouts."); }
            requirements.Build().Validate(this);
            requirements.Add(collection.Complete());
            return new([], requirements.Build(), currentStateType);
        }
        List<UpgradeStep> steps = [];
        DurableSchema prior = source;
        StateDefinitionBinding definition = GetDefinition(source.SchemaId);
        while (prior.Version < current.Version) {
            StateUpgradeProvider provider = SelectUpgrade(definition, source.Type, prior.Version);
            Type priorState = ResolveReader(prior).StateType;
            Dictionary<Type, Type> variables = [];
            UnifyStateType(provider.PriorType, priorState, variables);
            DurableSchema next;
            if (provider.ClosedOwner is not null) {
                DurableSchema expectedPrior = InferSchemaFromState(source.Type, provider.FromVersion, provider.PriorType);
                if (!expectedPrior.Equals(prior)) { throw new InvalidDataException("The selected closed upgrade input has a different complete Schema."); }
                next = InferSchemaFromState(source.Type, provider.ToVersion, provider.NextType);
            } else if (provider.ToVersion == current.Version) {
                next = current;
            } else if (TryGetSchema(source.Type, provider.ToVersion, out DurableSchema? registered)) {
                next = registered!;
            } else {
                Type? inferredNextType = SubstituteStateType(provider.NextType, variables);
                next = inferredNextType is not null
                    ? InferSchemaFromState(source.Type, provider.ToVersion, inferredNextType)
                    : InferSchemaFromPrior(prior, provider.ToVersion);
            }
            if (next.Type != source.Type || next.Version != provider.ToVersion ||
                (next.Version == current.Version && !next.Equals(current))) {
                throw new InvalidDataException("The selected upgrade output does not match its adjacent exact endpoint.");
            }
            BindSchema(next);
            Type nextState = ResolveReader(next).StateType;
            UnifyStateType(provider.NextType, nextState, variables);
            MethodInfo method = CloseUpgradeMethod(provider.Method, variables);
            UpgradeDependencies dependencies = PrepareDependencies(provider.Dependencies, prior, next, 1);
            int stepIndex = steps.Count;
            string stepPath = $"{requirementRoot}.step[{stepIndex}] v{prior.Version}->v{next.Version}";
            requirements.Add(prior, $"{stepPath}.source");
            requirements.Add(next, $"{stepPath}.target");
            dependencies.CollectRequirements(requirements, stepPath, []);
            steps.Add(CreateUpgradeStep(prior, next, priorState, nextState, provider, method, dependencies));
            prior = next;
        }
        requirements.Build().Validate(this);
        requirements.Add(collection.Complete());
        return new(steps.ToArray(), requirements.Build(), currentStateType);
    }

    private static StateUpgradeProvider SelectUpgrade(StateDefinitionBinding definition, TypeExpr owner, int fromVersion) {
        StateUpgradeProvider[] specific = DistinctCapabilities(definition.Upgrades.Where(provider =>
            provider.FromVersion == fromVersion && provider.ClosedOwner == owner));
        if (specific.Length > 1) { throw new InvalidDataException("Conflicting closed owner upgrade providers are registered."); }
        if (specific.Length == 1) { return specific[0]; }
        StateUpgradeProvider[] general = DistinctCapabilities(definition.Upgrades.Where(provider =>
            provider.FromVersion == fromVersion && provider.ClosedOwner is null));
        return general.Length == 1 ? general[0] : throw new InvalidDataException(
            general.Length == 0 ? "An adjacent owner upgrade capability is missing." : "Conflicting generic owner upgrade providers are registered.");
    }

    private static StateUpgradeProvider[] DistinctCapabilities(IEnumerable<StateUpgradeProvider> providers) {
        List<StateUpgradeProvider> distinct = [];
        foreach (StateUpgradeProvider provider in providers) {
            if (!distinct.Any(existing => existing.IsSameCapability(provider))) { distinct.Add(provider); }
        }
        return distinct.ToArray();
    }

    /// <summary>Derives an exact layout from an explicitly selected generated DTO representation and nominal owner.</summary>
    public DurableSchema InferSchemaFromState(TypeExpr owner, int version, Type stateType) {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(stateType);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        if (owner.Kind != TypeExprKind.Named || !owner.IsClosed) {
            throw new ArgumentException("An exact DTO requires a closed named owner.", nameof(owner));
        }
        return InferSchemaFromState(owner, version, stateType, 1);
    }

    private DurableSchema InferSchemaFromState(TypeExpr owner, int version, Type stateType, int depth) {
        if (depth > 256) { throw new InvalidDataException("Exact DTO inference exceeded its layout depth bound."); }
        StateSchemaTemplate template = GetTemplate(owner.DefinitionId!, version);
        if (template.Arity != owner.Arguments.Length || template.StateTypeDefinition is null) {
            throw new InvalidDataException("The retained template has no matching generated DTO metadata.");
        }
        Type definition = stateType.IsGenericType ? stateType.GetGenericTypeDefinition() : stateType;
        if (definition != template.StateTypeDefinition || stateType.ContainsGenericParameters) {
            throw new InvalidDataException("The selected DTO does not belong to this exact definition version.");
        }
        Type[] arguments = stateType.IsGenericType ? stateType.GetGenericArguments() : [];
        if (arguments.Length != template.StateParameters.Length) {
            throw new InvalidDataException("The DTO representation arity does not match its generated metadata.");
        }
        Dictionary<(TypeExpr Expression, int? Version), DurableFieldInfo> selections = [];
        for (int index = 0; index < arguments.Length; index++) {
            StateParameterTemplate parameter = template.StateParameters[index];
            TypeExpr nominal = Substitute(parameter.Expression, owner.Arguments);
            DurableFieldInfo slot = InferSlotFromState(nominal, arguments[index], depth + 1);
            if (parameter.InlineVersion is { } inlineVersion && slot.ValueSchema?.Version != inlineVersion) {
                throw new InvalidDataException("An explicit value DTO disagrees with its fixed historical inline version.");
            }
            AddSelection(selections, (parameter.Expression, parameter.InlineVersion), slot);
        }
        DurableSchema result = BuildSchema(owner, template, selections, depth);
        BindSchema(result);
        Type actualState = result.Kind == SchemaKind.ReferenceObject ? ResolveReader(result).StateType :
            ResolveStoredValue(new(1, TypeTag.InlineValue, inlineSchema: result)).StateType;
        if (actualState != stateType) { throw new InvalidDataException("The inferred complete Schema has a different DTO representation."); }
        return result;
    }

    private DurableFieldInfo InferSlotFromState(TypeExpr nominal, Type stateType, int depth) {
        if (depth > 256) { throw new InvalidDataException("Exact DTO inference exceeded its layout depth bound."); }
        if (nominal.IsNullable) {
            if (!stateType.IsGenericType || stateType.GetGenericTypeDefinition() != typeof(NullableState<>)) {
                throw new InvalidDataException("A Nullable DTO operand must retain its NullableState wrapper.");
            }
            return DurableFieldInfo.Nullable(1, InferSlotFromState(nominal.ElementType!, stateType.GetGenericArguments()[0], depth + 1));
        }
        if (nominal.Kind == TypeExprKind.Builtin) {
            DurableFieldInfo slot = new(1, nominal.BuiltinTag);
            if (ResolveStoredValue(slot).StateType != stateType) { throw new InvalidDataException("A DTO value does not match its nominal built-in semantics."); }
            return slot;
        }
        if ((nominal.IsArray || nominal.IsList || nominal.IsDictionary) && nominal.IsClosed) {
            if (stateType != typeof(ObjectId)) { throw new InvalidDataException("A container reference DTO operand must contain an object ID."); }
            return DurableFieldInfo.Reference(1, nominal);
        }
        if (nominal.Kind != TypeExprKind.Named || !nominal.IsClosed) { throw new InvalidDataException("A value operand must have a closed nominal identity."); }
        if (GetNamedDeclarationKind(nominal) == SchemaKind.ReferenceObject) {
            if (stateType != typeof(ObjectId)) { throw new InvalidDataException("A durable reference DTO operand must contain an object ID."); }
            return DurableFieldInfo.Reference(1, nominal);
        }
        StateDefinitionBinding definition = GetDefinition(nominal.DefinitionId!);
        Type stateDefinition = stateType.IsGenericType ? stateType.GetGenericTypeDefinition() : stateType;
        StateSchemaTemplate[] candidates = definition.Templates.Where(template => template.StateTypeDefinition == stateDefinition).ToArray();
        if (candidates.Length != 1) { throw new InvalidDataException("The explicit inline DTO does not uniquely identify retained history."); }
        DurableSchema inline = InferSchemaFromState(nominal, candidates[0].Version, stateType, depth);
        return new(1, TypeTag.InlineValue, inlineSchema: inline);
    }

    private DurableSchema InferSchemaFromPrior(DurableSchema prior, int version) {
        StateSchemaBinding input = BindSchema(prior);
        StateSchemaTemplate target = GetTemplate(prior.SchemaId, version);
        Dictionary<(TypeExpr Expression, int? Version), DurableFieldInfo> selections = new(input.ValueSlots);
        return BuildSchema(prior.Type, target, selections, 1);
    }

    private DurableSchema BuildSchema(TypeExpr owner, StateSchemaTemplate template,
        Dictionary<(TypeExpr Expression, int? Version), DurableFieldInfo> selections, int depth) {
        if (depth > 256) { throw new InvalidDataException("Exact Schema inference exceeded its layout depth bound."); }
        DurableSchema? ancestor = null;
        if (template.BaseSchema is { } basePattern) {
            TypeExpr baseType = Substitute(basePattern.Type, owner.Arguments);
            var baseSelections = RebaseSelections(GetTemplate(baseType.DefinitionId!, basePattern.Version), basePattern.Type.Arguments, selections);
            ancestor = BuildSchema(baseType, GetTemplate(baseType.DefinitionId!, basePattern.Version), baseSelections, depth + 1);
        }
        DurableFieldInfo[] fields = template.Fields.Select(field => WithFieldId(
            BuildSlot(field.ValueType, field.InlineVersion, owner.Arguments, selections, depth + 1), field.FieldId)).ToArray();
        DurableSchema result = new(owner, template.Version, fields, ancestor, template.Kind);
        CheckRegistered(result);
        return result;
    }

    private DurableFieldInfo BuildSlot(TypeExpr expression, int? version, IReadOnlyList<TypeExpr> ownerArguments,
        Dictionary<(TypeExpr Expression, int? Version), DurableFieldInfo> selections, int depth) {
        if (depth > 256) { throw new InvalidDataException("Exact Schema inference exceeded its layout depth bound."); }
        if (selections.TryGetValue((expression, version), out DurableFieldInfo selected)) { return selected; }
        TypeExpr nominal = Substitute(expression, ownerArguments);
        if (nominal.IsNullable) {
            // An explicit Nullable(T) preserves T's source expression. A parameter
            // closed to Nullable instead carries that entire slot as its operand.
            TypeExpr child = expression.IsNullable ? expression.ElementType! : nominal.ElementType!;
            return DurableFieldInfo.Nullable(1, BuildSlot(child, version, ownerArguments, selections, depth + 1));
        }
        if (nominal.Kind == TypeExprKind.Builtin) { return new(1, nominal.BuiltinTag); }
        if (nominal.IsArray || nominal.IsList || nominal.IsDictionary) { return DurableFieldInfo.Reference(1, nominal); }
        if (GetNamedDeclarationKind(nominal) == SchemaKind.ReferenceObject) { return DurableFieldInfo.Reference(1, nominal); }
        if (!version.HasValue) {
            throw new InvalidDataException("An intermediate inline value has no unique historical layout; declare a closed owner conversion.");
        }
        StateSchemaTemplate nested = GetTemplate(nominal.DefinitionId!, version.Value);
        Dictionary<(TypeExpr Expression, int? Version), DurableFieldInfo> nestedSelections = expression.Kind == TypeExprKind.Named
            ? RebaseSelections(nested, expression.Arguments, selections) : [];
        DurableSchema inline = BuildSchema(nominal, nested, nestedSelections, depth);
        return new(1, TypeTag.InlineValue, inlineSchema: inline);
    }

    private static Dictionary<(TypeExpr Expression, int? Version), DurableFieldInfo> RebaseSelections(
        StateSchemaTemplate child, IReadOnlyList<TypeExpr> childArguments,
        Dictionary<(TypeExpr Expression, int? Version), DurableFieldInfo> parent) {
        Dictionary<(TypeExpr Expression, int? Version), DurableFieldInfo> result = [];
        foreach (StateParameterTemplate parameter in child.StateParameters) {
            TypeExpr outer = Substitute(parameter.Expression, childArguments);
            if (parent.TryGetValue((outer, parameter.InlineVersion), out DurableFieldInfo slot)) {
                result[(parameter.Expression, parameter.InlineVersion)] = slot;
            }
        }
        return result;
    }

    private static void AddSelection(Dictionary<(TypeExpr Expression, int? Version), DurableFieldInfo> selections,
        (TypeExpr Expression, int? Version) key, DurableFieldInfo slot) {
        DurableFieldInfo canonical = WithFieldId(slot, 1);
        if (selections.TryGetValue(key, out DurableFieldInfo previous) && previous != canonical) {
            throw new InvalidDataException("The explicit DTO binds one value expression to conflicting exact layouts.");
        }
        selections[key] = canonical;
        if (key.Expression.IsNullable && canonical.NullableLayout is { } nullable) {
            AddSelection(selections, (key.Expression.ElementType!, key.Version), nullable.ElementSlot);
        }
    }

    private static void UnifyStateType(Type pattern, Type actual, Dictionary<Type, Type> variables) {
        if (pattern.IsGenericParameter) {
            if (variables.TryGetValue(pattern, out Type? previous) && previous != actual) {
                throw new InvalidDataException("One upgrade type parameter cannot bind different DTO representations.");
            }
            variables[pattern] = actual;
            return;
        }
        if (pattern.IsGenericType && actual.IsGenericType && pattern.GetGenericTypeDefinition() == actual.GetGenericTypeDefinition()) {
            Type[] patterns = pattern.GetGenericArguments(), arguments = actual.GetGenericArguments();
            for (int index = 0; index < patterns.Length; index++) { UnifyStateType(patterns[index], arguments[index], variables); }
            return;
        }
        if (pattern != actual) { throw new InvalidDataException("The selected upgrade signature does not match its exact DTO endpoints."); }
    }

    private static Type? SubstituteStateType(Type pattern, IReadOnlyDictionary<Type, Type> variables) {
        if (pattern.IsGenericParameter) { return variables.TryGetValue(pattern, out Type? value) ? value : null; }
        if (!pattern.ContainsGenericParameters) { return pattern; }
        if (!pattern.IsGenericType) { return null; }
        Type?[] arguments = pattern.GetGenericArguments().Select(argument => SubstituteStateType(argument, variables)).ToArray();
        if (arguments.Any(static argument => argument is null)) { return null; }
        try { return pattern.GetGenericTypeDefinition().MakeGenericType(arguments.Select(static argument => argument!).ToArray()); }
        catch (ArgumentException error) { throw new InvalidDataException("The selected DTO closure violates its CLR constraints.", error); }
    }

    private static MethodInfo CloseUpgradeMethod(MethodInfo method, IReadOnlyDictionary<Type, Type> variables) {
        if (!method.IsGenericMethodDefinition) { return method; }
        Type[] arguments = method.GetGenericArguments().Select(parameter => variables.TryGetValue(parameter, out Type? type) ? type :
            throw new InvalidDataException("An upgrade method parameter cannot be uniquely inferred from its endpoints.")).ToArray();
        try { return method.MakeGenericMethod(arguments); }
        catch (ArgumentException error) { throw new InvalidDataException("The selected upgrade violates its CLR constraints.", error); }
    }

    private static UpgradeStep CreateUpgradeStep(DurableSchema source, DurableSchema target, Type priorType, Type nextType,
        StateUpgradeProvider provider, MethodInfo method, UpgradeDependencies dependencies) {
        MethodInfo factory = typeof(StateBindingContext).GetMethod(nameof(CreateUpgradeStepTyped), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(priorType, nextType);
        return factory.CreateDelegate<Func<DurableSchema, DurableSchema, StateUpgradeProvider, MethodInfo, UpgradeDependencies, UpgradeStep>>()(source, target, provider, method, dependencies);
    }

    private static UpgradeStep CreateUpgradeStepTyped<TPrior, TNext>(DurableSchema source, DurableSchema target,
        StateUpgradeProvider provider, MethodInfo method, UpgradeDependencies dependencies) where TPrior : unmanaged where TNext : unmanaged =>
        new TypedUpgradeStep<TPrior, TNext>(source, target, provider, method, dependencies);

    private sealed record UpgradePlan(UpgradeStep[] Steps, ExactSchemaRequirementSet Requirements, Type CurrentStateType);
    private abstract class UpgradeStep {
        internal abstract ObjectStateRecord Apply(ObjectStateRecord source);
    }
    private delegate void UpgradeAction<TPrior, TNext>(in TPrior prior, out TNext next, UpgradeContext context)
        where TPrior : unmanaged where TNext : unmanaged;
    private delegate void LegacyUpgradeAction<TPrior, TNext>(in TPrior prior, out TNext next)
        where TPrior : unmanaged where TNext : unmanaged;

    private sealed class TypedUpgradeStep<TPrior, TNext> : UpgradeStep where TPrior : unmanaged where TNext : unmanaged {
        private readonly DurableSchema _source;
        private readonly DurableSchema _target;
        private readonly UpgradeAction<TPrior, TNext> _action;
        private readonly UpgradeDependencies _dependencies;

        internal TypedUpgradeStep(DurableSchema source, DurableSchema target, StateUpgradeProvider provider, MethodInfo method, UpgradeDependencies dependencies) {
            _source = source;
            _target = target;
            _dependencies = dependencies;
            if (provider.IsLegacyTwoParameter) {
                LegacyUpgradeAction<TPrior, TNext> oldAction = method.CreateDelegate<LegacyUpgradeAction<TPrior, TNext>>();
                _action = (in TPrior prior, out TNext next, UpgradeContext _) => oldAction(in prior, out next);
            } else { _action = method.CreateDelegate<UpgradeAction<TPrior, TNext>>(); }
        }

        internal override ObjectStateRecord Apply(ObjectStateRecord source) {
            if (!source.Schema!.Equals(_source)) { throw new InvalidDataException("The bound upgrade received a different exact source."); }
            TPrior prior;
            try { prior = source.GetState<TPrior>(); }
            catch (InvalidOperationException error) { throw new InvalidDataException("The bound upgrade received a different source DTO.", error); }
            UpgradeContext context = _dependencies.CreateContext(source.Id, _source, _target);
            _action(in prior, out TNext next, context);
            return new(source.Id, _target, next);
        }
    }
}
