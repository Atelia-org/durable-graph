using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Reflection;
using Atelia.DurableGraph.Build;
using Atelia.DurableGraph.Generator;
using Atelia.DurableGraph.Persistence;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void GeneratedValueRulesComposeOpenOwnerAndPairWithHistoricalTypedAdapters() {
        using AncestryHistoryDirectory files = new();
        GeneratorTestRun first = RunGenerator(ValueUpgradeModelSource(1));
        AssertSchemaOnlyCompiles(first);
        new SchemaHistoryTool().Publish(files.WriteManifest(first), files.History);
        GeneratorTestRun run = RunGenerator(ValueUpgradeModelSource(2, includeUpgrades: true), files.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(run);
        string generated = GeneratedSource(run, "DurableGenericStates.g.cs");
        Assert.Contains("public static class UpgradeSlots_5570677261646573_426F78", generated);
        Assert.Contains("public const string Value = \"Value\";", generated);
        Assert.Contains("TypeExpr.Named(\"Pair\", global::Atelia.DurableGraph.Schema.TypeExpr.Parameter(0))", generated);
        Assert.Contains("allowKeepExact: true", generated);
        Assert.DoesNotContain("DynamicInvoke", generated);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        StateModelRegistry registry = new();
        assembly.GetType("Atelia.DurableGraph.Generated.DurableDefinitions")!.GetMethod("Register")!
            .CreateDelegate<Action<IStateDefinitionRegistration>>()(registry);
        StateBindingContext snapshot = registry.CreateEnvironment();
        Type pair = assembly.GetType("Pair`1")!.MakeGenericType(assembly.GetType("Point")!);
        StateModelBinding model = snapshot.ResolveCurrentModel(assembly.GetType("Box`1")!.MakeGenericType(pair));
        object prior = assembly.GetType("Host")!.GetMethod("Prior")!.CreateDelegate<Func<object>>()();
        DurableSchema source = snapshot.InferSchemaFromState(model.CurrentSchema.Type, 1, prior.GetType());
        foreach (uint id in new uint[] { 7, 11 }) {
            ObjectStateRecord current = model.Normalize(new(new(id), source, prior));
            object value = StateModelField(current, "Segment0Field1")!;
            Assert.Equal(20L, ValueStateField(ValueStateField(value, "Segment0Field1"), "Segment0Field1"));
            Assert.Equal(30L, ValueStateField(ValueStateField(value, "Segment0Field2"), "Segment0Field1"));
        }
        UpgradeContext[] contexts = assembly.GetType("Host")!.GetMethod("Contexts")!.CreateDelegate<Func<UpgradeContext[]>>()();
        Assert.Equal<uint>([7, 7, 7, 7, 11, 11, 11, 11], contexts.Select(context => context.ObjectId.Value));
        Assert.All(contexts, context => {
            Assert.Equal(source, context.SourceObjectSchema);
            Assert.Equal(model.CurrentSchema, context.TargetObjectSchema);
        });
        Assert.NotSame(contexts[0], contexts[1]);
        Assert.NotSame(contexts[0], contexts[4]);
        StateModelBinding numbers = snapshot.ResolveCurrentModel(assembly.GetType("Box`1")!.MakeGenericType(typeof(int)));
        object number = assembly.GetType("Host")!.GetMethod("Number")!.CreateDelegate<Func<object>>()();
        DurableSchema oldNumbers = snapshot.InferSchemaFromState(numbers.CurrentSchema.Type, 1, number.GetType());
        Assert.Equal(5, StateModelField(numbers.Normalize(new(new(23), oldNumbers, number)), "Segment0Field1"));
    }

    [Fact]
    public void GeneratedValueProviderKeepsHistoricalFamilyAfterInlineDomainRemoval() {
        using AncestryHistoryDirectory files = new();
        GeneratorTestRun previous = RunGenerator(ValueUpgradeModelSource(1));
        AssertSchemaOnlyCompiles(previous);
        new SchemaHistoryTool().Publish(files.WriteManifest(previous), files.History);
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using P = Atelia.DurableGraph.Generated.Family_506F696E74;
            [ValueUpgradeRuleSet] public sealed class Rules;
            public static class Upgrades {
                [DurableValueUpgrade(typeof(Rules), "Point", 1, 1)]
                public static void Point(in P.V1 prior, out P.V1 next, UpgradeContext context) => next = new(prior.Segment0Field1 + 2);
            }
            """, files.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(run);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        Assert.Null(assembly.GetType("Point"));
        Assert.NotNull(assembly.GetType("Atelia.DurableGraph.Generated.Family_506F696E74+V1"));
        StateModelRegistry registry = new();
        assembly.GetType("Atelia.DurableGraph.Generated.DurableDefinitions")!.GetMethod("Register")!
            .CreateDelegate<Action<IStateDefinitionRegistration>>()(registry);
        var rules = registry.CreateEnvironment().GetValueUpgradeRuleSet(assembly.GetType("Rules")!);
        Assert.Single(rules.Providers);
    }

    [Fact]
    public void GeneratedKeepExactRuleSetRegistersWithoutAnyDurableDeclarationsOrHistory() {
        GeneratorTestRun run = RunGenerator("""
            [Atelia.DurableGraph.ValueUpgradeRuleSet(AllowKeepExact=true)] public sealed class Rules;
            """);
        AssertSchemaOnlyCompiles(run);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        StateModelRegistry registry = new();
        assembly.GetType("Atelia.DurableGraph.Generated.DurableDefinitions")!.GetMethod("Register")!
            .CreateDelegate<Action<IStateDefinitionRegistration>>()(registry);
        StateValueUpgradeRuleSet rules = registry.CreateEnvironment().GetValueUpgradeRuleSet(assembly.GetType("Rules")!);
        Assert.True(rules.AllowKeepExact);
        Assert.Empty(rules.Providers);
    }

    [Fact]
    public void GeneratedOwnerSelectsDifferentRulesForTwoEqualStateRepresentations() {
        using AncestryHistoryDirectory files = new();
        const string declarations = """
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using W = Atelia.DurableGraph.Generated.Family_576F726C64;
            using P = Atelia.DurableGraph.Generated.Family_506F696E74;
            [ValueUpgradeRuleSet] public sealed class VersionMarker;
            [DurableType("Point",1)] public partial struct Point { [DurableField(1)] public int X; }
            [DurableType("World",VERSION)] public partial class World:IDurableObject {
                [DurableField(1)] public Point First; [DurableField(2)] public Point Second;
            }
            """;
        GeneratorTestRun first = RunGenerator(declarations.Replace("VERSION", "1"));
        AssertSchemaOnlyCompiles(first);
        new SchemaHistoryTool().Publish(files.WriteManifest(first), files.History);
        GeneratorTestRun run = RunGenerator(declarations.Replace("VERSION", "2") + """
            [ValueUpgradeRuleSet(AllowKeepExact=true)] public sealed class Scale;
            [ValueUpgradeRuleSet(AllowKeepExact=true)] public sealed class Offset;
            public static class Upgrades {
                [DurableUpgrade(typeof(World),1)]
                [UpgradeDependency("Scaled",typeof(Scale),"World",1,"World",1)]
                [UpgradeDependency("Offset",typeof(Offset),"World",2,"World",2)]
                public static void World(in W.V1 prior,out W.V2 next,UpgradeContext context) {
                    var scale=context.GetValueUpgrade<P.V1,P.V1>("Scaled");
                    var offset=context.GetValueUpgrade<P.V1,P.V1>("Offset");
                    next=new(scale(in prior.Segment0Field1),offset(in prior.Segment0Field2));
                }
                [DurableValueUpgrade(typeof(Scale),"Point",1,1)]
                public static void Scale(in P.V1 prior,out P.V1 next,UpgradeContext context)=>next=new(prior.Segment0Field1*10);
                [DurableValueUpgrade(typeof(Offset),"Point",1,1)]
                public static void Offset(in P.V1 prior,out P.V1 next,UpgradeContext context)=>next=new(prior.Segment0Field1+100);
            }
            public static class Host { public static object Prior()=>new W.V1(new P.V1(2),new P.V1(2)); }
            """, files.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(run);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        StateModelRegistry registry = new();
        assembly.GetType("Atelia.DurableGraph.Generated.DurableDefinitions")!.GetMethod("Register")!
            .CreateDelegate<Action<IStateDefinitionRegistration>>()(registry);
        StateBindingContext snapshot = registry.CreateEnvironment();
        StateModelBinding model = snapshot.ResolveCurrentModel(assembly.GetType("World")!);
        object prior = assembly.GetType("Host")!.GetMethod("Prior")!.CreateDelegate<Func<object>>()();
        DurableSchema source = snapshot.InferSchemaFromState(model.CurrentSchema.Type, 1, prior.GetType());
        ObjectStateRecord next = model.Normalize(new(new(5), source, prior));
        Assert.Equal(20, ValueStateField(StateModelField(next, "Segment0Field1")!, "Segment0Field1"));
        Assert.Equal(102, ValueStateField(StateModelField(next, "Segment0Field2")!, "Segment0Field1"));
    }

    [Theory]
    [InlineData("bad-key")]
    [InlineData("")]
    [InlineData("  ")]
    public void GeneratorRejectsDependencyKeysThatCannotNameGeneratedConstants(string key) {
        GeneratorTestRun run = RunGenerator(ValueUpgradeDiagnosticSource(
            $"[UpgradeDependency(\"{key}\", typeof(Rules), \"Box\", 1, \"Box\", 1)]"));
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0020");
    }

    [Fact]
    public void GeneratorRejectsDuplicateDependencyKeysAndUndeclaredRules() {
        const string dependency = "[UpgradeDependency(\"Value\", typeof(Rules), \"Box\", 1, \"Box\", 1)]";
        GeneratorTestRun duplicate = RunGenerator(ValueUpgradeDiagnosticSource(dependency + dependency));
        Assert.Contains(duplicate.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0020");
        GeneratorTestRun unregistered = RunGenerator(ValueUpgradeDiagnosticSource(dependency).Replace("[ValueUpgradeRuleSet]", ""));
        Assert.Contains(unregistered.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0020");
    }

    [Fact]
    public void GeneratorRejectsOrphanDependencyAndLegacyTwoParameterOwnerDependency() {
        const string dependency = "[UpgradeDependency(\"Value\", typeof(Rules), \"Box\", 1, \"Box\", 1)]";
        GeneratorTestRun orphan = RunGenerator(ValueUpgradeDiagnosticSource(dependency).Replace("[DurableUpgrade(typeof(Box<>),1)]", ""));
        Assert.Contains(orphan.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0020");
        GeneratorTestRun legacy = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [ValueUpgradeRuleSet] public sealed class Rules;
            [DurableType("Box",2)] public partial class Box : IDurableObject {
                [UpgradeDependency("Value", typeof(Rules), "Box", 1, "Box", 1)]
                private static void UpgradeStateV1ToV2(in int prior, out int next) => next=prior;
            }
            """);
        Assert.Contains(legacy.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0020" && diagnostic.GetMessage().Contains("add UpgradeContext"));
    }

    [Theory]
    [InlineData("in P.V1 prior, out P.V1 next")]
    [InlineData("P.V1 prior, out P.V1 next, UpgradeContext context")]
    [InlineData("in P.V1 prior, out P.V1 next, ref UpgradeContext context")]
    public void GeneratorRejectsInvalidValueProviderSignatures(string parameters) {
        GeneratorTestRun run = RunGenerator($$"""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using P = Atelia.DurableGraph.Generated.Family_506F696E74;
            [DurableType("Point",1)] public partial struct Point { [DurableField(1)] public int X; }
            [ValueUpgradeRuleSet] public sealed class Rules;
            public static class Upgrades {
                [DurableValueUpgrade(typeof(Rules),"Point",1,1)]
                public static void Point({{parameters}}) => next=prior;
            }
            """);
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0020");
    }

    [Fact]
    public void GeneratorEmitsExactDeclarationSegmentsAndEscapedKeywordKey() {
        using AncestryHistoryDirectory files = new();
        GeneratorTestRun first = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("Box",1)] public partial class Box<T>:IDurableObject { [DurableField(1)] public T Value=default!; }
            """);
        AssertSchemaOnlyCompiles(first);
        new SchemaHistoryTool().Publish(files.WriteManifest(first), files.History);
        GeneratorTestRun run = RunGenerator(ValueUpgradeDiagnosticSource(
            "[UpgradeDependency(\"class\", typeof(Rules), \"Ancestor\", 7, \"Box\", 9)]"), files.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(run);
        string generated = GeneratedSource(run, "DurableGenericStates.g.cs");
        Assert.Contains("public const string @class = \"class\";", generated);
        Assert.Contains("new(\"Ancestor\", 7), new(\"Box\", 9)", generated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratorRejectsRuleMarkersFromAnotherAssemblyInsteadOfDroppingProviders(bool dependency) {
        GeneratorTestRun marker = RunGenerator("[Atelia.DurableGraph.ValueUpgradeRuleSet] public sealed class ExternalRules;");
        using MemoryStream bytes = new();
        var emitted = marker.OutputCompilation.Emit(bytes);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        MetadataReference reference = MetadataReference.CreateFromImage(bytes.ToArray());
        string source = dependency
            ? ValueUpgradeDiagnosticSource("[UpgradeDependency(\"Value\", typeof(ExternalRules), \"Box\", 1, \"Box\", 1)]")
            : """
                using Atelia.DurableGraph;
                using Atelia.DurableGraph.Schema;
                using Atelia.DurableGraph.Runtime;
                using P = Atelia.DurableGraph.Generated.Family_506F696E74;
                [DurableType("Point",1)] public partial struct Point { [DurableField(1)] public int X; }
                public static class Upgrades {
                    [DurableValueUpgrade(typeof(ExternalRules),"Point",1,1)]
                    public static void Point(in P.V1 prior,out P.V1 next,UpgradeContext context)=>next=prior;
                }
                """;
        CSharpCompilation input = RunGenerator("class Placeholder;").OutputCompilation.RemoveAllSyntaxTrees()
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(source, ParseOptions)).AddReferences(reference);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generators: [new DurableGraphGenerator().AsSourceGenerator()], parseOptions: ParseOptions);
        driver = driver.RunGenerators(input);
        Assert.Contains(driver.GetRunResult().Diagnostics,
            diagnostic => diagnostic.Id == "DG0020" && diagnostic.GetMessage().Contains("same compilation"));
    }

    private static string ValueUpgradeDiagnosticSource(string dependencies) => $$"""
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using B = Atelia.DurableGraph.Generated.Family_426F78;
        [DurableType("Box",2)] public partial class Box<T> : IDurableObject { [DurableField(1)] public T Value=default!; }
        [ValueUpgradeRuleSet] public sealed class Rules;
        public static class Upgrades {
            [DurableUpgrade(typeof(Box<>),1)] {{dependencies}}
            public static void Box<A,B>(in global::Atelia.DurableGraph.Generated.Family_426F78.V1<A> prior,
                out global::Atelia.DurableGraph.Generated.Family_426F78.V2<B> next, UpgradeContext context)
                where A:unmanaged where B:unmanaged => next=default;
        }
        """;

    private static object ValueStateField(object value, string name) => value.GetType().GetField(name)!.GetValue(value)!;

    private static string ValueUpgradeModelSource(int version, bool includeUpgrades = false) => $$"""
        global using P = Atelia.DurableGraph.Generated.Family_506F696E74;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using B = Atelia.DurableGraph.Generated.Family_426F78;
        using PairState = Atelia.DurableGraph.Generated.Family_50616972;
        [DurableType("Point",{{version}})] public partial struct Point { [DurableField(1)] public {{(version == 1 ? "int" : "long")}} X; }
        [DurableType("Pair",{{version}})] public partial struct Pair<T> { [DurableField(1)] public T Left; [DurableField(2)] public T Right; }
        [DurableType("Box",{{version}})] public partial class Box<T> : IDurableObject { [DurableField(1)] public T Value=default!; }
        """ + (includeUpgrades ? """
        [ValueUpgradeRuleSet(AllowKeepExact=true)] public sealed class Coordinates;
        public static class Upgrades {
            public static readonly System.Collections.Generic.List<UpgradeContext> Contexts = new();
            [DurableUpgrade(typeof(Box<>),1)]
            [UpgradeDependency("Value",typeof(Coordinates),"Box",1,"Box",1)]
            public static void Box<A,C>(in B.V1<A> prior, out B.V2<C> next, UpgradeContext context)
                where A:unmanaged where C:unmanaged {
                Contexts.Add(context);
                var value=context.GetValueUpgrade<A,C>(Atelia.DurableGraph.Generated.UpgradeSlots_5570677261646573_426F78.Value);
                next=new(value(in prior.Segment0Field1));
            }
            [DurableValueUpgrade(typeof(Coordinates),"Pair",1,2)]
            [UpgradeDependency("Element",typeof(Coordinates),"Pair",1,"Pair",1)]
            public static void Pair<A,C>(in PairState.V1<A> prior, out PairState.V2<C> next, UpgradeContext context)
                where A:unmanaged where C:unmanaged {
                Contexts.Add(context);
                var element=context.GetValueUpgrade<A,C>(Atelia.DurableGraph.Generated.UpgradeSlots_5570677261646573_50616972.Element);
                next=new(element(in prior.Segment0Field1),element(in prior.Segment0Field2));
            }
            [DurableValueUpgrade(typeof(Coordinates),"Point",1,2)]
            public static void Point(in P.V1 prior,out P.V2 next,UpgradeContext context) {
                Contexts.Add(context); next=new(prior.Segment0Field1*10L);
            }
        }
        public static class Host {
            public static object Prior()=>new B.V1<PairState.V1<P.V1>>(new(new(2),new(3)));
            public static object Number()=>new B.V1<int>(5);
            public static UpgradeContext[] Contexts()=>Upgrades.Contexts.ToArray();
        }
        """ : "");
}
