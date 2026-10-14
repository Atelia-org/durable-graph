using System.Text;
using Microsoft.CodeAnalysis;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    private const string NominalValidationWorldSource = """
        using Atelia.DurableGraph;
        [DurableType("World", 1)]
        public partial class World : IDurableObject {
            [DurableField(1)] public int Value;
        }
        """;

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void GeneratorKnownNominalArityChecksDirectAndNestedPatterns(bool nested, bool targetFirst, bool matches) {
        // History is sorted by definition ID, so these IDs control which side establishes the arity.
        string targetId = targetFirst ? "A.Target" : "Z.Target";
        string ownerId = targetFirst ? "Z.Owner" : "A.Owner";
        string targetPattern = NominalValidationPattern(targetId, matches ? "b2" : "");
        string pattern = nested ? NominalValidationPattern("Outer", targetPattern) : targetPattern;
        GeneratorTestRun run = RunGenerator(NominalValidationWorldSource,
            NominalValidationHistory("owner.dgschema", ownerId, fields: $"// field:1|15|{pattern}\n"),
            NominalValidationHistory("target.dgschema", targetId, arity: 1, fields: "// field:1|17|p0\n"));

        if (matches) {
            AssertSchemaOnlyCompiles(run);
            Assert.Contains(run.GeneratedSources, source => source.HintName == "DurableGenericStates.g.cs");
        } else {
            Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0019" && diagnostic.Severity == DiagnosticSeverity.Error);
            Assert.Empty(run.GeneratedSources);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratorSingleHistoricalFamilyConflictHasOneNominalDiagnostic(bool changesKind) {
        // No imports: the invalid accepted history is checked in one phase before current material is emitted.
        GeneratorTestRun run = RunGenerator(NominalValidationWorldSource,
            NominalValidationHistory("first-v1.dgschema", "First"),
            NominalValidationHistory("first-v2.dgschema", "First", version: 2,
                kind: changesKind ? 2 : 1, arity: changesKind ? 0 : 1));

        Diagnostic error = Assert.Single(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0019");
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("Schema family has conflicting kind or arity", error.GetMessage());
        Assert.Empty(run.GeneratedSources);
    }

    [Fact]
    public void GeneratorIndependentHistoricalFamilyConflictsAreBothReported() {
        GeneratorTestRun run = RunGenerator(NominalValidationWorldSource,
            NominalValidationHistory("first-v1.dgschema", "First"),
            NominalValidationHistory("first-v2.dgschema", "First", version: 2, arity: 1),
            NominalValidationHistory("second-v1.dgschema", "Second"),
            NominalValidationHistory("second-v2.dgschema", "Second", version: 2, kind: 2));

        Diagnostic[] errors = run.GeneratorDiagnostics.Where(diagnostic => diagnostic.Id == "DG0019").ToArray();
        Assert.Equal(2, errors.Length);
        Assert.Contains(errors, diagnostic => diagnostic.GetMessage().Contains("'First'"));
        Assert.Contains(errors, diagnostic => diagnostic.GetMessage().Contains("'Second'"));
        Assert.Empty(run.GeneratedSources);
    }

    [Fact]
    public void GeneratorNominalArityRetainsFirstValueAfterConflictingVersions() {
        GeneratorTestRun run = RunGenerator(NominalValidationWorldSource,
            NominalValidationHistory("first-v1.dgschema", "First"),
            NominalValidationHistory("first-v2.dgschema", "First", version: 2, arity: 1),
            NominalValidationHistory("first-v3.dgschema", "First", version: 3, arity: 1));

        Assert.Equal(2, run.GeneratorDiagnostics.Count(diagnostic => diagnostic.Id == "DG0019"));
        Assert.Empty(run.GeneratedSources);
    }

    [Fact]
    public void GeneratorIndependentNominalFieldConflictsAreBothReported() {
        GeneratorTestRun run = RunGenerator(NominalValidationWorldSource,
            NominalValidationHistory("first.dgschema", "A.First", arity: 1),
            NominalValidationHistory("second.dgschema", "B.Second", arity: 1),
            NominalValidationHistory("owner.dgschema", "Z.Owner", fields:
                $"// field:1|15|{NominalValidationPattern("A.First")}\n// field:2|15|{NominalValidationPattern("B.Second")}\n"));

        Diagnostic[] errors = run.GeneratorDiagnostics.Where(diagnostic => diagnostic.Id == "DG0019").ToArray();
        Assert.Equal(2, errors.Length);
        Assert.All(errors, diagnostic => Assert.Contains("field type pattern has conflicting kind or arity", diagnostic.GetMessage()));
        Assert.Empty(run.GeneratedSources);
    }

    [Fact]
    public void GeneratorNominalConflictKeepsIndependentInlineCycleDiagnostic() {
        GeneratorTestRun run = RunGenerator(NominalValidationWorldSource,
            NominalValidationHistory("nominal.dgschema", "A.Nominal", fields:
                $"// field:1|15|{NominalValidationPattern("Ghost", "b2")}\n// field:2|15|{NominalValidationPattern("Ghost", "b2,b2")}\n"),
            NominalValidationHistory("left.dgschema", "B.Left", kind: 2, fields:
                $"// field:1|16|{NominalValidationPattern("C.Right")}|1\n"),
            NominalValidationHistory("right.dgschema", "C.Right", kind: 2, fields:
                $"// field:1|16|{NominalValidationPattern("B.Left")}|1\n"));

        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0019" &&
            diagnostic.GetMessage().Contains("field type pattern has conflicting kind or arity"));
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0019" &&
            diagnostic.GetMessage().Contains("cyclic"));
        Assert.Empty(run.GeneratedSources);
    }

    private static InMemoryAdditionalText NominalValidationHistory(string path, string id, int version = 1,
        int kind = 1, int arity = 0, string fields = "") =>
        new(path, GenericTemplateHistoryTests.History(id, version, kind, arity, fields));

    private static string NominalValidationPattern(string id, string arguments = "") =>
        $"n{Convert.ToBase64String(Encoding.UTF8.GetBytes(id))}({arguments})";
}
