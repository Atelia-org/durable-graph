using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Reflection;
using Atelia.Rbf;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class ValueUpgradeCatalogTests {
    [Fact]
    public void RulesRegisteredLaterAreVisibleOnlyToLaterOperations() {
        StateModelRegistry registry = new();
        ModelExecutionEnvironment before = registry.CreateEnvironment();
        StateValueUpgradeRuleSet rules = new(typeof(Rules), [], allowKeepExact: true);
        registry.Register(rules);
        ModelExecutionEnvironment after = registry.CreateEnvironment();
        Assert.Throws<InvalidDataException>(() => before.GetValueUpgradeRuleSet(typeof(Rules)));
        Assert.Same(rules, after.GetValueUpgradeRuleSet(typeof(Rules)));
        registry.Register(rules);
        Assert.Throws<InvalidOperationException>(() => registry.Register(new StateValueUpgradeRuleSet(typeof(Rules), [])));
        Assert.Same(rules, after.GetValueUpgradeRuleSet(typeof(Rules)));
        Assert.Same(rules, registry.CreateEnvironment().GetValueUpgradeRuleSet(typeof(Rules)));
    }

    [Fact]
    public void IndependentApplicationCatalogsDoNotShareRulesForTheSameMarker() {
        StateModelRegistry first = new(), second = new();
        StateValueUpgradeRuleSet keep = new(typeof(Rules), [], allowKeepExact: true);
        StateValueUpgradeRuleSet reject = new(typeof(Rules), []);
        first.Register(keep);
        second.Register(reject);
        Assert.Same(keep, first.CreateEnvironment().GetValueUpgradeRuleSet(typeof(Rules)));
        Assert.Same(reject, second.CreateEnvironment().GetValueUpgradeRuleSet(typeof(Rules)));
    }

    [Fact]
    public void RuleAndDependencyInputsAreFrozenBeforeRegistration() {
        StateUpgradeDependency dependency = new("value", typeof(Rules), new("Owner", 1), new("Owner", 1));
        List<StateUpgradeDependency> dependencies = [dependency];
        StateUpgradeProvider owner = new("Owner", 1, Method(nameof(Keep)), dependencies: dependencies);
        List<StateValueUpgradeProvider> providers = [new(
            TypeExpr.Builtin(TypeTag.Int32), null, TypeExpr.Builtin(TypeTag.Int32), null, Method(nameof(Keep)))];
        StateValueUpgradeRuleSet rules = new(typeof(Rules), providers);
        providers.Clear();
        dependencies.Clear();
        Assert.Single(rules.Providers);
        Assert.Equal(dependency, Assert.Single(owner.Dependencies));
    }

    [Fact]
    public void GeneratedRegistrationCanUseTheReaderOnlySinkWithoutRunningUpgrade() {
        StateReaderRegistry registry = new();
        ModelExecutionEnvironment before = registry.CreateEnvironment(schemaLookup: null);
        StateValueUpgradeRuleSet rules = new(typeof(Rules), []);
        IStateDefinitionRegistration sink = registry;
        sink.Register(rules);
        sink.Register(rules);
        Assert.Throws<InvalidDataException>(() => before.GetValueUpgradeRuleSet(typeof(Rules)));
        Assert.Same(rules, registry.CreateEnvironment(schemaLookup: null).GetValueUpgradeRuleSet(typeof(Rules)));
        Assert.Throws<InvalidOperationException>(() => sink.Register(new StateValueUpgradeRuleSet(typeof(Rules), [])));
    }

    private static MethodInfo Method(string name) => typeof(ValueUpgradeCatalogTests).GetMethod(name,
        BindingFlags.Static | BindingFlags.NonPublic)!;
    private static void Keep(in int prior, out int next, UpgradeContext context) => next = prior;
    private sealed class Rules;
}
