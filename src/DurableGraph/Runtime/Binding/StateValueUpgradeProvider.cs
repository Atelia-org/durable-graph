using Atelia.DurableGraph.Schema;
using System.Collections.Immutable;
using System.Reflection;

namespace Atelia.DurableGraph.Runtime;

/// <summary>Selects one declared field, preserving its inheritance segment.</summary>
public sealed record StateUpgradeSlot {
    public StateUpgradeSlot(string declarationId, int fieldId) {
        ArgumentException.ThrowIfNullOrWhiteSpace(declarationId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fieldId);
        DeclarationId = declarationId;
        FieldId = fieldId;
    }

    public string DeclarationId { get; }
    public int FieldId { get; }
}

/// <summary>A named, explicitly selected value capability within one provider's local scope.</summary>
public sealed record StateUpgradeDependency {
    public StateUpgradeDependency(string key, Type ruleSet, StateUpgradeSlot source, StateUpgradeSlot target) {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(ruleSet);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (ruleSet.ContainsGenericParameters) { throw new ArgumentException("A rule-set identity must be a closed CLR type.", nameof(ruleSet)); }
        Key = key;
        RuleSet = ruleSet;
        Source = source;
        Target = target;
    }

    public string Key { get; }
    public Type RuleSet { get; }
    public StateUpgradeSlot Source { get; }
    public StateUpgradeSlot Target { get; }

    internal static ImmutableArray<StateUpgradeDependency> Freeze(IEnumerable<StateUpgradeDependency>? dependencies) {
        ImmutableArray<StateUpgradeDependency> result = dependencies?.ToImmutableArray() ?? [];
        if (result.Any(static item => item is null) ||
            result.Select(static item => item.Key).Distinct(StringComparer.Ordinal).Count() != result.Length) {
            throw new ArgumentException("Provider dependency keys must be non-null and unique within their local scope.", nameof(dependencies));
        }
        return result;
    }
}

/// <summary>One explicitly selected typed value conversion, separate from object version paths.</summary>
/// <remarks>Nominal patterns select candidates. Complete expected slots are checked after selection, without fallback.</remarks>
public sealed class StateValueUpgradeProvider {
    public StateValueUpgradeProvider(TypeExpr sourceType, int? sourceInlineVersion, TypeExpr targetType, int? targetInlineVersion,
        MethodInfo method, IEnumerable<StateUpgradeDependency>? dependencies = null,
        DurableFieldInfo? expectedSource = null, DurableFieldInfo? expectedTarget = null) {
        ValidateEndpoint(sourceType, sourceInlineVersion);
        ValidateEndpoint(targetType, targetInlineVersion);
        StateUpgradeProvider.ValidateMethod(method, allowLegacyTwoParameter: false);
        SourceType = sourceType;
        SourceInlineVersion = sourceInlineVersion;
        TargetType = targetType;
        TargetInlineVersion = targetInlineVersion;
        Method = method;
        Dependencies = StateUpgradeDependency.Freeze(dependencies);
        ExpectedSource = expectedSource is { } source ? DurableFieldInfo.WithFieldId(source, 1) : null;
        ExpectedTarget = expectedTarget is { } target ? DurableFieldInfo.WithFieldId(target, 1) : null;
    }

    public TypeExpr SourceType { get; }
    /// <summary>The direct inline version, or the direct inline child's version for Nullable endpoints.</summary>
    public int? SourceInlineVersion { get; }
    public TypeExpr TargetType { get; }
    /// <summary>The direct inline version, or the direct inline child's version for Nullable endpoints.</summary>
    public int? TargetInlineVersion { get; }
    public MethodInfo Method { get; }
    public ImmutableArray<StateUpgradeDependency> Dependencies { get; }
    public DurableFieldInfo? ExpectedSource { get; }
    public DurableFieldInfo? ExpectedTarget { get; }

    internal Type PriorType => Method.GetParameters()[0].ParameterType.GetElementType()!;
    internal Type NextType => Method.GetParameters()[1].ParameterType.GetElementType()!;

    internal bool IsSameCapability(StateValueUpgradeProvider other) =>
        SourceType == other.SourceType && SourceInlineVersion == other.SourceInlineVersion &&
        TargetType == other.TargetType && TargetInlineVersion == other.TargetInlineVersion && Method.Equals(other.Method) &&
        ExpectedSource == other.ExpectedSource && ExpectedTarget == other.ExpectedTarget && Dependencies.SequenceEqual(other.Dependencies);

    private static void ValidateEndpoint(TypeExpr type, int? version) {
        ArgumentNullException.ThrowIfNull(type);
        if ((type.Kind is not (TypeExprKind.Builtin or TypeExprKind.Named) && !type.IsArray && !type.IsList && !type.IsDictionary && !type.IsNullable) ||
            version is <= 0 || (version.HasValue && type.Kind != TypeExprKind.Named && !(type.IsNullable && type.ElementType!.Kind == TypeExprKind.Named))) {
            throw new ArgumentException("A value rule requires a builtin, named, array, List, Dictionary or Nullable pattern and only inline endpoints have versions.");
        }
    }
}

/// <summary>An immutable application-local rule collection frozen into the operation's code catalog.</summary>
public sealed class StateValueUpgradeRuleSet {
    public StateValueUpgradeRuleSet(Type ruleSet, IEnumerable<StateValueUpgradeProvider> providers, bool allowKeepExact = false, bool allowNullableLifting = false) {
        ArgumentNullException.ThrowIfNull(ruleSet);
        ArgumentNullException.ThrowIfNull(providers);
        if (ruleSet.ContainsGenericParameters) { throw new ArgumentException("A rule-set identity must be a closed CLR type.", nameof(ruleSet)); }
        RuleSet = ruleSet;
        List<StateValueUpgradeProvider> distinct = [];
        foreach (StateValueUpgradeProvider provider in providers) {
            ArgumentNullException.ThrowIfNull(provider);
            if (!distinct.Any(prior => prior.IsSameCapability(provider))) { distinct.Add(provider); }
        }
        Providers = distinct.ToImmutableArray();
        AllowKeepExact = allowKeepExact;
        AllowNullableLifting = allowNullableLifting;
    }

    public Type RuleSet { get; }
    public ImmutableArray<StateValueUpgradeProvider> Providers { get; }
    public bool AllowKeepExact { get; }
    /// <summary>Allows presence-preserving lifting after explicit providers and complete KeepExact selection.</summary>
    public bool AllowNullableLifting { get; }
}
