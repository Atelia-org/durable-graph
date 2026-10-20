using System.Globalization;
using System.Reflection;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Microsoft.CodeAnalysis;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Theory]
    [InlineData(32, false)]
    [InlineData(10000, false)]
    [InlineData(int.MaxValue, false)]
    [InlineData(32, true)]
    [InlineData(10000, true)]
    [InlineData(int.MaxValue, true)]
    public void MissingHistoryReportsOnlyTheFirstMissingVersionBeforeGeneratingBodies(int version, bool family) {
        GeneratorTestRun run = RunGenerator($$"""
            using Atelia.DurableGraph;
            [DurableType("Bounded", {{version}})]
            public partial class Model{{(family ? "<T>" : "")}} : IDurableObject {
                [DurableField(1)] public int Value;
            }
            """);
        Diagnostic missing = Assert.Single(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0014");
        Assert.Contains("version 1", missing.GetMessage());
        Assert.DoesNotContain(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "CS8785");
        Assert.Contains("// version:" + version.ToString(CultureInfo.InvariantCulture) + "\n",
            GeneratedSource(run, "DurableGraphSchemaHistoryCandidates.g.cs"));
        AssertMissingHistoryHasNoBodies(run);
    }

    [Fact]
    public void SuppressingMissingHistoryCannotGrantFamilyGenerationEligibility() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            [DurableType("Bounded", 10000)] public partial class Model<T> : IDurableObject { }
            """, [], null, null, new Dictionary<string, ReportDiagnostic> { ["DG0014"] = ReportDiagnostic.Suppress });
        Assert.DoesNotContain(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0014" && !diagnostic.IsSuppressed);
        GeneratedSource(run, "DurableGraphSchemaHistoryCandidates.g.cs");
        AssertMissingHistoryHasNoBodies(run);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HistoryPrefixGapsKeepOtherTypeAndCurrentShapeDiagnostics(bool currentMismatch) {
        List<AdditionalText> history = [
            BoundedHistory("A", 1), BoundedHistory("A", 2), BoundedHistory("A", 4),
        ];
        if (currentMismatch) history.Add(new InMemoryAdditionalText("a-current.dgschema",
            GenericTemplateHistoryTests.History("A", version: 32, arity: 1, body: "// field:1|3\n")));
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            [DurableType("A", 32)] public partial class A<T> : IDurableObject { [DurableField(1)] public T Value = default!; }
            [DurableType("B", 10000)] public partial class B<T> : IDurableObject { }
            """, history.ToArray());
        Diagnostic[] missing = run.GeneratorDiagnostics.Where(diagnostic => diagnostic.Id == "DG0014").ToArray();
        Assert.Equal(2, missing.Length);
        Assert.Contains(missing, diagnostic => diagnostic.GetMessage().Contains("schema 'A' version 3"));
        Assert.Contains(missing, diagnostic => diagnostic.GetMessage().Contains("schema 'B' version 1"));
        Assert.Equal(currentMismatch ? 1 : 0, run.GeneratorDiagnostics.Count(diagnostic => diagnostic.Id == "DG0015"));
        Assert.Contains("// version:32\n", GeneratedSource(run, "DurableGraphSchemaHistoryCandidates.g.cs"));
        AssertMissingHistoryHasNoBodies(run);
    }

    [Fact]
    public void CompleteHistoryStillGeneratesEveryRetainedReaderAndTheCurrentModel() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            [DurableType("Bounded", 4)] public partial class Model<T> : IDurableObject { [DurableField(1)] public T Value = default!; }
            """, BoundedHistory("Bounded", 1), BoundedHistory("Bounded", 2), BoundedHistory("Bounded", 3));
        AssertSchemaOnlyCompiles(run);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        StateModelRegistry models = BoundedHistoryModels(assembly);
        var environment = models.CreateEnvironment();
        Type domain = assembly.GetType("Model`1")!.MakeGenericType(typeof(int));
        Assert.Equal(4, environment.ResolveCurrentModel(domain).CurrentSchema.Version);
        for (int version = 1; version <= 4; version++) {
            DurableSchema schema = new(TypeExpr.Named("Bounded", TypeExpr.Builtin(TypeTag.Int32)), version,
                new DurableFieldInfo(1, TypeTag.Int32));
            Assert.Equal(schema, environment.ResolveReader(schema).Schema);
        }
    }

    [Theory]
    [InlineData("NotAnUpgrade", false)]
    [InlineData("upgradeStateV1ToV2", false)]
    [InlineData("UpgradeStateVToV1", false)]
    [InlineData("UpgradeStateV1", false)]
    [InlineData("UpgradeStateV0ToV1", false)]
    [InlineData("UpgradeStateV01ToV2", false)]
    [InlineData("UpgradeStateV1ToV02", false)]
    [InlineData("UpgradeStateV1ToV3", false)]
    [InlineData("UpgradeStateV1ToV2Extra", false)]
    [InlineData("UpgradeStateV1ToV2ToV3", false)]
    [InlineData("UpgradeStateV2147483647ToV2147483648", false)]
    [InlineData("UpgradeStateV2147483648ToV2147483649", false)]
    [InlineData("UpgradeStateV1ToV2", true)]
    [InlineData("UpgradeStateV2147483646ToV2147483647", true)]
    public void UpgradeDependencyOwnerNamesAreCanonicalWithoutScanningTheDeclaredVersionRange(string name, bool canonical) {
        GeneratorTestRun run = RunGenerator($$"""
            using Atelia.DurableGraph;
            [ValueUpgradeRuleSet] public sealed class Rules { }
            [DurableType("Bounded", int.MaxValue)] public partial class Model : IDurableObject {
                [DurableField(1)] public int Value;
                [UpgradeDependency("Slot", typeof(Rules), "Bounded", 1, "Bounded", 1)]
                public static void {{name}}(in int prior, out int next, UpgradeContext context) => next = prior;
            }
            """);
        Diagnostic[] wrongOwner = run.GeneratorDiagnostics.Where(diagnostic => diagnostic.Id == "DG0020" &&
            diagnostic.GetMessage().Contains("Value dependencies belong to exactly one")).ToArray();
        if (canonical) {
            Assert.Empty(wrongOwner);
            Assert.Single(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0014");
        } else {
            Assert.Single(wrongOwner);
        }
        Assert.DoesNotContain(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "CS8785");
        AssertMissingHistoryHasNoBodies(run);
    }

    [Fact]
    public void CanonicalImplicitUpgradeWithDependenciesKeepsItsActualUpgradeBehavior() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using States = Atelia.DurableGraph.Generated.Family_426F756E646564;
            [ValueUpgradeRuleSet(AllowKeepExact = true)] public sealed class Rules { }
            [DurableType("Bounded", 2)] public partial class Model : IDurableObject {
                [DurableField(1)] public int Value;
                [UpgradeDependency("Slot", typeof(Rules), "Bounded", 1, "Bounded", 1)]
                private static void UpgradeStateV1ToV2(in States.V1 prior, out States.V2 next, UpgradeContext context) =>
                    next = new(prior.Segment0Field1 + 1);
            }
            """, new InMemoryAdditionalText("bounded-v1.dgschema",
                GenericTemplateHistoryTests.History("Bounded", body: "// field:1|2\n")));
        AssertSchemaOnlyCompiles(run);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        var model = BoundedHistoryModels(assembly).CreateEnvironment().ResolveCurrentModel(assembly.GetType("Model")!);
        Type oldDto = assembly.GetType("Atelia.DurableGraph.Generated.Family_426F756E646564+V1")!;
        object prior = Activator.CreateInstance(oldDto, new object[] { 9 })!;
        ObjectStateRecord source = new(new(17), new DurableSchema("Bounded", 1, new DurableFieldInfo(1, TypeTag.Int32)), prior);
        Assert.Equal(10, StateModelField(model.Normalize(source), "Segment0Field1"));
        Assert.Equal(9, StateModelField(source, "Segment0Field1"));
    }

    private static AdditionalText BoundedHistory(string id, int version) => new InMemoryAdditionalText(
        id + "-v" + version.ToString(CultureInfo.InvariantCulture) + ".dgschema",
        GenericTemplateHistoryTests.History(id, version: version, arity: 1, body: "// field:1|17|p0\n"));

    private static StateModelRegistry BoundedHistoryModels(Assembly assembly) {
        StateModelRegistry models = new();
        assembly.GetType("Atelia.DurableGraph.Generated.DurableDefinitions")!.GetMethod("Register")!.Invoke(null, [models]);
        return models;
    }

    private static void AssertMissingHistoryHasNoBodies(GeneratorTestRun run) {
        Assert.DoesNotContain(run.GeneratedSources, source => source.HintName is
            "DurableGenericStates.g.cs" or "DurableStates.g.cs" or "DurableGraphSchemaExports.g.cs");
    }
}
