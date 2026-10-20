using System.Reflection;
using Atelia.DurableGraph.Persistence;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Theory]
    [InlineData("TState0")]
    [InlineData("TOps0")]
    [InlineData("TProjection0")]
    public void DomainParameterNamesCannotBeCapturedByCurrentProjectionParameters(string parameter) {
        GeneratorTestRun run = RunGenerator($$"""
            using Atelia.DurableGraph;
            [DurableType("names.box", 1)]
            public partial class Box<{{parameter}}> : IDurableObject where {{parameter}} : class {
                [DurableField(1)] public {{parameter}} Value = default!;
            }
            [DurableType("names.cell", 1)]
            public partial struct Cell<{{parameter}}> where {{parameter}} : class {
                [DurableField(1)] public {{parameter}} Value;
            }
            """);
        AssertSchemaOnlyCompiles(run);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        StateModelRegistry models = ParameterNameModels(assembly);
        var environment = models.CreateEnvironment();
        Type box = assembly.GetType("Box`1")!.MakeGenericType(typeof(string));
        Type cell = assembly.GetType("Cell`1")!.MakeGenericType(typeof(string));
        Assert.Equal(box, environment.ResolveCurrentModel(box).DomainType);
        Assert.Equal(cell, environment.ResolveCurrentValue(cell).DomainType);
    }

    [Theory]
    [InlineData("TState0")]
    [InlineData("TOps0")]
    [InlineData("TProjection0")]
    [InlineData("@TOps0")]
    public void PhantomParameterNamesCannotChangeTheActualClosedDomainType(string phantom) {
        GeneratorTestRun run = RunGenerator($$"""
            using Atelia.DurableGraph;
            [DurableType("names.phantom", 1)]
            public partial class Phantom<{{phantom}}, T> : IDurableObject {
                [DurableField(1)] public T Value = default!;
            }
            """);
        AssertSchemaOnlyCompiles(run);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        Type domain = assembly.GetType("Phantom`2")!.MakeGenericType(typeof(string), typeof(int));
        var binding = ParameterNameModels(assembly).CreateEnvironment().ResolveCurrentModel(domain);
        Assert.Equal(domain, binding.DomainType);
        Assert.Single(Assert.Single(binding.Readers).StateType.GetGenericArguments()); // The phantom remains absent from the DTO.
    }

    [Fact]
    public void GeneratedParameterPrefixAvoidsAdversarialDomainAndPhantomNames() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            [DurableType("names.adversarial", 1)]
            public partial class __DurableState0<__Durable_State0, __Durable__TOps0> : IDurableObject {
                [DurableField(1)] public __Durable__TOps0 Value = default!;
            }
            [DurableType("names.type-name", 1)]
            public partial class __DurableTState0<T> : IDurableObject {
                [DurableField(1)] public T Value = default!;
            }
            """);
        AssertSchemaOnlyCompiles(run);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        var environment = ParameterNameModels(assembly).CreateEnvironment();
        foreach (Type domain in new[] {
            assembly.GetType("__DurableState0`2")!.MakeGenericType(typeof(string), typeof(int)),
            assembly.GetType("__DurableTState0`1")!.MakeGenericType(typeof(int)),
        }) {
            Assert.Equal(domain, environment.ResolveCurrentModel(domain).DomainType);
        }
    }

    [Fact]
    public void ParameterNameCollisionsPreserveMultipleBaseSlotsAndInlineProjectionAcrossColdSave() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Persistence;
            using Atelia.DurableGraph.Generated;
            [DurableType("names.pair", 1)]
            public partial struct Pair<TState0, TOps0> {
                [DurableField(1)] public TState0 Left;
                [DurableField(2)] public TOps0 Right;
                public Pair(TState0 left, TOps0 right) { Left = left; Right = right; }
            }
            [DurableType("names.base", 1)]
            public partial class Base<TOps0, TProjection0> : IDurableObject {
                [DurableField(1)] public TOps0 BaseLeft;
                [DurableField(2)] public TProjection0 BaseRight;
                public Base(TOps0 left, TProjection0 right) { BaseLeft = left; BaseRight = right; }
            }
            [DurableType("names.derived", 1)]
            public partial class Derived<TState0, TOps0, TProjection0> : Base<TOps0, TState0> {
                [DurableField(1)] public Pair<TState0, TProjection0> Pair;
                [DurableField(2)] public TProjection0 Own;
                public Derived(TOps0 left, TState0 right, Pair<TState0, TProjection0> pair, TProjection0 own)
                    : base(left, right) { Pair = pair; Own = own; }
            }
            public static class Host {
                public static bool Probe(string path) {
                    var models = new StateModelRegistry();
                    DurableDefinitions.Register(models);
                    var original = new Derived<string, int, long>(17, "base", new("inline", 23L), 29L);
                    using (var repository = FixtureGraphRepository.CreateNew(path, models)) {
                        using var session = repository.Create(original);
                        session.Commit(new(100, 100));
                        original.BaseLeft = 41;
                        original.BaseRight = "updated base";
                        original.Pair = new("updated inline", 31L);
                        original.Own = 37L;
                        session.Commit(new(100, 100));
                    }
                    using (var repository = FixtureGraphRepository.OpenExisting(path, models)) {
                        using var session = repository.Load<Derived<string, int, long>>();
                        var restored = session.World;
                        if (ReferenceEquals(restored, original) || restored.BaseLeft != 41 ||
                            restored.BaseRight != "updated base" || restored.Pair.Left != "updated inline" ||
                            restored.Pair.Right != 31L || restored.Own != 37L) return false;
                        session.Commit(new(100, 100));
                    }
                    return true;
                }
            }
            """);
        AssertSchemaOnlyCompiles(run);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        using RawBaseDirectory directory = new();
        Assert.True(assembly.GetType("Host")!.GetMethod("Probe")!.CreateDelegate<Func<string, bool>>()(directory.Path));
    }

    private static StateModelRegistry ParameterNameModels(Assembly assembly) {
        StateModelRegistry models = new();
        assembly.GetType("Atelia.DurableGraph.Generated.DurableDefinitions")!.GetMethod("Register")!.Invoke(null, [models]);
        return models;
    }
}
