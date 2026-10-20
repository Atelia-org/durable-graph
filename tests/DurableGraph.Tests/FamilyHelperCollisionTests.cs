using System.Reflection;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Microsoft.CodeAnalysis;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Theory]
    [InlineData("class", "__DurableCapture")]
    [InlineData("class", "__DurableHydrate")]
    [InlineData("class", "__DurableCreateCurrent")]
    [InlineData("class", "__DurableCreateTyped")]
    [InlineData("struct", "__DurableCapture")]
    [InlineData("struct", "__DurableHydrate")]
    [InlineData("struct", "__DurableCreateCurrent")]
    [InlineData("struct", "__DurableCreateTyped")]
    [InlineData("struct", "__DurableProjection")]
    [InlineData("record class", "__DurableCapture")]
    [InlineData("record struct", "__DurableProjection")]
    [InlineData("class", "__DurableRead_Family_78_1")]
    [InlineData("struct", "__DurableWrite_Family_78_1")]
    public void ActualFamilyHelperCollisionsRejectCurrentBeforeCandidateAndExport(string kind, string name) {
        GeneratorTestRun run = RunGenerator($$"""
            using Atelia.DurableGraph;
            [DurableType("x", 1)] public partial {{kind}} Broken {{(kind.EndsWith("class", StringComparison.Ordinal) ? ": IDurableObject" : "")}} {
                [DurableField(1)] public int Value;
                public static int {{name}} => 0;
            }
            """, [], null, "true");
        Diagnostic collision = Assert.Single(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0020");
        Assert.Contains(name, collision.GetMessage());
        Assert.Empty(run.GeneratedSources);
        Assert.DoesNotContain(run.OutputCompilation.GetDiagnostics(), IsError);
    }

    [Theory]
    [InlineData("class", "[DurableField(1)] public readonly int Value;")]
    [InlineData("struct", "[DurableField(1)] public readonly int Value;")]
    [InlineData("record class", "[field: DurableField(1)] public int Value { get; set; }")]
    [InlineData("record struct", "[field: DurableField(1)] public int Value { get; set; }")]
    public void ActualReadonlyOrBackingAccessorCollisionIsRejected(string kind, string storage) {
        GeneratorTestRun run = RunGenerator($$"""
            using Atelia.DurableGraph;
            [DurableType("x", 1)] public partial {{kind}} Broken {{(kind.EndsWith("class", StringComparison.Ordinal) ? ": IDurableObject" : "")}} {
                {{storage}}
                public static int __DurableReadonly_Family_78_1 => 0;
            }
            """, [], null, "true");
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0020" &&
            diagnostic.GetMessage().Contains("__DurableReadonly_Family_78_1"));
        Assert.Empty(run.GeneratedSources);
    }

    [Theory]
    [InlineData("class", "__DurableProjection")]
    [InlineData("record class", "__DurableProjection")]
    [InlineData("class", "__DurableReadonly_Family_78_1")]
    [InlineData("class", "__DurableCheckImmutableLeafShape")]
    [InlineData("struct", "__DurableCheckImmutableLeafShape")]
    public void FamilyDoesNotReserveHelpersItDoesNotEmitInTheDomainScope(string kind, string name) {
        GeneratorTestRun run = RunGenerator($$"""
            using Atelia.DurableGraph;
            [DurableType("x", 1)] public partial {{kind}} Healthy {{(kind.EndsWith("class", StringComparison.Ordinal) ? ": IDurableObject" : "")}} {
                [DurableField(1)] public int Value;
                public static int {{name}} => 0;
            }
            """, [], null, "true");
        AssertSchemaOnlyCompiles(run);
        EmitAndLoad(run.OutputCompilation);
    }

    [Theory]
    [InlineData("class", "__DurableCapture")]
    [InlineData("struct", "__DurableProjection")]
    public void FamilyTypeNamesCannotCollideWithTheirActualHelper(string kind, string name) {
        GeneratorTestRun run = RunGenerator($$"""
            using Atelia.DurableGraph;
            [DurableType("x", 1)] public partial {{kind}} {{name}} {{(kind == "class" ? ": IDurableObject" : "")}} { }
            """, [], null, "true");
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0020" && diagnostic.GetMessage().Contains(name));
        Assert.Empty(run.GeneratedSources);
    }

    [Fact]
    public void FamilyImmutableLeafShapeHelperIsReservedOnlyForActualCandidates() {
        GeneratorTestRun rejected = RunGenerator("""
            using Atelia.DurableGraph;
            [DurableType("x", 1)] public partial class Broken : IDurableObject {
                [DurableField(1)] public readonly int Value;
                public static int __DurableCheckImmutableLeafShape => 0;
            }
            """, [], null, "true");
        Assert.Contains(rejected.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0020" &&
            diagnostic.GetMessage().Contains("__DurableCheckImmutableLeafShape"));
        Assert.Empty(rejected.GeneratedSources);
        GeneratorTestRun generic = RunGenerator("""
            using Atelia.DurableGraph;
            [DurableType("x", 1)] public partial class Healthy<T> : IDurableObject {
                [DurableField(1)] public readonly int Value;
                public static int __DurableCheckImmutableLeafShape => 0;
            }
            """);
        AssertSchemaOnlyCompiles(generic);
    }

    [Fact]
    public void LegacyDomainScopeKeepsAllUnusedFamilyHelperNamesAvailable() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            [DurableType("x", 1)] public partial class Healthy : IDurableObject {
                [DurableField(1)] public readonly int Value;
                public static int __DurableCapture => 0;
                public static int __DurableHydrate => 0;
                public static int __DurableCreateCurrent => 0;
                public static int __DurableCreateTyped => 0;
                public static int __DurableProjection => 0;
                public static int __DurableRead_Family_78_1 => 0;
                public static int __DurableWrite_Family_78_1 => 0;
                public static int __DurableReadonly_Family_78_1 => 0;
                public static int __DurableCheckImmutableLeafShape => 0;
            }
            """);
        AssertSchemaOnlyCompiles(run);
        GeneratedSource(run, "DurableStates.g.cs");
        Assert.DoesNotContain(run.GeneratedSources, source => source.HintName == "DurableGenericStates.g.cs");
    }

    [Theory]
    [InlineData("force")]
    [InlineData("generic")]
    [InlineData("sibling-generic")]
    [InlineData("record")]
    [InlineData("enum")]
    [InlineData("nullable")]
    [InlineData("history")]
    [InlineData("value-rule")]
    [InlineData("upgrade")]
    public void FamilyRouteStaysSelectedAfterItsCollidingTriggerIsRemoved(string route) {
        string sibling = route switch {
            "sibling-generic" => "[DurableType(\"sibling\", 1)] public partial class Sibling<T> : IDurableObject { }",
            "record" => "[DurableType(\"sibling\", 1)] public partial record struct Sibling;",
            "enum" => "[DurableType(\"sibling\", 1)] public enum Sibling { Zero }",
            "value-rule" => "[ValueUpgradeRuleSet(AllowKeepExact = true)] public sealed class Rules { }",
            "upgrade" => """
                public static class Upgrades {
                    [DurableUpgrade(typeof(Good), 1)]
                    public static void Upgrade(in Atelia.DurableGraph.Generated.Family_676F6F64.V1 prior,
                        out Atelia.DurableGraph.Generated.Family_676F6F64.V2 next, UpgradeContext context) => next = new(prior.Segment0Field1);
                }
                """,
            _ => "",
        };
        AdditionalText[] history = route switch {
            "history" => [new InMemoryAdditionalText("retained.dgschema",
                GenericTemplateHistoryTests.History("retained", kind: 2, body: "// field:1|2\n"))],
            "upgrade" => [FamilyCollisionHistory("good")],
            _ => [],
        };
        GeneratorTestRun run = RunGenerator($$"""
            using Atelia.DurableGraph;
            [DurableType("bad", 1)] public partial class Broken{{(route == "generic" ? "<T>" : "")}} : IDurableObject {
                [DurableField(1)] public {{(route == "nullable" ? "int?" : "int")}} Value;
                public static int __DurableCapture => 0;
            }
            [DurableType("good", {{(route == "upgrade" ? 2 : 1)}})] public partial class Good : IDurableObject {
                [DurableField(1)] public int Value;
            }
            {{sibling}}
            """, history, null, route == "force" ? "true" : null);
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0020" &&
            diagnostic.GetMessage().Contains("__DurableCapture"));
        Assert.DoesNotContain(run.OutputCompilation.GetDiagnostics(), IsError);
        string candidate = GeneratedSource(run, "DurableGraphSchemaHistoryCandidates.g.cs");
        Assert.DoesNotContain("// schema-id-base64:YmFk\n", candidate);
        Assert.Contains("// schema-id-base64:Z29vZA==\n", candidate);
        GeneratedSource(run, "DurableGenericStates.g.cs");
        Assert.DoesNotContain(run.GeneratedSources, source => source.HintName == "DurableStates.g.cs");
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        var environment = FamilyCollisionModels(assembly).CreateEnvironment();
        Type good = assembly.GetType("Good")!;
        Assert.Equal(good, environment.ResolveCurrentModel(good).DomainType);
    }

    [Fact]
    public void SuppressedFamilyCollisionsRemoveCurrentBaseAndInlineDependentsButKeepIndependentModels() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            [DurableType("bad-base", 1)] public partial class BadBase : IDurableObject {
                public static int __DurableCapture => 0;
            }
            [DurableType("derived", 1)] public partial class Derived : BadBase { }
            [DurableType("bad-inline", 1)] public partial struct BadInline {
                public static int __DurableCapture => 0;
            }
            [DurableType("owner", 1)] public partial class Owner : IDurableObject {
                [DurableField(1)] public BadInline Value;
            }
            [DurableType("good", 1)] public partial class Good : IDurableObject { [DurableField(1)] public int Value; }
            """, [], null, "true", new Dictionary<string, ReportDiagnostic> {
                ["DG0020"] = ReportDiagnostic.Suppress, ["DG0019"] = ReportDiagnostic.Suppress,
            });
        Assert.DoesNotContain(run.OutputCompilation.GetDiagnostics(), IsError);
        string candidate = GeneratedSource(run, "DurableGraphSchemaHistoryCandidates.g.cs");
        Assert.Equal(1, CountOccurrences(candidate, "// schema-begin"));
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        foreach (string name in new[] { "BadBase", "Derived", "BadInline", "Owner" }) {
            Assert.Null(assembly.GetType(name)!.GetMethod("__DurableCreateCurrent", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
        }
        Type good = assembly.GetType("Good")!;
        Assert.Equal(good, FamilyCollisionModels(assembly).CreateEnvironment().ResolveCurrentModel(good).DomainType);
    }

    [Fact]
    public void OnlyCollidingCurrentWithSuppressedDiagnosticStillKeepsItsAcceptedHistoricalReader() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            [DurableType("bad", 2)] public partial class Broken : IDurableObject {
                [DurableField(1)] public long CurrentOnly;
                public static int __DurableCapture => 0;
            }
            """, [FamilyCollisionHistory("bad")], null, "true",
            new Dictionary<string, ReportDiagnostic> { ["DG0020"] = ReportDiagnostic.Suppress });
        Assert.DoesNotContain(run.OutputCompilation.GetDiagnostics(), IsError);
        Assert.DoesNotContain(run.GeneratedSources, source => source.HintName == "DurableGraphSchemaHistoryCandidates.g.cs");
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        Type family = assembly.GetType("Atelia.DurableGraph.Generated.Family_626164")!;
        StateDefinitionBinding definition = (StateDefinitionBinding)family.GetField("Definition")!.GetValue(null)!;
        Assert.Null(definition.DomainTypeDefinition);
        Assert.Null(definition.CurrentModelFactory);
        Assert.NotNull(definition.HistoricalReaderFactory);
        Assert.Equal(1, Assert.Single(definition.Templates).Version);
        Assert.Null(family.GetNestedType("V2")); // The rejected current shape is not historical input.
        var environment = FamilyCollisionModels(assembly).CreateEnvironment();
        DurableSchema oldSchema = new("bad", 1, new DurableFieldInfo(1, TypeTag.Int32));
        Assert.Equal(oldSchema, environment.ResolveReader(oldSchema).Schema);
        Assert.All(assembly.GetCustomAttributes<DurableSchemaExportAttribute>(), export => Assert.Equal(1, export.SchemaVersion));
    }

    [Fact]
    public void ValueUpgradeRegistrationCannotKeepACollidingCurrentInlineEndpointAlive() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            [ValueUpgradeRuleSet] public sealed class Rules { }
            [DurableType("bad", 1)] public partial struct Broken {
                [DurableField(1)] public int Value;
                public static int __DurableCapture => 0;
            }
            public static class Upgrades {
                [DurableValueUpgrade(typeof(Rules), "bad", 1, 1)]
                public static void Convert(in int prior, out int next, UpgradeContext context) => next = prior;
            }
            """, [], null, null, new Dictionary<string, ReportDiagnostic> { ["DG0020"] = ReportDiagnostic.Suppress });
        Assert.Empty(run.GeneratedSources);
        Assert.DoesNotContain(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "CS8785");
    }

    [Theory]
    [InlineData("class", "__DurableCapture")]
    [InlineData("class", "@__DurableCapture")]
    [InlineData("struct", "__DurableProjection")]
    public void DomainTypeParametersCannotCollideWithActualFamilyHelpers(string kind, string parameter) {
        GeneratorTestRun run = RunGenerator($$"""
            using Atelia.DurableGraph;
            [DurableType("x", 1)] public partial {{kind}} Broken<{{parameter}}> {{(kind == "class" ? ": IDurableObject" : "")}} { }
            """);
        Diagnostic collision = Assert.Single(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0020");
        Assert.Contains(parameter.TrimStart('@'), collision.GetMessage());
        Assert.Equal(parameter, collision.Location.SourceTree!.GetText().ToString(collision.Location.SourceSpan));
        Assert.Empty(run.GeneratedSources);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void ImplicitUpgradeAdapterNameIsReservedOnlyWhenEmittedIntoTheFamilyDomain(bool family, bool implicitUpgrade) {
        string prior = family ? "Atelia.DurableGraph.Generated.Family_78.V1" : "__DurableState.V1";
        string next = family ? "Atelia.DurableGraph.Generated.Family_78.V2" : "__DurableState.V2";
        string method = implicitUpgrade
            ? $"private static void UpgradeStateV1ToV2(in {prior} prior, out {next} next) => next = new(prior.Segment0Field1);"
            : "";
        GeneratorTestRun run = RunGenerator($$"""
            using Atelia.DurableGraph;
            [DurableType("x", 2)] public partial class Model : IDurableObject {
                [DurableField(1)] public int Value;
                public static int __DurableUpgradeAdapter1 => 0;
                {{method}}
            }
            """, [FamilyCollisionHistory("x")], null, family ? "true" : null);
        if (family && implicitUpgrade) {
            Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0020" &&
                diagnostic.GetMessage().Contains("__DurableUpgradeAdapter1"));
            Assert.DoesNotContain(run.GeneratedSources, source => source.HintName == "DurableGraphSchemaHistoryCandidates.g.cs");
            Assert.DoesNotContain("struct V2", GeneratedSource(run, "DurableGenericStates.g.cs"));
        } else {
            AssertSchemaOnlyCompiles(run);
            EmitAndLoad(run.OutputCompilation);
        }
    }

    [Fact]
    public void ImportedExactDependencySelectsFamilyHelperValidationBeforePublishingCandidates() {
        var library = EmitCrossAssemblyReference(RunCrossAssemblyGenerator("""
            using Atelia.DurableGraph;
            namespace Remote;
            [DurableType("remote-point", 1)] public partial struct Point { [DurableField(1)] public int X; }
            """, forceDefinitions: "true"));
        GeneratorTestRun run = RunCrossAssemblyGenerator("""
            using Atelia.DurableGraph;
            [DurableType("bad", 1)] public partial class Broken : IDurableObject {
                [DurableField(1)] public Remote.Point Position;
                public static int __DurableCapture => 0;
            }
            [DurableType("good", 1)] public partial class Good : IDurableObject { [DurableField(1)] public int Value; }
            """, [library.Reference]);
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0020");
        Assert.DoesNotContain(run.OutputCompilation.GetDiagnostics(), IsError);
        string candidate = GeneratedSource(run, "DurableGraphSchemaHistoryCandidates.g.cs");
        Assert.DoesNotContain("// schema-id-base64:YmFk\n", candidate);
        Assert.Contains("// references-sha256:", candidate);
        string exports = GeneratedSource(run, "DurableGraphSchemaExports.g.cs");
        Assert.Contains("\"good\"", exports);
        Assert.DoesNotContain("\"bad\"", exports);
        Assert.DoesNotContain("\"remote-point\"", exports);
        GeneratedSource(run, "DurableGenericStates.g.cs");
    }

    [Fact]
    public void AcceptedHistoricalInlineEndpointSurvivesRejectedCurrentAndStillSupportsValueRules() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using States = Atelia.DurableGraph.Generated.Family_626164;
            [ValueUpgradeRuleSet] public sealed class Rules { }
            [DurableType("bad", 1)] public partial struct Broken {
                [DurableField(1)] public int Value;
                public static int __DurableCapture => 0;
            }
            public static class Upgrades {
                [DurableValueUpgrade(typeof(Rules), "bad", 1, 1)]
                public static void Convert(in States.V1 prior, out States.V1 next, UpgradeContext context) => next = prior;
            }
            """, [new InMemoryAdditionalText("bad.dgschema",
                GenericTemplateHistoryTests.History("bad", kind: 2, body: "// field:1|2\n"))], null, null,
            new Dictionary<string, ReportDiagnostic> { ["DG0020"] = ReportDiagnostic.Suppress });
        Assert.DoesNotContain(run.OutputCompilation.GetDiagnostics(), IsError);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        var environment = FamilyCollisionModels(assembly).CreateEnvironment();
        Assert.Single(environment.GetValueUpgradeRuleSet(assembly.GetType("Rules")!).Providers);
        StateDefinitionBinding definition = (StateDefinitionBinding)assembly.GetType("Atelia.DurableGraph.Generated.Family_626164")!
            .GetField("Definition")!.GetValue(null)!;
        Assert.Null(definition.DomainTypeDefinition);
        Assert.Null(definition.CurrentValueFactory);
        Assert.NotNull(definition.HistoricalValueFactory);
    }

    private static AdditionalText FamilyCollisionHistory(string id) => new InMemoryAdditionalText(id + ".dgschema",
        GenericTemplateHistoryTests.History(id, body: "// field:1|2\n"));

    private static StateModelRegistry FamilyCollisionModels(Assembly assembly) {
        StateModelRegistry models = new();
        assembly.GetType("Atelia.DurableGraph.Generated.DurableDefinitions")!.GetMethod("Register")!.Invoke(null, [models]);
        return models;
    }
}
