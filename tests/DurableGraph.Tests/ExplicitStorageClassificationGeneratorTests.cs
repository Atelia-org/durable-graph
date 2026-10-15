using System.Reflection;
using System.Text;
using Atelia.DurableGraph.Runtime;
using Microsoft.CodeAnalysis;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitStorageClassificationRejectsH01WithoutLosingIndependentEmitterControl(bool family) {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            [DurableType("Broken", 1)]
            public partial class Broken : IDurableObject {
                [field: DurableField(1)] public int Number { get; set; }
                [DurableField(2)] public int Control;
            }
            [DurableType("Good", 1)]
            public partial class Good : IDurableObject {
                [DurableField(1)] public int Number;
            }
            """, [], null, family ? "true" : null);

        AssertExplicitClassificationError(run, "DG0024", "DurableField(1)");
        AssertIndependentClassificationControl(run, family);
        AssertNoCurrentClassificationModel(run, "Broken");
    }

    public static TheoryData<string> UnsupportedOrdinaryBackingDeclarations() => new() {
        "public partial class Broken : IDurableObject { [field: DurableField(1)] public int Number { get; set; } }",
        "public partial class Broken : IDurableObject { [field: DurableField(1)] public int Number { get; } }",
        "public partial class Broken : IDurableObject { [field: DurableField(1)] public int Number { get => field + 100; set => field = value; } }",
        "public partial struct Broken { [field: DurableField(1)] public int Number { get; set; } }",
        "public partial struct Broken { [field: DurableField(1)] public int Number { get; } }",
        "public partial struct Broken { [field: DurableField(1)] public int Number { get => field + 100; set => field = value; } }",
        "public partial class Broken : IDurableObject { [field: DurableField(1)] public event System.Action? Changed; }",
    };

    [Theory]
    [MemberData(nameof(UnsupportedOrdinaryBackingDeclarations))]
    public void ExplicitStorageClassificationRejectsUnsupportedRealBacking(string declaration) {
        GeneratorTestRun run = RunGenerator("using Atelia.DurableGraph; [DurableType(\"Broken\", 1)] " + declaration);
        AssertExplicitClassificationError(run, "DG0024", "DurableField(1)");
        AssertNoCurrentClassificationModel(run, "Broken");
    }

    public static TheoryData<string, string> IgnoredOrdinaryClassificationDeclarations() {
        TheoryData<string, string> cases = new();
        foreach (string attribute in new[] { "DurableField(1)", "Transient" }) {
            cases.Add($"public partial class Broken : IDurableObject {{ [field: {attribute}] public int Computed => 1; }}", attribute);
            cases.Add($"public partial class Broken([field: {attribute}] int seed) : IDurableObject {{ public int Computed => seed; }}", attribute);
            cases.Add($"public partial class Broken : IDurableObject {{ [field: {attribute}] public event System.Action? Changed {{ add {{ }} remove {{ }} }} }}", attribute);
            cases.Add($"public abstract partial class Broken : IDurableObject {{ [field: {attribute}] public abstract event System.Action? Changed; }}", attribute);
            cases.Add($"public partial class Broken : IDurableObject {{ [field: {attribute}] public extern event System.Action? Changed; }}", attribute);
            cases.Add($"public partial class Broken : IDurableObject {{ [field: {attribute}] public partial event System.Action? Changed; public partial event System.Action? Changed {{ add {{ }} remove {{ }} }} }}", attribute);
        }
        cases.Add("public partial class Broken : IDurableObject { public partial event System.Action? Changed; [field: Transient] public partial event System.Action? Changed { add { } remove { } } }", "Transient");
        return cases;
    }

    [Theory]
    [MemberData(nameof(IgnoredOrdinaryClassificationDeclarations))]
    public void ExplicitStorageClassificationRejectsSemanticAttributeWithoutRealTarget(string declaration, string attribute) {
        GeneratorTestRun run = RunGenerator("using Atelia.DurableGraph; [DurableType(\"Broken\", 1)] " + declaration);
        AssertExplicitClassificationError(run, "DG0024", attribute);
        AssertNoCurrentClassificationModel(run, "Broken");
    }

    [Theory]
    [InlineData("[DurableField(1)] public static int Number;", "DG0009")]
    [InlineData("[Transient] public static int Number;", "DG0009")]
    [InlineData("[DurableField(1), Transient] public static int Number;", "DG0009")]
    [InlineData("[DurableField(1), Transient] public int Number;", "DG0004")]
    [InlineData("[field: DurableField(1)] public static int Number { get; set; }", "DG0009")]
    [InlineData("[field: Transient] public static int Number { get; set; }", "DG0009")]
    [InlineData("[field: DurableField(1), Transient] public static int Number { get; set; }", "DG0009")]
    [InlineData("[field: DurableField(1), Transient] public int Number { get; set; }", "DG0004")]
    [InlineData("[field: DurableField(1)] public static event System.Action? Changed;", "DG0009")]
    [InlineData("[field: Transient] public static event System.Action? Changed;", "DG0009")]
    [InlineData("[field: DurableField(1), Transient] public static event System.Action? Changed;", "DG0009")]
    [InlineData("[field: DurableField(1), Transient] public event System.Action? Changed;", "DG0004")]
    public void ExplicitStorageClassificationPreservesStaticAndConflictDiagnosticPriority(string member, string expectedId) {
        GeneratorTestRun run = RunGenerator("using Atelia.DurableGraph; [DurableType(\"Broken\", 1)] public partial class Broken : IDurableObject { " + member + " }");
        Diagnostic error = Assert.Single(run.GeneratorDiagnostics, IsError);
        Assert.Equal(expectedId, error.Id);
        Assert.DoesNotContain(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0024");
        AssertNoCurrentClassificationModel(run, "Broken");
    }

    [Theory]
    [InlineData("[field: Transient] public int Scratch { get; set; }")]
    [InlineData("[field: Transient] public int Scratch { get; }")]
    [InlineData("[field: Transient] public int Scratch { get => field; set => field = value; }")]
    [InlineData("[field: Transient] public event System.Action? Changed;")]
    [InlineData("[field: Transient] public virtual event System.Action? Changed;")]
    [InlineData("[field: Transient] public event System.Action? First, Second;")]
    [InlineData("public int Scratch { get; set; } public event System.Action? Changed;")]
    [InlineData("public int Wrapper { get => Number; set => Number = value; }")]
    public void ExplicitStorageClassificationAcceptsOrdinaryExclusionsWithoutNewSlotsOrFamilyRoute(string member) {
        GeneratorTestRun run = RunGenerator("using Atelia.DurableGraph; [DurableType(\"Good\", 1)] public partial class Good : IDurableObject { [DurableField(1)] public int Number; " + member + " }");
        AssertSchemaOnlyCompiles(run);
        Assert.Contains(run.GeneratedSources, source => source.HintName == "DurableStates.g.cs");
        Assert.DoesNotContain(run.GeneratedSources, source => source.HintName == "DurableGenericStates.g.cs");
        string candidate = GeneratedSource(run, "DurableGraphSchemaHistoryCandidates.g.cs");
        Assert.Equal(1, CountOccurrences(candidate, "// field:"));
        Type type = EmitAndLoad(run.OutputCompilation).GetType("Good")!;
        Assert.Single(ReadSchemaOnly(type, 1).Fields);
    }

    [Fact]
    public void ExplicitStorageClassificationKeepsUnmarkedPrimaryCapturePolicy() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            [DurableType("Good", 1)]
            public partial class Good(int seed) : IDurableObject {
                [DurableField(1)] public int Number;
                public int Captured => seed;
            }
            """);
        AssertSchemaOnlyCompiles(run);
        Assert.Equal(1, CountOccurrences(GeneratedSource(run, "DurableGraphSchemaHistoryCandidates.g.cs"), "// field:"));
    }

    [Theory]
    [InlineData("using Classification = Atelia.DurableGraph.DurableFieldAttribute;", "Classification(1)")]
    [InlineData("", "global::Atelia.DurableGraph.DurableFieldAttribute(1)")]
    public void ExplicitStorageClassificationResolvesAliasesAndFullMetadataNames(string imports, string attribute) {
        GeneratorTestRun run = RunGenerator(imports + "\nusing Atelia.DurableGraph; [DurableType(\"Broken\", 1)] public partial class Broken : IDurableObject { [field: " + attribute + "] public int Number { get; set; } }");
        AssertExplicitClassificationError(run, "DG0024", attribute);
    }

    [Fact]
    public void ExplicitStorageClassificationDoesNotConfuseUnrelatedAliasesOrNestedDeclarationsWithOwner() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using DurableField = LocalClassification;
            [System.AttributeUsage(System.AttributeTargets.Field)]
            public sealed class LocalClassification : System.Attribute { public LocalClassification(int id) { } }
            [DurableType("Good", 1)]
            public partial class Good : IDurableObject {
                [global::Atelia.DurableGraph.DurableField(1)] public int Number;
                [field: @DurableField(2)] public int Scratch { get; set; }
                public class Nested {
                    [field: global::Atelia.DurableGraph.DurableField(3)] public int Local { get; set; }
                }
            }
            """);
        AssertSchemaOnlyCompiles(run);
        Assert.Equal(1, CountOccurrences(GeneratedSource(run, "DurableGraphSchemaHistoryCandidates.g.cs"), "// field:"));
    }

    [Fact]
    public void ExplicitStorageClassificationDoesNotGrantQualificationToAmbiguousAttributeAlias() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using DurableField = LocalClassification;
            [System.AttributeUsage(System.AttributeTargets.Field)]
            public sealed class LocalClassification : System.Attribute { public LocalClassification(int id) { } }
            [DurableType("Broken", 1)]
            public partial class Broken : IDurableObject {
                [field: DurableField(1)] public int Number { get; set; }
            }
            """);
        Assert.Contains(run.OutputCompilation.GetDiagnostics(), diagnostic => diagnostic.Id == "CS1614");
        AssertExplicitClassificationError(run, "DG0024", "DurableField(1)");
        AssertNoCurrentClassificationModel(run, "Broken");
    }

    [Fact]
    public void ExplicitStorageClassificationScansAllPartialDeclarationsOfGenericOwner() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            [DurableType("Broken", 1)]
            public partial class Broken<T> : IDurableObject { [DurableField(1)] public T Number; }
            public partial class Broken<T> { [field: Transient] public int Computed => 1; }
            [DurableType("Good", 1)]
            public partial class Good : IDurableObject { [DurableField(1)] public int Number; }
            """);
        AssertExplicitClassificationError(run, "DG0024", "Transient");
        AssertNoCurrentClassificationModel(run, "Broken");
    }

    [Fact]
    public void ExplicitStorageClassificationAttributesStayAtTheirDeclaringInheritanceLayer() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            [DurableType("Base", 1)]
            public partial class Base : IDurableObject {
                [DurableField(1)] public int Number;
                [field: Transient] public int Scratch { get; set; }
            }
            [DurableType("Broken", 1)]
            public partial class Broken : Base {
                [field: DurableField(1)] public int Local { get; set; }
            }
            """);
        AssertExplicitClassificationError(run, "DG0024", "DurableField(1)");
        AssertNoCurrentClassificationModel(run, "Broken");
        Assert.Contains("global::Base", GeneratedSource(run, "DurableStates.g.cs"));
        string candidate = GeneratedSource(run, "DurableGraphSchemaHistoryCandidates.g.cs");
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("Base")), candidate);
    }

    [Fact]
    public void ExplicitStorageClassificationSuppressionCannotRestoreCurrentQualificationAndRetainsHistory() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            [DurableType("Broken", 1)]
            public partial class Broken : IDurableObject {
                [field: DurableField(1)] public int Number { get; set; }
            }
            [DurableType("Good", 1)]
            public partial class Good : IDurableObject { [DurableField(1)] public int Number; }
            """,
            [NominalValidationHistory("broken-v1.dgschema", "Broken", fields: "// field:1|2\n")],
            null, "true", new Dictionary<string, ReportDiagnostic> { ["DG0024"] = ReportDiagnostic.Suppress });

        Assert.DoesNotContain(run.GeneratorDiagnostics, diagnostic => IsError(diagnostic) && !diagnostic.IsSuppressed);
        Assert.DoesNotContain(run.OutputCompilation.GetDiagnostics(), IsError);
        AssertIndependentClassificationControl(run, family: true);
        AssertNoCurrentClassificationModel(run, "Broken");
        string familySource = GeneratedSource(run, "DurableGenericStates.g.cs");
        Assert.Contains("public static class Family_42726F6B656E", familySource);
        Assert.Contains("struct V1", familySource);
        Assert.Contains("struct BodyV1", familySource);
        // A valid ordinary CLR declaration can still emit after the DG diagnostic is suppressed.
        // This does not grant it a generated current model or remove retained historical bodies.
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        Type broken = assembly.GetType("Broken")!;
        Assert.Null(broken.GetNestedType("__DurableProjection", BindingFlags.Public | BindingFlags.NonPublic));
        Assert.Null(broken.GetNestedType("__DurableState", BindingFlags.Public | BindingFlags.NonPublic));
        Assert.Null(broken.GetProperty("Schema", BindingFlags.Public | BindingFlags.Static));
        StateDefinitionBinding retained = (StateDefinitionBinding)assembly
            .GetType("Atelia.DurableGraph.Generated.Family_42726F6B656E")!
            .GetField("Definition")!.GetValue(null)!;
        Assert.Null(retained.DomainTypeDefinition);
        Assert.Null(retained.CurrentModelFactory);
        Assert.NotNull(retained.HistoricalReaderFactory);
    }

    [Fact]
    public void ExplicitStorageClassificationTransientBackingIsNeverReadOrWrittenByCaptureAndHydrate() {
        GeneratorTestRun run = RunGenerator("""
            using System.Collections.Generic;
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Runtime;
            [DurableType("Good", 1)]
            public partial class Good : IDurableObject {
                [DurableField(1)] public int Number;
                public static int Getters, Setters;
                [field: Transient] public int Scratch {
                    get { Getters++; return field; }
                    set { Setters++; field = value; }
                }
            }
            public static class Host {
                public static bool Probe() {
                    var value = new Good { Number = 42, Scratch = 9 };
                    Good.Getters = Good.Setters = 0;
                    var first = Good.__DurableState.Capture(value);
                    value.Scratch = 77;
                    Good.Setters = 0;
                    var second = Good.__DurableState.Capture(value);
                    if (Good.__DurableState.PrepareDeltaBody(in first, in second).HasChanges) return false;
                    var restored = Good.__DurableState.Allocate();
                    Good.__DurableState.Hydrate(restored, in first, new ObjectReadTable(new Dictionary<ObjectId, object>()));
                    if (restored.Number != 42 || Good.Getters != 0 || Good.Setters != 0) return false;
                    return restored.Scratch == 0 && Good.Getters == 1 && Good.Setters == 0;
                }
            }
            """);
        AssertSchemaOnlyCompiles(run);
        string states = GeneratedSource(run, "DurableStates.g.cs");
        Assert.DoesNotContain(".Scratch", states);
        Assert.DoesNotContain(run.GeneratedSources, source => source.HintName == "DurableGenericStates.g.cs");
        Assert.Equal(1, CountOccurrences(GeneratedSource(run, "DurableGraphSchemaHistoryCandidates.g.cs"), "// field:"));
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        Assert.Single(ReadSchemaOnly(assembly.GetType("Good")!, 1).Fields);
        Assert.True(assembly.GetType("Host")!.GetMethod("Probe")!.CreateDelegate<Func<bool>>()());
    }

    private static void AssertExplicitClassificationError(GeneratorTestRun run, string id, string attribute) {
        Diagnostic error = Assert.Single(run.GeneratorDiagnostics, IsError);
        Assert.Equal(id, error.Id);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.True(error.Location.IsInSource);
        Assert.Equal(attribute, error.Location.SourceTree!.GetText().ToString(error.Location.SourceSpan));
        Assert.DoesNotContain(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "CS8785");
    }

    private static void AssertIndependentClassificationControl(GeneratorTestRun run, bool family) {
        string hint = family ? "DurableGenericStates.g.cs" : "DurableStates.g.cs";
        Assert.Contains("global::Good", GeneratedSource(run, hint));
        Assert.DoesNotContain(run.GeneratedSources,
            source => source.HintName == (family ? "DurableStates.g.cs" : "DurableGenericStates.g.cs"));
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("Good")),
            GeneratedSource(run, "DurableGraphSchemaHistoryCandidates.g.cs"));
    }

    private static void AssertNoCurrentClassificationModel(GeneratorTestRun run, string schemaId) {
        string allGenerated = string.Join("\n", run.GeneratedSources.Select(source => source.SourceText.ToString()));
        Assert.DoesNotContain("global::" + schemaId, allGenerated);
        Assert.DoesNotContain("partial class " + schemaId, allGenerated);
        Assert.DoesNotContain("partial struct " + schemaId, allGenerated);
        foreach (GeneratedSourceResult manifest in run.GeneratedSources.Where(
            source => source.HintName == "DurableGraphSchemaHistoryCandidates.g.cs")) {
            Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(schemaId)), manifest.SourceText.ToString());
        }
    }
}
